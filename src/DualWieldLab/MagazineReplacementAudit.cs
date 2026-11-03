namespace DualWieldLab;

internal static class MagazineReplacementAudit
{
    internal static bool Counts(int oldBefore, int spareBefore, int chamberBefore,
        int oldAfter, int spareAfter, int chamberAfter)
        => oldBefore >= 0 && spareBefore > 0 && (chamberBefore == 0 || chamberBefore == 1)
            && oldAfter == oldBefore && chamberAfter == 1
            && spareAfter == spareBefore - (chamberBefore == 0 ? 1 : 0);
    internal static bool Verified(bool nativeSuccess, bool identities, bool owners,
        bool weaponAddresses, bool magazineAddresses, bool countsAndChamber, bool otherWeaponAmmo)
        => nativeSuccess && identities && owners && weaponAddresses && magazineAddresses
            && countsAndChamber && otherWeaponAmmo;
}
