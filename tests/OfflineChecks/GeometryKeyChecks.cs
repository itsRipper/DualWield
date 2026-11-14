using DualWieldLab;
using Mono.Cecil;

// Pose caches follow the assembled build: a different grip or sight is sampled afresh, a reload is not.
internal static class GeometryKeyChecks
{
    internal static void Run(Action<string, bool> check, ModuleDefinition plugin)
    {
        const string glock = "5a7ae0c351dfba0017554310";
        string[] stock = { "mod_pistol_grip=grip_std", "mod_reciever=slide_std", "mod_sight_rear=rear_std" };
        string key = GeometryKey.Compose(glock, stock);
        check("The same build always has the same key", key == GeometryKey.Compose(glock, (string[])stock.Clone()));
        check("Another grip on the same pistol is another geometry",
            key != GeometryKey.Compose(glock, new[] { "mod_pistol_grip=grip_custom", "mod_reciever=slide_std", "mod_sight_rear=rear_std" }));
        check("Another sight on the same pistol is another geometry",
            key != GeometryKey.Compose(glock, new[] { "mod_pistol_grip=grip_std", "mod_reciever=slide_std", "mod_sight_rear=rear_tall" }));
        check("An added attachment (light) is another geometry", key != GeometryKey.Compose(glock, stock.Append("mod_tactical=light").ToArray()));
        check("The same attachments on another template are another geometry", key != GeometryKey.Compose("modded-clone", stock));
        check("Attachments do not run together across entries",
            GeometryKey.Compose(glock, new[] { "ab", "c" }) != GeometryKey.Compose(glock, new[] { "a", "bc" }));
        check("The key still names the template for diagnostics", key.StartsWith(glock + "#"));
        check("The magazine (swapped by every reload) does not change the geometry",
            !GeometryKey.AffectsGeometry("mod_magazine") && GeometryKey.AffectsGeometry("mod_pistol_grip") && GeometryKey.AffectsGeometry("mod_reciever"));

        var types = plugin.Types.ToDictionary(t => t.FullName);
        var tryPair = types["DualWieldLab.RuntimePlugin"].Methods.Single(m => m.Name == "TryPair");
        check("Pairing keys the pose caches by assembled geometry, not by template id",
            tryPair.Body.Instructions.Count(i => i.Operand is MethodReference m && m.Name == "Key" && m.DeclaringType.Name == "WeaponGeometry") == 2);
        bool templateKeyed = types["DualWieldLab.OffhandVisualRig"].Methods.Concat(types["DualWieldLab.OffhandHandPose"].Methods)
            .Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MethodReference m && m.Name == "get_StringTemplateId");
        check("No pose cache reads a bare template id any more", !templateKeyed);
    }
}
