# Defishinator

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for FFXIV that automatically desynthesises all Seafood-category items in your inventory.

- Stacks of 2+ are bulk-desynthed (entire stack at once)
- Single fish are desynthed one at a time
- Waits for each desynth to fully complete before moving on

## Usage

Use `/defish` to open the window, then click **Desynth All Fish**.

## Installation

Add the following URL to Dalamud's experimental plugin repositories (`/xlsettings` → Experimental):

```
https://raw.githubusercontent.com/ZeroTheScyther/Defishinator/master/repo.json
```

## Building

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) and a local Dalamud install.

```bash
dotnet build Defishinator/Defishinator.csproj
```
