using static CheckHelpers;
using DualWieldLab;
using Mono.Cecil;

internal static class VisualInputChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        // Executed policy scenarios; no Unity methods run in this process.
        var gate = new TriggerEdgeGate();
        check("Pairing while trigger held requires release", !gate.ClaimNative(1,true));
        gate.Observe(true,false);
        check("First admitted native press fires",gate.ClaimNative(2,true));
        check("Repeated command in same frame cannot double fire",!gate.ClaimNative(2,true));
        gate.Observe(true,true);
        check("Held semi-auto trigger cannot fire next frame",!gate.ClaimNative(3,true));
        gate.Observe(true,false);
        check("Press-mode Aim needs no EndAlternative command to rearm",gate.ClaimNative(4,true));
        gate.Observe(false,true);
        check("Menu command cannot request a shot",!gate.ClaimNative(5,false));
        gate.Observe(true,true);
        check("Closing menu while held cannot request a shot",!gate.ClaimNative(6,true));
        gate.Observe(true,false);
        check("Fresh press after menu release works",gate.ClaimNative(7,true));
        gate.Observe(true,false);
        check("Native and diagnostic paths share frame deduplication",!gate.Claim(7,true,true));
        check("Diagnostic holds are not fresh presses",!gate.Claim(8,true,false));
        gate.Reset();
        check("Unpair/session reset discards trigger state",!gate.ClaimNative(9,true));
        var second = new TriggerEdgeGate(); second.Observe(true,false);
        check("Separate trigger gates do not share state",second.ClaimNative(9,true));
        var barrier = new PairInputBarrier();
        check("Pair shortcut starts a delivery barrier",barrier.Begin(10)&&barrier.Blocks(10));
        check("Buffered knife command is blocked on a later frame",barrier.Blocks(11));
        check("Pending delivery cannot pair a second time",!barrier.Begin(11));
        barrier.Delivered();
        check("Later normal native commands pass after delivery",!barrier.Blocks(11));
        check("Update fallback cannot toggle twice on original frame",!barrier.Begin(10));
        check("A new shortcut works after completed delivery",barrier.Begin(12));
        barrier.Delivered();
        check("Same pairing frame still consumes conflicting commands",barrier.Blocks(12));
        barrier.Reset();
        check("Session cleanup discards input delivery barrier",!barrier.Pending&&!barrier.Blocks(12));

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string name)=>Calls(m).Any(c=>c.Name==name);
        var rt=pt["DualWieldLab.RuntimePlugin"];
        var vis=pt["DualWieldLab.OffhandVisualRig"];
        var input=pt["DualWieldLab.PairedInput"];
        var nativeHands=nt["EFT.FirearmHandsInputTranslator"];
        check("Inspected native Aim commands call native ToggleAim/SetAim",Has(M(nativeHands,"TranslateCommand"),"ToggleAim")&&Has(M(nativeHands,"TranslateCommand"),"SetAim"));
        check("Native translator exposes exact player/controller identity",nativeHands.Fields.Single(f=>f.Name=="_player").IsPublic && nativeHands.Fields.Single(f=>f.Name=="_controller").IsPublic);
        check("Native owner dispatches hands commands",Has(M(nt["EFT.PlayerOwner"],"TranslateCommand"),"get_HandsInputTranslator"));
        check("Native game owner filters screen and ignored input before dispatch",Has(M(nt["EFT.GamePlayerOwner"],"TranslateCommand"),"CanTranslateCommand")&&Has(M(nt["EFT.GamePlayerOwner"],"TranslateCommand"),"TranslateInventoryScreenInput"));
        check("Paired mouse entry uses gate and common shot path",Has(M(rt,"BeforeHandsCommand"),"ClaimNative")&&Has(M(rt,"BeforeHandsCommand"),"TryFire"));
        check("Paired input never calls or duplicates native primary firing",!Calls(M(rt,"BeforeHandsCommand")).Any(m=>m.Name is "SetTriggerPressed" or "ToggleAim" or "SetAim" or "Shot"));
        check("No raw mouse polling creates shots in Update",!Calls(M(rt,"Update")).Any(m=>m.Name is "GetMouseButton" or "GetMouseButtonDown" or "GetKeyDown"));
        check("Actual EFT bindings are queried once at pairing",Has(M(input,"Create"),"get_UserKeyBindings")||M(input,"Create").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="UserKeyBindings"));
        check("No binding or hierarchy lookup while observing trigger release",!Calls(M(input,"IsAimHeld")).Any(m=>m.Name is "GetKeyGroup" or "GetComponent" or "GetComponentsInChildren"));
        check("Gameplay gate checks owner membership and native ignore state",Has(M(input,"AllowsGameplay"),"Contains")&&Has(M(input,"AllowsGameplay"),"CanTranslateCommand"));
        check("Gameplay gate checks focus/cursor/screen/inventory/pause",new[]{"get_isFocused","get_lockState","CheckCurrentScreen","get_IsInventoryOpened","get_timeScale"}.All(n=>Has(M(input,"AllowsGameplay"),n)));
        check("No developer test keys or fixed-value settings remain",!SettingsCtor(plugin).Body.Instructions.Any(i=>i.Operand is string s&&s is "EnableDiagnosticKeys" or "TogglePair" or "FireOffhand" or "PrepareMainHandService" or "ExperimentalLeftHandIK" or "UseNativeAimBindingForOffhand" or "OffhandOffset" or "AlignMixedPistolGrips"));
        check("Pair chord is intercepted before native knife/player actions",Has(M(rt,"BeforeOwnerCommand"),"HandleShortcut")&&Has(M(rt,"HandleShortcut"),"TogglePairOnce")&&Has(M(rt,"BeforeOwnerCommand"),"AllowsGameplay"));
        check("Native delivery completion releases the pairing barrier",Has(M(rt,"AfterInputDelivery"),"Delivered")&&Has(M(rt,"BeforeOwnerCommand"),"Blocks"));
        check("RMB ADS guards do not change the paired stance",!new[]{"BeforeToggleAim","BeforeSetAim"}.SelectMany(n=>M(rt,n).Body.Instructions).Any(i=>i.Operand is FieldReference f&&f.Name=="_dualAiming"));
        check("Offhand visibility helper has one creation site",pt.Values.SelectMany(t=>t.Methods).SelectMany(Calls).Count(m=>m.DeclaringType.FullName==vis.FullName&&m.Name=="Create")==1);
        var ctor=M(vis,".ctor");
        var calls=Calls(ctor).Select(c=>c.Name).ToArray();
        check("Native idle explicitly sampled after default spawn reset",Array.IndexOf(calls,"ResetStatesToDefault")<Array.IndexOf(calls,"get_FullIdleStateName")&&Has(ctor,"Play")&&Has(ctor,"Update")&&Has(ctor,"IsIdling"));
        check("Native idle presentation does not run chamber SpawnOperation",!Calls(ctor).Any(c=>c.Name is "Spawn" or "Start" or "CreateController" or "PopTo" or "RemoveItem"));
        check("Idle sampling lives only in visual creation",!Has(M(rt,"TryPair"),"ResetStatesToDefault")&&!Has(M(vis,"Align"),"Play")&&!Has(M(vis,"Align"),"Update"));
        check("Visual alignment repairs parenting and presentation flags",Has(M(vis,"AlignToPalm"),"EnsurePresentation")&&Has(M(vis,"EnsurePresentation"),"SetParent")&&Has(M(vis,"EnsurePresentation"),"ApplyPresentationFlags"));
        check("Presentation uses actual primary layer without changing camera mask",Has(ctor,"get_layer")&&!vis.Methods.SelectMany(Calls).Any(m=>m.Name=="set_cullingMask"));
        check("Native LOD child active/enabled flags are preserved",!Calls(M(vis,"ApplyPresentationFlags")).Any(m=>m.Name=="set_enabled"));
        check("Pooling restores offhand layers/shadows/offscreen flags",new[]{"set_layer","set_forceRenderingOff","set_shadowCastingMode","set_updateWhenOffscreen"}.All(n=>Has(M(vis,"Restore"),n)));
        check("Visual hot paths perform no hierarchy or renderer discovery",!new[]{"Align","ApplyPresentationFlags"}.SelectMany(n=>Calls(M(vis,n))).Any(m=>m.Name is "GetComponentsInChildren" or "GetComponent" or "Find"));
        check("Unpair clears input and restores pooled presentation",Has(M(rt,"Unpair"),"Reset")&&Calls(M(rt,"Unpair")).Any(m=>m.DeclaringType.FullName==vis.FullName&&m.Name=="Restore"));
        check("Visual diagnostics include camera-space and visibility evidence",M(vis,"CaptureDiagnostic").Body.Instructions.Any(i=>i.Operand is string s&&s==" muzzle_viewport="));
    }
}
