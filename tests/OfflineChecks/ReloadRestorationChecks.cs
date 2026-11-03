using DualWieldLab;
using Mono.Cecil;

internal static class ReloadRestorationChecks
{
    internal static void Run(Action<string,bool> check, AssemblyDefinition plugin)
    {
        var gate = new ReloadPairRestoration();
        check("No native reload completion means no restoration admission", !gate.TryBind(1,true) && !gate.TryComplete(1,true));
        int version=gate.Begin();
        check("Native completion queues restoration without claiming a usable pair",gate.Waiting && !gate.Recovering && !gate.TryComplete(1,true));
        check("Synchronous completion callback cannot bind before a native visual pass",!gate.TryBind(1,true));
        gate.ObserveNativeFrame(1,false);
        check("Old native input translator cannot satisfy restoration readiness",!gate.TryBind(2,false));
        gate.ObserveNativeFrame(2,true);
        check("Same-frame native callbacks do not bypass the settling boundary",!gate.TryBind(2,true));
        check("Current native hands and a later frame admit one binding attempt",gate.TryBind(3,true));
        check("The same completion cannot schedule a second binding attempt",!gate.TryBind(4,true));
        check("Successful binding enters presentation recovery, not completed state",gate.BeginRecovery(version)&&gate.Recovering&&!gate.TryComplete(4,true));
        gate.PresentationFinished();
        check("Recovery exit still requires a normal paired native frame",gate.Verifying&&!gate.TryComplete(5,true));
        gate.ObserveNativeFrame(5,true);
        check("A normal paired frame is verified on a later frame",!gate.TryComplete(5,true)&&gate.TryComplete(6,true));
        check("Complete restoration releases the reload gate exactly once",!gate.Active&&!gate.TryComplete(7,true));

        gate.Begin();gate.ObserveNativeFrame(10,true);
        check("Lost readiness invalidates a previously observed native frame",!gate.TryBind(11,false)&&!gate.TryBind(12,true));
        gate.ObserveNativeFrame(12,true);gate.Cancel();
        check("Inventory or raid cancellation revokes a queued restoration",!gate.TryBind(13,true)&&!gate.Active);
        int stale=gate.Begin();gate.ObserveNativeFrame(20,true);gate.TryBind(21,true);gate.Cancel();
        int current=gate.Begin();
        check("Late pre-cancellation callbacks cannot begin a fresh recovery",!gate.BeginRecovery(stale)&&gate.Waiting&&current!=stale);
        gate.ObserveNativeFrame(22,true);gate.TryBind(23,true);gate.BeginRecovery(current);gate.PresentationFinished();
        gate.ObserveNativeFrame(24,true);gate.Cancel();
        check("Interruption after magazine and presentation completion cannot claim usable success",!gate.TryComplete(25,true));

        // Exercise callback reentrancy with the actual runtime admission helper.
        bool serviceActive=true;bool pairCreated=false;bool fullSuccess=false;
        gate.Cancel();
        void NativeCallback() { serviceActive=false;gate.Begin();pairCreated=gate.TryBind(30,true); }
        NativeCallback();
        check("Reentrant native service completion cannot create the pair inside its callback",!serviceActive&&!pairCreated&&gate.Waiting);
        gate.ObserveNativeFrame(31,true);
        pairCreated=gate.TryBind(32,true);
        gate.BeginRecovery(gate.Version);
        check("Service cleanup does not erase the independent pending recovery",pairCreated&&gate.Recovering&&!fullSuccess);
        gate.PresentationFinished();gate.ObserveNativeFrame(33,true);fullSuccess=gate.TryComplete(34,true);
        check("Complete callback simulation succeeds only after normal native paired presentation",fullSuccess&&!gate.Active);

        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> ts)=>ts.SelectMany(t=>new[]{t}.Concat(All(t.NestedTypes)));
        var types=All(plugin.MainModule.Types).ToDictionary(t=>t.FullName);
        MethodDefinition M(TypeDefinition t,string n)=>t.Methods.Single(m=>m.Name==n);
        IEnumerable<MethodReference> Calls(MethodDefinition m)=>m.HasBody?m.Body.Instructions.Select(i=>i.Operand).OfType<MethodReference>():[];
        bool Has(MethodDefinition m,string n)=>Calls(m).Any(c=>c.Name==n);
        var runtime=types["DualWieldLab.RuntimePlugin"];
        check("Native completion callback queues rather than immediately pairing",!Has(M(runtime,"ResumeReloadPair"),"TryPair")&&Has(M(runtime,"ResumeReloadPair"),"Begin"));
        check("Deferred native binding uses the exact original offhand reference",Has(M(runtime,"BindReloadPair"),"TryPair")&&M(runtime,"BindReloadPair").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="_returnOffhand"));
        check("Restoration checks real current native input translator and pending process",Has(M(runtime,"ReloadNativeReady"),"get_HandsInputTranslator")&&Has(M(runtime,"ReloadNativeReady"),"get_ScheduledProcess"));
        check("Final usable check includes actual normal hand snapshot and active secondary",Has(M(runtime,"ReloadPairedReady"),"get_HasSnapshot")&&Has(M(runtime,"ReloadPairedReady"),"get_activeInHierarchy"));
        check("Normal native pose brackets final restoration verification",Has(M(runtime,"AfterVisualPass"),"CaptureNativePose")&&Has(M(runtime,"AfterVisualPass"),"ObserveNativeFrame"));
        check("Final status is distinct from provisional pair acceptance",M(runtime,"Update").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("DW_RELOAD_COMPLETE usable=True"))&&M(runtime,"BindReloadPair").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("DW_RELOAD_PAIR provisional=")));
        check("Cleanup records unpair reason before invalidating restoration",M(runtime,"Unpair").Body.Instructions.Any(i=>i.Operand is string s&&s.Contains("DW_UNPAIR reason="))&&Has(M(runtime,"CancelReload"),"Cancel"));
    }
}
