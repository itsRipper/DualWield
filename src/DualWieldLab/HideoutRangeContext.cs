using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;

namespace DualWieldLab;

internal static class HideoutRangeContext
{
    // Native state/reference predicates only; suitable for input and lifecycle gates.
    internal static string? Refusal(Player player, PlayerOwner owner)
    {
        if (!(player is HideoutPlayer rangePlayer) || !(owner is HideoutPlayerOwner rangeOwner)
            || rangeOwner.Player != player || rangeOwner.HideoutPlayer != rangePlayer)
            return "Native hideout range player/owner pairing is unavailable.";
        var clone = rangePlayer.ShootingRangeInventory;
        var original = rangePlayer.OriginalInventory;
        bool cloneBound = clone != null && ReferenceEquals(player.InventoryController, clone)
            && ReferenceEquals(player.Equipment, clone.Inventory.Equipment);
        bool separate = original != null && clone != null && !ReferenceEquals(original, clone)
            && !ReferenceEquals(original.Inventory, clone.Inventory)
            && !ReferenceEquals(original.Inventory.Equipment, clone.Inventory.Equipment);
        return HideoutRangePolicy.Refusal(rangeOwner.InShootingRange, rangeOwner.IsGuest,
            rangePlayer.IsUpdateHideoutPlayerInventoryInProgress, rangePlayer.IsInPatrol,
            player.MovementContext.BlockFirearms, cloneBound, separate);
    }

    // Executed once during a requested pairing, never while firing or in a frame scan.
    internal static string? ValidateWeapons(Player player, Weapon primary, Weapon offhand)
    {
        if (!(player is HideoutPlayer rangePlayer)) return "Native hideout range player is unavailable.";
        var clone = rangePlayer.ShootingRangeInventory;
        var original = rangePlayer.OriginalInventory;
        if (clone == null || original == null || ReferenceEquals(clone, original)
            || !ReferenceEquals(player.InventoryController, clone))
            return "Native range inventory changed before pistol binding.";
        var originals = new List<Item>(128);
        original.Inventory.Equipment.GetAllItemsNonAlloc(originals);
        var candidates = new List<Item>(64);
        foreach (Weapon weapon in new[] { primary, offhand })
        {
            if (!ReferenceEquals(weapon.Owner, clone))
                return "A candidate pistol subtree is not owned by the native range clone.";
            candidates.Clear();
            weapon.GetAllItemsNonAlloc(candidates);
            foreach (Item item in candidates)
            {
                if (!ReferenceEquals(item.Owner, clone))
                    return "A candidate pistol subtree is not owned by the native range clone.";
                foreach (Item persistent in originals)
                    if (ReferenceEquals(item, persistent))
                        return "A candidate range item shares an object with persistent equipment; pairing refused.";
            }
        }
        return null;
    }
}
