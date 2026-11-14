namespace DualWieldLab;

// How EFT holds a weapon. Only Pistol handling is dual-wieldable.
internal enum HandlingClass
{
    Other,
    Pistol
}

// How ammunition moves through a weapon, in the order of EFT's fire-operation factory
// (FirearmController.GetOperationFactoryDelegates). Only MagazineFed is supported.
internal enum FirearmMechanism
{
    MagazineFed,
    Cylinder,
    InternalMagazine,
    BarrelOnly,
    Flare,
    OneOff,
    BoltAction,
    MultiChamber,
    Inconsistent
}

// Mirrors Weapon.EReloadMode plus the case where EFT's combined getter throws.
internal enum ReloadKind
{
    ExternalMagazine,
    InternalMagazine,
    OnlyBarrel,
    ExternalMagazineWithInternalReloadSupport,
    Inconsistent
}

internal enum MagazineKind
{
    None,
    Box,
    Cylinder,
    Belt
}

internal static class HandlingPolicy
{
    internal const string PistolWeaponClass = "pistol";

    // Mirror of Player.GetWeaponAnimationType (pinned by the offline checks): pistols, "pistol"-class
    // revolvers and flare launchers get the pistol hands set.
    internal static HandlingClass Classify(bool pistolType, bool revolverType, string? weaponClass, bool flareLauncher)
    {
        if (pistolType) return HandlingClass.Pistol;
        if (revolverType) return weaponClass == PistolWeaponClass ? HandlingClass.Pistol : HandlingClass.Other;
        return flareLauncher ? HandlingClass.Pistol : HandlingClass.Other;
    }
}

internal static class FirearmMechanismPolicy
{
    // Mirror of Weapon.ReloadMode: the template's mode combined with the installed magazine's.
    internal static ReloadKind CombineReload(ReloadKind weapon, ReloadKind? magazine)
    {
        if (magazine == null || magazine == weapon) return weapon;
        return weapon == ReloadKind.InternalMagazine && magazine == ReloadKind.ExternalMagazine
            ? ReloadKind.ExternalMagazineWithInternalReloadSupport : ReloadKind.Inconsistent;
    }

    internal static FirearmMechanism Classify(bool revolverType, bool flareGun, bool oneOff, bool boltAction,
        ReloadKind reload, int chamberCount, MagazineKind magazine)
    {
        if (flareGun) return FirearmMechanism.Flare;
        if (oneOff) return FirearmMechanism.OneOff;
        if (reload == ReloadKind.OnlyBarrel) return FirearmMechanism.BarrelOnly;
        if (revolverType || magazine == MagazineKind.Cylinder) return FirearmMechanism.Cylinder;
        if (boltAction) return FirearmMechanism.BoltAction;
        if (reload == ReloadKind.Inconsistent || magazine == MagazineKind.Belt) return FirearmMechanism.Inconsistent;
        if (reload != ReloadKind.ExternalMagazine) return FirearmMechanism.InternalMagazine;
        return chamberCount == 1 ? FirearmMechanism.MagazineFed : FirearmMechanism.MultiChamber;
    }

    internal static bool IsSupported(FirearmMechanism mechanism) => mechanism == FirearmMechanism.MagazineFed;

    internal static string Describe(FirearmMechanism mechanism)
    {
        switch (mechanism)
        {
            case FirearmMechanism.MagazineFed: return "magazine_fed";
            case FirearmMechanism.Cylinder: return "cylinder";
            case FirearmMechanism.InternalMagazine: return "internal_magazine";
            case FirearmMechanism.BarrelOnly: return "barrel_only";
            case FirearmMechanism.Flare: return "flare";
            case FirearmMechanism.OneOff: return "one_off";
            case FirearmMechanism.BoltAction: return "bolt_action";
            case FirearmMechanism.MultiChamber: return "multi_chamber";
            default: return "inconsistent";
        }
    }

    internal static string Refusal(FirearmMechanism mechanism)
    {
        switch (mechanism)
        {
            case FirearmMechanism.Cylinder: return "is a revolver; revolvers are not supported yet";
            case FirearmMechanism.InternalMagazine: return "has an internal magazine, which is not supported yet";
            case FirearmMechanism.BarrelOnly: return "loads through the barrel, which is not supported";
            case FirearmMechanism.Flare: return "is a flare pistol, which is not supported";
            case FirearmMechanism.OneOff: return "is a single-use weapon, which is not supported";
            case FirearmMechanism.BoltAction: return "uses a manual bolt, which is not supported";
            case FirearmMechanism.MultiChamber: return "requires exactly one firing chamber";
            case FirearmMechanism.Inconsistent: return "has inconsistent weapon/magazine reload metadata";
            default: return "";
        }
    }
}
