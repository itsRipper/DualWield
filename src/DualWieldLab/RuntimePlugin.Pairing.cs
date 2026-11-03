using System;
using System.Linq;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

public sealed partial class RuntimePlugin
{
    private bool BeginDualHolster(bool fastDrop)
    {
        if (_transition.Holstering) return true;
        if (_faulted || WeaponActionBusy || _handPose?.HasSnapshot != true || _visualRig == null || _player == null
            || !_player.HealthController.IsAlive || _player.IsInventoryOpened) return false;
        StopPrimaryTrigger(); StopOffhandTrigger();
        _transition.BeginHolster();
        _holsterPairing.Disarm();
        LogDiagnostic("DW_DUAL_HOLSTER step=begin fast=" + fastDrop + " draw_pending=" + _transition.DrawPending
            + " main=" + _primary?.Item.Id + " offhand=" + _secondary?.Id);
        return true;
    }

    // The draw pair takes native input once the controller is idle and the hands translator is
    // bound to it (two frames, as for any native readiness here).
    private void FinishDualDraw()
    {
        if (_player == null || _primary == null) return;
        if (_transition.DrawTimedOut) { Unpair("The native draw did not reach ready"); return; }
        PlayerOwner? owner = _transition.DrawOwner;
        bool ready = owner != null && !_primary.IsInSpawnOperation() && _primary.CurrentOperation is Player.FirearmController.Idling
            && owner.HandsInputTranslator is FirearmHandsInputTranslator translator && ReferenceEquals(translator._controller, _primary)
            && PairedInput.AllowsGameplay(owner);
        if (!_transition.ObserveDrawReady(ready)) return;
        try
        {
            PairedInput input = PairedInput.Create(_player);
            if (!ReferenceEquals(input.Translator._controller, _primary))
                throw new InvalidOperationException("Native firearm input is bound to another controller.");
            input.ValidateShortcuts(_settings.PairShortcut.Value, _settings.ReloadOffhand.Value, _settings.ModeOffhand.Value, new KeyboardShortcut(_settings.LeftModifier.Value));
            _input = input;
            _ownerInputAdmitted = false;
            _primaryRequiresRelease = true;
            float drawSeconds = _transition.DrawSeconds;
            _transition.CompleteDraw();
            LogDiagnostic("DW_BINDINGS " + input.BindingSummary);
            LogDiagnostic("DW_DUAL_DRAW step=ready seconds=" + drawSeconds.ToString("F3")
                + " " + (_visualRig?.IdleMarkerCheck() ?? ""));
        }
        catch (Exception e)
        {
            Unpair("Pair refused: " + e.Message);
            LogDiagnostic("DW_PAIR_REFUSED " + e);
        }
    }

    private void TogglePairOnce(PlayerOwner owner)
    {
        if (!BeginShortcut(owner)) return;
        _holsterPairing.CancelPending();
        if (_player != null)
        {
            Unpair("Manual unpair");
            _holsterPairing.SuppressUntilHandsChange();
        }
        else if (_offhandReload != null) Notify("Wait for the left reload to finish.");
        else if (owner.Player != null && _holsterPairing.SelectRightHolsterForPair(owner.Player, "manual"))
            SetStatus("Drawing the right-holster pistol; dual wield follows.");
        else if (!_faulted && !_pairing) TryPair();
    }

    // drawing: the native pistol draw is in progress (automatic pairing). The presentation is
    // built now and follows the draw; native input binds in FinishDualDraw once it is idle.
    private void TryPair(Weapon? exactOffhand = null, bool drawing = false)
    {
        if (_pairing || _player != null || _contextObject != null) return;
        _pairing = true;
        try
        {
            if (HasFika()) throw new InvalidOperationException("Fika was detected. Only standalone SPT is supported.");
            if (!Singleton<GameWorld>.Instantiated) throw new InvalidOperationException("No raid world.");
            GameWorld world = Singleton<GameWorld>.Instance;
            if (_cooldownWorld != world) { _cooldowns.Clear(); _cooldownWorld = world; }
            if (!(world is HideoutGameWorld) && !world.IsLocalGame()) throw new InvalidOperationException("Only a standalone local raid or native hideout shooting range is supported.");
            Player player = world.MainPlayer;
            if (player == null || !player.IsYourPlayer || !player.HealthController.IsAlive || !player.FirstPersonPointOfView || player.IsInventoryOpened
                || (player.IsSprintEnabled && !drawing))
                throw new InvalidOperationException("Use first-person local gameplay, with inventory closed and sprint stopped.");
            if (world is HideoutGameWorld)
            {
                var rangeOwner = player.GetComponent<PlayerOwner>();
                if (rangeOwner == null) throw new InvalidOperationException("Native shooting-range input owner unavailable.");
                string? rangeReason = HideoutRangeContext.Refusal(player, rangeOwner);
                if (rangeReason != null) throw new InvalidOperationException(rangeReason);
            }
            var primary = player.HandsController as Player.FirearmController;
            if (primary == null) throw new InvalidOperationException("No native firearm controller in hands.");
            string? weaponReason = WeaponCompatibility.GetRefusal(primary.Item, "Primary", allowMalfunction: true);
            if (weaponReason != null) throw new InvalidOperationException(weaponReason);
            if (primary.IsAiming) throw new InvalidOperationException("Exit single-weapon ADS before pairing.");
            if (primary.IsInSpawnOperation() && !drawing) throw new InvalidOperationException("Primary is drawing/equipping; wait until idle.");
            if (primary.IsInReloadOperation()) throw new InvalidOperationException("Primary is reloading; wait until idle.");
            if (primary.IsInRemoveOperation() || primary.Destroyed) throw new InvalidOperationException("Primary is holstering.");
            if (primary.IsInInteraction()) throw new InvalidOperationException("Primary is in a native hands interaction.");
            if (primary.CurrentOperation is not Player.FirearmController.Idling && !(drawing && primary.IsInSpawnOperation()))
                throw new InvalidOperationException("Primary operation is " + primary.CurrentHandsOperationName + "; release fire and wait until idle.");
            PairedInput? input = null;
            PlayerOwner? drawOwner = null;
            if (drawing)
            {
                drawOwner = player.GetComponent<PlayerOwner>();
                if (drawOwner == null) throw new InvalidOperationException("Native player input owner unavailable.");
            }
            else
            {
                input = PairedInput.Create(player);
                if (!ReferenceEquals(input.Translator._controller, primary))
                    throw new InvalidOperationException("Native firearm input is still binding to the equipped controller; wait until ready.");
                input.ValidateShortcuts(_settings.PairShortcut.Value, _settings.ReloadOffhand.Value, _settings.ModeOffhand.Value, new KeyboardShortcut(_settings.LeftModifier.Value));
                if (!PairedInput.AllowsGameplay(input.Owner)) throw new InvalidOperationException("Native gameplay input is suppressed.");
            }
            // The left holster (a real equipment slot added by the dual-holster mods) is the
            // offhand's place; otherwise the backpack. Stash, secure container, pockets and all
            // other profile data are deliberately excluded.
            Slot? leftHolster = player.Equipment.Slots.FirstOrDefault(s => s.ID == HolsterAutoPairing.LeftHolsterSlot);
            Weapon[] candidates;
            if (leftHolster?.ContainedItem is Pistol holstered && holstered.Id != primary.Item.Id
                && (exactOffhand == null || ReferenceEquals(holstered, exactOffhand)))
                candidates = new Weapon[] { holstered };
            else
            {
                Item backpack = player.Equipment.GetSlot(EquipmentSlot.Backpack).ContainedItem;
                if (backpack == null) throw new InvalidOperationException("Put the loaded second magazine-fed pistol in your left holster or backpack first.");
                _items.Clear();
                backpack.GetAllItemsNonAlloc(_items);
                candidates = _items.OfType<Weapon>().Where(w => w is Pistol && w.Id != primary.Item.Id
                    && (exactOffhand != null ? ReferenceEquals(w, exactOffhand) : string.IsNullOrEmpty(_settings.OffhandId.Value) || w.Id == _settings.OffhandId.Value)).ToArray();
                _items.Clear();
            }
            if (candidates.Length > 1) candidates = candidates.Where(w => WeaponCompatibility.IsEligible(w, allowMalfunction: true)).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("No second pistol: equip one in each holster, or carry exactly one compatible pistol in the backpack (OffhandItemId picks one of several).");
            Weapon secondary = candidates[0];
            weaponReason = WeaponCompatibility.GetRefusal(secondary, "Offhand", allowMalfunction: true);
            if (weaponReason != null) throw new InvalidOperationException(weaponReason);
            if (!PairPolicy.IsDistinctPair(primary.Item.Id, secondary.Id, primary.Item.StringTemplateId, secondary.StringTemplateId))
                throw new InvalidOperationException("Distinct existing pistol identities were not established.");
            if (secondary.MalfState.State == Weapon.EMalfunctionState.None && secondary.Chambers[0].ContainedItem is Ammo ammo && ammo.IsUsed)
                throw new InvalidOperationException("Service the spent offhand chamber through native handling before pairing.");
            if (player is HideoutPlayer)
            {
                string? rangeReason = HideoutRangeContext.ValidateWeapons(player, primary.Item, secondary);
                if (rangeReason != null) throw new InvalidOperationException(rangeReason);
            }

            _player = player;
            _primary = primary;
            _primaryMode = primary.Item.SelectedFireMode;
            _primaryRequiresRelease = true;
            _primaryLastShotFrame = _primaryAutoUpdateFrame = -1;
            primary.OnShot += OnPrimaryShot;
            _secondary = secondary;
            _input = input;
            _transition.BeginPair(drawing, drawOwner);
            _ownerInputAdmitted = false;
            _trigger.Reset();
            _secondaryAddress = secondary.CurrentAddress;
            // Null player prevents WeaponPrefab.Parent from rebinding the live hands skeleton.
            _model = Singleton<ObjectsFactory>.Instance.CreateItem(secondary, Player.GetVisibleToCamera(player), null, false);
            if (_model == null) throw new InvalidOperationException("The native objects factory could not assemble the offhand pistol.");
            var prefab = _model.GetComponent<WeaponPrefab>();
            if (prefab == null) throw new InvalidOperationException("The offhand model has no WeaponPrefab.");
            prefab.Parent(null);
            _prefab = prefab;
            if (prefab.Hierarchy.GetTransform(ECharacterWeaponBones.Weapon_root) == null || primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root) == null) throw new InvalidOperationException("The required common pistol rig transforms were not found.");
            foreach (var collider in _model.GetComponentsInChildren<Collider>(true))
            {
                _colliderStates.Add((collider, collider.enabled));
                collider.enabled = false;
            }
            // Owns idle sampling, parenting and native layers. ResetStatesToDefault leaves the
            // SPAWN frame (gun lowered); the rig samples the native idle instead.
            _visualRig = OffhandVisualRig.Create(_model, prefab, primary, primary.Item.StringTemplateId + "|" + secondary.StringTemplateId);
            _contextObject = new GameObject("DualWieldLab.OffhandShotContext");
            _contextObject.transform.SetParent(player.Transform.Original, false);
            _shot = _contextObject.AddComponent<OffhandShotContext>();
            _shot.Initialize(player, secondary, primary, prefab, _visualRig.Fireport);
            float recoilScale = Mathf.Max(0f, 1f + secondary.RecoilDelta);
            if (!PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol,
                secondary.Template.RecoilForceUp * recoilScale, secondary.RecoilForceBack * recoilScale,
                _shot.WeaponLn, out _animationProfile))
                throw new InvalidOperationException("Native recoil/assembled geometry cannot form a supported pistol animation profile.");
            _animation = new PistolAnimationState(_animationProfile);
            // The native draw carries both pistols in; the procedural draw-in is for a pair made
            // with a pistol already in hands.
            if (drawing) _animation.Reset(drawIn: false);
            _handPose = OffhandHandPose.Create(player, primary, prefab, secondary);
            if (_handPose != null) _handPose.PresentationFollowsGun = _visualRig.NativeClip != null;
            _dualAiming = _settings.ReadyAim.Value;
            _reportedVisualPhase = -1;
            _poseReportPending = false;
            AlignModel();
            LogDiagnostic(_visualRig.DiagnosticSummary);
            LogDiagnostic("DW_GRIP " + _visualRig.GripCorrectionSummary);
            _visualRig.GripCorrectionChanged = false;
            if (input != null) LogDiagnostic("DW_BINDINGS " + input.BindingSummary);
            else LogDiagnostic("DW_DUAL_DRAW step=begin native_operation=" + primary.CurrentHandsOperationName
                + " grip_settled=" + _visualRig.GripCorrectionSettled);
            if (_handPose != null) LogDiagnostic(_handPose.DiagnosticSummary);
            Logger.LogInfo("Paired right " + primary.Item.Id + " (" + primary.Item.StringTemplateId + ") with left " + secondary.Id + " (" + secondary.StringTemplateId + ").");
        }
        catch (Exception e)
        {
            Unpair("Pair refused: " + e.Message);
            LogDiagnostic("DW_PAIR_REFUSED " + e);
        }
        finally { _pairing = false; }
    }

    private void Unpair(string reason)
    {
        if (_unpairing) return;
        _inspect = InspectPhase.None;
        // Covers the per-frame "Inventory opened" stop as well as the native UI patch.
        if (_player != null && _player.IsInventoryOpened)
            _holsterPairing.RememberPairForInventory(_player, _input?.Owner, _primary?.Item, _secondary);
        _offhandReload?.Interrupt(reason);
        EndOffhandReloadPresentation();
        ReleaseOffhandService();
        if (_settings.Diagnostics.Value) Logger.LogInfo("DW_UNPAIR reason=" + reason + " reload_return=" + _reloadReturn.State);
        if (ReloadVisualTransition || _reloadReturn.Active) CancelReload(reason);
        _unpairing = true;
        _poseRecovery.ObserveValid();
        try
        {
            // Remove from callbacks first. Native IK runs normally again on its next update.
            try
            {
                if (_primary != null) _primary.OnShot -= OnPrimaryShot;
                StopPrimaryTrigger();
                if (_primary != null && !_primary.Destroyed && _primary.CurrentOperation is Player.FirearmController.AutomaticFireOperation automatic)
                    automatic.SwitchToIdling();
            }
            catch (Exception e) { Logger.LogError("Native primary stop during cleanup: " + e); }
            _primaryRequiresRelease = true;
            _primaryLastShotFrame = _primaryAutoUpdateFrame = -1;
            _player = null;
            _primary = null;
            _secondary = null;
            _secondaryAddress = null;
            _input = null;
            _transition.Reset();
            _ownerInputAdmitted = false;
            _trigger.Reset();
            _poseReportPending = false;
            _reportedVisualPhase = -1;
            _dualAiming = false;
            _animation?.Reset(false);
            _animation = null;
            _animationProfile = default;
            try { _handPose?.Restore(); }
            catch (Exception e) { Logger.LogError("Hand pose restore: " + e); }
            _handPose = null;
            try { _visualRig?.Restore(); }
            catch (Exception e) { Logger.LogError("Visual state restore: " + e); }
            _visualRig = null;
            _prefab = null;
            try { _shot?.DisposeContext(); }
            catch (Exception e) { Logger.LogError("Offhand context cleanup: " + e); }
            _shot = null;
            if (_contextObject != null) Destroy(_contextObject);
            _contextObject = null;
            foreach (var state in _colliderStates)
                if (state.collider != null) state.collider.enabled = state.enabled;
            _colliderStates.Clear();
            if (_model != null)
            {
                try { AssetPoolObject.ReturnToPool(_model); }
                catch (Exception e) { Logger.LogError("Offhand model return: " + e); }
            }
            _model = null;
            _items.Clear();
        }
        finally { _unpairing = false; }
    }

    bool IPairingHost.Faulted => _faulted;
    bool IPairingHost.Pairing => _pairing;
    bool IPairingHost.ReloadBusy => ReloadBusy;
    bool IPairingHost.OffhandReloadPending => _offhandReload != null;
    bool IPairingHost.IsPairedWith(Player player) => _player == player && _primary != null && _secondary != null;
    Weapon? IPairingHost.PairedMain => _primary?.Item;
    Weapon? IPairingHost.PairedOffhand => _secondary;
    void IPairingHost.TryPair(Weapon? exactOffhand, bool drawing) => TryPair(exactOffhand, drawing);
    void IPairingHost.Notify(string message) => Notify(message);
    void IPairingHost.SetStatus(string message) => SetStatus(message);
    void IPairingHost.LogDiagnostic(string line) => LogDiagnostic(line);
}
