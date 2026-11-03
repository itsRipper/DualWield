# Building DualWield

## Requirements

- Windows with Windows PowerShell 5.1
- .NET 10 SDK
- .NET Framework 4.7.2 targeting pack (Visual Studio or the .NET Framework 4.7.2 Developer Pack)
- An SPT 4.1.6 installation. The build references its game, BepInEx and SPT server assemblies;
  nothing is downloaded and nothing in the installation is changed.

## Commands

```powershell
# Build the three components and run the checks
powershell -ExecutionPolicy Bypass -File scripts\Build.ps1 -SptRoot "C:\SPT"

# Build, check and write dist\DualWield-<version>.zip and its SHA-256 list
powershell -ExecutionPolicy Bypass -File scripts\Package.ps1 -SptRoot "C:\SPT"
```

Builds are reproducible: the same commit produces byte-identical DLLs in any folder.

## Projects

| Path | Output |
|---|---|
| `src/DualWieldLab` | `DualWieldLab.dll`, the dual-pistol runtime (BepInEx, .NET Framework 4.7.2) |
| `src/DualWieldLab.DualHolster` | `DualWieldLab.DualHolster.dll`, the second holster in the client |
| `server/DualWieldLab.DualHolster` | `DualWieldLab.DualHolster.Server.dll`, the SPT server mod (.NET 10) |

## Checks

| Path | Checks |
|---|---|
| `tests/OfflineChecks` | The compiled runtime against the installed EFT assemblies: native method contracts, input routing, reload and ammunition invariants |
| `tests/DualHolsterChecks` | Slot template, death rule, save/load and the all-or-nothing uninstall migration on the SPT server code; client plugin contracts |
| `tests/InventoryChecks` | SPT server inventory contracts the slot relies on |
| `tests/ConfigMigrationChecks` | Settings migration from earlier configuration files, on BepInEx's own configuration code |

Gameplay itself is tested in game.

## Reload choreography

The runtime embeds the clips in `assets/exports/unity`. They are exported from the original
Blender sources in `assets/blender` with `assets/tools/export/Build-Animations.ps1`
(Blender 5.2); `Build.ps1` checks that the exported clips match their source when Blender is
installed.
