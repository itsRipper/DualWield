using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using UnityEngine;

namespace DualWieldLab;

// Starts DualWield's Fika network layer (DualWieldLab.Fika.dll, shipped next to this DLL) when
// Fika is loaded, and does nothing in standalone SPT. That assembly references Fika.Core and must
// never be loaded without it (see DualWieldLab.Fika.FikaIntegration); it is not a BepInEx plugin,
// so BepInEx neither loads it nor reports a missing dependency. Only reflection is used here: this
// assembly references no Fika type.
internal static class FikaIntegrationLoader
{
    internal const string FikaGuid = NetworkSessionPolicy.FikaCoreGuid;
    internal const string FileName = "DualWieldLab.Fika.dll";
    internal const string EntryType = "DualWieldLab.Fika.FikaIntegration";
    // The Fika release line the integration is built and tested against (Fika.Core 2.4.x, SPT 4.1).
    internal static readonly Version TestedFika = new Version(2, 4);

    // Called from the runtime's Start, after every plugin's Awake: BepInEx lists a plugin in
    // PluginInfos only when it loads it, so during this plugin's Awake Fika may not be listed yet.
    internal static void TryStart(GameObject host, ManualLogSource log)
    {
        if (!Chainloader.PluginInfos.TryGetValue(FikaGuid, out PluginInfo fika) || fika.Instance == null) return;
        Version version = fika.Metadata.Version;
        if (version.Major != TestedFika.Major || version.Minor != TestedFika.Minor)
        {
            // Never load the Fika-dependent assembly against a Fika it was not built for.
            DualWieldNetwork.ReplicationReady = false;
            log.LogError("Fika " + version + " is installed; this DualWield build supports Fika " + TestedFika + ".x (SPT 4.1). Dual wielding stays off in Fika raids.");
            return;
        }
        string path = Path.Combine(Path.GetDirectoryName(typeof(FikaIntegrationLoader).Assembly.Location) ?? "", FileName);
        if (!File.Exists(path))
        {
            log.LogError("Fika is installed but " + FileName + " is missing next to DualWieldLab.dll; dual wielding stays off in Fika raids. Reinstall the DualWield package.");
            return;
        }
        try
        {
            Type entry = Assembly.LoadFile(path).GetType(EntryType, throwOnError: true);
            host.AddComponent(entry);
        }
        catch (Exception e)
        {
            DualWieldNetwork.ReplicationReady = false;
            log.LogError("DualWield's Fika integration could not be loaded; dual wielding stays off in Fika raids: " + e);
        }
    }
}
