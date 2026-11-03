using System;

namespace DualWieldLab;

// Small Unity-free description of mechanical compatibility. Native adapters fill
// these facts; no template id or weapon model name selects a firing algorithm.
internal enum PistolTriggerMode
{
    Unsupported,
    Single,
    SemiAutomatic,
    FullAutomatic,
    Burst,
    Doublet,
    DoubleAction
}

internal struct PistolFacts
{
    internal bool IsNativePistol;
    internal bool ExternalMagazineReload;
    internal bool BoltAction;
    internal bool OneOff;
    internal int ChamberCount;
    internal bool HasMagazine;
    internal bool MagazineExternal;
    internal bool MagazineBeltOrCylinder;
    internal bool AmmoCompatible;
    internal bool MissingVitalParts;
    internal bool Malfunctioned;
    internal bool Suppressed;
    internal PistolTriggerMode TriggerMode;
}

internal static class PistolMechanics
{
    internal static string? GetRefusal(PistolFacts facts, string side)
    {
        if (!facts.IsNativePistol) return side + " is not a regular native pistol";
        if (facts.BoltAction || facts.OneOff) return side + " uses an unsupported manual or one-off mechanism";
        if (facts.ChamberCount != 1) return side + " requires exactly one firing chamber";
        if (!facts.ExternalMagazineReload) return side + " requires ordinary external-magazine reload mechanics";
        if (facts.MissingVitalParts) return side + " is missing vital parts";
        if (facts.Malfunctioned) return side + " has a malfunction; unpair and service it natively";
        if (!facts.HasMagazine) return side + " requires an installed magazine";
        if (!facts.MagazineExternal || facts.MagazineBeltOrCylinder) return side + " has an unsupported magazine mechanism";
        if (!facts.AmmoCompatible) return side + " magazine ammunition is incompatible with its chamber";
        if (!IsSupportedMode(facts.TriggerMode))
            return side + " selected fire mode is unsupported; select native single, semiautomatic or fullauto";
        return null;
    }

    internal static bool IsSupportedMode(PistolTriggerMode mode) => mode == PistolTriggerMode.Single
        || mode == PistolTriggerMode.SemiAutomatic || mode == PistolTriggerMode.FullAutomatic;

    internal static double EffectiveRate(PistolTriggerMode mode, int nativeSingleRate, int nativeAutoRate, float overheatMultiplier)
    {
        if (!IsSupportedMode(mode)) throw new InvalidOperationException("Unsupported native trigger mode.");
        double rate = mode == PistolTriggerMode.FullAutomatic
            ? nativeAutoRate * (overheatMultiplier > 0f ? (double)overheatMultiplier : 1d)
            : nativeSingleRate;
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0d)
            throw new InvalidOperationException("Native effective firing rate must be finite and positive.");
        return rate;
    }

    internal static double EffectiveInterval(double rate)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0d)
            throw new InvalidOperationException("Invalid native firing rate.");
        return 60d / rate;
    }

    internal static float BoundedNativeStep(float deltaTime, double effectiveInterval, bool alreadyFiredThisFrame)
        => alreadyFiredThisFrame || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0f
            || double.IsNaN(effectiveInterval) || double.IsInfinity(effectiveInterval) || effectiveInterval <= 0d
            ? 0f : (float)Math.Min(deltaTime, effectiveInterval);

    // Mirrors the inspected native Weapon.SingleFireRate minimum/fallback. The
    // caller passes that live getter; this is not a model-specific RPM preset.
    internal static double ShotInterval(int nativeSingleFireRate) => 60d / Math.Max(240, nativeSingleFireRate);
}
