using System;
using System.Collections.Generic;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Defishinator;

public class DesynthService : IDisposable
{
    private enum State { Idle, Processing, WaitingForSalvageDialog, WaitingForResult, WaitingForOccupiedToClear, Done }

    private State _state = State.Idle;
    private readonly Queue<(InventoryType Type, int Slot, string Name)> _queue = new();
    private int _totalCount;
    private int _processedCount;
    private long _lastActionTick;
    private bool _currentSlotIsBulk;

    private const int ActionDelayMs   = 1500;  // delay before triggering each desynth
    private const int DialogTimeoutMs = 8000;  // give up waiting for dialog after 8 s
    private const int ResultTimeoutMs = 30000; // give up waiting for SalvageAutoDialog after 30 s

    // ItemUICategory row 47 = "Seafood" — all fisher-caught fish belong to this category
    private const uint FishUICategoryId = 47;

    // SalvageDialog node IDs (confirmed via xldata inspector):
    // 25 = Desynthesize button, 26 = Cancel button
    // 23 = "Desynthesize entire stack" checkbox (only visible for stacks of qty > 1)
    private const uint BulkCheckboxNodeId = 23;

    public string Status { get; private set; } = "Idle.";
    public int TotalFish     => _totalCount;
    public int ProcessedFish => _processedCount;
    public bool IsRunning    => _state is State.Processing or State.WaitingForSalvageDialog
                                         or State.WaitingForResult or State.WaitingForOccupiedToClear;

    public DesynthService()
    {
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SalvageAutoDialog", OnSalvageAutoDialogSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "SalvageResult",     OnSalvageResultSetup);
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(OnSalvageAutoDialogSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnSalvageResultSetup);
    }

    public unsafe void StartDesynth()
    {
        if (IsRunning) return;

        if (!Plugin.ClientState.IsLoggedIn)
        {
            Status = "Not logged in.";
            return;
        }

        _queue.Clear();
        CollectFish();

        if (_queue.Count == 0)
        {
            Status = "No fish found in inventory.";
            _state = State.Done;
            return;
        }

        _totalCount = _queue.Count;
        _processedCount = 0;
        Status = $"Found {_totalCount} fish stacks. Starting...";
        _state = State.Processing;
        _lastActionTick = 0;
    }

    public void Stop()
    {
        _queue.Clear();
        _state = State.Idle;
        Status = "Stopped.";
    }

    private unsafe void CollectFish()
    {
        var invTypes = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        var itemSheet  = Plugin.DataManager.GetExcelSheet<Item>();
        var invManager = InventoryManager.Instance();

        foreach (var invType in invTypes)
        {
            var container = invManager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId == 0) continue;
                if (!itemSheet.TryGetRow(invItem->ItemId, out var row)) continue;
                if (row.ItemUICategory.RowId != FishUICategoryId) continue;

                _queue.Enqueue((invType, slot, row.Name.ToString()));
            }
        }
    }

    private unsafe void OnFrameworkUpdate(IFramework framework)
    {
        var now = Environment.TickCount64;

        switch (_state)
        {
            case State.Processing:              OnProcessing(now);              break;
            case State.WaitingForSalvageDialog: OnWaitingForSalvageDialog(now); break;
            case State.WaitingForResult:        OnWaitingForResult(now);        break;
            case State.WaitingForOccupiedToClear: OnWaitingForOccupiedToClear(now); break;
        }
    }

    private unsafe void OnProcessing(long now)
    {
        if (_queue.Count == 0)
        {
            _state = State.Done;
            Status = $"Done! Desynthed {_processedCount}/{_totalCount} fish stacks.";
            return;
        }

        if (now - _lastActionTick < ActionDelayMs) return;

        var (type, slot, name) = _queue.Peek();

        if (!ValidateItemInSlot(type, slot))
        {
            _queue.Dequeue();
            _processedCount++;
            return;
        }

        var quantity = GetSlotQuantity(type, slot);
        _currentSlotIsBulk = quantity > 1;
        Status = $"Desynthing {name} x{quantity} ({_processedCount + 1}/{_totalCount})...";
        TriggerDesynth(type, slot);
        _lastActionTick = now;
        _state = State.WaitingForSalvageDialog;
    }

    /// <summary>
    /// Polls each frame until SalvageDialog is open. For bulk slots, also waits for the
    /// "Desynthesize entire stack" checkbox (node 23) to become visible, then ticks it.
    /// </summary>
    private unsafe void OnWaitingForSalvageDialog(long now)
    {
        if (now - _lastActionTick > DialogTimeoutMs)
        {
            Plugin.Log.Warning("SalvageDialog did not open in time — skipping slot.");
            _queue.Dequeue();
            _processedCount++;
            _lastActionTick = now;
            _state = State.Processing;
            return;
        }

        var addonPtr = (nint)Plugin.GameGui.GetAddonByName("SalvageDialog");
        if (addonPtr == nint.Zero) return;

        var salvageDialog = (AddonSalvageDialog*)addonPtr;

        if (!_currentSlotIsBulk)
        {
            // Single item — bulk checkbox never appears, fire Desynthesize directly.
            salvageDialog->AtkUnitBase.FireCallbackInt(0);
            _state = State.WaitingForResult;
            return;
        }

        // For stacks, wait until node 23 is visible (it initialises a frame or two after PostSetup).
        var componentNode = (AtkComponentNode*)salvageDialog->AtkUnitBase.GetNodeById(BulkCheckboxNodeId);
        if (componentNode == null || !((AtkResNode*)componentNode)->NodeFlags.HasFlag(NodeFlags.Visible))
            return;

        // Tick the checkbox then fire Desynthesize. The game reads IsChecked from this node.
        var checkbox = (AtkComponentCheckBox*)componentNode->Component;
        checkbox->SetChecked(true);
        salvageDialog->AtkUnitBase.FireCallbackInt(0);
        _state = State.WaitingForResult;
    }

    private void OnWaitingForResult(long now)
    {
        if (now - _lastActionTick > ResultTimeoutMs)
        {
            Plugin.Log.Warning("Timed out waiting for desynth result — skipping slot.");
            AdvanceQueue(now);
        }
    }

    private void OnWaitingForOccupiedToClear(long now)
    {
        // Bulk desynth processes all items in the stack sequentially; wait until the game
        // clears Occupied39 before starting the next slot.
        if (Plugin.Condition[ConditionFlag.Occupied39]) return;
        AdvanceQueue(now);
    }

    /// <summary>
    /// Fires when SalvageAutoDialog (bulk result window) opens — close it and wait for occupied to clear.
    /// </summary>
    private unsafe void OnSalvageAutoDialogSetup(AddonEvent eventType, AddonArgs addonInfo)
    {
        if (_state != State.WaitingForResult) return;

        var addonPtr = (nint)Plugin.GameGui.GetAddonByName("SalvageAutoDialog");
        if (addonPtr == nint.Zero) return;

        ((AtkUnitBase*)addonPtr)->Close(true);
        _lastActionTick = Environment.TickCount64;
        _state = State.WaitingForOccupiedToClear;
    }

    /// <summary>
    /// Fires when SalvageResult (single-item result window) opens — close it and advance.
    /// </summary>
    private unsafe void OnSalvageResultSetup(AddonEvent eventType, AddonArgs addonInfo)
    {
        if (_state != State.WaitingForResult) return;

        var addonPtr = (nint)Plugin.GameGui.GetAddonByName("SalvageResult");
        if (addonPtr == nint.Zero) return;

        ((AtkUnitBase*)addonPtr)->Close(true);
        AdvanceQueue(Environment.TickCount64);
    }

    private void AdvanceQueue(long now)
    {
        _queue.Dequeue();
        _processedCount++;
        _lastActionTick = now;
        _state = State.Processing;
    }

    private unsafe int GetSlotQuantity(InventoryType type, int slot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        if (container == null || slot >= container->Size) return 0;
        var item = container->GetInventorySlot(slot);
        return item == null ? 0 : item->Quantity;
    }

    private unsafe bool ValidateItemInSlot(InventoryType type, int slot)
    {
        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        if (container == null || slot >= container->Size) return false;
        var item = container->GetInventorySlot(slot);
        return item != null && item->ItemId != 0;
    }

    private unsafe void TriggerDesynth(InventoryType type, int slot)
    {
        var agentSalvage = AgentSalvage.Instance();
        if (agentSalvage == null)
        {
            Plugin.Log.Error("AgentSalvage instance is null.");
            Stop();
            Status = "Error: AgentSalvage unavailable.";
            return;
        }

        var container = InventoryManager.Instance()->GetInventoryContainer(type);
        if (container == null) return;
        var invItem = container->GetInventorySlot(slot);
        if (invItem == null) return;

        var salvageItem = (delegate* unmanaged<AgentSalvage*, InventoryItem*, int, byte, void>)AgentSalvage.Addresses.SalvageItem.Value;
        salvageItem(agentSalvage, invItem, 0, 0);
    }

    /// <summary>
    /// Returns a deduplicated list of (item name, total quantity) for all fish currently
    /// in the main inventory. Used to populate the UI fish list.
    /// </summary>
    public unsafe List<(string Name, uint Count)> GetFishInInventory()
    {
        var invTypes = new[]
        {
            InventoryType.Inventory1,
            InventoryType.Inventory2,
            InventoryType.Inventory3,
            InventoryType.Inventory4,
        };

        var itemSheet  = Plugin.DataManager.GetExcelSheet<Item>();
        var invManager = InventoryManager.Instance();
        var seen       = new Dictionary<uint, (string Name, uint Count)>();

        foreach (var invType in invTypes)
        {
            var container = invManager->GetInventoryContainer(invType);
            if (container == null) continue;

            for (int slot = 0; slot < container->Size; slot++)
            {
                var invItem = container->GetInventorySlot(slot);
                if (invItem == null || invItem->ItemId == 0) continue;
                if (!itemSheet.TryGetRow(invItem->ItemId, out var row)) continue;
                if (row.ItemUICategory.RowId != FishUICategoryId) continue;

                var qty = (uint)invItem->Quantity;
                if (seen.TryGetValue(invItem->ItemId, out var existing))
                    seen[invItem->ItemId] = (existing.Name, existing.Count + qty);
                else
                    seen[invItem->ItemId] = (row.Name.ToString(), qty);
            }
        }

        var result = new List<(string Name, uint Count)>(seen.Count);
        result.AddRange(seen.Values);
        return result;
    }
}
