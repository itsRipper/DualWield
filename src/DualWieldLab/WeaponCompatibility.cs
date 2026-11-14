using System;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

namespace DualWieldLab;

// Native adapter for the pure classification: anything EFT holds as a pistol is a candidate; its
// mechanism decides whether it can be dual-wielded.
internal static class WeaponCompatibility
{
    // Candidates are offered for pairing, or refused with a reason; others are ignored.
    internal static bool IsCandidate(Item? item) => item is Weapon weapon && GetHandling(weapon) == HandlingClass.Pistol;

    internal static bool IsEligible(Weapon? weapon, bool allowMalfunction = false) => GetRefusal(weapon, "Weapon", allowMalfunction) == null;

    // allowMalfunction: a paired pistol keeps its native malfunction and is serviced in place.
    internal static string? GetRefusal(Weapon? weapon, string side, bool allowMalfunction = false)
    {
        if (weapon == null) return side + " is not a firearm";
        try
        {
            PistolFacts facts = GetFacts(weapon);
            if (allowMalfunction) facts.Malfunctioned = false;
            string? refusal = PistolMechanics.GetRefusal(facts, side);
            return refusal == null ? null : refusal + " (" + weapon.LocalizedShortName() + ")";
        }
        catch (Exception e)
        {
            return side + " native mechanical metadata is invalid: " + e.Message;
        }
    }

    // The permanent reason a candidate can never pair: its mechanism.
    internal static string? GetMechanismRefusal(Weapon weapon, string side)
    {
        if (GetHandling(weapon) != HandlingClass.Pistol) return null;
        FirearmMechanism mechanism = GetMechanism(weapon);
        return FirearmMechanismPolicy.IsSupported(mechanism) ? null
            : side + " " + FirearmMechanismPolicy.Refusal(mechanism) + " (" + weapon.LocalizedShortName() + ")";
    }

    internal static HandlingClass GetHandling(Weapon weapon)
        => HandlingPolicy.Classify(weapon is Pistol, weapon is Revolver, weapon.WeapClass, weapon is GrenadeLauncher && weapon.IsFlareGun);

    internal static FirearmMechanism GetMechanism(Weapon weapon)
    {
        Magazine? magazine = weapon.GetCurrentMagazine();
        // Weapon.ReloadMode throws on inconsistent metadata; reported as a mechanism.
        ReloadKind reload = FirearmMechanismPolicy.CombineReload(ToKind(weapon.GetTemplate<WeaponTemplate>().ReloadMode),
            magazine != null ? ToKind(magazine.ReloadMagType) : (ReloadKind?)null);
        return FirearmMechanismPolicy.Classify(weapon is Revolver, weapon.IsFlareGun, weapon.IsOneOff, weapon.BoltAction,
            reload, weapon.Chambers?.Length ?? 0, GetMagazineKind(magazine));
    }

    internal static PistolFacts GetFacts(Weapon weapon)
    {
        HandlingClass handling = GetHandling(weapon);
        if (handling != HandlingClass.Pistol) return new PistolFacts { Handling = handling };
        FirearmMechanism mechanism = GetMechanism(weapon);
        if (!FirearmMechanismPolicy.IsSupported(mechanism)) return new PistolFacts { Handling = handling, Mechanism = mechanism };
        // No magazine, or an incompatible one, is a state EFT fires in (NativeFeed); neither refuses a pair.
        return new PistolFacts
        {
            Handling = handling,
            Mechanism = mechanism,
            MissingVitalParts = weapon.MissingVitalParts.Any(),
            Malfunctioned = weapon.MalfState.State != Weapon.EMalfunctionState.None,
            Suppressed = weapon.GetItemComponentsInChildren<SilencerComponent>().Any(),
            TriggerMode = GetTriggerMode(weapon.SelectedFireMode)
        };
    }

    private static MagazineKind GetMagazineKind(Magazine? magazine)
    {
        if (magazine == null) return MagazineKind.None;
        if (magazine is CylinderMagazine) return MagazineKind.Cylinder;
        return magazine.IsMagazineWithBelt || magazine.IsMagazineForStationaryWeapon ? MagazineKind.Belt : MagazineKind.Box;
    }

    private static ReloadKind ToKind(Weapon.EReloadMode mode)
    {
        switch (mode)
        {
            case Weapon.EReloadMode.ExternalMagazine: return ReloadKind.ExternalMagazine;
            case Weapon.EReloadMode.InternalMagazine: return ReloadKind.InternalMagazine;
            case Weapon.EReloadMode.OnlyBarrel: return ReloadKind.OnlyBarrel;
            case Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport: return ReloadKind.ExternalMagazineWithInternalReloadSupport;
            default: return ReloadKind.Inconsistent;
        }
    }

    internal static double GetShotRate(Weapon weapon)
    {
        if (!IsSupportedMode(weapon.SelectedFireMode))
            throw new InvalidOperationException("Selected native fire mode is not supported by the paired trigger.");
        return PistolMechanics.EffectiveRate(GetTriggerMode(weapon.SelectedFireMode), weapon.SingleFireRate,
            weapon.FireRate, weapon.MalfState.OverheatFirerateMult);
    }

    internal static bool IsSupportedMode(Weapon.EFireMode mode)
        => PistolMechanics.IsSupportedMode(GetTriggerMode(mode));

    internal static bool IsAutomaticOperation(Weapon.EFireMode mode)
        => PistolMechanics.IsAutomaticOperation(GetTriggerMode(mode));

    // As AutomaticFireOperation's constructor reads it.
    internal static int GetBurstShotsCount(Weapon weapon) => weapon.GetItemComponent<FireModeComponent>()?.BurstShotsCount ?? 3;

    internal static PistolTriggerMode GetTriggerMode(Weapon.EFireMode mode)
    {
        switch (mode)
        {
            case Weapon.EFireMode.single: return PistolTriggerMode.Single;
            case Weapon.EFireMode.semiauto: return PistolTriggerMode.SemiAutomatic;
            case Weapon.EFireMode.fullauto: return PistolTriggerMode.FullAutomatic;
            case Weapon.EFireMode.burst: return PistolTriggerMode.Burst;
            case Weapon.EFireMode.doublet: return PistolTriggerMode.Doublet;
            case Weapon.EFireMode.doubleaction: return PistolTriggerMode.DoubleAction;
            default: return PistolTriggerMode.Unsupported;
        }
    }
}
