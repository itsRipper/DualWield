using Mono.Cecil;

internal static class ReloadCallbackChecks
{
    internal static void Run(Action<string, bool> check, AssemblyDefinition native, AssemblyDefinition plugin)
    {
        var token = new DualWieldLab.ReloadCallbackGuard();
        check("Reload callback guard begins inactive", !token.IsCurrent(token.Version));
        int first = token.Begin();
        check("Current reload callbacks admitted", token.IsCurrent(first));
        token.Invalidate();
        check("Cancelled reload cannot resume from late equip or reload callback", !token.IsCurrent(first));
        int second = token.Begin();
        check("A subsequent reload cannot accept previous service callbacks", token.IsCurrent(second) && !token.IsCurrent(first));
        token.Invalidate(); token.Invalidate();
        check("Repeated death/session cleanup keeps callbacks revoked", !token.IsCurrent(first) && !token.IsCurrent(second));
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts) => ts.SelectMany(t => new[] { t }.Concat(All(t.NestedTypes)));
        var nt = All(native.MainModule.Types).ToDictionary(t => t.FullName);
        var pt = All(plugin.MainModule.Types).ToDictionary(t => t.FullName);
        MethodDefinition M(TypeDefinition t, string n) => t.Methods.Single(m => m.Name == n);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        bool Has(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);
        var service = pt["DualWieldLab.NativeReloadService"];
        var nativePlayer = nt["EFT.Player"];
        var inventory = nt["EFT.InventoryLogic.InventoryController"];
        var reload = nt["EFT.Player/FirearmController/ReloadExternalMagOperation"];
        var startCalls = new[] { service }.Concat(service.NestedTypes).SelectMany(t => t.Methods)
            .Where(m => m.Name == "TryPlanReload" || m.Name == "StartNativeReload" || m.Name.Contains("<StartNativeReload>") || m.Name.Contains("<TryPlanReload>"))
            .SelectMany(Calls).ToArray();
        check("Native equip entry preserves TryProceed validation", Has(M(nativePlayer, "SetItemInHands"), "TryProceed") && Has(M(nativePlayer, "SetItemInHands"), "CheckAction"));
        check("Native equip reachability uses actual bindable equipment set", M(inventory, "IsAtReachablePlace").Body.Instructions.Any(i => i.Operand is FieldReference f && f.Name == "BindAvailableSlotsExtended"));
        check("Native reload completion transitions to idle and succeeds callback", Has(M(reload, "SwitchToIdlingState"), "InitiateOperation") && Has(M(reload, "SwitchToIdlingState"), "Succeed"));
        check("Managed reload calls the native magazine operation", startCalls.Any(c=>c.Name=="ReloadMag") && startCalls.Any(c=>c.Name=="CheckMoveIgnoringTargetItem"));
        check("Managed reload uses native reachable magazines and old-mag placement", startCalls.Any(c=>c.Name=="GetReachableItemsOfTypeNonAlloc") && startCalls.Any(c=>c.Name=="FindLocationForItem"));
        check("Reload retains the existing native active controller rather than equipping a weapon", !service.Methods.Any(m=>m.Name=="EquipExpected") && Has(M(service,"Update"),"get_HandsController"));
        var methods = new[] { service }.Concat(service.NestedTypes).SelectMany(t => t.Methods).ToArray();
        check("Reload has no weapon staging or manual network inventory transaction", !methods.SelectMany(Calls).Any(c => c.Name is "Swap" or "SetItemInHands" or "SetEmptyHands" or "TryRunNetworkTransaction"));
        check("Managed reload creates no raw controller or item", !methods.SelectMany(Calls).Any(c => c.Name is "CreateController" or "CreateControllerAsync" or "set_HandsController" or "CreateItem"));
        check("Managed reload fabricates no magazine/ammo mutation", !methods.SelectMany(Calls).Any(c => c.DeclaringType.FullName == "EFT.InventoryLogic.ItemManipulator" && c.Name is "Move" or "Remove" or "Add"));
        check("Managed reload cancellation performs no forced native rollback", !Calls(M(service, "Cancel")).Any(c => c.Name is "Swap" or "TryRunNetworkTransaction" or "RollBack" or "SetItemInHands"));
        check("Native service validates exact existing IDs and original addresses", Has(M(service, "AddressesMatch"), "IdentityValid") && Has(M(service, "Complete"), "AddressesMatch"));
        check("Native service invalidates late callbacks at cleanup", Has(M(service, "Clear"), "Invalidate"));
        check("Native service uses bounded active-state update without inventory scans", !Calls(M(service, "Update")).Any(c => c.Name is "FindObjectOfType" or "GetAllItemsNonAlloc" or "GetComponentsInChildren" or "GetReachableItemsOfTypeNonAlloc"));
        check("Native service never fast-forwards reload animations", !methods.SelectMany(Calls).Any(c => c.Name is "FastForward" or "FastForwardCurrentState" or "FastForwardCurrentOperations"));
    }
}
