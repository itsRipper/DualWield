# Configuration

BepInEx writes both files on the first start; edit them there or in the BepInEx configuration
manager (`F12` in SPT's default setup).

| File | Plugin |
|---|---|
| `BepInEx\config\itsripper.dualwield.cfg` | Dual-pistol runtime |
| `BepInEx\config\itsripper.dualwield.dualholster.cfg` | Dual holster |

Settings from earlier test builds (`local.dualwieldlab.glock17.cfg`,
`local.dualwieldlab.dualholster.cfg`) are copied into these files once, on the first start
without them. The old files are then renamed with a `.migrated` suffix and can be deleted.

## Runtime: `itsripper.dualwield.cfg`

| Section | Key | Default | Meaning |
|---|---|---|---|
| `General` | `Enabled` | `true` | Master switch. `false` disables dual wielding without uninstalling. |
| `Pair` | `AutoPairFromHolsters` | `true` | Pair automatically when the right-holster pistol is drawn and the left holster holds a compatible pistol. |
| `Pair` | `OffhandItemId` | empty | Item id of a backpack pistol to pair with when the left holster is empty. Empty: the only compatible backpack pistol, if there is exactly one. |
| `Input` | `PairShortcut` | `LeftAlt+V` | Pair/unpair. Choose a chord that does not overlap EFT's *Shoot* and *Aim* bindings. |
| `Input` | `ReloadOffhand` | `LeftAlt+R` | Reload the left pistol. |
| `Input` | `ChangeOffhandMode` | `LeftAlt+B` | Cycle the left pistol's fire modes. Not used while `LeftHandModifier` is held, so `Mouse4+Alt+B` stays the left fire-mode check. |
| `Input` | `LeftHandModifier` | `Mouse4` | Hold while using an EFT weapon action (reload, fire mode, fire-mode check, magazine check, chamber check, examine) to send it to the left pistol. Any single key; `Mouse3` is the other side button, `None` disables. While held and paired, the key's own EFT binding is suppressed. |
| `Pose` | `UseAimingReadyStance` | `true` | Paired aiming-ready stance. `false` uses a wider hip stance. Sprinting lowers both pistols. |
| `Diagnostics` | `Enabled` | `false` | Write detailed `DW_*` lines to `BepInEx\LogOutput.log` for troubleshooting. Warnings and errors are always logged. |

While paired, EFT's *Shoot* binding fires the right pistol and *Aim* the left; iron-sight aiming
is replaced by the paired stance.

## Dual holster: `itsripper.dualwield.dualholster.cfg`

| Section | Key | Default | Meaning |
|---|---|---|---|
| `Layout` | `LeftLabel` | `LEFT HOLSTER` | Caption of the second holster. |
| `Layout` | `RightLabel` | `RIGHT HOLSTER` | Caption of the native holster while the second one exists. |
| `Presentation` | `DualPistolCharacterPreview` | `true` | Show both holster pistols in the character previews. `false` keeps the native single-weapon preview. |

## Server: `SPT_Runtime\user\mods\DualWieldLab-DualHolster\config.json`

| Key | Default | Meaning |
|---|---|---|
| `uninstallMigration` | `false` | Set to `true` only to uninstall; see [UNINSTALL.md](UNINSTALL.md). |
