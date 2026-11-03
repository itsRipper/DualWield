using EFT.Animations;
using UnityEngine;

namespace DualWieldLab;

// The recoil EFT applied to the right weapon root this frame. A shot adds its impulse to the
// recoil effect mid-frame (OnShot), after ProceduralWeaponAnimation has already posed the root,
// so the effect's values read later that frame belong to the next frame. They are recorded
// right after ApplyComplexRotation, the last step that reads them for the first-person root.
internal static class PrimaryRecoilSample
{
    private static ProceduralWeaponAnimation? _source;
    private static Vector3 _rotation, _position;
    private static int _frame = -1;

    // Harmony postfix of ProceduralWeaponAnimation.ApplyComplexRotation.
    internal static void Record(ProceduralWeaponAnimation pwa)
    {
        if (!ReferenceEquals(pwa, _source)) return;
        var effect = pwa.Shootingg?.CurrentRecoilEffect;
        if (effect == null) { _frame = -1; return; }
        _rotation = effect.GetHandRotationRecoil();
        _position = effect.GetHandPositionRecoil();
        if (effect.WeaponRecoilEffect != null)
        {
            _rotation += effect.WeaponRecoilEffect.GetRotationRecoil();
            _position += effect.WeaponRecoilEffect.GetPositionRecoil();
        }
        float scale = pwa.Scale.y;
        if (scale < 1f && pwa.ShotNeedsFovAdjustments)
        {
            _rotation.x = Mathf.Atan(Mathf.Tan(_rotation.x * Mathf.Deg2Rad) * scale) * Mathf.Rad2Deg;
            _rotation.z = Mathf.Atan(Mathf.Tan(_rotation.z * Mathf.Deg2Rad) * scale) * Mathf.Rad2Deg;
        }
        _frame = Time.frameCount;
    }

    // Only the paired player's own weapon animation is recorded; asking for another starts it.
    internal static bool TryGet(ProceduralWeaponAnimation pwa, out Vector3 rotation, out Vector3 position)
    {
        rotation = _rotation; position = _position;
        if (!ReferenceEquals(pwa, _source)) { _source = pwa; _frame = -1; return false; }
        return _frame == Time.frameCount;
    }
}
