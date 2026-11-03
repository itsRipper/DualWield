using System.Linq;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

// What automatic pairing needs from the runtime: its busy state and the ordinary pairing path.
internal interface IPairingHost
{
    bool Faulted { get; }
    bool Pairing { get; }
    bool ReloadBusy { get; }
    bool OffhandReloadPending { get; }
    bool IsPairedWith(Player player);
    Weapon? PairedMain { get; }
    Weapon? PairedOffhand { get; }
    void TryPair(Weapon? exactOffhand, bool drawing);
    void Notify(string message);
    void SetStatus(string message);
    void LogDiagnostic(string line);
}

// When pairing starts without Alt+V: on the native draw of a holster pair, after the inventory
// closes (resuming the suspended pair or pairing newly filled holsters), and by reselecting the
// HOLSTER pistol when the HOLSTERLEFT pistol is in hands. Pairing itself stays in the runtime.
internal sealed class HolsterAutoPairing
{
    // Equipment slot name appended by the dual-holster server mod.
    internal const string LeftHolsterSlot = "HolsterLeft";

    private sealed class InventoryResume
    {
        internal Player Player = null!; internal PlayerOwner Owner = null!;
        internal Weapon Main = null!, Offhand = null!;
        internal ItemAddress? OffhandAddress;
        internal float ClosedAt;
    }

    private readonly DualWieldSettings _settings;
    private readonly IPairingHost _host;
    private InventoryResume? _inventoryResume;
    private readonly PresentationFrameGate _resumeFrame = new PresentationFrameGate();
    // EFT's hands-changed event arms automatic pairing when both holster slots hold compatible
    // pistols and the right one is drawn; Update pairs on the first frame of the native draw.
    // A manual unpair is respected until the hands change again.
    private Player? _handsWatched;
    private bool _autoPairArmed, _autoPairSuppressed;
    private float _autoPairArmedAt;
    // A HOLSTER reselection made for pairing (Alt+V, inventory close) pairs on its draw even
    // with automatic pairing disabled.
    private bool _pairOnNextDraw;
    // Inventory closed without a pair to resume: one check whether the holsters now form a pair.
    private Player? _holsterCheckPlayer;
    private float _holsterCheckClosedAt;
    private readonly PresentationFrameGate _holsterCheckFrame = new PresentationFrameGate();

    internal HolsterAutoPairing(DualWieldSettings settings, IPairingHost host)
    {
        _settings = settings;
        _host = host;
    }

    internal void Update(bool paired)
    {
        WatchHands();
        if (paired) return;
        if (_inventoryResume != null) TryResumeAfterInventory();
        else if (_autoPairArmed && !_autoPairSuppressed) TryAutoPair();
        else if (_holsterCheckPlayer != null) CheckHolstersAfterInventory();
    }

    // Alt+V or the diagnostic toggle: nothing pending survives a manual decision.
    internal void CancelPending()
    {
        _inventoryResume = null;
        _autoPairArmed = false;
    }

    // Deliberately unpaired: no automatic pairing until the hands change again.
    internal void SuppressUntilHandsChange() => _autoPairSuppressed = true;

    internal void Disarm() => _autoPairArmed = false;

    internal void Reset()
    {
        _inventoryResume = null;
        _holsterCheckPlayer = null;
        _pairOnNextDraw = false;
    }

    internal void Dispose()
    {
        if (_handsWatched != null) _handsWatched.OnHandsControllerChanged -= OnHandsChanged;
        _handsWatched = null;
    }

    // Closing: equipment may now form a holster pair while a single pistol is in hands.
    internal void OnInventoryClosed(Player player)
    {
        if (_inventoryResume != null) return;
        _holsterCheckPlayer = player;
        _holsterCheckClosedAt = Time.unscaledTime;
        _holsterCheckFrame.Reset();
    }

    internal void RememberPairForInventory(Player player, PlayerOwner? owner, Weapon? main, Weapon? offhand)
    {
        if (_host.Faulted || owner == null || main == null || offhand == null || _inventoryResume != null) return;
        _inventoryResume = new InventoryResume { Player = player, Owner = owner, Main = main, Offhand = offhand,
            OffhandAddress = offhand.CurrentAddress, ClosedAt = Time.unscaledTime };
        _resumeFrame.Reset();
        _host.LogDiagnostic("DW_INVENTORY_SUSPEND main=" + main.Id + " offhand=" + offhand.Id);
    }

    // notify=false: a legitimate result of the inventory change (a non-pistol or a single
    // holstered pistol in hands), not a failure to report.
    private void DropInventoryResume(string reason, bool notify = true)
    {
        if (_inventoryResume == null) return;
        _inventoryResume = null;
        _host.LogDiagnostic("DW_INVENTORY_RESUME restored=False reason=" + reason);
        if (notify) _host.Notify("Dual wield could not resume after the inventory: " + reason + ". Alt+V pairs again.");
    }

    // After an inventory close with no pair to resume: once the hands are ready, the holsters
    // that now form a pair are paired (HOLSTER pistol in hands) or the HOLSTER pistol is
    // reselected for it (HOLSTERLEFT pistol in hands).
    private void CheckHolstersAfterInventory()
    {
        Player? player = _holsterCheckPlayer;
        if (player == null || player.IsInventoryOpened) return;
        if (Time.unscaledTime - _holsterCheckClosedAt > 10f || !Singleton<GameWorld>.Instantiated
            || Singleton<GameWorld>.Instance.MainPlayer != player) { _holsterCheckPlayer = null; return; }
        var controller = player.HandsController as Player.FirearmController;
        var owner = player.GetComponent<PlayerOwner>();
        if (owner == null || !PistolHandsReady(player, owner, controller)) { _holsterCheckFrame.Reset(); return; }
        _holsterCheckFrame.Mark(Time.frameCount);
        if (!_holsterCheckFrame.Ready(Time.frameCount)) return;
        _holsterCheckPlayer = null;
        if (!_settings.AutoPair.Value || _autoPairSuppressed) return;
        if (BothHolstersCompatible(player, controller!.Item))
        {
            _host.TryPair(null, drawing: false);
            _host.LogDiagnostic("DW_HOLSTER_CHECK paired=" + _host.IsPairedWith(player) + " main=" + controller.Item.Id);
        }
        else SelectRightHolsterForPair(player, "inventory_closed");
    }

    // Same native readiness the reload restoration uses, then the ordinary pairing path,
    // which re-validates ownership, backpack placement and compatibility from scratch.
    private void TryResumeAfterInventory()
    {
        var resume = _inventoryResume!;
        Player player = resume.Player;
        if (player == null || !player.HealthController.IsAlive || !Singleton<GameWorld>.Instantiated
            || Singleton<GameWorld>.Instance.MainPlayer != player)
        { DropInventoryResume("player or world changed"); return; }
        if (player.IsInventoryOpened) { resume.ClosedAt = Time.unscaledTime; _resumeFrame.Reset(); return; }
        if (Time.unscaledTime - resume.ClosedAt > 10f) { DropInventoryResume("native hands did not become ready"); return; }
        // Suspend and refresh: whatever the inventory changed (a swapped holster pistol, new
        // attachments, the shooting range re-cloning its items) is paired fresh. The remembered
        // left pistol is kept only if it is still the same item at the same address.
        var controller = player.HandsController as Player.FirearmController;
        if (controller != null && !(controller.Item is Pistol) && !player.InventoryController.IsChangingWeapon)
        { DropInventoryResume("a non-pistol is in hands", notify: false); return; }
        if (!PistolHandsReady(player, resume.Owner, controller)) { _resumeFrame.Reset(); return; }
        _resumeFrame.Mark(Time.frameCount);
        if (!_resumeFrame.Ready(Time.frameCount)) return;
        // The HOLSTERLEFT pistol in hands: with a filled HOLSTER the pair is reselected natively,
        // otherwise it is a legitimate single pistol. Never a backpack search.
        if (InLeftHolster(controller!.Item))
        {
            bool selecting = SelectRightHolsterForPair(player, "inventory_resume");
            DropInventoryResume(selecting ? "right holster pistol reselected for the pair" : "single left-holster pistol", notify: false);
            return;
        }
        // A holster pair whose left pistol was removed is now a single right pistol.
        if (resume.OffhandAddress?.Container?.ID == LeftHolsterSlot && controller.Item.CurrentAddress?.Container?.ID == "Holster"
            && !HolsterPairCompatible(player))
        { DropInventoryResume("single right-holster pistol", notify: false); return; }
        _inventoryResume = null;
        bool sameOffhand = ReferenceEquals(resume.Offhand.Owner, player.InventoryController)
            && NativeReloadService.SameAddress(resume.Offhand.CurrentAddress, resume.OffhandAddress);
        _host.TryPair(sameOffhand ? resume.Offhand : null, drawing: false);
        bool paired = _host.IsPairedWith(player);
        _host.LogDiagnostic("DW_INVENTORY_RESUME restored=" + paired + " same_offhand=" + sameOffhand
            + " main=" + (_host.PairedMain?.Id.ToString() ?? "none") + " offhand=" + (_host.PairedOffhand?.Id.ToString() ?? "none"));
        if (paired) _host.SetStatus(sameOffhand ? "Dual wield restored after inventory." : "Dual wield refreshed with the equipped pistols.");
    }

    // A drawn pistol, idle, with native input bound to it: the state pairing needs.
    private bool PistolHandsReady(Player player, PlayerOwner owner, Player.FirearmController? controller)
        => controller != null && !controller.Destroyed && controller.Item is Pistol
            && controller.CurrentOperation is Player.FirearmController.Idling && !controller.IsInReloadOperation()
            && !player.InventoryController.IsChangingWeapon && player.ProcessStatus == Player.EProcessStatus.None
            && !player.IsSprintEnabled && !player.IsInventoryOpened && !_host.ReloadBusy && !_host.OffhandReloadPending && !_host.Faulted
            && owner.HandsInputTranslator is FirearmHandsInputTranslator translator
            && ReferenceEquals(translator._controller, controller) && PairedInput.AllowsGameplay(owner);

    // Follows the local player's native hands-changed event (no inventory polling).
    private void WatchHands()
    {
        Player? player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
        if (ReferenceEquals(player, _handsWatched)) return;
        if (_handsWatched != null) _handsWatched.OnHandsControllerChanged -= OnHandsChanged;
        _handsWatched = player;
        _autoPairArmed = _autoPairSuppressed = _pairOnNextDraw = false;
        _holsterCheckPlayer = null;
        if (player == null) return;
        player.OnHandsControllerChanged += OnHandsChanged;
        OnHandsChanged(null!, player.HandsController);
    }

    private void OnHandsChanged(Player.AbstractHandsController previous, Player.AbstractHandsController current)
    {
        _autoPairSuppressed = false;
        bool requested = _pairOnNextDraw && current is Player.FirearmController;
        if (current is Player.FirearmController) _pairOnNextDraw = false;
        _autoPairArmed = (_settings.AutoPair.Value || requested) && _settings.Enabled.Value && current is Player.FirearmController controller
            && _handsWatched != null && BothHolstersCompatible(_handsWatched, controller.Item);
        _autoPairArmedAt = Time.unscaledTime;
    }

    // Only the two genuine holster slots arm automatic pairing; a backpack pistol never does.
    private static bool BothHolstersCompatible(Player player, Item drawn)
        => drawn is Pistol && drawn.CurrentAddress?.Container?.ID == "Holster" && HolsterPairCompatible(player);

    // The equipped holster pair itself: HOLSTER pistol as primary, HOLSTERLEFT pistol as offhand.
    private static bool HolsterPairCompatible(Player player)
    {
        var right = player.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem as Pistol;
        var left = player.Equipment.Slots.FirstOrDefault(s => s.ID == LeftHolsterSlot)?.ContainedItem as Pistol;
        return right != null && left != null && left.Id != right.Id && WeaponCompatibility.GetRefusal(right, "Primary", allowMalfunction: true) == null
            && WeaponCompatibility.GetRefusal(left, "Offhand", allowMalfunction: true) == null;
    }

    private static bool InLeftHolster(Item item) => item.CurrentAddress?.Container?.ID == LeftHolsterSlot;

    // The HOLSTERLEFT pistol is in hands (selected while HOLSTER was empty) and HOLSTER now holds
    // a compatible pistol: the pair is the two holsters, so EFT's own selection draws the
    // HOLSTER pistol and automatic pairing follows on that draw. No item is moved; the left-hand
    // pistol in hands is never paired with a backpack search.
    internal bool SelectRightHolsterForPair(Player player, string reason)
    {
        if (!(player.HandsController is Player.FirearmController controller) || !InLeftHolster(controller.Item)
            || !HolsterPairCompatible(player) || player.InventoryController.IsChangingWeapon || player.IsInventoryOpened) return false;
        _autoPairSuppressed = false;
        _pairOnNextDraw = true;
        _host.LogDiagnostic("DW_HOLSTER_SELECT reason=" + reason + " left_in_hands=" + controller.Item.Id
            + " right=" + player.Equipment.GetSlot(EquipmentSlot.Holster).ContainedItem.Id);
        player.SetSlotItem(EquipmentSlot.Holster, _ => { });
        return true;
    }

    // Pairs on the first frame of the native draw (or at idle, for a pistol already in hands),
    // so the left pistol is drawn by the same mirrored native draw as the right one.
    private void TryAutoPair()
    {
        Player? player = _handsWatched;
        if (player == null || _host.Pairing || _host.Faulted) return;
        if (Time.unscaledTime - _autoPairArmedAt > 15f) { _autoPairArmed = false; return; }
        var controller = player.HandsController as Player.FirearmController;
        if (!DrawPairReady(player, controller) || !BothHolstersCompatible(player, controller!.Item)) return;
        _autoPairArmed = false;
        string operation = controller.CurrentHandsOperationName;
        _host.TryPair(null, drawing: true);
        _host.LogDiagnostic("DW_AUTO_PAIR paired=" + _host.IsPairedWith(player) + " native_operation=" + operation
            + " main=" + controller.Item.Id + " offhand=" + (_host.PairedOffhand?.Id.ToString() ?? "none"));
    }

    private bool DrawPairReady(Player player, Player.FirearmController? controller)
        => controller != null && !controller.Destroyed && controller.Item is Pistol && controller.CurrentOperation != null
            && (controller.IsInSpawnOperation() || controller.CurrentOperation is Player.FirearmController.Idling)
            && !controller.IsInReloadOperation() && player.HealthController.IsAlive && player.FirstPersonPointOfView
            // ProcessStatus stays Scheduled for the whole selection, draw included; only a
            // further queued switch (ScheduledProcess) means this draw will be abandoned.
            && !player.IsInventoryOpened && player.ScheduledProcess == null
            && !_host.ReloadBusy && !_host.OffhandReloadPending && !_host.Faulted;
}
