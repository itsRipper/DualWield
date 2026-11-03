using System.Text.Json;
using Mono.Cecil;

internal static class ReloadClipChecks
{
    internal static void Run(Action<string,bool> check, string project, AssemblyDefinition plugin)
    {
        string exports = Path.Combine(project, "assets", "exports", "unity");
        using var offhand = JsonDocument.Parse(File.ReadAllText(Path.Combine(exports, "dw_offhand_reload.json")));
        using var handoff = JsonDocument.Parse(File.ReadAllText(Path.Combine(exports, "dw_main_reload_handoff.json")));
        var clip = offhand.RootElement;
        int frames = clip.GetProperty("frames").GetInt32();
        var markers = clip.GetProperty("markers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var ch = clip.GetProperty("channels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetSingle()).ToArray());
        float[] At(string name, int frame, int width) => ch[name].Skip(frame * width).Take(width).ToArray();
        bool Near(float[] a, float[] b, float tol) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= tol);
        bool Zero(float[] v) => v.All(x => Math.Abs(x) < 1e-5f);
        int last = frames - 1;
        string[] order = ["holstered", "mag_grab", "mag_seated", "rack_start", "chamber", "rack_end"];
        check("Left reload clip events are ordered holster -> grab -> seat -> rack -> chamber",
            order.All(markers.ContainsKey) && order.Zip(order.Skip(1)).All(p => markers[p.First] < markers[p.Second]) && markers["rack_end"] < last);
        var widths = new Dictionary<string,int> { ["L.pos"]=3, ["L.rot"]=4, ["R.pos"]=3, ["R.rot"]=4, ["G.pos"]=3, ["G.rot"]=4,
            ["G.w"]=1, ["M.pos"]=3, ["M.rot"]=4, ["R.curl"]=1, ["R.vis"]=1, ["M.vis"]=1 };
        check("Left reload clip has every runtime channel at full length", widths.All(w => ch.TryGetValue(w.Key, out var v) && v.Length == frames * w.Value));
        check("Left reload rotations are unit quaternions", new[]{"L.rot","R.rot","G.rot","M.rot"}.All(n =>
            Enumerable.Range(0, frames).All(f => Math.Abs(At(n, f, 4).Sum(x => x * x) - 1f) < 1e-3f)));
        check("Left reload starts and ends exactly in the paired idle (no snap into or out of it)",
            new[]{0, last}.All(f => Zero(At("L.pos", f, 3)) && Zero(At("R.pos", f, 3)) && At("G.w", f, 1)[0] == 0f
                && At("R.vis", f, 1)[0] == 1f && At("M.vis", f, 1)[0] == 1f && Zero(At("M.pos", f, 3))));
        check("Right pistol stays holstered for the whole assisted magazine change",
            Enumerable.Range(markers["holstered"], markers["rack_end"] - markers["holstered"]).All(f => At("R.vis", f, 1)[0] == 0f));
        check("Right hand is attached to the left pistol from grab through the rack",
            Enumerable.Range(markers["mag_grab"], markers["rack_end"] - markers["mag_grab"] + 1).All(f => At("G.w", f, 1)[0] > .999f));
        check("Magazine is visible and seated at the native swap event",
            At("M.vis", markers["mag_seated"], 1)[0] == 1f && Zero(At("M.pos", markers["mag_seated"], 3)));
        check("Magazine is out of the well while the right hand carries it",
            Enumerable.Range(markers["mag_grab"] + 6, 20).Any(f => At("M.pos", f, 3).Select(x => x * x).Sum() > .0025f));
        check("Tactical reload can skip the rack seamlessly (rack_start pose equals rack_end pose)",
            Near(At("G.pos", markers["rack_start"], 3), At("G.pos", markers["rack_end"], 3), 1e-4f)
            && Near(At("G.rot", markers["rack_start"], 4), At("G.rot", markers["rack_end"], 4), 1e-4f)
            && Near(At("L.pos", markers["rack_start"], 3), At("L.pos", markers["rack_end"], 3), .012f));
        check("No per-frame pose jump larger than 6 cm in the left reload clip",
            new[]{"L.pos","R.pos","G.pos"}.All(n => Enumerable.Range(1, last).Where(f => n != "G.pos" || At("G.w", f, 1)[0] > .999f && At("G.w", f - 1, 1)[0] > .999f)
                .All(f => Math.Sqrt(At(n, f, 3).Zip(At(n, f - 1, 3)).Sum(p => (p.First - p.Second) * (p.First - p.Second))) < .06)));
        var segments = clip.GetProperty("segments").EnumerateArray().ToArray();
        check("Each segment is retimed against a native clip family",
            segments.Length == 5 && segments.All(s => s.GetProperty("native").GetArrayLength() > 0)
            && segments.Select(s => s.GetProperty("name").GetString()).SequenceEqual(new[]{"holster","mag_out","mag_in","rack","draw"}));
        var h = handoff.RootElement;
        var hm = h.GetProperty("markers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        float[] release = h.GetProperty("channels").GetProperty("release").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        float[] capture = h.GetProperty("channels").GetProperty("capture").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        check("R handoff eases from holstered to native and back from native to draw",
            release[0] == 0f && release[hm["release_end"]] > .999f && capture[hm["capture_start"]] == 0f && capture[hm["capture_end"]] > .999f
            && hm["capture_start"] < hm["gun_show"] && hm["gun_show"] < hm["capture_end"]
            && h.GetProperty("constants").GetProperty("holster_rate").GetSingle() is > 1f and < 2f);
        foreach (string blend in new[]{"rigs/dw_fps_arms.blend", "pistols/dw_compact_pistol.blend", "animations/dw_reload_choreography.blend"})
            check("Editable Blender source present: " + blend, new FileInfo(Path.Combine(project, "assets", "blender", blend)) is { Exists: true, Length: > 10000 });

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        string[] inventory = ["TryRunNetworkTransaction", "Swap", "ApplyToAddress", "Move", "RemoveItem", "PopTo", "ReloadMag", "SetItemInHands"];
        check("Reload presentation and handoff are purely visual (no inventory or hands-controller calls)",
            new[]{"DualWieldLab.OffhandNativeAction", "DualWieldLab.OffhandNativeReload", "DualWieldLab.LeftArmHandoff", "DualWieldLab.ReloadClip"}
                .All(n => !All(new[]{pt[n]}).SelectMany(t => t.Methods).SelectMany(Calls).Any(c => inventory.Contains(c.Name))));
        var reload = pt["DualWieldLab.InactiveMagazineReload"];
        check("Preflight simulates the native swap but submits nothing",
            Has(M(reload, "Prepare"), "Swap") && Has(M(reload, "Prepare"), "CanExecute") && !Has(M(reload, "Preflight"), "Begin")
            && !Has(M(reload, "Prepare"), "TryRunNetworkTransaction") && !Has(M(reload, "Preflight"), "TryRunNetworkTransaction"));
        check("Interrupt releases a waiting chamber gate so it cannot submit later", Has(M(reload, "Interrupt"), "TrySetResult"));
        var runtime = pt["DualWieldLab.RuntimePlugin"];
        check("Native swap is submitted at the native clip's OnMagOut and the chamber feed at its OnAddAmmoInChamber",
            Has(M(runtime, "AdvanceOffhandReload"), "Begin") && Has(M(runtime, "AdvanceOffhandReload"), "ReleaseChamber")
            && Has(M(runtime, "AdvanceOffhandReload"), "get_MagOutSeen") && Has(M(runtime, "AdvanceOffhandReload"), "get_ChamberEvent"));
        check("Alt+R refuses before animating when the native swap would be declined",
            Has(M(runtime, "BeginOffhandMagazineReload"), "Preflight"));
        check("Every unpair restores the borrowed right arm, right pistol and magazine",
            Has(M(runtime, "Unpair"), "EndOffhandReloadPresentation") && Has(M(runtime, "Unpair"), "ReleaseOffhandService")
            && Has(M(runtime, "ReleaseOffhandService"), "Restore"));
        check("Presentation overrides are bracketed around native evaluation",
            Has(M(runtime, "BeforeNativeFrame"), "BeforeNativePose") && Has(M(runtime, "AfterVisualPass"), "Apply"));
        check("Verified left reload refreshes the offhand view in place instead of redrawing it",
            Has(M(runtime, "ObserveOffhandMagazineReload"), "RefreshPairInPlace") && Has(M(runtime, "RefreshPairInPlace"), "TryPair"));
        check("Choreography clips are embedded in the plugin",
            new[]{"DualWieldLab.Clips.dw_offhand_reload.json", "DualWieldLab.Clips.dw_main_reload_handoff.json"}
                .All(n => plugin.MainModule.Resources.Any(r => r.Name == n)));
    }
}
