using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class ReloadHandoverChecks
{
    internal static void Run(Action<string,bool> check,AssemblyDefinition native,AssemblyDefinition plugin)
    {
        var gate=new PresentationFrameGate();
        check("Reload handover cannot advance before a pose is presented",!gate.Ready(10));
        gate.Mark(10);
        check("Same-frame fixed/late callbacks cannot bypass handover presentation",!gate.Ready(10));
        gate.Mark(11);
        check("Repeated presentation callbacks retain the first completed frame",gate.Ready(11));
        gate.Reset();
        check("Cancelled reload clears the previous handover acknowledgement",!gate.Ready(12));

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition type,string n)=>type.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        check("Obsolete mirrored reload borrower is absent from the plugin",!pt.ContainsKey("DualWieldLab.ReloadPresentation"));
        var nativeReload=nt["EFT.Player/FirearmController/ReloadExternalMagOperation"];
        check("Real native magazine appearance and completion remain authoritative",Has(M(nativeReload,"OnMagAppeared"),"SetupMod")&&Has(M(nativeReload,"SwitchToIdlingState"),"Succeed"));
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        check("Native animation boundaries restore the ordinary paired arm borrower",Has(M(runtime,"BeforeNativeFrame"),"BeforeNativePose"));
        var presentation=M(runtime,"ApplyReloadPresentation");
        check("Cosmetic offhand holster/draw does not swap both arms or move native held props",!Calls(presentation).Any(c=>c.Name is "ApplySwap" or "ApplyDualHold" or "ApplyLoweringPose" or "set_position" or "set_rotation")&&Has(presentation,"get_ReloadVisualTransition"));
        check("Native reload presentation performs no inventory or ammunition operation",!Calls(presentation).Any(c=>c.Name is "Swap" or "ReloadMag" or "TryRunNetworkTransaction" or "PopTo" or "CreateItem"));
        check("Native reload presentation performs no rig hierarchy discovery",!Calls(presentation).Any(c=>c.Name is "GetComponentsInChildren" or "FindTransformRecursive" or "GetModTransform"));
        check("Pre-shot visual refresh does not capture modified arm output",!Has(M(runtime,"TryFire"),"CaptureNativePose"));
        check("Service begins only after the preparatory lowering phase",!Has(M(runtime,"BeginReload"),"Begin")&&Has(M(runtime,"StartPreparedReload"),"Begin"));
        check("Cancellation restores presentation and clears the two cosmetic phase gates",Has(M(runtime,"CancelReload"),"ReleaseReloadPresentation")&&Calls(M(runtime,"CancelReload")).Count(c=>c.Name=="Reset")==2);
        check("Native controller boundaries release reload overlays",Has(M(runtime,"BeforePlayerBoundary"),"ReleaseReloadPresentation"));
        var service=pt["DualWieldLab.NativeReloadService"];
        check("Offhand reload does not enter a weapon holster or equipment staging path",!service.Methods.Any(m=>m.Name is "HolsterForSwap" or "SwapWeapons"));
        check("Native primary reload checks its unchanged pistol addresses every update",Has(M(service,"Update"),"AddressesMatch"));
        check("Installed native holster API delegates to the ordinary hands process",Has(M(nt["EFT.Player"],"SetEmptyHands"),"Proceed"));
    }
}
