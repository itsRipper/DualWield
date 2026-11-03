using System.Text.Json;
using DualWieldLab;
using Mono.Cecil;

internal static class MagazineCompatibilityChecks
{
    internal static void Run(Action<string,bool> check, string game, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        using var db=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json")));
        var items=db.RootElement;
        string[] Filter(string name) => items.EnumerateObject().Single(i=>i.Value.GetProperty("_name").GetString()==name).Value.GetProperty("_props").GetProperty("Slots").EnumerateArray().Single(s=>s.GetProperty("_name").GetString()=="mod_magazine").GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter").EnumerateArray().Select(v=>v.GetString()!).ToArray();
        int Capacity(string id)=>items.GetProperty(id).GetProperty("_props").GetProperty("Cartridges")[0].GetProperty("_max_count").GetInt32();
        var g17=Filter("weapon_glock_glock_17_gen3_9x19");var g18=Filter("weapon_glock_glock_18c_gen3_9x19");
        var capacities=g18.Select(Capacity).ToArray();
        check("Installed G18C native slot accepts distinct 17 and 50 round magazine templates", capacities.Contains(17)&&capacities.Contains(50)&&g18.Distinct().Count()>1);
        check("Installed G17 native slot also accepts 17 and 50 round magazines", g17.Select(Capacity).Contains(17)&&g17.Select(Capacity).Contains(50));
        check("Installed native magazine data contains more than two accepted capacities", capacities.Distinct().Count()>2);
        var m9=Filter("weapon_beretta_m9a3_9x19");
        check("Native slot filters distinguish other pistols despite matching cartridge family", m9.Except(g18).Any());

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var service=pt["DualWieldLab.NativeReloadService"];var plan=M(service,"TryPlanReload");
        check("Reload selection has no magazine-template equality gate",!Calls(plan).Any(c=>c.Name=="get_StringTemplateId"));
        check("Reload selection still uses native magazine slot and ammo compatibility",Has(plan,"CheckMoveIgnoringTargetItem")&&Has(plan,"IsAmmoCompatible")&&Has(plan,"GetReachableItemsOfTypeNonAlloc"));
        check("Reload reserves no return footprint because neither pistol changes address", !service.Methods.Any(m=>m.Name is "CanReturnWithMagazine" or "OverlapsReturnSpace")&&Has(M(service,"StartNativeReload"),"AddressesMatch"));
        check("Native magazine planning never assigns a different magazine or item address",!Calls(plan).Any(c=>c.Name is "set_ContainedItem" or "set_Parent" or "Move" or "Swap" or "Add" or "Remove"));
        check("Old magazine storage is selected through occupied native grid placement",Has(plan,"FindLocationForItem")||service.NestedTypes.SelectMany(t=>t.Methods).SelectMany(Calls).Any(c=>c.Name=="FindLocationForItem"));
        var compound=nt["EFT.InventoryLogic.CompoundItem"];
        check("Installed native projected weapon size uses merged attachment traversal",Has(M(compound,"GetSizeAfterAttachment"),"CalculateExtraSize")&&Has(M(compound,"GetSizeAfterAttachment"),"Apply"));
        var resize=M(nt["EFT.InventoryLogic.Grid"],"Resize");
        check("Installed native grid resize simulation restores occupancy through its own layout calls",Calls(resize).Count(c=>c.Name=="SetLayout")==2&&Has(resize,"CheckLayout")&&resize.Parameters.Any(p=>p.Name=="simulate"));
    }
}
