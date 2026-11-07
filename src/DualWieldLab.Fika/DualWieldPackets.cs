using Fika.Core.Networking.LiteNetLib.Utils;
using UnityEngine;

namespace DualWieldLab.Fika;

// Wire format of the left pistol of a dual-wield pair. Every peer that receives these must run the
// same protocol version; a packet of another version is read completely and then ignored.
internal static class DualWieldProtocol
{
    internal const byte Version = 2;
}

// The owner's left-pistol snapshot, with the shot that produced it when there was one.
internal struct DualWieldStatePacket : INetSerializable
{
    public byte Version;
    public int NetId;
    public OffhandSnapshot State;
    public bool HasShot;
    public OffhandShot Shot;

    public void Serialize(NetDataWriter writer)
    {
        writer.Put(Version);
        writer.Put(NetId);
        writer.Put(State.WeaponId ?? "");
        writer.Put(State.Paired);
        writer.Put(State.FireMode);
        writer.Put(State.MalfunctionState);
        writer.Put(State.ChamberAmmo ?? "");
        writer.Put(State.MagazineId ?? "");
        writer.Put(State.MagazineCount);
        writer.Put(State.Durability);
        writer.Put((byte)State.Action);
        writer.Put(State.Sequence);
        writer.Put(State.Session);
        writer.Put(HasShot);
        if (!HasShot) return;
        PutVector(writer, Shot.Position);
        PutVector(writer, Shot.Direction);
        writer.Put(Shot.AmmoTemplate ?? "");
        writer.Put(Shot.Overheat);
    }

    public void Deserialize(NetDataReader reader)
    {
        Version = reader.GetByte();
        NetId = reader.GetInt();
        State = new OffhandSnapshot
        {
            WeaponId = reader.GetString(),
            Paired = reader.GetBool(),
            FireMode = reader.GetByte(),
            MalfunctionState = reader.GetByte(),
            ChamberAmmo = reader.GetString(),
            MagazineId = reader.GetString(),
            MagazineCount = reader.GetInt(),
            Durability = reader.GetFloat(),
            Action = (OffhandAction)reader.GetByte(),
            Sequence = reader.GetInt(),
            Session = reader.GetInt(),
        };
        HasShot = reader.GetBool();
        Shot = default;
        if (!HasShot) return;
        Shot.Position = GetVector(reader);
        Shot.Direction = GetVector(reader);
        Shot.AmmoTemplate = reader.GetString();
        Shot.Overheat = reader.GetFloat();
    }

    private static void PutVector(NetDataWriter writer, Vector3 v) { writer.Put(v.x); writer.Put(v.y); writer.Put(v.z); }
    private static Vector3 GetVector(NetDataReader reader) => new Vector3(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());
}

// Sent by a peer when its raid starts (also after a reconnect): every owner answers with its
// current snapshot.
internal struct DualWieldStateRequestPacket : INetSerializable
{
    public byte Version;
    public int NetId;

    public void Serialize(NetDataWriter writer) { writer.Put(Version); writer.Put(NetId); }

    public void Deserialize(NetDataReader reader) { Version = reader.GetByte(); NetId = reader.GetInt(); }
}
