using System.Security.Cryptography;
using System.Text.Json;
using DualWieldLab;
using Mono.Cecil;

string project = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("DualWieldLab");
string game = args.Length > 1 ? args[1] : throw new ArgumentException("Supply the local SPT installation path as the second argument.");
var results = new List<object>();
int failures = 0;
void Check(string name, bool condition)
{
    results.Add(new { name, passed = condition });
    Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
    if (!condition) failures++;
}
IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] {t}.Concat(AllTypes(t.NestedTypes)));
IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
bool CallsName(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);

string template = "5a7ae0c351dfba0017554310"; // Baseline regression fixture, never a runtime whitelist.
Check("Distinct existing Glock pair accepted", PairPolicy.IsDistinctPair("a", "b", template, template));
Check("Same identity refused", !PairPolicy.IsDistinctPair("a", "a", template, template));
Check("Empty identity refused", !PairPolicy.IsDistinctPair("", "b", template, template));
Check("Different pistol templates preserve distinct pair identity", PairPolicy.IsDistinctPair("a", "b", template, "other-native-pistol"));
Check("Missing second template refused", !PairPolicy.IsDistinctPair("a", "b", template, ""));
Check("Case-sensitive identity", PairPolicy.IsDistinctPair("a", "A", template, template));
bool conservation = true;
for (int p = 0; p <= 50; p++) for (int o = 1; o <= 50; o++)
{
    conservation &= PairPolicy.IsValidShotDelta(p, p, o, o - 1);
    conservation &= !PairPolicy.IsValidShotDelta(p, p - 1, o, o - 1);
    conservation &= !PairPolicy.IsValidShotDelta(p, p, o, o);
    conservation &= !PairPolicy.IsValidShotDelta(p, p, o, o - 2);
    conservation &= !PairPolicy.IsValidShotDelta(p, p, o, o + 1);
}
Check("Ammo conservation: 12,750 valid/invalid combinations", conservation);
Check("Dry state never authorizes an emitted shot", !PairPolicy.IsValidShotDelta(1,1,0,-1));
Check("Negative state refused", !PairPolicy.IsValidShotDelta(-1,-1,1,0));
Check("Native cyclic rate interval", Math.Abs(PairPolicy.ShotInterval(400) - .15) < .000001);
Check("Native invalid-rate fallback is 240 RPM", PairPolicy.ShotInterval(0) == .25);
Check("Glock semiauto interval is 133.333ms", Math.Abs(PairPolicy.ShotInterval(450) - 2d/15d) < 1e-12);
double deadline = PairPolicy.NextShotTime(10d, 450);
Check("Request before deadline refused", !PairPolicy.IsCooldownReady(deadline - 1e-8, deadline));
Check("Request exactly at deadline accepted", PairPolicy.IsCooldownReady(deadline, deadline));
Check("Pause cannot advance cooldown", !PairPolicy.IsCooldownReady(10d, deadline));
Check("Slow motion gates by scaled simulation time", !PairPolicy.IsCooldownReady(10d + .2d * .5d, deadline) && PairPolicy.IsCooldownReady(10d + .3d * .5d, deadline));
var rates = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(game,"SPT_Runtime","SPT_Data","database","templates","items.json"))).RootElement.GetProperty(template).GetProperty("_props");
int configuredSingleRate = rates.GetProperty("SingleFireRate").GetInt32();
int oldWrongRate = rates.GetProperty("bFirerate").GetInt32();
Check("Installed Glock rates: the cooldown follows SingleFireRate (450), not bFirerate (30, 15x slower)", configuredSingleRate==450 && oldWrongRate==30 && Math.Abs((60d/oldWrongRate)/PairPolicy.ShotInterval(configuredSingleRate)-15d)<1e-9);
int scheduled = 0;
double nextAllowed = 0;
for (int ms=0;ms<2000;ms++)
{
    double now = ms/1000d;
    if (!PairPolicy.IsCooldownReady(now,nextAllowed)) continue;
    scheduled++;
    nextAllowed = PairPolicy.NextShotTime(now,configuredSingleRate);
}
Check("2000 synthetic 1ms requests respect native 450 RPM cap", scheduled==15);
GenericChecks.Run(Check, game);
AnimationChecks.Run(Check);

string target = Path.Combine(game, "EscapeFromTarkov_Data", "Managed", "Assembly-CSharp.dll");
using var native = AssemblyDefinition.ReadAssembly(target);
Check("Exact inspected assembly MVID", native.MainModule.Mvid == Guid.Parse("cc2d80b0-6d5b-4cb1-a581-6d2cc901d4c7"));
Check("Exact inspected assembly SHA256", Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target))).Equals("ee25cee1259777b38ed8b3e7841fdc2db3c98540b1469fa539b1ff183476e436", StringComparison.OrdinalIgnoreCase));
var nt = AllTypes(native.MainModule.Types).ToDictionary(t => t.FullName);
var firearm = nt["EFT.Player/FirearmController"];
var operation = nt["EFT.Player/FirearmController/FireOperationBase"];
MethodDefinition NM(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
Check("Native ammo operation removes real chamber item", CallsName(NM(operation,"ProcessInventoryOperationsForSingleShot"), "RemoveItem"));
Check("Native ammo operation feeds real offhand chamber", CallsName(NM(operation,"ProcessInventoryOperationsForSingleShot"), "PopTo"));
Check("Native shot applies weapon wear and heat", CallsName(NM(firearm,"Shot"), "OnShot"));
Check("Native shot invokes own audio", CallsName(NM(firearm,"Shot"), "PlayFireBulletSound"));
Check("Native shot invokes real ballistic path", CallsName(NM(firearm,"Shot"), "InitiateShot") && CallsName(NM(firearm,"InitiateShot"), "Shoot"));
Check("Native ballistic path invokes own model effects", CallsName(NM(firearm,"InitiateShot"), "PlayShotEffects"));
Check("Native semiauto fire operation uses SingleFireRate", CallsName(NM(nt["EFT.Player/FirearmController/FireOperation"],"Start"),"get_SingleFireRate"));
Check("Native client collision calls real ApplyHit", CallsName(NM(nt["EFT.ClientGameWorld"],"ShotDelegate"),"ApplyHit"));
Check("Native body collider applies real player shot", CallsName(NM(nt["BodyPartCollider"],"ApplyHit"),"ApplyShot"));
Check("Native Player.ApplyShot invokes damage handling", CallsName(NM(nt["EFT.Player"],"ApplyShot"),"ApplyDamageInfo"));
Check("Native damage reaches ActiveHealthController.ApplyDamage", CallsName(NM(nt["EFT.Player"],"ApplyDamageInfo"),"ApplyDamage"));
Check("Native Pistol and Revolver have distinct direct types", nt["EFT.InventoryLogic.Pistol"].BaseType.FullName=="EFT.InventoryLogic.Weapon" && nt["EFT.InventoryLogic.Revolver"].BaseType.FullName=="EFT.InventoryLogic.Weapon");

string output = Path.Combine(project, "src", "DualWieldLab", "bin", "Release", "DualWieldLab.dll");
using var plugin = AssemblyDefinition.ReadAssembly(output);
var pt = AllTypes(plugin.MainModule.Types).ToDictionary(t => t.FullName);
var context = pt["DualWieldLab.OffhandShotContext"];
var runtime = pt["DualWieldLab.RuntimePlugin"];
MethodDefinition PM(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
Check("Offhand calls native ammo and native Shot", CallsName(PM(context,"FireOnce"), "ProcessInventoryOperationsForSingleshotWithMalf") && CallsName(PM(context,"FireOnce"), "Shot"));
Check("Offhand initializes no native global hands setup", !Calls(PM(context,"Initialize")).Any(m => new[] {"Setup", "CreateController", "CreateControllerAsync", "Spawn", "SetArmsAnimatorCommon", "InitWeaponData", "InitTransforms", "InitStreamingAnimators", "set_HandsController"}.Contains(m.Name)));
Check("Firing never swaps a controller item", !PM(context,"FireOnce").Body.Instructions.Any(i=>i.OpCode.Name=="stfld" && i.Operand is FieldReference f && f.Name=="_item"));
Check("Shadow destroy bypasses native Destroy", !Calls(PM(context,"Destroy")).Any(m=>m.DeclaringType.FullName == "EFT.Player/FirearmController" && m.Name == "Destroy"));
Check("Shadow has no automatic native update operations", Calls(PM(context,"ManualUpdate")).Count()==0 && Calls(PM(context,"ManualLateUpdate")).Count()==0);
Check("Audio shuts down synchronously before model return", CallsName(PM(context,"DisposeContext"),"Shutdown"));
Check("Pooled model returned by unpair", CallsName(PM(runtime,"Unpair"),"ReturnToPool"));
Check("Pair-time network guard", CallsName(PM(runtime,"TryPair"),"HasFika"));
Check("Shared rate adapter reads native single and automatic getters", CallsName(PM(runtime,"TryFire"),"GetShotRate") && CallsName(PM(pt["DualWieldLab.WeaponCompatibility"],"GetShotRate"),"get_SingleFireRate") && CallsName(PM(pt["DualWieldLab.WeaponCompatibility"],"GetShotRate"),"get_FireRate"));
Check("No inventory-tree traversal while shooting", !CallsName(PM(runtime,"TryFire"),"GetAllItemsNonAlloc"));
Check("Shot callback submits no duplicate projectile", !Calls(PM(context,"RegisterShot")).Any(m=>m.Name=="Shoot" || m.Name=="InitiateShot" || m.Name=="Shot"));
Check("Native projectile count and weapon attribution are inspected", PM(context,"RegisterShot").Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="Weapon" && f.DeclaringType.Name=="Shot") && PM(context,"RegisterShot").Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="_projectileCount"));
Check("Paired ADS interceptors never invoke native aim", !CallsName(PM(runtime,"BeforeSetAim"),"SetAim") && !CallsName(PM(runtime,"BeforeToggleAim"),"ToggleAim"));
Check("ADS alone does not unpair", !CallsName(PM(runtime,"PairedStopReason"),"get_IsAiming"));
Check("Pose override bracketed around native evaluation", CallsName(PM(runtime,"BeforeVisualPass"),"BeforeNativeFrame") && CallsName(PM(runtime,"BeforeNativeFrame"),"BeforeNativePose") && CallsName(PM(runtime,"AfterVisualPass"),"CaptureNativePose"));
Check("Actual hand renderer and finger binding resolved at pairing", CallsName(PM(pt["DualWieldLab.NativeArmRetarget"],"Create"),"GetRenderers") && CallsName(PM(pt["DualWieldLab.NativeArmRetarget"],"Create"),"get_bones"));
Check("Hands restored before model pooling", CallsName(PM(runtime,"Unpair"),"Restore"));
Check("World/session reference cleanup present", CallsName(PM(runtime,"EndSession"),"Clear") && CallsName(PM(runtime,"EndSession"),"Unpair"));
Check("Switching preserves per-weapon cooldown", !PM(runtime,"Unpair").Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="_cooldowns"));
Check("Damage diagnostics use bounded arrays", pt["DualWieldLab.DamageDiagnostics"].Fields.Count(f=>f.FieldType is ArrayType)==2);
Check("No shot-path logger I/O", !Calls(PM(runtime,"TryFire")).Any(m=>m.Name=="LogInfo") && !Calls(PM(context,"FireOnce")).Any(m=>m.Name.StartsWith("Log")));
Check("No file, profile, server, HTTP, clone or weapon factory writes outside the configuration migration", !pt.Values.Where(t=>t.FullName!="DualWieldLab.ConfigMigration").SelectMany(t=>t.Methods).SelectMany(Calls).Any(m=>(m.DeclaringType.FullName.StartsWith("System.IO.File") && m.Name!="Exists") || m.DeclaringType.FullName.StartsWith("System.Net") || m.DeclaringType.FullName=="EFT.ItemFactory" || m.Name=="set_HandsController"));
string[] frameMethods = ["Update", "LateUpdate", "BeforeVisualPass", "AfterVisualPass", "AlignModel"];
Check("No scene or inventory searches in frame methods", !runtime.Methods.Where(m=>frameMethods.Contains(m.Name)).SelectMany(Calls).Any(m=>m.Name.StartsWith("FindObject") || m.Name=="GetAllItemsNonAlloc" || m.Name=="GetComponentsInChildren" || m.Name=="GetComponent"));
Check("Own DLL only in plugin build output", Directory.GetFiles(Path.GetDirectoryName(output)!,"*.dll").Length==1);
Check("No hierarchy discovery in hand-pose hot paths", !pt["DualWieldLab.OffhandHandPose"].Methods.Where(m=>new[]{"Apply","BeforeNativePose","SolveArm"}.Contains(m.Name)).SelectMany(Calls).Any(m=>m.Name=="GetComponentsInChildren" || m.Name=="get_bones" || m.DeclaringType.Namespace=="System.Reflection"));
Check("No runtime template whitelist remains", !pt["DualWieldLab.WeaponCompatibility"].Methods.SelectMany(m=>m.HasBody?m.Body.Instructions:[]).Any(i=>i.Operand is string s && s==template) && !pt["DualWieldLab.PairPolicy"].Fields.Any(f=>f.Name.Contains("Template")));
Check("Live mode checked before native ammo mutation", CallsName(PM(context,"FireOnce"),"IsSupportedMode") && CallsName(PM(runtime,"PairedStopReason"),"IsSupportedMode"));
Check("Native trigger release emits sound-loop break", CallsName(PM(context,"FireOnce"),"set_IsTriggerPressed"));
Check("Same shot implementation serves every eligible pistol", pt.Values.Count(t=>t.BaseType?.FullName=="EFT.Player/FirearmController")==1);
Check("Semi-auto input remains edge-only", CallsName(PM(runtime,"Update"),"IsDown") && !CallsName(PM(runtime,"Update"),"IsPressed"));
Check("Reload service only selects existing owned items", !pt["DualWieldLab.ReloadCoordinator"].Methods.SelectMany(Calls).Any(m=>new[]{"Move","RemoveItem","PopTo","ReloadMag","CreateController","SetItemInHands"}.Contains(m.Name)));
Check("Lowered/drawing offhand is gated before firing", CallsName(PM(runtime,"TryFire"),"get_IsSprintEnabled") && CallsName(PM(runtime,"TryFire"),"get_EnterBlend") && CallsName(PM(runtime,"TryFire"),"get_LowerBlend"));
Check("No full compatibility hierarchy scan in frame or shot paths", !Calls(PM(runtime,"TryFire")).Concat(Calls(PM(context,"FireOnce"))).Concat(Calls(PM(runtime,"PairedStopReason"))).Any(m=>m.Name=="GetFacts" || m.Name=="GetRefusal"));
Check("An empty left chamber with a loaded magazine is fed by the native feed transaction before firing, never by item moves",
    CallsName(PM(runtime,"TryFire"),"OffhandNeedsChamber") && CallsName(PM(runtime,"TryFire"),"ChamberOffhand")
    && pt.Values.Where(t=>t.Name.Contains("ChamberOffhand")).SelectMany(t=>t.Methods).SelectMany(Calls).Any(m=>m.Name=="ApplyToAddress")
    && !pt.Values.Where(t=>t.Name.Contains("ChamberOffhand")).SelectMany(t=>t.Methods).SelectMany(Calls).Any(m=>m.Name is "Move" or "RemoveItem" or "PopTo"));
Check("The left trigger finger keeps its own grip instead of the right trigger pull",
    pt["DualWieldLab.NativeArmRetarget"].Fields.Any(f=>f.Name=="TriggerFingerIndex" && f.HasConstant && (int)f.Constant==1));
VisualInputChecks.Run(Check,native,plugin);
AutomaticChecks.Run(Check,game,native,plugin);
InventoryClientChecks.Run(Check,game,native);
FireModeChecks.Run(Check,game,native,plugin);
ReloadCallbackChecks.Run(Check,native,plugin);
RecoilAnimationChecks.Run(Check);
ShortcutChecks.Run(Check,plugin);
ArmRetargetChecks.Run(Check,native,plugin);
LocomotionPresentationChecks.Run(Check,native,plugin);
ReloadHandoverChecks.Run(Check,native,plugin);
MalfunctionSamplingChecks.Run(Check,native,plugin);
LookSpaceChecks.Run(Check,native,plugin);
ReloadProofChecks.Run(Check,native,plugin);
ReloadContactChecks.Run(Check,plugin);
MagazineCompatibilityChecks.Run(Check,game,native,plugin);
LocomotionChecks.Run(Check,native,plugin);
ReloadRestorationChecks.Run(Check,plugin);
ShootingRangeChecks.Run(Check,native,plugin);
SelectorPoseChecks.Run(Check,native,plugin);
PoseRecoveryChecks.Run(Check,native,plugin);
HolsterLifecycleChecks.Run(Check,native,plugin);
SlideMechanicsChecks.Run(Check,native,plugin);
EquipLifecycleChecks.Run(Check,native,plugin);
ReloadServiceChecks.Run(Check,native,plugin);
CosmeticActionChecks.Run(Check,plugin);
PluginIdentityChecks.Run(Check,plugin);
LeftArmIsolationChecks.Run(Check,plugin);
AmmunitionConservationChecks.Run(Check,native,plugin);
ReloadClipChecks.Run(Check,project,plugin);
ReloadSoundChecks.Run(Check,project,plugin);
WeaponCheckRoutingChecks.Run(Check,project,native,plugin);
MalfunctionHandlingChecks.Run(Check,project,native,plugin);
LeftModifierChecks.Run(Check,native,plugin);
SuppressorAndGripChecks.Run(Check,native,plugin);

string resultsDir = Path.Combine(project,"artifacts","test-results");
Directory.CreateDirectory(resultsDir);
File.WriteAllText(Path.Combine(resultsDir,"offline-checks.json"),JsonSerializer.Serialize(new {
    executedAtUtc=DateTime.UtcNow, target, output,
    outputSha256=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))).ToLowerInvariant(), checks=results.Count, failures,
    limitation="Static metadata and pure policy checks only. No Unity/EFT methods, Harmony patches, game, profile or raid executed.", results
},new JsonSerializerOptions {WriteIndented=true}));
Console.WriteLine($"{results.Count - failures}/{results.Count} checks passed (static contract checks; gameplay is verified in game).");
return failures==0 ? 0 : 1;
