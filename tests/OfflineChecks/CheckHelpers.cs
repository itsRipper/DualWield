using Mono.Cecil;

internal static class CheckHelpers
{
    // Configuration bindings live in DualWieldSettings' constructor.
    internal static MethodDefinition SettingsCtor(AssemblyDefinition plugin)
        => plugin.MainModule.GetType("DualWieldLab.DualWieldSettings").Methods.Single(m => m.IsConstructor && !m.IsStatic);
}
