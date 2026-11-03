using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class ArmRetargetChecks
{
    internal static void Run(Action<string,bool> check,AssemblyDefinition native,AssemblyDefinition plugin)
    {
        Quaternion Rot(float x,float y,float z)=>Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(y,x,z));
        var rightBind=Rot(.2f,-.6f,.8f);var leftBind=Rot(-.3f,.9f,-.7f);
        check("Live retarget maps source rest exactly to destination rest",NativeArmRetargetMath.SameRotation(NativeArmRetargetMath.TransferBody(rightBind,rightBind,leftBind),leftBind));
        var live=Rot(.7f,.2f,-.9f);
        var mapped=NativeArmRetargetMath.TransferBody(live,rightBind,leftBind);
        var recovered=NativeArmRetargetMath.TransferBody(mapped,leftBind,rightBind);
        check("Bind-calibrated handedness transfer round trips",NativeArmRetargetMath.SameRotation(live,recovered));
        var reflection=Matrix4x4.Identity;reflection.M11=-1;
        var delta=Quaternion.Normalize(live*Quaternion.Inverse(rightBind));
        var expected=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(reflection*Matrix4x4.CreateFromQuaternion(delta)*reflection)*leftBind);
        check("Quaternion delta reflection matches independent matrix reflection",NativeArmRetargetMath.SameRotation(mapped,expected));
        bool roundTrips=true,units=true;var random=new Random(71);
        for(int i=0;i<1000;i++){
            Quaternion Sample()=>Rot((float)random.NextDouble()*6-3,(float)random.NextDouble()*6-3,(float)random.NextDouble()*6-3);
            var s=Sample();var sb=Sample();var tb=Sample();var t=NativeArmRetargetMath.TransferBody(s,sb,tb);
            roundTrips&=NativeArmRetargetMath.SameRotation(s,NativeArmRetargetMath.TransferBody(t,tb,sb));
            units&=Math.Abs(t.LengthSquared()-1)<.00001;
        }
        check("1000 asymmetric bind and pose cases retain reversible handedness",roundTrips);
        check("1000 live transfer results stay normalized",units);
        var ownGrip=Rot(-.4f,.2f,.1f);
        check("Mixed pistol finger baseline preserves offhand native grip",NativeArmRetargetMath.SameRotation(NativeArmRetargetMath.ApplyGripMotion(rightBind,ownGrip,rightBind),ownGrip));
        check("Identical finger grip profiles preserve current source motion",NativeArmRetargetMath.SameRotation(NativeArmRetargetMath.ApplyGripMotion(rightBind,rightBind,live),live));
        bool stances=true;
        foreach(var torso in new[]{Rot(0,0,0),Rot(.3f,0,0),Rot(1.3f,0,0),Rot(.1f,.4f,-.2f)}){
            var bodyPose=torso*live;
            var transferred=NativeArmRetargetMath.TransferBody(bodyPose,rightBind,leftBind);
            stances&=!NativeArmRetargetMath.SameRotation(transferred,NativeArmRetargetMath.TransferBody(live,rightBind,leftBind))||NativeArmRetargetMath.SameRotation(torso,Quaternion.Identity);
        }
        check("Crouch prone and turning pose deltas are not frozen at idle",stances);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        var arms=pt["DualWieldLab.NativeArmRetarget"];
        var wrapper=pt["DualWieldLab.OffhandHandPose"];
        var visualCalls=Calls(M(nt["EFT.Player"],"VisualPass")).Select(c=>c.Name).ToArray();
        check("Native pose order ends after IK procedural transforms and hands late update",Array.IndexOf(visualCalls,"IkApply")<Array.IndexOf(visualCalls,"LateTransformations")&&Array.IndexOf(visualCalls,"LateTransformations")<Array.IndexOf(visualCalls,"ManualLateUpdate"));
        check("Live retarget resolves actual skin and bindposes once",Calls(M(arms,"Create")).Any(c=>c.Name=="GetRenderers")&&Calls(M(arms,"Create")).Any(c=>c.Name=="get_bindposes"));
        check("Live source capture reads no frozen prefab donor",!Calls(M(arms,"CaptureNativePose")).Any(c=>c.Name is "GetTransform" or "GetComponent" or "GetComponentsInChildren"));
        check("Source snapshot never writes bone rotations",!Calls(M(arms,"CaptureNativePose")).Any(c=>c.Name is "set_rotation" or "set_localRotation"));
        check("Capture rejects snapshots from an applied overlay",M(arms,"CaptureNativePose").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("Restore borrowed")));
        check("Live normal retarget invokes one target side and preserves right arm",Calls(M(arms,"ApplyRightToLeft")).Count(c=>c.Name=="ApplySide")==1);
        check("Only the left arm is ever retargeted",!arms.Methods.Any(m=>m.Name is "ApplySwap" or "ApplyDualHold" or "ApplyLoweringPose"));
        check("Retarget never changes native bone positions or scales",!arms.Methods.SelectMany(Calls).Any(c=>c.Name is "set_position" or "set_localPosition" or "set_localScale"));
        check("Native twist bones are included in the cached arm mapping",arms.Methods.SelectMany(m=>m.HasBody?m.Body.Instructions:[]).Any(i=>i.Operand is string s&&s=="Forearm2")&&arms.Methods.SelectMany(m=>m.HasBody?m.Body.Instructions:[]).Any(i=>i.Operand is string s&&s=="Forearm3"));
        check("Arm hot paths perform no hierarchy or bindpose discovery",!arms.Methods.Where(m=>new[]{"CaptureNativePose","ApplyRightToLeft","ApplySwap","ApplySide","RestoreBeforeNativePose","CommitOverrides"}.Contains(m.Name)).SelectMany(Calls).Any(c=>c.Name is "GetComponentsInChildren" or "get_bindposes" or "get_bones" or "Find"));
        check("Grip correction records final overrides for restoration",Calls(M(wrapper,"ApplyGripTarget")).Any(c=>c.Name=="CommitOverrides"));
        check("Temporary pose cleanup restores borrowed renderer settings",Calls(M(arms,"Restore")).Any(c=>c.Name=="set_updateWhenOffscreen")&&Calls(M(arms,"Restore")).Any(c=>c.Name=="RestoreBeforeNativePose"));
        check("Restoring borrowed pose preserves last native snapshot until final cleanup",!Calls(M(arms,"RestoreBeforeNativePose")).Any(c=>c.Name=="set_HasSnapshot")&&Calls(M(arms,"Restore")).Any(c=>c.Name=="set_HasSnapshot"));
    }
}
