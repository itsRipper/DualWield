using Mono.Cecil;
using Mono.Cecil.Cil;

// Installed native ABI and compiled presentation contracts, not Unity playback.
internal static class EquipLifecycleChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
            => types.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition method)=>method.HasBody
            ? method.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition method,string name)=>Calls(method).Any(c=>c.Name==name);
        var nativeSetter=M(nt["ObjectInHandsAnimator"],"SetActiveParam");
        check("Installed equipped animation parameter has optional left-hand reset",nativeSetter.Parameters.Count==2
            &&nativeSetter.Parameters[0].ParameterType.MetadataType==MetadataType.Boolean
            &&nativeSetter.Parameters[1].ParameterType.MetadataType==MetadataType.Boolean
            &&nativeSetter.Parameters[1].HasConstant&&(bool)nativeSetter.Parameters[1].Constant);
        check("Installed equipped parameter setter only delegates animation state and optional hand reset",Has(nativeSetter,"SetActive")
            &&Has(nativeSetter,"ResetLeftHand")&&!Calls(nativeSetter).Any(c=>c.Name is "SetItemInHands" or "ReloadMag" or "TryRunNetworkTransaction"));
        var rig=pt["DualWieldLab.OffhandVisualRig"];
        var init=M(rig,".ctor");
        int Index(string name)=>init.Body.Instructions.ToList().FindIndex(i=>i.Operand is MethodReference m&&m.Name==name);
        int activeIndex=Index("SetActiveParam");
        check("Owned equipped state is initialized after reset and before first native animation sample",Index("ResetStatesToDefault")>=0
            &&activeIndex>Index("ResetStatesToDefault")&&activeIndex<Index("Update"));
        check("Owned initialization enables Active without resetting the player left hand",activeIndex>=2
            &&init.Body.Instructions[activeIndex-2].OpCode==OpCodes.Ldc_I4_1
            &&init.Body.Instructions[activeIndex-1].OpCode==OpCodes.Ldc_I4_0);
        check("Owned FastHide is cleared before the first sampled native update",Index("SetFastHide")>Index("ResetStatesToDefault")
            &&Index("SetFastHide")<Index("Update")&&init.Body.Instructions[Index("SetFastHide")-1].OpCode==OpCodes.Ldc_I4_0);
        var pose=pt["DualWieldLab.OffhandHandPose"];
        var advance=M(pose,"AdvanceOffhandAnimation");
        check("Continuous owned native animator sampling remains enabled",Has(advance,"Update")&&Has(advance,"get_frameCount")
            &&Has(M(pose,"CaptureNativePose"),"AdvanceOffhandAnimation"));
        check("Normal sampling does not force equipped state or recreate the model every frame",!Calls(advance).Any(c=>c.Name is
            "SetActiveParam" or "SetFastHide" or "Instantiate" or "CreateObject" or "TryPair" or "set_enabled"));
        check("Normal sampling does not mask the missing parameter by rewinding idle",!Has(advance,"Play"));
        check("Actual successful shot retains native fire-state sampling",Has(M(pose,"BeginOffhandShot"),"Play")
            &&Has(M(pose,"BeginOffhandShot"),"get_FullFireStateName")&&Has(M(pose,"BeginOffhandShot"),"SyncMechanicalState"));
        check("Visual alignment does not override native enabled flags for every attachment",!Has(M(rig,"ApplyPresentationFlags"),"set_enabled"));
        var diagnostic=M(pose,"CaptureDiagnostic");
        check("Visibility diagnostics inspect actual current native animator state and equipped parameter",Has(diagnostic,"GetBool")
            &&Has(diagnostic,"GetCurrentAnimatorStateInfo"));
    }
}
