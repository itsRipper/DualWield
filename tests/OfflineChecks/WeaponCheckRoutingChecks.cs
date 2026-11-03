using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class WeaponCheckRoutingChecks
{
    internal static void Run(Action<string,bool> check, string project, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name && (m.Parameters.Count == 0 || name != "CheckAmmo"));
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        bool Stores(MethodDefinition m, string field) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == field);
        bool Reads(MethodDefinition m, string field) => m.HasBody && m.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == field);
        bool IsInst(MethodDefinition m, string type) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand is TypeReference t && t.Name == type);

        var utility = nt["EFT.Player/FirearmController/UtilityOperation"];
        check("The native magazine-check UtilityOperation derives from Idling",
            utility.BaseType.FullName == "EFT.Player/FirearmController/Idling");
        var idleOp = nt["EFT.Player/FirearmController/Idling"];
        check("Native Alt+T path starts the CheckMagazine utility operation and animator check",
            Has(idleOp.Methods.Single(m => m.Name == "CheckAmmo"), "RunUtilityOperation") && Has(idleOp.Methods.Single(m => m.Name == "CheckAmmo"), "CheckAmmo"));

        var runtime = pt["DualWieldLab.RuntimePlugin"];
        var hands = M(runtime, "BeforeHandsCommand");
        check("Paired magazine/chamber checks and examine are routed through the left-hand handoff",
            Has(hands, "IsTwoHandedCheck") && Has(hands, "BeginInspection"));
        check("Two-handed native utility operations are detected despite deriving from Idling",
            IsInst(M(runtime, "IsTwoHandedUtility"), "UtilityOperation") && Reads(M(runtime, "IsTwoHandedUtility"), "_utilityOperationType"));
        var start = M(runtime, "StartNativeInspection");
        check("The right pistol uses its own native check methods",
            Has(start, "CheckAmmo") && Has(start, "CheckChamber") && Has(start, "ExamineWeapon"));
        check("Left pistol holsters with its native clip before the check and draws after it",
            Has(M(runtime, "BeginInspection"), "BeginPresentationAction") && Has(M(runtime, "BeginInspectionDraw"), "BeginPresentationAction"));
        var apply = M(runtime, "ApplyInspection");
        check("During the check the left arm eases back to native IK and the left pistol stays holstered",
            Has(apply, "BlendLeftFrom") && Has(apply, "SetPresentationVisible") && Has(apply, "PresentLeftHolster")
            && Has(M(runtime, "PresentLeftHolster"), "BlendDrawFromNative"));
        check("Frame pass routes inspection before the ordinary paired alignment",
            Has(M(runtime, "AfterVisualPass"), "ApplyInspection") && Has(M(runtime, "Update"), "AdvanceInspection"));
        string[] inspection = ["BeginInspection", "StartNativeInspection", "StartNativeInspectionObserved", "BeginInspectionDraw", "EndInspection", "AdvanceInspection", "ApplyInspection"];
        string[] forbidden = ["Unpair", "TryRunNetworkTransaction", "Swap", "ApplyToAddress", "Move", "RemoveItem", "SetItemInHands", "SetParent", "set_parent"];
        check("Inspection keeps the pair and never touches inventory or reparents a magazine",
            inspection.All(n => !Calls(M(runtime, n)).Any(c => forbidden.Contains(c.Name))));
        check("Both triggers, R, Alt+R and the left selector are blocked while a check runs",
            Reads(M(runtime, "get_WeaponActionBusy"), "_inspect") && Has(M(runtime, "get_ReloadBusy"), "get_WeaponActionBusy") && Has(M(runtime, "ChangeOffhandModeOnce"), "get_ReloadBusy"));
        check("Reload entry points refuse a running native utility operation",
            IsInst(M(runtime, "BeginReload"), "UtilityOperation") && IsInst(M(runtime, "BeginOffhandMagazineReload"), "UtilityOperation")
            && IsInst(M(pt["DualWieldLab.InactiveMagazineReload"], "Prepare"), "UtilityOperation"));
        check("Unpair clears any inspection phase", Stores(M(runtime, "Unpair"), "_inspect"));
        check("Reload presentation never reparents the magazine or pistols",
            !All(new[]{pt["DualWieldLab.OffhandNativeAction"], pt["DualWieldLab.OffhandNativeReload"]}).SelectMany(t => t.Methods).SelectMany(Calls).Any(c => c.Name is "SetParent" or "set_parent"));
        check("An interrupted reload presentation stops its handling sound",
            Has(M(runtime, "EndOffhandReloadPresentation"), "StopHandlingSounds") && Has(M(pt["DualWieldLab.OffhandShotContext"], "StopHandlingSounds"), "ReleaseClipsSource"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "assets", "exports", "unity", "dw_offhand_reload.json")));
        var sounds = doc.RootElement.GetProperty("markers").EnumerateObject().Where(p => p.Name.StartsWith("snd:L:"))
            .OrderBy(p => p.Value.GetInt32()).Select(p => p.Value.GetInt32()).ToArray();
        check("Left handling cues never share a frame (one native clip source per pistol)",
            sounds.Zip(sounds.Skip(1)).All(p => p.Second - p.First >= 2));
        var names = doc.RootElement.GetProperty("markers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        check("Hand contact precedes the release and the release precedes extraction",
            names["snd:L:SndHandTouch"] <= names["mag_grab"] && names["snd:L:SndHandTouch"] < names["snd:L:SndMagButton"]
            && names["snd:L:SndMagButton"] < names["snd:L:SndMagOut"]);
    }
}
