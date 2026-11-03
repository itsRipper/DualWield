using Mono.Cecil;
using Mono.Cecil.Cil;

// These checks audit the shipped native implementation and the compiled bridge.
// They do not execute Unity's animator or claim an in-game moving slide.
internal static class SlideMechanicsChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
            => types.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition type,string name)=>type.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition method)=>method.HasBody
            ? method.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition method,string name)=>Calls(method).Any(c=>c.Name==name);
        bool Field(MethodDefinition method,string name)=>method.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==name);
        bool Number(MethodDefinition method,float value)=>method.Body.Instructions.Any(i=>i.OpCode==OpCodes.Ldc_R4&&i.Operand is float f&&f==value);
        var pose=pt["DualWieldLab.OffhandHandPose"];
        var shot=M(pose,"BeginOffhandShot");
        var advance=M(pose,"AdvanceOffhandAnimation");
        var sync=M(pose,"SyncMechanicalState");
        var lockSlide=M(pose,"ShouldLockSlide");
        var fire=M(nt["EFT.Player/FirearmController/FireOperation"],"StartFireAnimation");
        var auto=M(nt["EFT.Player/FirearmController/AutomaticFireOperation"],"Start");
        check("Installed native single-shot visual starts full or semi fire at the actual .2 shot phase",
            Has(fire,"get_FullFireStateName")&&Has(fire,"get_FullSemiFireStateName")&&Has(fire,"Play")&&Number(fire,.2f));
        check("Installed native automatic operation uses the same fire state and .2 phase",
            Has(auto,"get_FullFireStateName")&&Has(auto,"Play")&&Number(auto,.2f));
        check("Offhand visual uses the installed fire states and shot phase rather than a slide offset",
            Has(shot,"get_FullFireStateName")&&Has(shot,"get_FullSemiFireStateName")&&Has(shot,"Play")&&Number(shot,.2f)
            &&!Has(shot,"set_localPosition")&&!Has(shot,"set_localRotation"));
        // Release compilation lowers ReferenceEquals to a reference branch.
        var shotGuard=shot.Body.Instructions.TakeWhile(i=>!(i.Operand is MethodReference m&&m.Name=="SyncMechanicalState")).ToArray();
        check("Offhand mechanical bridge validates the exact paired weapon reference",
            shotGuard.Any(i=>i.Operand is FieldReference f&&f.Name=="_offhandWeapon")
            &&shotGuard.Any(i=>i.OpCode==OpCodes.Ldarg_1)
            &&shotGuard.Any(i=>i.OpCode.Code is Code.Beq or Code.Beq_S or Code.Bne_Un or Code.Bne_Un_S)
            &&shotGuard.Any(i=>i.OpCode==OpCodes.Ret));
        check("Mechanical ammunition parameters are read from the real weapon",
            Has(sync,"get_ChamberAmmoCount")&&Has(sync,"GetCurrentMagazineCount")&&Has(sync,"GetCurrentMagazine")
            &&Has(sync,"SetAmmoInChamber")&&Has(sync,"SetAmmoOnMag")&&Has(sync,"SetMagInWeapon"));
        check("Empty slide catch respects native capability, manual-catch flag and actual chamber/magazine",
            Has(lockSlide,"get_IsBoltCatch")&&Has(lockSlide,"get_ManualBoltCatch")&&Has(lockSlide,"get_ChamberAmmoCount")
            &&Has(lockSlide,"GetCurrentMagazine")&&Has(lockSlide,"GetCurrentMagazineCount"));
        check("Native slide catch is sampled at the native automatic operation's .75 phase",Number(advance,.75f)&&Has(advance,"SetBoltCatch"));
        check("Owned animator advances once per Unity frame and outside selector-only gating",
            Field(advance,"_offhandSelectorFrame")&&Has(advance,"get_frameCount")&&Has(advance,"Update")&&Has(M(pose,"CaptureNativePose"),"AdvanceOffhandAnimation"));
        check("Native fire playback cannot autonomously request extra authoritative shots",
            Has(shot,"SetFire")&&!Calls(shot).Any(c=>c.Name is "FireOnce" or "EmitEvents" or "ProcessInventoryOperationsForSingleshotWithMalf"));
        check("Ordinary visual sampling does not force IDLE every frame",!Has(advance,"Play"));
        check("Selector request survives initial native IDLE until its state has really been observed",
            Field(advance,"_offhandSelectorObserved")&&Field(advance,"_selectorWait")&&!Has(advance,"Play"));
        check("Selector actions use the actual native mode component and reject single-mode pistols",
            Has(M(pose,"BeginOffhandSelector"),"get_AvailableEFireModes")&&!Has(M(pose,"BeginOffhandSelector"),"SetFireMode"));
        check("Mechanical state and selector parameter initialise from the paired weapon on every fresh binding",
            Has(M(pose,".ctor"),"SyncMechanicalState")&&Has(M(pose,".ctor"),"get_SelectedFireMode")&&Has(M(pose,".ctor"),"SetFireMode"));
        check("Mechanical bridge has no native gameplay-event emitter or inventory transaction entry point",
            !pose.Methods.SelectMany(Calls).Any(c=>c.Name is "EmitEvents" or "Emit" or "ReloadMag" or "TryRunNetworkTransaction" or "ProcessInventoryOperationsForSingleshotWithMalf"));
        var visibility=M(pt["DualWieldLab.OffhandVisualRig"],"SetPresentationVisible");
        check("Service hiding only masks cached geometry and leaves native component enabled states and hierarchy alone",
            Has(visibility,"set_forceRenderingOff")&&!Calls(visibility).Any(c=>c.Name is "SetActive" or "SetParent" or "set_enabled" or "GetComponentsInChildren"));
        var runtimeFire=M(pt["DualWieldLab.RuntimePlugin"],"TryFire");
        var firingCalls=Calls(runtimeFire).Select(c=>c.Name).ToList();
        check("Actual offhand shot bridge is integrated after authoritative firing",
            firingCalls.IndexOf("BeginOffhandShot")>firingCalls.IndexOf("FireOnce")
            &&firingCalls.IndexOf("BeginOffhandShot")>firingCalls.IndexOf("IsValidShotDelta")
            &&Has(runtimeFire,"IsValidShotDelta"));
    }
}
