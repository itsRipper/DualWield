using DualWieldLab;

// Whether the pair-time network guard fires, driven with realistic BepInEx plugin lists: standalone
// SPT must never be treated as a Fika raid because of an unrelated "fika"-named plugin.
internal static class NetworkSessionChecks
{
    private static readonly string[] Standalone =
        { "itsripper.dualwield", "itsripper.dualwield.dualholster", "com.SPT.core", "com.SPT.custom" };
    private static readonly string[] WithUnrelatedFikaNamedPlugins =
        Standalone.Concat(new[] { "com.example.questing.fikasync", "fika.compat.example" }).ToArray();
    private static readonly string[] FikaClient = Standalone.Append("com.fika.core").ToArray();
    private static readonly string[] FikaHeadlessHost = FikaClient.Append("com.fika.headless").ToArray();

    private static bool Fika(string[] plugins) => NetworkSessionPolicy.IsFikaLoaded(plugins.Contains);

    private static string? Refusal(string[] plugins, bool hideout, bool ready)
        => NetworkSessionPolicy.PairRefusal(Fika(plugins), hideout, ready);

    internal static void Run(Action<string, bool> check)
    {
        check("Pairing guard: standalone hideout range pairs", Refusal(Standalone, hideout: true, ready: false) == null);
        check("Pairing guard: standalone raid pairs", Refusal(Standalone, hideout: false, ready: false) == null);
        check("Pairing guard: standalone raid pairs although other mods' Fika add-ons are loaded (v1.1.0 regression)",
            !Fika(WithUnrelatedFikaNamedPlugins) && Refusal(WithUnrelatedFikaNamedPlugins, hideout: false, ready: false) == null);
        check("Pairing guard: standalone hideout pairs with other mods' Fika add-ons loaded",
            Refusal(WithUnrelatedFikaNamedPlugins, hideout: true, ready: false) == null);
        check("Pairing guard: Fika client and host with the integration ready pair in a raid",
            Refusal(FikaClient, hideout: false, ready: true) == null && Refusal(FikaHeadlessHost, hideout: false, ready: true) == null);
        check("Pairing guard: a Fika raid without a working integration refuses the pair",
            Refusal(FikaClient, hideout: false, ready: false) != null && Refusal(FikaHeadlessHost, hideout: false, ready: false) != null);
        check("Pairing guard: the refusal names the missing integration and the log",
            Refusal(FikaClient, hideout: false, ready: false)!.Contains("Fika integration is not running") && Refusal(FikaClient, hideout: false, ready: false)!.Contains("LogOutput.log"));
        check("Pairing guard: the hideout range pairs under Fika without the integration (not a network session)",
            Refusal(FikaClient, hideout: true, ready: false) == null);
        check("Pairing guard: Fika is recognised only by the exact, case-sensitive Fika.Core GUID",
            !Fika(new[] { "COM.FIKA.CORE", "com.fika.core.extra", "com.fika.headless" }) && NetworkSessionPolicy.FikaCoreGuid == "com.fika.core");
    }
}
