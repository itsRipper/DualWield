using System;
using System.Text;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

internal sealed class OffhandHandPose
{
    private readonly NativeArmRetarget _arms;
    private readonly Transform _upperArm, _elbow, _palm, _bodyReference, _model;
    private readonly Transform _primaryRoot, _offhandRoot;
    private readonly IAnimator _primaryAnimator;
    private readonly IAnimator _offhandAnimator;
    private readonly FirearmsAnimator _offhandFirearmsAnimator;
    private readonly int[] _primarySelectorStates, _offhandSelectorStates;
    private readonly string _offhandIdle;
    // The right hand's native idle finger grip per right-pistol template (see
    // NativeArmRetarget.CaptureRightGripReference), captured only at settled idle: a mid-draw
    // finger pose taken as the reference bends the left grip for the whole pair.
    private static readonly System.Collections.Generic.Dictionary<string, Quaternion[]> IdleRightGrips = new();
    private readonly Player.FirearmController _primary;
    private bool _gripReferenceSettled;
    private bool _offhandSelectorActive, _offhandSelectorObserved;
    private int _offhandSelectorFrame = -1;
    private float _selectorWait;
    private readonly Weapon _offhandWeapon;
    private readonly int _fireState, _semiFireState;
    private readonly int _activeParameter;
    private bool _pendingBoltCatch;
    private bool _shotVisualActive;
    private long _sampledShots;
    private bool _selectorIsolated;
    private bool _restored;
    private bool _nativeActionOwned;
    private int _presentationState, _presentationTerminal;
    private bool _presentationAction, _presentationObserved;
    private float _presentationStarted;
    private float _presentationRate = 1f;
    private readonly Quaternion[] _drawFrom;
    private bool _drawBlendPending, _drawBlendCaptured;
    private int _reportedNativeState = int.MinValue;
    internal bool NativeStateChanged { get; private set; }
    internal bool PresentationFinished { get; private set; }
    internal bool PresentationFailed { get; private set; }
    internal bool PresentationActive => _presentationAction;
    internal bool PairedIdleReady => !_restored && !_presentationAction && !_nativeActionOwned
        && _offhandAnimator.GetBool(_activeParameter) && _offhandFirearmsAnimator.IsIdling();
    internal string DiagnosticSummary { get; }
    internal float ReachError { get; private set; }
    internal float ReachCorrection { get; private set; }
    internal int FingerCount => _arms.FingerCount;
    internal bool HasSnapshot => _arms.HasSnapshot;
    internal Vector3 PalmPosition => _palm.position;
    internal Quaternion PalmRotation => _palm.rotation;
    internal Transform RightPalm => _arms.RightPalm;
    internal NativeArmRetarget Arms => _arms;
    // Monotonic progress through the holster/draw state: it never jumps back when the
    // animator moves on to the terminal state.
    internal float PresentationNormalizedTime => _presentationAction ? _presentationProgress : 1f;
    private float _presentationProgress;
    internal bool CanChangeOffhandSelector => !_restored && !(_offhandWeapon.IsBoltCatch
        && _offhandWeapon.NoFiremodeOnBoltcatch && _offhandFirearmsAnimator.GetBoltCatch());

    private OffhandHandPose(NativeArmRetarget arms, Transform body, Transform model,
        Player.FirearmController primary, WeaponPrefab prefab, Weapon offhandWeapon)
    {
        _arms = arms; _bodyReference = body; _model = model;
        _upperArm = arms.LeftUpperArm; _elbow = arms.LeftElbow; _palm = arms.LeftPalm;
        _drawFrom = new Quaternion[arms.ChainLength];
        _primaryRoot = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        _offhandRoot = prefab.Hierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        _primaryAnimator = primary.FirearmsAnimator.Animator;
        _offhandFirearmsAnimator = prefab.FirearmsAnimator;
        _offhandAnimator = _offhandFirearmsAnimator.Animator;
        _offhandWeapon = offhandWeapon;
        _activeParameter = _offhandAnimator.StringToHash("Active");
        _fireState = _offhandAnimator.StringToHash(_offhandFirearmsAnimator.FullFireStateName);
        _semiFireState = _offhandAnimator.StringToHash(_offhandFirearmsAnimator.FullSemiFireStateName);
        _primarySelectorStates = SelectorStates(_primaryAnimator);
        _offhandSelectorStates = SelectorStates(_offhandAnimator);
        _offhandIdle = _offhandFirearmsAnimator.FullIdleStateName;
        SyncMechanicalState();
        _offhandFirearmsAnimator.SetFireMode(_offhandWeapon.SelectedFireMode, skipAnimation: true);
        _arms.EnableAlternatingLocomotion();
        _primary = primary;
        if (PrimaryIdle()) { IdleRightGrips[primary.Item.StringTemplateId] = _arms.CaptureRightGripReference(); _gripReferenceSettled = true; }
        else if (IdleRightGrips.TryGetValue(primary.Item.StringTemplateId, out Quaternion[] known))
        { _arms.UseRightGripReference(known); _gripReferenceSettled = true; }
        DiagnosticSummary = "Live native FPS arm retarget: " + arms.BoneCount
            + " mapped bones including forearm twists and " + arms.FingerCount
            + " fingers; source captured before offhand writes. Native skin/gloves retained.";
    }

    internal static OffhandHandPose Create(Player player, Player.FirearmController primary, WeaponPrefab offhandPrefab, Weapon offhandWeapon)
    {
        if (player == null || primary == null || offhandPrefab == null || player.HandsController != primary)
            throw new InvalidOperationException("The native first-person primary changed before arm binding.");
        NativeArmRetarget arms = NativeArmRetarget.Create(player);
        try
        {
            Transform donor = offhandPrefab.Hierarchy.GetTransform(ECharacterWeaponBones.HumanRPalm);
            if (donor == null) throw new InvalidOperationException("The offhand native grip donor is unavailable.");
            arms.ConfigureFingerGrip(donor);
            return new OffhandHandPose(arms, player.Transform.Original, offhandPrefab.transform, primary, offhandPrefab, offhandWeapon);
        }
        catch { arms.Restore(); throw; }
    }

    // Settled native idle of the right pistol: not a draw, check or animator transition.
    private bool PrimaryIdle()
    {
        var firearms = _primary.FirearmsAnimator;
        return firearms != null && !_primary.Destroyed && !_primary.IsInSpawnOperation()
            && _primary.CurrentOperation is Player.FirearmController.Idling && firearms.IsIdling()
            && !firearms.Animator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX);
    }

    internal void BeforeNativePose() => _arms.RestoreBeforeNativePose();
    internal void CaptureNativePose()
    {
        AdvanceOffhandAnimation();
        _selectorIsolated = PrimarySelectorPlaying();
        // A right shot's recoil is removed from the mirrored source itself (NativeArmRetarget).
        _arms.IsolatePrimaryAction(_selectorIsolated);
        _arms.CaptureNativePose();
        if (!_gripReferenceSettled && PrimaryIdle())
        {
            IdleRightGrips[_primary.Item.StringTemplateId] = _arms.CaptureRightGripReference();
            _gripReferenceSettled = true;
        }
        // Bones are still the native evaluation here: the start of a smooth draw.
        if (_drawBlendPending && !_drawBlendCaptured) { _arms.CopyLeftLocal(_drawFrom); _drawBlendCaptured = true; }
    }

    // Draw recovery after a native right reload starts from the native left support
    // grip, not from a lowered snap. weight follows the authored handoff curve.
    internal void BlendDrawFromNative(float weight)
    {
        if (_restored || !_drawBlendCaptured || weight >= 1f) return;
        _arms.BlendLeftFrom(_drawFrom, weight);
    }

    internal void CopyLeftPose(Quaternion[] buffer) => _arms.CopyLeftLocal(buffer);

    internal void SolveRight(Vector3 target, Quaternion rotation, float curl)
    {
        if (_restored || !_arms.HasSnapshot) return;
        _arms.BeginRightOverride();
        SolveArm(_arms.RightUpperArm, _arms.RightElbow, _arms.RightPalm, target, rotation, 1f);
        _arms.ApplyRightFingerCurl(curl);
        _arms.CommitOverrides();
    }
    private bool PrimarySelectorPlaying()
        => SelectorPlaying(_primaryAnimator, _primarySelectorStates);

    private static int[] SelectorStates(IAnimator animator)
    {
        const int handsLayer = FirearmsAnimator.HANDS_LAYER_INDEX;
        string layer = animator.GetLayerName(handsLayer) + ".";
        return new[] { animator.StringToHash(layer + "FIREMODE"), animator.StringToHash(layer + "FIREMODE EMPTY"),
            animator.StringToHash(layer + "FIREMODE CHECK") };
    }

    private static bool SelectorPlaying(IAnimator animator, int[] hashes)
    {
        const int layer = FirearmsAnimator.HANDS_LAYER_INDEX;
        int current = animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
        for (int i = 0; i < hashes.Length; i++) if (current == hashes[i]) return true;
        if (!animator.IsInTransition(layer)) return false;
        int next = animator.GetNextAnimatorStateInfo(layer).fullPathHash;
        for (int i = 0; i < hashes.Length; i++) if (next == hashes[i]) return true;
        return false;
    }

    // Native ChangeFireMode already set the animator's selector parameter; this tracks the
    // resulting native selector state for the arm.
    internal void BeginOffhandSelector()
    {
        if (!CanChangeOffhandSelector) return;
        // A single-mode pistol has no selector action to fabricate.
        if (_offhandWeapon.FireMode?.AvailableEFireModes == null
            || _offhandWeapon.FireMode.AvailableEFireModes.Length < 2) return;
        _offhandSelectorActive = true; _offhandSelectorObserved = false; _offhandSelectorFrame = -1;
        _selectorWait = 0f;
    }


    // A native offhand operation (malfunction, examine, checks, reload) owns the left pistol's
    // animator. The left arm is not driven by the clip's raw arm bones (EFT itself IKs the hands
    // to the weapon markers); NativeClipRetarget moves the gun and the palm follows it.
    internal void OwnNativeAction(bool owned) => _nativeActionOwned = owned && !_restored;

    // A native left reload waits on its inventory transaction at the current clip frame; no
    // event is emitted while held.
    internal bool HoldNativeAnimation { get; set; }

    internal bool OffhandSelectorActive => _offhandSelectorActive;

    // Holster/draw presentation: the gun leads (NativeClipRetarget) instead of the clip's raw arm.
    internal bool PresentationFollowsGun { get; set; }

    // Purely visual native clip playback. No SpawnOperation, inventory operation
    // or animation-event consumer is created for this independent display.
    internal bool BeginPresentationAction(bool draw, float rate = 1f, bool blendFromNative = false)
    {
        if (_restored) return false;
        _presentationRate = Mathf.Clamp(rate, .5f, 2f);
        _drawBlendPending = blendFromNative; _drawBlendCaptured = false;
        _offhandSelectorActive = false;
        _offhandFirearmsAnimator.SetFire(false);
        _offhandFirearmsAnimator.SetFastHide(false);
        _offhandFirearmsAnimator.SetActiveParam(draw, resetLeftHand: false);
        string layer = _offhandAnimator.GetLayerName(FirearmsAnimator.HANDS_LAYER_INDEX) + ".";
        _presentationState = _offhandAnimator.StringToHash(layer + (draw ? "OUT TO IDLE" : "IDLE TO OUT"));
        _presentationTerminal = _offhandAnimator.StringToHash(draw ? _offhandIdle : layer + "SPAWN");
        _offhandAnimator.Play(_presentationState, FirearmsAnimator.HANDS_LAYER_INDEX, 0f);
        _offhandAnimator.Update(0f);
        _presentationObserved = _offhandAnimator.GetCurrentAnimatorStateInfo(FirearmsAnimator.HANDS_LAYER_INDEX).fullPathHash == _presentationState;
        if (!_presentationObserved) { CancelPresentationAction(); return false; }
        _presentationAction = true;
        _presentationProgress = 0f;
        PresentationFinished = PresentationFailed = false;
        _presentationStarted = Time.unscaledTime;
        return true;
    }

    internal void CancelPresentationAction()
    {
        if (_restored) return;
        _presentationAction = false;
        _presentationRate = 1f; _drawBlendPending = _drawBlendCaptured = false;
        PresentationFinished = PresentationFailed = false;
        _arms.OwnSelector(false);
        _offhandFirearmsAnimator.SetActiveParam(true, resetLeftHand: false);
        _offhandAnimator.Play(_offhandIdle, FirearmsAnimator.HANDS_LAYER_INDEX, 0f);
        _offhandAnimator.Update(0f);
    }

    internal void FinishPresentationAction()
    {
        _presentationAction = false;
        _presentationRate = 1f; _drawBlendPending = _drawBlendCaptured = false;
        _arms.OwnSelector(false);
        _offhandFirearmsAnimator.SetActiveParam(true, resetLeftHand: false);
    }

    // Called only after the authoritative shot reports Fired and its ammunition
    // invariants pass. It never transacts cartridges or emits native shot events.
    internal void BeginOffhandShot(Weapon weapon)
    {
        if (_restored || !ReferenceEquals(weapon, _offhandWeapon)) return;
        SyncMechanicalState();
        _pendingBoltCatch = ShouldLockSlide(weapon);
        _offhandFirearmsAnimator.SetBoltCatch(false);
        _offhandFirearmsAnimator.SetFire(false);
        string state = weapon.SelectedFireMode == Weapon.EFireMode.semiauto
            ? _offhandFirearmsAnimator.FullSemiFireStateName : _offhandFirearmsAnimator.FullFireStateName;
        // These are the exact state and event phase used by native FireOperation
        // and AutomaticFireOperation; one explicit playback per actual shot.
        _offhandAnimator.Play(state, FirearmsAnimator.HANDS_LAYER_INDEX, .2f);
        _offhandAnimator.Update(0f);
        _shotVisualActive = true;
        _sampledShots++;
    }

    private static bool ShouldLockSlide(Weapon weapon)
        => weapon.IsBoltCatch && !weapon.ManualBoltCatch && weapon.ChamberAmmoCount == 0
            && weapon.GetCurrentMagazine() != null && weapon.GetCurrentMagazineCount() == 0;

    // After a native magazine/chamber change: e.g. releases a locked slide once fed.
    internal void RefreshMechanicalState() { if (!_restored) SyncMechanicalState(); }

    private void SyncMechanicalState()
    {
        _offhandFirearmsAnimator.SetAmmoInChamber(_offhandWeapon.ChamberAmmoCount);
        _offhandFirearmsAnimator.SetAmmoOnMag(_offhandWeapon.GetCurrentMagazineCount());
        _offhandFirearmsAnimator.SetMagInWeapon(_offhandWeapon.GetCurrentMagazine() != null);
        _offhandFirearmsAnimator.SetBoltCatch(ShouldLockSlide(_offhandWeapon));
    }

    private void AdvanceOffhandAnimation()
    {
        if (_restored || _offhandSelectorFrame == Time.frameCount) return;
        _offhandSelectorFrame = Time.frameCount;
        _offhandAnimator.Update(HoldNativeAnimation ? 0f : Mathf.Max(0f, Time.deltaTime) * (_presentationAction ? _presentationRate : 1f));
        var state = _offhandAnimator.GetCurrentAnimatorStateInfo(FirearmsAnimator.HANDS_LAYER_INDEX);
        NativeStateChanged = state.fullPathHash != _reportedNativeState;
        _reportedNativeState = state.fullPathHash;
        bool firing = state.fullPathHash == _fireState || state.fullPathHash == _semiFireState;
        if (_shotVisualActive && (!firing || state.normalizedTime >= .75f))
        {
            // Native automatic operation engages the catch at 75% of the last
            // shot. Sample that mechanical state, never a fabricated cartridge.
            _offhandFirearmsAnimator.SetBoltCatch(_pendingBoltCatch);
            _shotVisualActive = false;
        }
        if (_presentationAction)
        {
            _arms.OwnSelector(!PresentationFollowsGun);
            _presentationObserved |= state.fullPathHash == _presentationState;
            _presentationProgress = state.fullPathHash == _presentationState
                ? Mathf.Max(_presentationProgress, Mathf.Clamp01(state.normalizedTime))
                : _presentationObserved ? 1f : _presentationProgress;
            PresentationFinished = _presentationObserved && state.fullPathHash == _presentationTerminal
                && !_offhandAnimator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX);
            PresentationFailed = Time.unscaledTime - _presentationStarted > 10f;
            return;
        }
        // The selector and native actions keep the paired grip: only the presentation holster/draw
        // takes the clip's arm motion.
        _arms.OwnSelector(false);
        if (_nativeActionOwned || !_offhandSelectorActive) return;
        bool playing = SelectorPlaying(_offhandAnimator, _offhandSelectorStates);
        _offhandSelectorObserved |= playing;
        _selectorWait += Mathf.Max(0f, Time.deltaTime);
        if ((!playing && _offhandSelectorObserved && !_offhandAnimator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX))
            || _selectorWait > 5f)
        {
            // Do not abort an IDLE frame before the native trigger transition
            // has entered. Missing native actions time out without fake motion.
            _offhandSelectorActive = false;
        }
    }
    internal void ApplyRetarget()
    {
        if (_restored || !_arms.HasSnapshot) return;
        _arms.ApplyRightToLeft();
        // The source palm's actual depth/height in shared native hands-look
        // space is authoritative; local offsets never establish this target.
        SolveArm(_upperArm, _elbow, _palm, _arms.LeftPalmTarget, _palm.rotation, -1f);
        _arms.CommitOverrides();
    }
    internal void Apply() => ApplyRetarget();

    internal void ApplyGripTarget(Vector3 target, Quaternion rotation)
    {
        if (_restored || !_arms.HasSnapshot) return;
        ReachCorrection = 0f;
        if (Vector3.SqrMagnitude(target - _palm.position) > .00000001f
            || Quaternion.Angle(rotation, _palm.rotation) > .001f)
            SolveArm(_upperArm, _elbow, _palm, target, rotation, -1f);
        Vector3 correction = _palm.position - target;
        ReachCorrection = correction.magnitude;
        if (ReachCorrection > .001f)
        {
            _model.position += correction;
            target += correction;
        }
        ReachError = Vector3.Distance(_palm.position, target);
        _arms.CommitOverrides();
    }

    private void SolveArm(Transform upperArm, Transform elbow, Transform palm, Vector3 target, Quaternion targetRotation, float side)
    {
        Vector3 shoulder = upperArm.position;
        float upperLength = Vector3.Distance(shoulder, elbow.position);
        float lowerLength = Vector3.Distance(elbow.position, palm.position);
        Vector3 toTarget = target - shoulder;
        float distance = toTarget.magnitude;
        if (upperLength < .0001f || lowerLength < .0001f || distance < .0001f) return;
        Vector3 direction = toTarget / distance;
        float reach = Mathf.Clamp(distance, Mathf.Abs(upperLength - lowerLength) + .0001f,
            (upperLength + lowerLength) * .9999f);
        float along = (upperLength * upperLength - lowerLength * lowerLength + reach * reach) / (2f * reach);
        float height = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
        Vector3 bend = elbow.position - shoulder;
        bend -= direction * Vector3.Dot(bend, direction);
        if (bend.sqrMagnitude < .0000001f)
        {
            Vector3 pole = _bodyReference.right * side - _bodyReference.up * .35f;
            bend = pole - direction * Vector3.Dot(pole, direction);
        }
        if (bend.sqrMagnitude < .0000001f) bend = Vector3.Cross(direction, _bodyReference.forward);
        bend.Normalize();
        Vector3 desiredElbow = shoulder + direction * along + bend * height;
        Vector3 desiredPalm = shoulder + direction * reach;
        upperArm.rotation = Quaternion.FromToRotation(elbow.position - shoulder, desiredElbow - shoulder) * upperArm.rotation;
        elbow.rotation = Quaternion.FromToRotation(palm.position - elbow.position, desiredPalm - elbow.position) * elbow.rotation;
        palm.rotation = targetRotation;
    }

    internal string CaptureDiagnostic()
    {
        var text = new StringBuilder("DW_HAND_VISUAL");
        text.Append(" source=live_native_fps snapshot_frame=").Append(_arms.SnapshotFrame)
            .Append(" mapped_bones=").Append(_arms.BoneCount).Append(" fingers=").Append(FingerCount)
            .Append(" selector_isolated=").Append(_selectorIsolated);
        var state = _offhandAnimator.GetCurrentAnimatorStateInfo(FirearmsAnimator.HANDS_LAYER_INDEX);
        text.Append(" native_active=").Append(_offhandAnimator.GetBool(_activeParameter))
            .Append(" native_hands_state=").Append(state.fullPathHash)
            .Append(" native_hands_phase=").Append(state.normalizedTime.ToString("F4"))
            .Append(" native_next_state=").Append(_offhandAnimator.IsInTransition(FirearmsAnimator.HANDS_LAYER_INDEX)
                ? _offhandAnimator.GetNextAnimatorStateInfo(FirearmsAnimator.HANDS_LAYER_INDEX).fullPathHash : 0)
            .Append(" presentation_action=").Append(_presentationAction)
            .Append(" presentation_done=").Append(PresentationFinished);
        text.Append(" left_selector_active=").Append(_offhandSelectorActive)
            .Append(" left_selector_observed=").Append(_offhandSelectorObserved)
            .Append(" left_selector_wait_s=").Append(_selectorWait.ToString("F3"))
            .Append(" native_shot_visuals=").Append(_sampledShots)
            .Append(" native_boltcatch=").Append(_offhandFirearmsAnimator.GetBoltCatch())
            .Append(" native_cycle_depth_correction_m=").Append(_arms.LocomotionDepthCorrection.ToString("F4"));
        text.Append(" native_left_locomotion=").Append(_arms.NativeLocomotionActive)
            .Append(" native_locomotion_weight=").Append(_arms.NativeLocomotionWeight.ToString("F4"))
            .Append(" native_body_phase=").Append(_arms.NativeLocomotionPhase.ToString("F4"));
        foreach (SkinnedMeshRenderer renderer in _arms.Renderers)
        {
            if (renderer == null) continue;
            text.Append(" skin=").Append(renderer.name).Append("/active:").Append(renderer.gameObject.activeInHierarchy)
                .Append("/enabled:").Append(renderer.enabled).Append("/forced_off:").Append(renderer.forceRenderingOff)
                .Append("/layer:").Append(renderer.gameObject.layer);
        }
        var camera = CameraManager.Instance?.Camera;
        if (camera != null)
        {
            Vector3 right = camera.transform.InverseTransformPoint(_primaryRoot.position);
            Vector3 left = camera.transform.InverseTransformPoint(_offhandRoot.position);
            text.Append(" palm_camera=").Append(camera.transform.InverseTransformPoint(_palm.position).ToString("F4"))
                .Append(" shoulder_camera=").Append(camera.transform.InverseTransformPoint(_upperArm.position).ToString("F4"))
                .Append(" primary_root_camera=").Append(right.ToString("F4"))
                .Append(" offhand_root_camera=").Append(left.ToString("F4"))
                .Append(" root_delta_camera=").Append((left-right).ToString("F4"))
                .Append(" target_reach_error_m=").Append(Vector3.Distance(_palm.position,_arms.LeftPalmTarget).ToString("F4"));
        }
        return text.Append(" reach_error_m=").Append(ReachError.ToString("F4"))
            .Append(" reach_correction_m=").Append(ReachCorrection.ToString("F4")).ToString();
    }

    internal void Restore()
    {
        if (_restored) return;
        _restored = true;
        _offhandSelectorActive = false;
        _nativeActionOwned = false;
        _arms.OwnSelector(false);
        try
        {
            _offhandAnimator.Play(_offhandIdle, FirearmsAnimator.HANDS_LAYER_INDEX, 0f);
            _offhandAnimator.Update(0f);
        }
        finally { _arms.Restore(); }
    }
}
