using System;
using EFT;
using Fika.Core.Main.Players;
using HarmonyLib;

namespace DualWieldLab.Fika;

// Fika's ObservedPlayer does not run Player.VisualPass: its LateUpdate runs ObservedVisualPass,
// which applies the third-person hand IK itself. The observed left pistol is placed around that
// pass instead (the runtime's own VisualPass hook never fires for these players).
internal static class ObservedPass
{
    internal const string MethodName = "ObservedVisualPass";

    internal static void Install(Harmony harmony)
    {
        var pass = AccessTools.Method(typeof(ObservedPlayer), MethodName)
            ?? throw new InvalidOperationException("Fika's observed player pass (ObservedPlayer." + MethodName + ") was not found.");
        harmony.Patch(pass,
            prefix: new HarmonyMethod(typeof(ObservedPass), nameof(Before)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(ObservedPass), nameof(After)) { priority = Priority.Last });
    }

    private static void Before(ObservedPlayer __instance) => DualWieldNetwork.BeforeObservedPass(__instance);

    private static void After(ObservedPlayer __instance) => DualWieldNetwork.AfterObservedPass(__instance);
}
