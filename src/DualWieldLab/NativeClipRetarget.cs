using System;
using UnityEngine;

namespace DualWieldLab;

// Left-handed playback of the offhand pistol's own native clips.
//
// EFT does not take the first-person hands from the clips' raw arm bones: limb IK puts them on
// weapon_R_IK_marker / weapon_L_IK_marker (Player.IkProcess, TransformLinks.GatherIK). In the
// pistol clips the right marker never leaves the gun, while the raw right palm drifts up to
// ~10 cm and ~50 deg off it, and the left marker carries the magazine and slide contacts while
// the raw left palm is up to ~45 cm away. So the animated gun is the authority: the mirrored
// gun motion moves the gun, the left palm rides it rigidly, and the right palm follows the
// mirrored motion of the left IK marker. Every motion is a mirrored delta from the native idle
// sampled at pairing, so idle frames are unchanged for any pistol and no offsets are authored.
// Mirroring is across Weapon_root's YZ plane (x lateral, -y barrel, z up), as for the grip.
internal sealed class NativeClipRetarget
{
    private sealed class Written
    {
        private readonly Transform _bone;
        private Vector3 _saved, _written;
        private Quaternion _savedRotation, _writtenRotation;
        private bool _active;

        internal Written(Transform bone) { _bone = bone; }

        internal void Set(Vector3 position, Quaternion rotation)
        {
            if (!_active) { _saved = _bone.localPosition; _savedRotation = _bone.localRotation; }
            _bone.SetPositionAndRotation(position, rotation);
            _written = _bone.localPosition; _writtenRotation = _bone.localRotation;
            _active = true;
        }

        // Undone before the next native evaluation unless the animator or EFT wrote the bone.
        internal void Restore()
        {
            if (!_active || _bone == null) return;
            if ((_bone.localPosition - _written).sqrMagnitude < 1e-12f && Quaternion.Angle(_bone.localRotation, _writtenRotation) < .001f)
                _bone.SetLocalPositionAndRotation(_saved, _savedRotation);
            _active = false;
        }
    }

    private readonly Transform _root, _weapon, _support;
    private readonly Transform? _magazine;
    private readonly Written _animWrite;
    private readonly Written? _magazineWrite;
    private readonly Vector3 _weaponIdle, _supportIdle, _supportPalmIdle, _magazineIdle;
    private readonly Quaternion _weaponIdleRotation, _supportIdleRotation, _supportPalmIdleRotation, _magazineIdleRotation;
    private Vector3 _weaponNow, _supportNow, _magazineNow;
    private Quaternion _weaponNowRotation, _supportNowRotation, _magazineNowRotation;
    private bool _sampled;

    private NativeClipRetarget(Transform root, Transform anim, Transform weapon, Transform support, Transform supportPalm, Transform? magazine)
    {
        _root = root; _weapon = weapon; _support = support; _magazine = magazine;
        _animWrite = new Written(anim);
        if (magazine != null) _magazineWrite = new Written(magazine);
        InRoot(weapon, out _weaponIdle, out _weaponIdleRotation);
        InRoot(support, out _supportIdle, out _supportIdleRotation);
        InRoot(supportPalm, out _supportPalmIdle, out _supportPalmIdleRotation);
        if (magazine != null) InGun(magazine, out _magazineIdle, out _magazineIdleRotation);
    }

    // Call with the offhand animator in its sampled native idle.
    internal static NativeClipRetarget Create(WeaponPrefab prefab)
    {
        var bones = prefab.Hierarchy;
        Transform? root = bones.GetTransform(ECharacterWeaponBones.Weapon_root);
        Transform? anim = bones.GetTransform(ECharacterWeaponBones.Weapon_root_anim);
        Transform? weapon = bones.GetTransform(ECharacterWeaponBones.weapon);
        Transform? support = bones.GetTransform(ECharacterWeaponBones.weapon_L_IK_marker);
        Transform? supportPalm = bones.GetTransform(ECharacterWeaponBones.HumanLPalm);
        if (root == null || anim == null || weapon == null || support == null || supportPalm == null || anim.parent != root)
            throw new InvalidOperationException("The offhand pistol lacks the native weapon/IK marker hierarchy.");
        return new NativeClipRetarget(root, anim, weapon, support, supportPalm, FindNamed(weapon, "mod_magazine"));
    }

    private static Transform? FindNamed(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform? found = FindNamed(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    private void InRoot(Transform bone, out Vector3 position, out Quaternion rotation)
    {
        position = _root.InverseTransformPoint(bone.position);
        rotation = Quaternion.Inverse(_root.rotation) * bone.rotation;
    }

    private void InGun(Transform bone, out Vector3 position, out Quaternion rotation)
    {
        position = _weapon.InverseTransformPoint(bone.position);
        rotation = Quaternion.Inverse(_weapon.rotation) * bone.rotation;
    }

    private static Vector3 Mirror(Vector3 v) => new Vector3(-v.x, v.y, v.z);

    // Reflection conjugate of a rigid rotation (still proper).
    private static Quaternion Mirror(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

    // Mirrored delta from idle, b = M(now) * M(idle)^-1 applied to idle: identity at idle.
    private static void MirroredFromIdle(Vector3 now, Quaternion nowRotation, Vector3 idle, Quaternion idleRotation,
        out Vector3 deltaPosition, out Quaternion deltaRotation)
    {
        Quaternion inverseIdle = Quaternion.Inverse(Mirror(idleRotation));
        deltaRotation = Mirror(nowRotation) * inverseIdle;
        deltaPosition = Mirror(now) - deltaRotation * Mirror(idle);
    }

    // After the native evaluation, before any write: this frame's clip in root/gun space.
    internal void Sample()
    {
        // Once per native pass: a second paired alignment in the same frame (shot preflight)
        // must not read back this pass's own writes.
        if (_sampled) return;
        InRoot(_weapon, out _weaponNow, out _weaponNowRotation);
        InRoot(_support, out _supportNow, out _supportNowRotation);
        if (_magazine != null) InGun(_magazine, out _magazineNow, out _magazineNowRotation);
        _sampled = true;
    }

    // With the left palm at its paired idle grip (root aligned as in paired idle): moves the gun
    // and magazine by their mirrored native motion and returns where the gripping palm goes.
    internal void FollowGun(Vector3 palmPosition, Quaternion palmRotation, out Vector3 gripPosition, out Quaternion gripRotation)
    {
        gripPosition = palmPosition; gripRotation = palmRotation;
        if (!_sampled) return;
        MirroredFromIdle(_weaponNow, _weaponNowRotation, _weaponIdle, _weaponIdleRotation, out Vector3 dp, out Quaternion dr);
        Quaternion rootRotation = _root.rotation;
        Vector3 weaponPosition = _root.TransformPoint(dp + dr * _weaponIdle);
        Quaternion weaponRotation = rootRotation * dr * _weaponIdleRotation;
        // weapon is a child of Weapon_root_anim: move the anim bone so weapon lands there.
        Quaternion animRotation = weaponRotation * Quaternion.Inverse(_weapon.localRotation);
        _animWrite.Set(weaponPosition - animRotation * _weapon.localPosition, animRotation);
        if (_magazine != null)
        {
            MirroredFromIdle(_magazineNow, _magazineNowRotation, _magazineIdle, _magazineIdleRotation, out Vector3 mp, out Quaternion mr);
            _magazineWrite!.Set(_weapon.TransformPoint(mp + mr * _magazineIdle), _weapon.rotation * mr * _magazineIdleRotation);
        }
        Vector3 palm = _root.InverseTransformPoint(palmPosition);
        gripPosition = _root.TransformPoint(dp + dr * palm);
        gripRotation = rootRotation * dr * Quaternion.Inverse(rootRotation) * palmRotation;
    }

    // Keeps the gun at its native idle pose while a clip plays only for timing.
    internal void HoldIdle()
    {
        Quaternion weaponRotation = _root.rotation * _weaponIdleRotation;
        Quaternion animRotation = weaponRotation * Quaternion.Inverse(_weapon.localRotation);
        _animWrite.Set(_root.TransformPoint(_weaponIdle) - animRotation * _weapon.localPosition, animRotation);
    }

    // The native support palm (raw idle palm carried by the left IK marker's motion), mirrored
    // as a hand onto the player's right hand, the same reflection as the left grip.
    internal void SupportPalm(out Vector3 position, out Quaternion rotation)
    {
        Vector3 markerNow = _sampled ? _supportNow : _supportIdle;
        Quaternion markerNowRotation = _sampled ? _supportNowRotation : _supportIdleRotation;
        Quaternion carried = markerNowRotation * Quaternion.Inverse(_supportIdleRotation);
        Vector3 local = markerNow + carried * (_supportPalmIdle - _supportIdle);
        Quaternion r = carried * _supportPalmIdleRotation;
        position = _root.TransformPoint(Mirror(local));
        rotation = _root.rotation * new Quaternion(r.x, -r.y, -r.z, r.w) * new Quaternion(0f, 1f, 0f, 0f);
    }

    internal void BeforeNativePose()
    {
        _magazineWrite?.Restore();
        _animWrite.Restore();
        _sampled = false;
    }
}
