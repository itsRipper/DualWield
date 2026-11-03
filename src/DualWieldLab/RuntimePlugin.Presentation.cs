using System;
using EFT;
using EFT.InputSystem;
using UnityEngine;

namespace DualWieldLab;

public sealed partial class RuntimePlugin
{
    // Left pistol offset (right Weapon_root frame) for the frames before the live arm pose exists.
    private static readonly Vector3 HipStanceOffset = new Vector3(-0.30f, 0f, 0f);
    private static readonly Vector3 AimStanceOffset = new Vector3(-0.22f, 0f, 0.04f);
    private int _reportedHoldRejections;

    // Native malfunction identification and repair use both hands as the checks do.
    private static bool IsTwoHandedUtility(Player.FirearmController controller)
        => controller.CurrentOperation is Player.FirearmController.ExamineMalfunctionTypeOperation
            || controller.CurrentOperation is Player.FirearmController.RepairMalfunction
            || (controller.CurrentOperation is Player.FirearmController.UtilityOperation utility
            && utility._utilityOperationType != Player.FirearmController.UtilityOperation.EUtilityType.None
            && utility._utilityOperationType != Player.FirearmController.UtilityOperation.EUtilityType.CheckFireMode);

    // Native magazine/chamber checks animate the magazine along the native LEFT-hand IK path,
    // and UtilityOperation derives from Idling, so an "is Idling" pose gate keeps mirroring the
    // right arm onto the left (a floating magazine). The left arm is returned to native here.
    private void BeginInspection(ECommand command)
    {
        if (_player == null || _primary == null || _handPose == null || _visualRig == null
            || _primary.CurrentOperation is not Player.FirearmController.Idling || IsTwoHandedUtility(_primary)
            || _primary.IsTriggerPressed) return;
        StopPrimaryTrigger(); StopOffhandTrigger();
        _inspectCommand = command;
        _inspectSince = Time.unscaledTime;
        _inspectSeen = _inspectStepDone = false;
        _inspectDone.Reset();
        _holsterPoseCaptured = false;
        bool holstering;
        try { holstering = _handPose.BeginPresentationAction(false, _handoffClip?.Constant("holster_rate", 1f) ?? 1f); }
        catch (Exception e) { Logger.LogWarning("Offhand holster for native check unavailable: " + e.Message); holstering = false; }
        _inspect = InspectPhase.Holstering;
        LogDiagnostic("DW_INSPECT step=holster command=" + command + " native_holster=" + holstering);
        if (holstering) _shot?.PlayHandlingSound("SndHolster");
        else StartNativeInspection();
    }

    private void StartNativeInspection()
    {
        _visualRig?.SetPresentationVisible(false);
        _inspect = InspectPhase.Native;
        _inspectSince = Time.unscaledTime;
        bool started = false;
        try
        {
            if (_primary != null)
                started = _inspectCommand == ECommand.CheckAmmo ? _primary.CheckAmmo()
                    : _inspectCommand == ECommand.CheckChamber ? _primary.CheckChamber() : _primary.ExamineWeapon();
        }
        catch (Exception e) { Logger.LogWarning("Native weapon check failed to start: " + e.Message); }
        LogDiagnostic("DW_INSPECT step=native command=" + _inspectCommand + " started=" + started);
        if (!started) BeginInspectionDraw();
    }

    private void BeginInspectionDraw()
    {
        if (_handPose == null) { EndInspection(); return; }
        _inspect = InspectPhase.Drawing;
        _inspectStepDone = false;
        _drawStarted = Time.unscaledTime;
        bool drawing;
        try { drawing = _handPose.BeginPresentationAction(true, _handoffClip?.Constant("draw_rate", 1f) ?? 1f, blendFromNative: _handoffClip != null); }
        catch (Exception e) { Logger.LogWarning("Offhand draw after native check unavailable: " + e.Message); drawing = false; }
        LogDiagnostic("DW_INSPECT step=draw native_draw=" + drawing);
        if (drawing) _shot?.PlayHandlingSound("SndDraw");
        else EndInspection();
    }

    private void EndInspection()
    {
        try { if (_handPose?.PresentationActive == true) _handPose.FinishPresentationAction(); }
        finally
        {
            _visualRig?.RestoreReadyGrip();
            _visualRig?.SetPresentationVisible(true);
            _inspect = InspectPhase.None;
            LogDiagnostic("DW_INSPECT step=complete paired=" + (_player != null));
        }
    }

    // Update: phase transitions only. Native completion = the utility operation was seen
    // and the controller has been plainly idle for two native frames.
    private void AdvanceInspection()
    {
        if (_primary == null) return;
        if (_inspect == InspectPhase.None)
        {
            // A two-handed check started by another native path: release the left arm too.
            if (_handPose != null && IsTwoHandedUtility(_primary))
            {
                _inspectSeen = true; _inspectStepDone = false;
                _handPose.CopyLeftPose(_holsterPose); _holsterPoseCaptured = true;
                StartNativeInspectionObserved();
            }
            return;
        }
        if (_inspect == InspectPhase.Holstering && (_inspectStepDone || Time.unscaledTime - _inspectSince > 5f)) StartNativeInspection();
        else if (_inspect == InspectPhase.Drawing && (_inspectStepDone || Time.unscaledTime - _drawStarted > 5f)) EndInspection();
        else if (_inspect == InspectPhase.Native)
        {
            bool utility = IsTwoHandedUtility(_primary);
            _inspectSeen |= utility;
            bool idle = !utility && _primary.CurrentOperation is Player.FirearmController.Idling;
            if (idle && (_inspectSeen || Time.unscaledTime - _inspectSince > .75f)) _inspectDone.Mark(Time.frameCount);
            else _inspectDone.Reset();
            if (_inspectDone.Ready(Time.frameCount) || Time.unscaledTime - _inspectSince > 10f) BeginInspectionDraw();
        }
    }

    private void StartNativeInspectionObserved()
    {
        _visualRig?.SetPresentationVisible(false);
        _inspect = InspectPhase.Native;
        _inspectSince = Time.unscaledTime;
        LogDiagnostic("DW_INSPECT step=native_observed");
    }

    // Left pistol holster/draw around a right-pistol native action (reload, checks), called
    // from AfterVisualPass after CaptureNativePose. It plays the authored lowering that carries
    // the right pistol out of view for left actions (R.* of dw_offhand_reload), mirrored onto
    // the left palm with the gun held by its grip; the native holster/draw clip only times the
    // transition. A draw starts where the native action left the left hand; during the native
    // check itself the left arm stays native.
    private void PresentLeftHolster(bool drawing)
    {
        var clip = _visualRig!.NativeClip;
        if (_offhandClip == null || clip == null)
        {
            PresentLeftHolsterRaw(drawing);
            return;
        }
        float t = Mathf.Clamp01(_handPose!.PresentationNormalizedTime);
        float frame = drawing
            ? Mathf.Lerp(_offhandClip.Marker("draw_start"), _offhandClip.LastFrame, t)
            : _offhandClip.Marker("holstered") * t;
        Quaternion look = _player != null ? _player.TargetHandsRotation : _handPose.PalmRotation;
        Quaternion inverseLook = Quaternion.Inverse(look);
        Vector3 offset = _offhandClip.Vector("R.pos", frame);
        Quaternion turn = _offhandClip.Rotation("R.rot", frame);
        Vector3 palm = _handPose.PalmPosition + look * new Vector3(-offset.x, offset.y, offset.z);
        Quaternion palmRotation = look * new Quaternion(turn.x, -turn.y, -turn.z, turn.w) * inverseLook * _handPose.PalmRotation;
        clip.Sample();
        clip.HoldIdle();
        _visualRig.RestoreReadyGrip();
        _visualRig.AlignToPalm(palm, palmRotation, default);
        Vector3 grip = _visualRig.LeftGripPosition;
        Quaternion gripRotation = _visualRig.LeftGripRotation;
        bool visible = _offhandClip.Step("R.vis", frame) >= .5f;
        if (drawing && _handoffClip != null)
        {
            float capture = _handoffClip.Marker("capture_start") + (Time.unscaledTime - _drawStarted) * _handoffClip.Fps;
            float weight = _handoffClip.Scalar("capture", capture);
            _handPose.BlendDrawFromNative(weight);
            grip = Vector3.Lerp(_handPose.PalmPosition, grip, weight);
            gripRotation = Quaternion.Slerp(_handPose.PalmRotation, gripRotation, weight);
            visible &= capture >= _handoffClip.Marker("gun_show");
        }
        _visualRig.SetPresentationVisible(visible);
        _handPose.ApplyGripTarget(grip, gripRotation);
    }

    // Fallback for pistols without EFT's IK marker hierarchy or without the authored clip.
    private void PresentLeftHolsterRaw(bool drawing)
    {
        if (drawing && _handoffClip != null)
        {
            float frame = _handoffClip.Marker("capture_start") + (Time.unscaledTime - _drawStarted) * _handoffClip.Fps;
            _handPose!.BlendDrawFromNative(_handoffClip.Scalar("capture", frame));
            _visualRig!.SetPresentationVisible(frame >= _handoffClip.Marker("gun_show"));
        }
        _visualRig!.UseNativeActionGrip();
        _visualRig.AlignToPalm(_handPose!.PalmPosition, _handPose.PalmRotation, default);
        _handPose.ApplyGripTarget(_visualRig.LeftGripPosition, _visualRig.LeftGripRotation);
    }

    private void ApplyInspection()
    {
        if (_handPose == null || _visualRig == null) return;
        if (_inspect == InspectPhase.Native)
        {
            if (_holsterPoseCaptured && _handoffClip != null)
            {
                float frame = (Time.unscaledTime - _inspectSince) * _handoffClip.Fps;
                if (frame < _handoffClip.Marker("release_end"))
                    _handPose.Arms.BlendLeftFrom(_holsterPose, _handoffClip.Scalar("release", frame));
            }
            _visualRig.SetPresentationVisible(false);
            return;
        }
        _handPose.ApplyRetarget();
        PresentLeftHolster(_inspect == InspectPhase.Drawing);
        if (_inspect == InspectPhase.Holstering) { _handPose.CopyLeftPose(_holsterPose); _holsterPoseCaptured = true; }
        if (_handPose.PresentationFinished || _handPose.PresentationFailed)
        {
            if (_inspect == InspectPhase.Holstering) _visualRig.SetPresentationVisible(false);
            _inspectStepDone = true;
        }
    }

    private static void AfterVisualPass(Player __instance)
    {
        var self = Instance;
        if (self == null) return;
        if (self._servicePlayer == __instance && self._reloadReturn.Waiting)
        {
            self._reloadReturn.ObserveNativeFrame(Time.frameCount, self.ReloadNativeReady(out _));
            return;
        }
        if (self._servicePlayer == __instance && (self._reloadService?.Active == true || self.ReloadVisualTransition))
        {
            self._handoff?.Apply();
            self.ApplyReloadPresentation();
            return;
        }
        if (self._player != __instance) return;
        string? stop = self.PairedStopReason();
        if (stop != null) { self.Unpair(stop); return; }
        try
        {
            self._handPose?.CaptureNativePose();
            self._shot?.PumpEvents();
            if (self._offhandAction != null) self._offhandAction.Apply(self._animation?.Sample() ?? default);
            else if (self._inspect != InspectPhase.None) self.ApplyInspection();
            else self.AlignModel();
            self._poseRecovery.ObserveValid();
            if (self._handPose?.NativeStateChanged == true && self._settings.Diagnostics.Value)
                self._poseReportPending = true;
            if (self._reloadReturn.Verifying)
                self._reloadReturn.ObserveNativeFrame(Time.frameCount, self.ReloadPairedReady(out _));
            // One diagnostic snapshot at settled ready/lowered transitions,
            // rather than measuring a transient first frame of the draw.
            int phase = self._animation == null || self._animation.EnterBlend < .999f ? -1
                : self._animation.LowerBlend > .999f ? 2
                : self._animation.LowerBlend < .001f ? (self._dualAiming ? 1 : 0) : -1;
            if (phase >= 0 && phase != self._reportedVisualPhase)
            { self._reportedVisualPhase = phase; self._poseReportPending = true; }
        }
        catch (NativePoseUnavailableException e)
        {
            // Capture invalidates the snapshot before throwing, so neither the
            // visual override nor a shot may reuse partially evaluated geometry.
            self._handPose?.BeforeNativePose();
            self.StopOffhandTrigger();
            PresentationRecoveryDecision decision = self._poseRecovery.ObserveUnavailable(Time.frameCount);
            if (decision == PresentationRecoveryDecision.Reject)
            {
                self.Logger.LogError("DW_POSE_REJECT frame=" + Time.frameCount + " invariant=" + e.Message);
                self.Unpair("Native pose remained unavailable for 120 evaluated frames.");
            }
            else if (decision == PresentationRecoveryDecision.Report)
                self.Logger.LogWarning("DW_POSE_DEFER frame=" + Time.frameCount + " invariant=" + e.Message);
        }
        catch (Exception e)
        {
            self.Unpair("First-person presentation failed; pair removed safely.");
            self.Logger.LogError(e);
        }
    }

    private static void BeforeVisualPass(Player __instance)
        => BeforeNativeFrame(__instance);

    private static void BeforeNativeFrame(Player __instance)
    {
        var self = Instance;
        if (self == null) return;
        if (self._player == __instance)
        {
            self._offhandAction?.BeforeNativePose();
            self._visualRig?.NativeClip?.BeforeNativePose();
            self._handPose?.BeforeNativePose();
        }
        if (self._servicePlayer == __instance) self._handoff?.BeforeNativePose();
    }

    private void AlignModel()
    {
        PistolPoseSample pose = _animation?.Sample() ?? default;
        if (_handPose?.HasSnapshot == true && _visualRig != null)
        {
            _handPose.ApplyRetarget();
            _visualRig.AlignToPalm(_handPose.PalmPosition, _handPose.PalmRotation, pose);
            if (_visualRig.GripCorrectionChanged)
            { _visualRig.GripCorrectionChanged = false; LogDiagnostic("DW_GRIP settled " + _visualRig.GripCorrectionSummary); }
            if (_handPose.Arms.HeldTargetRejections != _reportedHoldRejections)
            {
                _reportedHoldRejections = _handPose.Arms.HeldTargetRejections;
                LogDiagnostic("DW_HOLD_REJECTED count=" + _reportedHoldRejections + " right_operation=" + _primary?.CurrentHandsOperationName);
            }
            // Native draw/holster: the left gun follows the right gun's own animation, mirrored,
            // and the left palm is put on it (see OffhandVisualRig.AlignToMirroredGun).
            Vector3 shoulders = (_handPose.Arms.LeftUpperArm.position + _handPose.Arms.RightUpperArm.position) * .5f;
            Quaternion look = _player != null ? _player.TargetHandsRotation : Quaternion.identity;
            float mirror = _player != null ? _transition.MirrorPlacementWeight(_visualRig) : 0f;
            _visualRig.AlignToMirroredGun(shoulders, look, mirror);
            _handPose.ApplyGripTarget(_visualRig.LeftGripPosition, _visualRig.LeftGripRotation);
            // A native selector or malfunction clip moves the gun; the left palm stays on it.
            var clip = _visualRig.NativeClip;
            bool nativeLeftAction = _handPose.OffhandSelectorActive || _shot?.NativeActionActive == true;
            if (clip != null && nativeLeftAction)
            {
                clip.Sample();
                clip.FollowGun(_handPose.PalmPosition, _handPose.PalmRotation, out Vector3 grip, out Quaternion gripRotation);
                _handPose.ApplyGripTarget(grip, gripRotation);
            }
            if (mirror <= 0f && !nativeLeftAction && pose.Recoil < .01f && pose.EnterBlend >= .999f && _player != null)
                _visualRig.RecordMirrorOffset(shoulders, look);
            return;
        }
        Vector3 stance = Vector3.Lerp(HipStanceOffset, AimStanceOffset, pose.AimBlend);
        _visualRig?.Align(stance, _animationProfile.GripSpacingScale);
    }
}
