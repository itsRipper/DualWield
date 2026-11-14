using System.Collections.Generic;
using EFT.InventoryLogic;

namespace DualWieldLab;

// Native adapter for GeometryKey: walks the attachment tree once per pairing.
internal static class WeaponGeometry
{
    private static readonly List<string> Attachments = new List<string>();

    internal static string Key(Weapon weapon)
    {
        Attachments.Clear();
        Collect(weapon);
        string key = GeometryKey.Compose(weapon.StringTemplateId, Attachments);
        Attachments.Clear();
        return key;
    }

    private static void Collect(CompoundItem item)
    {
        foreach (Slot slot in item.Slots)
        {
            if (!GeometryKey.AffectsGeometry(slot.ID) || !(slot.ContainedItem is Item mod) || mod is Ammo) continue;
            Attachments.Add(slot.ID + "=" + mod.StringTemplateId);
            if (mod is CompoundItem compound) Collect(compound);
        }
    }
}
