using DualWieldLab;
using Mono.Cecil;
using Mono.Cecil.Cil;

// A failure after an inventory mutation stops only that pair for the raid and is never retried.
internal static class FailureScopeChecks
{
    internal static void Run(Action<string, bool> check, ModuleDefinition plugin)
    {
        var quarantine = new PairQuarantine();
        check("A fresh raid quarantines nothing", !quarantine.Contains("glock", "pm"));
        quarantine.Add("glock", "pm");
        check("The failed pair stays refused in either hand", quarantine.Contains("glock", "pm") && quarantine.Contains("pm", "glock"));
        check("Each pistol of the failed pair still pairs with another pistol", !quarantine.Contains("glock", "m1911") && !quarantine.Contains("usp", "pm"));
        quarantine.Add("usp", "fn57");
        check("Several failed pairs are kept independently", quarantine.Contains("fn57", "usp") && quarantine.Contains("glock", "pm"));
        quarantine.Clear();
        check("The raid end clears every quarantined pair", !quarantine.Contains("glock", "pm") && !quarantine.Contains("usp", "fn57"));

        var types = AllTypes(plugin.Types).ToDictionary(t => t.FullName);
        var runtime = types["DualWieldLab.RuntimePlugin"];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>();
        bool WritesField(MethodDefinition m, string name) => m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == name);

        check("A post-mutation shot failure quarantines the pair instead of disabling DualWield",
            Calls(M(runtime, "TryFire")).Any(c => c.Name == "QuarantinePair") && !WritesField(M(runtime, "TryFire"), "_faulted"));
        check("Pairing refuses a quarantined pair", Calls(M(runtime, "TryPair")).Any(c => c.Name == "Contains" && c.DeclaringType.Name == "PairQuarantine"));
        check("The raid end clears the quarantine", Calls(M(runtime, "EndSession")).Any(c => c.Name == "Clear" && c.DeclaringType.Name == "PairQuarantine"));

        var fire = M(types["DualWieldLab.OffhandShotContext"], "FireOnce");
        var calls = fire.Body.Instructions.Select(i => i.Operand).ToList();
        int projectiles = calls.FindIndex(o => (o is MethodReference m && m.Name == "get_ProjectileCount") || (o is FieldReference f && f.Name == "ProjectileCount"));
        // FireOnce first resets MutationStarted to false; the mutation begins at the last write.
        int mutation = calls.FindLastIndex(o => o is MethodReference m && m.Name == "set_MutationStarted");
        check("The expected projectile count (buckshot: one per pellet) is read from the round before any mutation",
            projectiles >= 0 && mutation > projectiles);
        check("No fixed single-projectile comparison remains in the shot path",
            !fire.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_projectileCount"
                && i.Next?.OpCode == OpCodes.Ldc_I4_1));
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
}
