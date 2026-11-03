namespace DualWieldLab;

internal static class HideoutRangePolicy
{
    internal static string? Refusal(bool inRange, bool guest, bool updating, bool patrol,
        bool firearmsBlocked, bool cloneBound, bool persistentInventorySeparate)
    {
        if (!inRange) return "Dual pistols are available only in the native hideout shooting range.";
        if (guest) return "Guest hideout inventories are not supported.";
        if (updating) return "Native shooting-range inventory is entering, refreshing or leaving.";
        if (!cloneBound || !persistentInventorySeparate) return "Native shooting-range clone inventory could not be isolated from persistent equipment.";
        if (patrol || firearmsBlocked) return "Native shooting-range patrol or firearm restrictions currently lower the weapons.";
        return null;
    }
}
