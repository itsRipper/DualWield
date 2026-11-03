using System;
using System.Linq;
using EFT;
using EFT.InventoryLogic;

namespace DualWieldLab;

public sealed partial class RuntimePlugin
{
    // Diagnostics only: the AI noise class Player.OnMakingShot is about to use (it reads
    // HandsController.IsSilenced) against the firing weapon's own suppressor.
    private static void BeforeMakingShot(Player __instance, IWeapon weapon)
    {
        var self = Instance;
        if (self == null || !self._settings.Diagnostics.Value || self._player != __instance || weapon is not Weapon fired) return;
        bool classified = (__instance.HandsController as Player.FirearmController)?.IsSilenced == true;
        bool actual = fired.GetItemComponentsInChildren<SilencerComponent>().Any();
        self.Logger.LogInfo("DW_SHOT_NOISE weapon=" + fired.Id + " side=" + (ReferenceEquals(fired, self._secondary) ? "left" : "right")
            + " ai_sound=" + (classified ? "silencedGun" : "gun") + " suppressor=" + actual + " consistent=" + (classified == actual));
    }

    private void QueueAudit(ShotAudit audit)
    {
        if (_settings.Diagnostics.Value) _shotAudit.Queue(audit);
    }

    private void DrainDiagnostics()
    {
        if (_poseReportPending)
        {
            _poseReportPending = false;
            if (_settings.Diagnostics.Value && _handPose != null) Logger.LogInfo(FormattableString.Invariant($"DW_POSE fingers={_handPose.FingerCount} reach_error_m={_handPose.ReachError:F4} reach_correction_m={_handPose.ReachCorrection:F4}"));
            if (_settings.Diagnostics.Value && _visualRig != null) Logger.LogInfo(_visualRig.CaptureDiagnostic());
            if (_settings.Diagnostics.Value && _handPose != null) Logger.LogInfo(_handPose.CaptureDiagnostic());
            if (_settings.Diagnostics.Value && _primary != null && _secondary != null)
                Logger.LogInfo("DW_PAIR_STATE main=" + _primary.Item.Id + " offhand=" + _secondary.Id
                    + " native_controller_matches=" + (_player?.HandsController == _primary)
                    + " owner_matches=" + ReferenceEquals(_primary.Item.Owner, _secondary.Owner)
                    + " main_address=" + NativeReloadService.DescribeAddress(_primary.Item.CurrentAddress)
                    + " offhand_address=" + NativeReloadService.DescribeAddress(_secondary.CurrentAddress));
        }
        _shotAudit.Drain(maxEntries: 2);
        for (int i = 0; i < 4 && DamageDiagnostics.TryDequeue(out DamageDiagnosticEvent hit); i++)
        {
            Logger.LogInfo(FormattableString.Invariant($"DW_HIT kind={hit.Kind} seq={hit.Sequence} t={hit.Tick} weapon={hit.Weapon?.Id} ammo={hit.Ammo?.Id} body={hit.BodyCollider} target_ai={hit.TargetIsAI} part={hit.BodyPart} damage_input={hit.RequestedDamage:F3} penetration={hit.PenetrationPower:F3} penetrated={hit.Penetrated} health={hit.HealthBefore:F3}->{hit.HealthAfter:F3} part_health={hit.PartHealthBefore:F3}->{hit.PartHealthAfter:F3} fragment={hit.FragmentIndex} point={hit.HitPoint.ToString("F4")}"));
        }
        if (_reportedDamageDrops != DamageDiagnostics.DroppedEvents || _reportedDamageFailures != DamageDiagnostics.ObservationFailures)
        {
            _reportedDamageDrops = DamageDiagnostics.DroppedEvents;
            _reportedDamageFailures = DamageDiagnostics.ObservationFailures;
            Logger.LogWarning("DW_DAMAGE_AUDIT dropped=" + _reportedDamageDrops + " observation_failures=" + _reportedDamageFailures);
        }
    }
}
