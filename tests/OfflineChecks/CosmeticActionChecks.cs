using Mono.Cecil;
using Mono.Cecil.Cil;

// Compiled integration contracts, supplemented by actual serialized native
// state-graph checks. Neither these assertions nor the graphs run Unity.
internal static class CosmeticActionChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var types=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(string type,string name)=>types["DualWieldLab."+type].Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string name)=>Calls(m).Any(c=>c.Name==name);
        bool Field(MethodDefinition m,string name)=>m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==name);
        var begin=M("OffhandHandPose","BeginPresentationAction");
        var advance=M("OffhandHandPose","AdvanceOffhandAnimation");
        var presentation=M("RuntimePlugin","ApplyReloadPresentation");
        var strings=begin.Body.Instructions.Select(i=>i.Operand).OfType<string>().ToArray();
        check("Cosmetic action selects native plain holster and draw instead of chambering spawn operation",
            strings.Contains("IDLE TO OUT")&&strings.Contains("OUT TO IDLE")&&!Calls(begin).Any(c=>c.Name is "SpawnOperation" or "PopTo"));
        check("Requested native action must be acknowledged by the actual sampled state",
            Has(begin,"Play")&&Has(begin,"Update")&&Has(begin,"GetCurrentAnimatorStateInfo")&&Has(begin,"CancelPresentationAction"));
        check("Cosmetic completion follows observed native terminal state and transition boundary",
            Field(advance,"_presentationObserved")&&Field(advance,"_presentationTerminal")&&Has(advance,"IsInTransition")&&Has(advance,"set_PresentationFinished"));
        check("Missing native completion is bounded and cannot report a successful cosmetic action",
            Field(advance,"_presentationStarted")&&Has(advance,"get_unscaledTime")&&Has(advance,"set_PresentationFailed")&&Has(presentation,"get_PresentationFailed"));
        check("Holster and draw use the same independent native left action retarget",
            Has(presentation,"ApplyRetarget")&&Has(advance,"OwnSelector")&&!Has(presentation,"ApplySwap")&&!Has(presentation,"ApplyDualHold"));
        check("Actual native reload bypasses cosmetic pose overrides",
            Has(presentation,"get_ReloadVisualTransition")&&!Calls(presentation).Any(c=>c.Name is "ReloadMag" or "EmitEvents" or "SetupMod"));
        var grip=M("OffhandVisualRig","UseNativeActionGrip");
        check("Animated action contact reads the actual native palm/root relation without fixed offsets",
            Has(grip,"InverseTransformPoint")&&Has(grip,"get_rotation")&&Field(grip,"_nativeGripDonor")&&!Has(grip,"AngleAxis"));
        check("Ordinary paired grip is restored after the cosmetic action",
            Has(M("RuntimePlugin","ReleaseReloadPresentation"),"RestoreReadyGrip")&&Field(M("OffhandVisualRig","RestoreReadyGrip"),"_readyGripRotation"));
        check("Cancellation restores native equipped idle rather than leaving a lowered clone",
            Has(M("OffhandHandPose","CancelPresentationAction"),"SetActiveParam")&&Has(M("OffhandHandPose","CancelPresentationAction"),"Play"));
        check("Successful native draw does not rewind its idle every frame",
            !Has(M("OffhandHandPose","FinishPresentationAction"),"Play")&&!Has(advance,"Play"));
        var request=M("RuntimePlugin","BeginReload");
        var refusal=request.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.Name=="BeginOffhandMagazineReload");
        var action=request.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.Name=="TryBeginReloadPresentation");
        check("Left inventory reload routes separately before primary cosmetic holster choreography",
            refusal.Offset<action.Offset&&request.Body.Instructions.Any(i=>i.Offset>refusal.Offset&&i.Offset<action.Offset&&i.OpCode==OpCodes.Ret)&&!Has(request,"Unpair"));
        check("Native right reload starts only through the existing active controller after holster presentation",
            Has(M("RuntimePlugin","StartPreparedReload"),"Begin")&&!Has(request,"Begin")&&Has(presentation,"get_PresentationFinished"));
        check("Final pair admission requires equipped native idle and real visible geometry",
            Has(M("RuntimePlugin","ReloadPairedReady"),"get_PairedIdleReady")&&Has(M("RuntimePlugin","ReloadPairedReady"),"get_HasVisibleGeometry"));
        check("Visible geometry proof observes cached native flags and camera layer without enabling every renderer",
            Has(M("OffhandVisualRig","get_HasVisibleGeometry"),"get_enabled")&&Has(M("OffhandVisualRig","get_HasVisibleGeometry"),"get_forceRenderingOff")
            &&Has(M("OffhandVisualRig","get_HasVisibleGeometry"),"get_cullingMask")&&!Has(M("OffhandVisualRig","get_HasVisibleGeometry"),"set_enabled"));
        check("Restoration proves native active idle before opening the existing firing gate",
            Has(M("OffhandHandPose","get_PairedIdleReady"),"GetBool")&&Has(M("OffhandHandPose","get_PairedIdleReady"),"IsIdling"));
        check("Transition diagnostics correlate actual native ownership and address state",
            Has(M("RuntimePlugin","DrainDiagnostics"),"DescribeAddress")&&Has(M("RuntimePlugin","DrainDiagnostics"),"get_HandsController"));
        check("Original pistol addresses span cosmetic preparation and later native readiness",
            Has(M("RuntimePlugin","Update"),"ReloadAddressesMatch")&&Has(M("RuntimePlugin","ReloadNativeReady"),"ReloadAddressesMatch")
            &&Field(request,"_reloadMainAddress")&&Field(request,"_reloadOffhandAddress"));
        check("Address proof is released on cancellation instead of retaining stale session items",
            Field(M("RuntimePlugin","CancelReload"),"_reloadMainAddress")&&Field(M("RuntimePlugin","CancelReload"),"_reloadOffhandAddress"));
    }
}
