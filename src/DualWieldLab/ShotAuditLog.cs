using System;
using System.Diagnostics;
using BepInEx.Logging;

namespace DualWieldLab;

internal struct ShotAudit
{
    internal OffhandShotResult Result;
    internal long InputTicks;
    internal double RemainingMs;
    internal double Rpm;
    internal string? FireMode;
    internal bool Cooldown;
    internal bool PrimaryFiring;
    internal string? WeaponId;
}

// Bounded per-shot audit. Queue runs inside the shot path and only copies a struct; formatting
// and logger I/O happen later in Drain, outside FireOnce and the native damage callbacks.
internal sealed class ShotAuditLog
{
    private readonly ShotAudit[] _entries = new ShotAudit[64];
    private readonly ManualLogSource _logger;
    private int _read, _write, _count, _dropped;

    internal ShotAuditLog(ManualLogSource logger) => _logger = logger;

    internal void Queue(ShotAudit audit)
    {
        if (_count == _entries.Length) { _dropped++; return; }
        _entries[_write] = audit;
        _write = (_write + 1) % _entries.Length;
        _count++;
    }

    internal void Drain(int maxEntries)
    {
        for (int i = 0; i < maxEntries && _count > 0; i++)
        {
            ShotAudit a = _entries[_read];
            _entries[_read] = default;
            _read = (_read + 1) % _entries.Length;
            _count--;
            if (a.Cooldown)
            {
                _logger.LogInfo(FormattableString.Invariant($"DW_INPUT t={a.InputTicks} outcome=cooldown remaining_ms={a.RemainingMs:F3} effective_rpm={a.Rpm:F3}"));
                continue;
            }
            OffhandShotResult r = a.Result;
            OffhandShotMetrics m = r.Metrics;
            _logger.LogInfo(FormattableString.Invariant($"DW_SHOT seq={r.Sequence} input_t={a.InputTicks} projectile_t={m.FirstProjectileTicks} frequency={Stopwatch.Frequency} fired={r.Fired} projectiles={r.ProjectileCount} effective_rpm={a.Rpm:F3} fire_mode={a.FireMode} interval_ms={PairPolicy.ShotInterval(a.Rpm)*1000:F3} primary_trigger={a.PrimaryFiring} input_to_entry_ms={OffhandShotMetrics.Milliseconds(m.InputTicks,m.EntryTicks):F3} preflight_ms={OffhandShotMetrics.Milliseconds(m.EntryTicks,m.AmmoStartTicks):F3} ammo_ms={OffhandShotMetrics.Milliseconds(m.AmmoStartTicks,m.AmmoEndTicks):F3} input_to_projectile_ms={OffhandShotMetrics.Milliseconds(m.InputTicks,m.FirstProjectileTicks):F3} native_shot_ms={OffhandShotMetrics.Milliseconds(m.NativeShotStartTicks,m.NativeShotEndTicks):F3} total_ms={OffhandShotMetrics.Milliseconds(m.InputTicks,m.CompletedTicks):F3} primary={r.PrimaryBefore}->{r.PrimaryAfter} offhand={r.OffhandBefore}->{r.OffhandAfter} weapon={a.WeaponId} ammo={r.AmmoId} ammo_template={r.AmmoTemplate} muzzle={r.MuzzlePosition.ToString("F4")} origin={r.ProjectileOrigin.ToString("F4")} direction={r.ProjectileDirection.ToString("F4")} damage={r.ProjectileDamage:F3} penetration={r.ProjectilePenetration:F3}"));
        }
        if (_dropped > 0)
        {
            _logger.LogWarning("DW_AUDIT dropped=" + _dropped);
            _dropped = 0;
        }
    }
}
