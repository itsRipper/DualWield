using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DualWieldLab.Fika;

// DualWield's Fika network layer. Shipped next to DualWieldLab.dll in the one DualWield package,
// but deliberately not a BepInEx plugin (no [BepInPlugin]): BepInEx never loads an assembly that
// does not reference BepInPlugin, so without Fika this assembly never enters the game. Any loaded
// assembly with unresolvable Fika references breaks the game's JSON converter discovery (it
// inspects every type of every loaded assembly), after which SPT requests are malformed and raids
// cannot start. The runtime (FikaIntegrationLoader) loads it only once a supported Fika is loaded.
// Keep this assembly free of any reference to BepInEx.BepInPlugin (also indirectly, e.g. through
// PluginInfo.Metadata): that type reference is what makes BepInEx inspect an assembly.
public sealed class FikaIntegration : MonoBehaviour
{
    private FikaBridge? _bridge;
    private Harmony? _harmony;
    private ManualLogSource? _log;

    // The runtime contract this integration is built against.
    private const int RequiredApi = 3;

    private void Awake()
    {
        _log = BepInEx.Logging.Logger.CreateLogSource("DualWield Fika");
        try
        {
            if (DualWieldNetwork.ApiVersion != RequiredApi)
                throw new InvalidOperationException("DualWieldLab.dll has network API " + DualWieldNetwork.ApiVersion + ", DualWieldLab.Fika.dll needs " + RequiredApi
                    + ". Both files must come from the same DualWield package.");
            _harmony = new Harmony("itsripper.dualwield.fika");
            ObservedPass.Install(_harmony);
            _bridge = new FikaBridge(_log);
            _log.LogInfo("DualWield Fika integration loaded (protocol " + DualWieldProtocol.Version + ").");
        }
        catch (Exception e)
        {
            _harmony?.UnpatchSelf();
            DualWieldNetwork.ReplicationReady = false;
            _log.LogError("DualWield Fika integration not started, dual wielding stays off in Fika raids: "
                + (e is InvalidOperationException ? e.Message : e.ToString()));
            enabled = false;
        }
    }

    private void Update() => _bridge?.Update();
}
