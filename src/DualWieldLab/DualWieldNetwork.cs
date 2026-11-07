using System;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

// The left pistol of a dual-wield pair as other machines need it. The owning machine publishes
// its own pair; a network integration (DualWieldLab.Fika) carries the data and hands it to the
// observed copy of that player. This assembly references no network library.
//
// Native EFT inventory transactions the pair runs (magazine swaps, chambering) are already
// replicated by the network layer's inventory synchronisation. What is not: rounds the left
// pistol fires (native Shot consumes them without an inventory operation, as on any hands
// controller), its malfunction, heat and wear, its fire mode, and the fact that a second pistol
// is held at all. Those travel as a state snapshot of the left weapon.
// OffhandAction and OffhandSnapshot: OffhandSnapshot.cs.

public struct OffhandShot
{
    public Vector3 Position;
    public Vector3 Direction;
    public string AmmoTemplate;
    public float Overheat;
}

public static class DualWieldNetwork
{
    // The contract a network add-on is built against (this class and the snapshot fields). A
    // property, not a constant, so an add-on reads the value of the runtime actually installed.
    public static int ApiVersion => 3;

    // For network layers whose observed players do not run Player.VisualPass (Fika's ObservedPlayer
    // runs its own third-person pass, with the same IkProcess/IkApply): call these around that pass.
    // Before restores the right pistol's native pose, after places the left pistol and arm.
    public static void BeforeObservedPass(Player player) => RemoteDualPistols.BeforeVisualPass(player);

    public static void AfterObservedPass(Player player) => RemoteDualPistols.AfterPass(player, "observed pass");

    // Raised on the owning machine. The shot event carries the snapshot taken after the shot.
    public static event Action<OffhandSnapshot>? LocalStateChanged;
    public static event Action<OffhandSnapshot, OffhandShot>? LocalShot;

    // Set by a network integration once its packets are registered. In a cooperative session the
    // pair is refused without it: a second weapon that only one machine knows about would fire
    // unseen rounds and leave every other copy of the inventory with ammunition it no longer has.
    public static bool ReplicationReady { get; set; }

    internal static Action<string>? Log;

    // This machine's current state for peers that start their raid, reconnect or ask: the last
    // published snapshot (without its action cue), or "not paired" when nothing was published. Sent
    // even when not paired, so a peer that still holds this player's state from before a reconnect
    // drops a pair that ended.
    public static OffhandSnapshot CurrentLocalState()
    {
        if (_published) return _lastPublished;
        return new OffhandSnapshot { WeaponId = "", ChamberAmmo = "", MagazineId = "", Session = SessionId, Sequence = ++_sequence };
    }

    // Per game process: never 0, so a default (empty) snapshot never matches a real session.
    internal static readonly int SessionId = new System.Random().Next(1, int.MaxValue);

    public static void ApplyRemoteState(Player player, OffhandSnapshot snapshot)
    {
        if (player == null || player.IsYourPlayer) return;
        try { RemoteDualPistols.ApplyState(player, snapshot); }
        catch (Exception e) { Log?.Invoke("DW_NET remote state failed for " + player.ProfileId + ": " + e); }
    }

    public static void ApplyRemoteShot(Player player, OffhandSnapshot after, OffhandShot shot)
    {
        if (player == null || player.IsYourPlayer) return;
        try { RemoteDualPistols.ApplyShot(player, after, shot); }
        catch (Exception e) { Log?.Invoke("DW_NET remote shot failed for " + player.ProfileId + ": " + e); }
    }

    public static void ForgetRemote(Player player) => RemoteDualPistols.Release(player);

    public static void ForgetAllRemote() => RemoteDualPistols.ReleaseAll();

    private static OffhandSnapshot _lastPublished;
    private static bool _published;
    private static int _sequence;

    internal static bool HasSubscribers => LocalStateChanged != null || LocalShot != null;

    internal static OffhandSnapshot Capture(Weapon? left, bool paired, OffhandAction action)
    {
        var snapshot = new OffhandSnapshot { WeaponId = "", ChamberAmmo = "", MagazineId = "", Paired = paired && left != null, Action = action, Session = SessionId };
        if (left == null) return snapshot;
        snapshot.WeaponId = left.Id.ToString();
        snapshot.FireMode = (byte)left.SelectedFireMode;
        snapshot.MalfunctionState = (byte)left.MalfState.State;
        snapshot.ChamberAmmo = left.Chambers.Length > 0 && left.Chambers[0].ContainedItem is Ammo ammo && !ammo.IsUsed ? ammo.TemplateId.ToString() : "";
        Magazine? magazine = left.GetCurrentMagazine();
        snapshot.MagazineId = magazine != null ? magazine.Id.ToString() : "";
        snapshot.MagazineCount = magazine?.Count ?? 0;
        snapshot.Durability = left.Repairable?.Durability ?? 0f;
        return snapshot;
    }

    // Publishes when the pair or the left pistol changed since the last publication. Comparing a
    // handful of values runs every frame; a packet is sent only for a real change.
    internal static void PublishIfChanged(Weapon? left, bool paired, OffhandAction action = OffhandAction.None)
    {
        if (!HasSubscribers) return;
        OffhandSnapshot snapshot = Capture(left, paired, action);
        if (!RemoteSyncPolicy.ShouldPublish(_published, snapshot, _lastPublished)) return;
        snapshot.Sequence = ++_sequence;
        _published = true;
        // The action cue goes out with this publication only; the kept state (re-announced on
        // request or reconnect) carries none.
        _lastPublished = RemoteSyncPolicy.Kept(snapshot);
        LocalStateChanged?.Invoke(snapshot);
    }

    internal static void PublishShot(Weapon left, OffhandShot shot)
    {
        if (!HasSubscribers) return;
        OffhandSnapshot snapshot = Capture(left, paired: true, OffhandAction.None);
        snapshot.Sequence = ++_sequence;
        _lastPublished = snapshot;
        _published = true;
        LocalShot?.Invoke(snapshot, shot);
    }

    internal static void ResetLocal()
    {
        _published = false;
        _lastPublished = default;
    }

}
