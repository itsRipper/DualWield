namespace DualWieldLab;

// The left pistol of a dual-wield pair as other machines need it (see DualWieldNetwork). Plain data
// only, no game types, so the replication rules (RemoteSyncPolicy) can be checked offline.
public enum OffhandAction : byte
{
    None = 0,
    Reload = 1,
    MagazineCheck = 2,
    ChamberCheck = 3,
    Examine = 4,
    FireModeCheck = 5,
    FireModeChange = 6,
    Malfunction = 7,
}

public struct OffhandSnapshot
{
    // The pistol's own item id; empty while not paired.
    public string WeaponId;
    public bool Paired;
    public byte FireMode;
    public byte MalfunctionState;
    // Template id of the chambered round, empty for an empty chamber.
    public string ChamberAmmo;
    // The magazine in the pistol (empty when none) and its round count.
    public string MagazineId;
    public int MagazineCount;
    public float Durability;
    // A one-shot cue: the action that started with this publication. It is sent once and never
    // repeated by later publications or re-announcements of the same state.
    public OffhandAction Action;
    // Increases with every publication of the owner; a receiver ignores older ones of the same
    // Session. A restarted game (reconnect) starts a new Session and counts from 1 again.
    public int Sequence;
    public int Session;
}
