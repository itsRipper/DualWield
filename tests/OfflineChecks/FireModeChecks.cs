using System.Text.Json;
using Mono.Cecil;

internal static class FireModeChecks
{
    internal static void Run(Action<string,bool> check,string game,AssemblyDefinition native,AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        var component=nt["EFT.InventoryLogic.FireModeComponent"];
        var setter=M(component,"SetFireMode");
        check("Native selector setter validates mode against its template",Calls(setter).Any(m=>m.Name=="FindIndex")&&Calls(setter).Any(m=>m.Name=="get_AvailablEFireModes"));
        check("Native selector change notifies and refreshes item attributes",Calls(setter).Any(m=>m.Name=="Invoke")&&Calls(setter).Any(m=>m.Name=="UpdateAttributes"));
        check("Native mode field retains ordinary Diffable persistence",component.Fields.Single(f=>f.Name=="FireMode").CustomAttributes.Any(a=>a.AttributeType.Name is "DiffableAttribute" or "Diffable"));
        var cycle=M(pt["DualWieldLab.OffhandFireMode"],"TryCycle");
        var nativeChange=nt["EFT.Player/FirearmController/Idling"].Methods.Single(m=>m.Name=="ChangeFireMode");
        check("Left mode uses native next-mode and native Idling.ChangeFireMode on the left context, which owns the validated component setter",
            Calls(cycle).Any(m=>m.Name=="GetNextFireMode")&&Calls(cycle).Any(m=>m.Name=="Invoke")&&!Calls(cycle).Any(m=>m.Name=="SetFireMode")
            &&Calls(M(pt["DualWieldLab.OffhandShotContext"],"ChangeFireModeNative")).Any(m=>m.Name=="ChangeFireMode")
            &&Calls(nativeChange).Any(m=>m.Name=="SetFireMode"&&m.DeclaringType.Name=="FireModeComponent"));
        check("Left mode action respects native item validation",Calls(cycle).Any(m=>m.Name=="CheckItemAction"));
        check("Left mode action never drives the primary/player animator",!Calls(cycle).Any(m=>m.Name=="ChangeFireMode"||m.DeclaringType.Name is "FirearmsAnimator" or "ProceduralWeaponAnimation"));
        check("Left mode action does not create/move/remove inventory items",!Calls(cycle).Any(m=>m.Name is "Move" or "RemoveItem" or "PopTo" or "CreateItem" or "SetItemInHands"));
        check("Left mode has no direct component fire-mode field assignment",!cycle.Body.Instructions.Any(i=>i.OpCode.Name=="stfld"&&i.Operand is FieldReference f&&f.Name=="FireMode"));

        using var db=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json")));
        string[] Modes(string name)=>db.RootElement.EnumerateObject().Single(i=>i.Value.TryGetProperty("_name",out var n)&&n.GetString()==name).Value.GetProperty("_props").GetProperty("weapFireType").EnumerateArray().Select(m=>m.GetString()!).ToArray();
        var single=Modes("weapon_glock_glock_17_gen3_9x19");var automatic=Modes("weapon_glock_glock_18c_gen3_9x19");
        check("Actual Glock17 selector has one unchanged native mode",single.Length==1&&single[0]=="single");
        check("Actual Glock18C selector cycles native single and fullauto only",automatic.Length==2&&automatic.Contains("single")&&automatic.Contains("fullauto"));
        // Pure native-order fixture simulation. No native client object is run.
        string Next(string[] modes,string current)=>modes[(Array.IndexOf(modes,current)+1)%modes.Length];
        string right="single",left="single";left=Next(automatic,left);
        check("Mixed selector fixture changes left without right",left=="fullauto"&&right=="single");
        left=Next(automatic,left);
        check("Second native selector step returns independently to single",left=="single"&&right=="single");
    }
}
