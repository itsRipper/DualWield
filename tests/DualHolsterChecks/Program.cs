using System.Text.Json;
using DualWieldLab.DualHolster.Server;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

// Isolated: installed server/client code on synthetic in-memory data. No server start,
// SaveServer, profile files, raid or game. Proves the server/data contracts only.
if (args.Length != 2) throw new ArgumentException("Supply project root and local SPT root.");
string project = Path.GetFullPath(args[0]), game = Path.GetFullPath(args[1]);
var results = new List<object>(); int failures = 0;
void Check(string name, bool passed) { results.Add(new { name, passed }); Console.WriteLine((passed ? "PASS " : "FAIL ") + name); if (!passed) failures++; }

var json = new JsonUtil(new[] { new SptJsonConverterRegistrator() });
using var database = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "SPT_Runtime", "SPT_Data", "database", "templates", "items.json")));
string Find(string name) => database.RootElement.EnumerateObject().Single(p => p.Value.TryGetProperty("_name", out var n) && n.GetString() == name).Name;
var inventory = json.Deserialize<TemplateItem>(database.RootElement.GetProperty(LeftHolster.DefaultInventory.ToString()).GetRawText())!;
int before = inventory.Properties!.Slots!.Count();

// 1. Template: one real slot, a copy of the native Holster definition.
Check("Default Inventory gains the left holster slot", LeftHolster.AddTo(inventory) && inventory.Properties.Slots!.Count() == before + 1);
Check("Adding is idempotent", LeftHolster.AddTo(inventory) && inventory.Properties.Slots!.Count() == before + 1);
var slots = inventory.Properties.Slots!.ToList();
var holster = slots.Single(s => s.Name == "Holster");
var left = slots.Single(s => s.Name == LeftHolster.SlotName);
Check("Left holster sits after the native holster with its own id",
    slots.IndexOf(left) == slots.IndexOf(holster) + 1 && left.Id == LeftHolster.SlotId && left.Id != holster.Id
    && slots.Select(s => s.Id).Distinct().Count() == slots.Count);
Check("Left holster admits exactly the native holster's items",
    json.Serialize(left.Properties, false) == json.Serialize(holster.Properties, false) && left.Required == holster.Required);
var serialized = json.Serialize(inventory, false)!;
Check("Template serializes the slot under its own name", serialized.Contains("\"_name\":\"HolsterLeft\"") && serialized.Contains(LeftHolster.SlotId.ToString()));

// 2. Death: the real native predicate, with the mod's patch enabled through SPT's patcher.
var lost = json.Deserialize<LostOnDeathConfig>(File.ReadAllText(Path.Combine(game, "SPT_Runtime", "SPT_Data", "configs", "lostondeath.json")))!;
MongoId Id(int n) => (MongoId)n.ToString("x24");
string g17 = Find("weapon_glock_glock_17_gen3_9x19"), g18 = Find("weapon_glock_glock_18c_gen3_9x19");
string equipment = LeftHolster.DefaultInventory.ToString();
string stashTpl = database.RootElement.EnumerateObject().First(p => p.Value.TryGetProperty("_name", out var n) && n.GetString()!.StartsWith("stash ", StringComparison.OrdinalIgnoreCase)
    && p.Value.GetProperty("_props").GetProperty("Grids").GetArrayLength() > 0).Name;
Item Item(int n, string tpl, int? parent, string? slot) => new() { Id = Id(n), Template = (MongoId)tpl, ParentId = parent.HasValue ? Id(parent.Value).ToString() : null, SlotId = slot };
string Magazine(string weapon) => database.RootElement.GetProperty(weapon).GetProperty("_props").GetProperty("Slots").EnumerateArray()
    .Single(s => s.GetProperty("_name").GetString() == "mod_magazine").GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter")[0].GetString()!;
string ammo = database.RootElement.EnumerateObject().First(p => p.Value.TryGetProperty("_props", out var p0) && p0.TryGetProperty("Caliber", out var c)
    && c.GetString() == "Caliber9x19PARA" && p0.TryGetProperty("ammoType", out var a) && a.GetString() == "bullet").Name;
List<Item> Fixture() => new()
{
    Item(1, equipment, null, null), Item(2, stashTpl, null, null),
    Item(3, g17, 1, "Holster"), Item(4, g18, 1, LeftHolster.SlotName),
    Item(5, Magazine(g17), 3, "mod_magazine"), Item(6, Magazine(g18), 4, "mod_magazine"),
    Item(7, ammo, 5, "cartridges"), Item(8, ammo, 6, "cartridges"), Item(9, ammo, 4, "patron_in_weapon"),
};
var items = Fixture();
items.Single(i => i.Id == Id(8)).Upd = new Upd { StackObjectsCount = 11 };
var pmc = Activator.CreateInstance<PmcData>();
pmc.Inventory = Activator.CreateInstance<BotBaseInventory>();
pmc.Inventory.Equipment = Id(1); pmc.Inventory.Stash = Id(2); pmc.Inventory.Items = items;
var probe = new DeathProbe(lost);
bool nativeHolsterKept = probe.Kept(pmc, items.Single(i => i.Id == Id(3)));
bool unpatched = probe.Kept(pmc, items.Single(i => i.Id == Id(4)));
new LeftHolsterDeathPatch(lost).Activate();
bool patched = probe.Kept(pmc, items.Single(i => i.Id == Id(4)));
Check("Without the patch the native predicate keeps the unknown slot (the blocker)", unpatched);
Check("With the patch the left holster follows the native Holster loss rule", patched == nativeHolsterKept && patched == !lost.Equipment.Holster);
Check("The native Holster decision is unchanged by the patch", probe.Kept(pmc, items.Single(i => i.Id == Id(3))) == nativeHolsterKept);
Check("Children of the left pistol are not equipment roots and keep native rules",
    probe.Kept(pmc, items.Single(i => i.Id == Id(6))) == probe.Kept(pmc, items.Single(i => i.Id == Id(5))));

// 3. Persistence: both pistols remain separate, real equipment children.
var reloaded = json.Deserialize<List<Item>>(json.Serialize(items, false))!;
Check("Save/load keeps both pistol identities, slots and subtrees",
    reloaded.Single(i => i.Id == Id(4)).SlotId == LeftHolster.SlotName && reloaded.Single(i => i.Id == Id(3)).SlotId == "Holster"
    && reloaded.GetItemWithChildren(Id(4)).Select(i => i.Id).ToHashSet().SetEquals(new[] { Id(4), Id(6), Id(8), Id(9) })
    && reloaded.Single(i => i.Id == Id(8)).Upd!.StackObjectsCount == 11);
Check("Equipment traversal (raid snapshot, extraction) includes both holsters", reloaded.GetItemWithChildren(Id(1)).Count == 8);

// 4. Uninstall migration plan.
var grid = database.RootElement.GetProperty(stashTpl).GetProperty("_props").GetProperty("Grids")[0].GetProperty("_props");
int cellsH = grid.GetProperty("cellsH").GetInt32(), cellsV = grid.GetProperty("cellsV").GetInt32();
(int, int) Size(Item pistol) => (2, 1);
var occupied = new int[cellsV, cellsH]; occupied[0, 0] = 1; occupied[0, 1] = 1;
var plan = LeftHolster.PlanUninstall(reloaded, Id(1), Id(2), occupied, Size);
Check("Occupied Holster: the left pistol goes to a free stash cell", plan != null && plan.Count == 1 && plan[0].ParentId == Id(2).ToString()
    && plan[0].SlotId == "hideout" && plan[0].Location != null && !(plan[0].Location!.Y == 0 && plan[0].Location!.X < 2));
var migrated = json.Deserialize<List<Item>>(json.Serialize(reloaded, false))!;
LeftHolster.Apply(migrated, plan!);
Check("Migration keeps every item id and the whole subtree", migrated.Select(i => i.Id).ToHashSet().SetEquals(reloaded.Select(i => i.Id))
    && migrated.GetItemWithChildren(Id(4)).Count == 4 && migrated.Single(i => i.Id == Id(6)).ParentId == Id(4).ToString());
Check("After migration nothing refers to the left holster", migrated.All(i => i.SlotId != LeftHolster.SlotName));
var stashOnly = reloaded.Where(i => i.Id != Id(3) && i.ParentId != Id(3).ToString() && i.ParentId != Id(5).ToString()).ToList();
var toHolster = LeftHolster.PlanUninstall(stashOnly, Id(1), Id(2), new int[cellsV, cellsH], Size);
Check("Empty Holster: the left pistol becomes the ordinary holstered pistol",
    toHolster != null && toHolster.Single().SlotId == "Holster" && toHolster.Single().Location == null);
var full = new int[cellsV, cellsH];
for (int y = 0; y < cellsV; y++) for (int x = 0; x < cellsH; x++) full[y, x] = 1;
var untouched = json.Serialize(reloaded, false);
Check("Full stash: migration refuses and changes nothing", LeftHolster.PlanUninstall(reloaded, Id(1), Id(2), full, Size) == null
    && json.Serialize(reloaded, false) == untouched);
// Scav inventory: a scav can extract with a pistol in the left holster (the slot is on the shared
// Default Inventory template); it must not be stranded in the scav profile.
List<Item> ScavFixture(bool holsterTaken)
{
    var scav = new List<Item> { Item(21, equipment, null, null), Item(24, g18, 21, LeftHolster.SlotName), Item(26, Magazine(g18), 24, "mod_magazine"), Item(28, ammo, 26, "cartridges") };
    if (holsterTaken) scav.Add(Item(23, g17, 21, "Holster"));
    return scav;
}
var scavFree = LeftHolster.PlanScav(ScavFixture(false), Id(21), new int[cellsV, cellsH], Size);
Check("Scav: an empty scav Holster takes the left pistol", scavFree is var (fm, ft) && fm.Single().SlotId == "Holster" && ft.Count == 0);
var scavItems = ScavFixture(true);
var pmcItems = json.Deserialize<List<Item>>(json.Serialize(migrated, false))!;
var sharedMap = new int[cellsV, cellsH]; sharedMap[0, 0] = 1; sharedMap[0, 1] = 1;
var scavPlan = LeftHolster.PlanScav(scavItems, Id(21), sharedMap, Size);
Check("Scav: with the scav Holster taken, the pistol goes to a free PMC stash cell", scavPlan is var (sm, st) && sm.Count == 0 && st.Count == 1
    && !(st[0].Location.Y == 0 && st[0].Location.X < 2));
LeftHolster.ApplyTransfers(scavItems, pmcItems, Id(2), scavPlan!.Value.Transfers);
Check("Scav transfer keeps ids and the whole subtree and leaves nothing in the scav slot",
    scavItems.All(i => i.SlotId != LeftHolster.SlotName) && scavItems.All(i => i.Id != Id(24) && i.Id != Id(26) && i.Id != Id(28))
    && pmcItems.GetItemWithChildren(Id(24)).Count == 3 && pmcItems.Single(i => i.Id == Id(24)).ParentId == Id(2).ToString()
    && pmcItems.Single(i => i.Id == Id(24)).SlotId == "hideout");
var fullScav = new int[cellsV, cellsH];
for (int y = 0; y < cellsV; y++) for (int x = 0; x < cellsH; x++) fullScav[y, x] = 1;
Check("Scav: a full stash refuses the scav plan", LeftHolster.PlanScav(ScavFixture(true), Id(21), fullScav, Size) == null);
var build = Activator.CreateInstance<SPTarkov.Server.Core.Models.Eft.Profile.EquipmentBuild>();
build.Items = Fixture();
Check("Saved equipment builds lose only their left-holster entries (presets, not owned items)",
    LeftHolster.StripBuilds(new[] { build.Items }) == 1 && build.Items.All(i => i.SlotId != LeftHolster.SlotName)
    && build.Items.All(i => i.Id != Id(4) && i.Id != Id(6) && i.Id != Id(8) && i.Id != Id(9)) && build.Items.Any(i => i.Id == Id(3)));

// Whole-profile migration: all or nothing across PMC inventory, Scav inventory and saved builds.
List<Item> Clone(List<Item> list) => json.Deserialize<List<Item>>(json.Serialize(list, false))!;
SPTarkov.Server.Core.Models.Eft.Profile.SptProfile Profile(Action<PmcData, PmcData>? damage = null)
{
    var p = Activator.CreateInstance<SPTarkov.Server.Core.Models.Eft.Profile.SptProfile>();
    var pmcData = Activator.CreateInstance<PmcData>();
    pmcData.Inventory = Activator.CreateInstance<BotBaseInventory>();
    pmcData.Inventory.Equipment = Id(1); pmcData.Inventory.Stash = Id(2); pmcData.Inventory.Items = Fixture();
    var scavData = Activator.CreateInstance<PmcData>();
    scavData.Inventory = Activator.CreateInstance<BotBaseInventory>();
    scavData.Inventory.Equipment = Id(21); scavData.Inventory.Items = ScavFixture(true);
    p.CharacterData = Activator.CreateInstance<SPTarkov.Server.Core.Models.Eft.Profile.Characters>();
    p.CharacterData.PmcData = pmcData; p.CharacterData.ScavData = scavData;
    var saved = Activator.CreateInstance<SPTarkov.Server.Core.Models.Eft.Profile.EquipmentBuild>();
    saved.Items = Fixture();
    p.UserBuildData = Activator.CreateInstance<SPTarkov.Server.Core.Models.Eft.Profile.UserBuilds>();
    p.UserBuildData.EquipmentBuilds = new() { saved };
    damage?.Invoke(pmcData, scavData);
    return p;
}
int[,] Map(int free)
{
    var m = new int[cellsV, cellsH];
    for (int y = 0; y < cellsV; y++) for (int x = 0; x < cellsH; x++) m[y, x] = y * cellsH + x < cellsV * cellsH - free ? 1 : 0;
    return m;
}
Func<List<Item>, Func<Item, (int, int)>> Sizes = _ => Size;
LeftHolster.UninstallResult Migrate(SPTarkov.Server.Core.Models.Eft.Profile.SptProfile p, int[,]? map) => LeftHolster.MigrateProfile(p, Clone, _ => map, Sizes);
bool RefusedUnchanged(SPTarkov.Server.Core.Models.Eft.Profile.SptProfile p, int[,]? map, LeftHolster.UninstallOutcome expected)
{
    string savedProfile = json.Serialize(p, false)!;
    return savedProfile.Contains(LeftHolster.SlotName) && savedProfile.Contains(Id(21).ToString())
        && Migrate(p, map).Outcome == expected && json.Serialize(p, false) == savedProfile;
}
Check("Profile migration with a full stash refuses and leaves the whole profile unchanged (builds, item ids, inventory, Scav)",
    RefusedUnchanged(Profile(), Map(0), LeftHolster.UninstallOutcome.NoSpace)
    && Profile().UserBuildData!.EquipmentBuilds![0].Items.Any(i => i.SlotId == LeftHolster.SlotName));
Check("Profile migration where the PMC pistol fits but the Scav pistol does not changes nothing",
    RefusedUnchanged(Profile(), Map(2), LeftHolster.UninstallOutcome.NoSpace));
Check("A profile using the slot without a stash id is unreadable and unchanged",
    RefusedUnchanged(Profile((pm, _) => pm.Inventory!.Stash = null), Map(100), LeftHolster.UninstallOutcome.Unreadable));
Check("A profile using the slot whose stash cannot be read is unreadable and unchanged",
    RefusedUnchanged(Profile(), null, LeftHolster.UninstallOutcome.Unreadable));
Check("A left-holster item outside the equipment is unreadable and unchanged",
    RefusedUnchanged(Profile((pm, _) => pm.Inventory!.Items!.Single(i => i.Id == Id(4)).ParentId = Id(2).ToString()), Map(100), LeftHolster.UninstallOutcome.Unreadable));
Check("A Scav using the slot without an equipment id is unreadable and unchanged (was skipped as unused)",
    RefusedUnchanged(Profile((_, sc) => sc.Inventory!.Equipment = null), Map(100), LeftHolster.UninstallOutcome.Unreadable));
Check("Duplicate item ids are unreadable and unchanged",
    RefusedUnchanged(Profile((pm, _) => pm.Inventory!.Items!.Add(Item(4, g18, 1, "Holster"))), Map(100), LeftHolster.UninstallOutcome.Unreadable));
var good = Profile();
var goodIds = good.CharacterData!.PmcData!.Inventory!.Items!.Select(i => i.Id).Concat(good.CharacterData.ScavData!.Inventory!.Items!.Select(i => i.Id)).ToHashSet();
var done = Migrate(good, Map(100));
Check("A complete profile migrates PMC, Scav and builds together with unchanged item ids",
    done is { Outcome: LeftHolster.UninstallOutcome.Migrated, Pistols: 1, ScavPistols: 1, BuildEntries: 1 }
    && good.CharacterData.PmcData.Inventory.Items.Concat(good.CharacterData.ScavData.Inventory.Items)
        .Concat(good.UserBuildData!.EquipmentBuilds![0].Items).All(i => i.SlotId != LeftHolster.SlotName)
    && good.CharacterData.PmcData.Inventory.Items.Select(i => i.Id).Concat(good.CharacterData.ScavData.Inventory.Items.Select(i => i.Id)).ToHashSet().SetEquals(goodIds));
var unused = Profile((pm, sc) => { pm.Inventory!.Items!.RemoveAll(i => i.SlotId == LeftHolster.SlotName); sc.Inventory!.Items!.RemoveAll(i => i.SlotId == LeftHolster.SlotName); });
unused.UserBuildData!.EquipmentBuilds!.Clear();
Check("A profile that does not use the slot is reported unused", Migrate(unused, Map(0)).Outcome == LeftHolster.UninstallOutcome.Unused);
Check("A migrated inventory needs no mod template or slot", migrated.All(i => i.Template == (MongoId)equipment || i.Template == (MongoId)stashTpl
    || database.RootElement.TryGetProperty(i.Template.ToString(), out _)));

// 5. Client contracts the client plugin relies on (installed Assembly-CSharp, read-only).
using var client = AssemblyDefinition.ReadAssembly(Path.Combine(game, "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll"));
IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] { t }.Concat(All(t.NestedTypes)));
var types = All(client.MainModule.Types).ToList();
var slotEnum = types.Single(t => t.FullName == "EFT.InventoryLogic.EquipmentSlot");
var values = slotEnum.Fields.Where(f => f.HasConstant).Select(f => Convert.ToInt32(f.Constant)).ToList();
Check("Client EquipmentSlot has 15 values ending at 14: the appended slot is index 15", values.Count == 15 && values.Max() == 14);
var ctor = types.Single(t => t.FullName == "EFT.InventoryLogic.InventoryEquipment").Methods.Single(m => m.IsConstructor && m.Parameters.Count == 2);
Check("InventoryEquipment indexes slots through exactly one Enum.Parse (the transpiled call)",
    ctor.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "Parse" && m.DeclaringType.FullName == "System.Enum") == 1);
var parsers = types.SelectMany(t => t.Methods).Where(m => m.HasBody
    && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldtoken && (i.Operand as TypeReference)?.FullName == "EFT.InventoryLogic.EquipmentSlot")
    && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.DeclaringType.FullName == "System.Enum" && r.Name is "Parse" or "TryParse"))
    .Select(m => m.FullName).ToList();
foreach (var parser in parsers) Console.WriteLine("SLOT PARSER " + parser);
Check("No other client code parses slot names into EquipmentSlot", parsers.Count == 1 && parsers[0] == ctor.FullName);
var tab = types.Single(t => t.FullName == "EFT.UI.EquipmentTab");
Check("Equipment tab binds views through its EquipmentSlot dictionary and one holster view",
    tab.Fields.Any(f => f.Name == "_slotViews") && tab.Fields.Count(f => f.Name == "_holsterSlot") == 1);
// Found in the isolated client run: total weight skipped the left holster, and the insurance
// screens build their equipment tab views only when Show activates them.
var weight = types.Single(t => t.FullName == "EFT.InventoryLogic.InventoryEquipment").Methods.Single(m => m.Name == "GetEquipmentWeight");
Check("Native equipment weight walks the built-in slot list, which ends before the appended slot",
    weight.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "GetAllSlots"));
Check("Insurance stash panels reuse EquipmentTab, whose views are created in Awake",
    types.Single(t => t.FullName == "EFT.UI.DragAndDrop.ComplexStashPanel").Fields.Any(f => f.FieldType.FullName == "EFT.UI.EquipmentTab")
    && tab.Methods.Single(m => m.Name == "Awake").Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && (i.Operand as FieldReference)?.Name == "_slotViews"));
using var plugin = AssemblyDefinition.ReadAssembly(Path.Combine(project, "src", "DualWieldLab.DualHolster", "bin", "Release", "DualWieldLab.DualHolster.dll"));
var pluginType = plugin.MainModule.Types.Single(t => t.Name == "DualHolsterPlugin");
string? PluginAttribute(string name) => pluginType.CustomAttributes.SingleOrDefault(a => a.AttributeType.FullName == name)?.ConstructorArguments[0].Value as string;
Check("Client plugin GUID is itsripper.dualwield.dualholster and it refuses to load next to its previous GUID",
    PluginAttribute("BepInEx.BepInPlugin") == "itsripper.dualwield.dualholster" && PluginAttribute("BepInEx.BepInIncompatibility") == "local.dualwieldlab.dualholster");
var clientAwake = pluginType.Methods.Single(m => m.Name == "Awake").Body.Instructions.ToList();
int AwakeIndex(Func<Instruction, bool> match) => clientAwake.FindIndex(i => match(i));
int clientExists = AwakeIndex(i => i.Operand is MethodReference m && m.Name == "Exists" && m.DeclaringType.FullName == "System.IO.File");
int lastBind = clientAwake.FindLastIndex(i => i.Operand is MethodReference m && m.Name == "Bind");
int clientMigrate = AwakeIndex(i => i.Operand is MethodReference m && m.Name == "Apply" && m.DeclaringType.Name == "ConfigMigration");
int versionCheck = AwakeIndex(i => i.Operand is MethodReference m && m.Name == "IsSupported" && m.DeclaringType.Name == "SupportedGame");
Check("Client Awake: config existence is read before binding, every setting is bound before the migration, and it migrates before the EFT build check can return",
    clientExists >= 0 && clientExists < lastBind && lastBind < clientMigrate && clientMigrate < versionCheck
    && pluginType.Fields.Any(f => f.Name == "LegacyConfigFile" && (string)f.Constant == "local.dualwieldlab.dualholster.cfg"));
var supported = plugin.MainModule.Types.Single(t => t.Name == "SupportedGame").Fields.Single(f => f.Name == "Build").Constant as string;
Check("The plugins accept the installed EFT build by its file version, so any SPT 4.1 release on it is supported",
    supported == System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(game, "EscapeFromTarkov.exe")).FileVersion);
// The shooting range re-clones and releases equipment through GetAllSlots; without the left
// holster there it kept the pistol from the moment the hideout loaded (stale offhand).
var getAllSlots = types.Single(t => t.FullName == "EFT.InventoryLogic.InventoryEquipmentExtension").Methods.Single(m => m.Name == "GetAllSlots");
var hideout = types.Single(t => t.FullName == "EFT.HideoutPlayer");
Check("Native GetAllSlots enumerates the built-in slot names, and the shooting range refreshes/releases through it",
    getAllSlots.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "AllSlotNames")
    && hideout.NestedTypes.SelectMany(n => n.Methods).Concat(hideout.Methods).Count(m => m.HasBody
        && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "GetAllSlots")) >= 2);
Check("Client plugin appends the left holster to GetAllSlots (weight, range, equipped checks) and activates an unbuilt tab before binding",
    pluginType.Methods.Any(m => m.Name == "AfterGetAllSlots") && !pluginType.Methods.Any(m => m.Name == "AfterEquipmentWeight")
    && pluginType.Methods.Single(m => m.Name == "Patch").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "GetAllSlots")
    && pluginType.Methods.Single(m => m.Name == "BeforeEquipmentTabShow").Parameters.Single(p => p.Name == "____slotViews").ParameterType.IsByReference);
// The pistol shortcut resolves only EquipmentSlot.Holster, and equipping requires the item in one
// of the built-in "reachable" weapon slots: a left-holster-only pistol could not be selected.
var player = types.Single(t => t.FullName == "EFT.Player");
var setSlotItem = player.Methods.Single(m => m.Name == "SetSlotItem");
var translator = types.Single(t => t.FullName == "EFT.PlayerInputTranslator");
var reachable = types.Single(t => t.FullName == "EFT.InventoryLogic.InventoryController").Methods.Single(m => m.Name == "IsAtReachablePlace");
Check("Native pistol selection calls SetSlotItem(Holster), and reachability lists only the built-in weapon slots",
    translator.NestedTypes.SelectMany(n => n.Methods).Concat(translator.Methods).Any(m => m.HasBody
        && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "SetSlotItem"))
    && setSlotItem.Parameters[0].ParameterType.FullName == "EFT.InventoryLogic.EquipmentSlot"
    && reachable.Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "BindAvailableSlotsExtended"));
Check("Client plugin redirects an empty-Holster pistol selection to the left holster and makes that slot reachable",
    pluginType.Methods.Single(m => m.Name == "BeforeSetSlotItem").Parameters.Single(p => p.Name == "equipmentSlot").ParameterType.IsByReference
    && pluginType.Methods.Any(m => m.Name == "AfterIsAtReachablePlace")
    && pluginType.Methods.Single(m => m.Name == "Patch").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "IsAtReachablePlace"));
// Character preview: both menu screens run MenuPlayerPoser; EFT places the in-hands weapon and
// solves both Final IK limbs in its LateUpdate, so the two-pistol stance is applied after it.
var poser = types.Single(t => t.Name == "MenuPlayerPoser");
var menu = plugin.MainModule.Types.Single(t => t.Name == "MenuDualPistols");
bool Calls(MethodDefinition m, string name) => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == name);
MethodDefinition MenuMethod(string name) => menu.Methods.Single(m => m.Name == name);
var loader = types.Single(t => t.Name == "PlayerModelLoader");
Check("Character previews pick the item in hands without HOLSTERLEFT; the plugin falls back to the left holster pistol like the pistol key",
    loader.Methods.Single(m => m.Name == "GetItemInHands").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "ItemInHands" || i.Operand is MethodReference r && r.Name == "get_ItemInHands")
    && pluginType.Methods.Single(m => m.Name == "AfterGetItemInHands").Parameters.Single(p => p.Name == "__result").ParameterType.IsByReference
    && pluginType.Methods.Single(m => m.Name == "Patch").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "GetItemInHands"));
Check("Native menu weapon creation activates the pooled model, initialises it and poses its animator",
    new[] { "CreateItem", "SetActive", "Init", "Play" }.All(n => Calls(loader.Methods.Single(m => m.Name == "CreateWeapon"), n))
    && Calls(loader.Methods.Single(m => m.Name == "Clear"), "ReturnToPool"));
Check("Native menu poser exposes the weapon change, LateUpdate and the limb IK the preview builds on",
    poser.Methods.Any(m => m.Name == "ChangeWeapon") && poser.Methods.Any(m => m.Name == "LateUpdate")
    && poser.Fields.Any(f => f.Name == "_markers") && (poser.Fields.Any(f => f.Name == "Limbs") || poser.Properties.Any(p => p.Name == "Limbs")));
Check("Two-pistol preview: real left item model, both pistols placed by their own grip marker after the native pose, both arms solved",
    Calls(MenuMethod("Attach"), "CreatePreviewWeapon") && Calls(MenuMethod("Attach"), "HasLeftHolster")
    // As PlayerModelLoader.CreateWeapon: pooled models come back inactive, and the animator is posed.
    && new[] { "CreateItem", "SetActive", "Init", "Play", "SetStockFolded", "SetMagInWeapon", "Update" }.All(n => Calls(MenuMethod("CreatePreviewWeapon"), n))
    && Calls(MenuMethod("Release"), "ReturnToPool") && !Calls(MenuMethod("Release"), "Destroy")
    && Calls(MenuMethod("AfterLateUpdate"), "StanceRoot") && Calls(MenuMethod("AfterLateUpdate"), "Place")
    && MenuMethod("AfterLateUpdate").Body.Instructions.Count(i => i.Operand is MethodReference r && r.Name == "Solve") == 2
    && Calls(MenuMethod("StanceRoot"), "BestAxis") && Calls(MenuMethod("AfterChangeWeapon"), "Release")
    && pluginType.Methods.Single(m => m.Name == "Awake").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "DualPistolCharacterPreview"));
// EFT's menu idle randomly starts weapon operations (reload, magazine/chamber checks) and gestures,
// and its HandPoser drives each hand's fingers from a grip pose: the preview must own both.
Check("Native menu randomizer starts weapon operations, and HandPoser fingers come from grip poses",
    Calls(poser.Methods.Single(m => m.Name == "OnRandomizeAnimation"), "TurnOffLights")
    && poser.Methods.Single(m => m.Name == "ChangeWeapon").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "TRIGGER_RELOAD")
    && types.Single(t => t.Name == "HandPoser").Methods.Single(m => m.Name == "GetFingerRotation").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "get_Item"));
Check("Dual preview suppresses random weapon operations, restores the native weapon pose before EFT's pass, and mirrors the left hand through bind poses",
    MenuMethod("BeforeRandomizeAnimation").ReturnType.FullName == "System.Boolean"
    && MenuMethod("BeforeLateUpdate").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "set_localPosition")
    && Calls(MenuMethod("AfterLateUpdate"), "MirrorPalm") && Calls(MenuMethod("AfterLateUpdate"), "ApplyFingers") && Calls(MenuMethod("AfterLateUpdate"), "Relax")
    && !menu.Fields.Any(f => f.Name is "FlipZ" or "FlipX")
    && plugin.MainModule.Types.Single(t => t.Name == "HandMirror").Methods.Single(m => m.Name == "Create").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "get_bindposes"));
Check("Preview cleanup: a failed bind returns the pooled left model, release restores the right pistol pose, and a left pistol leaving the slot ends the preview",
    Calls(MenuMethod("Attach"), "ReturnToPool") && Calls(MenuMethod("Attach"), "Bind")
    && MenuMethod("Release").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "set_localRotation")
    && Calls(MenuMethod("AfterLateUpdate"), "get_ContainedItem"));
// Quick slot 1: FastAccess binds Item1 to the Holster slot; the bound view shows whatever slot it
// is given and reacts to adds/removes in that slot only. The plugin swaps only the view's slot.
var boundView = types.Single(t => t.FullName == "EFT.UI.DragAndDrop.BoundSlotView");
var quickView = types.Single(t => t.FullName == "EFT.UI.DragAndDrop.QuickSlotView");
var quick = plugin.MainModule.Types.Single(t => t.Name == "QuickSlotPistol");
Check("Quick slot 1 is bound to Holster; the view API the left-holster display relies on exists",
    Calls(types.Single(t => t.FullName == "EFT.InventoryLogic.FastAccess").Methods.Single(m => m.IsConstructor && !m.IsStatic), "GetSlot")
    && boundView.Methods.Any(m => m.Name == "Show" && m.Parameters.Count == 3 && m.Parameters[0].ParameterType.Name == "Slot")
    && boundView.Methods.Any(m => m.Name.EndsWith("OnItemAdded")) && boundView.Methods.Any(m => m.Name.EndsWith("OnItemRemoved"))
    && boundView.Methods.Any(m => m.Name == "GetSlot") && quickView.Methods.Any(m => m.Name == "RemoveItemView" && m.IsPublic)
    && quickView.Fields.Any(f => f.Name == "InventoryController") && quickView.Fields.Any(f => f.Name == "ItemUiContext"));
Check("Quick-slot display: only the Item1 view's slot is swapped, never FastAccess bindings or items",
    quick.Methods.Single(m => m.Name == "BeforeShow").Parameters.Single(p => p.Name == "slot").ParameterType.IsByReference
    && !quick.Methods.Any(m => m.HasBody && m.Body.Instructions.Any(i => i.Operand is MethodReference r
        && (r.DeclaringType.Name == "FastAccess" || r.Name is "Move" or "TryRunNetworkTransaction" or "RunNetworkTransaction" or "AddItem" or "RemoveItem")))
    && pluginType.Methods.Single(m => m.Name == "Patch").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "Patch" && r.DeclaringType.Name == "QuickSlotPistol"));
Check("Quick equip (Alt+Click) lists only Holster for pistols; the plugin offers an empty left holster when the native search finds none",
    types.Single(t => t.FullName == "EFT.InventoryLogic.InventoryExtension").Methods.Any(m => m.Name == "FindSlotToPickUp" && m.Parameters.Count == 2 && m.Parameters[0].ParameterType.Name == "InventoryEquipment")
    && pluginType.Methods.Single(m => m.Name == "AfterFindSlotToPickUp").Parameters.Single(p => p.Name == "__result").ParameterType.IsByReference
    && Calls(pluginType.Methods.Single(m => m.Name == "AfterFindSlotToPickUp"), "FindLocationForItem")
    && pluginType.Methods.Single(m => m.Name == "Patch").Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "FindSlotToPickUp"));

var report = new { executedAtUtc = DateTime.UtcNow, checks = results.Count, failures, otherSlotParsers = parsers,
    scope = "Installed server code (template model, JsonUtil, native death predicate with the mod patch, container search) and installed client metadata on synthetic data. No server start, profile file, raid, client UI or game.", results };
string resultsDir = Path.Combine(project, "artifacts", "test-results");
Directory.CreateDirectory(resultsDir);
File.WriteAllText(Path.Combine(resultsDir, "dual-holster-checks.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(results.Count - failures + "/" + results.Count + " dual-holster checks passed.");
return failures == 0 ? 0 : 1;

internal sealed class DeathProbe : InRaidHelper
{
    internal DeathProbe(LostOnDeathConfig config) : base(null!, null!, null!, null!, config, null!) { }
    internal bool Kept(PmcData pmc, Item item) => IsItemKeptAfterDeath(pmc, item);
}
