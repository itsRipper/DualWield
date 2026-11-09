using System;

namespace DualWieldLab;

// Whether a pair may be made with respect to co-op replication. Pure so the standalone and Fika
// cases can be tested without Unity.
public static class NetworkSessionPolicy
{
    public const string FikaCoreGuid = "com.fika.core";

    // Fika is identified only by Fika.Core's exact GUID: other mods' Fika add-ons may load in
    // standalone SPT and must not turn a local raid into a "Fika raid".
    public static bool IsFikaLoaded(Func<string, bool> isPluginLoaded) => isPluginLoaded(FikaCoreGuid);

    // In a Fika raid (even hosted alone) a left pistol must be replicated, so the pair needs the
    // integration's registered packets. The hideout range is not a network session.
    public static string? PairRefusal(bool fikaLoaded, bool hideout, bool replicationReady)
        => fikaLoaded && !hideout && !replicationReady
            ? "DualWield's Fika integration is not running (see BepInEx\\LogOutput.log): a left pistol the other players cannot see is not allowed."
            : null;
}
