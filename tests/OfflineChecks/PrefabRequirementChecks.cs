using DualWieldLab;
using Mono.Cecil;

// Incomplete pistol models: which missing part refuses a pair, which only degrades it, and that
// refusal precedes any shot context.
internal static class PrefabRequirementChecks
{
    internal static void Run(Action<string, bool> check, ModuleDefinition plugin, ModuleDefinition native)
    {
        PrefabPart complete = PrefabRequirements.Required | PrefabRequirements.Optional;
        check("A complete native pistol model pairs with nothing degraded",
            PrefabRequirements.Refusal(complete) == null && PrefabRequirements.MissingOptional(complete, true) == PrefabPart.None
            && PrefabRequirements.NativeLeftActions(complete) && PrefabRequirements.SelectorAnimated(complete));

        bool everyRequiredRefuses = true, everyRequiredNamed = true;
        foreach (PrefabPart part in Enum.GetValues(typeof(PrefabPart)))
        {
            if ((PrefabRequirements.Required & part) == 0 || part == PrefabPart.None) continue;
            string? refusal = PrefabRequirements.Refusal(complete & ~part);
            everyRequiredRefuses &= refusal != null;
            everyRequiredNamed &= refusal != null && refusal == "its model lacks " + PrefabRequirements.Describe(part) && !refusal.Contains(',');
        }
        check("Every essential model part refuses the pair when missing", everyRequiredRefuses);
        check("A refusal names exactly the missing part", everyRequiredNamed);

        bool optionalOnlyDegrades = true;
        foreach (PrefabPart part in Enum.GetValues(typeof(PrefabPart)))
        {
            if ((PrefabRequirements.Optional & part) == 0 || part == PrefabPart.None) continue;
            optionalOnlyDegrades &= PrefabRequirements.Refusal(complete & ~part) == null && PrefabRequirements.MissingOptional(complete & ~part, true) == part;
        }
        check("Missing optional parts never refuse and are reported as degradations", optionalOnlyDegrades);
        check("A loop-based automatic sound bank without its tail is refused (EFT's FireBullet dereferences the tail)",
            PrefabRequirements.Refusal(complete & ~PrefabPart.SoundTail)?.Contains("automatic_sound_tail") == true);
        check("No gunshot bank or no renderer only degrades: EFT fires such a pistol silently or unseen",
            PrefabRequirements.Refusal(complete & ~PrefabPart.SoundBody & ~PrefabPart.Geometry) == null
            && PrefabRequirements.MissingOptional(complete & ~PrefabPart.SoundBody & ~PrefabPart.Geometry, true) == (PrefabPart.SoundBody | PrefabPart.Geometry));
        check("Fireport presence is one requirement (EFT's resolution), not a count of fireports",
            PrefabRequirements.Refusal(complete & ~PrefabPart.Fireport) == "its model lacks fireport"
            && !Enum.GetNames(typeof(PrefabPart)).Any(n => n.Contains("Single")));
        // WeaponSoundPlayer: (one-shot, body, tail, silenced body, silenced tail).
        check("Sound: a one-shot bank never needs a tail", PrefabRequirements.LoopTailAvailable(true, true, false, true, false));
        check("Sound: no bank at all needs no tail (FireBullet returns before the loop)", PrefabRequirements.LoopTailAvailable(false, false, false, false, false));
        check("Sound: a looping bank with its tail is complete", PrefabRequirements.LoopTailAvailable(false, true, true, false, false));
        check("Sound: a looping bank without any tail is incomplete", !PrefabRequirements.LoopTailAvailable(false, true, false, false, false));
        check("Sound: a silenced loop falls back to the plain tail, as WeaponSoundPlayer.IsSilenced does",
            PrefabRequirements.LoopTailAvailable(false, true, true, true, false) && PrefabRequirements.LoopTailAvailable(false, false, true, true, false));
        check("Sound: a silenced tail alone does not end the unsuppressed loop", !PrefabRequirements.LoopTailAvailable(false, true, false, true, true));
        check("A single-fire-mode pistol without selector states is not reported as degraded",
            PrefabRequirements.MissingOptional(complete & ~PrefabPart.SelectorStates, false) == PrefabPart.None
            && PrefabRequirements.MissingOptional(complete & ~PrefabPart.SelectorStates, true) == PrefabPart.SelectorStates);
        check("No support-hand marker or magazine node: native left actions fall back to the inventory-only path",
            !PrefabRequirements.NativeLeftActions(complete & ~PrefabPart.LeftIkMarker) && !PrefabRequirements.NativeLeftActions(complete & ~PrefabPart.MagazineNode)
            && PrefabRequirements.NativeLeftActions(complete & ~PrefabPart.RightIkMarker));
        check("No selector states: the left fire-mode change does not wait for an animation",
            !PrefabRequirements.SelectorAnimated(complete & ~PrefabPart.SelectorStates));
        check("Several missing parts are all listed", PrefabRequirements.Describe(PrefabPart.Shellport | PrefabPart.ShellExtraction) == "shellport,shell_extraction_data");

        var types = AllTypes(plugin.Types).ToDictionary(t => t.FullName);
        int Position(MethodDefinition method, string declaringType, string name)
        {
            var calls = method.Body.Instructions.Select(i => i.Operand).ToList();
            return calls.FindIndex(o => o is MethodReference m && m.Name == name && m.DeclaringType.Name == declaringType);
        }
        var tryPair = types["DualWieldLab.RuntimePlugin"].Methods.Single(m => m.Name == "TryPair");
        int inspect = Position(tryPair, "PrefabProbe", "Inspect");
        check("Pairing inspects the model before the visual rig and the shot context are built",
            inspect >= 0 && inspect < Position(tryPair, "OffhandVisualRig", "Create") && inspect < Position(tryPair, "OffhandShotContext", "Initialize"));
        var bind = types["DualWieldLab.RemoteDualPistols"].Methods.Single(m => m.Name == "Bind");
        int remoteInspect = Position(bind, "PrefabProbe", "Inspect");
        check("A remote left pistol is inspected before its replay context is built",
            remoteInspect >= 0 && remoteInspect < Position(bind, "OffhandShotContext", "Initialize"));
        var nativeTypes = AllTypes(native.Types).ToDictionary(t => t.FullName);
        bool FindsFireportRecursively(MethodDefinition method)
        {
            var ops = method.Body.Instructions.Select(i => i.Operand).ToList();
            return ops.Any(o => o is MethodReference m && m.Name == "FindTransformRecursive" && m.DeclaringType.Name == "TransformTools")
                && ops.Any(o => o is string text && text == "fireport");
        }
        check("EFT resolves the muzzle as the first fireport depth-first under Weapon_root (PlayerBones.FindFireport)",
            FindsFireportRecursively(nativeTypes["PlayerBones"].Methods.Single(m => m.Name == "FindFireport")));
        var nativeFireport = types["DualWieldLab.PrefabProbe"].Methods.Single(m => m.Name == "NativeFireport");
        check("The left pistol resolves its muzzle with EFT's own rule, not a count of active fireports",
            FindsFireportRecursively(nativeFireport)
            && !types.Values.SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
                .Any(i => i.Operand is string text && text.Contains("Multiple active offhand fireports")));
        check("The visual rig, the shot context's fireport and the remote view all use that muzzle",
            Position(types["DualWieldLab.OffhandVisualRig"].Methods.Single(m => m.IsConstructor && !m.IsStatic), "PrefabProbe", "NativeFireport") >= 0
            && Position(bind, "PrefabProbe", "NativeFireport") >= 0
            && Position(tryPair, "OffhandVisualRig", "get_Fireport") >= 0);
        check("EFT resolves shell port, chamber visuals and shell ejection data when it pools the model; the probe reads those",
            new[] { "SetupShellPortTransform", "SetupPatronInWeaponTransform" }.All(name => Position(nativeTypes["EFT.Firearms"].Methods.Single(m => m.Name == "OnCreatePoolObject"), "Firearms", name) >= 0)
            && new[] { "_shellPortsTransforms", "_patronInWeaponTransforms", "_shellExtractionData" }.All(field =>
                types["DualWieldLab.PrefabProbe"].Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.Operand is FieldReference f && f.Name == field)));
        check("Probing never runs in frame methods", !types["DualWieldLab.RuntimePlugin"].Methods
            .Where(m => m.HasBody && new[] { "Update", "LateUpdate", "AlignModel", "TryFire" }.Contains(m.Name))
            .Any(m => Position(m, "PrefabProbe", "Inspect") >= 0));
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
}
