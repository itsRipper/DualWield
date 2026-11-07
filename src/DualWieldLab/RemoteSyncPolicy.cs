using System;

namespace DualWieldLab;

// Replication rules of the left pistol, free of game types so they are checked offline
// (tests/OfflineChecks/RemoteSyncChecks.cs).
internal static class RemoteSyncPolicy
{
    // A state replaces the stored one unless it is an older or repeated publication of the same
    // game process. A different session is a restarted owner (reconnect), which counts from 1 again;
    // a network layer delivers one owner's packets in order, so an earlier session cannot follow a
    // later one (its process is gone before the new one starts).
    internal static bool Newer(in OffhandSnapshot incoming, in OffhandSnapshot stored)
        => incoming.Session != stored.Session || incoming.Sequence == 0 || incoming.Sequence > stored.Sequence;

    // An incoming shot: ignored when it is not newer than the stored state, so a repeated or older
    // shot never fires again. Otherwise it is replayed once and accepted even when the replay fails:
    // the shot's snapshot is authoritative for the rounds left, and the copy must follow it whether
    // or not a projectile or effect could be shown here.
    internal static bool AcceptShot(bool hasState, in OffhandSnapshot stored, in OffhandSnapshot incoming, Action replay, out Exception? replayError)
    {
        replayError = null;
        if (hasState && !Newer(incoming, stored)) return false;
        try { replay(); }
        catch (Exception e) { replayError = e; }
        return true;
    }

    internal static bool SameState(in OffhandSnapshot a, in OffhandSnapshot b)
        => a.Paired == b.Paired && a.WeaponId == b.WeaponId && a.FireMode == b.FireMode && a.MalfunctionState == b.MalfunctionState
            && a.ChamberAmmo == b.ChamberAmmo && a.MagazineId == b.MagazineId && a.MagazineCount == b.MagazineCount
            && Math.Abs(a.Durability - b.Durability) < .5f;

    // The owner publishes a state change, every action cue, and nothing before its first pair.
    internal static bool ShouldPublish(bool published, in OffhandSnapshot next, in OffhandSnapshot last)
    {
        if (!published) return next.Paired;
        return next.Action != OffhandAction.None || !SameState(next, last);
    }

    // What the owner keeps of a publication for later comparison and re-announcement: the action
    // cue is a one-shot event and is not repeated.
    internal static OffhandSnapshot Kept(OffhandSnapshot sent)
    {
        sent.Action = OffhandAction.None;
        return sent;
    }

    internal readonly struct ReconcilePlan
    {
        internal readonly bool RemoveChambered;
        internal readonly int RemoveFromMagazine;
        // The copy still differs from the owner after the removals: an inventory operation of the
        // owner (magazine swap, chambering) has not reached this copy yet, or it failed here.
        internal readonly bool Waiting;
        internal ReconcilePlan(bool removeChambered, int removeFromMagazine, bool waiting)
        { RemoveChambered = removeChambered; RemoveFromMagazine = removeFromMagazine; Waiting = waiting; }
    }

    // How the observed copy of the left pistol follows the owner's state. Only rounds the owner has
    // fired are removed: while paired, the left pistol's rounds (magazine plus chamber) change only
    // by shots, which are not inventory operations, and by native inventory operations that the
    // network layer replicates itself: a magazine swap (another magazine id) and chambering (a round
    // moved from the magazine to the chamber, the total unchanged). Unloading is blocked while
    // paired. So only an excess of the copy's total over the owner's is removed, the chambered
    // round first when the owner's chamber is empty; with another magazine, or a copy behind the
    // owner, nothing is removed and the copy waits for the operation. Rounds are never created.
    internal static ReconcilePlan Plan(string ownerMagazine, int ownerCount, bool ownerChambered,
        string copyMagazine, int copyCount, bool copyChambered)
    {
        if (ownerMagazine != copyMagazine) return new ReconcilePlan(false, 0, true);
        int excess = copyCount + (copyChambered ? 1 : 0) - ownerCount - (ownerChambered ? 1 : 0);
        bool chambered = excess > 0 && copyChambered && !ownerChambered;
        int fromMagazine = Math.Max(0, Math.Min(copyCount, excess - (chambered ? 1 : 0)));
        int countAfter = copyCount - fromMagazine;
        bool chamberAfter = copyChambered && !chambered;
        return new ReconcilePlan(chambered, fromMagazine, countAfter != ownerCount || chamberAfter != ownerChambered);
    }
}
