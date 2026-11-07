using System;
using System.Diagnostics;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Animations.NewRecoil;
using EFT.InputSystem;
using UnityEngine;

namespace DualWieldLab;

public sealed partial class RuntimePlugin
{
    private static bool BeforeToggleAim(Player.FirearmController __instance)
    {
        var self = Instance;
        if (self == null || self._primary != __instance || self._player == null) return true;
        return false;
    }

    private static bool BeforeSetAim(Player.FirearmController __instance, bool value)
    {
        var self = Instance;
        if (self == null || self._primary != __instance || self._player == null) return true;
        return false;
    }

    private static bool BeforeHandsCommand(FirearmHandsInputTranslator __instance, ECommand command, ref InputNode.ETranslateResult __result)
    {
        var self = Instance;
        if (self == null || self._primary == null || self._player != __instance._player
            || !ReferenceEquals(self._primary, __instance._controller)) return true;
        bool allowed = self._settings.Enabled.Value && !self.ReloadBusy && self._ownerInputAdmitted && PairedInput.AllowsGameplay(self._input?.Owner);
        self.UpdatePrimaryTriggerSafety(allowed);
        // A double-tapped R (native quick reload) is the same paired right reload; reaching the
        // native translator it would start an unmanaged reload and end the pair.
        if (command == ECommand.ReloadWeapon || command == ECommand.QuickReloadWeapon)
        {
            if (allowed) self.BeginReload(PistolHand.MainHand);
            __result = InputNode.ETranslateResult.Block;
            return false;
        }
        if (IsTwoHandedCheck(command))
        {
            // The native check needs the left hand: holster the left pistol first.
            if (allowed) self.BeginInspection(command);
            __result = InputNode.ETranslateResult.Block;
            return false;
        }
        // Native firing remains authoritative. A held resume requires release.
        if (command == ECommand.ToggleShooting && (!allowed || self._primaryRequiresRelease || !(self._input?.IsShootHeld() ?? false)))
        { __result = InputNode.ETranslateResult.Block; return false; }
        if (command != ECommand.ToggleAlternativeShooting && command != ECommand.EndAlternativeShooting) return true;
        __result = InputNode.ETranslateResult.Block;
        if (!allowed) self.StopOffhandTrigger();
        if (command == ECommand.EndAlternativeShooting)
        {
            if (self._input?.IsAimHeld() ?? true) self.StopOffhandTrigger();
            else { self._trigger.Observe(allowed, false); self._shot?.ReleaseTrigger(); }
            return false;
        }
        if (command == ECommand.ToggleAlternativeShooting && self._secondary != null && self._trigger.ClaimNative(Time.frameCount, allowed,
                self._input?.IsAimHeld() ?? false, WeaponCompatibility.GetTriggerMode(self._secondary.SelectedFireMode))) self.TryFire();
        return false;
    }

    // EFT's right fire-mode clip plays its selector click about 0.38 s in.
    private const float SelectorSoundDelay = .38f;
    private float _selectorSoundAt = -1f;

    private void PlayPendingSelectorSound()
    {
        if (_selectorSoundAt < 0f || Time.unscaledTime < _selectorSoundAt) return;
        _selectorSoundAt = -1f;
        if (_shot != null && !_shot.PlayHandlingSound("Selector")) LogDiagnostic("DW_SOUND left pistol has no Selector sound");
    }

    // A right shot pushes EFT's camera recoil through the shared shot effect, which also kicks the
    // right hands. A left shot pushes only that effect's camera-rotation channel, in the direction
    // EFT computes for a shot, scaled by the left pistol's recoil relative to the right one.
    private void ApplyLeftCameraRecoil()
    {
        try
        {
            if (_player == null || _primary == null || _secondary == null
                || _player.ProceduralWeaponAnimation?.Shootingg?.CurrentRecoilEffect is not NewRecoilShotEffect effect) return;
            effect.CalculateShotRecoilPower(1f, out float rotationPower, out float positionPower);
            effect.CalculateShotRecoilRadian(out Vector2 radian);
            Vector3 native = effect.RecoilDirection;
            effect.CalculateRecoilDirection(radian, rotationPower, positionPower);
            Vector3 direction = effect.RecoilDirection * RecoilRatio(_secondary, _primary.Item);
            effect.RecoilDirection = native;
            foreach (var value in effect.ShotRecoilProcessValues)
                if (value.ProcessType == Target.CameraRotation) value.Process(direction);
        }
        catch (Exception e) { LogDiagnostic("DW_CAMERA_RECOIL skipped: " + e.Message); }
    }

    private static float RecoilRatio(Weapon left, Weapon right)
    {
        float l = left.Template.RecoilForceUp * Mathf.Max(0f, 1f + left.RecoilDelta);
        float r = right.Template.RecoilForceUp * Mathf.Max(0f, 1f + right.RecoilDelta);
        return r > 0f ? Mathf.Clamp(l / r, 0f, 3f) : 1f;
    }

    private void OnPrimaryShot()
    {
        _primaryLastShotFrame = Time.frameCount;
    }

    private static bool IsTwoHandedCheck(ECommand command)
        => command == ECommand.CheckAmmo || command == ECommand.CheckChamber || command == ECommand.ExamineWeapon;

    private static void ObserveOwnerInput(InputNode __instance, ref float[] __1, ref ECursorResult __2)
    {
        var self = Instance;
        if (self != null && self.ReloadBusy
            && ReferenceEquals(self._serviceOwner, __instance))
        {
            self._reloadInputAdmitted = __1 != null && __2 != ECursorResult.ShowCursor;
            if (__1 == null || __2 == ECursorResult.ShowCursor)
                self.CancelReload("Native UI input interrupted automatic reload service");
        }
        if (self?._input == null || !ReferenceEquals(self._input.Owner, __instance)) return;
        // Higher-priority UI nodes have already filtered this shared axes value.
        // Observe admission; do not alter commands, axes, cursor or native UI.
        self._ownerInputAdmitted = __1 != null && __2 != ECursorResult.ShowCursor;
        if (!self._ownerInputAdmitted)
        { self.StopOffhandTrigger(); self.UpdatePrimaryTriggerSafety(false); }
    }

    private static bool BeforeAutomaticUpdate(Player.FirearmController.AutomaticFireOperation __instance, ref float __0)
    {
        var self = Instance;
        if (self == null || self._primary == null || __instance.Controller != self._primary) return true;
        try
        {
            bool gameplay = self._settings.Enabled.Value && !self.ReloadBusy && self._ownerInputAdmitted && PairedInput.AllowsGameplay(self._input?.Owner);
            self.UpdatePrimaryTriggerSafety(gameplay);
            if (!gameplay || self._primaryRequiresRelease || !(self._input?.IsShootHeld() ?? false))
            {
                self.StopPrimaryTrigger();
                __instance.SwitchToIdling();
                return false;
            }
            // Keep native per-round operation/animation; only bound elapsed work.
            // OnShot includes the native immediate first round at Start.
            double interval = Math.Min(__instance.ShotsTime, PairPolicy.ShotInterval(WeaponCompatibility.GetShotRate(self._primary.Item)));
            __0 = PistolMechanics.BoundedNativeStep(__0, interval,
                self._primaryLastShotFrame == Time.frameCount || self._primaryAutoUpdateFrame == Time.frameCount);
            self._primaryAutoUpdateFrame = Time.frameCount;
            return true;
        }
        catch (Exception e)
        {
            self.Unpair("Native automatic timing/input refused; pair removed safely.");
            self.Logger.LogError(e);
            return false;
        }
    }

    private void StopPrimaryTrigger()
    {
        if (_primary == null || _input == null || !ReferenceEquals(_input.Translator._controller, _primary)) return;
        if (_primary.IsTriggerPressed) _primary.SetTriggerPressed(false);
        if (_primary.IsTriggerPressed) _primary.IsTriggerPressed = false;
        _input.Translator._firearmTriggerPressed = false;
    }

    private void UpdatePrimaryTriggerSafety(bool gameplay)
    {
        if (_primary == null || _input == null) return;
        bool held = _input.IsShootHeld();
        if (_primaryMode != _primary.Item.SelectedFireMode)
        { _primaryMode = _primary.Item.SelectedFireMode; _primaryRequiresRelease = true; StopPrimaryTrigger(); }
        if (!gameplay || !held)
        { StopPrimaryTrigger(); _primaryRequiresRelease = !gameplay; }
    }

    private void StopOffhandTrigger()
    {
        _trigger.CancelHold();
        _shot?.ReleaseTrigger();
    }

    private static bool BeforeOwnerCommand(PlayerOwner __instance, ECommand command, ref InputNode.ETranslateResult __result)
    {
        var self = Instance;
        if (self == null || !self._settings.Enabled.Value || __instance.Player == null || !__instance.Player.IsYourPlayer) return true;
        bool modeShortcut = self.ModeShortcutDown();
        if (command == ECommand.ChangeWeaponMode || command == ECommand.ForceAutoWeaponMode || command == ECommand.CheckFireMode)
            self.LogDiagnostic("DW_MODE_INPUT command=" + command + " left_modifier=" + self.LeftModifierActive() + " left_shortcut=" + modeShortcut
                + " paired=" + (self._player != null));
        bool shortcut = self._settings.PairShortcut.Value.IsDown() || (self._player != null && self._settings.ReloadOffhand.Value.IsDown());
        // Modifier + native action: the native command itself is the request, so it never also
        // reaches the right pistol; only weapon commands are taken, movement/fire/aim pass.
        if (self._player != null && self.LeftModifierActive() && PairedInput.AllowsGameplay(__instance))
        {
            LeftCommand left = LeftCommandFor(command);
            if (left != LeftCommand.Pass)
            {
                if (left != LeftCommand.Block) self.RouteLeftCommand(left);
                __result = InputNode.ETranslateResult.Block;
                return false;
            }
        }
        if (self.ReloadBusy && self._serviceOwner == __instance)
        {
            if (self._settings.PairShortcut.Value.IsDown() || IsServiceItemAction(command))
            {
                if (self._settings.PairShortcut.Value.IsDown())
                {
                    if (self.BeginShortcut(__instance))
                    {
                        self.CancelReload("Manual unpair during reload service");
                        if (self._offhandReload != null && self._player != null) self.Unpair("Manual unpair during native offhand transaction");
                    }
                    __result = InputNode.ETranslateResult.Block;
                    return false;
                }
                self.CancelReload("Manual weapon action (" + command + ") interrupted automatic reload service");
                if (!self._settings.PairShortcut.Value.IsDown()) return true;
            }
            if (command == ECommand.ToggleShooting || command == ECommand.ToggleAlternativeShooting
                || command == ECommand.ReloadWeapon || command == ECommand.QuickReloadWeapon
                || command == ECommand.ChangeWeaponMode || command == ECommand.ForceAutoWeaponMode)
            { __result = InputNode.ETranslateResult.Block; return false; }
        }
        if (modeShortcut && PairedInput.AllowsGameplay(__instance))
        {
            self.HandleShortcut(__instance);
            // Alt+B is also the native right CheckFireMode chord: only the left selector moves.
            if (command == ECommand.ChangeWeaponMode || command == ECommand.ForceAutoWeaponMode || command == ECommand.CheckFireMode)
            { __result = InputNode.ETranslateResult.Block; return false; }
        }
        if (!shortcut && (self._pairBarrierOwner != __instance || !self._pairBarrier.Blocks(Time.frameCount))) return true;
        if (!PairedInput.AllowsGameplay(__instance)) return true;
        if (shortcut) self.HandleShortcut(__instance);
        // Keep native trigger-release bookkeeping even when V also maps to knife.
        if (command == ECommand.EndShooting || command == ECommand.EndAlternativeShooting) return true;
        __result = InputNode.ETranslateResult.Block;
        return false;
    }

    private static void AfterInputDelivery(InputManager __instance)
    {
        var self = Instance;
        if (self != null && self._pairBarrierManager == __instance) self._pairBarrier.Delivered();
    }

    private bool BeginShortcut(PlayerOwner owner)
    {
        if (!(owner.InputTree is InputTree tree) || !_pairBarrier.Begin(Time.frameCount)) return false;
        _pairBarrierOwner = owner;
        _pairBarrierManager = tree.InputManager;
        return true;
    }

    private void HandleShortcut(PlayerOwner owner)
    {
        if (_settings.PairShortcut.Value.IsDown())
        {
            if (_reloadService?.Active == true) { if (BeginShortcut(owner)) CancelReload("Manual unpair during reload service"); }
            else TogglePairOnce(owner);
        }
        else if (_player != null && _settings.ReloadOffhand.Value.IsDown())
        { if (BeginShortcut(owner)) BeginReload(PistolHand.Offhand); }
        else if (ModeShortcutDown()) ChangeOffhandModeOnce();
    }

    // The left-hand modifier chord wins: Mouse4+Alt+B is EFT's fire-mode check for the left
    // pistol, even when the left fire-mode shortcut is Alt+B.
    private bool ModeShortcutDown() => _player != null && _settings.ModeOffhand.Value.IsDown() && !LeftModifierActive();

    private void ChangeOffhandModeOnce()
    {
        if (_player != null && _modeShortcutFrame != Time.frameCount)
        {
            _modeShortcutFrame = Time.frameCount;
            if (!_ownerInputAdmitted) { SetStatus("Left selector blocked by native UI input."); return; }
            if (_trigger.Holding || _shot?.IsTriggerPressed == true)
            { Notify("Release the left trigger to change its fire mode."); return; }
            if (_primary != null && _secondary != null)
            {
                try
                {
                    if (ReloadBusy) { Notify("Finish the current action before changing the left fire mode."); return; }
                    if (_handPose?.CanChangeOffhandSelector == false)
                    { Notify("The left fire mode cannot change while its slide is locked back."); return; }
                    if (_shot == null || _shot.NativeActionActive) { Notify("Finish the current action before changing the left fire mode."); return; }
                    bool changed = OffhandFireMode.TryCycle(_player, _primary, _secondary, _shot.ChangeFireModeNative, out string feedback);
                    // A change shows in EFT's own fire-mode display; only a refusal needs a message.
                    // The selector click is an animation event of the native clip, which the
                    // offhand context does not receive outside native operations.
                    if (changed)
                    {
                        LogDiagnostic("DW_LEFT_MODE mode=" + _secondary.SelectedFireMode + " right_mode=" + _primary.Item.SelectedFireMode);
                        _handPose?.BeginOffhandSelector();
                        _selectorSoundAt = Time.unscaledTime + SelectorSoundDelay;
                        PublishOffhandAction(OffhandAction.FireModeChange);
                    }
                    else Notify(feedback);
                }
                catch (Exception e)
                { Unpair("Native selector changed unexpectedly; pair removed without retry."); Logger.LogError(e); }
            }
        }
    }

    // EFT's own fire-mode check (Alt+B) on the left pistol's context: the native display shows the
    // left mode and the left pistol plays its native check clip. One hand: the right stays drawn.
    private void CheckOffhandFireMode()
    {
        if (_player == null || _primary == null || _secondary == null || _shot == null) return;
        if (!_ownerInputAdmitted || ReloadBusy || _shot.NativeActionActive || _trigger.Holding || _shot.IsTriggerPressed)
        { _shot.ShowNativeFireMode(); return; }
        OffhandShotContext.NativeCheckResult result;
        try { result = _shot.TryNativeCheck(OffhandShotContext.NativeCheck.FireMode); }
        catch (Exception e) { Logger.LogError("Native left fire-mode check failed: " + e); result = OffhandShotContext.NativeCheckResult.Refused; }
        bool started = result == OffhandShotContext.NativeCheckResult.Started;
        _handPose?.OwnNativeAction(started);
        // A single-mode pistol has no check clip; EFT still shows its mode.
        if (!started) _shot.ShowNativeFireMode();
        LogDiagnostic("DW_LEFT_MODE_CHECK result=" + result + " mode=" + _secondary.SelectedFireMode + " operation=" + _shot.NativeOperationName);
    }

    private enum LeftCommand { Pass, Block, Reload, Mode, CheckMode, Magazine, Chamber, Examine }

    // EFT's own weapon commands, as the native input translator would deliver them to the right
    // pistol. Right-only weapon commands are swallowed while the modifier is held.
    private static LeftCommand LeftCommandFor(ECommand command) => command switch
    {
        ECommand.ReloadWeapon or ECommand.QuickReloadWeapon => LeftCommand.Reload,
        ECommand.ChangeWeaponMode => LeftCommand.Mode,
        ECommand.CheckFireMode => LeftCommand.CheckMode,
        ECommand.CheckAmmo => LeftCommand.Magazine,
        ECommand.CheckChamber => LeftCommand.Chamber,
        ECommand.ExamineWeapon => LeftCommand.Examine,
        ECommand.ForceAutoWeaponMode or ECommand.NextMagazine or ECommand.PreviousMagazine
            or ECommand.UnloadMagazine or ECommand.ChamberUnload or ECommand.FoldStock or ECommand.ToggleLockMode
            or ECommand.TogglePinMode or ECommand.LeftStanceToggle or ECommand.SetLeftStance => LeftCommand.Block,
        _ => LeftCommand.Pass,
    };

    // Held now, or released moments ago: a native Release-type binding (R reloads on release)
    // still belongs to the left pistol when the modifier is let go first.
    private bool LeftModifierActive()
    {
        KeyCode key = _settings.LeftModifier.Value;
        if (key == KeyCode.None) return false;
        if (Input.GetKey(key)) { _leftModifierHeldAt = Time.unscaledTime; return true; }
        return _leftModifierHeldAt >= 0f && Time.unscaledTime - _leftModifierHeldAt < .25f;
    }

    // One left request per frame, even when a chord delivers several native commands.
    private void RouteLeftCommand(LeftCommand command)
    {
        if (_leftCommandFrame == Time.frameCount || _player == null) return;
        _leftCommandFrame = Time.frameCount;
        switch (command)
        {
            case LeftCommand.Reload: BeginReload(PistolHand.Offhand); break;
            case LeftCommand.Mode: ChangeOffhandModeOnce(); break;
            case LeftCommand.CheckMode: CheckOffhandFireMode(); break;
            case LeftCommand.Magazine: BeginOffhandService(OffhandShotContext.NativeCheck.Magazine); break;
            case LeftCommand.Chamber: BeginOffhandService(OffhandShotContext.NativeCheck.Chamber); break;
            case LeftCommand.Examine: BeginOffhandService(OffhandShotContext.NativeCheck.Examine); break;
        }
    }

    private static bool IsWeaponSwitchCommand(ECommand command) => command == ECommand.SelectFirstPrimaryWeapon
        || command == ECommand.SelectSecondPrimaryWeapon || command == ECommand.SelectSecondaryWeapon
        || command == ECommand.QuickSelectSecondaryWeapon || command == ECommand.SelectKnife || command == ECommand.QuickKnifeKick;

    // EFT sends TryHighThrow/TryLowThrow for its throw keys (Shoot/Aim by default) whatever is in
    // the hands; only a grenade in hand acts on them, so they never interrupt a service.
    private static bool IsServiceItemAction(ECommand command) => IsWeaponSwitchCommand(command)
        || command == ECommand.ThrowGrenade || command == ECommand.PressThrowGrenade
        || command == ECommand.PressSlot0 || command == ECommand.PressSlot4 || command == ECommand.PressSlot5
        || command == ECommand.PressSlot6 || command == ECommand.PressSlot7 || command == ECommand.PressSlot8 || command == ECommand.PressSlot9
        || command == ECommand.SelectFastSlot0 || command == ECommand.SelectFastSlot4 || command == ECommand.SelectFastSlot5
        || command == ECommand.SelectFastSlot6 || command == ECommand.SelectFastSlot7 || command == ECommand.SelectFastSlot8 || command == ECommand.SelectFastSlot9;

    private PlayerOwner? ShortcutOwner()
    {
        if (_input != null) return _input.Owner;
        if (!Singleton<GameWorld>.Instantiated) return null;
        Player player = Singleton<GameWorld>.Instance.MainPlayer;
        // Component lookup only on a requested shortcut, never an idle frame scan.
        return player != null ? player.GetComponent<PlayerOwner>() : null;
    }

    private bool _chamberingOffhand;

    private static bool OffhandNeedsChamber(Weapon offhand, out Ammo? top)
    {
        top = null;
        if (offhand.Chambers.Length != 1 || offhand.Chambers[0].ContainedItem != null) return false;
        top = offhand.GetCurrentMagazine()?.Cartridges?.Last as Ammo;
        return top != null && !top.IsUsed;
    }

    private async void ChamberOffhand(Weapon offhand, Ammo top)
    {
        if (_player == null) return;
        InventoryController inventory = _player.InventoryController;
        _chamberingOffhand = true;
        try
        {
            var feed = Ammo.ApplyToAddress(top, offhand.Chambers[0].CreateItemAddress(), 1, inventory, simulate: true);
            if (feed.Failed || !feed.Value.CanExecute(inventory)) { SetStatus("Left pistol could not chamber: " + (feed.Failed ? feed.Error.ToString() : "not executable")); return; }
            SetStatus("Left pistol chambering.");
            var result = await inventory.TryRunNetworkTransaction(feed);
            SetStatus(result.Succeed ? "Left pistol chambered." : "Left pistol could not chamber: " + result.Error);
            if (_settings.Diagnostics.Value) Logger.LogInfo("DW_OFFHAND_CHAMBER ok=" + result.Succeed + " ammo=" + top.Id);
        }
        catch (Exception e) { SetStatus("Left pistol could not chamber."); Logger.LogWarning(e); }
        finally { _chamberingOffhand = false; }
    }

    private void TryFire()
    {
        long inputTicks = Stopwatch.GetTimestamp();
        if (ReloadBusy || _shot == null || _player == null || _primary == null || _secondary == null) return;
        if (_handPose?.HasSnapshot != true) { StopOffhandTrigger(); return; }
        if (!_ownerInputAdmitted || !PairedInput.AllowsGameplay(_input?.Owner)) { StopOffhandTrigger(); return; }
        string? stop = PairedStopReason();
        if (stop != null) { Unpair(stop); return; }
        if (_player.IsSprintEnabled || !_primary.SuitableForHandInput || _animation == null || _animation.EnterBlend < .999f || _animation.LowerBlend > .001f)
        { SetStatus("Pistols are drawing/lowered or native hands input is unavailable; release and press again."); StopOffhandTrigger(); return; }
        if (!WeaponCompatibility.IsSupportedMode(_secondary.SelectedFireMode)) { Unpair("Offhand fire mode changed to an unsupported mode"); return; }
        if (_shot.NativeActionActive) { StopOffhandTrigger(); return; }
        if (_secondary.MalfState.State != Weapon.EMalfunctionState.None)
        {
            _shot.PressMalfunctionedTrigger();
            StopOffhandTrigger();
            SetStatus("Left pistol malfunction: " + ServiceHint());
            return;
        }
        if (_primary.CurrentOperation is Player.FirearmController.MalfunctionOperation) { StopOffhandTrigger(); return; }
        // Native pistols chamber the top round on the first trigger press with a loaded magazine
        // and an empty chamber; the left pistol does the same through the native feed transaction.
        if (_chamberingOffhand) { StopOffhandTrigger(); return; }
        if (OffhandNeedsChamber(_secondary, out Ammo? top)) { ChamberOffhand(_secondary, top!); StopOffhandTrigger(); return; }
        double rpm;
        try { rpm = WeaponCompatibility.GetShotRate(_secondary); }
        catch (Exception e) { Unpair("Invalid native firing rate: " + e.Message); Logger.LogWarning(e); return; }
        double gameTime = Time.timeAsDouble;
        _cooldowns.TryGetValue(_secondary, out double nextTime);
        if (!PairPolicy.IsCooldownReady(gameTime, nextTime))
        {
            QueueAudit(new ShotAudit { InputTicks = inputTicks, Rpm = rpm, Cooldown = true,
                RemainingMs = (nextTime - gameTime) * 1000d });
            return;
        }
        bool nativeStarted = false;
        try
        {
            // Cached address plus current owner is sufficient between inventory
            // boundaries. No inventory-tree traversal in the shot path.
            if (!ReferenceEquals(_secondary.CurrentAddress, _secondaryAddress)
                || !ReferenceEquals(_secondary.Owner, _player.InventoryController)
                || _primary.Item.Id == _secondary.Id)
                throw new InvalidOperationException("Offhand ownership/address changed.");
            if (_primary.CurrentOperation is not Player.FirearmController.Idling && _primary.CurrentOperation is not Player.FirearmController.FireOperationBase)
                throw new InvalidOperationException("Primary is performing an incompatible operation.");
            // The offhand lacks native overlap handling; refuse shots through nearby geometry.
            AlignModel();
            Vector3 muzzle = _shot.CurrentFireport.position;
            Vector3 head = _player.PlayerBones.Head.position;
            if (_collisionMask == 0) throw new InvalidOperationException("EFT collision layers unavailable.");
            if (Physics.Linecast(head, muzzle, _collisionMask, QueryTriggerInteraction.Ignore))
            {
                SetStatus("Shot refused: offhand muzzle is obstructed. No ammunition consumed.");
                StopOffhandTrigger();
                return;
            }
            int before = CountAmmo(_primary.Item);
            nativeStarted = true;
            bool primaryFiring = _primary.IsTriggerPressed;
            OffhandShotResult result = _shot.FireOnce(inputTicks, _settings.Diagnostics.Value,
                _trigger.Holding && _secondary.SelectedFireMode == Weapon.EFireMode.fullauto);
            if (result.Malfunction != Weapon.EMalfunctionState.None)
            {
                // The pair stays: the left pistol keeps its native malfunction, the right keeps firing.
                StopOffhandTrigger();
                Notify("Left pistol malfunction: " + ServiceHint());
                LogDiagnostic("DW_MALFUNCTION side=left state=" + result.Malfunction + " fired=" + result.Fired
                    + " native_operation=" + _shot.NativeOperationName + " offhand=" + result.OffhandBefore + "->" + result.OffhandAfter);
                if (!result.Fired) { QueueAudit(new ShotAudit { Result = result, InputTicks = inputTicks, Rpm = rpm }); return; }
            }
            if (!result.Fired)
            {
                SetStatus("Left pistol empty: " + _settings.LeftModifier.Value + "+R (or " + _settings.ReloadOffhand.Value + ") reloads it; R reloads right.");
                StopOffhandTrigger();
                QueueAudit(new ShotAudit { Result = result, InputTicks = inputTicks, Rpm = rpm });
                return;
            }
            int after = CountAmmo(_primary.Item);
            // A native feed failure also strips the next round, which stays in the weapon until cleared.
            int stuckRound = result.Malfunction == Weapon.EMalfunctionState.Feed ? 1 : 0;
            if (!PairPolicy.IsValidShotDelta(before, after, result.OffhandBefore, result.OffhandAfter + stuckRound))
                throw new InvalidOperationException("Ammunition invariant failed. No automatic retry or refund.");
            // Unity scaled game time matches the native cooldown's delta-time
            // progression. Stopwatch remains diagnostics only, including slow motion.
            // Shot updates the native heat multiplier. Base the NEXT deadline
            // on that post-shot state, not the multiplier before this round.
            _cooldowns[_secondary] = PairPolicy.NextShotTime(Time.timeAsDouble, WeaponCompatibility.GetShotRate(_secondary));
            if (DualWieldNetwork.HasSubscribers)
                DualWieldNetwork.PublishShot(_secondary, new OffhandShot { Position = result.ProjectileOrigin, Direction = result.ProjectileDirection,
                    AmmoTemplate = result.AmmoTemplate, Overheat = _secondary.MalfState.LastShotOverheat });
            _animation.Fire(_shot.float_5);
            ApplyLeftCameraRecoil();
            // A jam/feed/slide failure already plays its own native malfunction state.
            if (result.Malfunction == Weapon.EMalfunctionState.None) _handPose?.BeginOffhandShot(_secondary);
            SetStatus("Offhand fired using its native selected mode. Alt+V unpairs.");
            QueueAudit(new ShotAudit { Result = result, InputTicks = inputTicks, Rpm = rpm, FireMode = _secondary.SelectedFireMode.ToString(), PrimaryFiring = primaryFiring, WeaponId = _secondary.Id.ToString() });
        }
        catch (Exception e)
        {
            if (nativeStarted && _shot?.MutationStarted == true)
            {
                if (_shot != null)
                    Logger.LogError("DW_FAILURE native_projectiles=" + _shot.LastProjectileCount + "; offhand_ammo=" + CountAmmo(_secondary!) + "; primary_ammo=" + CountAmmo(_primary!.Item));
                _faulted = true;
                Unpair("Stopped: " + e.Message + " Restart the test session before re-pairing.");
                Logger.LogError("Offhand native path failed; no retry or inventory rollback performed: " + e);
            }
            else
            {
                Unpair("Shot refused before native ammo transaction: " + e.Message);
                Logger.LogWarning(e);
            }
        }
    }

    internal static int CountAmmo(Weapon weapon) => weapon.ChamberAmmoCount + weapon.GetCurrentMagazineCount();
}
