namespace DualWieldLab;

internal static class MagazineReplacementAudit
{
    // oldBefore/oldAfter are 0 for an insert. The chamber gains a round only where NativeFeed says so.
    internal static bool Counts(int oldBefore, int spareBefore, int chamberBefore,
        int oldAfter, int spareAfter, int chamberAfter, bool spareCompatible)
    {
        if (oldBefore < 0 || spareBefore <= 0 || chamberBefore < 0 || chamberBefore > 1) return false;
        int fed = NativeFeed.Feeds(chamberBefore == 0, spareBefore, spareCompatible) ? 1 : 0;
        return oldAfter == oldBefore && chamberAfter == chamberBefore + fed && spareAfter == spareBefore - fed;
    }
    internal static bool Verified(bool nativeSuccess, bool identities, bool owners,
        bool weaponAddresses, bool magazineAddresses, bool countsAndChamber, bool otherWeaponAmmo)
        => nativeSuccess && identities && owners && weaponAddresses && magazineAddresses
            && countsAndChamber && otherWeaponAmmo;
}
