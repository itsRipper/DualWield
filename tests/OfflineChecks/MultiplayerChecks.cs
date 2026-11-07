using Mono.Cecil;
using Mono.Cecil.Cil;

// The Fika integration and the network-facing parts of the runtime, checked on the built
// assemblies. These are structural checks; multiplayer behaviour is only verified in game.
internal static class MultiplayerChecks
{
    internal static void Run(Action<string, bool> check, string project, AssemblyDefinition runtime, string game)
    {
        IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types) => types.SelectMany(t => new[] { t }.Concat(All(t.NestedTypes)));
        var rt = All(runtime.MainModule.Types).ToDictionary(t => t.FullName);
        MethodDefinition M(TypeDefinition t, string name) => t.Methods.Single(m => m.Name == name);
        IEnumerable<MethodReference> Calls(MethodDefinition m) => m.HasBody ? m.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>() : [];
        bool CallsName(MethodDefinition m, string name) => Calls(m).Any(c => c.Name == name);

        check("Standalone runtime references no Fika assembly (no missing-dependency load in plain SPT)",
            !runtime.MainModule.AssemblyReferences.Any(r => r.Name.StartsWith("Fika", StringComparison.OrdinalIgnoreCase)));
        var tryPair = M(rt["DualWieldLab.RuntimePlugin"], "TryPair");
        check("In a Fika raid the pair requires the network integration (ReplicationReady), the hideout range does not",
            CallsName(tryPair, "HasFika") && CallsName(tryPair, "get_ReplicationReady"));
        var context = rt["DualWieldLab.OffhandShotContext"];
        var replay = M(context, "ReplayRemoteShot");
        check("A replayed remote shot uses native InitiateShot and shot sound, and never runs the ammunition operation or native Shot",
            CallsName(replay, "InitiateShot") && CallsName(replay, "PlayFireBulletSound")
            && !Calls(replay).Any(c => c.Name is "Shot" or "ProcessInventoryOperationsForSingleshotWithMalf" or "ProcessInventoryOperationsForSingleShot"));
        check("A replayed projectile is not counted as a local offhand projectile",
            M(context, "RegisterShot").Body.Instructions.Take(4).Any(i => i.Operand is FieldReference f && f.Name == "_replaying"));
        var remote = rt["DualWieldLab.RemoteDualPistols"];
        var reconcile = M(remote, "Reconcile");
        check("Observed-copy reconciliation only removes rounds (no item is created or moved)",
            CallsName(reconcile, "PopToNowhere") && CallsName(reconcile, "RemoveItemWithoutRestrictions")
            && !Calls(reconcile).Any(c => c.Name is "CreateItem" or "PopTo" or "AddItem" or "TryRunNetworkTransaction" or "Move"));
        check("The observed left pistol is a model of the real inventory item, found by its id",
            CallsName(M(remote, "Bind"), "FindItemById") && CallsName(M(remote, "Bind"), "CreateItem"));
        check("The observed presentation runs after EFT's third-person IK and restores the right pistol before it",
            CallsName(M(remote, "Install"), "Patch") && CallsName(M(remote, "BeforeVisualPass"), "RestoreRight")
            && CallsName(M(remote, "AfterVisualPass"), "AfterPass") && CallsName(M(remote, "AfterPass"), "Pose")
            && CallsName(M(rt["DualWieldLab.DualWieldNetwork"], "AfterObservedPass"), "AfterPass"));
        // Found in Fika two-client tests: EFT's third-person pistol stance is bladed, so a mirror across
        // the shoulders' mid-plane turned the left barrel about 67 degrees across the body.
        check("The observed left pistol is mirrored across the plane containing the aim (shoulder axis projected off the right barrel)",
            CallsName(M(remote, "Pose"), "ProjectOnPlane"));
        // Found in manual Fika tests: the palm was mirrored across the mesh mid-plane while the pistol
        // and hand position used the aim plane, so the fingers closed beside the grip.
        check("The observed left palm is mirrored across the same plane as the pistol (MirrorPalm with the plane normal)",
            Calls(M(remote, "Pose")).Any(c => c.Name == "MirrorPalm" && c.Parameters.Count == 2)
            && !Calls(M(remote, "Pose")).Any(c => c.Name == "MirrorPalm" && c.Parameters.Count == 1));
        // Found in manual Fika tests: a native right-pistol magazine check replayed by Fika left the
        // magazine floating while the left hand stayed on the left pistol.
        var nativeGate = M(remote, "NativeLeftHandAction");
        var gateTypes = nativeGate.Body.Instructions.Where(i => i.OpCode == OpCodes.Isinst).Select(i => ((TypeReference)i.Operand).Name).ToList();
        check("Observed copies leave the left arm native and hide the left pistol during native two-handed right-pistol actions: " + string.Join(",", gateTypes),
            gateTypes.Contains("UtilityOperation") && gateTypes.Contains("MalfunctionOperation") && gateTypes.Contains("BaseReloadOperation")
            && gateTypes.Contains("PullOutMagOperation") && gateTypes.Contains("InsertMagOperation") && gateTypes.Contains("RechamberOperation")
            && CallsName(M(remote, "AfterPass"), "NativeLeftHandAction") && CallsName(M(remote, "AfterPass"), "SetLeftVisible")
            && Calls(M(remote, "AfterPass")).TakeWhile(c => c.Name != "Pose").Any(c => c.Name == "NativeLeftHandAction"));
        check("An observed left pistol returns to the pool with its renderers restored",
            CallsName(M(remote, "Unbind"), "SetLeftVisible")
            && Calls(M(remote, "Unbind")).TakeWhile(c => c.Name != "ReturnToPool").Any(c => c.Name == "SetLeftVisible"));
        // The ordering rule itself is executed in RemoteSyncChecks; states and shots both go through it.
        check("Observed states and shots use the tested ordering rule (RemoteSyncPolicy.Newer); a shot is replayed only inside RemoteSyncPolicy.AcceptShot, which contains replay failures",
            CallsName(M(remote, "Newer"), "Newer") && CallsName(M(remote, "ApplyState"), "Newer")
            && CallsName(M(remote, "ApplyShot"), "AcceptShot") && !CallsName(M(remote, "ApplyShot"), "ReplayRemoteShot")
            && CallsName(M(remote, "Replay"), "ReplayRemoteShot"));
        check("Owners announce their current state even when not paired (a pair that ended before a reconnect is dropped)",
            !M(rt["DualWieldLab.DualWieldNetwork"], "CurrentLocalState").Body.Instructions.Any(i => i.Operand is MethodReference r && r.Name == "get_Paired"));
        check("Local publication is change-driven (snapshot comparison), an action cue is kept out of the re-announced state, shots publish once each",
            CallsName(M(rt["DualWieldLab.DualWieldNetwork"], "PublishIfChanged"), "ShouldPublish") && CallsName(M(rt["DualWieldLab.DualWieldNetwork"], "PublishIfChanged"), "Kept")
            && CallsName(M(rt["DualWieldLab.RuntimePlugin"], "TryFire"), "PublishShot"));

        string fikaPath = Path.Combine(project, "src", "DualWieldLab.Fika", "bin", "Release", "DualWieldLab.Fika.dll");
        if (!File.Exists(fikaPath)) { check("Fika integration built (DualWieldLab.Fika.dll, part of the DualWield package)", false); return; }
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(game, "BepInEx", "core"));
        resolver.AddSearchDirectory(Path.Combine(game, "EscapeFromTarkov_Data", "Managed"));
        using var fika = AssemblyDefinition.ReadAssembly(fikaPath, new ReaderParameters { AssemblyResolver = resolver });
        var ft = All(fika.MainModule.Types).ToDictionary(t => t.FullName);
        var loader = rt["DualWieldLab.FikaIntegrationLoader"];
        string Const(TypeDefinition t, string name) => (string)t.Fields.Single(f => f.Name == name).Constant;
        var entry = ft[Const(loader, "EntryType")];
        var plugin = ft["DualWieldLab.Fika.FikaBridge"];
        // BepInEx 5.4.23 (Chainloader.HasBepinPlugins) inspects, and can load, only an assembly that
        // references BepInEx AND has a type reference to BepInEx.BepInPlugin. The integration must fail
        // that test: as a loaded assembly without Fika it broke standalone raids (JSON converter discovery),
        // and as a hard-dependency plugin it reported a missing dependency in every standalone start.
        check("The Fika integration is invisible to BepInEx (no BepInPlugin type reference, no plugin attribute): never loaded or reported without Fika",
            !(fika.MainModule.AssemblyReferences.Any(r => r.Name == "BepInEx") && fika.MainModule.GetTypeReferences().Any(r => r.FullName == "BepInEx.BepInPlugin"))
            && !fika.MainModule.Types.Any(t => t.CustomAttributes.Any(a => a.AttributeType.Name is "BepInPlugin" or "BepInDependency")));
        var tryStart = M(loader, "TryStart");
        check("The runtime loads the integration from its own folder only after a supported Fika is loaded (from Start, after every plugin's Awake)",
            Const(loader, "FileName") == Path.GetFileName(fikaPath) && Const(loader, "FikaGuid") == "com.fika.core"
            && Calls(tryStart).TakeWhile(c => c.Name != "LoadFile").Any(c => c.Name == "get_PluginInfos")
            && Calls(tryStart).TakeWhile(c => c.Name != "LoadFile").Any(c => c.Name == "get_Metadata")
            && CallsName(tryStart, "LoadFile") && CallsName(tryStart, "AddComponent")
            && CallsName(M(rt["DualWieldLab.RuntimePlugin"], "Start"), "TryStart"));
        // Found in two-client tests: Fika's ObservedPlayer never runs Player.VisualPass, so the left
        // pistol of an observed player was created but never posed (the native one-pistol grip showed).
        var observedPass = ft["DualWieldLab.Fika.ObservedPass"];
        check("The integration poses observed left pistols around Fika's own observed pass (ObservedPlayer.ObservedVisualPass)",
            observedPass.Fields.Any(f => f.Name == "MethodName" && (string)f.Constant == "ObservedVisualPass")
            && CallsName(M(observedPass, "Before"), "BeforeObservedPass") && CallsName(M(observedPass, "After"), "AfterObservedPass")
            && CallsName(M(entry, "Awake"), "Install"));
        check("The integration starts only with a matching DualWield network API (read from the loaded runtime)",
            CallsName(M(entry, "Awake"), "get_ApiVersion")
            && rt["DualWieldLab.DualWieldNetwork"].Properties.Any(p => p.Name == "ApiVersion" && p.GetMethod.IsStatic));
        check("Packets are registered when Fika creates its network manager, and the pair is enabled only then",
            CallsName(M(plugin, "OnManagerCreated"), "RegisterPacket") && CallsName(M(plugin, "OnManagerCreated"), "set_ReplicationReady")
            && CallsName(M(plugin, "OnManagerDestroyed"), "set_ReplicationReady"));
        var send = M(plugin, "Send");
        check("State and shots are sent reliably ordered with broadcast (Fika's server relays them to every peer)",
            CallsName(send, "SendData") && send.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_2 || (i.OpCode == OpCodes.Ldc_I4_S && Convert.ToInt32(i.Operand) == 2)));
        check("Own packets are never applied as a remote player; late joiners request every owner's state",
            CallsName(M(plugin, "OnStatePacket"), "TryGetMyNetId") && CallsName(M(plugin, "RequestStates"), "SendData")
            && CallsName(M(plugin, "OnRequestPacket"), "SendOwnState") && CallsName(M(plugin, "SendOwnState"), "CurrentLocalState")
            && CallsName(M(plugin, "OnRaidStarted"), "SendOwnState"));
        foreach (string name in new[] { "DualWieldLab.Fika.DualWieldStatePacket", "DualWieldLab.Fika.DualWieldStateRequestPacket" })
        {
            var packet = ft[name];
            var written = WireSequence(M(packet, "Serialize"), write: true);
            var read = WireSequence(M(packet, "Deserialize"), write: false);
            check("Wire symmetry of " + packet.Name + ": " + string.Join(",", written), written.Count > 0 && written.SequenceEqual(read));
        }
    }

    // The value types written with NetDataWriter.Put and read with NetDataReader.Get*, in order
    // (helper methods of the packet are followed).
    private static List<string> WireSequence(MethodDefinition method, bool write)
    {
        var sequence = new List<string>();
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not MethodReference call) continue;
            if (call.DeclaringType.FullName == method.DeclaringType.FullName && method.DeclaringType.Methods.FirstOrDefault(m => m.Name == call.Name && m.Parameters.Count == call.Parameters.Count) is MethodDefinition own && own.HasBody)
            { sequence.AddRange(WireSequence(own, write)); continue; }
            if (write && call.DeclaringType.Name == "NetDataWriter" && call.Name == "Put" && call.Parameters.Count >= 1)
                sequence.Add(call.Parameters[0].ParameterType.Name);
            else if (!write && call.DeclaringType.Name == "NetDataReader" && call.Name.StartsWith("Get"))
                sequence.Add(call.ReturnType.Name);
        }
        return sequence;
    }
}
