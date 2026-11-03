using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class SelectorPoseChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        Quaternion Q(float x,float y,float z)=>Quaternion.CreateFromYawPitchRoll(x,y,z);
        var idle=Q(.2f,-.5f,.8f);var source=Q(-.6f,.3f,.7f);var target=Q(.9f,-.8f,.2f);
        check("Native idle selector reference adds no artificial arm gesture", NativeArmRetargetMath.SameRotation(
            NativeActionPoseMath.LocalDelta(idle,idle,source,target),Quaternion.Identity));
        var random=new Random(1010);bool roundTrip=true,unit=true;
        Quaternion Next()=>Q((float)random.NextDouble()*5,(float)random.NextDouble()*5,(float)random.NextDouble()*5);
        for(int i=0;i<1000;i++)
        {
            var a=Next();var b=Next();var sb=Next();var tb=Next();
            var mapped=NativeActionPoseMath.LocalDelta(a,b,sb,tb);
            var back=NativeActionPoseMath.LocalDelta(Quaternion.Identity,mapped,tb,sb);
            roundTrip &= NativeArmRetargetMath.SameRotation(back,Quaternion.Inverse(a)*b);
            unit &= Math.Abs(mapped.LengthSquared()-1)<1e-5;
        }
        check("Native selector joint deltas round-trip across1000 asymmetric bind orientations",roundTrip);
        check("Native selector transfer preserves unit rotations without scaling bones",unit);
        bool invalid=false;
        try { NativeActionPoseMath.LocalDelta(new Quaternion(float.NaN,0,0,1),idle,source,target); }
        catch(ArgumentException){invalid=true;}
        check("Nonfinite native selector references refuse before a skin write",invalid);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        bool Field(MethodDefinition m,string n)=>m.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name==n);
        var arms=pt["DualWieldLab.NativeArmRetarget"];var pose=pt["DualWieldLab.OffhandHandPose"];
        var capture=M(arms,"CaptureNativePose");var apply=M(arms,"ApplyRightToLeft");
        check("Locomotion preserves the pristine evaluated native left source",Field(apply,"_leftSourceBody")&&Field(apply,"_nativeLocomotionWeight"));
        check("Native left palm trajectory is the locomotion target rather than inverted depth",Field(M(arms,"LivePalmTarget"),"_leftPalmInLook")&&!Calls(capture).Any(c=>c.Name=="Observe"));
        check("Native animator transition progress controls locomotion pose blending",Has(capture,"GetAnimatorTransitionInfo")&&Has(apply,"Slerp"));
        var wrapper=nt["AnimationSystem.UnityAnimatorWrapper"];
        check("Installed Unity wrapper transition interface is an unimplemented trap",M(wrapper,"GetTransitionNormalizedTime").Body.Instructions.Any(i=>i.OpCode==Mono.Cecil.Cil.OpCodes.Throw));
        check("Movement samples the supported Unity transition API through the concrete wrapper",Has(capture,"get_Animator")&&Has(capture,"GetAnimatorTransitionInfo")&&capture.Body.Instructions.Any(i=>i.Operand is TypeReference t&&t.FullName==wrapper.FullName));
        check("Nonfinite pose uses a narrow recoverable exception and invalidates its snapshot",capture.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="NativePoseUnavailableException")&&Has(capture,"set_HasSnapshot")&&Has(capture,"set_SnapshotFrame"));
        check("Native body phase is observed without advancing a plugin locomotion clock",Has(capture,"GetCurrentAnimatorStateInfo")&&!Has(capture,"get_deltaTime")&&!Has(capture,"get_time"));
        var nativePass=M(nt["EFT.Player"],"VisualPass");
        check("Installed VisualPass evaluates left-hand authority and native IK before the source snapshot",Field(nativePass,"LEFT_HAND_WEIGHT")&&Has(nativePass,"IkApply"));
        check("Native selector transfers forearm and wrist deltas as well as finger pose",Has(apply,"LocalDelta")&&Field(apply,"_ownArmDonors"));
        check("Selector donor chains are cached during grip setup",Field(M(arms,"ConfigureFingerGrip"),"_ownArmDonors")&&!Calls(apply).Any(c=>c.Name is "GetComponentsInChildren" or "Find"));
        check("Own selector movement does not get erased by the ordinary mirrored palm target",Field(M(arms,"get_LeftPalmTarget"),"_ownSelector"));
        check("Existing prefab sampler never dispatches authoritative gameplay events",!pose.Methods.SelectMany(Calls).Any(c=>c.Name is "EmitEvents" or "Emit" or "ReloadMag" or "ProcessInventoryOperationsForSingleShot"));
        check("Skin restoration remains finally-protected if the secondary animator disappears",M(pose,"Restore").Body.ExceptionHandlers.Any(h=>h.HandlerType==Mono.Cecil.Cil.ExceptionHandlerType.Finally));
        check("Pairing does not replay the offhand animator to sample a lowering pose",!pt.ContainsKey("DualWieldLab.NativeLoweringPose")&&!pose.Methods.Any(m=>m.Name=="CacheLoweringPose"));
        var reload=M(pt["DualWieldLab.RuntimePlugin"],"ApplyReloadPresentation");
        check("Reload leaves the actual single-pistol arm animation to the native controller",!Has(reload,"ApplyLoweringPose")&&!Has(reload,"ApplyDualHold")&&!Has(reload,"ApplySwap")&&!Has(reload,"AngleAxis"));
    }
}
