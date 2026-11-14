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
    internal HandlingClass Handling;
    internal FirearmMechanism Mechanism;
    internal bool MissingVitalParts;
    internal bool Malfunctioned;
    internal bool Suppressed;
    internal PistolTriggerMode TriggerMode;
}

internal static class PistolMechanics
{
    internal static string? GetRefusal(PistolFacts facts, string side)
    {
        if (facts.Handling != HandlingClass.Pistol) return side + " is not a pistol (EFT does not hold it as one)";
        if (!FirearmMechanismPolicy.IsSupported(facts.Mechanism)) return side + " " + FirearmMechanismPolicy.Refusal(facts.Mechanism);
        if (facts.MissingVitalParts) return side + " is missing vital parts";
        if (facts.Malfunctioned) return side + " has a malfunction; unpair and service it natively";
        if (!IsSupportedMode(facts.TriggerMode))
            return side + " selected fire mode is unsupported; select native single, semiautomatic, fullauto, burst or doublet";
        return null;
    }

    // Double action is a revolver mode. On a magazine weapon doublet is an ordinary FireOperation
    // (one round per press); burst runs in AutomaticFireOperation.
    internal static bool IsSupportedMode(PistolTriggerMode mode) => mode == PistolTriggerMode.Single
        || mode == PistolTriggerMode.SemiAutomatic || mode == PistolTriggerMode.FullAutomatic
        || mode == PistolTriggerMode.Burst || mode == PistolTriggerMode.Doublet;

    // Modes EFT fires through AutomaticFireOperation: its cyclic FireRate and loop audio.
    internal static bool IsAutomaticOperation(PistolTriggerMode mode) => mode == PistolTriggerMode.FullAutomatic || mode == PistolTriggerMode.Burst;

    // Rounds after the first of a burst: AutomaticFireOperation fires until BurstShotsCount have left
    // (default 3 without a fire-mode component).
    internal static int BurstFollowUps(int burstShotsCount) => Math.Max(0, burstShotsCount - 1);

    internal static double EffectiveRate(PistolTriggerMode mode, int nativeSingleRate, int nativeAutoRate, float overheatMultiplier)
    {
        if (!IsSupportedMode(mode)) throw new InvalidOperationException("Unsupported native trigger mode.");
        double rate = IsAutomaticOperation(mode)
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
