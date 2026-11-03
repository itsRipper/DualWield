using System.Text.Json;
using DualWieldLab;
using Mono.Cecil;

internal static class AutomaticChecks
{
    internal static void Run(Action<string,bool> check,string game,AssemblyDefinition native,AssemblyDefinition plugin)
    {
        using var db=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json")));
        var items=db.RootElement.EnumerateObject().ToArray();
        JsonElement Props(string name)=>items.Single(i=>i.Value.TryGetProperty("_name",out var n)&&n.GetString()==name).Value.GetProperty("_props");
        var g17=Props("weapon_glock_glock_17_gen3_9x19");
        var g18=Props("weapon_glock_glock_18c_gen3_9x19");
        int single=g17.GetProperty("SingleFireRate").GetInt32(), automatic=g18.GetProperty("bFirerate").GetInt32();
        check("Installed G18C supplies fullauto1200/single450",automatic==1200&&g18.GetProperty("SingleFireRate").GetInt32()==450&&g18.GetProperty("weapFireType").EnumerateArray().Any(m=>m.GetString()=="fullauto"));
        check("Native single selector uses SingleFireRate even for G18C",PistolMechanics.EffectiveRate(PistolTriggerMode.Single,450,automatic,0)==450);
        check("Native fullauto selector uses FireRate",PistolMechanics.EffectiveRate(PistolTriggerMode.FullAutomatic,450,automatic,0)==automatic);
        check("Native heat multiplier adjusts automatic rate",Math.Abs(PistolMechanics.EffectiveRate(PistolTriggerMode.FullAutomatic,450,automatic,.75f)-automatic*.75d)<1e-8);
        check("Heat multiplier does not change native single getter",PistolMechanics.EffectiveRate(PistolTriggerMode.Single,450,automatic,.75f)==450);
        check("Automatic periods are not clamped to semiauto240 minimum",PairPolicy.ShotInterval(120d)==.5d&&PairPolicy.ShotInterval((double)automatic)==.05d);
        bool invalid=true; foreach(double rate in new[]{0d,-1d,double.NaN,double.PositiveInfinity}){try{PairPolicy.ShotInterval(rate);invalid=false;}catch(InvalidOperationException){}}
        check("Invalid effective rates refuse before scheduling",invalid);
        var trigger=new TriggerEdgeGate(); trigger.Observe(true,false);
        check("Fullauto requires admitted native initial press",!trigger.ClaimRepeat(0,true,true,PistolTriggerMode.FullAutomatic,1,0));
        check("Admitted automatic press starts independent hold",trigger.ClaimNative(1,true,true,PistolTriggerMode.FullAutomatic));
        check("Initial press and repeat cannot fire twice that frame",!trigger.ClaimRepeat(1,true,true,PistolTriggerMode.FullAutomatic,1,0));
        check("Held automatic fires when its own deadline is due",trigger.ClaimRepeat(2,true,true,PistolTriggerMode.FullAutomatic,.05,.05));
        check("Automatic waits until native rate deadline",!trigger.ClaimRepeat(3,true,true,PistolTriggerMode.FullAutomatic,.075,.1));
        check("Release stops automatic immediately",!trigger.ClaimRepeat(4,true,false,PistolTriggerMode.FullAutomatic,.1,.1)&&!trigger.Holding);
        check("Physical hold cannot synthesize a new press",!trigger.ClaimRepeat(5,true,true,PistolTriggerMode.FullAutomatic,1,0));
        check("New physical press rearms automatic",trigger.ClaimNative(6,true,true,PistolTriggerMode.FullAutomatic));
        check("Focus/UI loss cancels automatic",!trigger.ClaimRepeat(7,false,true,PistolTriggerMode.FullAutomatic,2,0)&&!trigger.Holding);
        check("Held focus resume never restarts automatic",!trigger.ClaimNative(8,true,true,PistolTriggerMode.FullAutomatic));
        trigger.Observe(true,false); trigger.ClaimNative(9,true,true,PistolTriggerMode.FullAutomatic);
        check("Selector change during hold cancels rather than converting trigger",!trigger.ClaimRepeat(10,true,true,PistolTriggerMode.Single,3,0)&&!trigger.Holding);
        trigger.Observe(true,false); trigger.ClaimNative(11,true,true,PistolTriggerMode.Single);
        check("Holding single-mode cannot become auto without release",!trigger.ClaimRepeat(12,true,true,PistolTriggerMode.FullAutomatic,4,0));
        trigger.Observe(true,false); trigger.ClaimNative(13,true,true,PistolTriggerMode.FullAutomatic); trigger.CancelHold();
        check("Empty/draw/obstruction cancellation requires release",!trigger.ClaimRepeat(14,true,true,PistolTriggerMode.FullAutomatic,5,0)&&!trigger.ClaimNative(15,true,true,PistolTriggerMode.FullAutomatic));

        int Count(PistolTriggerMode mode,double rpm,int rounds=100){
            var t=new TriggerEdgeGate(); t.Observe(true,false); double next=0; int count=0;
            for(int ms=0;ms<1000;ms++){
                bool request=ms==0?t.ClaimNative(ms,true,true,mode):t.ClaimRepeat(ms,true,true,mode,ms/1000d,next);
                if(!request||count>=rounds)continue;
                count++; next=PairPolicy.NextShotTime(ms/1000d,rpm);
            } return count;
        }
        int semi=Count(PistolTriggerMode.Single,single),auto=Count(PistolTriggerMode.FullAutomatic,automatic),other=Count(PistolTriggerMode.FullAutomatic,750);
        check("G17+G17 held combination emits one request each",semi==1&&Count(PistolTriggerMode.Single,single)==1);
        check("G17+G18C mixed hold independently schedules one vs20",semi==1&&auto>=19&&auto<=20);
        check("G18C+G18C separate schedulers sustain native cadence",auto>=19&&Count(PistolTriggerMode.FullAutomatic,automatic)==auto);
        check("Different automatic RPM values retain separate cadence",other>=12&&other<=13&&other<auto);
        check("Empty/final-round fixtures cannot generate extra ammunition",Count(PistolTriggerMode.FullAutomatic,automatic,0)==0&&Count(PistolTriggerMode.FullAutomatic,automatic,1)==1);
        trigger.Reset(); trigger.Observe(true,false);trigger.ClaimNative(0,true,true,PistolTriggerMode.FullAutomatic);
        check("Long stall admits at most one offhand round",trigger.ClaimRepeat(1,true,true,PistolTriggerMode.FullAutomatic,10,.05)&&!trigger.ClaimRepeat(1,true,true,PistolTriggerMode.FullAutomatic,10,.05));
        double afterStall=PairPolicy.NextShotTime(10,(double)automatic);
        check("Stall debt is discarded; deadline starts at current simulation time",afterStall==10.05&&!trigger.ClaimRepeat(2,true,true,PistolTriggerMode.FullAutomatic,10.001,afterStall));
        check("Primary native stalled step capped to one old period",Math.Abs(PistolMechanics.BoundedNativeStep(2,.05,false)-.05f)<1e-8);
        check("Primary repeated/initial shot frame cannot advance native cycle",PistolMechanics.BoundedNativeStep(.05f,.05,true)==0);
        check("Native old/new heat periods cap to shorter during transition",PistolMechanics.BoundedNativeStep(2,Math.Min(.05,.1),false)<=.050001f);
        check("Invalid native timestep/period cannot advance cycles",PistolMechanics.BoundedNativeStep(float.NaN,.05,false)==0&&PistolMechanics.BoundedNativeStep(1,double.NaN,false)==0);

        PistolAnimationProfile.TryCreate(PistolAnimationFamily.MagazineFedPistol,50,100,.2f,out var profile);
        var l=new PistolAnimationState(profile);var r=new PistolAnimationState(profile);bool bounded=true;
        for(int i=0;i<10000;i++){l.Step(.001f,true,false);r.Step(.001f,true,false);if(i%50==0){l.Fire();r.Fire();}var p=l.Sample();bounded&=l.Recoil>=0&&l.Recoil<=1&&float.IsFinite(p.PitchUpDegrees)&&l.Recoil==r.Recoil;}
        check("Simultaneous1200RPM procedural recoil remains finite/bounded",bounded);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var nt=All(native.MainModule.Types).ToDictionary(t=>t.FullName);var pt=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string name)=>t.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string name)=>Calls(m).Any(c=>c.Name==name);
        var rt=pt["DualWieldLab.RuntimePlugin"];var fc=nt["EFT.Player/FirearmController"];var op=nt["EFT.Player/FirearmController/AutomaticFireOperation"];
        check("Automatic Harmony argument uses positional native float ABI",M(rt,"BeforeAutomaticUpdate").Parameters.Last().Name=="__0"&&M(op,"Update").Parameters.Single().ParameterType.FullName=="System.Single");
        check("UI-filtered owner admission uses positional ref axes/cursor ABI",M(rt,"ObserveOwnerInput").Parameters.Single(p=>p.Name=="__1").ParameterType.FullName=="System.Single[]&"&&M(rt,"ObserveOwnerInput").Parameters.Single(p=>p.Name=="__2").ParameterType.IsByReference);
        check("Native UI axes suppression cancels both paired triggers",Has(M(rt,"ObserveOwnerInput"),"StopOffhandTrigger")&&Has(M(rt,"ObserveOwnerInput"),"UpdatePrimaryTriggerSafety"));
        check("Native primary budget callback subscribed and unsubscribed",Has(M(rt,"TryPair"),"add_OnShot")&&Has(M(rt,"Unpair"),"remove_OnShot"));
        check("Primary bounded guard checks exact native Controller",M(rt,"BeforeAutomaticUpdate").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="Controller")&&Has(M(rt,"BeforeAutomaticUpdate"),"BoundedNativeStep"));
        check("Native automatic release goes to idle before update",Has(M(rt,"BeforeAutomaticUpdate"),"SwitchToIdling")&&Has(M(rt,"Unpair"),"SwitchToIdling"));
        check("Primary release synchronizes native translator trigger flag",M(rt,"StopPrimaryTrigger").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_firearmTriggerPressed"));
        check("Automatic repeats use same shared TryFire",Has(M(rt,"Update"),"ClaimRepeat")&&Has(M(rt,"Update"),"TryFire"));
        check("Automatic loop sound release remains native",Has(M(pt["DualWieldLab.OffhandShotContext"],"ReleaseTrigger"),"set_IsTriggerPressed"));
        check("Offhand delegates native malfunction selection and ammo handling",Has(M(pt["DualWieldLab.OffhandShotContext"],"FireOnce"),"ProcessInventoryOperationsForSingleshotWithMalf"));
        check("Offhand recoil does not call native primary animation Shoot",!Calls(M(rt,"TryFire")).Any(c=>c.DeclaringType.Name=="ProceduralWeaponAnimation"));
        check("No persistent inventory slot/template mutation added",!pt.Values.SelectMany(t=>t.Methods).SelectMany(Calls).Any(c=>c.Name is "AddSlot" or "set_SlotId" or "set_ParentId" or "SaveProfile"));
        var equipment=nt["EFT.InventoryLogic.EquipmentSlot"];
        check("Native equipment remains15 slots and exactlyoneHolster",equipment.Fields.Count(f=>f.HasConstant)==15&&equipment.Fields.Count(f=>f.Name=="Holster")==1&&!equipment.Fields.Any(f=>f.Name=="HolsterLeft"));
    }
}
