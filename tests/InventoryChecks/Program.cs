using System.Security.Cryptography;
using System.Text.Json;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

// This is an isolated data-contract probe. It does not start SaveServer, DI,
// HTTP, the game or a raid, and never reads user/profiles. All IDs are synthetic.
if(args.Length!=2) throw new ArgumentException("Supply project root and local SPT root.");
string project=Path.GetFullPath(args[0]),game=Path.GetFullPath(args[1]);
var results=new List<object>(); int failures=0;
void Check(string name,bool passed) { results.Add(new{name,passed}); Console.WriteLine((passed?"PASS ":"FAIL ")+name); if(!passed)failures++; }
Check("Exact inspected SPT server assembly",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game,"SPT_Runtime","SPTarkov.Server.Core.dll")))).Equals("490409F7C67480A8BF647DA67ADA8B91361330C2A9324D9E9787CD1EBAF5BF27",StringComparison.OrdinalIgnoreCase));
var json=new JsonUtil(new[]{new SptJsonConverterRegistrator()});
string dbPath=Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json");
using var database=JsonDocument.Parse(File.ReadAllText(dbPath));
string Find(string name)=>database.RootElement.EnumerateObject().Single(p=>p.Value.TryGetProperty("_name",out var n)&&n.GetString()==name).Name;
string g17=Find("weapon_glock_glock_17_gen3_9x19"),g18=Find("weapon_glock_glock_18c_gen3_9x19");
string equipment=database.RootElement.EnumerateObject().First(p=>p.Value.TryGetProperty("_name",out var n)&&n.GetString()=="Inventory").Name;
string stash=database.RootElement.EnumerateObject().First(p=>p.Value.TryGetProperty("_name",out var n)&&n.GetString()!.StartsWith("stash ",StringComparison.OrdinalIgnoreCase)&&p.Value.GetProperty("_props").GetProperty("Grids").GetArrayLength()>0).Name;
MongoId Id(int n)=>(MongoId)n.ToString("x24");
Item Item(int n,string tpl,int? parent,string? slot)=>new(){Id=Id(n),Template=(MongoId)tpl,ParentId=parent.HasValue?Id(parent.Value).ToString():null,SlotId=slot};
// A proposed custom container shape only. Its template is intentionally absent:
// preservation by JSON is tested separately from template validity/uninstallation.
string custom="dddddddddddddddddddddddd";
var items=new List<Item>{Item(1,equipment,null,null),Item(2,custom,1,"Holster"),Item(3,g17,2,"left_pistol"),Item(4,g18,2,"right_pistol")};
string Magazine(string weapon)=>database.RootElement.GetProperty(weapon).GetProperty("_props").GetProperty("Slots").EnumerateArray().Single(s=>s.GetProperty("_name").GetString()=="mod_magazine").GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter")[0].GetString()!;
string ammo=database.RootElement.EnumerateObject().First(p=>p.Value.TryGetProperty("_props",out var p0)&&p0.TryGetProperty("Caliber",out var c)&&c.GetString()=="Caliber9x19PARA"&&p0.TryGetProperty("ammoType",out var a)&&a.GetString()=="bullet").Name;
items.AddRange(new[]{Item(5,Magazine(g17),3,"mod_magazine"),Item(6,Magazine(g18),4,"mod_magazine"),Item(7,ammo,5,"cartridges"),Item(8,ammo,6,"cartridges"),Item(9,ammo,3,"patron_in_weapon"),Item(11,ammo,4,"patron_in_weapon")});
items.Single(i=>i.Id==Id(7)).Upd=new Upd{StackObjectsCount=7};items.Single(i=>i.Id==Id(8)).Upd=new Upd{StackObjectsCount=11};
var baseline=json.Serialize(items,false)!;
var roundtrip=json.Deserialize<List<Item>>(baseline)!;
Check("Native JSON preserves separate synthetic pistol identities",roundtrip.Count==10&&roundtrip.Select(i=>i.Id).Distinct().Count()==10&&roundtrip[2].Id==Id(3)&&roundtrip[3].Id==Id(4));
Check("Native JSON preserves nested native parent/slot strings",roundtrip[2].ParentId==Id(2).ToString()&&roundtrip[2].SlotId=="left_pistol"&&roundtrip[3].SlotId=="right_pistol");
Check("Native JSON preserves separate magazine/chamber/ammo subtrees",roundtrip.GetItemWithChildren(Id(3)).Select(i=>i.Id).ToHashSet().SetEquals(new[]{Id(3),Id(5),Id(7),Id(9)})&&roundtrip.GetItemWithChildren(Id(4)).Select(i=>i.Id).ToHashSet().SetEquals(new[]{Id(4),Id(6),Id(8),Id(11)}));
Check("Native JSON preserves independent ammunition stack counts",roundtrip.Single(i=>i.Id==Id(7)).Upd!.StackObjectsCount==7&&roundtrip.Single(i=>i.Id==Id(8)).Upd!.StackObjectsCount==11);
Check("Native child traversal includes both pistols below equipment",roundtrip.GetItemWithChildren(Id(1)).Count==10&&roundtrip.GetItemWithChildren(Id(2)).Count==9);
var rightSubtree=roundtrip.GetItemWithChildren(Id(4)).Select(i=>i.Id).ToHashSet();
Check("Native child traversal tolerates single occupied pistol slot",roundtrip.Where(i=>!rightSubtree.Contains(i.Id)).GetItemWithChildren(Id(2)).Count==5);
var changed=roundtrip.Single(i=>i.Id==Id(3));changed.SlotId="HolsterLeft";changed.ParentId=Id(1).ToString();
var unsupported=json.Deserialize<List<Item>>(json.Serialize(roundtrip,false))!;
Check("Server JSON accepts unknown equipment slot strings (not client proof)",unsupported.Single(i=>i.Id==Id(3)).SlotId=="HolsterLeft");
Check("Absent custom template proves container cannot be vanilla-resolved",!database.RootElement.TryGetProperty(custom,out _));
// No migration is deployed. Demonstrate only a disposable rollback of ordinary
// ownership addresses, preserving IDs, and roundtrip through native serialization.
roundtrip.Add(Item(10,stash,null,null));
foreach(var pistol in roundtrip.Where(i=>i.Id==Id(3)||i.Id==Id(4))) { pistol.ParentId=pistol.Id==Id(3)?Id(1).ToString():Id(10).ToString();pistol.SlotId=pistol.Id==Id(3)?"Holster":"hideout"; if(pistol.Id==Id(4))pistol.Location=new{x=0,y=0,r=0}; }
var afterRollback=json.Deserialize<List<Item>>(json.Serialize(roundtrip.Where(i=>i.Id!=Id(2)).ToList(),false))!;
Check("Synthetic rollback preserves both pistol IDs and removes custom parent",afterRollback.Count==10&&afterRollback.Select(i=>i.Id).ToHashSet().SetEquals(new[]{Id(1),Id(3),Id(4),Id(5),Id(6),Id(7),Id(8),Id(9),Id(10),Id(11)})&&afterRollback.All(i=>i.ParentId!=Id(2).ToString()));
Check("Rollback root traversal contains no orphan fixture items",afterRollback.GetItemWithChildren(Id(1)).Count==5&&afterRollback.GetItemWithChildren(Id(10)).Count==5);
Check("Rollback fixture resolves all templates without mod data",afterRollback.All(i=>database.RootElement.TryGetProperty(i.Template.ToString(),out _)));
var lost=json.Deserialize<LostOnDeathConfig>(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","configs","lostondeath.json")))!;
var pmc=Activator.CreateInstance<PmcData>();
pmc.Inventory=Activator.CreateInstance<BotBaseInventory>();pmc.Inventory.Equipment=Id(1);pmc.Inventory.Items=items;
var probe=new DeathProbe(lost);
Check("Native ordinary Holster follows installed lost-on-death flag",probe.Kept(pmc,Item(9,g17,1,"Holster"))==!lost.Equipment.Holster);
string unknownDeath;
try { unknownDeath="kept="+probe.Kept(pmc,Item(9,g17,1,"HolsterLeft")); }
catch(Exception e) { unknownDeath="throws="+e.GetType().FullName; }
Check("New slot has no defined installed death property",typeof(LostEquipment).GetProperties().All(p=>p.Name!="HolsterLeft"));
Check("Native unknown slot retention confirms death-contract blocker",unknownDeath=="kept=True");
Console.WriteLine("OBSERVED unknown-slot death contract: "+unknownDeath);
var report=new {executedAtUtc=DateTime.UtcNow,checks=results.Count,failures,serverCoreSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(game,"SPT_Runtime","SPTarkov.Server.Core.dll")))).ToLowerInvariant(),databaseSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dbPath))).ToLowerInvariant(),unknownSlotDeathResult=unknownDeath,scope="Native JsonUtil/models/child traversal and native death predicate on entirely synthetic in-memory fixtures. No SaveServer, server startup, raid, client inventory, profile files, mail or insurance transaction executed. Rollback fixture is address conservation only, not a valid equipped/stash placement.",results};
string resultsDir=Path.Combine(project,"artifacts","test-results");
Directory.CreateDirectory(resultsDir);
File.WriteAllText(Path.Combine(resultsDir,"inventory-server-checks.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
return failures==0?0:1;

internal sealed class DeathProbe : InRaidHelper
{
    internal DeathProbe(LostOnDeathConfig config):base(null!,null!,null!,null!,config,null!){}
    internal bool Kept(PmcData pmc,Item item)=>IsItemKeptAfterDeath(pmc,item);
}
