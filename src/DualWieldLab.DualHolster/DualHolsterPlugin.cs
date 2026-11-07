using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace DualWieldLab.DualHolster;

// Client half of the second holster. The server mod appends one real slot, "HolsterLeft", to
// the Default Inventory template; this lets the compiled client build and show it. Items in it
// are ordinary native equipment: real addresses, native filters, drag-and-drop transactions,
// inspection and weight. Required whenever the server mod is installed; harmless without it.
[BepInPlugin(Id, "DualWield dual holster", "1.1.0")]
[BepInIncompatibility(LegacyId)]
public sealed class DualHolsterPlugin : BaseUnityPlugin
{
    public const string Id = "itsripper.dualwield.dualholster";
    // The plugin's id before the rename: never load next to an old copy of this plugin.
    private const string LegacyId = "local.dualwieldlab.dualholster";
    private const string LegacyConfigFile = LegacyId + ".cfg";
    public const string SlotName = "HolsterLeft";
    // EquipmentSlot ends at ArmBand = 14 in the inspected build; the appended slot is index 15.
    internal const EquipmentSlot LeftHolster = (EquipmentSlot)15;
    private static ConfigEntry<string> _leftLabel = null!, _rightLabel = null!;
    // Native size/position of the views each tab's layout changes (one set per EquipmentTab:
    // character screen, in-raid inventory, insurance screens).
    private static readonly Dictionary<RectTransform, (Vector2 Size, Vector2 Position)> NativeLayout = new();
    private const float HolsterWidth = .8f, Gap = .08f;
    private Harmony? _harmony;

    private void Awake()
    {
        bool configExisted = File.Exists(Config.ConfigFilePath);
        _leftLabel = Config.Bind("Layout", "LeftLabel", "LEFT HOLSTER", "Caption of the second holster.");
        _rightLabel = Config.Bind("Layout", "RightLabel", "RIGHT HOLSTER", "Caption of the native holster while the second exists.");
        var menuPreview = Config.Bind("Presentation", "DualPistolCharacterPreview", true, "Character previews (character selection, character overview) hold both holster pistols in a two-pistol stance when the HOLSTER pistol is in hands and HOLSTERLEFT also holds a pistol. Off: the native single-weapon preview.");
        ConfigMigration.Apply(Config, configExisted, LegacyConfigFile, new Dictionary<string, string>(), Logger.LogInfo, Logger.LogWarning);
        if (!SupportedGame.IsSupported(typeof(InventoryEquipment))
            || Enum.IsDefined(typeof(EquipmentSlot), (int)LeftHolster) || Enum.GetValues(typeof(EquipmentSlot)).Length != 15)
        {
            Logger.LogError("Dual holster disabled: EFT " + SupportedGame.Running() + " or its EquipmentSlot enum differs from the supported " + SupportedGame.Build + ".");
            return;
        }
        try
        {
            Patch(menuPreview);
        }
        catch (Exception e)
        {
            _harmony?.UnpatchSelf();
            Logger.LogError("Dual holster patch setup failed; disabled: " + e);
        }
    }

    private void Patch(ConfigEntry<bool> menuPreview)
    {
        _harmony = new Harmony(Id);
        _harmony.Patch(AccessTools.Constructor(typeof(InventoryEquipment), new[] { typeof(string), typeof(InventoryEquipmentTemplate) }),
            transpiler: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(ParseTranspiler)));
        _harmony.Patch(AccessTools.Method(typeof(EquipmentTab), nameof(EquipmentTab.Show)),
            prefix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(BeforeEquipmentTabShow)),
            postfix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(AfterEquipmentTabShow)));
        _harmony.Patch(AccessTools.Method(typeof(InventoryEquipmentExtension), nameof(InventoryEquipmentExtension.GetAllSlots)),
            postfix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(AfterGetAllSlots)));
        _harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SetSlotItem)),
            prefix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(BeforeSetSlotItem)));
        _harmony.Patch(AccessTools.Method(typeof(InventoryController), nameof(InventoryController.IsAtReachablePlace)),
            postfix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(AfterIsAtReachablePlace)));
        _harmony.Patch(AccessTools.Method(typeof(InventoryExtension), nameof(InventoryExtension.FindSlotToPickUp), new[] { typeof(InventoryEquipment), typeof(Item) }),
            postfix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(AfterFindSlotToPickUp)));
        _harmony.Patch(AccessTools.Method(typeof(PlayerModelLoader), nameof(PlayerModelLoader.GetItemInHands)),
            postfix: new HarmonyMethod(typeof(DualHolsterPlugin), nameof(AfterGetItemInHands)));
        MenuDualPistols.Enabled = () => menuPreview.Value;
        MenuDualPistols.Log = m => Logger.LogDebug(m);
        MenuDualPistols.Warn = m => Logger.LogWarning(m);
        MenuDualPistols.Patch(_harmony);
        QuickSlotPistol.Patch(_harmony);
        Logger.LogInfo("Dual holster client support active: slot " + SlotName + " maps to equipment index " + (int)LeftHolster + ".");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();

    // InventoryEquipment indexes its slots by Enum.Parse(EquipmentSlot, slot.ID); only the
    // appended slot name is mapped, every other name still goes through Enum.Parse.
    private static IEnumerable<CodeInstruction> ParseTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        MethodInfo parse = AccessTools.Method(typeof(Enum), nameof(Enum.Parse), new[] { typeof(Type), typeof(string) });
        MethodInfo replacement = AccessTools.Method(typeof(DualHolsterPlugin), nameof(ParseSlot));
        int replaced = 0;
        foreach (CodeInstruction instruction in instructions)
        {
            if (instruction.Calls(parse)) { instruction.opcode = OpCodes.Call; instruction.operand = replacement; replaced++; }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("InventoryEquipment slot parsing changed; expected one Enum.Parse, found " + replaced + ".");
    }

    public static object ParseSlot(Type type, string value)
        => type == typeof(EquipmentSlot) && value == SlotName ? LeftHolster : Enum.Parse(type, value);

    // GetAllSlots walks the built-in EquipmentSlot values, which end before the appended slot.
    // Its callers are equipment weight, IsItemEquipped, Inventory.AllRealPlayerItems and the
    // hideout shooting range, which re-clones and releases its equipment through it and would
    // otherwise keep a stale left pistol.
    private static void AfterGetAllSlots(InventoryEquipment equipment, ref IEnumerable<Slot> __result)
    {
        if (HasLeftHolster(equipment)) __result = __result.Concat(new[] { equipment.GetSlot(LeftHolster) });
    }

    // The native pistol selection (EFT's SelectSecondaryWeapon/QuickSelectSecondaryWeapon
    // bindings, 1 by default) resolves only EquipmentSlot.Holster. With that slot empty it now
    // resolves the left holster instead, and the native selection continues unchanged with the
    // real item there (one-handed native presentation; no item moves).
    private static void BeforeSetSlotItem(Player __instance, ref EquipmentSlot equipmentSlot)
    {
        if (equipmentSlot != EquipmentSlot.Holster || __instance == null || !__instance.IsYourPlayer) return;
        InventoryEquipment equipment = __instance.InventoryController.Inventory.Equipment;
        if (HasLeftHolster(equipment) && equipment.GetSlot(EquipmentSlot.Holster).ContainedItem == null
            && equipment.GetSlot(LeftHolster).ContainedItem is Weapon)
            equipmentSlot = LeftHolster;
    }

    // Alt+Click / quick equip asks FindSlotToPickUp, whose pistol list is only EquipmentSlot.Holster:
    // with the right holster taken it found nothing ("No empty space"). When the native search finds
    // no slot, an empty left holster that accepts the weapon is offered (its native filters decide).
    private static void AfterFindSlotToPickUp(InventoryEquipment equipment, Item item, ref ItemAddress __result)
    {
        if (__result != null || !(item is Weapon) || !HasLeftHolster(equipment)) return;
        Slot left = equipment.GetSlot(LeftHolster);
        if (left.ContainedItem != null || item.Parent?.Container == left) return;
        __result = left.FindLocationForItem(item, out _);
    }

    // Character previews pick the item in hands from FirstPrimary, SecondPrimary, Holster, Scabbard
    // (or the controller's ItemInHands), never HOLSTERLEFT: with only the left holster filled they
    // showed the knife or empty hands. Same rule as the pistol key: the left holster pistol stands in
    // for an empty Holster.
    private static void AfterGetItemInHands(PlayerVisualRepresentation playerVisual, InventoryController inventoryController, ref Item __result)
    {
        InventoryEquipment? equipment = playerVisual?.Equipment;
        if (equipment == null || !HasLeftHolster(equipment) || equipment.GetSlot(EquipmentSlot.Holster).ContainedItem != null) return;
        bool fallback = __result == null || (inventoryController == null && __result.Parent?.Container?.ID == EquipmentSlot.Scabbard.ToString());
        if (!fallback || equipment.GetSlot(EquipmentSlot.FirstPrimaryWeapon).ContainedItem != null
            || equipment.GetSlot(EquipmentSlot.SecondPrimaryWeapon).ContainedItem != null) return;
        if (equipment.GetSlot(LeftHolster).ContainedItem is Weapon left) __result = left;
    }

    // Equipping goes through IsAtReachablePlace, which lists the built-in weapon slots only; a
    // left-holster weapon was "unreachable" and TryProceed equipped the first available weapon
    // instead. The native conditions (vital parts, examined) still apply.
    private static void AfterIsAtReachablePlace(InventoryController __instance, Item item, ref bool __result)
    {
        if (__result || !(item is Weapon) || !(item.Parent?.Container is Slot slot) || slot.ID != SlotName) return;
        InventoryEquipment equipment = __instance.Inventory.Equipment;
        __result = HasLeftHolster(equipment) && ReferenceEquals(equipment.GetSlot(LeftHolster), slot)
            && !(item is CompoundItem compound && compound.MissingVitalParts.Any()) && __instance.Examined(item);
    }

    internal static bool HasLeftHolster(InventoryEquipment? equipment)
        => equipment != null && equipment.Slots.Length > (int)LeftHolster && equipment.GetSlot(LeftHolster)?.ID == SlotName;

    // The left view is a native SlotView bound to the real appended Slot. It is offered only
    // when the loaded equipment has that slot (an unmodded server gives 15 slots).
    private static void BeforeEquipmentTabShow(EquipmentTab __instance, InventoryEquipment equipment,
        ref Dictionary<EquipmentSlot, SlotView> ____slotViews)
    {
        SlotView? holster = __instance._holsterSlot;
        if (holster == null) return;
        // A tab instantiated inactive (insurance screens) builds its views in Awake, which
        // Show's own activation would run just after this prefix.
        if (____slotViews == null && HasLeftHolster(equipment)) __instance.gameObject.SetActive(true);
        var views = ____slotViews;
        if (views == null) return;
        views.TryGetValue(LeftHolster, out SlotView? left);
        if (!HasLeftHolster(equipment))
        {
            if (left != null) { views.Remove(LeftHolster); left.gameObject.SetActive(false); }
            RestoreNative(holster.transform as RectTransform);
            RestoreNative(__instance._primaryWeaponSlot?.transform as RectTransform);
            return;
        }
        if (left == null)
        {
            GameObject clone = Instantiate(holster.gameObject, holster.transform.parent, false);
            clone.name = "DualWieldLab_LeftHolsterSlot";
            left = clone.GetComponent<SlotView>();
        }
        left.gameObject.SetActive(true);
        views[LeftHolster] = left;
        if (left.transform is RectTransform rect && holster.transform is RectTransform source)
            PlaceSideBySide(rect, source, __instance._primaryWeaponSlot?.transform as RectTransform);
    }

    // SlotView.Show writes the localized slot caption; replace it afterwards.
    private static void AfterEquipmentTabShow(EquipmentTab __instance, Dictionary<EquipmentSlot, SlotView> ____slotViews)
    {
        if (____slotViews == null || !____slotViews.TryGetValue(LeftHolster, out SlotView? left) || left == null
            || !left.gameObject.activeSelf || __instance._holsterSlot == null) return;
        Relabel(left.gameObject, _leftLabel.Value);
        Relabel(__instance._holsterSlot.gameObject, _rightLabel.Value);
    }

    // ON SLING | LEFT HOLSTER | RIGHT HOLSTER within the native row, at native height (the item
    // overlays, ammo count and icon fitting are laid out for it): the holsters become two equal
    // cells of 80% native width and ON SLING gives up only the rest (about a tenth of its width,
    // most of it taken from the wide native gap).
    private static void PlaceSideBySide(RectTransform left, RectTransform holster, RectTransform? sling)
    {
        Remember(holster);
        RestoreNative(holster);
        Rect cell = WorldRect(holster);
        float width = cell.width * HolsterWidth, gap = cell.width * Gap;
        SetSpan(holster, cell.xMax - width, cell.xMax);
        left.anchoredPosition = holster.anchoredPosition;
        left.sizeDelta = holster.sizeDelta;
        SetSpan(left, cell.xMax - 2f * width - gap, cell.xMax - width - gap);
        if (sling == null) return;
        Remember(sling);
        RestoreNative(sling);
        Rect slingCell = WorldRect(sling);
        float right = Mathf.Min(slingCell.xMax, cell.xMax - 2f * width - 2f * gap);
        SetSpan(sling, slingCell.xMin, right);
    }

    // Places a view horizontally between two world x positions, keeping its height and row.
    private static void SetSpan(RectTransform view, float xMin, float xMax)
    {
        float scale = Mathf.Max(1e-4f, view.parent.lossyScale.x);
        view.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, (xMax - xMin) / scale);
        view.anchoredPosition += new Vector2((xMin - WorldRect(view).xMin) / scale, 0f);
    }

    private static void Remember(RectTransform view)
    {
        if (NativeLayout.ContainsKey(view)) return;
        foreach (var destroyed in NativeLayout.Keys.Where(k => k == null).ToList()) NativeLayout.Remove(destroyed);
        NativeLayout[view] = (view.sizeDelta, view.anchoredPosition);
    }

    private static void RestoreNative(RectTransform? view)
    {
        if (view != null && NativeLayout.TryGetValue(view, out var native))
        { view.sizeDelta = native.Size; view.anchoredPosition = native.Position; }
    }

    private static Rect WorldRect(RectTransform rect)
    {
        var corners = new Vector3[4];
        rect.GetWorldCorners(corners);
        return Rect.MinMaxRect(corners[0].x, corners[0].y, corners[2].x, corners[2].y);
    }

    private static void Relabel(GameObject view, string caption)
    {
        foreach (TMP_Text text in view.GetComponentsInChildren<TMP_Text>(true)
                     .Where(t => t.text.IndexOf("holster", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            // The localization component would restore the stock caption.
            foreach (var localized in text.GetComponents<MonoBehaviour>().Where(c => c != null && c.GetType().Name.StartsWith("Localized")))
                localized.enabled = false;
            if (!text.enableAutoSizing)
            {
                text.fontSizeMax = text.fontSize;
                text.fontSizeMin = text.fontSize * .6f;
                text.enableAutoSizing = true;
            }
            text.text = caption;
        }
    }
}
