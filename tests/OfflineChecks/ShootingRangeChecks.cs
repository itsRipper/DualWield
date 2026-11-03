using DualWieldLab;
using Mono.Cecil;

internal static class ShootingRangeChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        check("Native range clone with ready local owner is admitted",HideoutRangePolicy.Refusal(true,false,false,false,false,true,true)==null);
        check("Ordinary hideout walking remains excluded",HideoutRangePolicy.Refusal(false,false,false,false,false,true,true)!=null);
        check("Guest hideout ownership remains excluded",HideoutRangePolicy.Refusal(true,true,false,false,false,true,true)!=null);
        check("Range entry/refresh cannot bind partially updated clone items",HideoutRangePolicy.Refusal(true,false,true,false,false,true,true)!=null);
        check("Native patrol restrictions are retained",HideoutRangePolicy.Refusal(true,false,false,true,false,true,true)!=null);
        check("Native blocked firearms remain blocked in range",HideoutRangePolicy.Refusal(true,false,false,false,true,true,true)!=null);
        check("Active persistent inventory cannot pass as a range clone",HideoutRangePolicy.Refusal(true,false,false,false,false,false,true)!=null);
        check("Shared original/range inventory references fail closed",HideoutRangePolicy.Refusal(true,false,false,false,false,true,false)!=null);
        check("Leaving range revokes admission before native clone discard",HideoutRangePolicy.Refusal(false,false,true,false,false,true,true)!=null);
        check("Raid admission is outside this range-only adapter",HideoutRangePolicy.Refusal(false,false,false,false,false,false,false)!=null);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        IEnumerable<MethodReference> Generated(TypeDefinition type,string method)=>new[]{type}.Concat(type.NestedTypes).SelectMany(t=>t.Methods)
            .Where(m=>m.Name==method || m.Name=="MoveNext"&&(m.DeclaringType.Name=="CG_"+method||m.DeclaringType.Name.Contains("<"+method+">")))
            .SelectMany(Calls);
        var player=nt["EFT.HideoutPlayer"];
        var owner=nt["EFT.HideoutPlayerOwner"];
        var create=Generated(player,"Create").ToArray();
        var update=Generated(player,"UpdateHideoutPlayerInventory").ToArray();
        var release=Generated(player,"ReleaseShootingRangeInventory").ToArray();
        check("Installed hideout constructs its range inventory from a cloned profile",create.Any(c=>c.Name=="Clone")&&create.Any(c=>c.DeclaringType.Name=="SinglePlayerInventoryController"&&c.Name==".ctor"));
        check("Installed native range entry clones original item subtrees",update.Any(c=>c.Name=="CloneItem")&&update.Any(c=>c.Name=="AddWithoutRestrictions"));
        check("Installed range exit discards native cloned equipment",release.Any(c=>c.Name=="RemoveWithoutRestrictions"));
        check("Installed range release does not save or copy clone ammunition to profile",!release.Any(c=>c.Name is "Save" or "SaveProfile" or "CloneItem" or "AddWithoutRestrictions"));
        check("Native hideout owner still derives from the ordinary game player owner",nt["EFT.CommonPlayerOwner`1"].BaseType.Name=="GamePlayerOwner"&&owner.BaseType.Name=="EftGamePlayerOwner");
        check("Range state chooses actual native hands input instead of dummy input",Has(M(owner,"get_HandsInputTranslator"),"get_InShootingRange"));
        var adapter=pt["DualWieldLab.HideoutRangeContext"];
        check("Range admission reads actual cloned/original inventory and refresh state",new[]{"get_ShootingRangeInventory","get_OriginalInventory","get_IsUpdateHideoutPlayerInventoryInProgress","get_InShootingRange"}.All(n=>Has(M(adapter,"Refusal"),n)));
        var audit=M(adapter,"ValidateWeapons");
        bool referenceComparison=Has(audit,"ReferenceEquals")||audit.Body.Instructions.Any(i=>i.OpCode.Code is Mono.Cecil.Cil.Code.Ceq or Mono.Cecil.Cil.Code.Beq or Mono.Cecil.Cil.Code.Beq_S or Mono.Cecil.Cil.Code.Bne_Un or Mono.Cecil.Cil.Code.Bne_Un_S);
        check("Range item validation uses object identity and native ownership",referenceComparison&&Has(audit,"get_Owner"));
        check("Range subtree audit is isolated from the recurring admission path",!Has(M(adapter,"Refusal"),"GetAllItemsNonAlloc")&&Has(M(adapter,"ValidateWeapons"),"GetAllItemsNonAlloc"));
        check("Hideout adapter mutates no inventory, weapon, profile or native range flag",!adapter.Methods.SelectMany(Calls).Any(c=>c.Name is "AddWithoutRestrictions" or "RemoveWithoutRestrictions" or "CloneItem" or "SaveProfile" or "set_InShootingRange" or "SetPatrol" or "Swap" or "ReloadMag"));
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        check("Hideout pair admission validates both native clone context and complete pistol subtrees",Has(M(runtime,"TryPair"),"Refusal")&&Has(M(runtime,"TryPair"),"ValidateWeapons"));
        check("Native range scope is checked during paired and input admission",Has(M(runtime,"PairedStopReason"),"Refusal")&&Has(M(pt["DualWieldLab.PairedInput"],"AllowsGameplay"),"Refusal"));
        check("Native range inventory refresh and exit stop the shared session before clone disposal",Has(M(runtime,"BeforeRangeInventoryChange"),"EndSession")&&Has(M(runtime,"BeforeRangeStatus"),"EndSession"));
        check("Deferred reload restoration retains range clone admission",Has(M(runtime,"ReloadNativeReady"),"Refusal"));
        check("Range lifecycle hooks are limited to the actual local range actor",Has(M(runtime,"BeforeRangeInventoryChange"),"IsCurrentRangePlayer")&&Has(M(runtime,"BeforeRangeStatus"),"IsCurrentRangePlayer"));
    }
}
