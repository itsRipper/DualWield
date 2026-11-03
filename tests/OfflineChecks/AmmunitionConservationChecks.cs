using DualWieldLab;
using Mono.Cecil;

internal static class AmmunitionConservationChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        foreach (int capacity in new[]{17,19,21,31,33,50})
        {
            check("Inventory replacement preserves loaded chamber and magazine counts at capacity "+capacity,
                MagazineReplacementAudit.Counts(0,capacity,1,0,capacity,1));
            check("Native one-round feed conserves total cartridges at capacity "+capacity,
                MagazineReplacementAudit.Counts(0,capacity,0,0,capacity-1,1));
        }
        check("Missing native feed cannot become a completed empty reload",!MagazineReplacementAudit.Counts(0,17,0,0,17,0));
        check("Double feed or fabricated ammunition cannot pass reload audit",!MagazineReplacementAudit.Counts(0,17,0,0,15,1)&&!MagazineReplacementAudit.Counts(0,17,0,0,17,1));
        check("Old magazine cartridge loss is rejected",!MagazineReplacementAudit.Counts(3,50,1,2,50,1));
        for(int failed=0;failed<7;failed++)
        {
            var p=Enumerable.Repeat(true,7).ToArray();p[failed]=false;
            check("Native reload proof rejects failed invariant "+failed,!MagazineReplacementAudit.Verified(p[0],p[1],p[2],p[3],p[4],p[5],p[6]));
        }
        check("Full native magazine/chamber/identity proof admits completion",MagazineReplacementAudit.Verified(true,true,true,true,true,true,true));
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string name)=>Calls(m).Any(c=>c.Name==name);
        bool Field(MethodDefinition m,string name)=>m.HasBody&&m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==name);
        var ui=M(nt["EFT.UI.ItemUiContext"],"ReloadWeapon");
        check("Installed inventory UI replaces inactive weapon magazines with Swap and native transaction",Has(ui,"Swap")&&Has(ui,"TryRunNetworkTransaction")&&Has(ui,"GetMagazineSlot")&&!Has(ui,"SetItemInHands"));
        var apply=M(nt["EFT.InventoryLogic.Ammo"],"ApplyToAddress");
        check("Installed chamber address API delegates to native Move or SplitMax",Has(apply,"Move")&&Has(apply,"SplitMax")&&!Has(apply,"set_StackObjectsCount"));
        var service=pt["DualWieldLab.InactiveMagazineReload"];
        var methods=All(new[]{service}).SelectMany(t=>t.Methods).ToArray();var calls=methods.SelectMany(Calls).ToArray();
        var worker=service.NestedTypes.Single(t=>t.Name.Contains("<Begin>"));var body=M(worker,"MoveNext");
        check("Offhand submits actual native magazine and chamber operations",calls.Any(c=>c.Name=="Swap")&&Has(body,"ApplyToAddress")&&calls.Count(c=>c.Name=="TryRunNetworkTransaction")==2);
        check("Native simulation failure precedes submission; native authority stays intact",Has(body,"get_Failed")&&calls.Any(c=>c.Name=="CanAccept")&&calls.Any(c=>c.Name=="IsAmmoCompatible")&&calls.Any(c=>c.Name=="Examined"));
        check("Offhand never equips or relocates either weapon through a hands controller",!calls.Any(c=>c.Name is "SetItemInHands" or "SetEmptyHands" or "set_HandsController" or "ReloadMag"));
        check("No direct cartridge mutation, handmade item or synthetic stack assignment",!calls.Any(c=>c.Name is "PopTo" or "AddItem" or "CreateItem" or "set_StackObjectsCount" or "set_Parent"));
        check("Native completion verifies magazine identities and both original addresses",Has(body,"SameAddress")&&Has(body,"get_Id")&&Has(body,"get_Owner")&&Has(body,"Verified"));
        check("Live chamber identity and native empty-chamber result are checked",Has(body,"Counts")&&Has(body,"get_ContainedItem")&&Has(body,"get_ChamberAmmoCount"));
        check("Interrupted transaction cannot start a new chamber operation",Has(body,"get_Interrupted")&&Has(body,"set_MagazineVerified"));
        check("Transaction state remains observed until actual native task settlement",Has(body,"set_Pending")&&Has(body,"set_Finished")&&body.Body.ExceptionHandlers.Any(h=>h.HandlerType==Mono.Cecil.Cil.ExceptionHandlerType.Finally));
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        check("Finished synchronous callbacks stay input-blocked until consumed",Field(M(runtime,"get_WeaponActionBusy"),"_offhandReload")&&Has(M(runtime,"get_ReloadBusy"),"get_WeaponActionBusy")&&!Has(M(runtime,"get_WeaponActionBusy"),"get_Pending"));
        check("Cancellation marks native work interrupted instead of clearing or retrying it",Has(M(runtime,"CancelReload"),"Interrupt")
            &&!M(runtime,"CancelReload").Body.Instructions.Any(i=>i.OpCode==Mono.Cecil.Cil.OpCodes.Stfld&&i.Operand is FieldReference f&&f.Name=="_offhandReload"));
        check("Verified completion rebinds the exact pair through existing readiness gates",Has(M(runtime,"ObserveOffhandMagazineReload"),"ResumeReloadPair")&&Has(M(runtime,"ObserveOffhandMagazineReload"),"get_Verified")&&Has(M(runtime,"ObserveOffhandMagazineReload"),"get_Interrupted"));
        check("Native right reload keeps its existing controller path",Has(M(pt["DualWieldLab.NativeReloadService"],"StartNativeReload"),"ReloadMag"));
        check("Transaction admission is single-use even before native submission",Field(body,"_started"));
        check("Interrupting a native task never clears its pending/completed state early",
            Has(M(service,"Interrupt"),"set_Interrupted")&&!Has(M(service,"Interrupt"),"set_Pending")&&!Has(M(service,"Interrupt"),"set_Finished"));
        check("Native swap rollback remains owned by the installed operation result",
            Calls(M(nt["EFT.InventoryLogic.SwapResult"],"RollBack")).Count(c=>c.Name=="RollBack")>=4);
        check("Completion is observed before the mod-disabled branch so native work can settle",
            Calls(M(runtime,"Update")).First().Name=="ObserveOffhandMagazineReload");
        check("Native chamber transaction is limited to the validated original active scope",
            Has(body,"get_IsAlive")&&Has(body,"get_FirstPersonPointOfView")&&Has(body,"get_IsInventoryOpened")&&Has(body,"get_CurrentOperation"));
        check("Unknown native completion locks out another paired submission instead of retrying",
            Has(body,"set_CompletionUncertain")&&Has(M(runtime,"ObserveOffhandMagazineReload"),"get_CompletionUncertain")
            &&Field(M(runtime,"ObserveOffhandMagazineReload"),"_faulted"));
        check("Each offhand request clears any earlier reload-success diagnostic state",
            Field(M(runtime,"BeginOffhandMagazineReload"),"_returnReloadVerified"));
    }
}
