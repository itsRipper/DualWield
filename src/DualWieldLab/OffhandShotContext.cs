using System;
using System.Diagnostics;
using System.Threading;
using Diz.Utils;
using EFT;
using EFT.AssetsManager;
using EFT.Ballistics;
using EFT.InventoryLogic;
using UnityEngine;

namespace DualWieldLab;

// An isolated native shot context, never installed as Player.HandsController. It deliberately
// does not run EFT's single-hands Setup/Spawn/Destroy paths; the native magazine-pistol ammo
// transaction and Shot path are shared.
internal sealed class OffhandShotContext : Player.FirearmController
{

    private Player.FirearmController _primary = null!;
    private Weapon _primaryWeapon = null!;
    private FireOperationBase _shotOperation = null!;
    private OffhandSoundPlayer? _ownedSound;
    private bool _ready;
    private bool _faulted;
    private bool _firing;
    private bool _diagnosticsActive;
    private OffhandShotMetrics _metrics;
    private int _projectileCount;
    private long _sequence;
    private static long _nextSequence;
    private Vector3 _projectileOrigin;
    private Vector3 _projectileDirection;
    private float _projectileDamage;
    private float _projectilePenetration;
    private string _projectileAmmoTemplate = "";
    internal int LastProjectileCount => _projectileCount;
    internal bool MutationStarted { get; private set; }

    internal static bool IsEligible(Weapon weapon)
    {
        return WeaponCompatibility.IsEligible(weapon, allowMalfunction: true);
    }

    // pointOfView: FirstPerson for the local player's own left pistol; ThirdPerson for the left
    // pistol of an observed player, whose shots are replayed from the network (ReplayRemoteShot).
    internal void Initialize(Player player, Weapon weapon,
        Player.FirearmController primary, WeaponPrefab prefab, Transform fireport,
        EPointOfView pointOfView = EPointOfView.FirstPerson)
    {
        if (_ready || _faulted)
            throw new InvalidOperationException("Shot context cannot be reused.");
        if (player == null || primary == null || prefab == null || fireport == null)
            throw new ArgumentNullException("An offhand shot dependency is missing.");
        if (!IsEligible(weapon) || !IsEligible(primary.Item))
            throw new InvalidOperationException("Both weapons must pass the shared magazine-fed pistol compatibility checks.");
        if (ReferenceEquals(weapon, primary.Item) || weapon.Id == primary.Item.Id)
            throw new InvalidOperationException("The two weapons must have distinct identities.");
        if (player.HandsController != primary)
            throw new InvalidOperationException("The primary is no longer the active hands controller.");

        // The model owner must assemble with player:null and call Parent(null).
        // Neither CreateController nor either native Setup method is safe here.
        enabled = false;
        _player = player;
        _item = weapon;
        _primary = primary;
        _primaryWeapon = primary.Item;
        _controllerObject = prefab.gameObject;
        _weaponPrefab = prefab;
        _handsHierarchy = prefab.Hierarchy;
        _firearmsAnimator = prefab.FirearmsAnimator;
        _objectInHandsAnimator = _firearmsAnimator;
        AnimationEventsEmitter = prefab.AnimationEventsEmitter;
        Firearms = prefab.ObjectInHands as Firearms;
        if (Firearms == null || _firearmsAnimator == null || _handsHierarchy == null)
            throw new InvalidOperationException("The offhand prefab has no native firearm rig.");

        var poolObject = prefab.GetComponent<AssetPoolObject>();
        CCV = poolObject != null ? poolObject.ContainerCollectionView : null;
        if (CCV == null)
            throw new InvalidOperationException("The offhand model has no native container view.");
        if (primary.BallisticsCalculator == null)
            throw new InvalidOperationException("The primary ballistic calculator is unavailable.");

        Fireport = new BifacialTransform { Original = fireport };
        MultiBarrelsFireports = Array.Empty<BifacialTransform>();
        GunBaseTransform = _handsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root_anim);
        WeaponRoot = _handsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        WeaponLn = GunBaseTransform != null
            ? Vector3.Distance(GunBaseTransform.position, fireport.position) : 0f;
        IsAIShootFromEyes = () => false;
        IsNoOffsetShooting = () => false;
        _buffInfo = player.Skills.GetWeaponInfo(weapon);
        _malfRandoms = player.MalfRandoms;
        _deferredErgonomic = new Deferred<float>(TotalErgonomicsFunc);
        _deferredErgonomicWeight = new Deferred<float>(ErgonomicWeightFunc);
        float_3 = weapon.GetTotalCenterOfImpact(includeAmmo: false);
        float_4 = weapon.TotalShotgunDispersion;
        BallisticsCalculator = primary.BallisticsCalculator;

        // This initializes only the offhand model's effects and controllers.
        // Do not call PWA.InitWeaponData, PWA.InitTransforms, SetArmsAnimatorCommon,
        // Firearms.InitStreamingAnimators, or add this as an animation consumer.
        Firearms.Player = player;
        Firearms.AfterGetFromPoolInit(player.ProceduralWeaponAnimation, CCV, true);
        Firearms.SetFireport(Fireport);
        Firearms.SetPointOfView(pointOfView);
        UpdateIsWithSilencer();
        var sourceSound = prefab.GetComponent<WeaponSoundPlayer>();
        if (sourceSound == null || sourceSound.Body == null)
            throw new InvalidOperationException("The offhand model has no native gunshot sound bank.");
        if (!sourceSound.Non_auto && sourceSound.Tail == null)
            throw new InvalidOperationException("The native loop-based pistol audio has no release-tail bank; refusing before ammunition is consumed.");
        // A dedicated audio component owns its subscriptions. The pooled model's
        // component remains uninitialized and can be returned without leaking a
        // bridge to this temporary context.
        _ownedSound = gameObject.AddComponent<OffhandSoundPlayer>();
        _weaponSoundPlayer = _ownedSound;
        _weaponSoundPlayer.Body = sourceSound.Body;
        _weaponSoundPlayer.Tail = sourceSound.Tail;
        _weaponSoundPlayer.Doublet = sourceSound.Doublet;
        _weaponSoundPlayer.BodySilenced = sourceSound.BodySilenced;
        _weaponSoundPlayer.TailSilenced = sourceSound.TailSilenced;
        _weaponSoundPlayer.DoubletSilenced = sourceSound.DoubletSilenced;
        _weaponSoundPlayer.Non_auto = sourceSound.Non_auto;
        // The weapon's own handling banks (SndMagOut, SndSlideIn, ...) for reload events.
        _weaponSoundPlayer.MainSounds = sourceSound.MainSounds;
        _weaponSoundPlayer.AdditionalSounds = sourceSound.AdditionalSounds;
        _weaponSoundPlayer.Init(this, CurrentFireport, player);
        // The left pistol's own suppressor selects its own native silenced banks.
        _weaponSoundPlayer.IsSilenced = IsSilenced;

        // Native setup ends with this logger; InitiateOperation traces every state change
        // through it and throws when it is missing.
        Logger = new HandsControllerLogger(LoggerMode.Add, this);
        _shotOperation = new FireOperationBase(this);
        // A pistol paired while malfunctioned shows the state native StartMalfunction leaves.
        if (weapon.MalfState.State != Weapon.EMalfunctionState.None)
        {
            _firearmsAnimator.SetLayerWeight(FirearmsAnimator.MALFUNCTION_LAYER_INDEX, 1);
            _firearmsAnimator.Malfunction(-1);
        }
        _ready = true;
    }

    internal OffhandShotResult FireOnce(long inputTimestamp, bool diagnosticsEnabled, bool holdAutomaticTrigger = false)
    {
        MutationStarted = false;
        _diagnosticsActive = diagnosticsEnabled;
        _metrics = default;
        _metrics.InputTicks = inputTimestamp;
        if (diagnosticsEnabled) _metrics.EntryTicks = Stopwatch.GetTimestamp();
        _projectileCount = 0;
        if (!_ready || _faulted || _firing)
            throw new InvalidOperationException("Offhand controller context unavailable, faulted or reentrant; this is an integration error, not a weapon malfunction.");
        if (_player.HandsController != _primary || _primary.Item != _primaryWeapon)
            throw new InvalidOperationException("Weapon switching invalidated the offhand context.");
        if (!WeaponCompatibility.IsSupportedMode(Item.SelectedFireMode))
            throw new InvalidOperationException("Offhand fire mode changed to an unsupported mechanism; no ammunition consumed.");
        if (Item.MalfState.State != Weapon.EMalfunctionState.None)
            throw new InvalidOperationException("Clear the offhand malfunction through ordinary native handling.");

        var chamber = Item.Chambers[0];
        var ammo = chamber.ContainedItem as Ammo;
        int primaryBefore = LiveAmmo(_primaryWeapon);
        int offhandBefore = LiveAmmo(Item);
        if (ammo == null || ammo.IsUsed)
        {
            ReleaseTrigger();
            return OffhandShotResult.Dry(primaryBefore, offhandBefore);
        }
        if (ammo.ProjectileCount != 1 || !(ammo.InitialSpeed > 0f))
            throw new InvalidOperationException("Only live single-projectile pistol ammunition is supported.");
        // EFT's firing routine ignores the chamber removal return value.
        // Validate it before handing control to that routine.
        if (!chamber.RemoveItem(simulate: true).Succeeded)
            throw new InvalidOperationException("Native chamber removal preflight failed.");

        _firing = true;
        _sequence = Interlocked.Increment(ref _nextSequence);
        try
        {
            if (diagnosticsEnabled) _metrics.AmmoStartTicks = Stopwatch.GetTimestamp();
            MutationStarted = true;
            var shot = _shotOperation.ProcessInventoryOperationsForSingleshotWithMalf(out var malfunction, out var source);
            if (diagnosticsEnabled) _metrics.AmmoEndTicks = Stopwatch.GetTimestamp();
            bool failedFeed = shot.FedAmmo != null;
            int expectedDelta = failedFeed ? 2 : 1;
            int primaryAfter = LiveAmmo(_primaryWeapon);
            int offhandAfter = LiveAmmo(Item);
            if (!ReferenceEquals(shot.AmmoToFire, ammo)
                || primaryAfter != primaryBefore
                || offhandAfter != offhandBefore - expectedDelta)
                throw new InvalidOperationException("Native offhand ammo invariant failed; no projectile emitted.");

            if (malfunction != Weapon.EMalfunctionState.None)
            {
                // Preserve the payload expected by native equip/repair. Selection,
                // chamber removal and feed handling above are entirely native.
                Item.MalfState.State = malfunction;
                Item.MalfState.LastMalfunctionTime = GameHelper.PastTime;
                Item.MalfState.AmmoToFire = shot.AmmoToFire;
                Item.MalfState.AmmoWillBeLoadedToChamber = shot.AmmoWillBeLoadedToChamber;
                Item.MalfState.MalfunctionedAmmo = shot.FedAmmo ?? shot.AmmoToFire;
                _malfunction = true;
                if (_player.Skills.TroubleFixingDurElite.Value)
                    Item.MalfState.AddMalfReduceChance(_player.ProfileId, source);
                _player.InventoryController.ExamineMalfunction(Item, clearRest: true);
                if (malfunction == Weapon.EMalfunctionState.Misfire)
                {
                    // Native Start restores the unspent misfire round flag and
                    // invokes ShotMisfired; it emits no projectile.
                    SwitchToNativeMalfunction(malfunction);
                    ReleaseTrigger();
                    if (diagnosticsEnabled) _metrics.CompletedTicks = Stopwatch.GetTimestamp();
                    return new OffhandShotResult(false, primaryBefore, primaryAfter,
                        offhandBefore, offhandAfter, ammo.Id.ToString(), Fireport.position,
                        _sequence, 0, _metrics,
                        malfunction: malfunction);
                }
            }

            // Native Shot applies this weapon's wear/heat, spread, real projectiles,
            // weapon identity, muzzle effects, statistics, and its own sound player.
            // No primary fields or inventory items are temporarily replaced.
            IsTriggerPressed = true;
            if (diagnosticsEnabled) _metrics.NativeShotStartTicks = Stopwatch.GetTimestamp();
            Shot(Item, shot.AmmoToFire, 0);
            if (diagnosticsEnabled) _metrics.NativeShotEndTicks = Stopwatch.GetTimestamp();
            if (_projectileCount != 1)
                throw new InvalidOperationException("Expected exactly one registered native offhand projectile; stop and inspect the audit.");
            int finalPrimary = LiveAmmo(_primaryWeapon);
            int finalOffhand = LiveAmmo(Item);
            if (finalPrimary != primaryBefore || finalOffhand != offhandBefore - expectedDelta)
                throw new InvalidOperationException("Post-shot ammo invariant failed; stop and inspect the audit.");
            // Jam, feed and slide failures follow the fired round, as in native FireOperation.
            if (malfunction != Weapon.EMalfunctionState.None) SwitchToNativeMalfunction(malfunction);
            if (diagnosticsEnabled) _metrics.CompletedTicks = Stopwatch.GetTimestamp();
            return new OffhandShotResult(true, primaryBefore, finalPrimary,
                offhandBefore, finalOffhand, shot.AmmoToFire.Id.ToString(),
                Fireport.position, _sequence, _projectileCount, _metrics,
                _projectileOrigin, _projectileDirection, _projectileDamage, _projectilePenetration, _projectileAmmoTemplate, malfunction);
        }
        catch
        {
            // Consumption may already have occurred. Retrying could duplicate a
            // projectile or consume another round. The owner must deactivate.
            _faulted = MutationStarted;
            throw;
        }
        finally
        {
            // Native loop audio spans a held burst. Single shots/faults stop.
            if (_faulted || Item.MalfState.State != Weapon.EMalfunctionState.None || !holdAutomaticTrigger) ReleaseTrigger();
            _firing = false;
            _diagnosticsActive = false;
        }
    }

    internal void ReleaseTrigger()
    {
        if (IsTriggerPressed) IsTriggerPressed = false;
    }

    public override void InitiateShot(IWeapon weapon, Ammo ammo, Vector3 shotPosition,
        Vector3 shotDirection, Vector3 fireportPosition, int chamberIndex, float overheat)
    {
        if (_diagnosticsActive) _metrics.InitiateTicks = Stopwatch.GetTimestamp();
        // Player.OnMakingShot classifies the AI noise (gun / silencedGun) by
        // HandsController.IsSilenced, i.e. the right pistol. For this shot it reports the
        // left pistol's own suppressor, and is restored before anything else can read it.
        bool primarySilenced = _primary.IsSilenced;
        _primary.IsSilenced = IsSilenced;
        try { base.InitiateShot(weapon, ammo, shotPosition, shotDirection, fireportPosition, chamberIndex, overheat); }
        finally { _primary.IsSilenced = primarySilenced; }
    }

    // A shot the owning machine fired with this pistol, shown on an observed player: the same
    // native InitiateShot an observed hands controller runs for its own replicated shots
    // (projectile, AI noise, muzzle effects of this model) and the native shot sound. Rounds,
    // heat and wear come from the owner's state snapshot, never from this replay.
    internal void ReplayRemoteShot(Ammo ammo, Vector3 position, Vector3 direction, float overheat)
    {
        if (!_ready || _faulted || _firing) return;
        _replaying = true;
        try
        {
            InitiateShot(Item, ammo, position, direction, Fireport.position, 0, overheat);
            PlayFireBulletSound(_weaponSoundPlayer, ammo, position, direction, false);
            _firearmsAnimator.SetFire(true);
            if (Item.MalfState.State == Weapon.EMalfunctionState.None)
                _firearmsAnimator.Animator.Play(Item.SelectedFireMode == Weapon.EFireMode.semiauto
                    ? _firearmsAnimator.FullSemiFireStateName : _firearmsAnimator.FullFireStateName, 1, .2f);
        }
        finally { _replaying = false; }
    }

    internal void ResetFire()
    {
        if (_ready) _firearmsAnimator.SetFire(false);
    }

    private bool _replaying;

    public override void RegisterShot(Item weapon, Shot shot)
    {
        if (_replaying) return;
        // Native base RegisterShot is empty in the inspected build. This only
        // observes the already-created projectile; it never creates another.
        if (!_firing || !ReferenceEquals(weapon, Item) || !ReferenceEquals(shot.Weapon, Item))
            throw new InvalidOperationException("Native projectile weapon attribution did not match the offhand item.");
        _projectileCount++;
        _projectileOrigin = shot.MasterOrigin;
        _projectileDirection = shot.Direction;
        _projectileDamage = shot.Damage;
        _projectilePenetration = shot.PenetrationPower;
        _projectileAmmoTemplate = shot.Ammo.StringTemplateId;
        if (_metrics.FirstProjectileTicks == 0) _metrics.FirstProjectileTicks = Stopwatch.GetTimestamp();
        if (_diagnosticsActive)
        {
            DamageDiagnostics.ObserveProjectile(shot, _sequence);
        }
    }

    internal static int LiveAmmo(Weapon weapon)
    {
        return weapon.ChamberAmmoCount + weapon.GetCurrentMagazineCount();
    }

    internal enum NativeCheck { Examine, Chamber, Magazine, Reload, FireMode }
    internal enum NativeCheckResult { Refused, Notified, Started }

    // Native malfunction, examine and check operations run on this context with the left
    // pistol as Controller.Item. Their animation events come from the left model's own
    // emitter, which is only drained into this controller while such an operation runs.
    internal bool NativeActionActive => _nativeAction;
    internal float NativeActionSeconds => _nativeAction ? Time.unscaledTime - _nativeStarted : 0f;
    internal string NativeOperationName => CurrentHandsOperation?.GetType().Name ?? "none";
    private bool _nativeAction, _eventsAttached;
    private float _nativeStarted;

    // Native SwitchToMalfunction for the left pistol after its own shot decided the state:
    // AutomaticFireOperation plays the malfunction state, FireOperation (single/semi) lets
    // the Malfunction parameter leave the fire state it is playing.
    private void SwitchToNativeMalfunction(Weapon.EMalfunctionState state)
    {
        _firearmsAnimator.SetFire(false);
        _firearmsAnimator.Malfunction((int)state);
        autoFireOn = false;
        BeginNativeAction();
        string? clip = Item.SelectedFireMode != Weapon.EFireMode.fullauto
            ? (Item.SelectedFireMode == Weapon.EFireMode.semiauto ? _firearmsAnimator.FullSemiFireStateName : _firearmsAnimator.FullFireStateName)
            : state switch
            {
                Weapon.EMalfunctionState.Misfire => "MISFIRE",
                Weapon.EMalfunctionState.Jam => "JAM",
                Weapon.EMalfunctionState.HardSlide => "HARD_SLIDE",
                Weapon.EMalfunctionState.SoftSlide => "SOFT_SLIDE",
                Weapon.EMalfunctionState.Feed => "FEED",
                _ => null
            };
        if (clip != null) _firearmsAnimator.Animator.Play(clip, 1, Item.SelectedFireMode == Weapon.EFireMode.fullauto ? 0f : .2f);
        InitiateOperation<StartMalfunction>().Start();
    }

    // The same Idling entry points the native input translator calls for L, Shift+T, Alt+T and Alt+B.
    // Refused: native rules declined. Notified: knowledge/notification only, no animation.
    internal NativeCheckResult TryNativeCheck(NativeCheck check)
    {
        if (!_ready || _faulted || _firing || _nativeAction || check == NativeCheck.Reload) return NativeCheckResult.Refused;
        Idling idle = CurrentHandsOperation as Idling ?? ParkedIdle();
        BeginNativeAction();
        bool accepted;
        try
        {
            accepted = check == NativeCheck.Examine ? idle.ExamineWeapon()
                : check == NativeCheck.Chamber ? idle.CheckChamber()
                : check == NativeCheck.FireMode ? idle.CheckFireMode() : idle.CheckAmmo();
        }
        catch { EndNativeAction(); throw; }
        if (!ReferenceEquals(CurrentHandsOperation, idle)) return NativeCheckResult.Started;
        EndNativeAction();
        return accepted ? NativeCheckResult.Notified : NativeCheckResult.Refused;
    }

    // Events of the left pistol's native magazine reload (FirearmController's OnMagOut,
    // OnMagHide, OnMagShow, OnMagIn, OnAddAmmoInChamber, bolt catch and OnIdleStart).
    internal interface IReloadEvents
    {
        void MagOut();
        void MagHide();
        void MagShow();
        void MagIn();
        void AddAmmoInChamber();
        // Native OnIdleStartEvent ends the reload only once the new magazine appeared.
        bool IdleStart();
    }

    private IReloadEvents? _reloadEvents;
    internal bool NativeReloadActive => _reloadEvents != null && _nativeAction;

    // ReloadExternalMagOperation.Start's animator calls on the left pistol's own animator,
    // without that operation: the magazine moves through the backpack-free native
    // inventory transaction, which the caller synchronises with these events.
    internal bool BeginNativeReload(IReloadEvents events, Magazine? current, Magazine next)
    {
        if (!_ready || _faulted || _firing || _nativeAction || events == null || next == null) return false;
        if (!(CurrentHandsOperation is Idling)) ParkedIdle();
        _reloadEvents = events;
        BeginNativeAction();
        _firearmsAnimator.SetFire(false);
        _firearmsAnimator.SetAmmoCompatible(true);
        _firearmsAnimator.SetIsExternalMag(true);
        _firearmsAnimator.SetCanReload(true);
        _firearmsAnimator.Reload(current?.magAnimationIndex ?? -1, next.magAnimationIndex, false);
        if (Item.IsBoltCatch && Item.ChamberAmmoCount == 1 && !Item.ManualBoltCatch
            && !Item.MustBoltBeOpennedForExternalReload && !Item.MustBoltBeOpennedForInternalReload)
            _firearmsAnimator.SetBoltCatch(false);
        return true;
    }

    // Native OnMagPulledOutFromWeapon / OnMagInsertedToWeapon / OnAddAmmoInChamber
    // animator state for the magazine and chamber the transaction actually produced.
    internal void SetNativeMagazineState(bool inWeapon, Magazine? magazine)
    {
        if (!_ready) return;
        _firearmsAnimator.SetMagInWeapon(inWeapon);
        if (!inWeapon || magazine == null) return;
        _firearmsAnimator.SetAmmoOnMag(magazine.Count);
        _firearmsAnimator.SetMagTypeCurrent(magazine.magAnimationIndex);
    }

    internal void SetNativeChamberState()
    {
        if (!_ready) return;
        _firearmsAnimator.SetAmmoInChamber(Item.ChamberAmmoCount);
        if (Item.ChamberAmmoCount > 0) _firearmsAnimator.SetBoltCatch(false);
    }

    // From inside the emitter's dispatch (OnIdleStart): like ParkIdle, the rest of the
    // batch is dropped and PumpEvents detaches the consumer after the dispatch returns.
    private void CompleteNativeReload()
    {
        _firearmsAnimator.SetCanReload(false);
        _nativeAction = false;
        AnimationEventsEmitter?._delayedEvents.Clear();
    }

    public override void IEventsConsumerOnMagOut()
    { if (_reloadEvents != null) _reloadEvents.MagOut(); else base.IEventsConsumerOnMagOut(); }

    public override void IEventsConsumerOnMagHide()
    { if (_reloadEvents != null) _reloadEvents.MagHide(); else base.IEventsConsumerOnMagHide(); }

    public override void IEventsConsumerOnMagShow()
    { if (_reloadEvents != null) _reloadEvents.MagShow(); else base.IEventsConsumerOnMagShow(); }

    public override void IEventsConsumerOnMagIn()
    { if (_reloadEvents != null) _reloadEvents.MagIn(); else base.IEventsConsumerOnMagIn(); }

    public override void IEventsConsumerOnAddAmmoInChamber()
    { if (_reloadEvents != null) _reloadEvents.AddAmmoInChamber(); else base.IEventsConsumerOnAddAmmoInChamber(); }

    public override void IEventsConsumerOnOffBoltCatch()
    { if (_reloadEvents != null) _firearmsAnimator.SetBoltCatch(false); else base.IEventsConsumerOnOffBoltCatch(); }

    public override void IEventsConsumerOnOnBoltCatch()
    { if (_reloadEvents != null) _firearmsAnimator.SetBoltCatch(true); else base.IEventsConsumerOnOnBoltCatch(); }

    public override void IEventsConsumerOnIdleStart()
    {
        var events = _reloadEvents;
        if (events == null) { base.IEventsConsumerOnIdleStart(); return; }
        if (events.IdleStart()) CompleteNativeReload();
    }

    // Native Idling.ChangeFireMode for the left pistol: its own FireModeComponent, its own
    // animator selector state and the native fire-mode display.
    internal bool ChangeFireModeNative(Weapon.EFireMode mode)
    {
        if (!_ready || _faulted || _firing || _nativeAction) return false;
        Idling idle = CurrentHandsOperation as Idling ?? ParkedIdle();
        return idle.ChangeFireMode(mode);
    }

    // Native Idling.ShowFireMode: EFT's fire-mode display with the left pistol's mode.
    internal void ShowNativeFireMode()
    {
        if (!_ready || _faulted) return;
        (CurrentHandsOperation as Idling ?? ParkedIdle()).ShowFireMode();
    }

    // Native Idling.SetTriggerPressed on a malfunctioned weapon: no shot; the player is told.
    internal void PressMalfunctionedTrigger()
    {
        if (!_ready || Item.MalfState.State == Weapon.EMalfunctionState.None) return;
        _firearmsAnimator.MisfireSlideUnknown(val: false);
        _player.InventoryController.ExamineMalfunction(Item);
    }

    // Harmony prefix target: native Idling.Start writes the player's shared procedural
    // weapon animation for the held weapon, so the left pistol only records the state.
    // Usually reached from inside the emitter's dispatch: the rest of that batch is
    // dropped and this consumer detaches after the dispatch returns.
    internal void ParkIdle(Idling idle, Action? callback)
    {
        idle.State = Player.EOperationState.Executing;
        _nativeAction = false;
        AnimationEventsEmitter?._delayedEvents.Clear();
        callback?.Invoke();
    }

    internal void PumpEvents()
    {
        if (!_ready || AnimationEventsEmitter == null) return;
        if (_nativeAction) AnimationEventsEmitter.EmitEvents();
        else AnimationEventsEmitter._delayedEvents.Clear();
        if (!_nativeAction) EndNativeAction();
    }

    // A clip whose events never arrive (or an unpair mid-operation) must not strand the
    // weapon: the operation's own native completion applies the state it committed to.
    internal void FinishNativeAction()
    {
        if (!_nativeAction) return;
        if (_reloadEvents != null)
        {
            // ReloadExternalMagOperation.FastForward's animator end; the transaction is the caller's.
            _firearmsAnimator.Animator.Play(_firearmsAnimator.FullIdleStateName, 1, .1f);
            CompleteNativeReload();
            EndNativeAction();
            return;
        }
        try
        {
            if (CurrentHandsOperation is UtilityOperation utility)
            { utility.OnUtilityOperationStartEvent(); utility.OnIdleStartEvent(); }
            else CurrentOperation?.FastForward();
        }
        finally
        {
            if (_nativeAction) { EndNativeAction(); if (!(CurrentHandsOperation is Idling)) ParkedIdle(); }
        }
    }

    private Idling ParkedIdle()
    {
        var idle = InitiateOperation<Idling>();
        idle.State = Player.EOperationState.Executing;
        return idle;
    }

    private void BeginNativeAction()
    {
        AnimationEventsEmitter._delayedEvents.Clear();
        if (!_eventsAttached) { _firearmsAnimator.AddEventsConsumer(this); _eventsAttached = true; }
        _nativeAction = true;
        _nativeStarted = Time.unscaledTime;
    }

    private void EndNativeAction()
    {
        if (_eventsAttached) { _firearmsAnimator.RemoveEventsConsumer(this); _eventsAttached = false; }
        _nativeAction = false;
        _reloadEvents = null;
    }

    // Same lookup a native animation OnSound event uses; unknown names are silent.
    internal bool PlayHandlingSound(string eventName)
    {
        if (!_ready || _ownedSound == null) return false;
        bool known = _ownedSound.AdditionalSounds.Exists(s => s.EventName == eventName || s.EventName == "Snd" + eventName);
        if (known) _ownedSound.SoundEventHandler(eventName);
        return known;
    }

    // Interrupted handling: return the clip source so a cut-off action stops sounding.
    internal void StopHandlingSounds()
    {
        if (_ready) _ownedSound?.ReleaseClipsSource();
    }

    internal void DisposeContext()
    {
        try { if (_ready) FinishNativeAction(); }
        finally
        {
            EndNativeAction();
            _ready = false;
            ReleaseTrigger();
            _shotOperation = null!;
            try
            {
                // Detach synchronously before the owner returns the model to its
                // pool. Unity's deferred Object.Destroy would leave a brief race.
                _ownedSound?.Shutdown();
            }
            finally
            {
                // Native Destroy would clear the primary player's global PWA.
                BallisticsCalculator = null;
                CompositeDisposable.Dispose();
            }
        }
    }

    public override void Destroy()
    {
        DisposeContext();
    }

    // The normal player update loop does not own this context. These overrides
    // also guard against an external caller invoking inherited hands updates.
    public override void ManualUpdate(float deltaTime) { }
    public override void ManualLateUpdate(float deltaTime) { }
    public override void OnPlayerDead() { DisposeContext(); }
    public override void OnGameSessionEnd() { DisposeContext(); }
}

// Owns subscriptions independently of the reusable pooled model.
internal sealed class OffhandSoundPlayer : WeaponSoundPlayer
{
    private bool _shutdown;

    internal void Shutdown()
    {
        if (_shutdown)
            return;
        _shutdown = true;
        enabled = false;
        try
        {
            StopSoundCoroutine();
            Release();
        }
        finally
        {
            // Native teardown removes animator and movement-context listeners
            // and disposes the bridge. Call it exactly once, including when
            // initialization stopped after creating only part of the bridge.
            base.OnDestroy();
        }
    }

    public override void OnDisable()
    {
        if (!_shutdown)
            base.OnDisable();
    }

    public override void OnDestroy()
    {
        Shutdown();
    }
}

internal readonly struct OffhandShotResult
{
    internal bool Fired { get; }
    internal int PrimaryBefore { get; }
    internal int PrimaryAfter { get; }
    internal int OffhandBefore { get; }
    internal int OffhandAfter { get; }
    internal string AmmoId { get; }
    internal Vector3 MuzzlePosition { get; }
    internal long Sequence { get; }
    internal int ProjectileCount { get; }
    internal OffhandShotMetrics Metrics { get; }
    internal Vector3 ProjectileOrigin { get; }
    internal Vector3 ProjectileDirection { get; }
    internal float ProjectileDamage { get; }
    internal float ProjectilePenetration { get; }
    internal string AmmoTemplate { get; }
    internal Weapon.EMalfunctionState Malfunction { get; }

    internal OffhandShotResult(bool fired, int primaryBefore, int primaryAfter,
        int offhandBefore, int offhandAfter, string ammoId, Vector3 muzzlePosition,
        long sequence = 0, int projectileCount = 0, OffhandShotMetrics metrics = default,
        Vector3 projectileOrigin = default, Vector3 projectileDirection = default,
        float projectileDamage = 0f, float projectilePenetration = 0f, string ammoTemplate = "",
        Weapon.EMalfunctionState malfunction = Weapon.EMalfunctionState.None)
    {
        Fired = fired;
        PrimaryBefore = primaryBefore;
        PrimaryAfter = primaryAfter;
        OffhandBefore = offhandBefore;
        OffhandAfter = offhandAfter;
        AmmoId = ammoId;
        MuzzlePosition = muzzlePosition;
        Sequence = sequence;
        ProjectileCount = projectileCount;
        Metrics = metrics;
        ProjectileOrigin = projectileOrigin;
        ProjectileDirection = projectileDirection;
        ProjectileDamage = projectileDamage;
        ProjectilePenetration = projectilePenetration;
        AmmoTemplate = ammoTemplate;
        Malfunction = malfunction;
    }

    internal static OffhandShotResult Dry(int primary, int offhand)
    {
        return new OffhandShotResult(false, primary, primary, offhand, offhand,
            string.Empty, Vector3.zero);
    }
}

internal struct OffhandShotMetrics
{
    internal long InputTicks;
    internal long EntryTicks;
    internal long AmmoStartTicks;
    internal long AmmoEndTicks;
    internal long NativeShotStartTicks;
    internal long InitiateTicks;
    internal long FirstProjectileTicks;
    internal long NativeShotEndTicks;
    internal long CompletedTicks;

    internal static double Milliseconds(long start, long end)
        => start == 0 || end == 0 ? 0d : (end - start) * 1000d / Stopwatch.Frequency;
}
