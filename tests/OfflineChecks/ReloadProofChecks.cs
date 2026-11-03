using DualWieldLab;
using Mono.Cecil;

internal static class ReloadProofChecks
{
    internal static void Run(Action<string, bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        check("Reload proof accepts tactical transfer with conserved chamber and magazines", ReloadOutcomeAudit.Verified(true,true,true,true,41,41,17,17));
        check("Reload proof accepts empty chamber feed without changing total cartridges", ReloadOutcomeAudit.Verified(true,true,true,true,30,30,0,0));
        check("Accepted reload callback alone is not success", !ReloadOutcomeAudit.Verified(true,true,false,true,30,30,17,17));
        check("Declined callback cannot claim success from coincidental item state", !ReloadOutcomeAudit.Verified(false,true,true,true,30,30,17,17));
        check("Lost native magazine ownership fails reload proof", !ReloadOutcomeAudit.Verified(true,false,true,true,30,30,17,17));
        check("Missing old magazine storage fails reload proof", !ReloadOutcomeAudit.Verified(true,true,true,false,30,30,17,17));
        check("Lost cartridges fail reload proof", !ReloadOutcomeAudit.Verified(true,true,true,true,30,29,17,17));
        check("Fabricated cartridges fail reload proof", !ReloadOutcomeAudit.Verified(true,true,true,true,30,31,17,17));
        check("Reloading left cannot consume right-hand cartridges", !ReloadOutcomeAudit.Verified(true,true,true,true,30,30,17,16));
        check("Reloading right cannot fabricate offhand cartridges", !ReloadOutcomeAudit.Verified(true,true,true,true,30,30,17,18));
        check("Negative cartridge counts cannot verify a reload", !ReloadOutcomeAudit.Verified(true,true,true,true,-1,-1,17,17));
        check("Tactical native reload retains chamber and both magazine counts", ReloadOutcomeAudit.NativeCountsMatch(1,7,17,1,7,17));
        check("Empty native reload feeds exactly one cartridge from replacement", ReloadOutcomeAudit.NativeCountsMatch(0,0,17,1,0,16));
        check("Chambered cartridge cannot be fabricated from unchanged magazine", !ReloadOutcomeAudit.NativeCountsMatch(0,0,17,1,0,17));
        check("Old magazine cannot silently lose ammunition during reload", !ReloadOutcomeAudit.NativeCountsMatch(1,7,17,1,6,17));
        check("Empty compatible replacement cannot claim usable reload", !ReloadOutcomeAudit.NativeCountsMatch(0,0,0,0,0,0));
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition type,string name)=>type.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition method)=>method.HasBody?method.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition method,string name)=>Calls(method).Any(c=>c.Name==name);
        var service=pt["DualWieldLab.NativeReloadService"];
        var begin=Calls(M(service,"Begin")).Select(c=>c.Name).ToArray();
        check("Reload candidate preflight remains without any weapon holster or staging", begin.Contains("TryPlanReload")&&!begin.Contains("HolsterForSwap")&&!begin.Contains("SwapWeapons"));
        check("Reload admission revalidates native candidate on the unchanged active controller", Has(M(service,"StartNativeReload"),"TryPlanReload"));
        check("Native completion is verified from actual magazine and owner state", Has(M(service,"VerifyReloadOutcome"),"get_Owner")&&Has(M(service,"VerifyReloadOutcome"),"GetCurrentMagazine")&&Has(M(service,"VerifyReloadOutcome"),"SameAddress")&&Has(M(service,"VerifyReloadOutcome"),"Verified"));
        check("Native callback handler never directly sets verified reload success", !M(service,"FinishReload").Body.Instructions.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_1&&i.Next?.Operand is FieldReference f&&f.Name=="_reloadSucceeded"));
        check("Native service checks authoritative state before pair restoration", Has(M(service,"Update"),"VerifyReloadOutcome")&&Has(M(service,"Complete"),"AddressesMatch"));
        check("Native reload proof never edits ammunition or item addresses", !Calls(M(service,"VerifyReloadOutcome")).Any(c=>c.Name is "PopTo" or "SetFireMode" or "Add" or "Remove" or "set_ContainedItem" or "set_StackObjectsCount"));
        check("Reload diagnostics contain stage and actual magazine identities", M(service,"Trace").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("DW_RELOAD step="))&&Has(M(service,"Trace"),"GetCurrentMagazine"));
        var reload=nt["EFT.Player/FirearmController/ReloadExternalMagOperation"];
        check("Installed native reload callback follows idle operation and native success events", Has(M(reload,"SwitchToIdlingState"),"RaiseEvents")&&Has(M(reload,"SwitchToIdlingState"),"InitiateOperation")&&Has(M(reload,"SwitchToIdlingState"),"Succeed"));
    }
}
