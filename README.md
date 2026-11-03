# DualWield

*Dual-wield pistols in SPT 4.1*

DualWield adds a second holster and lets you fire two pistols at once. Each pistol keeps its own
magazine, ammunition, attachments, fire mode and malfunctions, and every shot, reload and weapon
check uses EFT's own weapon logic.

## Features

- A `LEFT HOLSTER` slot that works like the normal holster: drag and drop, quick equip, weight,
  insurance, loss on death.
- Drawing a pistol pairs it with the one in the other holster.
- Independent firing, recoil, sound, suppressors and malfunctions; single and full-auto modes.
- Reloads, fire-mode changes, magazine and chamber checks and examine for either pistol, also
  while sprinting.
- Mixed pairs of different pistol models.
- Both pistols are drawn and holstered together and shown in the character previews.
- An uninstall migration that keeps your pistols.

## Requirements

- SPT 4.1 (Escape from Tarkov 0.16.9.40743). On any other EFT build the plugins stay off.
- Single-player only for now. With Fika installed, dual wielding stays off.

## Installation

1. Close the game and the SPT server, then back up `SPT_Runtime\user\profiles`.
2. Extract the ZIP into your SPT folder (the one with `EscapeFromTarkov.exe`).
3. Start the server and the game.

To update, extract the new ZIP over the old files. Your settings are kept.

## Controls

The normal Tarkov keys control the right pistol. For the left pistol, hold `Mouse4` (the side
mouse button) and press the same key.

| Input | Action |
|---|---|
| `1` | Draw both pistols. With only the left holster filled, draws that pistol. |
| `Alt+V` | Pair or unpair. |
| Left mouse button | Fire the right pistol. |
| Right mouse button | Fire the left pistol. |
| `R`, `B`, `Alt+T`, `Shift+T`, `L` | Reload, fire mode, magazine check, chamber check, examine (right pistol). |
| `Mouse4` + the same key | The same for the left pistol, e.g. `Mouse4+R` reloads it and `Mouse4+Alt+B` shows its fire mode. |
| `Alt+R`, `Alt+B` | Left reload and left fire mode. |
| `Alt+Click` a pistol | Equip it; if the right holster is taken it goes into the left one. |

`Mouse4`, `Alt+V`, `Alt+R` and `Alt+B` can be changed with the BepInEx configuration manager
(`F12`); see [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## Uninstalling

Deleting the files alone leaves profiles that no longer load. Migrate first:

1. Back up `SPT_Runtime\user\profiles` and keep DualWield installed.
2. Set `"uninstallMigration": true` in `SPT_Runtime\user\mods\DualWieldLab-DualHolster\config.json`.
3. Start the server, wait for `Dual holster uninstall migration complete`, then stop it.
4. Delete `BepInEx\plugins\DualWieldLab`, `BepInEx\plugins\DualWieldLab.DualHolster` and
   `SPT_Runtime\user\mods\DualWieldLab-DualHolster`.

Left-holster pistols move to the empty holster or a free stash cell. If a profile cannot be
migrated, for example because the stash is full, nothing in it changes and the server says so.
Details: [docs/UNINSTALL.md](docs/UNINSTALL.md).

## Known issues and work in progress

- Some animations clip into the hands. I am working on it.
- Fika support is in progress.
- Revolver support is in progress. Signal pistols, single-shot weapons and burst modes are not
  supported.
- The left pistol can show a round in an empty chamber.
- The left pistol moves slightly with each right shot.
- The first draw of a new pair settles into the stance over about 0.2 s.
- First person only; bots carry an empty left holster.

## Building and licence

See [docs/BUILDING.md](docs/BUILDING.md). DualWield by ItsRipper is released under the
[MIT License](LICENSE); see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). It is a fan-made
mod, not affiliated with Battlestate Games or the SPT team.
