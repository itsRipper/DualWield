using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class SuppressorAndGripChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        var runtime = pt["DualWieldLab.RuntimePlugin"];
        var pairing = pt["DualWieldLab.HolsterAutoPairing"];
        var transition = pt["DualWieldLab.DualTransition"];
        var context = pt["DualWieldLab.OffhandShotContext"];
        var rig = pt["DualWieldLab.OffhandVisualRig"];

        // Suppressors: EFT classifies the AI shot noise from HandsController.IsSilenced (the right pistol).
        var onMakingShot = nt["EFT.Player"].Methods.Single(m => m.Name == "OnMakingShot");
        check("Native AI shot noise is classified from the hands controller's IsSilenced, i.e. the right pistol",
            Has(onMakingShot, "get_HandsController") && Has(onMakingShot, "get_IsSilenced") && Has(onMakingShot, "TryPlayShootSound"));
        var initiate = M(context, "InitiateShot");
        check("A left shot reports its own suppressor to the native noise classification and restores the right pistol's",
            Calls(initiate).Count(c => c.Name == "set_IsSilenced") == 2 && Has(initiate, "get_IsSilenced")
            && initiate.Body.ExceptionHandlers.Any(h => h.HandlerType == ExceptionHandlerType.Finally));
        check("The left sound player selects the left pistol's own silenced banks and suppressed pistols pair",
            M(context, "Initialize").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "set_IsSilenced"
                && i.Previous?.Operand is MethodReference g && g.Name == "get_IsSilenced")
            && !M(context, "Initialize").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).Contains("suppressed")));

        // Mixed pistols: the left gun is placed by its own grip marker, measured in rigid weapon frames.
        var measure = M(rig, "MeasureGripCorrection");
        check("Grip correction compares both pistols' native weapon_R_IK_marker in their weapon-node frames",
            Has(measure, "GetTransform") && rig.Methods.Any(m => Has(m, "get_worldToLocalMatrix")));
        check("Every paired placement applies the correction (AlignToPalm)",
            M(rig, "AlignToPalm").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_gripCorrectionRotation"));
        // The right palm and marker are animated outside idle (a draw after the inventory closes,
        // the end of an empty reload): the correction must not depend on when the pair is made.
        var idle = M(rig, "RightHandsIdle");
        check("Grip correction trusts the right marker only at native idle and settles a mid-transition pair once",
            Has(idle, "IsIdling") && Has(idle, "IsInTransition") && Has(M(rig, "AlignToPalm"), "RightHandsIdle")
            && Has(M(rig, "AlignToPalm"), "MeasureRightGrip")
            // Two markers and two weapon nodes; no palm.
            && Calls(M(rig, "MeasureGripCorrection")).Count(c => c.Name == "GetTransform") == 4);

        // Sprint: the left reload's clip and transactions continue; only the view refresh, which
        // re-pairs and is refused in the sprint pose, waits for the sprint to end.
        var magazineReload = pt["DualWieldLab.InactiveMagazineReload"];
        check("The left magazine/chamber transactions do not stop for sprint, and the post-reload refresh waits for it",
            !new[] { magazineReload }.Concat(magazineReload.NestedTypes).SelectMany(t => t.Methods).Any(m => Has(m, "get_IsSprintEnabled"))
            && Has(M(runtime, "ObserveOffhandMagazineReload"), "get_IsSprintEnabled")
            && runtime.Fields.Any(f => f.Name == "_offhandReturnWaitsForSprint"));

        // Automatic pairing: armed by EFT's hands-changed event, only from the two holster slots,
        // and a manual unpair is respected until the hands change again.
        check("Automatic pairing follows the native hands-changed event, only from HOLSTER + HOLSTERLEFT, and respects a manual unpair",
            Has(M(pairing, "WatchHands"), "add_OnHandsControllerChanged") && Has(M(runtime, "OnDestroy"), "Dispose")
            && Has(M(pairing, "Dispose"), "remove_OnHandsControllerChanged")
            && M(pairing, "BothHolstersCompatible").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Holster")
            && Has(M(pairing, "BothHolstersCompatible"), "HolsterPairCompatible") && Has(M(pairing, "HolsterPairCompatible"), "GetRefusal")
            && Has(M(runtime, "TogglePairOnce"), "SuppressUntilHandsChange")
            && M(pairing, "SuppressUntilHandsChange").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_autoPairSuppressed")
            && Has(M(pairing, "TryAutoPair"), "DrawPairReady") && Has(M(pairing, "TryResumeAfterInventory"), "PistolHandsReady"));

        // Coordinated draw: the pair is built during the native SpawnOperation and binds input at idle.
        check("Automatic pairing starts with the native draw and admits input only after the controller is idle and bound",
            Has(M(pairing, "DrawPairReady"), "IsInSpawnOperation") && Has(M(runtime, "FinishDualDraw"), "Create")
            && Has(M(runtime, "FinishDualDraw"), "IsInSpawnOperation") && Has(M(runtime, "Update"), "FinishDualDraw")
            && Has(M(runtime, "get_ReloadBusy"), "get_Active") && Has(M(transition, "get_Active"), "get_DrawPending"));
        // Draw/holster: the left gun mirrors the RENDERED right gun (the weapon node above the right
        // fireport; EFT moves it in its last pass, HandsHierarchy's node lagged ~10 cm). Nothing is
        // created from the live right item (a pooled copy could be handed back to EFT for the next draw).
        check("During the native draw/holster the left gun mirrors the rendered right gun; no pooled copy of the right pistol",
            Has(M(runtime, "AlignModel"), "AlignToMirroredGun") && Has(M(rig, "AlignToMirroredGun"), "MirroredRightGun")
            && M(rig, "MirroredRightGun").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_rightVisibleGun")
            && Has(rig.Methods.Single(m => m.IsConstructor && !m.IsStatic), "VisibleWeaponNode")
            && !runtime.Methods.Any(m => m.Name == "PrimeRightIdleMarker") && !rig.Methods.Any(m => m.Name == "RecordIdleMarker"));
        // No forward snap at the end of a draw: the transition placement carries the ready pose's own
        // offset to the mirrored gun (recorded at paired idle, per pistol pair) and eases out after ready.
        check("Draw/holster placement carries the recorded ready-pose offset and eases into the ready pose after the draw",
            Has(M(rig, "AlignToMirroredGun"), "TryGetValue") && Has(M(rig, "RecordMirrorOffset"), "RightHandsIdle")
            && Has(M(runtime, "AlignModel"), "RecordMirrorOffset") && Has(M(runtime, "AlignModel"), "MirrorPlacementWeight")
            && Has(M(runtime, "FinishDualDraw"), "CompleteDraw")
            && M(transition, "CompleteDraw").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "_handoverStartedAt"));
        // A single transient frame stored as the offset left the gun ~6 cm forward for every later
        // draw and holster: only a sample that holds over consecutive frames is stored, never one
        // from the procedural draw-in, and the handover waits for settled right-hand idle.
        check("The draw/holster offset is stored only from consecutive agreeing idle frames and the handover waits for native idle",
            Has(M(rig, "RecordMirrorOffset"), "Agrees") && Has(M(rig, "RecordMirrorOffset"), "get_frameCount")
            && Has(M(runtime, "AlignModel"), "get_EnterBlend") && Has(M(transition, "MirrorPlacementWeight"), "RightHandsIdle"));
        // Grip after a weapon switch: everything measured from the live right hand (finger grip
        // reference, IK marker) is taken at settled native idle only, never on a draw frame.
        var pose = pt["DualWieldLab.OffhandHandPose"];
        check("The right finger-grip reference and IK marker are measured only at settled native idle (not during the draw)",
            Has(M(pose, "PrimaryIdle"), "IsInSpawnOperation") && Has(M(pose, "PrimaryIdle"), "IsIdling")
            && Has(M(pose, "CaptureNativePose"), "CaptureRightGripReference") && Has(M(pose, "CaptureNativePose"), "PrimaryIdle")
            && Has(M(rig, "RightHandsIdle"), "IsInSpawnOperation"));
        // Holster pairing: a HOLSTERLEFT pistol in hands is a single pistol; with a filled HOLSTER the
        // pair is made by natively reselecting HOLSTER, never by a backpack search.
        check("A left-holster pistol in hands reselects HOLSTER natively for the pair (Alt+V, inventory close/resume)",
            Has(M(pairing, "SelectRightHolsterForPair"), "SetSlotItem") && Has(M(pairing, "SelectRightHolsterForPair"), "HolsterPairCompatible")
            && Has(M(runtime, "TogglePairOnce"), "SelectRightHolsterForPair") && Has(M(pairing, "TryResumeAfterInventory"), "SelectRightHolsterForPair")
            && Has(M(pairing, "CheckHolstersAfterInventory"), "SelectRightHolsterForPair") && Has(M(pairing, "Update"), "CheckHolstersAfterInventory"));
        // Coordinated holster: the native hide keeps the pair; DestroyController releases it.
        check("A native switch away keeps the pair through the hide animation and releases it at DestroyController",
            Has(M(runtime, "BeforeDropController"), "BeginDualHolster") && Has(M(runtime, "BeforeDropController"), "BeforePlayerBoundary")
            && Has(M(runtime, "get_ReloadBusy"), "get_Active") && Has(M(transition, "get_Active"), "get_Holstering")
            && Has(M(runtime, "PairedStopReason"), "get_Holstering"));

        // Right reload: the left pistol is lowered by the authored lowering, gun held at idle in the hand.
        var holster = M(runtime, "PresentLeftHolster");
        check("Right-reload holster/draw lowers the left pistol in the hand with the mirrored authored R.* lowering",
            Has(holster, "HoldIdle") && Has(holster, "AlignToPalm") && Has(holster, "ApplyGripTarget") && !Has(holster, "UseNativeActionGrip")
            && holster.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "R.pos"));
        check("Reload and right-pistol checks share the presentation",
            Has(M(runtime, "ApplyReloadPresentation"), "PresentLeftHolster") && Has(M(runtime, "ApplyInspection"), "PresentLeftHolster"));
    }
}
