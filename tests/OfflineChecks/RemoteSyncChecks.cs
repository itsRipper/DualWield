using DualWieldLab;

// Behaviour of the left-pistol replication rules (RemoteSyncPolicy), executed on the linked source:
// ordering of snapshots and shots, the one-shot action cue, and how the observed copy of the left
// pistol follows the owner without losing or inventing rounds when Fika's inventory operations
// arrive late or fail on that copy.
internal static class RemoteSyncChecks
{
    private static OffhandSnapshot S(int session, int sequence, int count = 10, bool chambered = true, string magazine = "m1",
        OffhandAction action = OffhandAction.None, bool paired = true)
        => new OffhandSnapshot { WeaponId = "w", Paired = paired, ChamberAmmo = chambered ? "ammo" : "", MagazineId = magazine,
            MagazineCount = count, Durability = 100f, Action = action, Session = session, Sequence = sequence };

    internal static void Run(Action<string, bool> check)
    {
        // --- Ordering (findings 1, 2, 5) ---
        check("Sync order: a later publication of the same session replaces the stored state", RemoteSyncPolicy.Newer(S(7, 5), S(7, 4)));
        check("Sync order: a repeated publication (re-announcement, same sequence) is not applied again", !RemoteSyncPolicy.Newer(S(7, 5), S(7, 5)));
        check("Sync order: an older publication of the same session is ignored", !RemoteSyncPolicy.Newer(S(7, 3), S(7, 5)));
        check("Sync order: a restarted owner (new session) counting from 1 again is accepted", RemoteSyncPolicy.Newer(S(9, 1), S(7, 50)));

        // One owner's stream as a receiver sees it (in order, with re-announcements, a reconnect):
        // the stored state ends at the newest publication and every shot is replayed exactly once.
        var stream = new (OffhandSnapshot State, bool Shot)[]
        {
            (S(7, 1, 20), false), (S(7, 2, 19), true), (S(7, 2, 19), false), (S(7, 3, 18), true),
            (S(7, 3, 18), true), // a shot delivered twice
            (S(7, 2, 19), true), // an older shot
            (S(9, 1, 18, paired: false), false), (S(9, 2, 18), false), (S(9, 3, 17), true), (S(9, 3, 17), false),
        };
        OffhandSnapshot stored = default; bool has = false; int replayed = 0;
        foreach (var (state, shot) in stream)
        {
            if (has && !RemoteSyncPolicy.Newer(state, stored)) continue;
            if (shot) replayed++;
            stored = state; has = true;
        }
        check("Sync order: duplicate and older shots are not replayed; the stream ends at the newest state (3 replays, session 9 seq 3, 17 rounds)",
            replayed == 3 && stored.Session == 9 && stored.Sequence == 3 && stored.MagazineCount == 17);

        // --- Shot replay is contained: a failed replay never loses the shot's state ---
        int attempts = 0;
        bool accepted = RemoteSyncPolicy.AcceptShot(true, S(7, 4, 18), S(7, 5, 17), () => { attempts++; throw new InvalidOperationException("no projectile"); }, out Exception? error);
        check("Shot replay: a replay that throws still accepts the shot's snapshot (rounds reconciled) and reports the error",
            accepted && attempts == 1 && error is InvalidOperationException);
        accepted = RemoteSyncPolicy.AcceptShot(true, S(7, 5, 17), S(7, 5, 17), () => attempts++, out error);
        check("Shot replay: the same shot delivered again is neither replayed nor accepted", !accepted && attempts == 1 && error == null);
        accepted = RemoteSyncPolicy.AcceptShot(false, default, S(7, 1, 19), () => attempts++, out error);
        check("Shot replay: the first shot of a player not seen before is replayed and accepted", accepted && attempts == 2);

        // A burst where every second replay fails (e.g. no audio or effects on this machine): every
        // shot is replayed exactly once and the stored state, hence the copy's rounds, ends at the owner's.
        stored = S(7, 1, 20); attempts = 0; int failures = 0;
        foreach (var next in new[] { S(7, 2, 19), S(7, 3, 18), S(7, 3, 18), S(7, 4, 17), S(7, 5, 16) })
        {
            bool fail = attempts % 2 == 1;
            if (RemoteSyncPolicy.AcceptShot(true, stored, next, () => { attempts++; if (fail) throw new Exception("replay"); }, out error))
                stored = next;
            if (error != null) failures++;
        }
        check("Shot replay: with failing replays every fresh shot is replayed once and the state ends at the owner's (4 replays, 2 failed, 16 rounds)",
            attempts == 4 && failures == 2 && stored.Sequence == 5 && stored.MagazineCount == 16);

        // --- Action cue (finding 4) ---
        var reload = S(7, 4, 20, action: OffhandAction.Reload);
        var kept = RemoteSyncPolicy.Kept(reload);
        check("Action cue: an action is published even when the pistol state is unchanged", RemoteSyncPolicy.ShouldPublish(true, reload, RemoteSyncPolicy.Kept(S(7, 3, 20))));
        check("Action cue: the kept state carries no action, so a re-announcement never repeats it", kept.Action == OffhandAction.None && kept.MagazineCount == 20);
        check("Action cue: the next unchanged frame publishes nothing", !RemoteSyncPolicy.ShouldPublish(true, S(7, 0, 20), kept));
        check("Action cue: the same action again is a new cue and is published", RemoteSyncPolicy.ShouldPublish(true, S(7, 0, 20, action: OffhandAction.Reload), kept));
        check("Action cue: nothing is published before the first pair; an unpair after it is",
            !RemoteSyncPolicy.ShouldPublish(false, S(7, 0, paired: false), default) && RemoteSyncPolicy.ShouldPublish(true, S(7, 0, paired: false), kept));

        // --- Observed copy follows the owner (finding 3) ---
        check("Copy: one shot removes one round from the magazine (chamber refed)", Is(Plan(10, true, 11, true), false, 1, false));
        check("Copy: the last chambered round fired empties the chamber", Is(Plan(0, false, 0, true), true, 0, false));
        check("Copy: several shots in one update remove that many rounds", Is(Plan(7, true, 11, true), false, 4, false));
        check("Copy: chambering not yet applied here removes nothing and waits (round moves, total unchanged)", Is(Plan(9, true, 10, false), false, 0, true));
        check("Copy: chambering not yet applied plus a shot removes exactly the fired round and waits", Is(Plan(8, true, 10, false), false, 1, true));
        check("Copy: another magazine (swap not applied here) removes nothing and waits", Is(RemoteSyncPolicy.Plan("m2", 20, true, "m1", 3, true), false, 0, true));
        check("Copy: a copy behind the owner is never topped up and waits", Is(Plan(10, true, 8, true), false, 0, true));
        check("Copy: an equal copy changes nothing", Is(Plan(10, true, 10, true), false, 0, false));
        check("Copy: never removes more rounds than the copy holds", Plan(0, false, 1, false).RemoveFromMagazine <= 1);

        check("Copy race: a late chambering operation ends exactly at the owner's rounds, never below", LateChamberingEndsInSync());
        check("Copy race: a chambering operation that never arrives costs no round (copy total never below the owner's)", LostChamberingKeepsRounds());
    }

    private static RemoteSyncPolicy.ReconcilePlan Plan(int ownerCount, bool ownerChambered, int copyCount, bool copyChambered)
        => RemoteSyncPolicy.Plan("m1", ownerCount, ownerChambered, "m1", copyCount, copyChambered);

    private static bool Is(RemoteSyncPolicy.ReconcilePlan plan, bool chambered, int fromMagazine, bool waiting)
        => plan.RemoveChambered == chambered && plan.RemoveFromMagazine == fromMagazine && plan.Waiting == waiting;

    // The copy's left pistol (magazine count, chamber), changed only by the plan's removals and by
    // the replicated chambering operation (one round from the magazine into an empty chamber).
    private sealed class Copy
    {
        internal int Count; internal bool Chambered;
        internal void Follow(int ownerCount, bool ownerChambered)
        {
            var plan = RemoteSyncPolicy.Plan("m1", ownerCount, ownerChambered, "m1", Count, Chambered);
            if (plan.RemoveChambered) Chambered = false;
            Count -= plan.RemoveFromMagazine;
        }
        internal void Chamber() { if (!Chambered && Count > 0) { Count--; Chambered = true; } }
        internal int Total => Count + (Chambered ? 1 : 0);
    }

    // Owner: 10 rounds, empty chamber; chambers (operation T), then fires twice. The copy receives
    // the three snapshots first and T last (retried as the runtime does while waiting).
    private static bool LateChamberingEndsInSync()
    {
        var copy = new Copy { Count = 10, Chambered = false };
        bool neverBelow = true;
        foreach (var (count, chambered) in new[] { (9, true), (8, true), (7, true) })
        {
            copy.Follow(count, chambered);
            neverBelow &= copy.Total >= count + 1;
        }
        copy.Chamber();
        copy.Follow(7, true);
        return neverBelow && copy.Count == 7 && copy.Chambered;
    }

    // As above, but the chambering operation fails on this copy: the copy keeps the round the owner
    // still has (in the magazine instead of the chamber) and stays reported as waiting.
    private static bool LostChamberingKeepsRounds()
    {
        var copy = new Copy { Count = 10, Chambered = false };
        foreach (var (count, chambered) in new[] { (9, true), (8, true), (7, true) }) copy.Follow(count, chambered);
        var plan = RemoteSyncPolicy.Plan("m1", 7, true, "m1", copy.Count, copy.Chambered);
        return copy.Total == 8 && plan.Waiting && plan.RemoveFromMagazine == 0 && !plan.RemoveChambered;
    }
}
