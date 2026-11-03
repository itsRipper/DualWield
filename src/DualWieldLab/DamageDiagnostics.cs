using System;
using System.Diagnostics;
using EFT;
using EFT.Ballistics;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace DualWieldLab;

// Optional bounded observations. No damage is modified, no target scans are used,
// and log formatting/I/O belongs to the caller outside the native shot callbacks.
internal static class DamageDiagnostics
{
    private const int Capacity = 64;
    private static readonly Correlation[] Correlations = new Correlation[Capacity];
    private static readonly DamageDiagnosticEvent[] Events = new DamageDiagnosticEvent[Capacity];
    private static int _correlationWrite;
    private static int _read;
    private static int _write;
    private static int _count;
    [ThreadStatic] private static Correlation _active;

    internal static bool Enabled { get; set; }
    internal static int DroppedEvents { get; private set; }
    internal static int ObservationFailures { get; private set; }

    internal static void Install(Harmony harmony)
    {
        var delegateMethod = AccessTools.Method(typeof(GameWorld), nameof(GameWorld.ShotDelegate), new[] { typeof(Shot) });
        harmony.Patch(delegateMethod,
            prefix: new HarmonyMethod(typeof(DamageDiagnostics), nameof(BeforeHit)),
            finalizer: new HarmonyMethod(typeof(DamageDiagnostics), nameof(AfterHit)));
        // Standalone SPT uses ClientLocalGameWorld -> ClientGameWorld, whose
        // override handles impacts directly instead of calling the base method.
        var clientDelegate = AccessTools.Method(typeof(ClientGameWorld), nameof(GameWorld.ShotDelegate), new[] { typeof(Shot) });
        harmony.Patch(clientDelegate,
            prefix: new HarmonyMethod(typeof(DamageDiagnostics), nameof(BeforeHit)),
            finalizer: new HarmonyMethod(typeof(DamageDiagnostics), nameof(AfterHit)));
        var damageMethod = AccessTools.Method(typeof(Player), nameof(Player.ApplyDamageInfo),
            new[] { typeof(DamageInfo), typeof(EBodyPart), typeof(EBodyPartColliderType), typeof(float) });
        harmony.Patch(damageMethod,
            prefix: new HarmonyMethod(typeof(DamageDiagnostics), nameof(BeforeDamage)),
            postfix: new HarmonyMethod(typeof(DamageDiagnostics), nameof(AfterDamage)));
    }

    internal static void ObserveProjectile(Shot shot, long sequence)
    {
        if (!Enabled) return;
        Correlations[_correlationWrite] = new Correlation
        {
            Ammo = shot.Ammo,
            Weapon = shot.Weapon,
            Sequence = sequence,
            Expires = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15L
        };
        _correlationWrite = (_correlationWrite + 1) % Capacity;
    }

    private static void BeforeHit(Shot shotResult, out Correlation __state)
    {
        __state = _active;
        _active = default;
        try
        {
        if (!Enabled || shotResult.IsFlyingOutOfTime) return;
        long now = Stopwatch.GetTimestamp();
        for (int i = 0; i < Capacity; i++)
        {
            Correlation candidate = Correlations[i];
            if (candidate.Expires < now || !ReferenceEquals(candidate.Ammo, shotResult.Ammo) || !ReferenceEquals(candidate.Weapon, shotResult.Weapon)) continue;
            _active = candidate;
            _active.FragmentIndex = shotResult.FragmentIndex;
            Enqueue(new DamageDiagnosticEvent
            {
                Kind = DamageDiagnosticKind.Collision,
                Sequence = candidate.Sequence,
                Ammo = candidate.Ammo,
                Weapon = candidate.Weapon,
                Tick = now,
                HitPoint = shotResult.HitPoint,
                BodyCollider = shotResult.HittedBallisticCollider is BodyPartCollider,
                RequestedDamage = shotResult.Damage,
                PenetrationPower = shotResult.PenetrationPower,
                FragmentIndex = shotResult.FragmentIndex
            });
            break;
        }
        }
        catch { ObservationFailures++; _active = default; }
    }

    // A finalizer restores a nested hit observation even if native code throws.
    // Returning the same exception preserves native behavior.
    private static Exception? AfterHit(Exception? __exception, Correlation __state)
    {
        _active = __state;
        return __exception;
    }

    private static void BeforeDamage(Player __instance, DamageInfo damageInfo,
        EBodyPart bodyPartType, out HealthObservation __state)
    {
        __state = default;
        try
        {
        if (!Enabled || _active.Sequence == 0 || !ReferenceEquals(damageInfo.Weapon, _active.Weapon) || __instance.HealthController == null) return;
        __state = new HealthObservation
        {
            Active = true,
            Correlation = _active,
            CommonBefore = __instance.HealthController.GetBodyPartHealth(EBodyPart.Common).Current,
            PartBefore = __instance.HealthController.GetBodyPartHealth(bodyPartType).Current
        };
        }
        catch { ObservationFailures++; __state = default; }
    }

    private static void AfterDamage(Player __instance, DamageInfo damageInfo,
        EBodyPart bodyPartType, HealthObservation __state)
    {
        try
        {
        if (!__state.Active || __instance.HealthController == null) return;
        Enqueue(new DamageDiagnosticEvent
        {
            Kind = DamageDiagnosticKind.Health,
            Sequence = __state.Correlation.Sequence,
            Ammo = __state.Correlation.Ammo,
            Weapon = __state.Correlation.Weapon,
            Tick = Stopwatch.GetTimestamp(),
            TargetIsAI = __instance.IsAI,
            BodyPart = bodyPartType,
            BodyCollider = true,
            HitPoint = damageInfo.HitPoint,
            RequestedDamage = damageInfo.Damage,
            PenetrationPower = damageInfo.PenetrationPower,
            Penetrated = damageInfo.Penetrated,
            FragmentIndex = __state.Correlation.FragmentIndex,
            HealthBefore = __state.CommonBefore,
            HealthAfter = __instance.HealthController.GetBodyPartHealth(EBodyPart.Common).Current,
            PartHealthBefore = __state.PartBefore,
            PartHealthAfter = __instance.HealthController.GetBodyPartHealth(bodyPartType).Current
        });
        }
        catch { ObservationFailures++; }
    }

    private static void Enqueue(DamageDiagnosticEvent item)
    {
        if (_count == Capacity)
        {
            DroppedEvents++;
            return;
        }
        Events[_write] = item;
        _write = (_write + 1) % Capacity;
        _count++;
    }

    internal static bool TryDequeue(out DamageDiagnosticEvent item)
    {
        if (_count == 0) { item = default; return false; }
        item = Events[_read];
        Events[_read] = default;
        _read = (_read + 1) % Capacity;
        _count--;
        return true;
    }

    internal static void Clear()
    {
        Array.Clear(Correlations, 0, Capacity);
        Array.Clear(Events, 0, Capacity);
        _active = default;
        _read = _write = _count = _correlationWrite = DroppedEvents = ObservationFailures = 0;
    }

    private struct Correlation
    {
        internal Item? Ammo;
        internal Item? Weapon;
        internal long Sequence;
        internal long Expires;
        internal int FragmentIndex;
    }

    private struct HealthObservation
    {
        internal bool Active;
        internal Correlation Correlation;
        internal float CommonBefore;
        internal float PartBefore;
    }
}

internal enum DamageDiagnosticKind { Collision, Health }

internal struct DamageDiagnosticEvent
{
    internal DamageDiagnosticKind Kind;
    internal long Sequence;
    internal Item? Ammo;
    internal Item? Weapon;
    internal long Tick;
    internal Vector3 HitPoint;
    internal bool BodyCollider;
    internal bool TargetIsAI;
    internal EBodyPart BodyPart;
    internal float RequestedDamage;
    internal float PenetrationPower;
    internal bool Penetrated;
    internal int FragmentIndex;
    internal float HealthBefore;
    internal float HealthAfter;
    internal float PartHealthBefore;
    internal float PartHealthAfter;
}
