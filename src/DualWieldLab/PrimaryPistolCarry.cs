using System.Linq;
using UnityEngine;

namespace DualWieldLab;

// Cosmetic carry of the right pistol's native "weapon" node with a retargeted right palm
// (holstered out of view, drawn back). Never reparents; restored before each native pass.
internal sealed class PrimaryPistolCarry
{
    private readonly Transform _weapon;
    private readonly Renderer[] _renderers;
    private readonly bool[] _forcedOff;
    private Vector3 _gunInPalm, _savedPosition, _writtenPosition;
    private Quaternion _gunInPalmRotation, _savedRotation, _writtenRotation;
    private bool _written, _restored;

    internal PrimaryPistolCarry(Transform weapon)
    {
        _weapon = weapon;
        _renderers = weapon.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
        _forcedOff = _renderers.Select(r => r.forceRenderingOff).ToArray();
    }


    // Before the right palm is solved: the native palm-to-gun relation of this frame.
    internal void Capture(Transform palm)
    {
        Quaternion inverse = Quaternion.Inverse(palm.rotation);
        _gunInPalm = inverse * (_weapon.position - palm.position);
        _gunInPalmRotation = inverse * _weapon.rotation;
    }

    internal void Follow(Transform palm, bool visible)
    {
        if (_restored) return;
        _savedPosition = _weapon.localPosition; _savedRotation = _weapon.localRotation;
        _weapon.SetPositionAndRotation(palm.position + palm.rotation * _gunInPalm, palm.rotation * _gunInPalmRotation);
        _writtenPosition = _weapon.localPosition; _writtenRotation = _weapon.localRotation;
        _written = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].forceRenderingOff = _forcedOff[i] || !visible;
    }

    internal void BeforeNativePose()
    {
        if (!_written || _weapon == null) return;
        if ((_weapon.localPosition - _writtenPosition).sqrMagnitude < 1e-12f
            && Quaternion.Angle(_weapon.localRotation, _writtenRotation) < .001f)
            _weapon.SetLocalPositionAndRotation(_savedPosition, _savedRotation);
        _written = false;
    }

    internal void Restore()
    {
        if (_restored) return;
        BeforeNativePose();
        _restored = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].forceRenderingOff = _forcedOff[i];
    }
}
