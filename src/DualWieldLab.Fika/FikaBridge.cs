using System;
using System.Collections.Generic;
using BepInEx.Logging;
using EFT;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using UnityEngine;

namespace DualWieldLab.Fika;

// Everything that touches Fika. Constructed by FikaIntegration, which the runtime loads only once
// Fika is loaded, so this type (and Fika.Core) is never loaded in standalone SPT.
//
// Fika already replicates everything native: the right pistol (hands controller, shots, reloads,
// checks), the inventory including the HOLSTERLEFT slot and every inventory transaction of the
// left pistol (magazine swaps, chambering), damage (decided on the shooter's machine, as for any
// shot), death and extraction. This layer adds only what Fika cannot know about: that a second
// pistol is held, its shots, and its state that native Shot changes without an inventory
// operation. Packets are sent reliably and ordered on state changes and shots only, never per
// frame; a client's packet is broadcast by Fika's server to every other peer. Every peer must run
// this add-on: Fika parses each received packet, and an unregistered packet type aborts the rest
// of that network poll on the receiving machine.
internal sealed class FikaBridge
{
    private readonly ManualLogSource Logger;

    internal FikaBridge(ManualLogSource logger)
    {
        Logger = logger;
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnManagerDestroyed);
        FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
        FikaEventDispatcher.SubscribeEvent<PeerConnectedEvent>(OnPeerConnected);
        DualWieldNetwork.LocalStateChanged += OnLocalState;
        DualWieldNetwork.LocalShot += OnLocalShot;
    }
    private IFikaNetworkManager? _manager;
    // The latest snapshot of every other player, by Fika net id, for a player whose snapshot arrived
    // before its object existed (a peer still loading) or whose object was replaced.
    private readonly Dictionary<int, DualWieldStatePacket> _latest = new Dictionary<int, DualWieldStatePacket>();
    private readonly Dictionary<int, Player> _appliedTo = new Dictionary<int, Player>();
    private readonly List<int> _pending = new List<int>();
    private float _nextSweep;
    private bool _versionWarned;
    // Packet counts of this raid, logged once when it ends (DW_NET_STATS).
    private int _sentState, _sentShot, _sentRequest, _recvState, _recvShot, _recvRequest, _ignoredOwn, _unknownPlayer, _rejectedVersion, _sendFailures;

    private void OnManagerCreated(FikaNetworkManagerCreatedEvent e)
    {
        try
        {
            _manager = e.Manager;
            _manager.RegisterPacket<DualWieldStatePacket>(OnStatePacket);
            _manager.RegisterPacket<DualWieldStateRequestPacket>(OnRequestPacket);
            _latest.Clear(); _appliedTo.Clear();
            _sentState = _sentShot = _sentRequest = _recvState = _recvShot = _recvRequest = _ignoredOwn = _unknownPlayer = _rejectedVersion = _sendFailures = 0;
            DualWieldNetwork.ReplicationReady = true;
            Logger.LogInfo("DualWield packets registered (" + (FikaBackendUtils.IsServer ? "host" : "client") + ").");
        }
        catch (Exception ex)
        {
            _manager = null;
            DualWieldNetwork.ReplicationReady = false;
            Logger.LogError("DualWield packets could not be registered; dual wielding stays off in this raid: " + ex);
        }
    }

    private void OnManagerDestroyed(FikaNetworkManagerDestroyedEvent e)
    {
        LogStats();
        _manager = null;
        DualWieldNetwork.ReplicationReady = false;
        DualWieldNetwork.ForgetAllRemote();
        _latest.Clear(); _appliedTo.Clear();
    }

    // A peer starting (or, after a reconnect, restarting) its raid announces its own state, paired or
    // not, and asks every owner for theirs.
    private void OnRaidStarted(FikaRaidStartedEvent e) { SendOwnState(); RequestStates(); }

    // A (re)connecting peer receives this machine's state, paired or not, even before it asks.
    private void OnPeerConnected(PeerConnectedEvent e) => SendOwnState();

    private void RequestStates()
    {
        if (_manager == null || !TryGetMyNetId(out int netId)) return;
        var packet = new DualWieldStateRequestPacket { Version = DualWieldProtocol.Version, NetId = netId };
        _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered, broadcast: true);
        _sentRequest++;
    }

    private void SendOwnState()
    {
        OnLocalState(DualWieldNetwork.CurrentLocalState());
    }

    private void OnLocalState(OffhandSnapshot state) => Send(state, hasShot: false, default);

    private void OnLocalShot(OffhandSnapshot state, OffhandShot shot) => Send(state, hasShot: true, shot);

    private void Send(OffhandSnapshot state, bool hasShot, OffhandShot shot)
    {
        if (_manager == null || !TryGetMyNetId(out int netId)) return;
        var packet = new DualWieldStatePacket { Version = DualWieldProtocol.Version, NetId = netId, State = state, HasShot = hasShot, Shot = shot };
        try { _manager.SendData(ref packet, DeliveryMethod.ReliableOrdered, broadcast: true); if (hasShot) _sentShot++; else _sentState++; }
        catch (Exception e) { _sendFailures++; Logger.LogWarning("DualWield state could not be sent: " + e.Message); }
    }

    private void OnRequestPacket(DualWieldStateRequestPacket packet)
    {
        _recvRequest++;
        if (!CompatibleVersion(packet.Version)) return;
        SendOwnState();
    }

    private void OnStatePacket(DualWieldStatePacket packet)
    {
        if (packet.HasShot) _recvShot++; else _recvState++;
        if (!CompatibleVersion(packet.Version)) return;
        if (TryGetMyNetId(out int mine) && mine == packet.NetId) { _ignoredOwn++; return; }
        // A state replaces the last one; a shot is applied once, its snapshot is kept.
        DualWieldStatePacket latest = packet;
        latest.HasShot = false;
        _latest[packet.NetId] = latest;
        if (!TryGetPlayer(packet.NetId, out Player player)) { _unknownPlayer++; _appliedTo.Remove(packet.NetId); return; }
        if (packet.HasShot) DualWieldNetwork.ApplyRemoteShot(player, packet.State, packet.Shot);
        else DualWieldNetwork.ApplyRemoteState(player, packet.State);
        _appliedTo[packet.NetId] = player;
    }

    // Twice a second: such players receive the latest snapshot. (On a reconnect Fika's host keeps
    // the player's object; the restarted owner announces a new session itself.)
    internal void Update()
    {
        if (_latest.Count == 0 || Time.unscaledTime < _nextSweep) return;
        _nextSweep = Time.unscaledTime + .5f;
        _pending.Clear();
        foreach (var entry in _latest)
            if (TryGetPlayer(entry.Key, out Player player) && (!_appliedTo.TryGetValue(entry.Key, out Player applied) || applied != player))
                _pending.Add(entry.Key);
        foreach (int netId in _pending)
            if (TryGetPlayer(netId, out Player player))
            {
                DualWieldNetwork.ApplyRemoteState(player, _latest[netId].State);
                _appliedTo[netId] = player;
            }
    }

    private void LogStats()
    {
        Logger.LogInfo("DW_NET_STATS sent state=" + _sentState + " shot=" + _sentShot + " request=" + _sentRequest + " failed=" + _sendFailures
            + " | received state=" + _recvState + " shot=" + _recvShot + " request=" + _recvRequest
            + " own=" + _ignoredOwn + " unknown_player=" + _unknownPlayer + " other_version=" + _rejectedVersion + " (raid ended)");
    }

    private bool CompatibleVersion(byte version)
    {
        if (version == DualWieldProtocol.Version) return true;
        _rejectedVersion++;
        if (!_versionWarned)
        {
            _versionWarned = true;
            Logger.LogWarning("A peer runs DualWield network protocol " + version + " (this machine: " + DualWieldProtocol.Version
                + "); its left pistol is not shown. Every player needs the same DualWield version.");
        }
        return false;
    }

    private static bool TryGetMyNetId(out int netId)
    {
        netId = 0;
        if (!CoopHandler.TryGetCoopHandler(out CoopHandler handler) || handler.MyPlayer == null) return false;
        netId = handler.MyPlayer.NetId;
        return true;
    }

    private static bool TryGetPlayer(int netId, out Player player)
    {
        player = null!;
        if (!CoopHandler.TryGetCoopHandler(out CoopHandler handler) || !handler.Players.TryGetValue(netId, out FikaPlayer fikaPlayer) || fikaPlayer == null)
            return false;
        player = fikaPlayer;
        return !fikaPlayer.IsYourPlayer;
    }
}
