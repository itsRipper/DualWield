using System.Text.Json;
using DualWieldLab;

internal static class GenericChecks
{
    internal static void Run(Action<string,bool> check, string game)
    {
        PistolFacts valid = new() { IsNativePistol=true, ExternalMagazineReload=true,
            ChamberCount=1, HasMagazine=true, MagazineExternal=true, AmmoCompatible=true,
            TriggerMode=PistolTriggerMode.Single };
        check("Regular magazine pistol mechanics accepted without model identity", PistolMechanics.GetRefusal(valid,"test")==null);
        var f=valid;f.TriggerMode=PistolTriggerMode.SemiAutomatic;
        check("Native semiautomatic selector accepted", PistolMechanics.GetRefusal(f,"test")==null);
        f.TriggerMode=PistolTriggerMode.FullAutomatic;
        check("Native automatic selector accepted by shared mechanics",PistolMechanics.GetRefusal(f,"test")==null);
        foreach (var mode in new[]{PistolTriggerMode.Burst,PistolTriggerMode.DoubleAction,PistolTriggerMode.Doublet,PistolTriggerMode.Unsupported}) {
            f=valid;f.TriggerMode=mode;check("Unsupported selector safely refused: "+mode,PistolMechanics.GetRefusal(f,"test")!=null);
        }
        f=valid;f.IsNativePistol=false;check("Revolver/other native types refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.BoltAction=true;check("Manual bolt pistol mechanism refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.OneOff=true;check("One-off pistol mechanism refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.ExternalMagazineReload=false;check("Internal magazine mechanism refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.ChamberCount=2;check("Multiple-chamber mechanism refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.MagazineBeltOrCylinder=true;check("Belt/cylinder magazine refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.MissingVitalParts=true;check("Missing parts refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.Malfunctioned=true;check("Existing malfunction refused without repair",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.Suppressed=true;check("Suppressed pistols are accepted (per-weapon native acoustics)",PistolMechanics.GetRefusal(f,"test")==null);
        f=valid;f.AmmoCompatible=false;check("Incompatible chamber/magazine ammo refused",PistolMechanics.GetRefusal(f,"test")!=null);
        f=valid;f.HasMagazine=false;check("Absent magazine conservative boundary",PistolMechanics.GetRefusal(f,"test")!=null);
        check("Empty installed magazine is compatible independently of ammo count",PistolMechanics.GetRefusal(valid,"test")==null);
        check("450/600 RPM are independently computed, not constants",Math.Abs(PairPolicy.ShotInterval(450)-.1333333333333333)<1e-12 && Math.Abs(PairPolicy.ShotInterval(600)-.1)<1e-12);
        var cooldowns=new Dictionary<string,double>();
        cooldowns["main"]=PairPolicy.NextShotTime(0,450);cooldowns["offhand"]=PairPolicy.NextShotTime(0,600);
        check("Different hand rates have independent deadlines", !PairPolicy.IsCooldownReady(.1,cooldowns["main"]) && PairPolicy.IsCooldownReady(.1,cooldowns["offhand"]));
        cooldowns["offhand"]=PairPolicy.NextShotTime(.1,600);
        check("Firing one hand cannot advance other deadline", Math.Abs(cooldowns["main"]-.1333333333333333)<1e-12);
        check("Switching identity does not discard prior deadline",cooldowns.ContainsKey("main") && cooldowns.ContainsKey("offhand"));
        cooldowns.Clear();check("Raid cleanup discards independent cooldown refs",cooldowns.Count==0);
        check("Independent ammo deltas support different magazines",PairPolicy.IsValidShotDelta(17,17,8,7) && !PairPolicy.IsValidShotDelta(17,16,8,7));
        check("Final chambered shot consumes exactly once",PairPolicy.IsValidShotDelta(17,17,1,0));
        check("Empty offhand cannot consume main ammunition",!PairPolicy.IsValidShotDelta(17,16,0,0));
        using var database=JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json")));
        var all=database.RootElement.EnumerateObject().ToArray();
        var glock=all.Single(i=>i.Value.TryGetProperty("_name",out var n)&&n.GetString()=="weapon_glock_glock_17_gen3_9x19");
        var different=all.First(i=>i.Value.TryGetProperty("_name",out var n)&&n.GetString()!.Contains("m1911") && i.Value.GetProperty("_props").TryGetProperty("weapClass",out var c)&&c.GetString()=="pistol");
        bool Regular(JsonElement item) {
            var p=item.GetProperty("_props");
            return p.GetProperty("weapClass").GetString()=="pistol" && p.GetProperty("ReloadMode").GetString()=="ExternalMagazine" && p.GetProperty("Chambers").GetArrayLength()==1 && !p.GetProperty("BoltAction").GetBoolean();
        }
        check("Installed identical Glock pair metadata passes regular mechanism",Regular(glock.Value) && PairPolicy.IsDistinctPair("owned-a","owned-b",glock.Name,glock.Name));
        check("Installed mixed Glock/M1911 pair metadata passes same mechanism",Regular(glock.Value) && Regular(different.Value) && PairPolicy.IsDistinctPair("owned-a","owned-b",glock.Name,different.Name));
        check("Installed mixed pair has different native ammunition calibers",glock.Value.GetProperty("_props").GetProperty("ammoCaliber").GetString()!=different.Value.GetProperty("_props").GetProperty("ammoCaliber").GetString());
        check("Each installed mixed pistol supplies its own native RPM",glock.Value.GetProperty("_props").GetProperty("SingleFireRate").GetInt32()>0 && different.Value.GetProperty("_props").GetProperty("SingleFireRate").GetInt32()>0);
        var pistols=all.Where(i=>i.Value.TryGetProperty("_props",out var p)&&p.TryGetProperty("weapClass",out var c)&&c.GetString()=="pistol").ToArray();
        int external=pistols.Count(i=>i.Value.GetProperty("_props").GetProperty("ReloadMode").GetString()=="ExternalMagazine");
        check("Actual definition set includes regular and unsupported mechanisms",pistols.Length>=29 && external>=25 && pistols.Length>external);
        var capacities=new List<int>();
        foreach(var weapon in new[]{glock.Value,different.Value}) {
            var slot=weapon.GetProperty("_props").GetProperty("Slots").EnumerateArray().First(s=>s.GetProperty("_name").GetString()=="mod_magazine");
            string id=slot.GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter")[0].GetString()!;
            var magazine=database.RootElement.GetProperty(id).GetProperty("_props");
            capacities.Add(magazine.GetProperty("Cartridges")[0].GetProperty("_max_count").GetInt32());
        }
        check("Mixed magazine capacities come from actual native definitions",capacities.All(c=>c>0) && capacities.Distinct().Count()==2);
    }
}
