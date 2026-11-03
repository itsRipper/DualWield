using System;
using System.Linq;
using EFT;
using UnityEngine;

namespace DualWieldLab;

// Left native handling (examine, chamber check / malfunction clear, magazine check, magazine
// reload). The operation and its clip are native and run on the offhand context; this only
// frees the right hand for it: the right pistol holsters on the Alt+R clip, the left gun follows
// its own clip through NativeClipRetarget (left palm on the gun, right palm on the mirrored left
// IK marker), then the right pistol draws.
internal sealed class OffhandNativeAction
{
    // Reaching: reload only; the right hand takes the support grip before the native clip starts.
    internal enum Phase { Holstering, Reaching, AwaitingNative, Native, Releasing, Drawing, Finished }

    private readonly ReloadClip _reload, _service;
    private readonly Player _player;
    private readonly OffhandHandPose _hand;
    private readonly OffhandVisualRig _rig;
    private readonly NativeClipRetarget _clip;
    private readonly Renderer[] _magazineRenderers;
    private readonly bool[] _magazineForcedOff;
    private readonly PrimaryPistolCarry _carry;
    private readonly float _holstered, _drawStart, _reachFrames, _releaseStart, _releaseFrames, _holsterRate, _drawRate;
    private float _frame, _phaseTime;
    private bool _restored;

    internal Phase State { get; private set; }
    internal OffhandShotContext.NativeCheck Check { get; }

    private OffhandNativeAction(OffhandShotContext.NativeCheck check, ReloadClip reload, ReloadClip service, Player player,
        OffhandHandPose hand, OffhandVisualRig rig, NativeClipRetarget clip, Transform? magazine, Transform primaryWeapon)
    {
        Check = check; _reload = reload; _service = service; _player = player; _hand = hand; _rig = rig; _clip = clip;
        _carry = new PrimaryPistolCarry(primaryWeapon);
        _holstered = reload.Marker("holstered"); _drawStart = reload.Marker("draw_start");
        _reachFrames = service.Marker("reach_end");
        _releaseStart = service.Marker("release_start");
        _releaseFrames = service.Marker("release_end") - _releaseStart;
        _holsterRate = service.Constant("holster_rate", 1f); _drawRate = service.Constant("draw_rate", 1f);
        _magazineRenderers = magazine == null ? Array.Empty<Renderer>()
            : magazine.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer || r is SkinnedMeshRenderer).ToArray();
        _magazineForcedOff = _magazineRenderers.Select(r => r.forceRenderingOff).ToArray();
    }

    internal static OffhandNativeAction? TryCreate(OffhandShotContext.NativeCheck check, ReloadClip? reload, ReloadClip? service,
        Player player, Player.FirearmController primary, WeaponPrefab offhand, OffhandHandPose? hand, OffhandVisualRig? rig, out string reason)
    {
        reason = "";
        try
        {
            if (reload == null || service == null) { reason = "choreography clip unavailable"; return null; }
            if (hand == null || rig == null || !hand.HasSnapshot) { reason = "live native arm retarget unavailable"; return null; }
            if (rig.NativeClip == null) { reason = "native IK markers unavailable on this pistol"; return null; }
            Transform? root = offhand.Hierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
            Transform? weapon = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon);
            if (root == null || weapon == null) { reason = "native weapon node not found"; return null; }
            Transform? magazine = FindNamed(root, "mod_magazine");
            if (check == OffhandShotContext.NativeCheck.Reload && magazine == null) { reason = "native magazine node not found"; return null; }
            return new OffhandNativeAction(check, reload, service, player, hand, rig, rig.NativeClip, magazine, weapon);
        }
        catch (Exception e) { reason = e.GetType().Name + ": " + e.Message; return null; }
    }

    private static Transform? FindNamed(Transform? root, string name)
    {
        if (root == null) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform? found = FindNamed(root.GetChild(i), name);
            if (found != null) return found;
        }
        return null;
    }

    internal void Advance(float deltaTime, bool nativeActive)
    {
        if (_restored || State == Phase.Finished) return;
        float dt = Mathf.Max(0f, deltaTime);
        _phaseTime += dt;
        switch (State)
        {
            case Phase.Holstering:
                _frame += dt * _reload.Fps * _holsterRate;
                if (_frame >= _holstered)
                { _frame = _holstered; Enter(Check == OffhandShotContext.NativeCheck.Reload ? Phase.Reaching : Phase.AwaitingNative); }
                break;
            case Phase.Reaching:
                if (_phaseTime * _service.Fps >= _reachFrames) Enter(Phase.AwaitingNative);
                break;
            case Phase.Native:
                if (!nativeActive) Enter(Phase.Releasing);
                break;
            case Phase.Releasing:
                if (_phaseTime * _service.Fps >= _releaseFrames) { _frame = _drawStart; Enter(Phase.Drawing); }
                break;
            case Phase.Drawing:
                _frame += dt * _reload.Fps * _drawRate;
                if (_frame >= _reload.LastFrame) { _frame = _reload.LastFrame; Enter(Phase.Finished); }
                break;
        }
    }

    // started=false: native rules refused or only notified; the right pistol draws at once
    // (a reload that never started releases the support grip first).
    internal void NativeStarted(bool started)
    {
        if (State != Phase.AwaitingNative) return;
        if (started) Enter(Phase.Native);
        else if (Check == OffhandShotContext.NativeCheck.Reload) Enter(Phase.Releasing);
        else { _frame = _drawStart; Enter(Phase.Drawing); }
    }

    private void Enter(Phase phase) { State = phase; _phaseTime = 0f; }

    // Native OnMagHide / OnMagShow: the old magazine is put away, the next one appears in hand.
    internal void SetMagazineVisible(bool visible)
    {
        if (_restored) return;
        for (int i = 0; i < _magazineRenderers.Length; i++)
            if (_magazineRenderers[i] != null) _magazineRenderers[i].forceRenderingOff = _magazineForcedOff[i] || !visible;
    }

    internal void BeforeNativePose()
    {
        _carry.BeforeNativePose();
        _clip.BeforeNativePose();
    }

    private float SupportReach()
    {
        float t = _phaseTime * _service.Fps;
        bool reload = Check == OffhandShotContext.NativeCheck.Reload;
        return State switch
        {
            Phase.Reaching => _service.Scalar("reach", t),
            Phase.AwaitingNative => reload ? 1f : 0f,
            Phase.Native => reload ? 1f : _service.Scalar("reach", t),
            Phase.Releasing => _service.Scalar("release", _releaseStart + t),
            _ => 0f,
        };
    }

    private float SupportCurl()
    {
        bool held = Check == OffhandShotContext.NativeCheck.Reload || State != Phase.Native;
        return _service.Scalar("curl", held ? _service.Marker("reach_end") : _phaseTime * _service.Fps);
    }

    // After OffhandHandPose.CaptureNativePose in the same native pass.
    internal void Apply(PistolPoseSample pose)
    {
        if (_restored) return;
        Quaternion look = _player.TargetHandsRotation, inverseLook = Quaternion.Inverse(look);
        Transform palm = _hand.RightPalm;
        Vector3 nativePalm = palm.position;
        Quaternion nativePalmRotation = palm.rotation;
        _carry.Capture(palm);
        _clip.Sample();

        // Paired idle first (same as AlignModel), then the gun moves by its own mirrored clip
        // and the left palm stays on it.
        _hand.ApplyRetarget();
        _rig.RestoreReadyGrip();
        _rig.AlignToPalm(_hand.PalmPosition, _hand.PalmRotation, pose);
        _hand.ApplyGripTarget(_rig.LeftGripPosition, _rig.LeftGripRotation);
        _clip.FollowGun(_hand.PalmPosition, _hand.PalmRotation, out Vector3 grip, out Quaternion gripRotation);
        _hand.ApplyGripTarget(grip, gripRotation);

        float f = State == Phase.Drawing || State == Phase.Finished ? _frame : Mathf.Min(_frame, _holstered);
        Vector3 target = nativePalm + look * _reload.Vector("R.pos", f);
        Quaternion rotation = look * _reload.Rotation("R.rot", f) * inverseLook * nativePalmRotation;
        float curl = _reload.Scalar("R.curl", f);
        float reach = SupportReach();
        if (reach > 0f)
        {
            _clip.SupportPalm(out Vector3 supportPosition, out Quaternion supportRotation);
            target = Vector3.Lerp(target, supportPosition, reach);
            rotation = Quaternion.Slerp(rotation, supportRotation, reach);
            curl = Mathf.Lerp(curl, SupportCurl(), reach);
        }
        _hand.SolveRight(target, rotation, curl);
        _carry.Follow(palm, _reload.Step("R.vis", f) >= .5f);
    }

    internal void Restore()
    {
        if (_restored) return;
        BeforeNativePose();
        SetMagazineVisible(true);
        _restored = true;
        _carry.Restore();
    }
}
