# Changelog

## 1.1.1

Hotfix for SPT 4.1 (EFT 0.16.9.40743), with optional Fika 2.4.x support.

- **Fixed:** Dual wielding being unavailable in standalone raids due to incorrect Fika detection.
- **Improved:** Release packaging now keeps documentation and licence files inside the mod folder.

## 1.1.0

For SPT 4.1 (EFT 0.16.9.40743), optionally with Fika 2.4.x.

- **Fika 2.4.x co-op support, built in.** The same ZIP works with and without Fika: with Fika
  installed, other players see your left pistol in your left hand, its shots, ammunition,
  chamber, malfunction, fire mode and wear, also after a reconnect; damage from left shots counts
  as for any shot. Without Fika nothing of it is loaded and single player is unchanged.
- DualWield no longer turns itself off when Fika is installed. If its co-op support cannot start,
  it refuses to pair pistols in Fika raids; the hideout shooting range is unaffected.
- Every game client in a Fika raid (players and a headless client) needs the same DualWield
  version; the README shows Fika's required-mods setting for it.

## 1.0.0

First public release, for SPT 4.1 (EFT 0.16.9.40743).

- Dual wielding of two magazine-fed pistols, each with its own firing, ammunition, recoil, sound,
  suppressor, wear and malfunctions.
- A real `LEFT HOLSTER` slot with native filters, drag and drop, quick equip (`Alt+Click`),
  weight, insurance and loss on death.
- Automatic pairing on draw; `Alt+V` pairs and unpairs. The pistol key draws the left-holster
  pistol when the right holster is empty.
- Reloads, fire-mode changes, magazine and chamber checks, malfunction clearing and examine for
  either pistol; `Mouse4` plus the Tarkov key acts on the left pistol.
- Both pistols drawn and holstered together and shown in the character previews.
- Uninstall migration that moves left-holster pistols to a legal place with unchanged item ids,
  and changes nothing in a profile it cannot fully migrate.
- Diagnostics and the uninstall migration are off by default. Settings from test builds are
  carried over on the first start.
