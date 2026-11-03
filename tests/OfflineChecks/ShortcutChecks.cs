using static CheckHelpers;
using Mono.Cecil;

internal static class ShortcutChecks
{
    internal static void Run(Action<string,bool> check,AssemblyDefinition plugin)
    {
        var runtime=plugin.MainModule.Types.Single(t=>t.FullName=="DualWieldLab.RuntimePlugin");
        MethodDefinition M(string name)=>runtime.Methods.Single(m=>m.Name==name);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>();
        bool Has(string method,string call)=>Calls(M(method)).Any(c=>c.Name==call);
        check("Native right reload action routes through managed service",Has("BeforeHandsCommand","BeginReload"));
        check("New controls retain separate configurable reload and mode shortcuts",new[]{"ReloadOffhand","ChangeOffhandMode"}.All(n=>SettingsCtor(plugin).Body.Instructions.Any(i=>i.Operand is string s&&s==n)));
        check("Pair/reload/mode shortcuts share native delivery barrier",Has("HandleShortcut","BeginShortcut")&&Has("TogglePairOnce","BeginShortcut"));
        check("New hotkeys validate native fire-key conflicts at pairing",Has("TryPair","ValidateShortcuts"));
        var beginCalls=Calls(M("StartPreparedReload")).Select(c=>c.Name).ToArray();
        check("Native reload removes secondary context before ordinary service",Array.IndexOf(beginCalls,"Unpair")>=0&&Array.IndexOf(beginCalls,"Unpair")<Array.IndexOf(beginCalls,"Begin"));
        check("Reload return uses exact original offhand in the pairing path",Has("BindReloadPair","TryPair")&&M("ResumeReloadPair").Parameters.Any(p=>p.Name=="offhand"));
        check("Inventory death scene and plugin boundaries revoke reload callbacks",new[]{"BeforeInventory","BeforeDead","EndSession","OnDestroy"}.All(n=>Has(n,"CancelReload")));
        check("Native UI admission also cancels an active service",Has("ObserveOwnerInput","CancelReload"));
        check("Mode cycling requires release without resetting the opposite trigger",!Has("HandleShortcut","StopPrimaryTrigger")&&Has("ChangeOffhandModeOnce","TryCycle")&&Has("ChangeOffhandModeOnce","Notify"));
        check("Movement pose comes from a pristine native snapshot",Has("AfterVisualPass","CaptureNativePose")&&Has("AlignModel","ApplyRetarget"));
        check("Normal empty feedback uses managed reload controls rather than F7",!M("TryFire").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("F7")));
    }
}
