using System;
using System.Linq;
using EFT.InventoryLogic;

namespace DualWieldLab;

internal static class WeaponCompatibility
{
    internal static bool IsEligible(Weapon? weapon, bool allowMalfunction = false) => GetRefusal(weapon, "Weapon", allowMalfunction) == null;

    // allowMalfunction: a paired pistol keeps its native malfunction and is serviced in place.
    internal static string? GetRefusal(Weapon? weapon, string side, bool allowMalfunction = false)
    {
        if (weapon == null) return side + " is not a firearm";
        try
        {
            PistolFacts facts = GetFacts(weapon);
            if (allowMalfunction) facts.Malfunctioned = false;
            return PistolMechanics.GetRefusal(facts, side);
        }
        catch (Exception e)
        {
            // Weapon.ReloadMode itself rejects inconsistent weapon/magazine
            // metadata. Report that at the boundary instead of forcing a model.
            return side + " native mechanical metadata is invalid: " + e.Message;
        }
    }

    internal static PistolFacts GetFacts(Weapon weapon)
    {
        // A native Revolver derives Weapon directly, not Pistol. WeapClass alone
        // would also include revolvers and signal pistols in the installed data.
        bool nativePistol = weapon is Pistol && weapon.WeapClass == Weapon.WEAPON_CLASS_PISTOL;
        if (!nativePistol) return new PistolFacts { IsNativePistol = false };
        Magazine? magazine = weapon.GetCurrentMagazine();
        return new PistolFacts
        {
            IsNativePistol = true,
            BoltAction = weapon.BoltAction,
            OneOff = weapon.IsOneOff,
            ChamberCount = weapon.Chambers?.Length ?? 0,
            ExternalMagazineReload = weapon.ReloadMode == Weapon.EReloadMode.ExternalMagazine,
            HasMagazine = magazine != null,
            MagazineExternal = magazine != null && magazine.ReloadMagType == Weapon.EReloadMode.ExternalMagazine,
            MagazineBeltOrCylinder = magazine is CylinderMagazine || (magazine != null && (magazine.IsMagazineWithBelt || magazine.IsMagazineForStationaryWeapon)),
            AmmoCompatible = magazine != null && magazine.IsAmmoCompatible(weapon.Chambers),
            MissingVitalParts = weapon.MissingVitalParts.Any(),
            Malfunctioned = weapon.MalfState.State != Weapon.EMalfunctionState.None,
            Suppressed = weapon.GetItemComponentsInChildren<SilencerComponent>().Any(),
            TriggerMode = GetTriggerMode(weapon.SelectedFireMode)
        };
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
