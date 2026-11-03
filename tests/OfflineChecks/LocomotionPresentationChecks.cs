using System.Numerics;
using DualWieldLab;
using Mono.Cecil;

internal static class LocomotionPresentationChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol,60,100,.2f,out var profile);
        var left=new PistolAnimationState(profile); var right=new PistolAnimationState(profile);
        left.Reset(false);right.Reset(false);
        bool noSynth=true;
        for(int i=0;i<1200;i++) {
            left.Step(1f/120,true,i>600,(i%120)/120f);
            var sample=left.Sample();
            noSynth&=sample.SwaySideMetres==0&&sample.SwayUpMetres==0&&sample.YawDegrees==0&&sample.RollDegrees==0;
        }
        check("Live native locomotion is not replaced by a synthetic sine clock",noSynth);
        check("Sprint metadata adds no authored gun drop to native arm motion",left.Sample().VerticalMetres==0&&left.Sample().PitchUpDegrees==0&&left.LowerBlend==1);
        left.Fire();
        check("Offhand additive recoil remains independent of primary state",left.Recoil==1&&right.Recoil==0&&left.Sample().PitchUpDegrees>0&&left.Sample().ForwardMetres<0);
        left.Step(.5f,false,false,0);
        check("Independent recoil still recovers without a locomotion clock",left.Recoil==0&&left.Sample().ForwardMetres==0);
        left.Reset();
        check("Pair draw remains a bounded additive transition",left.Sample().VerticalMetres<0&&left.EnterBlend==0);
        left.Reset(false);
        check("Pose reset removes all borrowed additive state",left.Sample().VerticalMetres==0&&left.Recoil==0&&left.LowerBlend==0);

        // Quaternion/rigid-transform fixtures test the grip inversion, not a
        // Unity engine scene or proof of real avatar skinning/visibility.
        var palms=new[]{new Vector3(-.2f,1.4f,.4f),new Vector3(-.2f,.8f,.4f),new Vector3(-.2f,.22f,.4f),new Vector3(-.2f,1.1f,.2f),new Vector3(-.23f,1.45f,.38f)};
        var grips=new[]{new Vector3(-.03f,.02f,-.06f),new Vector3(-.04f,.018f,-.08f)};
        bool contact=true, orientation=true;int cases=0;
        foreach(var palm in palms)foreach(var grip in grips)foreach(float scale in new[]{1f,1.1f}) {
            var targetRotation=Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(palm.Z,palm.Y*.3f,palm.X));
            var gripRotation=Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(grip.X,grip.Y,grip.Z));
            var rootRotation=targetRotation*Quaternion.Inverse(gripRotation);
            var rootPosition=palm-Vector3.Transform(grip*scale,rootRotation);
            var recovered=rootPosition+Vector3.Transform(grip*scale,rootRotation);
            contact&=Vector3.Distance(recovered,palm)<1e-5f;
            orientation&=Math.Abs(Quaternion.Dot(Quaternion.Normalize(rootRotation*gripRotation),targetRotation))>1-.00001;
            cases++;
        }
        check("Different grip/stance/scale fixtures retain exact palm contact",contact&&cases==20);
        check("Different grip orientations do not rotate the wrist twice",orientation);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)=>types.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);
        var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition type,string name)=>type.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        var nativePass=Calls(M(nt["EFT.Player"],"VisualPass")).Select(c=>c.Name).ToArray();
        check("Installed visual pass processes effectors before final weapon transformations",Array.IndexOf(nativePass,"ProcessEffectors")>=0&&Array.IndexOf(nativePass,"ProcessEffectors")<Array.IndexOf(nativePass,"LateTransformations"));
        check("Installed visual pass finalizes hands after procedural transformations",Array.IndexOf(nativePass,"LateTransformations")<Array.IndexOf(nativePass,"ManualLateUpdate"));
        var rig=pt["DualWieldLab.OffhandVisualRig"];
        check("Live weapon presentation inverts its native per-weapon grip",Calls(M(rig,"AlignToPalm")).Any(c=>c.Name=="Inverse")&&Calls(M(rig,"AlignToPalm")).Any(c=>c.Name=="get_LeftGripPosition"));
        check("Live weapon grip uses actual root transform and native scale",Calls(M(rig,"get_LeftGripPosition")).Any(c=>c.Name=="TransformPoint"));
        check("Weapon presentation does not retain a camera-fixed sprint anchor",!rig.Fields.Any(f=>f.Name.Contains("readyRootCamera")));
        check("Live weapon alignment does not clamp posture to viewport coordinates",!Calls(M(rig,"AlignToPalm")).Any(c=>c.Name is "ConstrainLoweredView" or "WorldToViewportPoint" or "InverseTransformPoint"));
        check("Live weapon alignment uses cached references without rig discovery",!Calls(M(rig,"AlignToPalm")).Any(c=>c.Name is "GetComponentsInChildren" or "GetComponent" or "Find"));
        check("Additive pose sample contains no sine/cosine locomotion",!Calls(M(pt["DualWieldLab.PistolAnimationState"],"Sample")).Any(c=>c.Name is "Sin" or "Cos"));
        check("New presentation keeps the original native idle state selection",Calls(M(rig,".ctor")).Any(c=>c.Name=="get_FullIdleStateName")&&Calls(M(rig,".ctor")).Any(c=>c.Name=="IsIdling"));
        check("Presentation still never modifies camera culling masks",!rig.Methods.SelectMany(Calls).Any(c=>c.Name=="set_cullingMask"));
    }
}
