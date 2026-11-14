using Mono.Cecil;

// Native holster and hands-process contracts of the controller lifecycle the pair follows.
// The paired reloads do not go through this route.
internal static class HolsterLifecycleChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var player=nt["EFT.Player"];var empty=M(player,"SetEmptyHands");
        check("Installed holster entry retains a checked native callback",empty.Parameters.Count==1&&empty.Parameters[0].ParameterType.FullName.Contains("IEmptyHandsController"));
        check("Installed empty-hands entry delegates to the ordinary native process",Has(empty,"Proceed"));
        var process=player.NestedTypes.Single(t=>t.Name=="Process`2");
        var calls=All(new[]{process}).SelectMany(t=>t.Methods).SelectMany(Calls).ToArray();
        check("Installed native hands process owns controller drop and destruction",calls.Any(c=>c.Name=="DropCurrentController")&&calls.Any(c=>c.Name=="DestroyController"));
        PooledModelChecks(check,nt,pt,Calls,M);
        var service=pt["DualWieldLab.NativeReloadService"];
        check("Primary reload cancellation invalidates all native callbacks",Has(M(service,"Clear"),"Invalidate")&&Has(M(service,"Cancel"),"Clear"));
        check("Primary reload completion preserves original addresses and magazine proof",Has(M(service,"Complete"),"AddressesMatch")&&Has(M(service,"Complete"),"VerifyReloadOutcome"));
        check("Mirrored active reload branch remains absent while left reload is disabled",!pt.ContainsKey("DualWieldLab.ReloadPresentation"));
        check("Primary reload never creates a fabricated cartridge or magazine",!All(new[]{service}).SelectMany(t=>t.Methods).SelectMany(Calls).Any(c=>c.Name is "set_StackObjectsCount" or "PopTo" or "CreateItem"));
    }

    // The Catch layer weight must not leak into the pool that inventory icons draw from.
    private static void PooledModelChecks(Action<string,bool> check, Dictionary<string,TypeDefinition> nt, Dictionary<string,TypeDefinition> pt,
        Func<MethodDefinition,IEnumerable<MethodReference>> calls, Func<TypeDefinition,string,MethodDefinition> method)
    {
        bool Has(MethodDefinition m,string n)=>calls(m).Any(c=>c.Name==n);
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        var unpair=method(runtime,"Unpair");
        var giveBack=method(runtime,"ReturnOffhandModel");
        var names=calls(giveBack).Select(c=>c.Name).ToList();
        check("Unpair returns the left model only through ReturnOffhandModel",Has(unpair,"ReturnOffhandModel")&&!Has(unpair,"ReturnToPool"));
        check("Left slide lock is cleared before the model returns to the pool",names.IndexOf("SetBoltCatch")>=0&&names.IndexOf("SetBoltCatch")<names.IndexOf("ReturnToPool"));
        check("Left model return is the only left-model ReturnToPool site in the runtime",pt.Values.Where(t=>t.FullName.StartsWith("DualWieldLab.RuntimePlugin")).SelectMany(t=>t.Methods).Count(m=>Has(m,"ReturnToPool"))==1);
        check("Installed EFT: SetBoltCatch sets the Catch layer weight",Has(method(nt["FirearmsAnimator"],"SetBoltCatch"),"SetLayerWeight"));
        var prefab=nt["WeaponPrefab"];
        var resets=new[]{"ReturnToPool","ResetAnimator","ResetStatesToDefault"}
            .SelectMany(n=>prefab.Methods.Where(m=>m.Name==n)).ToArray();
        check("Installed EFT: WeaponPrefab return to pool never resets layer weights",resets.Length>0&&!resets.Any(m=>Has(m,"SetLayerWeight")));
        check("Installed EFT: icon models come from the shared pool",nt.Values.Where(t=>t.FullName.StartsWith("EFT.ObjectsFactory/CG_CreateItemAsync")).SelectMany(t=>t.Methods).Any(m=>Has(m,"PopOrCreate")));
        check("Installed EFT: a reused model is only re-evaluated through Animator.Update",Has(method(prefab,"RevertMalfunctionState"),"Update"));
    }
}
