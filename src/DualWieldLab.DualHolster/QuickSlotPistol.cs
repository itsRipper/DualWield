using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace DualWieldLab.DualHolster;

// Quick slot 1 is FastAccess.BoundCells[Item1], the Holster slot. The pistol key already draws the
// HOLSTERLEFT pistol when Holster is empty (BeforeSetSlotItem), but the slot view stayed empty.
// Only the view changes: while Holster is empty it shows HOLSTERLEFT. FastAccess, the bindings
// and the items themselves are untouched.
internal static class QuickSlotPistol
{
    private static readonly AccessTools.FieldRef<QuickSlotView, InventoryController> Controller = AccessTools.FieldRefAccess<QuickSlotView, InventoryController>("InventoryController");
    private static readonly AccessTools.FieldRef<QuickSlotView, ItemUiContext> UiContext = AccessTools.FieldRefAccess<QuickSlotView, ItemUiContext>("ItemUiContext");

    internal static void Patch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(BoundSlotView), nameof(BoundSlotView.Show)),
            prefix: new HarmonyMethod(typeof(QuickSlotPistol), nameof(BeforeShow)));
        harmony.Patch(AccessTools.FirstMethod(typeof(BoundSlotView), m => m.Name.EndsWith("OnItemAdded")),
            postfix: new HarmonyMethod(typeof(QuickSlotPistol), nameof(AfterItemAdded)));
        harmony.Patch(AccessTools.FirstMethod(typeof(BoundSlotView), m => m.Name.EndsWith("OnItemRemoved")),
            postfix: new HarmonyMethod(typeof(QuickSlotPistol), nameof(AfterItemRemoved)));
    }

    private static Slot Effective(InventoryEquipment equipment, Slot holster)
    {
        if (holster.ContainedItem != null || !DualHolsterPlugin.HasLeftHolster(equipment)) return holster;
        Slot left = equipment.GetSlot(DualHolsterPlugin.LeftHolster);
        return left.ContainedItem is Weapon ? left : holster;
    }

    private static void BeforeShow(BoundSlotView __instance, ref Slot slot, InventoryController inventoryController)
    {
        if (__instance.BoundIndex != EBoundItem.Item1 || slot?.ID != "Holster" || inventoryController?.Inventory?.Equipment == null) return;
        slot = Effective(inventoryController.Inventory.Equipment, slot);
    }

    private static void AfterItemAdded(BoundSlotView __instance, AddItemEventArgs eventArgs)
    {
        if (eventArgs.Status == CommandStatus.Succeed) Reevaluate(__instance, eventArgs.To?.Container);
    }

    private static void AfterItemRemoved(BoundSlotView __instance, RemoveItemEventArgs eventArgs)
    {
        if (eventArgs.Status == CommandStatus.Succeed) Reevaluate(__instance, eventArgs.From?.Container);
    }

    // Adding to or removing from either holster can change which of the two the slot represents.
    private static void Reevaluate(BoundSlotView view, IContainer? container)
    {
        if (view.BoundIndex != EBoundItem.Item1 || !(container is Slot changed) || (changed.ID != "Holster" && changed.ID != DualHolsterPlugin.SlotName)) return;
        InventoryController controller = Controller(view);
        InventoryEquipment? equipment = controller?.Inventory?.Equipment;
        if (equipment == null || !DualHolsterPlugin.HasLeftHolster(equipment)) return;
        Slot target = Effective(equipment, equipment.GetSlot(EquipmentSlot.Holster));
        if (ReferenceEquals(target, view.GetSlot())) return;
        view.RemoveItemView();
        view.Show(target, controller, UiContext(view));
    }
}
