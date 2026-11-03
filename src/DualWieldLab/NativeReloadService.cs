using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

// The existing active native controller owns the entire right reload. Pistols
// retain their original addresses; only the native magazine operation may move
// magazines/cartridges. The left reload is InactiveMagazineReload.
internal sealed class NativeReloadService
{
    private enum Stage { Idle, WaitingForTarget, Reloading, WaitingForMain }
    private Stage _stage;
    private readonly ReloadCallbackGuard _callbacks = new ReloadCallbackGuard();
    private Player? _player;
    private PlayerOwner? _owner;
    private Weapon? _main, _offhand, _target;
    private ItemAddress? _mainAddress, _offhandAddress;
    private string _mainId = "", _offhandId = "";
    private PistolHand _hand;
    private Action<Weapon, Weapon>? _completed;
    private Action<string>? _status;
    private Action<string>? _diagnostic;
    private bool _reloadFinished, _reloadSucceeded, _reloadObserved;
    private Player.FirearmController? _controller;
    private float _stageTime, _beginTime;
    private string _reloadOutcome = "";
    private readonly List<Magazine> _magazines = new List<Magazine>(32);
    private Magazine? _oldMagazine, _nextMagazine;
    private GridItemAddress? _oldMagazineDestination;
    private int _reloadAmmoBefore, _otherAmmoBefore;
    private int _chamberBefore, _oldCountBefore, _nextCountBefore;
    private bool _nativeReloadSucceeded, _completionChecked;

    internal bool Active => _stage != Stage.Idle;
    internal bool ReloadWasVerified { get; private set; }
    internal bool NativeReloadVerified => _reloadSucceeded || ReloadWasVerified;
    internal const string OffhandReloadRefusal = "Left reload is disabled: safe in-place native reload/event handling is not verified. Both pistols and their inventory addresses are unchanged.";

    internal bool Begin(Player player, PlayerOwner owner, Weapon main, Weapon offhand, PistolHand hand,
        Action<Weapon, Weapon> onCompletedExactPair, Action<string> onStatus, Action<string>? onDiagnostic = null)
    {
        if (Active) return false;
        ReloadWasVerified = false;
        // Refuse before native reads, callbacks or any service/presentation state.
        if (hand == PistolHand.Offhand)
        {
            onDiagnostic?.Invoke("DW_RELOAD_REFUSED hand=Offhand reason=in_place_native_lifecycle_unverified weapon_relocations=0");
            onStatus(OffhandReloadRefusal);
            return false;
        }
        try
        {
            if (player == null || owner == null || owner.Player != player || !PairedInput.AllowsGameplay(owner))
                throw new InvalidOperationException("Native reload service requires admitted local gameplay input.");
            ReloadCoordinator.SelectForService(main, offhand, hand);
            if (!ReferenceEquals(main.Owner, player.InventoryController) || !ReferenceEquals(offhand.Owner, player.InventoryController))
                throw new InvalidOperationException("The two pistols no longer have the local native owner.");
            if (!player.InventoryController.Examined(main) || !player.InventoryController.Examined(offhand)
                || !main.CheckAction(null).Succeeded || !offhand.CheckAction(null).Succeeded)
                throw new InvalidOperationException("Both native pistols must be examined and available for the service action.");
            if (main.CurrentAddress == null || offhand.CurrentAddress == null)
                throw new InvalidOperationException("Both pistols must retain a valid native inventory address.");
            if (!(player.HandsController is Player.FirearmController controller) || controller.Item != main
                || controller.Destroyed || controller.CurrentOperation is not Player.FirearmController.Idling
                || player.InventoryController.IsChangingWeapon || player.ProcessStatus != Player.EProcessStatus.None)
                throw new InvalidOperationException("Release fire and wait until the native right pistol is idle before reloading.");
            // The holstered left pistol keeps its own native malfunction state through a right reload.
            if (main.MalfState.State != Weapon.EMalfunctionState.None)
                throw new InvalidOperationException("Service native malfunctions normally before managed reload.");

            _callbacks.Begin();
            _player = player; _owner = owner; _main = main; _offhand = offhand;
            _mainId = main.Id.ToString(); _offhandId = offhand.Id.ToString();
            _mainAddress = main.CurrentAddress; _offhandAddress = offhand.CurrentAddress;
            _target = main; _controller = controller;
            _hand = hand; _completed = onCompletedExactPair; _status = onStatus;
            _diagnostic = onDiagnostic;
            _beginTime = _stageTime = Time.unscaledTime;
            _reloadFinished = _reloadSucceeded = _reloadObserved = false;
            _reloadOutcome = "";
            if (!TryPlanReload(_target, out string refusal))
                throw new InvalidOperationException(refusal);
            Trace("preflight_valid");
            _stage = Stage.WaitingForTarget;
            onStatus("Reload right: native tactical/empty magazine reload; original weapon addresses remain unchanged.");
            return Active;
        }
        catch (Exception e)
        {
            Trace("preflight_refused:" + e.Message);
            Clear();
            onStatus("Reload refused before service: " + e.Message);
            return false;
        }
    }

    internal static bool SameAddress(ItemAddress? actual, ItemAddress? expected)
    {
        if (actual == null || expected == null || !ReferenceEquals(actual.Container, expected.Container)) return false;
        return actual is GridItemAddress || expected is GridItemAddress ? actual.Equals(expected) : true;
    }

    private bool IdentityValid() => _player != null && _main != null && _offhand != null
        && _main.Id.ToString() == _mainId && _offhand.Id.ToString() == _offhandId && _mainId != _offhandId
        && ReferenceEquals(_main.Owner, _player.InventoryController) && ReferenceEquals(_offhand.Owner, _player.InventoryController);

    private bool AddressesMatch() => IdentityValid()
        && SameAddress(_main!.CurrentAddress, _mainAddress)
        && SameAddress(_offhand!.CurrentAddress, _offhandAddress);

    internal void Update()
    {
        if (!Active) return;
        try
        {
            if (_player == null || !_player.HealthController.IsAlive || !_player.FirstPersonPointOfView
                || _player.IsInventoryOpened || _owner == null || _owner.Player != _player || !PairedInput.AllowsGameplay(_owner))
            { Cancel("Reload service cancelled by player/UI/raid boundary; no inventory rollback is forced."); return; }
            if (Time.unscaledTime - _beginTime > 60f)
            { Cancel("Native reload service timed out; no operation was retried or fabricated."); return; }
            if (!AddressesMatch())
            { Cancel("Pistol inventory address or identity changed; no weapon relocation or rollback requested."); return; }
            if (!ReferenceEquals(_player.HandsController, _controller) || _controller == null || _controller.Destroyed)
            { Cancel("Native active controller changed; no equip or ownership handoff requested."); return; }
            if (_stage == Stage.WaitingForTarget || _stage == Stage.WaitingForMain)
            {
                bool returning = _stage == Stage.WaitingForMain;
                Weapon expected = returning ? _main! : _target!;
                var controller = _player.HandsController as Player.FirearmController;
                if (controller == null || controller.Item != expected || !ReferenceEquals(controller, _controller))
                { Cancel("Original active pistol changed; no automatic equip or item move requested."); return; }
                if (controller.Destroyed || controller.IsInSpawnOperation() || _player.InventoryController.IsChangingWeapon
                    || _player.ProcessStatus != Player.EProcessStatus.None || controller.CurrentOperation is not Player.FirearmController.Idling) return;
                if (returning) Complete();
                else StartNativeReload(controller);
                return;
            }
            if (_stage == Stage.Reloading)
            {
                var controller = _player.HandsController as Player.FirearmController;
                if (controller == null || controller.Item != _target || !IdentityValid())
                { Cancel("Reload controller or item ownership changed; automatic service stopped."); return; }
                _reloadObserved |= controller.IsInReloadOperation();
                if (!_reloadFinished && !_reloadObserved && controller.CurrentOperation is Player.FirearmController.Idling
                    && Time.unscaledTime - _stageTime > 2f)
                {
                    _reloadFinished = true; _reloadSucceeded = false;
                    _reloadOutcome = "Native reload did not start; no custom magazine transaction was attempted.";
                }
                if (!_reloadFinished || controller.IsInReloadOperation()
                    || controller.CurrentOperation is not Player.FirearmController.Idling) return;
                if (!_completionChecked)
                {
                    _completionChecked = true;
                    if (_nativeReloadSucceeded) VerifyReloadOutcome();
                    Trace(_reloadSucceeded ? "reload_verified" : "reload_failed");
                }
                _stage = Stage.WaitingForMain; _stageTime = Time.unscaledTime;
            }
        }
        catch (Exception e) { Cancel("Native reload service stopped safely: " + e.Message); }
    }

    private void StartNativeReload(Player.FirearmController controller)
    {
        if (!AddressesMatch() || !ReferenceEquals(controller, _controller) || controller.Item != _main)
        { Cancel("Pistol identity/address changed before native reload; no operation requested."); return; }
        _stage = Stage.Reloading; _stageTime = Time.unscaledTime;
        _reloadFinished = _reloadSucceeded = _reloadObserved = false;
        _nativeReloadSucceeded = _completionChecked = false;
        if (!controller.CanStartReload()) { FinishReload(false, "Native controller cannot begin reload."); return; }
        if (!TryPlanReload(controller.Item, out string refusal)) { FinishReload(false, refusal); return; }
        Magazine next = _nextMagazine!;
        _reloadAmmoBefore = controller.Item.ChamberAmmoCount + (_oldMagazine?.Count ?? 0) + next.Count;
        _chamberBefore = controller.Item.ChamberAmmoCount;
        _oldCountBefore = _oldMagazine?.Count ?? 0; _nextCountBefore = next.Count;
        _otherAmmoBefore = OtherWeaponAmmo();
        int generation = _callbacks.Version;
        Trace("reload_requested");
        controller.ReloadMag(next, _oldMagazineDestination, result =>
        {
            if (!Active || !_callbacks.IsCurrent(generation) || _stage != Stage.Reloading) return;
            if (!AddressesMatch())
            { Cancel("Native callback observed changed pistol addresses; reload success refused."); return; }
            FinishReload(result.Succeed, result.Succeed ? "Native callback completed; verifying magazine and ammunition state." : "Native reload declined: " + result.Error + "; no custom retry or refund.");
            Trace("reload_callback");
        });
    }

    private bool TryPlanReload(Weapon target, out string refusal)
    {
        refusal = "";
        _oldMagazine = target.GetCurrentMagazine();
        _nextMagazine = null; _oldMagazineDestination = null;
        if (target.MustBoltBeOpennedForExternalReload)
        { refusal = "This reload mechanism can eject chamber ammunition and is not supported by the paired reload audit."; return false; }
        _magazines.Clear();
        _player!.InventoryController.GetReachableItemsOfTypeNonAlloc(_magazines, m => m.Count > 0);
        _magazines.Sort((a, b) => b.Count.CompareTo(a.Count));
        Slot magazineSlot = target.GetMagazineSlot();
        Magazine current = target.GetCurrentMagazine();
        string lastRefusal = "No compatible loaded native reachable magazine. No reload requested.";
        foreach (Magazine next in _magazines)
        {
            if (ReferenceEquals(next, current) || next.Parent?.Container.ParentItem is Weapon
                || !next.IsAmmoCompatible(target.Chambers)) continue;
            var nativeFit = ItemManipulator.CheckMoveIgnoringTargetItem(next, magazineSlot, _player.InventoryController);
            if (nativeFit.Failed) continue;
            GridItemAddress? destination = current == null ? null : _player.Equipment.GetPrioritizedGridsForUnloadedObject()
                .Select(g => g.FindLocationForItem(current)).Where(a => a != null)
                .OrderBy(a => a.Grid.GridWidth * a.Grid.GridHeight).FirstOrDefault();
            if (current != null && destination == null)
            { lastRefusal = "No native storage space for the old magazine; no reload requested."; continue; }
            _oldMagazine = current; _nextMagazine = next; _oldMagazineDestination = destination;
            Trace("candidate_planned");
            return true;
        }
        refusal = lastRefusal;
        return false;
    }

    private void FinishReload(bool success, string message)
    { _reloadFinished = true; _nativeReloadSucceeded = success; _reloadSucceeded = false; _reloadOutcome = message; }

    private int OtherWeaponAmmo()
    {
        return _offhand!.ChamberAmmoCount + _offhand.GetCurrentMagazineCount();
    }

    private void VerifyReloadOutcome()
    {
        Magazine? installed = _target!.GetCurrentMagazine();
        bool owned = AddressesMatch() && _nextMagazine != null
            && ReferenceEquals(_nextMagazine.Owner, _player!.InventoryController)
            && (_oldMagazine == null || ReferenceEquals(_oldMagazine.Owner, _player.InventoryController));
        bool installedCorrect = ReferenceEquals(installed, _nextMagazine)
            && installed != null && installed.Parent?.Container.ParentItem == _target
            && (_oldMagazine == null || installed.Id != _oldMagazine.Id);
        bool storedOld = _oldMagazine == null || (!ReferenceEquals(_oldMagazine, installed)
            && SameAddress(_oldMagazine.CurrentAddress, _oldMagazineDestination));
        int after = _target.ChamberAmmoCount + (installed?.Count ?? 0) + (_oldMagazine?.Count ?? 0);
        _reloadSucceeded = ReloadOutcomeAudit.Verified(_nativeReloadSucceeded, owned, installedCorrect,
            storedOld, _reloadAmmoBefore, after, _otherAmmoBefore, OtherWeaponAmmo())
            && ReloadOutcomeAudit.NativeCountsMatch(_chamberBefore, _oldCountBefore, _nextCountBefore,
                _target.ChamberAmmoCount, _oldMagazine?.Count ?? 0, installed?.Count ?? 0);
        _reloadOutcome = _reloadSucceeded ? "Native magazine reload verified: replacement installed, old magazine stored, ammunition conserved."
            : "Native callback completed but reload state verification failed; no reload success claimed. Inspect DW_RELOAD diagnostics and native inventory.";
    }

    internal static string DescribeAddress(ItemAddress? address)
    {
        if (address == null) return "none";
        string container = address.Container.ParentItem.Id + "/" + address.Container.ID;
        if (address is GridItemAddress grid)
            return container + ":" + grid.LocationInGrid.x + "," + grid.LocationInGrid.y + "," + grid.LocationInGrid.r;
        return container;
    }

    private void Trace(string step)
    {
        _diagnostic?.Invoke("DW_RELOAD step=" + step + " stage=" + _stage + " hand=" + _hand
            + " target=" + _target?.Id + " active=" + (_player?.HandsController as Player.FirearmController)?.Item?.Id
            + " operation=" + (_player?.HandsController as Player.FirearmController)?.CurrentOperation?.GetType().Name
            + " old_mag=" + _oldMagazine?.Id + " next_mag=" + _nextMagazine?.Id
            + " installed_mag=" + _target?.GetCurrentMagazine()?.Id + " chamber=" + _target?.ChamberAmmoCount
            + " old_count=" + _oldMagazine?.Count + " next_count=" + _nextMagazine?.Count
            + " main_address=" + DescribeAddress(_main?.CurrentAddress)
            + " offhand_address=" + DescribeAddress(_offhand?.CurrentAddress)
            + " original_main_address=" + DescribeAddress(_mainAddress)
            + " original_offhand_address=" + DescribeAddress(_offhandAddress)
            + " pistol_addresses_unchanged=" + AddressesMatch()
            + " callback_success=" + _nativeReloadSucceeded + " verified=" + _reloadSucceeded);
    }

    private void Complete()
    {
        if (!AddressesMatch()) { Cancel("Original pistol identities/addresses could not be verified after reload."); return; }
        if (_reloadSucceeded) VerifyReloadOutcome();
        Weapon main = _main!, offhand = _offhand!;
        var completed = _completed; var status = _status;
        string outcome = _reloadOutcome.Length == 0 ? "Native service finished." : _reloadOutcome;
        bool reloaded = _reloadSucceeded;
        ReloadWasVerified = reloaded;
        Trace("original_placement_verified");
        Clear();
        status?.Invoke(outcome + (reloaded ? " Original two-pistol placement verified; checking usable pair." : " Original unchanged two-pistol placement verified; checking usable pair."));
        completed?.Invoke(main, offhand);
    }

    internal void Cancel(string reason)
    {
        if (!Active) return;
        Trace("cancelled");
        var status = _status;
        Clear();
        status?.Invoke(reason);
    }

    private void Clear()
    {
        _callbacks.Invalidate();
        _stage = Stage.Idle; _player = null; _owner = null; _controller = null; _main = _offhand = _target = null;
        _mainAddress = _offhandAddress = null; _mainId = _offhandId = "";
        _completed = null; _status = null; _magazines.Clear();
        _diagnostic = null; _oldMagazine = _nextMagazine = null; _oldMagazineDestination = null;
        _reloadAmmoBefore = _otherAmmoBefore = 0; _nativeReloadSucceeded = _completionChecked = false;
        _chamberBefore = _oldCountBefore = _nextCountBefore = 0;
        _reloadFinished = _reloadSucceeded = _reloadObserved = false;
    }
}
