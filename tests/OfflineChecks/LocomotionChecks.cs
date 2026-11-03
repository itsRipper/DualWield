using DualWieldLab;
using Mono.Cecil;

internal static class LocomotionChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        float Curve(float p) => p < .25f ? p * 4f : p < .75f ? 2f - p * 4f : p * 4f - 4f;
        double Error(int kind, int fps)
        {
            var cycle = new NativeLocomotionCycle(); double error = 0;
            for (int i = 0; i <= fps * 8; i++)
            {
                float phase = i / (float)fps;
                float input = .4f + .025f * Curve(phase % 1f);
                float output = input + cycle.Observe(kind, phase, input, 1f / fps, false);
                if (phase >= 4f) error = Math.Max(error, Math.Abs(output - (.8f - input)));
            }
            return error;
        }
        check("Sprint uses current native displacement in opposite fore-aft phase", Error(1, 60) < .0001d);
        check("Prone movement uses the same opposite native-cycle equation", Error(2, 60) < .0001d);
        check("Opposition remains stable across frame rates without a delayed pose buffer", Error(1, 30) < .0001d && Error(2, 120) < .0001d);
        var sample = new NativeLocomotionCycle();
        check("First native locomotion sample does not inject a forward offset", sample.Observe(1, 5.75, .43f, .016f, false) == 0f);
        bool still = true; for (int i = 0; i < 300; i++) still &= sample.Observe(1, 5.75 + i / 60d, .43f, 1f/60f, false) == 0f;
        check("A native cycle with no measured displacement creates no artificial swing", still);
        sample.Observe(1, 11, .43f, .016f, false); sample.Observe(1, 11.5, .45f, .016f, false);
        float previous = Math.Abs(sample.Offset);
        bool settles = true; for (int i = 0; i < 120; i++) { float next = Math.Abs(sample.Observe(0, 0, .43f, 1f/60f, false)); settles &= next <= previous; previous = next; }
        check("Stopping locomotion releases correction monotonically without changing idle sway", settles && previous < .00001f);
        sample.Observe(1, 0, .4f, .016f, false);sample.Observe(1,.5,.45f,.016f,false);
        float transitioning = Math.Abs(sample.Offset);
        sample.Observe(2, 0, .6f, .016f, true);
        check("Native state transition does not treat stance lowering as cycle travel", Math.Abs(sample.Offset) < transitioning);
        sample.Reset();check("Unpair cleanup clears previous cycle calibration",sample.Offset == 0 && sample.Observe(2,.8,.22f,.016f,false)==0);
        check("Nonfinite phase clears rather than propagates a native pose error",sample.Observe(1,double.NaN,.4f,.016f,false)==0);
        sample.Observe(1,.2,.4f,.1f,false);sample.Observe(1,.8,.45f,.1f,false);
        float beforeSwitch=sample.Offset;
        float afterSwitch=sample.Observe(2,.9,.2f,0,false);
        check("Direct sprint-to-crawl admission retains the current correction before damping",Math.Abs(beforeSwitch-afterSwitch)<1e-7f);
        float beforeRewind=sample.Observe(2,.95,.23f,.016f,false);
        float afterRewind=sample.Observe(2,.1,.2f,0,false);
        check("Native phase rewind within one integer cycle resets calibration without snapping",Math.Abs(beforeRewind-afterRewind)<1e-7f);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var arm=pt["DualWieldLab.NativeArmRetarget"];var hand=pt["DualWieldLab.OffhandHandPose"];
        check("Native locomotion reads actual body phase and transition state",Has(M(arm,"CaptureNativePose"),"get_BodyAnimatorCommon")&&Has(M(arm,"CaptureNativePose"),"GetCurrentAnimatorStateInfo")&&Has(M(arm,"CaptureNativePose"),"IsInTransition"));
        check("Cycle correction is enabled only for the ordinary paired hand wrapper",Has(M(hand,".ctor"),"EnableAlternatingLocomotion")&&!pt.ContainsKey("DualWieldLab.ReloadPresentation"));
        check("Retired scalar cycle helper is absent from the production DLL",!pt.ContainsKey("DualWieldLab.NativeLocomotionCycle")&&!Calls(M(arm,"CaptureNativePose")).Any(c=>c.Name is "Sin" or "Cos" or "get_time" or "get_timeAsDouble"));
        check("Selector state admission uses cached native layer-qualified hashes",Has(M(hand,"SelectorStates"),"StringToHash")&&Has(M(hand,"SelectorStates"),"GetLayerName")&&!Has(M(hand,"SelectorPlaying"),"GetStateName"));
        check("Selector hash comparison checks both native current and next state",Has(M(hand,"SelectorPlaying"),"GetCurrentAnimatorStateInfo")&&Has(M(hand,"SelectorPlaying"),"GetNextAnimatorStateInfo"));
        var helper=nt["EFT.Tools.AnimationDebugging.EditorAnimatorHelperUtility"];
        check("Installed shipping animator debug helper returns numeric hashes, not names",helper.Methods.Where(m=>m.Name=="GetStateName").All(m=>Calls(m).Any(c=>c.Name=="ToString")));
        check("Left selector advances only the existing secondary visual animator",!Has(M(hand,"BeginOffhandSelector"),"SetFireMode")&&Has(M(hand,"AdvanceOffhandAnimation"),"Update")&&!Calls(M(hand,"BeginOffhandSelector")).Any(c=>c.Name is "CreateController" or "SetTriggerPressed" or "ReloadMag"));
        check("Left visual gesture updates actual cached native finger grip donors",M(arm,"ApplyRightToLeft").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_ownGripDonors"));
        check("Cleanup ends visual selector sampling and restores native idle reference",Has(M(hand,"Restore"),"Play")&&Has(M(hand,"Restore"),"OwnSelector")&&Has(M(hand,"Restore"),"Restore"));
        check("A lost secondary animator cannot skip borrowed skin restoration",M(hand,"Restore").Body.ExceptionHandlers.Any(h=>h.HandlerType==Mono.Cecil.Cil.ExceptionHandlerType.Finally));
        check("Secondary selector sampling dispatches no gameplay animation events",!hand.Methods.SelectMany(Calls).Any(c=>c.Name is "EmitEvents" or "Emit" or "RegisterAnimationEventConsumer" or "ProcessInventoryOperationsForSingleShot" or "ReloadMag"));
        check("Native left selector gesture is admitted by the actual component change handler",Has(M(pt["DualWieldLab.RuntimePlugin"],"ChangeOffhandModeOnce"),"BeginOffhandSelector"));
    }
}
