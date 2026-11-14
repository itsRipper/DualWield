using System.Text.Json;
using DualWieldLab;
using Mono.Cecil;

// Fixture: installed 12g/20g rounds stand in for a multi-projectile pistol round. The left shot
// expects one native registration per pellet.
internal static class BuckshotChecks
{
    private const string PistolNode = "5447b5cf4bdc2d65278b4567";

    internal static void Run(Action<string, bool> check, string game, ModuleDefinition native)
    {
        using var database = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "SPT_Runtime", "SPT_Data", "database", "templates", "items.json")));
        JsonElement items = database.RootElement;
        int Number(JsonElement props, string name) => props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 1;
        float Speed(JsonElement props) => props.TryGetProperty("InitialSpeed", out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : 0f;

        var buckshot = items.EnumerateObject()
            .Where(i => i.Value.GetProperty("_type").GetString() == "Item" && i.Value.TryGetProperty("_props", out var p)
                && p.TryGetProperty("ammoType", out var type) && type.GetString() == "buckshot" && Number(p, "ProjectileCount") > 1)
            .Select(i => (Name: i.Value.GetProperty("_name").GetString()!, Count: Number(i.Value.GetProperty("_props"), "ProjectileCount"), Speed: Speed(i.Value.GetProperty("_props"))))
            .ToList();
        check("Fixture: the installed data has buckshot rounds to stand in for a modded buckshot pistol", buckshot.Count >= 5);
        check("Each buckshot round is live and expects one registered shot per pellet",
            buckshot.All(b => ShotAccounting.ExpectedProjectiles(b.Count, b.Speed) == b.Count));
        check("A buckshot shot is complete only when every pellet registered (one missing pellet faults the pair, no retry)",
            buckshot.All(b => ShotAccounting.Complete(b.Count, b.Count) && !ShotAccounting.Complete(b.Count, b.Count - 1) && !ShotAccounting.Complete(b.Count, b.Count + 1)));
        check("A round without a projectile or muzzle velocity is refused before mutation",
            ShotAccounting.ExpectedProjectiles(0, 300f) == 0 && ShotAccounting.ExpectedProjectiles(8, 0f) == 0 && !ShotAccounting.Complete(0, 0));
        check("A single bullet expects exactly one registration", ShotAccounting.ExpectedProjectiles(1, 350f) == 1 && ShotAccounting.Complete(1, 1));

        var nativeTypes = AllTypes(native.Types).ToDictionary(t => t.FullName);
        var controller = nativeTypes["EFT.Player/FirearmController"];
        var initiate = controller.Methods.Single(m => m.Name == "InitiateShot");
        var calls = initiate.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().ToList();
        check("EFT's InitiateShot shoots multi-projectile rounds per pellet and registers each pellet",
            calls.Any(c => c.Name.EndsWith("ShotMultiProjectileShot")) && calls.Count(c => c.Name == "RegisterShot") >= 2);
        var create = nativeTypes["EFT.Ballistics.BallisticsCalculator"].Methods.Single(m => m.Name == "CreateMultiProjectileShot");
        check("EFT creates one shot per ProjectileCount of the round",
            create.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_ProjectileCount"));
        check("EFT's pellet buffer is static, so the left context (built without native controller setup) has it",
            controller.Fields.Single(f => f.Name == "_preallocatedShots").IsStatic);

        // Information only.
        bool Descends(string id)
        {
            for (string? current = id; !string.IsNullOrEmpty(current); current = items.TryGetProperty(current, out var t) ? t.GetProperty("_parent").GetString() : null)
                if (current == PistolNode) return true;
            return false;
        }
        var pelletRounds = items.EnumerateObject().Where(i => i.Value.TryGetProperty("_props", out var p) && Number(p, "ProjectileCount") > 1).Select(i => i.Name).ToHashSet();
        int pistolsWithBuckshot = items.EnumerateObject().Count(i => i.Value.GetProperty("_type").GetString() == "Item" && Descends(i.Value.GetProperty("_parent").GetString()!)
            && i.Value.GetProperty("_props").TryGetProperty("Chambers", out var chambers)
            && chambers.EnumerateArray().Any(c => c.GetProperty("_props").GetProperty("filters").EnumerateArray()
                .Any(f => f.GetProperty("Filter").EnumerateArray().Any(a => pelletRounds.Contains(a.GetString()!)))));
        Console.WriteLine("INFO installed pistols chambering multi-projectile rounds: " + pistolsWithBuckshot + " (modded buckshot test pending)");
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
}
