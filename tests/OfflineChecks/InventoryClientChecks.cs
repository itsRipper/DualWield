using System.Text.Json;
using Mono.Cecil;

internal static class InventoryClientChecks
{
    internal static void Run(Action<string,bool> check,string game,AssemblyDefinition native)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var types=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        var eq=types["EFT.InventoryLogic.InventoryEquipment"];
        check("Native equipment constructors parse compiled equipment enum",eq.Methods.Where(m=>m.IsConstructor).SelectMany(Calls).Any(m=>m.DeclaringType.FullName=="System.Enum"&&m.Name=="Parse"));
        check("Native equipment uses fixed cached Slot array",eq.Fields.Any(f=>f.Name=="_cachedSlots"&&f.FieldType is ArrayType));
        check("Native equipment UI contains a single serialized holster view",types["EFT.UI.EquipmentTab"].Fields.Count(f=>f.Name=="_holsterSlot")==1);
        check("Native flat items preserve ID/template/parent/slot",new[]{"_id","_tpl","parentId","slotId"}.All(n=>types["JsonType.FlatItem"].Fields.Any(f=>f.Name==n)));
        check("Native flat tree explicitly rejects missing child containers",types["EFT.ItemFactory"].Methods.Where(m=>m.Name=="FlatItemsToTree").SelectMany(Calls).Any(m=>m.DeclaringType.Name.Contains("NoContainerWithIdError")));
        check("Native real slot address uses container descriptor",types["EFT.InventoryLogic.SlotItemAddress"].Methods.Where(m=>m.Name=="ToDescriptor").SelectMany(Calls).Any(m=>m.Name=="FromContainer"));
        using var db=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json")));
        var inventory=db.RootElement.GetProperty("55d7217a4bdc2d86028b456d").GetProperty("_props").GetProperty("Slots").EnumerateArray().Select(s=>s.GetProperty("_name").GetString()).ToArray();
        check("Installed root equipment defines15 real slots and oneHolster",inventory.Length==15&&inventory.Count(n=>n=="Holster")==1&&!inventory.Contains("HolsterLeft"));
    }
}
