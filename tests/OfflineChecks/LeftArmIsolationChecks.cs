using Mono.Cecil;
using Mono.Cecil.Cil;

// Right-pistol actions (fire-mode clip, recoil) are not mirrored onto the left arm, and holding
// the left pose does not move the left pistol.
internal static class LeftArmIsolationChecks
{
    internal static void Run(Action<string, bool> check, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] { t }.Concat(All(t.NestedTypes)));
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        MethodDefinition M(string type, string name) => pt["DualWieldLab." + type].Methods.Single(m => m.Name == name);
        bool Calls(MethodDefinition m, string name) => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == name);
        var target = M("NativeArmRetarget", "get_LeftPalmTarget");
        check("A held left pose keeps its palm target relative to the shoulders' common parent, not the animated shoulders",
            Calls(target, "TransformPoint") && target.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_shoulderParent")
            && Calls(M("NativeArmRetarget", "CaptureNativePose"), "InverseTransformPoint") && !Calls(target, "InverseTransformPoint"));
        check("The left mirrors the right arm without EFT's procedural recoil on the right weapon root (no timed hold)",
            Calls(M("NativeArmRetarget", "CaptureNativePose"), "PrimaryRecoil") && Calls(M("NativeArmRetarget", "PrimaryRecoil"), "TryGet")
            && !pt["DualWieldLab.OffhandHandPose"].Fields.Any(f => f.Name == "PrimaryRecoilHoldSeconds")
            && pt["DualWieldLab.NativeArmRetarget"].Fields.Any(f => f.Name == "ReleaseSeconds"));
        check("The recoil is sampled when EFT applies it, not after a mid-frame shot changed it",
            Calls(M("RuntimePlugin", "AfterRecoilApplied"), "Record")
            && pt["DualWieldLab.RuntimePlugin"].Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "ApplyComplexRotation")));
        check("A left fire-mode change plays the pistol's own selector click (Selector, as the native right clip), delayed like it",
            M("RuntimePlugin", "PlayPendingSelectorSound").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Selector")
            && Calls(M("RuntimePlugin", "Update"), "PlayPendingSelectorSound"));
        check("A held left target turns with the live look rotation (mouse pitch while firing), anchored on the shoulders' parent",
            Calls(target, "get_TargetHandsRotation") && Calls(M("NativeArmRetarget", "CaptureNativePose"), "get_TargetHandsRotation"));
        check("A left shot pushes only EFT's camera recoil channel",
            Calls(M("RuntimePlugin", "TryFire"), "ApplyLeftCameraRecoil") && Calls(M("RuntimePlugin", "ApplyLeftCameraRecoil"), "Process")
            && !Calls(M("RuntimePlugin", "ApplyLeftCameraRecoil"), "AddRecoilForce") && !Calls(M("RuntimePlugin", "ApplyLeftCameraRecoil"), "Shoot"));
    }
}
