using DualWieldLab;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class PoseRecoveryChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        var recovery = new PresentationRecovery();
        check("First unavailable pose reports its invariant immediately", recovery.ObserveUnavailable(10) == PresentationRecoveryDecision.Report);
        check("Repeated native visual callbacks neither count nor log one unavailable frame twice", recovery.ObserveUnavailable(10) == PresentationRecoveryDecision.Defer && recovery.UnavailableFrames == 1);
        check("Next transient unavailable pose defers without logging every frame", recovery.ObserveUnavailable(11) == PresentationRecoveryDecision.Defer);
        recovery.ObserveValid();
        check("A valid evaluated pose clears the recovery budget", recovery.UnavailableFrames == 0);
        check("A later independent failure gets a fresh first diagnostic", recovery.ObserveUnavailable(100) == PresentationRecoveryDecision.Report);
        recovery.ObserveValid();
        for (int i=0; i<59; i++) recovery.ObserveUnavailable(200+i);
        check("Unavailable-pose diagnostics repeat at a bounded frame interval", recovery.ObserveUnavailable(259) == PresentationRecoveryDecision.Report);
        for (int i=60; i<119; i++) recovery.ObserveUnavailable(200+i);
        check("A brief unavailable pose does not destroy an otherwise safe pair", recovery.UnavailableFrames == 119);
        check("Persistent unavailable presentation is rejected after 120 distinct native frames", recovery.ObserveUnavailable(319) == PresentationRecoveryDecision.Reject);
        check("Repeated terminal callbacks remain rejected without consuming extra frames", recovery.ObserveUnavailable(319) == PresentationRecoveryDecision.Reject && recovery.UnavailableFrames == 120);
        recovery.ObserveValid();
        recovery.ObserveUnavailable(2000);
        check("Skipped frames do not consume a catch-up recovery budget", recovery.UnavailableFrames == 1);
        // A policy-level sequence, not an executed EFT movement simulation.
        bool transitionsRecover = true;
        foreach (string state in new[]{"Walk", "Sprint", "Crouch", "ProneMove", "Turn", "Idle"})
        {
            recovery.ObserveValid();
            transitionsRecover &= recovery.ObserveUnavailable(3000) == PresentationRecoveryDecision.Report;
            recovery.ObserveValid();
            transitionsRecover &= recovery.UnavailableFrames == 0;
        }
        check("Valid frames reset transient recovery across walk/sprint/crouch/prone/turn/idle policy sequences", transitionsRecover);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var wrapper=nt["AnimationSystem.UnityAnimatorWrapper"];
        var trap=M(wrapper,"GetTransitionNormalizedTime");
        check("Installed EFT Unity transition-interface implementation really throws NotImplementedException", trap.Body.Instructions.Any(i=>i.OpCode==OpCodes.Throw)
            && Calls(trap).Any(c=>c.DeclaringType.FullName=="System.NotImplementedException"));
        var capture=M(pt["DualWieldLab.NativeArmRetarget"],"CaptureNativePose");
        check("Movement transition capture obtains the real Unity Animator before sampling supported transition info", Has(capture,"get_Animator") && Has(capture,"GetAnimatorTransitionInfo")
            && capture.Body.Instructions.Any(i=>i.OpCode==OpCodes.Isinst && i.Operand is TypeReference t&&t.FullName==wrapper.FullName));
        var runtime=pt["DualWieldLab.RuntimePlugin"];
        var visual=M(runtime,"AfterVisualPass");
        check("Transient pose recovery is restricted to its explicit exception type", visual.Body.ExceptionHandlers.Any(h=>h.CatchType?.FullName=="DualWieldLab.NativePoseUnavailableException")
            && visual.Body.ExceptionHandlers.Any(h=>h.CatchType?.FullName=="System.Exception"));
        check("Visual safety still validates native paired state before capturing any pose", Has(visual,"PairedStopReason") && Has(visual,"Unpair"));
        var stop=M(runtime,"PairedStopReason");
        check("Unsafe native controller changes and unavailable offhand still reject pairing", Has(stop,"get_HandsController") && stop.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_primary")
            && stop.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_secondary"));
        check("Death, holstering and inventory opening remain native pair rejection conditions", Has(stop,"get_IsAlive") && Has(stop,"IsInRemoveOperation") && Has(stop,"get_IsInventoryOpened"));
        check("Offhand firing cannot use an unavailable native pose snapshot", Has(M(runtime,"TryFire"),"get_HasSnapshot") && Has(M(runtime,"TryFire"),"StopOffhandTrigger"));
        check("A normal captured pose resets only the recoverable presentation budget", Has(visual,"ObserveValid") && Has(visual,"ObserveUnavailable"));
    }
}
