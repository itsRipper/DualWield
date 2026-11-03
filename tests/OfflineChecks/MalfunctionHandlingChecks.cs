using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class MalfunctionHandlingChecks
{
    internal static void Run(Action<string,bool> check, string project, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        MethodDefinition MP(TypeDefinition t, string name, int parameters) => t.Methods.Single(m => m.Name == name && m.Parameters.Count == parameters);
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        bool IsInst(MethodDefinition m, string type) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand is TypeReference t && t.Name == type);
        // Overridden Start() calls reference the base virtual; the started operation is the
        // generic argument of InitiateOperation<T>.
        bool Initiates(MethodDefinition m, string op) => Calls(m).OfType<GenericInstanceMethod>()
            .Any(g => g.Name == "InitiateOperation" && g.GenericArguments[0].Name == op);
        int? Int(Instruction i) => i.OpCode.Code switch
        {
            Code.Ldc_I4_M1 => -1, Code.Ldc_I4_0 => 0, Code.Ldc_I4_1 => 1, Code.Ldc_I4_2 => 2, Code.Ldc_I4_3 => 3,
            Code.Ldc_I4_4 => 4, Code.Ldc_I4_5 => 5, Code.Ldc_I4_6 => 6, Code.Ldc_I4_7 => 7, Code.Ldc_I4_8 => 8,
            Code.Ldc_I4 or Code.Ldc_I4_S => Convert.ToInt32(i.Operand), _ => null
        };
        bool Text(MethodDefinition m, string value) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == value);

        var runtime = pt["DualWieldLab.RuntimePlugin"];
        var context = pt["DualWieldLab.OffhandShotContext"];
        var fc = "EFT.Player/FirearmController/";

        // Root cause of the vanishing offhand: a native left malfunction unpaired the pistols.
        check("A left or right malfunction does not end the pair",
            !Has(M(runtime, "PairedStopReason"), "get_MalfState") && Has(M(runtime, "TryFire"), "ServiceHint")
            && !M(runtime, "TryFire").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("Equip that pistol")));
        check("Malfunctioned pistols may pair and keep their native state",
            Calls(M(runtime, "TryPair")).Count(c => c.Name == "GetRefusal") >= 2 && Has(MP(context, "IsEligible", 1), "IsEligible"));

        var nativeSwitch = nt[fc + "AutomaticFireOperation"].Methods.Single(m => m.Name == "SwitchToMalfunction");
        var nativeSemi = nt[fc + "FireOperation"].Methods.Single(m => m.Name == "SwitchToMalfunction");
        var mine = M(context, "SwitchToNativeMalfunction");
        check("Left malfunction follows native SwitchToMalfunction: animator state, then StartMalfunction",
            Has(nativeSwitch, "Malfunction") && Has(nativeSwitch, "Play") && Has(mine, "Malfunction") && Has(mine, "Play")
            && Has(nativeSemi, "Malfunction") && Initiates(nativeSemi, "StartMalfunction")
            && Initiates(nativeSwitch, "StartMalfunction") && Initiates(mine, "StartMalfunction"));
        var check3 = M(context, "TryNativeCheck");
        check("Left examine, chamber check and magazine check call the native Idling entry points",
            Has(check3, "ExamineWeapon") && Has(check3, "CheckChamber") && Has(check3, "CheckAmmo"));
        check("Native examine identifies and native chamber check repairs a known malfunction",
            Initiates(nt[fc + "Idling"].Methods.Single(m => m.Name == "ExamineWeapon"), "ExamineMalfunctionTypeOperation")
            && Initiates(nt[fc + "Idling"].Methods.Single(m => m.Name == "CheckChamber"), "RepairMalfunction"));
        check("Native repair performs its own chamber feed transaction",
            Calls(M(nt[fc + "RepairMalfunction"], "OnAddAmmoInChamber")).Any(c => c.DeclaringType.Name.Contains("RepairFeed")));
        check("Native Idling.Start writes shared procedural state; the offhand context parks instead",
            Has(MP(nt[fc + "Idling"], "Start", 1), "SetAnimatorAndProceduralValues")
            && Has(M(runtime, "BeforeIdlingStart"), "ParkIdle") && !Has(M(context, "ParkIdle"), "SetAnimatorAndProceduralValues"));
        check("Offhand animation events reach the context only while a native operation runs",
            Has(M(context, "PumpEvents"), "EmitEvents") && Has(M(context, "PumpEvents"), "Clear")
            && Has(M(context, "BeginNativeAction"), "AddEventsConsumer") && !Has(M(context, "ParkIdle"), "RemoveEventsConsumer"));
        check("A stuck or interrupted native operation completes through its own native FastForward",
            Has(M(context, "FinishNativeAction"), "FastForward") && Has(M(context, "DisposeContext"), "FinishNativeAction"));
        check("A malfunctioned left trigger follows native rules: knowledge only, no shot",
            Has(M(context, "PressMalfunctionedTrigger"), "ExamineMalfunction") && !Has(M(context, "PressMalfunctionedTrigger"), "FireOnce"));
        // FireOnce only records the state the native shot sampled; nothing else writes or repairs it.
        check("No custom repair: the plugin never clears malfunction state itself",
            !pt.Values.SelectMany(t => t.Methods).Any(m => Calls(m).Any(c => c.DeclaringType.Name.Contains("Malf")
                && (c.Name == "Repair" || c.Name == "ChangeStateSilent" || (c.Name == "set_State" && m.Name != "FireOnce")))));

        var action = pt["DualWieldLab.OffhandNativeAction"];
        check("Left service lowers the right pistol and gives the native clip its second hand",
            Has(M(action, "Apply"), "SupportPalm") && Has(M(action, "Apply"), "SolveRight") && Has(M(action, "Apply"), "Follow"));
        check("The right pistol carry never reparents",
            !pt["DualWieldLab.PrimaryPistolCarry"].Methods.SelectMany(Calls).Any(c => c.Name is "SetParent" or "set_parent"));
        check("Right-pistol malfunction identification and repair count as two-handed native checks",
            IsInst(M(runtime, "IsTwoHandedUtility"), "ExamineMalfunctionTypeOperation") && IsInst(M(runtime, "IsTwoHandedUtility"), "RepairMalfunction"));
        check("Managed reloads refuse a malfunctioned pistol instead of bypassing native clearing",
            Has(M(runtime, "BeginReload"), "get_MalfState"));

        int checkFireMode = Convert.ToInt32(nt["EFT.InputSystem.ECommand"].Fields.Single(f => f.Name == "CheckFireMode").Constant);
        var owner = M(runtime, "BeforeOwnerCommand");
        check("Alt+B changes only the left selector: the native right CheckFireMode chord is blocked",
            owner.Body.Instructions.Any(i => Int(i) == checkFireMode));
        check("Modifier + EFT's CheckFireMode runs the native fire-mode check on the left pistol's context",
            Has(M(runtime, "RouteLeftCommand"), "CheckOffhandFireMode") && Has(M(runtime, "CheckOffhandFireMode"), "TryNativeCheck")
            && Has(M(context, "TryNativeCheck"), "CheckFireMode") && Has(M(runtime, "CheckOffhandFireMode"), "ShowNativeFireMode"));
        check("The left fire-mode shortcut yields to the left-hand modifier chord (Mouse4+Alt+B is the left check)",
            Has(M(runtime, "ModeShortcutDown"), "LeftModifierActive") && Has(owner, "ModeShortcutDown") && Has(M(runtime, "HandleShortcut"), "ModeShortcutDown")
            && Has(M(runtime, "Update"), "ModeShortcutDown"));
        int[] throws = new[] { "TryHighThrow", "TryLowThrow" }.Select(n => Convert.ToInt32(nt["EFT.InputSystem.ECommand"].Fields.Single(f => f.Name == n).Constant)).ToArray();
        check("EFT's throw commands (sent for Shoot/Aim whatever is in hand) never interrupt a left or right service",
            !M(runtime, "IsServiceItemAction").Body.Instructions.Any(i => Int(i) is int v && throws.Contains(v)));
        var hand = pt["DualWieldLab.OffhandHandPose"];
        check("The left selector change bends no finger onto the selector (it would pass through the gun)",
            !hand.Methods.Any(m => m.Name == "ApplySelectorReach") && !hand.Methods.Any(m => Text(m, "weapon_selector")));

        foreach (var (name, markers) in new[] { ("dw_offhand_service", new[] { "reach_end", "release_start", "release_end" }) })
        {
            string path = Path.Combine(project, "assets", "exports", "unity", name + ".json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            check("Blender clip " + name + " exported from dw_reload_choreography.blend with its markers",
                root.GetProperty("source").GetString()!.StartsWith("dw_reload_choreography.blend#")
                && markers.All(m => root.GetProperty("markers").TryGetProperty(m, out _)));
        }
        using var reload = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "assets", "exports", "unity", "dw_offhand_reload.json")));
        var marks = reload.RootElement.GetProperty("markers");
        check("Right draw for left service starts where the Alt+R right hand is still lowered",
            marks.GetProperty("draw_start").GetInt32() < marks.GetProperty("snd:R:SndDraw").GetInt32()
            && marks.GetProperty("draw_start").GetInt32() > marks.GetProperty("rack_end").GetInt32());
    }
}
