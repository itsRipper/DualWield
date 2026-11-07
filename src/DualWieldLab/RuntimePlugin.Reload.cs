using System;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

public sealed partial class RuntimePlugin
{
    // Native operations on the left pistol end in Idling.Start, which would write the held
    // weapon's shared procedural animation; the offhand context only records the state.
    private static bool BeforeIdlingStart(Player.FirearmController.Idling __instance, Action callback)
    {
        if (!(__instance.Controller is OffhandShotContext context)) return true;
        context.ParkIdle(__instance, callback);
        return false;
    }

    private string ServiceHint()
    {
        string modifier = _settings.LeftModifier.Value == KeyCode.None ? "the left-hand modifier" : _settings.LeftModifier.Value.ToString();
        return "hold " + modifier + " and press EFT's examine (L) to identify it, then its chamber check (Shift+T) to clear it.";
    }

    // Left examine / chamber check (clears an identified malfunction) / magazine check. The
    // native operation runs on the left pistol's own context; the right pistol is lowered
    // only to give the native clip its second hand.
    private void BeginOffhandService(OffhandShotContext.NativeCheck check)
    {
        if (_player == null || _primary == null || _secondary == null || _shot == null || _prefab == null || _input == null) return;
        if (ReloadBusy || _shot.NativeActionActive) { Notify("Finish the current action first."); return; }
        if (!_ownerInputAdmitted || !PairedInput.AllowsGameplay(_input.Owner) || _player.IsSprintEnabled
            || _primary.CurrentOperation is not Player.FirearmController.Idling || IsTwoHandedUtility(_primary)
            || _primary.IsTriggerPressed || _trigger.Holding || _shot.IsTriggerPressed)
        { Notify("Release the triggers and wait until both pistols are ready."); return; }
        var action = OffhandNativeAction.TryCreate(check, _offhandClip, _serviceClip, _player, _primary, _prefab, _handPose, _visualRig, out string reason);
        if (action == null)
        {
            LogDiagnostic("DW_LEFT_SERVICE check=" + check + " started=False reason=" + reason);
            LogDiagnostic("DW_NOTICE left action refused: " + reason);
            Notify("That action is not available for the left pistol right now.");
            return;
        }
        StopPrimaryTrigger(); StopOffhandTrigger();
        _offhandAction = action;
        _primary.WeaponSoundPlayer?.SoundEventHandler("SndHolster");
        LogDiagnostic("DW_LEFT_SERVICE step=holster check=" + check + " malfunction=" + _secondary.MalfState.State);
    }

    // Update: phase transitions only; the pose is applied in the native visual pass.
    private void AdvanceOffhandService()
    {
        if (_shot == null) return;
        _handPose?.OwnNativeAction(_shot.NativeActionActive);
        var action = _offhandAction;
        if (action == null)
        {
            // The automatic native malfunction start after a shot needs no second hand.
            if (_shot.NativeActionActive && _shot.NativeActionSeconds > 8f) FinishStuckNativeAction();
            return;
        }
        if (action.State == OffhandNativeAction.Phase.AwaitingNative)
        {
            OffhandShotContext.NativeCheckResult result;
            try { result = action.Check == OffhandShotContext.NativeCheck.Reload ? BeginNativeOffhandReload() : _shot.TryNativeCheck(action.Check); }
            catch (Exception e)
            {
                Logger.LogError("Native left " + action.Check + " failed: " + e);
                result = OffhandShotContext.NativeCheckResult.Refused;
            }
            bool started = result == OffhandShotContext.NativeCheckResult.Started;
            _handPose?.OwnNativeAction(started);
            action.NativeStarted(started);
            if (started) PublishOffhandAction(action.Check switch
            {
                OffhandShotContext.NativeCheck.Reload => OffhandAction.Reload,
                OffhandShotContext.NativeCheck.Magazine => OffhandAction.MagazineCheck,
                OffhandShotContext.NativeCheck.Chamber => OffhandAction.ChamberCheck,
                OffhandShotContext.NativeCheck.FireMode => OffhandAction.FireModeCheck,
                _ => OffhandAction.Examine,
            });
            LogDiagnostic("DW_LEFT_SERVICE step=native check=" + action.Check + " result=" + result
                + " operation=" + _shot.NativeOperationName + " malfunction=" + _secondary?.MalfState.State);
            if (!started && action.Check != OffhandShotContext.NativeCheck.Reload) _primary?.WeaponSoundPlayer?.SoundEventHandler("SndDraw");
            return;
        }
        var before = action.State;
        action.Advance(Time.deltaTime, _shot.NativeActionActive);
        if (action.State == OffhandNativeAction.Phase.Native && _shot.NativeActionSeconds > 15f) FinishStuckNativeAction();
        if (before != OffhandNativeAction.Phase.Drawing && action.State == OffhandNativeAction.Phase.Drawing && before != OffhandNativeAction.Phase.AwaitingNative)
            _primary?.WeaponSoundPlayer?.SoundEventHandler("SndDraw");
        if (action.State == OffhandNativeAction.Phase.Finished)
        {
            ReleaseOffhandService();
            _handPose?.RefreshMechanicalState();
            LogDiagnostic("DW_LEFT_SERVICE step=complete paired=" + (_player != null) + " malfunction=" + _secondary?.MalfState.State
                + " chamber=" + _secondary?.ChamberAmmoCount + " magazine=" + _secondary?.GetCurrentMagazineCount());
        }
    }

    private void FinishStuckNativeAction()
    {
        if (_shot == null) return;
        string operation = _shot.NativeOperationName;
        try { _shot.FinishNativeAction(); }
        catch (Exception e) { Logger.LogError("Native left operation completion failed: " + e); }
        LogDiagnostic("DW_LEFT_SERVICE step=native_timeout operation=" + operation + " completed_natively=True malfunction=" + _secondary?.MalfState.State);
    }

    private void ReleaseOffhandService()
    {
        var action = _offhandAction;
        _offhandAction = null;
        try { action?.Restore(); }
        catch (Exception e) { Logger.LogWarning("Left service presentation restore failed: " + e.Message); }
        if (action != null) _visualRig?.RestoreReadyGrip();
    }

    private void BeginReload(PistolHand hand)
    {
        if (ReloadBusy || _player == null || _primary == null || _secondary == null || _input == null) return;
        // The managed reload paths service an operable pistol; malfunctions are cleared natively.
        Weapon reloaded = hand == PistolHand.Offhand ? _secondary : _primary.Item;
        if (reloaded.MalfState.State != Weapon.EMalfunctionState.None || _shot?.NativeActionActive == true)
        {
            Notify(hand == PistolHand.Offhand ? "Clear the left malfunction first: " + ServiceHint()
                : "Clear the right malfunction first: press EFT's examine (L), then its chamber check (Shift+T).");
            return;
        }
        if (hand == PistolHand.Offhand)
        {
            BeginOffhandMagazineReload();
            return;
        }
        if (!_ownerInputAdmitted || !PairedInput.AllowsGameplay(_input.Owner) || _player.IsSprintEnabled
            || _primary.CurrentOperation is not Player.FirearmController.Idling || _primary.IsTriggerPressed
            || _primary.CurrentOperation is Player.FirearmController.UtilityOperation)
        { Notify("Release the triggers and wait until both pistols are ready."); return; }
        StopPrimaryTrigger(); StopOffhandTrigger();
        if (!TryBeginReloadPresentation(draw: false))
        { SetStatus("Native offhand holster pose unavailable; reload not started and both items unchanged."); Notify("The reload is not available right now."); return; }
        _shot?.PlayHandlingSound("SndHolster");
        _servicePlayer = _player;
        _serviceOwner = _input.Owner;
        _returnMain = _primary.Item; _returnOffhand = _secondary;
        _reloadMainAddress = _returnMain.CurrentAddress;
        _reloadOffhandAddress = _returnOffhand.CurrentAddress;
        LogDiagnostic("DW_RELOAD_PRESENTATION step=holster_started weapon_moves=0"
            + " main=" + _returnMain.Id + " offhand=" + _returnOffhand.Id
            + " main_address=" + NativeReloadService.DescribeAddress(_reloadMainAddress)
            + " offhand_address=" + NativeReloadService.DescribeAddress(_reloadOffhandAddress));
        _reloadInputAdmitted = _ownerInputAdmitted; _returnReloadVerified = false;
        _preparingReload = hand;
        _preparePresented.Reset();
        SetStatus("Holstering left pistol visually for the native right reload.");
    }

    private void BeginOffhandMagazineReload()
    {
        if (_player == null || _primary == null || _secondary == null || _input == null || _shot == null
            || !_ownerInputAdmitted || !PairedInput.AllowsGameplay(_input.Owner)
            || _primary.CurrentOperation is not Player.FirearmController.Idling || _primary.IsTriggerPressed
            || _primary.CurrentOperation is Player.FirearmController.UtilityOperation) return;
        // Refuse before any animation when the native swap could not be submitted.
        string? refusal = InactiveMagazineReload.Preflight(_player, _primary, _secondary, out bool chamberNeeded, out Magazine? next);
        if (refusal != null)
        {
            LogDiagnostic("DW_OFFHAND_RELOAD step=preflight_refused weapon_moves=0 reason=" + refusal);
            LogDiagnostic("DW_NOTICE left reload refused: " + refusal);
            Notify(refusal == InactiveMagazineReload.NoSpareMagazine ? refusal : "The left pistol cannot be reloaded right now.");
            return;
        }
        StopPrimaryTrigger(); StopOffhandTrigger();
        _servicePlayer = _player; _serviceOwner = _input.Owner;
        _returnMain = _primary.Item; _returnOffhand = _secondary;
        _reloadMainAddress = _returnMain.CurrentAddress; _reloadOffhandAddress = _secondary.CurrentAddress;
        _returnReloadVerified = false;
        _offhandReload = new InactiveMagazineReload();
        _offhandReloadNext = next;
        string reason = "offhand prefab unavailable";
        var action = _prefab == null ? null : OffhandNativeAction.TryCreate(OffhandShotContext.NativeCheck.Reload,
            _offhandClip, _serviceClip, _player, _primary, _prefab, _handPose, _visualRig, out reason);
        if (action != null)
        {
            _offhandAction = action;
            _nativeReload = new OffhandNativeReload(action);
            _offhandReloadPresented = true;
            _offhandMechanicsSynced = _offhandReloadSprinting = false;
            _offhandReload.UseChamberGate();
            _primary.WeaponSoundPlayer?.SoundEventHandler("SndHolster");
            LogDiagnostic("DW_OFFHAND_RELOAD step=presentation_started native_clip=True chamber_needed=" + chamberNeeded
                + " next_mag=" + next?.Id + " weapon_moves=0");
            SetStatus("Left reload: right pistol holstered; the right hand supports EFT's native left reload.");
            return;
        }
        _offhandReloadPresented = false;
        LogDiagnostic("DW_OFFHAND_RELOAD step=presentation_unavailable reason=" + reason);
        SetStatus("Native left inventory reload; both triggers suspended until completion.");
        _offhandReload.Begin(_player, _primary, _secondary, LogDiagnostic);
    }

    // Starts the left pistol's own native reload clip once the right hand holds its support grip.
    private OffhandShotContext.NativeCheckResult BeginNativeOffhandReload()
    {
        if (_shot == null || _secondary == null || _nativeReload == null || _offhandReloadNext == null || _offhandReload == null
            || _offhandReload.Interrupted)
            return OffhandShotContext.NativeCheckResult.Refused;
        _handPose?.RefreshMechanicalState();
        return _shot.BeginNativeReload(_nativeReload, _secondary.GetCurrentMagazine(), _offhandReloadNext)
            ? OffhandShotContext.NativeCheckResult.Started : OffhandShotContext.NativeCheckResult.Refused;
    }

    // Update: ties the backpack-free native magazine transaction to the native clip events.
    // Sprinting does not pause it: like EFT's own reloads, the clip, its magazine events and the
    // transactions continue while the arms take the native sprint pose.
    private void AdvanceOffhandReload()
    {
        var events = _nativeReload;
        var reload = _offhandReload;
        if (events == null) return;
        if (reload == null || _player == null || _primary == null || _secondary == null || _shot == null)
        { ReleaseNativeReload(); return; }
        var action = _offhandAction;
        if (!reload.Started && !_ownerInputAdmitted)
        { CancelReload("Left reload interrupted before the native magazine swap; nothing submitted."); return; }
        bool sprinting = _player.IsSprintEnabled;
        if (sprinting != _offhandReloadSprinting)
        {
            _offhandReloadSprinting = sprinting;
            LogDiagnostic("DW_OFFHAND_RELOAD step=" + (sprinting ? "sprint_started" : "sprint_stopped") + " submitted=" + reload.Started);
        }
        // A clip that never reaches its first magazine event is ended natively at once.
        if (_shot.NativeReloadActive && !events.MagOutSeen && _shot.NativeActionSeconds > 4f) FinishStuckNativeAction();
        bool clipRunning = _shot.NativeReloadActive;
        // A clip that never reached its magazine events (refused, or ended) still settles the
        // transaction: the swap and feed run without waiting for an event that cannot come.
        bool clipGone = action == null || (action.State != OffhandNativeAction.Phase.Holstering
            && action.State != OffhandNativeAction.Phase.Reaching && action.State != OffhandNativeAction.Phase.AwaitingNative && !clipRunning);
        if (!reload.Started && !reload.Interrupted && (events.MagOutSeen || clipGone))
        {
            LogDiagnostic("DW_OFFHAND_RELOAD step=mag_out native_event=" + events.MagOutSeen);
            reload.Begin(_player, _primary, _secondary, LogDiagnostic);
        }
        if (reload.SwapSettled && !_offhandMechanicsSynced && events.MagInSeen)
        {
            _offhandMechanicsSynced = true;
            _shot.SetNativeMagazineState(true, _secondary.GetCurrentMagazine());
        }
        if (reload.AwaitingChamberRelease && (events.ChamberEvent || events.Ended || clipGone))
        {
            LogDiagnostic("DW_OFFHAND_RELOAD step=chamber native_event=" + events.ChamberEvent);
            reload.ReleaseChamber();
        }
        if (reload.Finished) _shot.SetNativeChamberState();
        // The clip waits at the new magazine's appearance until the swap settled.
        if (_handPose != null)
            _handPose.HoldNativeAnimation = clipRunning && ((events.MagShown && !reload.SwapSettled)
                || (events.ChamberEvent && reload.ChamberNeeded && reload.MagazineVerified && !reload.Finished));
    }

    // An interrupted or settled left reload: the native clip is ended natively, the right
    // pistol carry and the left magazine renderers are restored.
    private void EndOffhandReloadPresentation()
    {
        if (_nativeReload != null && _shot?.NativeReloadActive == true)
        {
            try { _shot.StopHandlingSounds(); _shot.FinishNativeAction(); }
            catch (Exception e) { Logger.LogWarning("Ending the native left reload clip failed: " + e.Message); }
        }
        bool owned = _offhandAction?.Check == OffhandShotContext.NativeCheck.Reload;
        ReleaseNativeReload();
        if (owned) ReleaseOffhandService();
    }

    private void ReleaseNativeReload()
    {
        _nativeReload = null;
        _offhandReloadNext = null;
        if (_handPose != null) _handPose.HoldNativeAnimation = false;
    }

    // The new magazine needs a freshly assembled offhand view. Rebuilding inside one
    // Update keeps the left pistol in hand: no unpaired frame is ever presented.
    private void RefreshPairInPlace(Player player, Weapon main, Weapon offhand)
    {
        Unpair("Native left magazine settled; refreshing the offhand view in place.");
        CancelReload("Left reload presentation complete");
        TryPair(offhand);
        bool paired = _player == player && _primary?.Item == main && _secondary == offhand;
        if (paired) _animation?.Reset(false);
        LogDiagnostic("DW_RELOAD_COMPLETE usable=" + paired + " native_reload_verified=True main=" + main.Id
            + " offhand=" + offhand.Id + " in_place=True weapon_moves=0");
        if (paired) SetStatus("Left reload complete; both pistols ready.");
    }

    private void ObserveOffhandMagazineReload()
    {
        var service = _offhandReload;
        if (service == null) return;
        // Never submitted: nothing native to settle. Otherwise the draw plays out first.
        if (!service.Finished && !(service.Interrupted && !service.Started)) return;
        if (service.Finished && !service.Interrupted && _offhandAction?.Check == OffhandShotContext.NativeCheck.Reload && _player != null) return;
        // The magazine and chamber are settled; only the view refresh is left. Pairing is refused
        // in the sprint pose, so a reload finished mid-sprint keeps the pair (and the reload busy
        // state that blocks firing) until the sprint ends, then refreshes in place.
        if (service.Verified && !service.Interrupted && _player != null && _player.IsSprintEnabled)
        {
            if (!_offhandReturnWaitsForSprint) LogDiagnostic("DW_OFFHAND_RELOAD step=return_waits_for_sprint native_verified=True");
            _offhandReturnWaitsForSprint = true;
            return;
        }
        _offhandReturnWaitsForSprint = false;
        bool presented = _offhandReloadPresented;
        _offhandReloadPresented = false;
        EndOffhandReloadPresentation();
        _offhandReload = null;
        if (service.CompletionUncertain) _faulted = true;
        SetStatus(service.Message + (service.Interrupted ? " Interrupted; automatic pair restoration was not resumed." : ""));
        Player? player = _servicePlayer;
        Weapon? main = _returnMain, offhand = _returnOffhand;
        bool restore = _settings.Enabled.Value && service.Verified && !service.Interrupted && _player == player
            && player != null && _primary != null && player.HandsController == _primary
            && main != null && offhand != null && ReloadAddressesMatch()
            && PairedInput.AllowsGameplay(_serviceOwner);
        LogDiagnostic("DW_OFFHAND_RELOAD step=return queued=" + restore + " native_verified=" + service.Verified
            + " interrupted=" + service.Interrupted + " weapon_moves=0"
            + (restore ? "" : " paired=" + (_player == player && player != null) + " hands=" + (player != null && _primary != null && player.HandsController == _primary)
                + " addresses=" + ReloadAddressesMatch() + " gameplay=" + PairedInput.AllowsGameplay(_serviceOwner)));
        if (restore && presented && service.Submitted)
        { RefreshPairInPlace(player!, main!, offhand!); return; }
        if (service.Submitted && _player != null)
            Unpair(service.CompletionUncertain ? "Native inventory completion unknown; restart before pairing."
                : "Native inactive-magazine transaction settled; release the cached offhand magazine view.");
        if (restore) ResumeReloadPair(player!, main!, offhand!, true);
        else CancelReload(service.Message);
    }

    private void StartPreparedReload()
    {
        if (!_preparingReload.HasValue || _player == null || _primary == null || _secondary == null || _input == null) return;
        PistolHand hand = _preparingReload.Value;
        Player player = _player; PlayerOwner owner = _input.Owner;
        Weapon main = _primary.Item, offhand = _secondary;
        _preparingReload = null;
        ReleaseReloadPresentation();
        Unpair("Native reload in progress; dual stance restores after authoritative completion.");
        _handoff?.Restore();
        _handoff = _holsterPoseCaptured ? LeftArmHandoff.TryCreate(player, _holsterPose, _handoffClip) : null;
        _servicePlayer = player; _serviceOwner = owner;
        _reloadService = new NativeReloadService();
        if (!_reloadService.Begin(player, owner, main, offhand, hand,
            (original, second) => ResumeReloadPair(player, original, second), SetStatus, LogDiagnostic))
            ResumeReloadPair(player, main, offhand);
    }

    private void ResumeReloadPair(Player player, Weapon main, Weapon offhand, bool? verified = null)
    {
        ReleaseReloadPresentation();
        _handoff?.Restore(); _handoff = null;
        _returnReloadVerified = verified ?? _reloadService?.ReloadWasVerified == true;
        _reloadService = null;
        _servicePlayer = player;
        _serviceOwner = player != null ? player.GetComponent<PlayerOwner>() : null;
        _returnMain = main; _returnOffhand = offhand;
        _reloadInputAdmitted = false; _returnStarted = Time.unscaledTime; _returnWaitReason = "";
        _reloadReturn.Begin();
        SetStatus("Native placement finished; waiting for the original hands and input before restoring dual wield.");
    }

    private void BindReloadPair()
    {
        Player player = _servicePlayer!;
        Weapon main = _returnMain!, offhand = _returnOffhand!;
        int version = _reloadReturn.Version;
        TryPair(offhand);
        bool paired = _player == player && _primary?.Item == main && _secondary == offhand
            && _input != null && ReferenceEquals(_input.Translator._controller, _primary);
        LogDiagnostic("DW_RELOAD_PAIR provisional=" + paired + " main=" + main.Id + " offhand=" + offhand.Id
            + " main_mag=" + main.GetCurrentMagazine()?.Id + " offhand_mag=" + offhand.GetCurrentMagazine()?.Id);
        if (paired && _reloadReturn.BeginRecovery(version))
        {
            if (!TryBeginReloadPresentation(draw: true))
            { CancelReload("Native offhand draw pose unavailable; paired idle restored without claiming reload presentation success."); return; }
            _animation?.Reset(false);
            _drawStarted = Time.unscaledTime;
            _shot?.PlayHandlingSound("SndDraw");
            _ownerInputAdmitted = _reloadInputAdmitted;
            _servicePlayer = player; _serviceOwner = _input!.Owner;
            _recoverPresented.Reset();
        }
        else
        {
            CancelReload("Original pair could not bind once native readiness was established; no automatic retry.");
            if (_player != null) Unpair("Reload pair binding failed");
        }
    }

    private bool ReloadNativeReady(out string reason)
    {
        reason = "";
        if (_servicePlayer == null || _returnMain == null || _returnOffhand == null || _serviceOwner == null)
        { reason = "restoration context unavailable"; return false; }
        if (!_servicePlayer.HealthController.IsAlive || !_servicePlayer.FirstPersonPointOfView || _servicePlayer.IsInventoryOpened)
        { reason = "player/UI unavailable"; return false; }
        if (!Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer != _servicePlayer)
        { reason = "world changed"; return false; }
        if (_servicePlayer is HideoutPlayer)
        {
            string? rangeReason = HideoutRangeContext.Refusal(_servicePlayer, _serviceOwner);
            if (rangeReason != null) { reason = rangeReason; return false; }
        }
        if (!ReferenceEquals(_returnMain.Owner, _servicePlayer.InventoryController)
            || !ReferenceEquals(_returnOffhand.Owner, _servicePlayer.InventoryController))
        { reason = "weapon ownership changed"; return false; }
        if (!ReloadAddressesMatch())
        { reason = "original pistol addresses changed during reload presentation"; return false; }
        if (!(_servicePlayer.HandsController is Player.FirearmController controller) || controller.Item != _returnMain
            || controller.Destroyed || controller.CurrentOperation is not Player.FirearmController.Idling)
        { reason = "original native controller is not idle"; return false; }
        if (_servicePlayer.InventoryController.IsChangingWeapon || _servicePlayer.ProcessStatus != Player.EProcessStatus.None
            || _servicePlayer.ScheduledProcess != null || _servicePlayer.CustomAnimationsAreProcessing
            || _servicePlayer.IsSprintEnabled || controller.IsInInteraction() || !controller.SuitableForHandInput)
        { reason = "native hands process is still changing"; return false; }
        if (!(_serviceOwner.HandsInputTranslator is FirearmHandsInputTranslator translator)
            || !ReferenceEquals(translator._controller, controller) || translator._player != _servicePlayer)
        { reason = "native firearm input translator has not rebound"; return false; }
        if (!_reloadInputAdmitted || !PairedInput.AllowsGameplay(_serviceOwner))
        { reason = "native gameplay input is not admitted"; return false; }
        return true;
    }

    private void CancelReload(string reason)
    {
        _offhandReload?.Interrupt(reason);
        ReleaseReloadPresentation();
        _handoff?.Restore(); _handoff = null;
        if (ReloadBusy)
            LogDiagnostic("DW_RELOAD_COMPLETE usable=False native_reload_verified="
                + (_returnReloadVerified || _reloadService?.NativeReloadVerified == true) + " reason=" + reason);
        _preparingReload = null; _reloadReturn.Cancel();
        _preparePresented.Reset(); _recoverPresented.Reset();
        _reloadService?.Cancel(reason);
        _reloadService = null;
        _servicePlayer = null;
        _serviceOwner = null;
        _returnMain = _returnOffhand = null; _reloadInputAdmitted = false;
        _reloadMainAddress = _reloadOffhandAddress = null;
    }

    private bool ReloadAddressesMatch() => _returnMain != null && _returnOffhand != null
        && NativeReloadService.SameAddress(_returnMain.CurrentAddress, _reloadMainAddress)
        && NativeReloadService.SameAddress(_returnOffhand.CurrentAddress, _reloadOffhandAddress);

    private bool ReloadPairedReady(out string reason)
    {
        if (!ReloadNativeReady(out reason)) return false;
        if (_player != _servicePlayer || _primary?.Item != _returnMain || _secondary != _returnOffhand
            || _input == null || !ReferenceEquals(_input.Owner.HandsInputTranslator, _input.Translator)
            || !ReferenceEquals(_input.Translator._controller, _primary) || !_ownerInputAdmitted)
        { reason = "paired input/controller identity not ready"; return false; }
        if (_shot == null || _shot.Item != _returnOffhand || _visualRig == null || !_visualRig.HasVisibleGeometry || _model == null || !_model.activeInHierarchy
            || _animation == null || _animation.EnterBlend < .999f || _animation.LowerBlend > .001f
            || _handPose?.HasSnapshot != true || !_handPose.PairedIdleReady)
        { reason = "normal dual presentation has not settled"; return false; }
        string? stop = PairedStopReason();
        if (stop != null) { reason = stop; return false; }
        return true;
    }

    // DW_* detail lines: written only while Diagnostics/Enabled is on.
    private void LogDiagnostic(string message)
    {
        if (_settings.Diagnostics.Value) Logger.LogInfo(message);
    }

    private void ReleaseReloadPresentation()
    {
        try
        {
            if (_handPose?.PresentationActive == true)
            {
                if (_handPose.PresentationFinished) _handPose.FinishPresentationAction();
                else _handPose.CancelPresentationAction();
            }
        }
        catch (Exception e) { Logger.LogWarning("Owned reload presentation restore failed: " + e.Message); }
        finally
        {
            _visualRig?.RestoreReadyGrip();
            _visualRig?.SetPresentationVisible(true);
        }
    }

    private bool TryBeginReloadPresentation(bool draw)
    {
        float rate = _handoffClip?.Constant(draw ? "draw_rate" : "holster_rate", 1f) ?? 1f;
        if (!draw) _holsterPoseCaptured = false;
        try { return _handPose?.BeginPresentationAction(draw, rate, blendFromNative: draw && _handoffClip != null) == true; }
        catch (Exception e)
        {
            Logger.LogError("Native cosmetic reload action failed before an inventory operation: " + e);
            CancelReload("Native cosmetic holster/draw could not bind.");
            if (_player != null) Unpair("Native cosmetic action dependency unavailable");
            return false;
        }
    }

    private void ApplyReloadPresentation()
    {
        // During the actual reload the paired display has been released and EFT
        // alone owns both hands and the magazine. Only the cosmetic native
        // holster/draw is retargeted to the otherwise inactive left hand.
        if (!ReloadVisualTransition || _handPose == null || _visualRig == null) return;
        try
        {
            _handPose.CaptureNativePose();
            _handPose.ApplyRetarget();
            PresentLeftHolster(_recoveringReload);
            if (_preparingReload.HasValue) { _handPose.CopyLeftPose(_holsterPose); _holsterPoseCaptured = true; }
            _poseRecovery.ObserveValid();
            if (_handPose.PresentationFailed)
            { CancelReload("Native holster/draw did not reach its terminal state; no presentation success claimed."); return; }
            if (_handPose.NativeStateChanged && _settings.Diagnostics.Value) _poseReportPending = true;
            if (!_handPose.PresentationFinished) return;
            if (_preparingReload.HasValue)
            { _visualRig.SetPresentationVisible(false); _preparePresented.Mark(Time.frameCount); }
            if (_recoveringReload) _recoverPresented.Mark(Time.frameCount);
        }
        catch (NativePoseUnavailableException e)
        {
            StopOffhandTrigger();
            var decision = _poseRecovery.ObserveUnavailable(Time.frameCount);
            if (decision == PresentationRecoveryDecision.Reject) CancelReload("Native reload presentation pose remained unavailable.");
            else if (decision == PresentationRecoveryDecision.Report) Logger.LogWarning("DW_POSE_DEFER reload=True invariant=" + e.Message);
        }
        catch (Exception e)
        {
            CancelReload("Native reload presentation failed; native inventory retained: " + e.Message);
            if (_player != null) Unpair("Unsafe native reload presentation failure");
            Logger.LogError(e);
        }
    }
}
