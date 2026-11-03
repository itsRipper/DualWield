using System;
using EFT;
using EFT.InventoryLogic;

namespace DualWieldLab;

// The native component owns selector validity, change notification and its
// Diffable persistence field. The change itself is native Idling.ChangeFireMode run on
// the left pistol's own context; never the primary controller's mode path.
internal static class OffhandFireMode
{
    // True only when the native offhand component changed; otherwise feedback says why.
    internal static bool TryCycle(Player player, Player.FirearmController primary, Weapon offhand,
        Func<Weapon.EFireMode, bool> nativeChange, out string feedback)
    {
        feedback = "The left fire mode cannot change right now.";
        if (player == null || primary == null || offhand == null || player.HandsController != primary
            || !player.HealthController.IsAlive || !player.FirstPersonPointOfView || player.IsInventoryOpened)
            return false;
        Weapon main = primary.Item;
        if (ReferenceEquals(main, offhand) || main.Id == offhand.Id || !(offhand is Pistol)
            || !ReferenceEquals(main.Owner, player.InventoryController)
            || !ReferenceEquals(offhand.Owner, player.InventoryController) || offhand.CurrentAddress == null)
        return false;
        if ((primary.CurrentOperation is not Player.FirearmController.Idling
                && primary.CurrentOperation is not Player.FirearmController.FireOperationBase) || primary.IsInReloadOperation()
            || primary.IsInInteraction() || player.IsSprintEnabled
            || offhand.MalfState.State != Weapon.EMalfunctionState.None)
        { feedback = "Finish the current action before changing the left fire mode."; return false; }
        if (player.InventoryController.CheckItemAction(offhand, offhand.CurrentAddress).Failed)
        return false;

        FireModeComponent component = offhand.FireMode;
        if (component == null || component.AvailableEFireModes == null || component.AvailableEFireModes.Length == 0)
        { feedback = "The left pistol has no fire-mode selector."; return false; }
        Weapon.EFireMode current = offhand.SelectedFireMode;
        Weapon.EFireMode next = component.GetNextFireMode();
        if (next == current)
        { feedback = "The left pistol has only one fire mode."; return false; }
        if (!WeaponCompatibility.IsSupportedMode(next))
        { feedback = "The left pistol's next fire mode is not supported."; return false; }

        Weapon.EFireMode mainBefore = main.SelectedFireMode;
        if (!nativeChange(next) && offhand.SelectedFireMode == current)
        return false;
        if (main.SelectedFireMode != mainBefore || offhand.SelectedFireMode != next)
            throw new InvalidOperationException("Native offhand selector invariant failed; do not retry or rewrite either component.");
        feedback = "";
        return true;
    }
}
