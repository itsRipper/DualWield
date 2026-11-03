using Mono.Cecil;

internal static class MalfunctionSamplingChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] {t}.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        MethodDefinition M(TypeDefinition t, string n) => t.Methods.Single(m => m.Name == n);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        bool Has(MethodDefinition m, string n) => Calls(m).Any(c => c.Name == n);
        var context = pt["DualWieldLab.OffhandShotContext"];
        var fire = M(context,"FireOnce");
        var nativeFire = M(nt["EFT.Player/FirearmController/FireOperationBase"],"ProcessInventoryOperationsForSingleshotWithMalf");
        check("Malfunction probability never becomes a synthetic weapon lock", !context.Methods.Any(m => m.Name == "GetSafetyRefusal") && !pt["DualWieldLab.PistolMechanics"].Methods.Any(m => m.Name == "HasSafeOffhandConditions"));
        check("Native firing context uses the actual player malfunction random stream", M(context,"Initialize").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_malfRandoms"));
        check("Each shot delegates malfunction sampling exactly once", Calls(fire).Count(c => c.Name == "ProcessInventoryOperationsForSingleshotWithMalf") == 1);
        check("Delegated native path samples heat and validates actual generated malfunction", Has(nativeFire,"GetCurrentOverheat") && Has(nativeFire,"GetMalfunctionState") && Has(nativeFire,"ValidateMalfunction"));
        check("Native forced misfire effects remain inside delegated native policy", Has(nativeFire,"FindActiveEffect"));
        check("Native misfire state uses native StartMalfunction without a second projectile", Has(fire,"SwitchToNativeMalfunction") && Calls(M(context,"SwitchToNativeMalfunction")).Any(c => c.Name == "Start" && c.DeclaringType.Name is "StartMalfunction" or "MalfunctionOperation") && !Calls(M(nt["EFT.Player/FirearmController/StartMalfunction"],"Start")).Any(c => c.Name is "Shot" or "InitiateShot"));
        check("Native failure preserves the three authoritative repair ammo references", new[] {"AmmoToFire","AmmoWillBeLoadedToChamber","MalfunctionedAmmo"}.All(n => fire.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == n)));
        check("Native failed feed participates in offhand ammo accounting", fire.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "FedAmmo"));
        check("No manual ammo count or chamber item rewrite was added", !Calls(fire).Any(c => c.Name is "set_StackObjectsCount" or "set_ContainedItem" or "AddItem" or "PopTo" or "PopToNowhere"));
        check("Dry empty chamber exits before native inventory mutation", Array.FindIndex(fire.Body.Instructions.ToArray(), i => i.Operand is MethodReference m && m.Name == "Dry") < Array.FindIndex(fire.Body.Instructions.ToArray(), i => i.Operand is MethodReference m && m.Name == "ProcessInventoryOperationsForSingleshotWithMalf"));
        var runtime = pt["DualWieldLab.RuntimePlugin"];
        check("Native malfunction outcome is distinguished from an empty magazine", Has(M(runtime,"TryFire"),"get_Malfunction"));
        check("Left selector does not stop the other weapon's trigger", !Has(M(runtime,"HandleShortcut"),"StopPrimaryTrigger"));
        check("Left selector validation allows primary firing without reading its trigger flag", !Has(M(pt["DualWieldLab.OffhandFireMode"],"TryCycle"),"get_IsTriggerPressed"));
        check("Technical errors distinguish the native mutation boundary", Has(M(runtime,"TryFire"),"get_MutationStarted") && Has(fire,"set_MutationStarted"));
        check("Selector shortcut deduplication does not claim the pair-wide input barrier", M(runtime,"ChangeOffhandModeOnce").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_modeShortcutFrame"));
        check("Camera diagnostics are opt-in and use the native hands look", Has(M(runtime,"LateUpdate"),"get_TargetHandsRotation") && Has(M(runtime,"LateUpdate"),"get_Value"));
    }
}
