using DualWieldLab;

internal static class RecoilAnimationChecks
{
    internal static void Run(Action<string,bool> check)
    {
        PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol,60,100,.2f,out var profile);
        var left=new PistolAnimationState(profile);var right=new PistolAnimationState(profile);
        left.Step(.5f,true,false,0);right.Step(.5f,true,false,0);
        var idle=left.Sample();left.Step(.08f,true,false,0);
        check("Native ready pose is not replaced by authored breathing",left.Sample().SwayUpMetres==idle.SwayUpMetres&&left.Sample().SwayUpMetres==0);
        bool bounded=true;float maxWalk=0;
        for(int i=0;i<600;i++){
            left.Step(1f/120,true,false,1);var p=left.Sample();maxWalk=Math.Max(maxWalk,Math.Abs(p.SwaySideMetres));
            bounded&=Math.Abs(p.SwaySideMetres)<.005f&&Math.Abs(p.SwayUpMetres)<.004f&&Math.Abs(p.YawDegrees)<.5f&&Math.Abs(p.RollDegrees)<.7f;
        }
        check("The movement-speed parameter creates no synthetic sway",bounded&&maxWalk==0);
        left.Fire();check("Offhand recoil does not change primary animation state",left.Recoil>0&&right.Recoil==0);
        right.Fire();check("Simultaneous recoil uses separate bounded states",right.Recoil>0&&left.Recoil<=1&&right.Recoil<=1);
        left.Step(.5f,true,true,1);var lower=left.Sample();
        check("Sprint metadata retains the native arm position",lower.LowerBlend==1&&lower.VerticalMetres==0);
        check("Sprint metadata retains the native arm pitch",lower.PitchUpDegrees==0);
        bool smooth=true;float last=lower.VerticalMetres;
        for(int i=0;i<30;i++){left.Step(1f/120,true,false,0);var p=left.Sample();smooth&=Math.Abs(p.VerticalMetres-last)<.012f;last=p.VerticalMetres;}
        check("Sprint-to-ready transition avoids a one-frame pose snap",smooth&&left.LowerBlend==0);
        left.Step(.5f,false,false,float.NaN);check("Invalid movement metadata cannot create nonfinite sway",float.IsFinite(left.Sample().SwayUpMetres));
        var before=left.Sample();left.Step(float.NaN,false,true,1);check("Invalid timestep leaves pose intact",left.Sample().VerticalMetres==before.VerticalMetres);
        left.Reset(false);var reset=left.Sample();check("Unpair reset clears sway and recoil as well as stance",reset.SwaySideMetres==0&&reset.SwayUpMetres==0&&reset.Recoil==0&&reset.LowerBlend==0);
    }
}
