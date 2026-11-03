using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class LookSpaceChecks
{
    internal static void Run(Action<string,bool> check,AssemblyDefinition native,AssemblyDefinition plugin)
    {
        var sourceBind=Quaternion.CreateFromYawPitchRoll(.3f,-.7f,.4f);
        var targetBind=Quaternion.CreateFromYawPitchRoll(-.5f,.2f,-.9f);
        var residual=Quaternion.CreateFromYawPitchRoll(.04f,-.02f,.01f);
        bool follows=true,balanced=true,freezeFollows=true;
        var oldLook=Quaternion.CreateFromYawPitchRoll(.4f,-.2f,.1f);
        for(int yaw=-6;yaw<=6;yaw++)for(int pitch=-4;pitch<=4;pitch++)
        {
            var look=Quaternion.CreateFromYawPitchRoll(yaw*.2f,pitch*.2f,0);
            var actual=NativeArmRetargetMath.TransferLook(look*residual*sourceBind,sourceBind,targetBind,look,look);
            var expected=look*NativeArmRetargetMath.ReflectBodyDelta(residual)*targetBind;
            follows&=NativeArmRetargetMath.SameRotation(actual,expected);
            var held=NativeArmRetargetMath.TransferLook(oldLook*residual*sourceBind,sourceBind,targetBind,oldLook,look);
            freezeFollows&=NativeArmRetargetMath.SameRotation(held,expected);
            var right=new Vector3(.22f,.08f,.43f);
            var left=NativeArmRetargetMath.ReflectPalm(right);
            var recovered=Vector3.Transform(Vector3.Transform(left,look),Quaternion.Inverse(look));
            balanced&=Math.Abs(recovered.X+right.X)<1e-5f&&Math.Abs(recovered.Y-right.Y)<1e-5f&&Math.Abs(recovered.Z-right.Z)<1e-5f;
        }
        check("Shared native hands yaw/pitch remains unmirrored across 117 look poses",follows);
        check("Selector source isolation retains current mouse-look without replaying selector motion",freezeFollows);
        check("Native mirrored grip target preserves palm depth/height in all 117 look poses",balanced);
        check("Palm reflection round trip preserves measured geometry",Vector3.Distance(NativeArmRetargetMath.ReflectPalm(NativeArmRetargetMath.ReflectPalm(new Vector3(.3f,.1f,.5f))),new Vector3(.3f,.1f,.5f))<1e-6f);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var getter=M(nt["EFT.Player"],"get_TargetHandsRotation");
        check("Actual native hands look is provided by native movement pitch and yaw",Has(getter,"get_Pitch")&&Has(getter,"get_Yaw")&&Has(getter,"Euler"));
        var arm=pt["DualWieldLab.NativeArmRetarget"];var hand=pt["DualWieldLab.OffhandHandPose"];
        check("Retarget uses native hands look rather than head/free-look camera rotation",Has(M(arm,"CaptureNativePose"),"get_TargetHandsRotation")&&Has(M(arm,"ApplySide"),"TransferLook"));
        check("Actual grip target uses measured native palm depth and reflected look-frame lateral coordinate",Has(M(arm,"LivePalmTarget"),"ReflectPalm")&&Has(M(arm,"get_LeftPalmTarget"),"LivePalmTarget")&&Has(M(hand,"ApplyRetarget"),"get_LeftPalmTarget")&&Has(M(hand,"ApplyRetarget"),"SolveArm"));
        check("Mode isolation inspects native current/next animator states rather than a timer",Has(M(hand,"PrimarySelectorPlaying"),"SelectorPlaying")&&Has(M(hand,"SelectorPlaying"),"GetCurrentAnimatorStateInfo")&&Has(M(hand,"SelectorPlaying"),"GetNextAnimatorStateInfo")&&!Has(M(hand,"PrimarySelectorPlaying"),"get_time"));
        check("Selector detection gates source capture, not native selector or trigger state",Has(M(hand,"CaptureNativePose"),"IsolatePrimaryAction")&&!Calls(M(hand,"PrimarySelectorPlaying")).Any(c=>c.Name is "SetFireMode" or "SetTriggerPressed" or "Reset"));
        check("Look alignment retains native shoulder/bone translation ownership",!arm.Methods.SelectMany(Calls).Any(c=>c.Name is "set_position" or "set_localPosition" or "set_localScale"));
        check("Corrected arm target is committed for native-boundary restoration",Has(M(hand,"ApplyRetarget"),"CommitOverrides"));
        check("Relative root depth/reach diagnostics are explicit snapshots",M(hand,"CaptureDiagnostic").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("root_delta_camera="))&&M(hand,"CaptureDiagnostic").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("target_reach_error_m=")));
    }
}
