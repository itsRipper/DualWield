using System.Reflection;
using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace DualWieldLab.DualHolster.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "itsripper.dualwield.dualholster";
    public string Name { get; init; } = "DualWield dual holster";
    public string Author { get; init; } = "ItsRipper";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.0.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/itsRipper/DualWield";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

internal sealed record DualHolsterConfig(bool UninstallMigration)
{
    internal static DualHolsterConfig Load()
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(DualHolsterConfig).Assembly.Location)!, "config.json");
        if (!File.Exists(path)) return new DualHolsterConfig(false);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return new DualHolsterConfig(doc.RootElement.TryGetProperty("uninstallMigration", out var value) && value.GetBoolean());
    }
}

// After profiles load (SaveCallbacks) and before any client fetches the item templates.
// Normal mode: append the left holster slot and correct its death rule. Uninstall mode: migrate
// each profile (LeftHolster.MigrateProfile); the slot is withheld only once no profile still
// uses it, so a profile is never left referring to a missing slot.
// A Scav's post-raid inventory is kept until the transfer screen finishes; if the game closed
// before that (or during a map transit), it can still hold a left-holster pistol.
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public sealed class DualHolsterLoader(
    TemplateTable templates,
    SaveServer saveServer,
    InventoryHelper inventoryHelper,
    ICloner cloner,
    IEnumerable<IRuntimePatch> patches,
    ISptLogger<DualHolsterLoader> logger) : IOnLoad
{
    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var config = DualHolsterConfig.Load();
        int unresolved = 0;
        if (config.UninstallMigration)
        {
            foreach (var (session, profile) in saveServer.GetProfiles())
            {
                LeftHolster.UninstallResult result;
                try
                {
                    result = LeftHolster.MigrateProfile(profile, items => cloner.Clone(items)!, StashMap, Size);
                }
                catch (Exception e)
                {
                    result = new LeftHolster.UninstallResult(LeftHolster.UninstallOutcome.Unreadable);
                    logger.Error($"Dual holster uninstall: profile {session}: {e.Message}");
                }
                switch (result.Outcome)
                {
                    case LeftHolster.UninstallOutcome.Unused:
                        continue;
                    case LeftHolster.UninstallOutcome.NoSpace:
                        unresolved++;
                        logger.Error($"Dual holster uninstall: profile {session} not migrated: no free stash space for its left-holster pistol(s). Nothing was changed; free stash space and start the server again.");
                        continue;
                    case LeftHolster.UninstallOutcome.Unreadable:
                        unresolved++;
                        logger.Error($"Dual holster uninstall: profile {session} not migrated: it uses the left holster but its inventory or stash could not be read. Nothing was changed.");
                        continue;
                }
                await saveServer.SaveProfileAsync(session, cancellationToken);
                logger.Success($"Dual holster uninstall: profile {session} migrated {result.Pistols} left-holster pistol(s) to the Holster or stash"
                    + (result.ScavPistols > 0 ? $", plus {result.ScavPistols} from an unfinished Scav post-raid transfer" : "")
                    + (result.BuildEntries > 0 ? $"; {result.BuildEntries} saved-build entr(y/ies) removed" : "") + ". Item ids and attachments unchanged.");
            }
        }
        if (config.UninstallMigration && unresolved > 0)
            logger.Error($"Dual holster uninstall migration incomplete: {unresolved} profile(s) still use the left holster, so it stays available. Do not remove the mods yet.");
        if (config.UninstallMigration && unresolved == 0)
        {
            logger.Success("Dual holster uninstall migration complete. Stop the server, then remove the DualWield client plugins and this server mod.");
            return;
        }
        if (!templates.Items.TryGetValue(LeftHolster.DefaultInventory, out var inventory) || !LeftHolster.AddTo(inventory))
        {
            logger.Error("Dual holster: Default Inventory template has no native Holster to copy; slot not added.");
            return;
        }
        foreach (var patch in patches.OfType<LeftHolsterDeathPatch>()) patch.Activate();
        logger.Success("Dual holster: " + LeftHolster.SlotName + " slot added (native Holster filters and loss-on-death rule).");
    }

    private int[,]? StashMap(PmcData pmc)
    {
        var items = pmc.Inventory!.Items!;
        MongoId stash = pmc.Inventory.Stash!.Value;
        var stashItem = items.FirstOrDefault(i => i.Id == stash);
        if (stashItem == null || !templates.Items.TryGetValue(stashItem.Template, out var stashTemplate)) return null;
        var grid = stashTemplate.Properties?.Grids?.FirstOrDefault()?.Properties;
        if (grid?.CellsH == null || grid.CellsV == null) return null;
        return inventoryHelper.GetContainerMap(grid.CellsH.Value, grid.CellsV.Value, items, stash);
    }

    private Func<Item, (int Width, int Height)> Size(List<Item> items)
        => pistol => inventoryHelper.GetItemSize(pistol.Template, pistol.Id, items);
}

[Injectable]
public sealed class LeftHolsterDeathPatch : AbstractPatch
{
    private static LostOnDeathConfig _config = null!;

    public LeftHolsterDeathPatch(LostOnDeathConfig config) => _config = config;

    // AbstractPatch only honours Enable() from the assembly that created the patch.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Activate() => Enable();

    protected override MethodBase GetTargetMethod()
        => typeof(InRaidHelper).GetMethod("IsItemKeptAfterDeath", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InRaidHelper.IsItemKeptAfterDeath not found.");

    [PatchPrefix]
    public static bool Prefix(PmcData pmcData, Item itemToCheck, ref bool __result)
    {
        bool? kept = LeftHolster.KeptAfterDeath(_config, pmcData, itemToCheck);
        if (kept == null) return true;
        __result = kept.Value;
        return false;
    }
}
