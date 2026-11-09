using Mono.Cecil;
using Mono.Cecil.Cil;

// The renamed BepInEx identity and the one-time configuration migration that goes with it.
internal static class PluginIdentityChecks
{
    internal static void Run(Action<string, bool> check, AssemblyDefinition plugin)
    {
        var runtime = plugin.MainModule.GetType("DualWieldLab.RuntimePlugin");
        string? Attribute(string name) => runtime.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == name)?.ConstructorArguments[0].Value as string;
        check("Runtime plugin GUID is itsripper.dualwield", Attribute("BepInEx.BepInPlugin") == "itsripper.dualwield");
        check("Runtime plugin refuses to load next to its previous GUID", Attribute("BepInEx.BepInIncompatibility") == "local.dualwieldlab.glock17");
        check("Runtime plugin keeps the DualWield display name and version",
            runtime.CustomAttributes.Single(a => a.AttributeType.FullName == "BepInEx.BepInPlugin").ConstructorArguments.Select(a => a.Value as string).SequenceEqual(new[] { "itsripper.dualwield", "DualWield", "1.1.1" }));

        check("No on-screen IMGUI overlay: messages use EFT's own notifications", !runtime.Methods.Any(m => m.Name == "OnGUI")
            && runtime.Methods.Single(m => m.Name == "Notify").Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "DisplayMessageNotification"));
        var awake = runtime.Methods.Single(m => m.Name == "Awake").Body.Instructions.ToList();
        int Index(Func<Instruction, bool> match) => awake.FindIndex(i => match(i));
        int exists = Index(i => i.Operand is MethodReference m && m.Name == "Exists" && m.DeclaringType.FullName == "System.IO.File");
        int settings = Index(i => i.OpCode == OpCodes.Newobj && i.Operand is MethodReference m && m.DeclaringType.Name == "DualWieldSettings");
        int migrate = Index(i => i.Operand is MethodReference m && m.Name == "Apply" && m.DeclaringType.Name == "ConfigMigration");
        check("Runtime Awake: config existence is read before the first Bind, the migration runs right after binding",
            exists >= 0 && exists < settings && settings < migrate);
        check("Runtime Awake: the migration reads the previous runtime configuration file",
            plugin.MainModule.GetType("DualWieldLab.DualWieldSettings").Fields.Any(f => f.Name == "LegacyConfigFile" && (string)f.Constant == "local.dualwieldlab.glock17.cfg"));
    }
}
