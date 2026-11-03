using System.Text.Json;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ReloadSoundChecks
{
    internal static void Run(Action<string,bool> check, string project, AssemblyDefinition plugin)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(project, "assets", "exports", "unity", "dw_offhand_reload.json")));
        var markers = doc.RootElement.GetProperty("markers").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        var sounds = markers.Where(m => m.Key.StartsWith("snd:")).ToDictionary(m => m.Key, m => m.Value);
        string Find(string name) => sounds.Keys.Single(k => k.Split(':')[2].Split('@')[0] == name);
        check("Reload sound markers are well formed (side R/L, gate swap/chamber only)",
            sounds.Count >= 8 && sounds.Keys.All(k => k.Split(':') is [_, "R" or "L", var n] && n.Split('@') is [_] or [_, "swap" or "chamber"]));
        check("Magazine-in sound is gated on the verified swap at the seat event",
            Find("SndMagIn") == "snd:L:SndMagIn@swap" && sounds[Find("SndMagIn")] == markers["mag_seated"]);
        check("Slide-release sound is gated on the verified chamber feed at the chamber event",
            Find("SndSlideIn") == "snd:L:SndSlideIn@chamber" && sounds[Find("SndSlideIn")] == markers["chamber"]);
        check("Slide handling sounds sit inside the rack so tactical reloads never play them",
            new[]{"SndHandOn", "SndSlideOut", "SndSlideIn"}.All(n => sounds[Find(n)] >= markers["rack_start"] && sounds[Find(n)] < markers["rack_end"]));
        check("Removal sounds precede the native swap and the right holster/draw bracket the clip",
            sounds[Find("SndMagButton")] < sounds[Find("SndMagOut")] && sounds[Find("SndMagOut")] < markers["mag_seated"]
            && Find("SndHolster").StartsWith("snd:R:") && Find("SndDraw").StartsWith("snd:R:")
            && sounds[Find("SndHolster")] < markers["holstered"] && sounds[Find("SndDraw")] > markers["rack_end"]);
        check("Each native reload sound is authored once (no duplicate events)",
            sounds.Keys.Select(k => k.Split(':')[2].Split('@')[0]).Distinct().Count() == sounds.Count);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[]{t}.Concat(All(t.NestedTypes)));
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        bool Stores(MethodDefinition m, string field) => m.HasBody && m.Body.Instructions.Any(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == field);
        var runtime = pt["DualWieldLab.RuntimePlugin"];
        var pairing = pt["DualWieldLab.HolsterAutoPairing"];
        var advance = M(runtime, "AdvanceOffhandReload");
        check("Sprint pauses the left reload instead of cancelling it",
            Has(advance, "get_IsSprintEnabled") && Calls(advance).Count(c => c.Name == "CancelReload") == 1
            && Has(advance, "set_HoldNativeAnimation"));
        check("Native swap and chamber release are withheld while sprinting",
            advance.Body.Instructions.Count(i => i.Operand is MethodReference r && r.Name == "get_IsSprintEnabled") == 1
            && Has(advance, "Begin") && Has(advance, "ReleaseChamber"));
        check("Held reload follows the paired sprint-lowered pose", Has(M(runtime, "AfterVisualPass"), "Sample"));
        check("Left reload sounds are the native clip's own events through the left WeaponSoundPlayer",
            Has(M(pt["DualWieldLab.OffhandShotContext"], "BeginNativeReload"), "BeginNativeAction")
            && Stores(M(pt["DualWieldLab.OffhandShotContext"], "Initialize"), "AdditionalSounds")
            && Has(M(pt["DualWieldLab.OffhandShotContext"], "PlayHandlingSound"), "SoundEventHandler"));
        check("Opening the inventory records the exact pair before releasing it",
            Has(M(runtime, "BeforeInventory"), "RememberPairForInventory") && Has(M(runtime, "Unpair"), "RememberPairForInventory"));
        var resume = M(pairing, "TryResumeAfterInventory");
        check("Inventory close restores the pair through native readiness and the full pairing validation",
            Has(M(runtime, "Update"), "Update") && Has(M(pairing, "Update"), "TryResumeAfterInventory") && Has(resume, "TryPair") && Has(resume, "get_IsInventoryOpened")
            && Has(resume, "get_Owner") && Has(resume, "PistolHandsReady")
            // Readiness is shared with automatic pairing.
            && Has(M(pairing, "PistolHandsReady"), "get_IsChangingWeapon") && Has(M(pairing, "PistolHandsReady"), "AllowsGameplay")
            && Has(M(pairing, "PistolHandsReady"), "get_ReloadBusy"));
        check("Manual unpair and session end cancel a pending inventory resume",
            Has(M(runtime, "TogglePairOnce"), "CancelPending") && Has(M(runtime, "EndSession"), "Reset")
            && Stores(M(pairing, "CancelPending"), "_inventoryResume") && Stores(M(pairing, "Reset"), "_inventoryResume"));
    }
}
