using System;

namespace DualWieldLab;

// One family for the supported mechanism. Native per-weapon donor grips remain
// on the installed EFT rig; this profile never selects a model by template id.
public enum PistolAnimationFamily
{
    MagazineFedPistol
}

public readonly struct PistolAnimationProfile
{
    public PistolAnimationFamily Family { get; }
    public float NativeRecoilUp { get; }
    public float NativeRecoilBack { get; }
    public float MeasuredLengthMetres { get; }
    public float RecoilPitchDegrees { get; }
    public float RecoilBackMetres { get; }
    public float RecoverySeconds { get; }
    public float DrawSeconds { get; }
    public float GripSpacingScale { get; }
    public bool IsValid => MeasuredLengthMetres > 0f;

    private PistolAnimationProfile(float nativeRecoilUp, float nativeRecoilBack, float measuredLengthMetres)
    {
        Family = PistolAnimationFamily.MagazineFedPistol;
        NativeRecoilUp = nativeRecoilUp;
        NativeRecoilBack = nativeRecoilBack;
        MeasuredLengthMetres = measuredLengthMetres;
        // These are bounded original presentation curves, not a claim to match
        // EFT's camera-recoil simulation. Native data drives their amplitudes.
        RecoilPitchDegrees = Clamp(nativeRecoilUp * .06f, 0f, 10f);
        RecoilBackMetres = Clamp(nativeRecoilBack * .00025f, 0f, .035f);
        RecoverySeconds = Clamp(.10f + measuredLengthMetres * .25f, .12f, .22f);
        DrawSeconds = Clamp(.10f + measuredLengthMetres * .4f, .14f, .28f);
        GripSpacingScale = Clamp(measuredLengthMetres / .20f, .85f, 1.3f);
    }

    public static bool TryCreate(PistolAnimationFamily family, float nativeRecoilUp,
        float nativeRecoilBack, float measuredLengthMetres, out PistolAnimationProfile profile)
    {
        profile = default;
        if (family != PistolAnimationFamily.MagazineFedPistol
            || !Finite(nativeRecoilUp) || nativeRecoilUp < 0f
            || !Finite(nativeRecoilBack) || nativeRecoilBack < 0f
            || !Finite(measuredLengthMetres) || measuredLengthMetres <= .001f)
            return false;
        profile = new PistolAnimationProfile(nativeRecoilUp, nativeRecoilBack, measuredLengthMetres);
        return true;
    }

    internal static float Clamp(float value, float min, float max) => Math.Max(min, Math.Min(max, value));
    internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

}

public readonly struct PistolPoseSample
{
    public float AimBlend { get; }
    public float LowerBlend { get; }
    public float EnterBlend { get; }
    public float Recoil { get; }
    public float PitchUpDegrees { get; }
    public float ForwardMetres { get; }
    public float VerticalMetres { get; }
    public float SwaySideMetres { get; }
    public float SwayUpMetres { get; }
    public float YawDegrees { get; }
    public float RollDegrees { get; }

    internal PistolPoseSample(float aim, float lower, float enter, float recoil,
        float pitch, float forward, float vertical, float swaySide = 0f, float swayUp = 0f, float yaw = 0f, float roll = 0f)
    {
        AimBlend = aim; LowerBlend = lower; EnterBlend = enter; Recoil = recoil;
        PitchUpDegrees = pitch; ForwardMetres = forward; VerticalMetres = vertical;
        SwaySideMetres = swaySide; SwayUpMetres = swayUp; YawDegrees = yaw; RollDegrees = roll;
    }
}

// Allocation-free original pose curves shared by supported pistols. Presentation only: native
// retargeting owns locomotion. It never fires, consumes ammo, changes ownership or waits before
// native cleanup.
public sealed class PistolAnimationState
{
    private readonly PistolAnimationProfile _profile;
    public float AimBlend { get; private set; }
    public float LowerBlend { get; private set; }
    public float EnterBlend { get; private set; }
    public float Recoil { get; private set; }
    public PistolAnimationFamily Family => _profile.Family;

    public PistolAnimationState(PistolAnimationProfile profile)
    {
        if (!profile.IsValid) throw new ArgumentException("A validated pistol animation profile is required.", nameof(profile));
        _profile = profile;
        Reset();
    }

    public void Reset(bool drawIn = true)
    {
        AimBlend = LowerBlend = Recoil = 0f;
        EnterBlend = drawIn ? 0f : 1f;
    }

    public void Step(float deltaTime, bool aiming, bool lowered)
        => Step(deltaTime, aiming, lowered, 0f);

    public void Step(float deltaTime, bool aiming, bool lowered, float movementSpeed01)
    {
        if (!PistolAnimationProfile.Finite(deltaTime) || deltaTime <= 0f) return;
        AimBlend = MoveTowards(AimBlend, aiming && !lowered ? 1f : 0f, deltaTime / .125f);
        LowerBlend = MoveTowards(LowerBlend, lowered ? 1f : 0f, deltaTime / .12f);
        EnterBlend = MoveTowards(EnterBlend, 1f, deltaTime / _profile.DrawSeconds);
        Recoil = MoveTowards(Recoil, 0f, deltaTime / _profile.RecoverySeconds);
        // Locomotion comes from the captured native arm pose. No artificial
        // clock is advanced here; this state owns only additive offhand recoil.
    }

    public void Fire(float nativeAmmoRecoilMultiplier = 1f)
    {
        if (!PistolAnimationProfile.Finite(nativeAmmoRecoilMultiplier) || nativeAmmoRecoilMultiplier < 0f)
            return; // Presentation must never fail an already-emitted native shot.
        Recoil = PistolAnimationProfile.Clamp(Recoil + nativeAmmoRecoilMultiplier, 0f, 1f);
    }

    public PistolPoseSample Sample()
    {
        float enter = Smooth(EnterBlend);
        float lower = Smooth(LowerBlend);
        float recoil = Smooth(Recoil);
        return new PistolPoseSample(Smooth(AimBlend), lower, enter, recoil,
            _profile.RecoilPitchDegrees * recoil - 12f * (1f - enter),
            -_profile.RecoilBackMetres * recoil - .025f * (1f - enter),
            -.06f * (1f - enter));
    }

    private static float Smooth(float value) => value * value * (3f - 2f * value);
    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta) return target;
        return current + Math.Sign(target - current) * maxDelta;
    }
}
