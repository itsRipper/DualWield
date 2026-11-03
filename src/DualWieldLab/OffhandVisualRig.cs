using System;
using System.Text;
using EFT;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace DualWieldLab;

// Owns the assembled secondary presentation only. Never binds the player's animator, changes
// camera masks, or creates another weapon/controller. Hierarchy and renderer discovery happen
// once, before parenting under hands.
internal sealed class OffhandVisualRig
{
    private readonly Transform _model;
    private readonly Transform _parent;
    private readonly Transform _root;
    private readonly Transform _primaryRoot;
    private readonly Transform _fireport;
    internal Transform Fireport => _fireport;
    private readonly Transform _primaryFireport;
    private readonly Renderer[] _renderers;
    private readonly int[] _layers;
    private readonly bool[] _forcedOff;
    private readonly ShadowCastingMode[] _shadows;
    private readonly SkinnedMeshRenderer[] _skins;
    private readonly bool[] _offscreen;
    private readonly Camera? _camera;
    private readonly int _nativeLayer;
    private bool _restored;
    private Vector3 _leftGripPositionLocal;
    private Quaternion _leftGripRotationLocal;
    private readonly Vector3 _readyGripPosition;
    private readonly Quaternion _readyGripRotation;
    private readonly Transform _nativeGripDonor;
    private readonly Transform? _animatedWeapon;
    private bool _liveGripAligned;
    private bool _presentationVisible = true;
    // Null when the pistol lacks EFT's IK marker hierarchy; left native actions then refuse.
    internal NativeClipRetarget? NativeClip { get; }
    // Pistol-to-pistol grip correction in the offhand Weapon_root frame (identity for two
    // identical models); see MeasureGripCorrection.
    private Vector3 _gripCorrectionPosition;
    private Quaternion _gripCorrectionRotation = Quaternion.identity;
    // Inputs of the correction. The left side comes from the offhand prefab's own native idle,
    // sampled once at construction; the right marker is read only while the right hands are in
    // native idle (EFT animates the IK markers in every action clip).
    private Matrix4x4 _leftMarkerIdle, _leftPalmIdle;
    private Transform? _rightMarker, _rightGun;
    private readonly Transform? _rightVisibleGun;
    private readonly Player.FirearmController _primary;
    private bool _gripCorrectionSettled;
    // The right marker at native idle per pistol template (weapon geometry, constant per
    // template), recorded at settled live idle. A draw pair of a template seen before is exact
    // from the first frame; without an entry the correction is identity until the first idle
    // frame and then eases in.
    private static readonly System.Collections.Generic.Dictionary<string, Matrix4x4> IdleRightMarkers = new();
    // The correction applied this frame eases onto a newly settled value instead of snapping.
    private Vector3 _appliedCorrectionPosition;
    private Quaternion _appliedCorrectionRotation = Quaternion.identity;
    private bool _correctionApplied;
    // The approved ready pose relative to the mirrored rendered right gun, recorded at paired idle
    // and kept per pistol pair: draw/holster place the left gun at mirror(right) * offset, so both
    // ends of a transition coincide with the ready pose (see AlignToMirroredGun).
    private static readonly System.Collections.Generic.Dictionary<string, (Vector3 Position, Quaternion Rotation)> MirrorOffsets = new();
    private readonly string _pairKey;
    internal bool GripCorrectionSettled => _gripCorrectionSettled;
    internal string GripCorrectionSummary { get; private set; } = "";

    internal string DiagnosticSummary { get; }

    private OffhandVisualRig(GameObject model, WeaponPrefab prefab, Player.FirearmController primary, string pairKey)
    {
        _model = model.transform;
        _primary = primary;
        _pairKey = pairKey;
        _parent = primary.ControllerGameObject.transform;
        _root = prefab.Hierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        _primaryRoot = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        _primaryFireport = primary.CurrentFireport.Original;
        if (_root == null || _primaryRoot == null || _primaryFireport == null)
            throw new InvalidOperationException("Native offhand/primary presentation transforms are missing.");
        _fireport = FindFireport(model);
        _animatedWeapon = VisibleWeaponNode(_fireport, _root);
        _rightVisibleGun = VisibleWeaponNode(_primaryFireport, _primaryRoot);
        _camera = CameraManager.Instance?.Camera;

        // Match a real primary geometry renderer rather than assuming a layer
        // number, changing a camera mask, or copying particle/effect layers.
        _nativeLayer = -1;
        // The whole controller also contains arms/auxiliary renderers; start at
        // the native gun transform to select the weapon's rendering contract.
        if (primary.GunBaseTransform == null)
            throw new InvalidOperationException("The primary has no native gun transform for first-person geometry.");
        foreach (Renderer renderer in primary.GunBaseTransform.GetComponentsInChildren<Renderer>(true))
            if (IsGeometry(renderer) && renderer.enabled && renderer.gameObject.activeInHierarchy)
            { _nativeLayer = renderer.gameObject.layer; break; }
        if (_nativeLayer < 0)
            throw new InvalidOperationException("The primary has no active native geometry renderer to establish its first-person layer.");

        _renderers = Array.FindAll(model.GetComponentsInChildren<Renderer>(true), IsGeometry);
        if (_renderers.Length == 0)
            throw new InvalidOperationException("The assembled offhand has no mesh renderers.");
        _layers = new int[_renderers.Length];
        _forcedOff = new bool[_renderers.Length];
        _shadows = new ShadowCastingMode[_renderers.Length];
        for (int i = 0; i < _renderers.Length; i++)
        {
            Renderer renderer = _renderers[i];
            _layers[i] = renderer.gameObject.layer;
            _forcedOff[i] = renderer.forceRenderingOff;
            _shadows[i] = renderer.shadowCastingMode;
        }
        _skins = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        _offscreen = new bool[_skins.Length];
        for (int i = 0; i < _skins.Length; i++) _offscreen[i] = _skins[i].updateWhenOffscreen;

        // ResetStatesToDefault leaves the pistol animator in SPAWN (gun lowered), not IDLE. The
        // native idle is sampled explicitly instead, without dispatching animation events or
        // running SpawnOperation (which can transact chamber ammunition).
        var firearms = prefab.FirearmsAnimator;
        var animator = firearms.Animator;
        animator.enabled = true;
        prefab.ResetStatesToDefault();
        // The isolated presentation never runs SpawnOperation.Start, which
        // normally sets Active=true. Otherwise the next sampled IDLE frame
        // follows the native hide transition even though every renderer is on.
        firearms.SetActiveParam(true, resetLeftHand: false);
        firearms.SetFastHide(false);
        animator.Play(firearms.FullIdleStateName, FirearmsAnimator.HANDS_LAYER_INDEX, 0f);
        animator.Update(0f);
        if (!firearms.IsIdling())
            throw new InvalidOperationException("The native offhand animator did not accept its shared hands idle state.");
        try { NativeClip = NativeClipRetarget.Create(prefab); }
        catch (InvalidOperationException) { NativeClip = null; }
        animator.enabled = false;

        Transform donorPalm = prefab.Hierarchy.GetTransform(ECharacterWeaponBones.HumanRPalm);
        if (donorPalm == null)
            throw new InvalidOperationException("The assembled pistol has no native right-palm grip reference.");
        _nativeGripDonor = donorPalm;
        Vector3 rightGrip = _root.InverseTransformPoint(donorPalm.position);
        Quaternion rightGripRotation = Quaternion.Inverse(_root.rotation) * donorPalm.rotation;
        _leftGripPositionLocal = new Vector3(-rightGrip.x, rightGrip.y, rightGrip.z);
        _leftGripRotationLocal = new Quaternion(rightGripRotation.x, -rightGripRotation.y,
            -rightGripRotation.z, rightGripRotation.w) * new Quaternion(0f, 1f, 0f, 0f);
        _readyGripPosition = _leftGripPositionLocal;
        _readyGripRotation = _leftGripRotationLocal;
        MeasureGripCorrection(prefab, primary, rightGrip, rightGripRotation);
        _model.SetParent(_parent, true);
        ApplyPresentationFlags();
        DiagnosticSummary = "Native idle sampled; owned animator advances explicitly without gameplay events; geometry=" + _renderers.Length
            + "; native_primary_layer=" + _nativeLayer
            + "; independent secondary prefab under the live primary hands object. No player animator rebind.";
    }

    internal static OffhandVisualRig Create(GameObject model, WeaponPrefab prefab, Player.FirearmController primary, string pairKey)
    {
        if (model == null || prefab == null || primary == null)
            throw new ArgumentNullException("An offhand visual dependency is missing.");
        return new OffhandVisualRig(model, prefab, primary, pairKey);
    }

    private static bool IsGeometry(Renderer renderer) => renderer is MeshRenderer || renderer is SkinnedMeshRenderer;

    private static Transform FindFireport(GameObject model)
    {
        Transform? result = null;
        foreach (Transform transform in model.GetComponentsInChildren<Transform>(true))
        {
            if (transform.name != WeaponPrefab.BONE_FIREPORT || !transform.gameObject.activeInHierarchy) continue;
            if (result != null) throw new InvalidOperationException("Multiple active offhand fireports are unsupported.");
            result = transform;
        }
        return result ?? throw new InvalidOperationException("The assembled offhand has no active native fireport.");
    }

    private void ApplyPresentationFlags()
    {
        if (!_model.gameObject.activeSelf) _model.gameObject.SetActive(true);
        for (int i = 0; i < _renderers.Length; i++)
        {
            Renderer renderer = _renderers[i];
            if (renderer == null) continue;
            renderer.gameObject.layer = _nativeLayer;
            renderer.forceRenderingOff = !_presentationVisible;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                renderer.shadowCastingMode = ShadowCastingMode.On;
        }
        for (int i = 0; i < _skins.Length; i++)
            if (_skins[i] != null) _skins[i].updateWhenOffscreen = true;
        // Preserve native enabled/active states for LODs, alternative magazines,
        // cartridges and attachments. Enabling every child would duplicate them.
    }

    internal Vector3 LeftGripPosition => _root.TransformPoint(_leftGripPositionLocal);
    internal Quaternion LeftGripRotation => _root.rotation * _leftGripRotationLocal;
    internal bool HasVisibleGeometry
    {
        get
        {
            if (!_presentationVisible || _model == null || !_model.gameObject.activeInHierarchy) return false;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer != null && renderer.enabled && renderer.gameObject.activeInHierarchy
                    && !renderer.forceRenderingOff && (_camera == null || (_camera.cullingMask & (1 << renderer.gameObject.layer)) != 0))
                    return true;
            }
            return false;
        }
    }

    internal void UseNativeActionGrip()
    {
        // During native holster/draw the animated weapon and hand both move.
        // Their evaluated relative grip, not the cached idle grip, is the contact.
        Vector3 position = _root.InverseTransformPoint(_nativeGripDonor.position);
        Quaternion rotation = Quaternion.Inverse(_root.rotation) * _nativeGripDonor.rotation;
        _leftGripPositionLocal = new Vector3(-position.x, position.y, position.z);
        _leftGripRotationLocal = new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w)
            * new Quaternion(0f, 1f, 0f, 0f);
    }

    internal void RestoreReadyGrip()
    {
        _leftGripPositionLocal = _readyGripPosition;
        _leftGripRotationLocal = _readyGripRotation;
    }

    // Cosmetic service visibility only. Native magazine/attachment enabled
    // states and the controller's hierarchy/ownership remain untouched.
    internal void SetPresentationVisible(bool visible)
    {
        if (_restored) return;
        _presentationVisible = visible;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].forceRenderingOff = !visible;
    }

    // Initial placement only, before a live native arm snapshot is available.
    internal void Align(Vector3 stance, float spacingScale)
    {
        EnsurePresentation();
        if (_liveGripAligned) return;
        Quaternion desired = _primaryRoot.rotation;
        _model.rotation = desired * Quaternion.Inverse(_root.rotation) * _model.rotation;
        stance.x *= spacingScale;
        _model.position += _primaryRoot.TransformPoint(stance) - _root.position;
    }

    internal void AlignToPalm(Vector3 palmPosition, Quaternion palmRotation, PistolPoseSample pose)
    {
        EnsurePresentation();
        Quaternion nativeRoot = palmRotation * Quaternion.Inverse(_leftGripRotationLocal);
        Quaternion desired = nativeRoot * Quaternion.Euler(-pose.PitchUpDegrees, 0f, 0f);
        _model.rotation = desired * Quaternion.Inverse(_root.rotation) * _model.rotation;
        Vector3 target = palmPosition + nativeRoot * new Vector3(0f, pose.VerticalMetres, pose.ForwardMetres);
        _model.position += target - LeftGripPosition;
        if (!_gripCorrectionSettled && RightHandsIdle()) MeasureRightGrip(settle: true);
        if (!_correctionApplied)
        {
            _appliedCorrectionPosition = _gripCorrectionPosition;
            _appliedCorrectionRotation = _gripCorrectionRotation;
            _correctionApplied = true;
        }
        else
        {
            float ease = 1f - Mathf.Exp(-12f * Mathf.Max(0f, Time.deltaTime));
            _appliedCorrectionPosition = Vector3.Lerp(_appliedCorrectionPosition, _gripCorrectionPosition, ease);
            _appliedCorrectionRotation = Quaternion.Slerp(_appliedCorrectionRotation, _gripCorrectionRotation, ease);
        }
        // The palm came from the right pistol's grip; the left pistol sits where its own
        // grip would put it. The callers' grip IK then moves the palm onto this gun.
        Quaternion rootRotation = _root.rotation;
        Vector3 rootPosition = _root.position + rootRotation * _appliedCorrectionPosition;
        Quaternion corrected = rootRotation * _appliedCorrectionRotation;
        _model.rotation = corrected * Quaternion.Inverse(rootRotation) * _model.rotation;
        _model.position += rootPosition - _root.position;
        _liveGripAligned = true;
    }

    // The "weapon" node of the model that carries the given fireport (the rendered gun).
    private static Transform? VisibleWeaponNode(Transform fireport, Transform root)
    {
        for (Transform? node = fireport; node != null && node != root; node = node.parent)
            if (node.name == "weapon") return node;
        return null;
    }

    // Native draw/holster (SpawnOperation/Remove): the left gun follows the mirror image of the
    // rendered right gun (the weapon node above its fireport) in the hands' look space about the
    // shoulder centre, the retarget's own mirror. EFT's draw and hide clips rotate the gun
    // relative to the hand (the IK marker is animated), so a gun hung from the mirrored palm by
    // its idle grip drifts off that path.
    // The ready pose is not an exact mirror of the rendered right gun (the left muzzle sits a few
    // centimetres deeper), so the offset recorded at paired idle (RecordMirrorOffset) is applied
    // on top, making both ends of the transition the ready pose.
    // weight < 1 blends from the current (palm-driven) placement; the caller's grip IK then puts
    // the left palm on this gun.
    internal void AlignToMirroredGun(Vector3 centre, Quaternion look, float weight)
    {
        if (weight <= 0f || _animatedWeapon == null || !MirroredRightGun(centre, look, out Vector3 position, out Quaternion rotation)) return;
        if (MirrorOffsets.TryGetValue(_pairKey, out var offset))
        {
            position += rotation * offset.Position;
            rotation *= offset.Rotation;
        }
        if (weight < 1f)
        {
            position = Vector3.Lerp(_animatedWeapon.position, position, weight);
            rotation = Quaternion.Slerp(_animatedWeapon.rotation, rotation, weight);
        }
        _model.rotation = rotation * Quaternion.Inverse(_animatedWeapon.rotation) * _model.rotation;
        _model.position += position - _animatedWeapon.position;
    }

    // Settled paired idle, after the final placement of the pass: the ready pose relative to the
    // mirrored rendered right gun. A frame's sample is its last pass (what EFT renders). It is
    // stored only after StableFrames consecutive frames agree, so a transient frame (the frame a
    // switch begins in, a procedural draw-in) never becomes the offset for later transitions.
    internal void RecordMirrorOffset(Vector3 centre, Quaternion look)
    {
        if (_animatedWeapon == null || !RightHandsIdle() || !MirroredRightGun(centre, look, out Vector3 position, out Quaternion rotation)) return;
        Quaternion inverse = Quaternion.Inverse(rotation);
        var sample = (inverse * (_animatedWeapon.position - position), inverse * _animatedWeapon.rotation);
        int frame = Time.frameCount;
        if (frame != _sampleFrame)
        {
            bool consecutive = _sampleFrame == frame - 1;
            _stableFrames = consecutive && _settledFrames > 0 && Agrees(_frameSample, _settledSample) ? _stableFrames + 1 : 0;
            _settledSample = consecutive ? _frameSample : sample;
            _settledFrames = consecutive ? _settledFrames + 1 : 0;
            if (_stableFrames >= StableFrames) MirrorOffsets[_pairKey] = _settledSample;
            _sampleFrame = frame;
        }
        _frameSample = sample;
    }

    private const int StableFrames = 10;
    private int _sampleFrame = -1, _stableFrames, _settledFrames;
    private (Vector3 Position, Quaternion Rotation) _frameSample, _settledSample;

    private static bool Agrees((Vector3 Position, Quaternion Rotation) a, (Vector3 Position, Quaternion Rotation) b)
        => Vector3.Distance(a.Position, b.Position) < .003f && Quaternion.Angle(a.Rotation, b.Rotation) < 1f;

    private bool MirroredRightGun(Vector3 centre, Quaternion look, out Vector3 position, out Quaternion rotation)
    {
        Transform? right = _rightVisibleGun ?? _rightGun;
        position = default; rotation = Quaternion.identity;
        if (right == null) return false;
        Quaternion inverseLook = Quaternion.Inverse(look);
        Vector3 local = inverseLook * (right.position - centre);
        Quaternion lookRotation = inverseLook * right.rotation;
        position = centre + look * new Vector3(-local.x, local.y, local.z);
        rotation = look * new Quaternion(lookRotation.x, -lookRotation.y, -lookRotation.z, lookRotation.w);
        return true;
    }

    // The left palm mirrors the right hand, which holds the RIGHT pistol at that pistol's own
    // grip (its weapon_R_IK_marker, where EFT's limb IK puts the palm). Hanging the left pistol
    // from that palm by the left pistol's grip leaks the right pistol's grip into the left
    // pistol's pose: identical models cancel, different models rotate the left gun. The
    // correction is the difference of the two native grip markers, carried into the left gun's
    // frame through its native palm, then mirrored like the grip:
    //   W = P_left * K_right^-1 * K_left * P_left^-1
    // P_left: left pistol's native idle palm; K: R IK markers in their pistol's weapon-node frame.
    // The right palm itself is not used: at idle EFT's IK puts it on K_right, but outside idle
    // (a draw, a reload ending) it is well off the marker. The right marker is likewise only
    // trusted at native idle; a pair made mid-transition settles on the first idle frame.
    private void MeasureGripCorrection(WeaponPrefab prefab, Player.FirearmController primary, Vector3 palmPosition, Quaternion palmRotation)
    {
        Transform? leftMarker = prefab.Hierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
        Transform? leftGun = prefab.Hierarchy.GetTransform(ECharacterWeaponBones.weapon);
        _rightMarker = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
        _rightGun = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon);
        if (leftMarker == null || leftGun == null || _rightMarker == null || _rightGun == null)
        { GripCorrectionSummary = "grip_correction=unavailable"; _gripCorrectionSettled = true; return; }
        _leftMarkerIdle = RelativePose(leftGun, leftMarker);
        _leftPalmIdle = Matrix4x4.TRS(palmPosition, palmRotation, Vector3.one);
        if (RightHandsIdle()) MeasureRightGrip(settle: true);
        else if (IdleRightMarkers.TryGetValue(primary.Item.StringTemplateId, out Matrix4x4 known)) ApplyRightGrip(known, settle: true, "cached");
        else
        {
            _gripCorrectionPosition = Vector3.zero;
            _gripCorrectionRotation = Quaternion.identity;
            _gripCorrectionSettled = false;
            GripCorrectionSummary = "grip_correction=pending_idle";
        }
    }

    private static Matrix4x4 RelativePose(Transform frame, Transform bone) => frame.worldToLocalMatrix * bone.localToWorldMatrix;

    // Diagnostic at native idle: the recorded idle marker against the live one.
    internal string IdleMarkerCheck()
    {
        if (_rightMarker == null || _rightGun == null || !RightHandsIdle()
            || !IdleRightMarkers.TryGetValue(_primary.Item.StringTemplateId, out Matrix4x4 recorded)) return "marker_check=n/a";
        Matrix4x4 live = RelativePose(_rightGun, _rightMarker);
        return "marker_recorded_vs_live_m=" + Vector3.Distance(recorded.GetColumn(3), live.GetColumn(3)).ToString("F4")
            + " marker_recorded_vs_live_deg=" + Quaternion.Angle(recorded.rotation, live.rotation).ToString("F2");
    }

    // Settled native idle only. A controller just created for a draw can still report its
    // pooled animator's last IDLE state on the first SpawnOperation frame; the operation check
    // keeps that from being measured (and cached) as the idle marker.
    internal bool RightHandsIdle()
    {
        var firearms = _primary.FirearmsAnimator;
        return firearms != null && !_primary.Destroyed && !_primary.IsInSpawnOperation()
            && _primary.CurrentOperation is Player.FirearmController.Idling && firearms.IsIdling()
            && !firearms.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX);
    }

    private void MeasureRightGrip(bool settle)
    {
        if (_rightMarker == null || _rightGun == null) return;
        Matrix4x4 kRight = RelativePose(_rightGun, _rightMarker);
        if (settle) IdleRightMarkers[_primary.Item.StringTemplateId] = kRight;
        ApplyRightGrip(kRight, settle, "live");
    }

    private void ApplyRightGrip(Matrix4x4 kRight, bool settle, string source)
    {
        Matrix4x4 w = _leftPalmIdle * kRight.inverse * _leftMarkerIdle * _leftPalmIdle.inverse;
        Vector3 position = w.GetColumn(3);
        Quaternion rotation = w.rotation;
        _gripCorrectionPosition = new Vector3(-position.x, position.y, position.z);
        _gripCorrectionRotation = new Quaternion(rotation.x, -rotation.y, -rotation.z, rotation.w);
        _gripCorrectionSettled = settle;
        GripCorrectionSummary = "grip_correction_m=" + position.magnitude.ToString("F4") + " grip_correction_deg="
            + Quaternion.Angle(Quaternion.identity, rotation).ToString("F2") + " marker_delta_deg="
            + Quaternion.Angle(_leftMarkerIdle.rotation, kRight.rotation).ToString("F2") + " right_idle=" + settle + " source=" + source;
        GripCorrectionChanged = true;
    }

    // Set when the correction is (re)measured, for the caller's DW_GRIP diagnostic.
    internal bool GripCorrectionChanged { get; set; }

    private void EnsurePresentation()
    {
        if (_restored || _model == null || _root == null || _primaryRoot == null || _parent == null)
            throw new InvalidOperationException("An offhand presentation transform is unavailable.");
        if (_model.parent != _parent) _model.SetParent(_parent, true);
        ApplyPresentationFlags();
    }

    // Explicit pair/stance diagnostics only; never called every frame.
    internal string CaptureDiagnostic()
    {
        var text = new StringBuilder("DW_VISUAL");
        text.Append(" live_grip=").Append(_liveGripAligned);
        text.Append(" presentation_visible=").Append(_presentationVisible);
        if (_animatedWeapon != null)
            text.Append(" animated_weapon_local_position=").Append(_animatedWeapon.localPosition.ToString("F4"))
                .Append(" animated_weapon_local_rotation=").Append(_animatedWeapon.localRotation.eulerAngles.ToString("F2"));
        text.Append(" active=").Append(_model != null && _model.gameObject.activeInHierarchy);
        text.Append(" parent_ok=").Append(_model != null && _model.parent == _parent);
        text.Append(" renderers=").Append(_renderers.Length);
        int enabled = 0, active = 0, forced = 0, masked = 0;
        for (int i = 0; i < _renderers.Length; i++)
        {
            Renderer renderer = _renderers[i];
            if (renderer == null) continue;
            if (renderer.enabled) enabled++;
            if (renderer.enabled && renderer.gameObject.activeInHierarchy) active++;
            if (renderer.forceRenderingOff) forced++;
            if (_camera != null && (_camera.cullingMask & (1 << renderer.gameObject.layer)) != 0) masked++;
        }
        text.Append(" enabled=").Append(enabled).Append(" active_enabled=").Append(active)
            .Append(" forced_off=").Append(forced).Append(" main_camera_layer_matches=").Append(masked);
        if (_camera != null)
            text.Append(" camera=").Append(_camera.name).Append(" mask=").Append(_camera.cullingMask)
                .Append(" near=").Append(_camera.nearClipPlane.ToString("F4"))
                .Append(" root_camera=").Append(_camera.transform.InverseTransformPoint(_root.position).ToString("F4"))
                .Append(" muzzle_viewport=").Append(_camera.WorldToViewportPoint(_fireport.position).ToString("F4"));
        text.Append(" muzzle_forward_dot=").Append(Vector3.Dot(_fireport.forward, _primaryFireport.forward).ToString("F4"))
            .Append(" muzzle_up_dot=").Append(Vector3.Dot(_fireport.up, _primaryFireport.up).ToString("F4"))
            .Append(" root_position=").Append(_root.position.ToString("F4"))
            .Append(" muzzle_position=").Append(_fireport.position.ToString("F4"));
        return text.ToString();
    }

    internal void Restore()
    {
        if (_restored) return;
        for (int i = 0; i < _renderers.Length; i++)
        {
            Renderer renderer = _renderers[i];
            if (renderer == null) continue;
            renderer.gameObject.layer = _layers[i];
            renderer.forceRenderingOff = _forcedOff[i];
            renderer.shadowCastingMode = _shadows[i];
        }
        for (int i = 0; i < _skins.Length; i++)
            if (_skins[i] != null) _skins[i].updateWhenOffscreen = _offscreen[i];
        _restored = true;
    }
}
