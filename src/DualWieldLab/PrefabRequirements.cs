using System;
using System.Text;

namespace DualWieldLab;

// Parts of a pistol model used by the left-hand presentation and shot path, read as EFT resolves them
// (PrefabProbe).
[Flags]
internal enum PrefabPart
{
    None = 0,
    WeaponPrefab = 1 << 0,
    Hierarchy = 1 << 1,
    WeaponRoot = 1 << 2,
    WeaponRootAnim = 1 << 3,
    Firearms = 1 << 4,
    Animator = 1 << 5,
    ContainerView = 1 << 6,
    Fireport = 1 << 7,
    Shellport = 1 << 8,
    ChamberVisual = 1 << 9,
    ShellExtraction = 1 << 10,
    SoundPlayer = 1 << 11,
    SoundTail = 1 << 12,
    Geometry = 1 << 13,
    IdleState = 1 << 14,
    GripPalm = 1 << 15,
    LeftIkMarker = 1 << 16,
    RightIkMarker = 1 << 17,
    MagazineNode = 1 << 18,
    DrawStates = 1 << 19,
    SelectorStates = 1 << 20,
    SoundBody = 1 << 21
}

// A missing part refuses the pair, before any inventory mutation, only when EFT's own firearm code
// would fault without it; otherwise the presentation degrades.
internal static class PrefabRequirements
{
    // EFT dereferences these without a null check, so a model lacking one faults natively too.
    internal const PrefabPart Required = PrefabPart.WeaponPrefab | PrefabPart.Hierarchy | PrefabPart.WeaponRoot
        | PrefabPart.WeaponRootAnim | PrefabPart.Firearms | PrefabPart.Animator | PrefabPart.ContainerView
        | PrefabPart.Fireport | PrefabPart.Shellport | PrefabPart.ChamberVisual | PrefabPart.ShellExtraction
        | PrefabPart.SoundPlayer | PrefabPart.SoundTail | PrefabPart.IdleState | PrefabPart.GripPalm;

    // EFT still fires without these; the left-hand animation uses fallbacks.
    internal const PrefabPart Optional = PrefabPart.LeftIkMarker | PrefabPart.RightIkMarker | PrefabPart.MagazineNode
        | PrefabPart.DrawStates | PrefabPart.SelectorStates | PrefabPart.SoundBody | PrefabPart.Geometry;

    internal static PrefabPart MissingRequired(PrefabPart present) => Required & ~present;

    // A pistol with a single fire mode has no selector to animate; that is not a degradation.
    internal static PrefabPart MissingOptional(PrefabPart present, bool selectableFireModes)
    {
        PrefabPart missing = Optional & ~present;
        return selectableFireModes ? missing : missing & ~PrefabPart.SelectorStates;
    }

    // FireBullet ends a looping (automatic) bank with the tail bank of the same suppression state,
    // unguarded; a one-shot bank or none needs no tail. Silenced banks fall back to the plain ones.
    internal static bool LoopTailAvailable(bool oneShot, bool body, bool tail, bool bodySilenced, bool tailSilenced)
    {
        if (oneShot) return true;
        bool plain = !body || tail;
        bool silenced = !(bodySilenced || body) || tailSilenced || tail;
        return plain && silenced;
    }

    // Animated left actions need the support-hand IK marker and magazine node; without them reloads
    // are inventory-only.
    internal static bool NativeLeftActions(PrefabPart present)
        => (present & (PrefabPart.LeftIkMarker | PrefabPart.MagazineNode)) == (PrefabPart.LeftIkMarker | PrefabPart.MagazineNode);

    internal static bool SelectorAnimated(PrefabPart present) => (present & PrefabPart.SelectorStates) != 0;

    internal static string Describe(PrefabPart parts)
    {
        if (parts == PrefabPart.None) return "none";
        var text = new StringBuilder();
        foreach (PrefabPart part in Enum.GetValues(typeof(PrefabPart)))
        {
            if (part == PrefabPart.None || (parts & part) == 0) continue;
            if (text.Length > 0) text.Append(',');
            text.Append(Name(part));
        }
        return text.ToString();
    }

    // Player-facing reason naming every essential part that is missing.
    internal static string? Refusal(PrefabPart present)
    {
        PrefabPart missing = MissingRequired(present);
        return missing == PrefabPart.None ? null : "its model lacks " + Describe(missing);
    }

    private static string Name(PrefabPart part)
    {
        switch (part)
        {
            case PrefabPart.WeaponPrefab: return "weapon_prefab";
            case PrefabPart.Hierarchy: return "transform_links";
            case PrefabPart.WeaponRoot: return "Weapon_root";
            case PrefabPart.WeaponRootAnim: return "Weapon_root_anim";
            case PrefabPart.Firearms: return "firearms";
            case PrefabPart.Animator: return "animator";
            case PrefabPart.ContainerView: return "container_view";
            case PrefabPart.Fireport: return "fireport";
            case PrefabPart.Shellport: return "shellport";
            case PrefabPart.ChamberVisual: return "patron_in_weapon";
            case PrefabPart.ShellExtraction: return "shell_extraction_data";
            case PrefabPart.SoundPlayer: return "weapon_sound_player";
            case PrefabPart.SoundBody: return "gunshot_sound";
            case PrefabPart.SoundTail: return "automatic_sound_tail";
            case PrefabPart.Geometry: return "mesh_renderer";
            case PrefabPart.IdleState: return "idle_state";
            case PrefabPart.GripPalm: return "grip_palm";
            case PrefabPart.LeftIkMarker: return "weapon_L_IK_marker";
            case PrefabPart.RightIkMarker: return "weapon_R_IK_marker";
            case PrefabPart.MagazineNode: return "mod_magazine";
            case PrefabPart.DrawStates: return "draw_states";
            case PrefabPart.SelectorStates: return "selector_states";
            default: return part.ToString();
        }
    }
}
