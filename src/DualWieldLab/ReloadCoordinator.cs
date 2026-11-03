using System;
using EFT.InventoryLogic;

namespace DualWieldLab;

internal enum PistolHand { MainHand, Offhand }

// Service intent only. Native reload operations require the one ordinary hands
// controller and its animator events. This helper never moves a magazine/item,
// creates a controller or starts an unverified asynchronous equip/reload chain.
internal static class ReloadCoordinator
{
    internal static Weapon SelectForService(Weapon main, Weapon offhand, PistolHand hand)
    {
        if (main == null || offhand == null || ReferenceEquals(main, offhand) || main.Id == offhand.Id)
            throw new InvalidOperationException("Two distinct existing weapon identities are required for service coordination.");
        if (main.Owner == null || !ReferenceEquals(main.Owner, offhand.Owner))
            throw new InvalidOperationException("The paired weapons no longer have the same native inventory owner.");
        if (hand != PistolHand.MainHand && hand != PistolHand.Offhand)
            throw new ArgumentOutOfRangeException(nameof(hand));
        // Existing malfunctions do not prevent selecting a physical weapon for
        // service: the native reload/repair path remains the authority afterward.
        return hand == PistolHand.MainHand ? main : offhand;
    }
}
