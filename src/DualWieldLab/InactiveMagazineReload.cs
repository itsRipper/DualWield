using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EFT;
using EFT.InventoryLogic;

namespace DualWieldLab;

// The inventory UI's ReloadWeapon route, not FirearmController.ReloadMag: a Swap with the installed
// magazine, or a Move into an empty well. The native operation owns validation, resize,
// synchronization and rollback; no weapon address changes. The chamber is fed as EFT does (NativeFeed).
internal sealed class InactiveMagazineReload
{
    // Shown to the player as is: the one refusal with an obvious fix.
    internal const string NoSpareMagazine = ReloadFeedback.LeftNoSpare;

    private sealed class Plan
    {
        internal Weapon Main = null!;
        internal Magazine? Old;
        internal Magazine Next = null!;
        internal Ammo? Chamber;
        internal bool Feeds;
        internal ItemAddress MainAddress = null!, OffhandAddress = null!, SlotAddress = null!;
        internal GridItemAddress? Destination;
        internal Func<Task<bool>> SubmitSwap = null!;
    }

    private bool _started;
    private bool _awaitingNativeResult;
    private TaskCompletionSource<bool>? _chamberGate;
    internal bool Started => _started;
    internal bool Pending { get; private set; }
    internal bool Finished { get; private set; }
    internal bool Interrupted { get; private set; }
    internal bool NativeSucceeded { get; private set; }
    internal bool Verified { get; private set; }
    internal bool Submitted { get; private set; }
    internal bool MagazineVerified { get; private set; }
    internal bool SwapSettled { get; private set; }
    internal bool ChamberNeeded { get; private set; }
    // No magazine was installed: the next one is inserted.
    internal bool Insert { get; private set; }
    internal bool AwaitingChamberRelease => _chamberGate != null && !_chamberGate.Task.IsCompleted && SwapSettled;
    internal bool CompletionUncertain { get; private set; }
    internal string Message { get; private set; } = "";

    // A timed presentation holds the separate chamber transaction until its slide event.
    internal void UseChamberGate() => _chamberGate ??= new TaskCompletionSource<bool>();
    internal void ReleaseChamber() => _chamberGate?.TrySetResult(true);

    internal void Interrupt(string reason)
    {
        Interrupted = true;
        Message = reason + "; any submitted native transaction must settle; no retry or forced rollback.";
        _chamberGate?.TrySetResult(false);
    }

    // Same validation and native simulation as submission; nothing is submitted.
    // next: the magazine the native simulation would install (its clip's magazine type).
    internal static string? Preflight(Player player, Player.FirearmController controller, Weapon offhand, out bool chamberNeeded, out Magazine? next)
    {
        chamberNeeded = false; next = null;
        try
        {
            var plan = Prepare(player, controller, offhand);
            chamberNeeded = plan.Feeds;
            next = plan.Next;
            return null;
        }
        catch (InvalidOperationException e) { return e.Message; }
    }

    private static Plan Prepare(Player player, Player.FirearmController controller, Weapon offhand)
    {
        var inventory = player.InventoryController;
        Weapon main = controller.Item;
        // UtilityOperation (magazine/chamber check, examine) derives from Idling.
        if (player.HandsController != controller || controller.Destroyed
            || controller.CurrentOperation is not Player.FirearmController.Idling
            || controller.CurrentOperation is Player.FirearmController.UtilityOperation
            || ReferenceEquals(main, offhand) || main.Id == offhand.Id
            || !ReferenceEquals(main.Owner, inventory) || !ReferenceEquals(offhand.Owner, inventory)
            || offhand.MalfState.State != Weapon.EMalfunctionState.None)
            throw new InvalidOperationException("Native controller, ownership or malfunction validation refused left reload.");
        if (offhand.Chambers.Length != 1
            || (offhand.Chambers[0].ContainedItem != null && offhand.Chambers[0].ContainedItem is not Ammo)
            || offhand.Chambers[0].ContainedItem is Ammo spent && spent.IsUsed)
            throw new InvalidOperationException("Native chamber configuration is unsupported or contains a used cartridge.");
        Magazine? old = offhand.GetCurrentMagazine();
        if (!inventory.Examined(offhand) || (old != null && (!inventory.Examined(old) || old.PinLockState == EItemPinLockState.Locked)))
            throw new InvalidOperationException("Native existing weapon/magazine examination or lock check refused reload.");
        var mainAddress = main.CurrentAddress;
        var offhandAddress = offhand.CurrentAddress;
        if (mainAddress == null || offhandAddress == null) throw new InvalidOperationException("Native weapon addresses unavailable.");
        Slot slot = offhand.GetMagazineSlot();
        // Like EFT's reload: the fullest reachable magazine that fits the slot, compatible rounds or not.
        var magazines = new List<Magazine>(32);
        inventory.GetReachableItemsOfTypeNonAlloc(magazines, m => m.Count > 0);
        magazines.Sort((a,b) => b.Count.CompareTo(a.Count));
        Magazine? next = magazines.FirstOrDefault(m => !ReferenceEquals(m, old)
            && m.CurrentAddress is GridItemAddress && ReferenceEquals(m.Owner, inventory)
            && inventory.Examined(m) && m.PinLockState != EItemPinLockState.Locked
            && slot.CanAccept(m));
        if (next == null) throw new InvalidOperationException(NoSpareMagazine);
        var slotAddress = slot.CreateItemAddress();
        var chamber = offhand.Chambers[0].ContainedItem as Ammo;
        var plan = new Plan { Main = main, Old = old, Next = next, Chamber = chamber,
            Feeds = NativeFeed.Feeds(chamber == null, next.Count, next.IsAmmoCompatible(offhand.Chambers)),
            MainAddress = mainAddress, OffhandAddress = offhandAddress, SlotAddress = slotAddress };
        if (old == null)
        {
            var insert = ItemManipulator.Move(next, slotAddress, inventory, simulate: true);
            if (insert.Failed) throw new InvalidOperationException("Native magazine-insert simulation declined: " + insert.Error);
            if (!insert.Value.CanExecute(inventory)) throw new InvalidOperationException("Native magazine operation is not executable; nothing submitted.");
            plan.SubmitSwap = async () => (await inventory.TryRunNetworkTransaction(insert)).Succeed;
            return plan;
        }
        plan.Destination = player.Equipment.GetPrioritizedGridsForUnloadedObject(backpackIncluded: true)
            .Select(g => g.FindLocationForItem(old)).Where(a => a != null)
            .OrderBy(a => a.Grid.GridWidth * a.Grid.GridHeight).FirstOrDefault();
        if (plan.Destination == null) throw new InvalidOperationException("No native storage for the old magazine; nothing dropped or moved.");
        var operation = ItemManipulator.Swap(old, plan.Destination, next, slotAddress, inventory, simulate: true);
        if (operation.Failed) throw new InvalidOperationException("Native magazine-swap simulation declined: " + operation.Error);
        if (!operation.Value.CanExecute(inventory)) throw new InvalidOperationException("Native magazine operation is not executable; nothing submitted.");
        plan.SubmitSwap = async () => (await inventory.TryRunNetworkTransaction(operation)).Succeed;
        return plan;
    }

    internal async void Begin(Player player, Player.FirearmController controller, Weapon offhand, Action<string> trace)
    {
        if (_started) return;
        _started = true;
        try
        {
            if (Interrupted) throw new InvalidOperationException("Reload interrupted before native submission.");
            var inventory = player.InventoryController;
            var plan = Prepare(player, controller, offhand);
            Weapon main = plan.Main;
            Magazine? old = plan.Old;
            Magazine next = plan.Next;
            Ammo? chamber = plan.Chamber;
            ChamberNeeded = plan.Feeds;
            Insert = old == null;
            var mainAddress = plan.MainAddress; var offhandAddress = plan.OffhandAddress;
            var slotAddress = plan.SlotAddress; var destination = plan.Destination;
            int oldCount = old?.Count ?? 0, newCount = next.Count, mainAmmo = RuntimePlugin.CountAmmo(main);
            string mainId = main.Id.ToString(), offhandId = offhand.Id.ToString();
            string oldId = old?.Id.ToString() ?? "", newId = next.Id.ToString();
            trace("DW_OFFHAND_RELOAD step=submit mode=" + (Insert ? "insert" : "swap") + " old_mag=" + (Insert ? "none" : oldId)
                + " next_mag=" + newId + " chamber_feed=" + ChamberNeeded
                + " weapon_moves=0 main_address=" + NativeReloadService.DescribeAddress(mainAddress)
                + " offhand_address=" + NativeReloadService.DescribeAddress(offhandAddress));
            Pending = Submitted = true;
            _awaitingNativeResult = true;
            NativeSucceeded = await plan.SubmitSwap();
            _awaitingNativeResult = false;
            // The canonical inventory magazine operation deliberately preserves
            // the chamber. Verify that first before considering a separate feed.
            bool oldStored = old == null || (ReferenceEquals(old.Owner, inventory)
                && NativeReloadService.SameAddress(old.CurrentAddress, destination) && old.Count == oldCount);
            MagazineVerified = NativeSucceeded && ReferenceEquals(offhand.GetCurrentMagazine(), next)
                && oldStored && ReferenceEquals(next.Owner, inventory) && next.Count == newCount
                && ReferenceEquals(offhand.Chambers[0].ContainedItem, chamber)
                && NativeReloadService.SameAddress(main.CurrentAddress, mainAddress)
                && NativeReloadService.SameAddress(offhand.CurrentAddress, offhandAddress);
            SwapSettled = true;
            bool fed = false;
            string? fedTemplate = null;
            if (MagazineVerified && ChamberNeeded && !Interrupted)
            {
                if (_chamberGate != null)
                {
                    trace("DW_OFFHAND_RELOAD step=chamber_wait_for_slide weapon_moves=0");
                    await _chamberGate.Task;
                }
                if (Interrupted) throw new InvalidOperationException("Reload interrupted before native chamber submission; magazine remains installed.");
                if (player == null || !player.HealthController.IsAlive || !player.FirstPersonPointOfView
                    || player.HandsController != controller || controller.Destroyed || player.IsInventoryOpened
                    || controller.CurrentOperation is not Player.FirearmController.Idling
                    || !ReferenceEquals(player.InventoryController, inventory)
                    || !ReferenceEquals(offhand.Owner, inventory))
                    throw new InvalidOperationException("Native context changed between magazine and chamber transactions.");
                Ammo source = next.Cartridges.Last as Ammo
                    ?? throw new InvalidOperationException("The installed native magazine has no next live cartridge.");
                if (source.IsUsed) throw new InvalidOperationException("Native next cartridge is used; feed refused.");
                fedTemplate = source.StringTemplateId;
                var feed = Ammo.ApplyToAddress(source, offhand.Chambers[0].CreateItemAddress(), 1, inventory, simulate: true);
                if (feed.Failed) throw new InvalidOperationException("Magazine installed, but native chamber preflight declined: " + feed.Error);
                if (!feed.Value.CanExecute(inventory)) throw new InvalidOperationException("Magazine installed, but native chamber operation is not executable.");
                if (Interrupted) throw new InvalidOperationException("Reload interrupted before native chamber submission; magazine remains installed.");
                trace("DW_OFFHAND_RELOAD step=chamber_submit source_ammo=" + source.Id + " count=1 weapon_moves=0");
                _awaitingNativeResult = true;
                var feedResult = await inventory.TryRunNetworkTransaction(feed);
                _awaitingNativeResult = false;
                fed = feedResult.Succeed;
                if (!fed) throw new InvalidOperationException("Magazine installed, but native chamber transaction declined: " + feedResult.Error);
            }
            bool identities = main.Id.ToString() == mainId && offhand.Id.ToString() == offhandId
                && (old?.Id.ToString() ?? "") == oldId && next.Id.ToString() == newId && oldId != newId;
            bool ownership = ReferenceEquals(main.Owner, inventory) && ReferenceEquals(offhand.Owner, inventory)
                && (old == null || ReferenceEquals(old.Owner, inventory)) && ReferenceEquals(next.Owner, inventory);
            bool addresses = NativeReloadService.SameAddress(main.CurrentAddress, mainAddress)
                && NativeReloadService.SameAddress(offhand.CurrentAddress, offhandAddress);
            bool installed = ReferenceEquals(offhand.GetCurrentMagazine(), next)
                && NativeReloadService.SameAddress(next.CurrentAddress, slotAddress)
                && (old == null || NativeReloadService.SameAddress(old.CurrentAddress, destination));
            Ammo? finalChamber = offhand.Chambers[0].ContainedItem as Ammo;
            // Not fed (no compatible round to take): the chamber stays as it was.
            bool chamberCorrect = !ChamberNeeded ? ReferenceEquals(finalChamber, chamber)
                : fed && finalChamber != null && finalChamber.StringTemplateId == fedTemplate
                    && ReferenceEquals(finalChamber.Owner, inventory) && finalChamber.StackObjectsCount == 1;
            bool chamberLive = finalChamber == null || (!finalChamber.IsUsed && ReferenceEquals(finalChamber.Owner, inventory)
                && NativeReloadService.SameAddress(finalChamber.CurrentAddress, offhand.Chambers[0].CreateItemAddress()));
            bool counts = MagazineReplacementAudit.Counts(oldCount, newCount, chamber == null ? 0 : 1,
                old?.Count ?? 0, next.Count, offhand.ChamberAmmoCount, next.IsAmmoCompatible(offhand.Chambers))
                && chamberCorrect && chamberLive;
            Verified = MagazineReplacementAudit.Verified(NativeSucceeded, identities, ownership, addresses,
                installed, counts, Interrupted || RuntimePlugin.CountAmmo(main) == mainAmmo);
            Message = Verified ? "Native left magazine reload verified; native chamber ready and both pistol addresses unchanged."
                : "Native left magazine result could not be fully verified; inspect inventory. No retry, refund or forced rollback.";
            trace("DW_OFFHAND_RELOAD step=settled native_success=" + NativeSucceeded + " verified=" + Verified
                + " interrupted=" + Interrupted + " pistol_addresses_unchanged=" + addresses
                + " old_count=" + (old?.Count ?? 0) + " next_count=" + next.Count + " chamber_fed=" + fed + " ammo_verified=" + counts + " weapon_moves=0");
        }
        catch (Exception e)
        {
            Verified = false;
            CompletionUncertain = _awaitingNativeResult;
            Message = "Left reload " + (Submitted ? "stopped after native submission: " : "refused before submission: ") + e.Message;
            if (CompletionUncertain) Message += " Native completion is unknown; restart the test session before pairing.";
            trace("DW_OFFHAND_RELOAD step=failed submitted=" + Submitted + " interrupted=" + Interrupted
                + " completion_uncertain=" + CompletionUncertain + " reason=" + e.Message);
        }
        finally { SwapSettled = true; Pending = false; Finished = true; }
    }
}
