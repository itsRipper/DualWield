using DualWieldLab;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Static native/compiled contracts only: no Unity execution or live client
// magazine transaction is claimed by these assertions.
internal static class ReloadServiceChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        bool Field(MethodDefinition m,string n)=>m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==n);
        var service=pt["DualWieldLab.NativeReloadService"];var begin=M(service,"Begin");
        var methods=All(new[]{service}).SelectMany(t=>t.Methods).ToArray();var calls=methods.SelectMany(Calls).ToArray();
        check("Reload service contains no weapon move, swap, handoff, empty-hands or manual network transaction",
            !calls.Any(c=>c.Name is "Move" or "Swap" or "SetItemInHands" or "SetEmptyHands" or "TryRunNetworkTransaction" or "set_Parent" or "set_CurrentAddress" or "set_HandsController"));
        check("Reload service removed every staging, equip and return-footprint method",
            !methods.Any(m=>m.Name is "SwapWeapons" or "HolsterForSwap" or "EquipExpected" or "CanReturnWithMagazine" or "OverlapsReturnSpace"));
        check("Reload stages cannot represent equipment staging or controller handoff",!service.NestedTypes.Single(t=>t.Name=="Stage").Fields.Any(f=>f.Name.Contains("Stag")||f.Name.Contains("Holster")));
        var refusal=begin.Body.Instructions.First(i=>i.Operand is string s&&s.StartsWith("Left reload is disabled:"));
        var firstNative=begin.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="EFT.Player");
        check("Offhand refusal returns before reading native player or starting service",refusal.Offset<firstNative.Offset&&begin.Body.Instructions.Any(i=>i.Offset>refusal.Offset&&i.Offset<firstNative.Offset&&i.OpCode==OpCodes.Ret));
        check("Offhand refusal reports the in-place lifecycle blocker rather than claiming success",begin.Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("in_place_native_lifecycle_unverified")));
        check("Only the main original weapon is stored as the native reload target",Field(begin,"_main")&&Field(begin,"_target")&&Field(begin,"_controller"));
        check("Both original pistol addresses and owner identities are checked in active updates",Has(M(service,"Update"),"AddressesMatch")&&Has(M(service,"AddressesMatch"),"IdentityValid")&&Field(M(service,"AddressesMatch"),"_mainAddress")&&Field(M(service,"AddressesMatch"),"_offhandAddress"));
        check("Native reload request rechecks original address and cached active controller",Has(M(service,"StartNativeReload"),"AddressesMatch")&&Field(M(service,"StartNativeReload"),"_controller")&&Has(M(service,"StartNativeReload"),"ReloadMag"));
        var callbacks=methods.Where(m=>m.Name.Contains("<StartNativeReload>")).ToArray();
        check("Native magazine callback checks original pistol addresses before accepting outcome",callbacks.Any(m=>Has(m,"AddressesMatch")&&Has(m,"FinishReload")&&Has(m,"IsCurrent")));
        check("Native magazine selection retains actual slot, ammo and reachable inventory validation",Has(M(service,"TryPlanReload"),"Plan")&&Has(M(service,"Plan"),"CheckMoveIgnoringTargetItem")&&Has(M(service,"StartNativeReload"),"IsAmmoCompatible")&&Has(M(service,"Plan"),"GetReachableItemsOfTypeNonAlloc"));
        check("Reload proof includes unchanged weapon addresses and both magazine/chamber counts",Has(M(service,"VerifyReloadOutcome"),"AddressesMatch")&&Has(M(service,"VerifyReloadOutcome"),"NativeCountsMatch")&&Has(M(service,"VerifyReloadOutcome"),"OtherWeaponAmmo"));
        check("Reload traces include current and original pistol addresses and actual identity verdict",Has(M(service,"Trace"),"DescribeAddress")&&Has(M(service,"Trace"),"AddressesMatch")&&M(service,"Trace").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("pistol_addresses_unchanged=")));
        check("Address diagnostics include native parent/container identity and grid coordinates",Has(M(service,"DescribeAddress"),"get_ParentItem")&&Has(M(service,"DescribeAddress"),"get_ID")&&(Has(M(service,"DescribeAddress"),"get_LocationInGrid")||Field(M(service,"DescribeAddress"),"LocationInGrid")));
        var controller=nt["EFT.Player/FirearmController"];var reload=nt["EFT.Player/FirearmController/ReloadExternalMagOperation"];
        check("Installed ReloadMag delegates to its current operation rather than taking a target weapon",!M(controller,"ReloadMag").Parameters.Any(p=>p.ParameterType.Name=="Weapon")&&Has(M(controller,"ReloadMag"),"get_CurrentOperation"));
        FeedbackChecks(check,pt,M,Has);
        check("Installed player animation events are emitted only through the active hands controller",Field(M(nt["EFT.Player"],"ArmsLateUpdate"),"_handsController")&&Has(M(nt["EFT.Player"],"ArmsLateUpdate"),"EmitEvents"));
        var result=nt["EFT.Player/FirearmController/ReloadExternalMagResult"];
        check("Native external reload result eagerly moves real magazines and feeds native chamber",Has(M(result,"Run"),"Move")&&Has(M(result,"Run"),"PopTo"));
        check("Native event-driven reload completion raises inventory success and changes operation",Has(M(reload,"SwitchToIdlingState"),"RaiseEvents")&&Has(M(reload,"SwitchToIdlingState"),"InitiateOperation")&&Has(M(reload,"SwitchToIdlingState"),"Succeed"));
        var baseReload=nt["EFT.Player/FirearmController/BaseReloadOperation"];
        check("Installed reload operation couples to shared player procedural/body/assisting-hand state",Has(M(baseReload,"Start"),"get_ProceduralWeaponAnimation")&&Has(M(baseReload,"Start"),"get_BodyAnimatorCommon")&&Has(M(baseReload,"Start"),"RemoveLeftHandItem"));
    }
    // A refused reload gives both hands the same feedback and starts nothing.
    private static void FeedbackChecks(Action<string,bool> check, Dictionary<string,TypeDefinition> pt,
        Func<TypeDefinition,string,MethodDefinition> M, Func<MethodDefinition,string,bool> Has)
    {
        var right=ReloadFeedback.Refused(ReloadFeedback.RightNoSpare);
        var left=ReloadFeedback.LeftRefused(ReloadFeedback.LeftNoSpare);
        check("No-spare refusals of the right and left reloads play EFT's need-ammo phrase with their own text",
            right.NeedAmmoPhrase&&left.NeedAmmoPhrase&&right.Message==ReloadFeedback.RightNoSpare&&left.Message==ReloadFeedback.LeftNoSpare
            &&right.Message!=left.Message&&right.Message.Contains("right")&&left.Message.Contains("left"));
        var service=pt["DualWieldLab.NativeReloadService"];
        string[] otherRefusals={service.Fields.Single(f=>f.Name=="NoRoomForOldMagazine").Constant as string??"",service.Fields.Single(f=>f.Name=="UnsupportedMechanism").Constant as string??""};
        check("Room and mechanism refusals notify without the need-ammo phrase",
            otherRefusals.All(r=>r.Length>0&&!ReloadFeedback.Refused(r).NeedAmmoPhrase&&ReloadFeedback.Refused(r).Message==r));
        var leftOther=ReloadFeedback.LeftRefused("Left magazine slot rejects the swap");
        check("A left refusal other than no-spare shows the generic text without the phrase",
            !leftOther.NeedAmmoPhrase&&leftOther.Message==ReloadFeedback.LeftUnavailable);
        check("The service refusal constants carry the texts the feedback classifies",
            (service.Fields.Single(f=>f.Name=="NoSpareMagazine").Constant as string)==ReloadFeedback.RightNoSpare
            &&(pt["DualWieldLab.InactiveMagazineReload"].Fields.Single(f=>f.Name=="NoSpareMagazine").Constant as string)==ReloadFeedback.LeftNoSpare);
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        var ins=M(runtime,"BeginReload").Body.Instructions;
        var preflight=ins.First(i=>i.Operand is MethodReference m&&m.Name=="Preflight");
        var presentation=ins.First(i=>i.Operand is MethodReference m&&m.Name=="TryBeginReloadPresentation");
        var refuse=ins.First(i=>i.Operand is MethodReference m&&m.Name=="RefuseReload");
        check("Classic right reload preflights the spare magazine, and refuses with a return, before the left pistol is lowered",
            preflight.Offset<refuse.Offset&&refuse.Offset<presentation.Offset
            &&ins.Any(i=>i.Offset>refuse.Offset&&i.Offset<presentation.Offset&&i.OpCode==OpCodes.Ret));
        check("The refusal path neither unpairs nor starts the reload service",!Has(M(runtime,"RefuseReload"),"Unpair")&&!Has(M(runtime,"RefuseReload"),"Begin")&&!Has(M(runtime,"RefuseReload"),"TryBeginReloadPresentation"));
        check("The refusal logs, voices EFT's need-ammo phrase and notifies",
            Has(M(runtime,"RefuseReload"),"LogDiagnostic")&&Has(M(runtime,"RefuseReload"),"ReloadMagNotFound")&&Has(M(runtime,"RefuseReload"),"Notify"));
        check("The left reload refuses through the same feedback",Has(M(runtime,"BeginOffhandMagazineReload"),"RefuseReload")&&Has(M(runtime,"BeginOffhandMagazineReload"),"LeftRefused"));
    }
}