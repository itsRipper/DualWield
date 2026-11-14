using Mono.Cecil;

// The magazine shown during a left insert (OnMagAppeared), only once the inventory holds it in the slot.
internal static class MagazineViewChecks
{
    internal static void Run(Action<string, bool> check, ModuleDefinition plugin, ModuleDefinition native)
    {
        var pt = AllTypes(plugin.Types).ToDictionary(t => t.FullName);
        var nt = AllTypes(native.Types).ToDictionary(t => t.FullName);
        List<object> Ops(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).ToList() : new List<object>();
        int Index(MethodDefinition m, string name) => Ops(m).FindIndex(o => o is MethodReference r && r.Name == name);
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);

        var appeared = nt.Values.Where(t => t.FullName.StartsWith("EFT.Player/FirearmController/")).SelectMany(t => t.Methods)
            .Where(m => m.Name == "OnMagAppeared" && Index(m, "SetupMod") >= 0).ToList();
        check("EFT shows the incoming magazine at OnMagAppeared: a pooled model attached with Firearms.SetupMod",
            appeared.Any(m => Index(m, "CreateItem") >= 0 && Index(m, "CreateItem") < Index(m, "SetupMod")));
        check("EFT returns models attached under a pooled weapon together with it (ReturnToPool clears children)",
            Index(nt["EFT.AssetsManager.AssetPoolObject"].Methods.Single(m => m.Name == "ReturnToPool" && !m.HasParameters), "ClearChildren") >= 0);

        var show = M(pt["DualWieldLab.OffhandShotContext"], "ShowInsertedMagazine");
        check("The left insert view uses the same CreateItem + SetupMod, after checking the slot holds that magazine",
            Index(show, "get_ContainedItem") >= 0 && Index(show, "get_ContainedItem") < Index(show, "CreateItem")
            && Index(show, "CreateItem") < Index(show, "SetupMod"));
        check("A magazine with its own streaming animator goes back to the pool instead of binding to the right pistol's animator",
            Index(show, "ReturnToPool") >= 0 && Index(show, "ReturnToPool") < Index(show, "SetupMod"));

        var advance = M(pt["DualWieldLab.RuntimePlugin"], "AdvanceOffhandReload");
        int shown = Index(advance, "ShowInsertedMagazine");
        check("The view appears only for an insert, at the native OnMagShow, once the inventory insert is verified",
            shown >= 0 && new[] { "get_Insert", "get_MagazineVerified", "get_MagShown" }.All(g => Index(advance, g) >= 0 && Index(advance, g) < shown));
        check("The attached magazine joins the left model's first-person layer, visibility and restore",
            Index(advance, "Adopt") > shown && Index(M(pt["DualWieldLab.OffhandVisualRig"], "Adopt"), "ApplyPresentationFlags") >= 0);
        check("A normal swap keeps the old magazine's model as the native stand-in (no extra model)",
            Index(M(pt["DualWieldLab.OffhandNativeReload"], "MagShow"), "SetMagazineVisible") >= 0);
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(AllTypes(t.NestedTypes)));
}
