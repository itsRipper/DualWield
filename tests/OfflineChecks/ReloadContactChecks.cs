using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class ReloadContactChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var types=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        var runtime=types["DualWieldLab.RuntimePlugin"];
        var apply=runtime.Methods.Single(m=>m.Name=="ApplyReloadPresentation");
        check("Reload leaves native palm and held magazine contact entirely to the native animator",!Calls(apply).Any(c=>c.Name is "HeldPosition" or "HeldRotation" or "CloseGrip" or "ApplySwap"));
        check("Reload borrows no weapon or magazine transform",!types.ContainsKey("DualWieldLab.ReloadPresentation"));
        check("Native reload overlay never reparents or moves inventory items",!Calls(apply).Any(c=>c.Name is "SetParent" or "set_parent" or "TryRunNetworkTransaction" or "AddItem"));
        check("Native pair acceptance is logged separately from completed inventory placement",runtime.Methods.Single(m=>m.Name=="BindReloadPair").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("DW_RELOAD_PAIR provisional=")));
    }
}
