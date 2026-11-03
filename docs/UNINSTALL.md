# Uninstalling DualWield

Pistols in the left holster live in a slot that exists only while the server mod is installed.
Deleting the mod files directly leaves profiles referring to that slot, and the game cannot load
them. The server's uninstall migration moves them out first.

## Steps

1. Back up `SPT_Runtime\user\profiles`.
2. Leave every DualWield file installed.
3. In `SPT_Runtime\user\mods\DualWieldLab-DualHolster\config.json`, set
   `"uninstallMigration": true`.
4. Start the SPT server. It logs one line per migrated profile and finally
   `Dual holster uninstall migration complete`.
5. Stop the server.
6. Delete `BepInEx\plugins\DualWieldLab`, `BepInEx\plugins\DualWieldLab.DualHolster` and
   `SPT_Runtime\user\mods\DualWieldLab-DualHolster`.
7. Start SPT and check that your profile loads and your inventory is complete.

## What the migration does

- Each left-holster pistol moves into the empty native holster, otherwise into a free cell of
  the stash, together with its magazine, ammunition and attachments and with unchanged item ids.
- Saved equipment builds lose only their left-holster entries.
- A Scav has no persistent equipment; its post-raid inventory is kept only until the post-raid
  transfer screen is finished. If the game was closed on that screen, a pistol still in the
  Scav's left holster is moved the same way.
- A profile is changed only when every pistol in it, including the Scav's, has a place; its
  saved builds are changed only then too. Running the migration again changes nothing further.

## If a profile is not migrated

The server logs `not migrated` for that profile, changes nothing in it and reports the migration
as incomplete. Do not remove the mod: the left holster stays available.

- `no free stash space`: start the game, free some stash space, stop the server and start it
  again.
- `its inventory or stash could not be read`: the profile file is damaged or was edited by hand.
  Restore it from your backup or fix it, then start the server again.
