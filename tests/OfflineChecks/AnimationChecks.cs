using DualWieldLab;

internal static class AnimationChecks
{
    internal static void Run(Action<string, bool> check)
    {
        bool first = PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol, 45f, 80f, .18f, out var compact);
        bool second = PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol, 75f, 110f, .24f, out var longSlide);
        check("Common magazine-pistol animation family accepts distinct native data", first && second && compact.Family == longSlide.Family);
        check("Native recoil drives distinct original presentation amplitudes", compact.RecoilPitchDegrees < longSlide.RecoilPitchDegrees && compact.RecoilBackMetres < longSlide.RecoilBackMetres);
        check("Measured geometry drives presentation profile without model ids", compact.DrawSeconds < longSlide.DrawSeconds && compact.GripSpacingScale < longSlide.GripSpacingScale);
        check("Unknown animation mechanism refused", !PistolAnimationProfile.TryCreate((PistolAnimationFamily)99, 45, 80, .18f, out _));
        check("Nonfinite native animation data refused", !PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol, float.NaN, 80, .18f, out _));
        check("Missing measured geometry refused", !PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol, 45, 80, 0, out _));
        var left = new PistolAnimationState(compact);
        var right = new PistolAnimationState(longSlide);
        check("Common draw begins below idle without native state changes", left.EnterBlend == 0 && left.Sample().VerticalMetres < 0);
        left.Step(.4f, false, false); right.Step(.4f, false, false);
        check("Draw reaches stable idle", left.EnterBlend == 1 && left.Sample().VerticalMetres == 0 && left.Recoil == 0);
        left.Step(.2f, true, false);
        check("Common aiming reaches paired stance", left.AimBlend == 1 && left.Sample().AimBlend == 1);
        left.Fire(.7f);
        check("Firing one hand leaves other hand animation unchanged", left.Recoil > 0 && right.Recoil == 0);
        check("Recoil is bounded and moves back with a pitch impulse", left.Sample().ForwardMetres < 0 && left.Sample().PitchUpDegrees > 0);
        left.Step(.4f, true, false);
        check("Fire recovery returns to idle recoil", left.Recoil == 0);
        left.Step(.2f, true, true);
        check("Lowering gates aim without replacing the native sprint pose", left.LowerBlend == 1 && left.AimBlend == 0 && left.Sample().PitchUpDegrees == 0);
        float lower = left.LowerBlend;
        left.Step(float.NaN, false, false); left.Step(-1, false, false);
        check("Invalid animation timestep cannot corrupt pose", left.LowerBlend == lower);
        left.Fire(float.NaN);
        check("Invalid ammo recoil presentation never fails native firing", left.Recoil == 0);
        left.Reset(false);
        check("Unpair/reset clears all shared transient animation state", left.AimBlend == 0 && left.LowerBlend == 0 && left.Recoil == 0 && left.EnterBlend == 1);
    }
}
