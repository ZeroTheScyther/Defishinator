using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace Defishinator.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private List<(string Name, uint Count)> _cachedFish = [];
    private long _lastRefreshTick;

    public MainWindow(Plugin plugin) : base("Defishinator###DefishinatorMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(320, 280),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        _plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var service = _plugin.DesynthService;

        // Refresh the fish list every 2 seconds while idle
        var now = Environment.TickCount64;
        if (!service.IsRunning && now - _lastRefreshTick > 2000)
        {
            _cachedFish = service.GetFishInInventory();
            _lastRefreshTick = now;
        }

        // --- Status & controls ---
        ImGui.TextUnformatted($"Status: {service.Status}");

        if (service.IsRunning)
        {
            var progress = service.TotalFish > 0
                ? (float)service.ProcessedFish / service.TotalFish
                : 0f;
            ImGui.ProgressBar(progress, new Vector2(-1, 0),
                $"{service.ProcessedFish} / {service.TotalFish}");

            if (ImGui.Button("Stop"))
                service.Stop();
        }
        else
        {
            using (ImRaii.Disabled(_cachedFish.Count == 0))
            {
                if (ImGui.Button("Desynth All Fish"))
                    service.StartDesynth();
            }

            if (_cachedFish.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(no fish in bags)");
            }
        }

        ImGui.Separator();

        // --- Fish list ---
        ImGui.TextUnformatted($"Fish in inventory  ({_cachedFish.Count} type{(_cachedFish.Count == 1 ? "" : "s")}):");

        using var child = ImRaii.Child("FishList", Vector2.Zero, true);
        if (!child.Success) return;

        if (_cachedFish.Count == 0)
        {
            ImGui.TextDisabled("No Seafood-category items found.");
            return;
        }

        using var table = ImRaii.Table("FishTable", 2,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table.Success) return;

        ImGui.TableSetupColumn("Fish", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty",  ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableHeadersRow();

        foreach (var (name, count) in _cachedFish)
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(name);
            ImGui.TableSetColumnIndex(1);
            ImGui.TextUnformatted(count.ToString());
        }
    }
}
