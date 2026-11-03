using static CheckHelpers;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class LeftModifierChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        bool Text(MethodDefinition m, string value) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == value);

        var runtime = pt["DualWieldLab.RuntimePlugin"];
        var context = pt["DualWieldLab.OffhandShotContext"];
        var action = pt["DualWieldLab.OffhandNativeAction"];

        // Controls: one modifier on EFT's own weapon commands replaces the separate chords.
        var awake = SettingsCtor(plugin);
        check("Left-hand modifier is configurable; there are no separate left check shortcuts",
            Text(awake, "LeftHandModifier") && !new[]{"ExamineOffhand", "CheckChamberOffhand", "CheckMagazineOffhand"}.Any(n => Text(awake, n)));
        var command = M(runtime, "BeforeOwnerCommand");
        check("Modifier + native command is routed to the left pistol and blocked for the right",
            Has(command, "LeftModifierActive") && Has(command, "LeftCommandFor") && Has(command, "RouteLeftCommand"));
        var route = M(runtime, "RouteLeftCommand");
        check("Routed commands reuse the native left reload, selector and check paths once per frame",
            Has(route, "BeginReload") && Has(route, "ChangeOffhandModeOnce") && Has(route, "BeginOffhandService")
            && route.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_leftCommandFrame"));
        var ecommand = nt["EFT.InputSystem.ECommand"];
        string[] routed = ["ReloadWeapon", "QuickReloadWeapon", "ChangeWeaponMode", "CheckAmmo", "CheckChamber", "ExamineWeapon", "LeftStanceToggle", "SetLeftStance"];
        check("Every routed or suppressed command exists in the native ECommand enum",
            routed.All(n => ecommand.Fields.Any(f => f.Name == n)));

        // Native left reload: the left pistol's own animator plays EFT's reload states.
        var begin = M(context, "BeginNativeReload");
        check("Left reload starts the native reload states on the left animator (no reload operation fabricated)",
            Has(begin, "Reload") && Has(begin, "SetCanReload") && Has(begin, "SetIsExternalMag") && Has(begin, "BeginNativeAction")
            && !Calls(begin).Any(c => c.Name is "ReloadMag" or "InitiateOperation" && c is GenericInstanceMethod g && g.GenericArguments[0].Name.Contains("Reload")));
        string[] events = ["IEventsConsumerOnMagOut", "IEventsConsumerOnMagHide", "IEventsConsumerOnMagShow", "IEventsConsumerOnMagIn",
            "IEventsConsumerOnAddAmmoInChamber", "IEventsConsumerOnIdleStart"];
        check("Native reload events of the left model reach the left reload, otherwise the native handler",
            events.All(n => context.Methods.Any(m => m.Name == n && m.IsVirtual && Calls(m).Any(c => c.Name == n))));
        check("A left reload clip that never finishes is ended with the native idle state, not a fabricated result",
            Has(M(context, "FinishNativeAction"), "Play") && Has(M(context, "FinishNativeAction"), "CompleteNativeReload"));
        check("The left clip holds at the new magazine until the native swap settled",
            Has(M(runtime, "AdvanceOffhandReload"), "set_HoldNativeAnimation") && Has(M(runtime, "AdvanceOffhandReload"), "get_SwapSettled")
            && Has(M(runtime, "AdvanceOffhandReload"), "get_MagShown"));
        // The gun is the authority: EFT IKs both hands to the weapon markers.
        var clip = pt["DualWieldLab.NativeClipRetarget"];
        check("Left native actions move the gun by its mirrored clip and keep both palms on it",
            Has(M(action, "Apply"), "Sample") && Has(M(action, "Apply"), "FollowGun") && Has(M(action, "Apply"), "SupportPalm")
            && Calls(M(action, "Apply")).Count(c => c.Name == "ApplyGripTarget") == 2 && Has(M(action, "BeforeNativePose"), "BeforeNativePose"));
        check("Native clip retarget reads the weapon IK markers, not the raw arm bones, and undoes its writes",
            Has(M(clip, "Create"), "GetTransform") && Has(M(clip, "BeforeNativePose"), "Restore") && !Has(M(action, "Apply"), "UseNativeActionGrip"));
        check("Selector and malfunction clips in the paired stance use the same retarget",
            Has(M(runtime, "AlignModel"), "FollowGun") && Has(M(runtime, "BeforeNativeFrame"), "get_NativeClip"));
        check("Left context gets the native hands logger that InitiateOperation traces through",
            M(context, "Initialize").Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "Logger"
                && f.FieldType.Name == "HandsControllerLogger"));
        int quick = Convert.ToInt32(ecommand.Fields.Single(f => f.Name == "QuickReloadWeapon").Constant);
        check("Native double-tap R (QuickReloadWeapon) starts the managed right reload instead of an unmanaged one",
            M(runtime, "BeforeHandsCommand").Body.Instructions.Any(i => (i.OpCode == OpCodes.Ldc_I4 || i.OpCode == OpCodes.Ldc_I4_S) && Convert.ToInt32(i.Operand) == quick));
        check("Right reload remains EFT's native ReloadMag on the right controller",
            Has(M(pt["DualWieldLab.NativeReloadService"], "StartNativeReload"), "ReloadMag"));
    }
}
