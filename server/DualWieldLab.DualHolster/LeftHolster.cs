using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Spt.Config;

namespace DualWieldLab.DualHolster.Server;

// The second holster is one more real slot of the Default Inventory template: a copy of the
// native Holster definition (same pistol filters) under its own name and id. Items in it are
// ordinary equipment children (ParentId = equipment, SlotId = HolsterLeft).
public static class LeftHolster
{
    public const string SlotName = "HolsterLeft";
    public const string NativeHolster = "Holster";
    public static readonly MongoId SlotId = new("6a1d0000000000000000d101");
    public static readonly MongoId DefaultInventory = new("55d7217a4bdc2d86028b456d");

    // Idempotent. False when the template has no native Holster to copy.
    public static bool AddTo(TemplateItem inventory)
    {
        var slots = inventory.Properties?.Slots?.ToList();
        if (slots == null) return false;
        if (slots.Any(s => s.Name == SlotName)) return true;
        var holster = slots.FirstOrDefault(s => s.Name == NativeHolster);
        if (holster == null) return false;
        slots.Insert(slots.IndexOf(holster) + 1, holster with { Name = SlotName, Id = SlotId });
        inventory.Properties!.Slots = slots;
        return true;
    }

    // The installed death predicate looks the slot name up as a LostEquipment property and
    // keeps anything it does not know. The left holster follows the native Holster rule.
    public static bool? KeptAfterDeath(LostOnDeathConfig config, PmcData pmc, Item item)
        => item.SlotId == SlotName && item.ParentId == pmc.Inventory?.Equipment?.ToString()
            ? !config.Equipment.Holster : null;

    public sealed record Move(MongoId Item, string ParentId, string SlotId, ItemLocation? Location);

    // Uninstall migration plan for one inventory: each left-holster pistol (with its whole
    // subtree untouched) goes to an empty native Holster, otherwise to a free stash cell.
    // Null when a pistol has nowhere legal to go; nothing must then be changed.
    public static List<Move>? PlanUninstall(IReadOnlyList<Item> items, MongoId equipment, MongoId stash,
        int[,] stashMap, Func<Item, (int Width, int Height)> size)
    {
        var moves = new List<Move>();
        bool holsterFree = !items.Any(i => i.ParentId == equipment.ToString() && i.SlotId == NativeHolster);
        foreach (var pistol in items.Where(i => i.ParentId == equipment.ToString() && i.SlotId == SlotName))
        {
            if (holsterFree)
            {
                moves.Add(new Move(pistol.Id, equipment.ToString(), NativeHolster, null));
                holsterFree = false;
                continue;
            }
            var (width, height) = size(pistol);
            var cell = stashMap.FindSlotForItem(width, height);
            if (cell.Success != true || cell.X == null || cell.Y == null || cell.Rotation == null
                || !stashMap.TryFillContainerMapWithItem(cell.X.Value, cell.Y.Value, width, height, cell.Rotation.Value, out _))
                return null;
            moves.Add(new Move(pistol.Id, stash.ToString(), "hideout",
                new ItemLocation { X = cell.X, Y = cell.Y, R = cell.Rotation.Value ? ItemRotation.Vertical : ItemRotation.Horizontal }));
        }
        return moves;
    }

    public sealed record Transfer(MongoId Item, ItemLocation Location);

    // Saved Scav post-raid inventory (an unfinished transfer, see DualHolsterLoader): a left-holster
    // pistol goes to the Scav's empty Holster, where the transfer screen still offers it, otherwise
    // with its whole subtree to a free cell of the PMC stash, as a finished transfer would.
    // stashMap is shared with the PMC plan. Null when a pistol has nowhere legal to go.
    public static (List<Move> Moves, List<Transfer> Transfers)? PlanScav(IReadOnlyList<Item> scavItems, MongoId scavEquipment,
        int[,] stashMap, Func<Item, (int Width, int Height)> size)
    {
        var moves = new List<Move>();
        var transfers = new List<Transfer>();
        bool holsterFree = !scavItems.Any(i => i.ParentId == scavEquipment.ToString() && i.SlotId == NativeHolster);
        foreach (var pistol in scavItems.Where(i => i.ParentId == scavEquipment.ToString() && i.SlotId == SlotName))
        {
            if (holsterFree)
            {
                moves.Add(new Move(pistol.Id, scavEquipment.ToString(), NativeHolster, null));
                holsterFree = false;
                continue;
            }
            var (width, height) = size(pistol);
            var cell = stashMap.FindSlotForItem(width, height);
            if (cell.Success != true || cell.X == null || cell.Y == null || cell.Rotation == null
                || !stashMap.TryFillContainerMapWithItem(cell.X.Value, cell.Y.Value, width, height, cell.Rotation.Value, out _))
                return null;
            transfers.Add(new Transfer(pistol.Id, new ItemLocation { X = cell.X, Y = cell.Y, R = cell.Rotation.Value ? ItemRotation.Vertical : ItemRotation.Horizontal }));
        }
        return (moves, transfers);
    }

    // Moves each transferred pistol with its whole subtree and unchanged ids into the PMC stash.
    public static void ApplyTransfers(List<Item> scavItems, List<Item> pmcItems, MongoId pmcStash, IEnumerable<Transfer> transfers)
    {
        foreach (var transfer in transfers)
        {
            var subtree = scavItems.GetItemWithChildren(transfer.Item);
            scavItems.RemoveAll(i => subtree.Contains(i));
            var root = subtree.Single(i => i.Id == transfer.Item);
            root.ParentId = pmcStash.ToString();
            root.SlotId = "hideout";
            root.Location = transfer.Location;
            pmcItems.AddRange(subtree);
        }
    }

    // Saved equipment builds are presets, not owned items: their left-holster entries are dropped
    // so a build never refers to the removed slot.
    public static int StripBuilds(IEnumerable<List<Item>> builds)
    {
        int removed = 0;
        foreach (var items in builds)
        {
            foreach (var root in items.Where(i => i.SlotId == SlotName).ToList())
            {
                var subtree = items.GetItemWithChildren(root.Id);
                items.RemoveAll(i => subtree.Contains(i));
                removed++;
            }
        }
        return removed;
    }

    public enum UninstallOutcome { Unused, Migrated, NoSpace, Unreadable }

    public sealed record UninstallResult(UninstallOutcome Outcome, int Pistols = 0, int ScavPistols = 0, int BuildEntries = 0);

    // Uninstall migration of one profile, all or nothing: every destination is planned on copies
    // of the PMC, Scav and saved-build item lists, which replace the originals only when all of
    // them succeed. Unreadable: the profile uses the slot but its inventory is incomplete or
    // inconsistent, so it cannot be migrated safely.
    public static UninstallResult MigrateProfile(SptProfile profile, Func<List<Item>, List<Item>> clone,
        Func<PmcData, int[,]?> stashMap, Func<List<Item>, Func<Item, (int Width, int Height)>> size)
    {
        PmcData? pmc = profile.CharacterData?.PmcData, scav = profile.CharacterData?.ScavData;
        var builds = profile.UserBuildData?.EquipmentBuilds?.Where(b => b?.Items != null).ToList() ?? new List<EquipmentBuild>();
        bool pmcUses = UsesSlot(pmc?.Inventory?.Items), scavUses = UsesSlot(scav?.Inventory?.Items);
        bool buildsUse = builds.Any(b => UsesSlot(b.Items));
        if (!pmcUses && !scavUses && !buildsUse) return new UninstallResult(UninstallOutcome.Unused);

        List<Move> moves = new(), scavMoves = new();
        List<Transfer> transfers = new();
        List<Item>? pmcItems = null, scavItems = null;
        if (pmcUses || scavUses)
        {
            if (pmc?.Inventory?.Items == null || pmc.Inventory.Equipment == null || pmc.Inventory.Stash == null
                || !Consistent(pmc.Inventory.Items, pmc.Inventory.Equipment.Value)
                || scavUses && (scav!.Inventory!.Equipment == null || !Consistent(scav.Inventory.Items!, scav.Inventory.Equipment.Value)))
                return new UninstallResult(UninstallOutcome.Unreadable);
            // One stash map for both plans, so the PMC and Scav pistols never get the same cell.
            int[,]? map = stashMap(pmc);
            if (map == null) return new UninstallResult(UninstallOutcome.Unreadable);
            var pmcPlan = PlanUninstall(pmc.Inventory.Items, pmc.Inventory.Equipment.Value, pmc.Inventory.Stash.Value, map, size(pmc.Inventory.Items));
            if (pmcPlan == null) return new UninstallResult(UninstallOutcome.NoSpace);
            moves = pmcPlan;
            if (scavUses)
            {
                var scavPlan = PlanScav(scav!.Inventory!.Items!, scav.Inventory.Equipment!.Value, map, size(scav.Inventory.Items!));
                if (scavPlan == null) return new UninstallResult(UninstallOutcome.NoSpace);
                (scavMoves, transfers) = scavPlan.Value;
            }
            pmcItems = clone(pmc.Inventory.Items);
            Apply(pmcItems, moves);
            if (scavUses)
            {
                scavItems = clone(scav!.Inventory!.Items!);
                Apply(scavItems, scavMoves);
                ApplyTransfers(scavItems, pmcItems, pmc.Inventory.Stash.Value, transfers);
            }
        }
        var buildItems = builds.Select(b => clone(b.Items)).ToList();
        int buildEntries = StripBuilds(buildItems);

        if (pmcItems != null) pmc!.Inventory!.Items = pmcItems;
        if (scavItems != null) scav!.Inventory!.Items = scavItems;
        for (int i = 0; i < builds.Count; i++) builds[i].Items = buildItems[i];
        return new UninstallResult(UninstallOutcome.Migrated, moves.Count, scavMoves.Count + transfers.Count, buildEntries);
    }

    private static bool UsesSlot(IEnumerable<Item>? items) => items?.Any(i => i.SlotId == SlotName) == true;

    // Every left-holster item is a direct equipment child, and no id is duplicated.
    private static bool Consistent(List<Item> items, MongoId equipment)
        => items.Where(i => i.SlotId == SlotName).All(i => i.ParentId == equipment.ToString())
            && items.Select(i => i.Id).Distinct().Count() == items.Count;

    public static void Apply(IList<Item> items, IEnumerable<Move> moves)
    {
        foreach (var move in moves)
        {
            var item = items.Single(i => i.Id == move.Item);
            item.ParentId = move.ParentId;
            item.SlotId = move.SlotId;
            item.Location = move.Location;
        }
    }
}
