using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.InputSystem;
using HarmonyLib;
using UnityEngine;

namespace DualWieldLab;

[BepInPlugin(Id, "DualWield", "1.0.0")]
[BepInIncompatibility(LegacyId)]
public sealed partial class RuntimePlugin : BaseUnityPlugin, IPairingHost
{
    public const string Id = "itsripper.dualwield";
    // The plugin's id before the rename: never load next to an old copy of this plugin.
    private const string LegacyId = "local.dualwieldlab.glock17";
    internal static RuntimePlugin? Instance;
    private Harmony? _harmony;
    private DualWieldSettings _settings = null!;
    private float _leftModifierHeldAt = -1f;
    private int _leftCommandFrame = -1;
    private NativeReloadService? _reloadService;
    private InactiveMagazineReload? _offhandReload;
    private OffhandNativeReload? _nativeReload;
    private Magazine? _offhandReloadNext;
    private bool _offhandReloadPresented;
    private LeftArmHandoff? _handoff;
    private ReloadClip? _offhandClip, _handoffClip, _serviceClip;
    private OffhandNativeAction? _offhandAction;
    private readonly Quaternion[] _holsterPose = new Quaternion[20];
    private bool _holsterPoseCaptured, _offhandMechanicsSynced, _offhandReloadSprinting, _offhandReturnWaitsForSprint;
    // Native two-handed weapon checks (magazine, chamber, examine) on the right pistol.
    private enum InspectPhase { None, Holstering, Native, Drawing }
    private InspectPhase _inspect;
    private ECommand _inspectCommand;
    private float _inspectSince;
    private bool _inspectSeen, _inspectStepDone;
    private readonly PresentationFrameGate _inspectDone = new PresentationFrameGate();
    private HolsterAutoPairing _holsterPairing = null!;
    private float _drawStarted;
    private WeaponPrefab? _prefab;
    private PistolHand? _preparingReload;
    private readonly ReloadPairRestoration _reloadReturn = new ReloadPairRestoration();
    private bool _recoveringReload => _reloadReturn.Recovering;
    private Weapon? _returnMain, _returnOffhand;
    private ItemAddress? _reloadMainAddress, _reloadOffhandAddress;
    private bool _reloadInputAdmitted, _returnReloadVerified;
    private float _returnStarted;
    private string _returnWaitReason = "";
    private readonly PresentationFrameGate _preparePresented = new PresentationFrameGate();
    private readonly PresentationFrameGate _recoverPresented = new PresentationFrameGate();
    private bool ReloadVisualTransition => _preparingReload.HasValue || _recoveringReload;
    private bool WeaponActionBusy => _offhandReload != null || _reloadService?.Active == true || ReloadVisualTransition || _reloadReturn.Active
        || _inspect != InspectPhase.None || _offhandAction != null;
    // Also covers the paired draw and holster: no weapon action or shot until both are ready.
    private bool ReloadBusy => WeaponActionBusy || _transition.Active;
    private readonly DualTransition _transition = new DualTransition();
    private PlayerOwner? PairOwner => _input?.Owner ?? _transition.DrawOwner;
    private Player? _servicePlayer;
    private PlayerOwner? _serviceOwner;
    private PairedInput? _input;
    private readonly TriggerEdgeGate _trigger = new TriggerEdgeGate();
    private readonly PresentationRecovery _poseRecovery = new PresentationRecovery();
    private readonly PairInputBarrier _pairBarrier = new PairInputBarrier();
    private PlayerOwner? _pairBarrierOwner;
    private InputManager? _pairBarrierManager;
    private OffhandVisualRig? _visualRig;
    private Weapon.EFireMode _primaryMode;
    private bool _primaryRequiresRelease = true;
    private int _primaryLastShotFrame = -1;
    private int _primaryAutoUpdateFrame = -1;
    private bool _ownerInputAdmitted;
    private readonly List<Item> _items = new List<Item>(256);
    private readonly List<(Collider collider, bool enabled)> _colliderStates = new List<(Collider, bool)>();
    private Player? _player;
    private Player.FirearmController? _primary;
    private Weapon? _secondary;
    private GameObject? _model;
    private GameObject? _contextObject;
    private OffhandShotContext? _shot;
    private OffhandHandPose? _handPose;
    private readonly Dictionary<Weapon, double> _cooldowns = new Dictionary<Weapon, double>(64);
    private GameWorld? _cooldownWorld;
    private ItemAddress? _secondaryAddress;
    private bool _pairing;
    private bool _unpairing;
    private bool _dualAiming;
    private PistolAnimationState? _animation;
    private PistolAnimationProfile _animationProfile;
    private int _collisionMask;
    private bool _faulted;
    private bool _patchesReady;
    private int _reportedDamageDrops, _reportedDamageFailures;
    private bool _poseReportPending;
    private int _reportedVisualPhase = -1;
    private int _modeShortcutFrame = -1;
    private Quaternion _diagnosticLook;
    private float _diagnosticPoseAt;
    private ShotAuditLog _shotAudit = null!;

    private void Awake()
    {
        Instance = this;
        bool configExisted = File.Exists(Config.ConfigFilePath);
        _settings = new DualWieldSettings(Config);
        ConfigMigration.Apply(Config, configExisted, DualWieldSettings.LegacyConfigFile, DualWieldSettings.RenamedKeys, Logger.LogInfo, Logger.LogWarning);
        _shotAudit = new ShotAuditLog(Logger);
        _holsterPairing = new HolsterAutoPairing(_settings, this);
        _collisionMask = LayerMask.GetMask("HighPolyCollider", "LowPolyCollider", "Terrain");
        _offhandClip = LoadClip("dw_offhand_reload");
        _handoffClip = LoadClip("dw_main_reload_handoff");
        _serviceClip = LoadClip("dw_offhand_service");
        if (!SupportedGame.IsSupported(typeof(Player)))
        {
            Logger.LogError("Disabled: EFT " + SupportedGame.Running() + " is not the supported " + SupportedGame.Build + ".");
            enabled = false;
            return;
        }
        // Never enter a cooperative/network session with an unreplicated second weapon.
        if (HasFika())
        {
            Logger.LogError("Disabled: Fika is installed; dual wielding is single-player only.");
            enabled = false;
            return;
        }
        try
        {
            _harmony = new Harmony(Id);
            Patch(typeof(Player), nameof(Player.DropCurrentController), nameof(BeforeDropController));
            Patch(typeof(Player), nameof(Player.DestroyController), nameof(BeforePlayerBoundary));
            Patch(typeof(Player), nameof(Player.SetInventoryOpened), nameof(BeforeInventory));
            Patch(typeof(Player), nameof(Player.OnGameSessionEnd), nameof(BeforeSessionEnd));
            Patch(typeof(HideoutPlayer), nameof(HideoutPlayer.ReleaseShootingRangeInventory), nameof(BeforeRangeInventoryChange));
            Patch(typeof(HideoutPlayer), nameof(HideoutPlayer.UpdateHideoutPlayerInventory), nameof(BeforeRangeInventoryChange));
            Patch(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.SetShootingRangeStatus), nameof(BeforeRangeStatus));
            Patch(typeof(Player.FirearmController), nameof(Player.FirearmController.OnPlayerDead), nameof(BeforeDead));
            _harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.ToggleAim)), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeToggleAim)));
            _harmony.Patch(AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.SetAim), new[] { typeof(bool) }), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeSetAim)));
            _harmony.Patch(AccessTools.Method(typeof(FirearmHandsInputTranslator), nameof(FirearmHandsInputTranslator.TranslateCommand), new[] { typeof(ECommand) }), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeHandsCommand)));
            _harmony.Patch(AccessTools.Method(typeof(PlayerOwner), nameof(PlayerOwner.TranslateCommand), new[] { typeof(ECommand) }), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeOwnerCommand)));
            _harmony.Patch(AccessTools.Method(typeof(InputManager), nameof(InputManager.DeliverInput)), postfix: new HarmonyMethod(typeof(RuntimePlugin), nameof(AfterInputDelivery)));
            _harmony.Patch(AccessTools.Method(typeof(InputNode), nameof(InputNode.TranslateInput)),
                prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(ObserveOwnerInput)),
                postfix: new HarmonyMethod(typeof(RuntimePlugin), nameof(ObserveOwnerInput)));
            _harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.Start), new[] { typeof(Action) }), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeIdlingStart)));
            _harmony.Patch(AccessTools.Method(typeof(Player.FirearmController.AutomaticFireOperation), nameof(Player.FirearmController.AutomaticFireOperation.Update), new[] { typeof(float) }), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeAutomaticUpdate)));
            _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.VisualPass)),
                prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeVisualPass)),
                postfix: new HarmonyMethod(typeof(RuntimePlugin), nameof(AfterVisualPass)));
            _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ComplexUpdate)), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeNativeFrame)));
            _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot)), prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeMakingShot)));
            _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.ComplexLateUpdate)),
                prefix: new HarmonyMethod(typeof(RuntimePlugin), nameof(BeforeNativeFrame)),
                postfix: new HarmonyMethod(typeof(RuntimePlugin), nameof(AfterVisualPass)));
            _harmony.Patch(AccessTools.Method(typeof(EFT.Animations.ProceduralWeaponAnimation), nameof(EFT.Animations.ProceduralWeaponAnimation.ApplyComplexRotation)),
                postfix: new HarmonyMethod(typeof(RuntimePlugin), nameof(AfterRecoilApplied)));
            DamageDiagnostics.Install(_harmony);
            GameWorld.OnDispose += OnWorldDisposed;
            _patchesReady = true;
            Logger.LogInfo("DualWield active for EFT " + SupportedGame.Running() + ".");
        }
        catch (Exception e)
        {
            _harmony?.UnpatchSelf();
            _faulted = true;
            Logger.LogError("Patch setup failed; DualWield disabled: " + e);
        }
    }

    private static void AfterRecoilApplied(EFT.Animations.ProceduralWeaponAnimation __instance) => PrimaryRecoilSample.Record(__instance);

    private ReloadClip? LoadClip(string name)
    {
        try { return ReloadClip.LoadEmbedded(name); }
        catch (Exception e) { Logger.LogError("Reload choreography " + name + " unavailable; the procedural reload presentation is used: " + e.Message); return null; }
    }

    private void Patch(Type type, string name, string prefix)
        => _harmony!.Patch(AccessTools.Method(type, name), prefix: new HarmonyMethod(typeof(RuntimePlugin), prefix));

    private static bool HasFika() => BepInEx.Bootstrap.Chainloader.PluginInfos.Values.Any(p => p.Metadata.GUID.IndexOf("fika", StringComparison.OrdinalIgnoreCase) >= 0);

    private static void BeforePlayerBoundary(Player __instance)
    {
        var self = Instance;
        if (self?._servicePlayer == __instance) self.ReleaseReloadPresentation();
        if (self?._servicePlayer == __instance && self.ReloadVisualTransition)
            self.CancelReload("Native controller change interrupted reload handover");
        if (self?._servicePlayer == __instance && self._reloadService?.Active == true)
            self.CancelReload("Native controller transition interrupted reload service");
        if (Instance?._player == __instance) Instance.Unpair("Player/controller transition");
    }

    // Native weapon switch: EFT plays the hide animation, then destroys the controller. A pair
    // with no weapon action in progress is kept through the hide so both pistols lower
    // together; DestroyController releases it.
    private static void BeforeDropController(Player __instance, bool fastDrop)
    {
        var self = Instance;
        if (self != null && self._player == __instance && self._servicePlayer != __instance && self.BeginDualHolster(fastDrop)) return;
        BeforePlayerBoundary(__instance);
    }

    private static void BeforeInventory(Player __instance, bool opened)
    {
        var self = Instance;
        if (self == null) return;
        if (!opened)
        {
            if (__instance.IsYourPlayer && self._player == null) self._holsterPairing.OnInventoryClosed(__instance);
            return;
        }
        // Inventory suspends the pair: the presentation is released (the native UI may move
        // items) and the exact pair is re-validated and rebuilt when the inventory closes.
        if (self._servicePlayer == __instance)
        {
            self._holsterPairing.RememberPairForInventory(__instance, self._serviceOwner, self._returnMain, self._returnOffhand);
            self.CancelReload("Inventory opened during reload service");
        }
        if (self._player == __instance)
        {
            self._holsterPairing.RememberPairForInventory(__instance, self.PairOwner, self._primary?.Item, self._secondary);
            self.Unpair("Inventory opened; dual wield resumes when it closes");
        }
    }

    private static void BeforeDead(Player.FirearmController __instance)
    {
        if (Instance?._servicePlayer?.HandsController == __instance) Instance.CancelReload("Player death");
        if (Instance?._primary == __instance) Instance.Unpair("Player death");
    }

    private static void BeforeSessionEnd(Player __instance)
    {
        var self = Instance;
        if (self == null || !__instance.IsYourPlayer) return;
        self.EndSession("Game session ended");
    }

    private static void BeforeRangeInventoryChange(HideoutPlayer __instance)
    {
        if (IsCurrentRangePlayer(__instance))
            Instance?.EndSession("Native shooting-range inventory is refreshing or leaving");
    }

    private static void BeforeRangeStatus(HideoutPlayerOwner __instance, bool __0)
    {
        if (!__0 && __instance.Player is HideoutPlayer player && IsCurrentRangePlayer(player))
            Instance?.EndSession("Leaving the native shooting range");
    }

    private static bool IsCurrentRangePlayer(HideoutPlayer player)
    {
        var self = Instance;
        return self != null && player != null && player.IsYourPlayer
            && (self._player == player || self._servicePlayer == player
                || (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer == player));
    }

    private void OnWorldDisposed() => EndSession("World disposed");

    private void EndSession(string reason)
    {
        _holsterPairing.Reset();
        CancelReload(reason);
        Unpair(reason);
        _cooldowns.Clear();
        _cooldownWorld = null;
        DamageDiagnostics.Clear();
        _pairBarrier.Reset();
        _pairBarrierOwner = null;
        _pairBarrierManager = null;
        _faulted = !_patchesReady;
    }

    // An action did nothing and the player should know why: EFT's own notification.
    private void Notify(string message)
    {
        LogDiagnostic("DW_NOTICE " + message);
        try { NotificationManager.DisplayMessageNotification(message); }
        catch (Exception e) { Logger.LogWarning("Notification failed: " + e.Message + " (" + message + ")"); }
    }

    // Progress of an action the player can already see; diagnostics only.
    private void SetStatus(string message) => LogDiagnostic("DW_STATUS " + message);

    private void Update()
    {
        // Observe one cached task state; no inventory/hierarchy polling.
        ObserveOffhandMagazineReload();
        AdvanceOffhandReload();
        PlayPendingSelectorSound();
        if (_handoff?.Finished == true) _handoff = null;
        if (!_pairBarrier.Blocks(Time.frameCount))
        { _pairBarrierOwner = null; _pairBarrierManager = null; }
        DamageDiagnostics.Enabled = _settings.Diagnostics.Value;
        if (!ReferenceEquals(_player, null) && _player == null) Unpair("Player object destroyed");
        if (!_settings.Enabled.Value)
        {
            CancelReload("Disabled in configuration");
            if (_player != null) Unpair("Disabled in configuration");
            return;
        }
        if (_reloadReturn.Waiting || _reloadReturn.Verifying)
        {
            if (_servicePlayer == null || !_servicePlayer.HealthController.IsAlive || _servicePlayer.IsInventoryOpened
                || !Singleton<GameWorld>.Instantiated || Singleton<GameWorld>.Instance.MainPlayer != _servicePlayer
                || Time.unscaledTime - _returnStarted > 15f)
            {
                CancelReload("Native pair restoration ended or timed out; original item ownership retained, no retry.");
                if (_player != null) Unpair("Reload restoration could not establish usable native hands");
                return;
            }
            if (_settings.PairShortcut.Value.IsDown() && PairedInput.AllowsGameplay(_serviceOwner) && BeginShortcut(_serviceOwner!))
            { CancelReload("Manual unpair during native pair restoration"); if (_player != null) Unpair("Manual unpair"); return; }
            StopPrimaryTrigger(); StopOffhandTrigger();
            bool ready = _reloadReturn.Waiting ? ReloadNativeReady(out string reason) : ReloadPairedReady(out reason);
            if (_returnWaitReason != reason)
            {
                _returnWaitReason = reason;
                LogDiagnostic("DW_RELOAD_RETURN state=" + _reloadReturn.State + " ready=" + ready + " reason=" + reason);
            }
            if (_reloadReturn.TryBind(Time.frameCount, ready)) BindReloadPair();
            else if (_reloadReturn.TryComplete(Time.frameCount, ready))
            {
                LogDiagnostic("DW_RELOAD_COMPLETE usable=True native_reload_verified=" + _returnReloadVerified
                    + " main=" + _returnMain?.Id + " offhand=" + _returnOffhand?.Id
                    + " pistol_addresses_unchanged=" + ReloadAddressesMatch() + " weapon_moves=0");
                SetStatus(_returnReloadVerified ? "Native reload complete; both original pistols restored and ready."
                    : "Native reload did not complete; original dual-wield setup restored and ready.");
                _returnMain = _returnOffhand = null;
                _reloadMainAddress = _reloadOffhandAddress = null;
                _servicePlayer = null; _serviceOwner = null; _reloadInputAdmitted = false;
            }
            return;
        }
        if (_reloadService != null && !_reloadService.Active && !_reloadReturn.Active)
        {
            ReleaseReloadPresentation();
            _reloadService = null; _servicePlayer = null; _serviceOwner = null;
        }
        if (ReloadVisualTransition)
        {
            if (_player == null || _input == null || !PairedInput.AllowsGameplay(_input.Owner)
                || _player.IsSprintEnabled || PairedStopReason() != null || !ReloadAddressesMatch())
            { CancelReload("Reload visual transition interrupted; native ownership retained."); return; }
            StopPrimaryTrigger(); StopOffhandTrigger();
            if (_settings.PairShortcut.Value.IsDown() && BeginShortcut(_input.Owner))
            { Unpair("Manual unpair during reload handover"); return; }
            if (_preparingReload.HasValue && _preparePresented.Ready(Time.frameCount)) StartPreparedReload();
            else if (_recoveringReload && _recoverPresented.Ready(Time.frameCount))
            {
                ReleaseReloadPresentation();
                _reloadReturn.PresentationFinished();
            }
            return;
        }
        if (_reloadService?.Active == true)
        {
            if (_servicePlayer == null || _servicePlayer.CustomAnimationsAreProcessing)
            { CancelReload("Native custom animation interrupted reload presentation; native item locations retained."); return; }
            if (_settings.PairShortcut.Value.IsDown() && PairedInput.AllowsGameplay(_serviceOwner))
            { if (BeginShortcut(_serviceOwner!)) CancelReload("Manual unpair during reload service"); }
            else
            {
                var service = _reloadService;
                service.Update();
                if (ReferenceEquals(_reloadService, service) && !service.Active)
                {
                    CancelReload("Native reload service stopped before usable paired completion; no weapon rollback requested.");
                }
            }
            return;
        }
        _holsterPairing.Update(paired: _player != null);
        if (_settings.LeftModifier.Value != KeyCode.None && Input.GetKey(_settings.LeftModifier.Value)) _leftModifierHeldAt = Time.unscaledTime;
        if (_settings.PairShortcut.Value.IsDown() || (_player != null && (_settings.ReloadOffhand.Value.IsDown() || ModeShortcutDown())))
        {
            var owner = ShortcutOwner();
            if (PairedInput.AllowsGameplay(owner)) HandleShortcut(owner!);
        }
        if (_player == null) return;
        if (_transition.Holstering)
        {
            // DestroyController normally ends it; a hide that never completes must not keep the pair.
            StopPrimaryTrigger(); StopOffhandTrigger();
            if (_transition.HolsterTimedOut) Unpair("The native holster did not complete");
            else if (PairedStopReason() is string reason) Unpair(reason);
            return;
        }
        if (_transition.DrawPending)
        {
            string? drawStop = PairedStopReason();
            if (drawStop != null) { Unpair(drawStop); return; }
            FinishDualDraw();
            if (_transition.DrawPending || _player == null) return;
        }
        bool gameplay = _ownerInputAdmitted && PairedInput.AllowsGameplay(_input?.Owner);
        _trigger.Observe(gameplay, _input?.IsAimHeld() ?? true);
        if (_secondary != null) _trigger.ObserveMode(WeaponCompatibility.GetTriggerMode(_secondary.SelectedFireMode));
        UpdatePrimaryTriggerSafety(gameplay);
        if (!_trigger.Holding || !gameplay) _shot?.ReleaseTrigger();
        // Only fixed references and state predicates while paired. No object or inventory scans.
        string? stopReason = PairedStopReason();
        if (stopReason != null)
        {
            Unpair(stopReason);
            return;
        }
        AdvanceInspection();
        AdvanceOffhandService();
        if (_player == null) return;
        _dualAiming = _settings.ReadyAim.Value;
        _animation?.Step(Time.deltaTime, _dualAiming, _player.IsSprintEnabled);
        if (_secondary != null)
        {
            _cooldowns.TryGetValue(_secondary, out double deadline);
            if (_trigger.ClaimRepeat(Time.frameCount, gameplay, _input?.IsAimHeld() ?? false,
                WeaponCompatibility.GetTriggerMode(_secondary.SelectedFireMode), Time.timeAsDouble, deadline)) TryFire();
        }
    }

    private string? PairedStopReason()
    {
        if (_player == null) return "Player object unavailable";
        if (_player is HideoutPlayer)
        {
            PlayerOwner? owner = PairOwner;
            if (owner == null) return "Native shooting-range input owner unavailable";
            string? rangeReason = HideoutRangeContext.Refusal(_player, owner);
            if (rangeReason != null) return rangeReason;
        }
        if (!_player.HealthController.IsAlive) return "Player death";
        if (_primary == null || _player.HandsController != _primary) return "Weapon/controller changed";
        if (!_transition.Holstering && (_primary.Destroyed || _primary.IsInRemoveOperation())) return "Weapon holstering";
        if (!_player.FirstPersonPointOfView) return "First-person view ended";
        if (_player.CustomAnimationsAreProcessing) return "Native custom animation owns the hands";
        if (_player.IsInventoryOpened) return "Inventory opened";
        if (_primary.IsInReloadOperation()) return "Primary reload started";
        if (_primary.IsInInteraction()) return "Native hands interaction started";
        if (_secondary == null) return "Offhand unavailable";
        if (!WeaponCompatibility.IsSupportedMode(_primary.Item.SelectedFireMode) || !WeaponCompatibility.IsSupportedMode(_secondary.SelectedFireMode))
            return "Paired fire mode changed to an unsupported mechanism";
        return null;
    }

    private void LateUpdate()
    {
        // Explicit diagnostics only: sample large user look changes at most 4/s.
        if (_settings.Diagnostics.Value && _player != null && _handPose != null && Time.unscaledTime >= _diagnosticPoseAt)
        {
            Quaternion look = _player.TargetHandsRotation;
            if (Quaternion.Angle(_diagnosticLook, look) >= 15f)
            { _diagnosticLook = look; _diagnosticPoseAt = Time.unscaledTime + .25f; _poseReportPending = true; }
        }
        // Formatting and logger I/O are deliberately outside FireOnce/TryFire and
        // the native damage callbacks. Work is bounded, including under overflow.
        DrainDiagnostics();
    }

    private void OnDestroy()
    {
        CancelReload("Plugin unloaded");
        Unpair("Plugin unloaded");
        _harmony?.UnpatchSelf();
        GameWorld.OnDispose -= OnWorldDisposed;
        _holsterPairing?.Dispose();
        _cooldowns.Clear();
        _pairBarrier.Reset();
        _pairBarrierOwner = null;
        _pairBarrierManager = null;
        DamageDiagnostics.Clear();
        Instance = null;
    }
}
