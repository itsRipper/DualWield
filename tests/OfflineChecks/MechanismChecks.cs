using System.Text.Json;
using DualWieldLab;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Candidate rule and mechanism classification over every weapon the installed holster accepts,
// plus synthetic clones. Template ids appear only as expected outcomes.
internal static class MechanismChecks
{
    private const string WeaponNode = "5422acb9af1c889c16000029";
    private const string PistolNode = "5447b5cf4bdc2d65278b4567";
    private const string RevolverNode = "617f1ef5e8b54b0998387733";
    private const string GrenadeLauncherNode = "5447bedf4bdc2d87278b4568";
    private const string CylinderMagazineNode = "610720f290b75a49ff2e5e25";
    private const string DefaultInventory = "55d7217a4bdc2d86028b456d";

    private sealed record Template(string Id, string Name, HandlingClass Handling, FirearmMechanism Mechanism);

    internal static void Run(Action<string, bool> check, string game, ModuleDefinition native, ModuleDefinition plugin)
    {
        using var database = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "SPT_Runtime", "SPT_Data", "database", "templates", "items.json")));
        JsonElement items = database.RootElement;
        bool Descends(string id, string node)
        {
            for (string? current = id; !string.IsNullOrEmpty(current); current = items.TryGetProperty(current, out var t) ? t.GetProperty("_parent").GetString() : null)
                if (current == node) return true;
            return false;
        }

        var holsterFilter = items.GetProperty(DefaultInventory).GetProperty("_props").GetProperty("Slots").EnumerateArray()
            .Single(s => s.GetProperty("_name").GetString() == "Holster").GetProperty("_props").GetProperty("filters")[0]
            .GetProperty("Filter").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var holsterable = items.EnumerateObject()
            .Where(i => i.Value.GetProperty("_type").GetString() == "Item" && Descends(i.Name, WeaponNode) && holsterFilter.Any(f => Descends(i.Name, f)))
            .Select(i => Classify(i.Name, i.Value, items, Descends)).ToArray();

        Template[] magazineFed = holsterable.Where(t => t.Handling == HandlingClass.Pistol && t.Mechanism == FirearmMechanism.MagazineFed).ToArray();
        check("Every installed holster pistol under EFT's Pistol node except the PD-20 toy is magazine-fed (" + magazineFed.Length + ")",
            magazineFed.Length >= 25 && holsterable.Where(t => Descends(t.Id, PistolNode) && t.Id != "66015072e9f84d5680039678")
                .All(t => t.Handling == HandlingClass.Pistol && t.Mechanism == FirearmMechanism.MagazineFed));
        check("PD-20 is a pistol with an internal magazine (stretch goal, refused with that reason)",
            holsterable.Single(t => t.Id == "66015072e9f84d5680039678") is { Handling: HandlingClass.Pistol, Mechanism: FirearmMechanism.InternalMagazine });
        Template[] revolvers = holsterable.Where(t => Descends(t.Id, RevolverNode)).ToArray();
        check("Installed revolvers are pistol candidates with a cylinder mechanism (" + revolvers.Length + ")",
            revolvers.Length >= 3 && revolvers.All(t => t.Handling == HandlingClass.Pistol && t.Mechanism == FirearmMechanism.Cylinder));
        check("SP-81 is held as a pistol and refused as a flare pistol",
            holsterable.Single(t => t.Id == "620109578d82e67e7911abf2") is { Handling: HandlingClass.Pistol, Mechanism: FirearmMechanism.Flare });
        check("MR-43 sawn-off fits the holster but is not held as a pistol (out of scope)",
            holsterable.Single(t => t.Id == "64748cb8de82c85eaf0a273a").Handling == HandlingClass.Other);
        check("Every holster weapon resolves to a named outcome, none inconsistent",
            holsterable.All(t => t.Mechanism != FirearmMechanism.Inconsistent));

        // Modded pistols clone a vanilla one under the Pistol node and override props.
        var glock = items.GetProperty("5a7ae0c351dfba0017554310");
        check("A modded clone under the Pistol node is a magazine-fed candidate whatever its weapClass",
            HandlingPolicy.Classify(true, false, "smg", false) == HandlingClass.Pistol
            && Classify("modded-clone", glock, items, (id, node) => node == PistolNode || node == WeaponNode || Descends(id, node)).Mechanism == FirearmMechanism.MagazineFed);
        check("A modded revolver of another weapon class is not held as a pistol", HandlingPolicy.Classify(false, true, "shotgun", false) == HandlingClass.Other);
        check("A modded revolver of weapClass pistol is a candidate", HandlingPolicy.Classify(false, true, "pistol", false) == HandlingClass.Pistol);
        check("A non-flare grenade launcher is not a candidate", HandlingPolicy.Classify(false, false, "grenadeLauncher", false) == HandlingClass.Other);
        check("EFT's combined reload mode: internal weapon + external magazine",
            FirearmMechanismPolicy.CombineReload(ReloadKind.InternalMagazine, ReloadKind.ExternalMagazine) == ReloadKind.ExternalMagazineWithInternalReloadSupport
            && FirearmMechanismPolicy.CombineReload(ReloadKind.ExternalMagazine, ReloadKind.InternalMagazine) == ReloadKind.Inconsistent
            && FirearmMechanismPolicy.CombineReload(ReloadKind.ExternalMagazine, null) == ReloadKind.ExternalMagazine);
        check("Mechanism precedence follows EFT's fire-operation factory (flare, one-off, barrel, revolver, bolt)",
            FirearmMechanismPolicy.Classify(true, true, true, true, ReloadKind.OnlyBarrel, 2, MagazineKind.Cylinder) == FirearmMechanism.Flare
            && FirearmMechanismPolicy.Classify(true, false, true, true, ReloadKind.OnlyBarrel, 2, MagazineKind.Cylinder) == FirearmMechanism.OneOff
            && FirearmMechanismPolicy.Classify(true, false, false, true, ReloadKind.OnlyBarrel, 2, MagazineKind.Cylinder) == FirearmMechanism.BarrelOnly
            && FirearmMechanismPolicy.Classify(true, false, false, true, ReloadKind.InternalMagazine, 0, MagazineKind.Cylinder) == FirearmMechanism.Cylinder
            && FirearmMechanismPolicy.Classify(false, false, false, true, ReloadKind.ExternalMagazine, 1, MagazineKind.Box) == FirearmMechanism.BoltAction
            && FirearmMechanismPolicy.Classify(false, false, false, false, ReloadKind.ExternalMagazine, 2, MagazineKind.Box) == FirearmMechanism.MultiChamber
            && FirearmMechanismPolicy.Classify(false, false, false, false, ReloadKind.ExternalMagazine, 1, MagazineKind.None) == FirearmMechanism.MagazineFed);

        // The handling mirror must keep matching the installed EFT rule.
        var animationType = AllTypes(native.Types).Single(t => t.FullName == "EFT.Player").Methods.Single(m => m.Name == "GetWeaponAnimationType");
        var instructions = animationType.Body.Instructions;
        string[] typeTests = instructions.Where(i => i.OpCode == OpCodes.Isinst).Select(i => ((TypeReference)i.Operand).Name).ToArray();
        check("EFT's GetWeaponAnimationType still tests exactly Pistol, rangefinder, radio, Revolver, GrenadeLauncher (and its known non-pistol kinds)",
            new[] { "Pistol", "PortableRangeFinder", "RadioTransmitter", "Revolver", "GrenadeLauncher" }.All(typeTests.Contains)
            && instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == HandlingPolicy.PistolWeaponClass)
            && instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_IsFlareGun"));

        // One candidate rule: no runtime path tests the Pistol class directly.
        var pistolTests = AllTypes(plugin.Types).SelectMany(t => t.Methods).Where(m => m.HasBody
            && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Isinst && i.Operand is TypeReference r && r.FullName == "EFT.InventoryLogic.Pistol"))
            .Select(m => m.DeclaringType.Name + "." + m.Name).ToArray();
        check("Only WeaponCompatibility.GetHandling tests the Pistol class (" + string.Join(",", pistolTests) + ")",
            pistolTests.Length == 1 && pistolTests[0] == "WeaponCompatibility.GetHandling");
    }

    private static Template Classify(string id, JsonElement item, JsonElement items, Func<string, string, bool> descends)
    {
        var props = item.GetProperty("_props");
        bool revolver = descends(id, RevolverNode);
        bool flare = descends(id, GrenadeLauncherNode) && props.TryGetProperty("IsFlareGun", out var f) && f.GetBoolean();
        HandlingClass handling = HandlingPolicy.Classify(descends(id, PistolNode), revolver, props.GetProperty("weapClass").GetString(), flare);
        MagazineKind magazine = MagazineKind.None;
        ReloadKind? magazineReload = null;
        var magazineSlot = props.GetProperty("Slots").EnumerateArray().FirstOrDefault(s => s.GetProperty("_name").GetString() == "mod_magazine");
        if (magazineSlot.ValueKind == JsonValueKind.Object)
        {
            string magazineId = magazineSlot.GetProperty("_props").GetProperty("filters")[0].GetProperty("Filter")[0].GetString()!;
            magazine = descends(magazineId, CylinderMagazineNode) ? MagazineKind.Cylinder : MagazineKind.Box;
            magazineReload = Enum.Parse<ReloadKind>(items.GetProperty(magazineId).GetProperty("_props").GetProperty("ReloadMagType").GetString()!);
        }
        ReloadKind reload = FirearmMechanismPolicy.CombineReload(Enum.Parse<ReloadKind>(props.GetProperty("ReloadMode").GetString()!), magazineReload);
        FirearmMechanism mechanism = FirearmMechanismPolicy.Classify(revolver, props.TryGetProperty("IsFlareGun", out var fg) && fg.GetBoolean(),
            props.TryGetProperty("IsOneoff", out var o) && o.GetBoolean(), props.TryGetProperty("BoltAction", out var b) && b.GetBoolean(),
            reload, props.GetProperty("Chambers").GetArrayLength(), magazine);
        return new Template(id, item.GetProperty("_name").GetString()!, handling, mechanism);
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
}
