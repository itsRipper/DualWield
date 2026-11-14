using EFT;
using EFT.AssetsManager;
using UnityEngine;

namespace DualWieldLab;

// Reads which PrefabRequirements parts a pistol model has, with EFT's own lookups
// (PlayerBones.FindFireport, Firearms.OnCreatePoolObject). Once per pairing, never per frame.
internal static class PrefabProbe
{
    private const int HandsLayer = FirearmsAnimator.HANDS_LAYER_INDEX;

    // FirearmController.CurrentFireport: the first "fireport" depth-first under Weapon_root, active or
    // not. A receiver and its barrel may each carry one; EFT takes the first, and so does the left pistol.
    internal static Transform? NativeFireport(WeaponPrefab prefab)
    {
        Transform? root = prefab.Hierarchy != null ? prefab.Hierarchy.GetTransform(ECharacterWeaponBones.Weapon_root) : null;
        return root != null ? TransformTools.FindTransformRecursive(root, WeaponPrefab.BONE_FIREPORT) : null;
    }

    internal static PrefabPart Inspect(GameObject model, WeaponPrefab? prefab)
    {
        if (model == null || prefab == null) return PrefabPart.None;
        PrefabPart present = PrefabPart.WeaponPrefab;
        TransformLinks? links = prefab.Hierarchy;
        if (links != null)
        {
            present |= PrefabPart.Hierarchy;
            if (links.GetTransform(ECharacterWeaponBones.Weapon_root) != null) present |= PrefabPart.WeaponRoot;
            if (links.GetTransform(ECharacterWeaponBones.Weapon_root_anim) != null) present |= PrefabPart.WeaponRootAnim;
            if (links.GetTransform(ECharacterWeaponBones.HumanRPalm) != null) present |= PrefabPart.GripPalm;
            if (links.GetTransform(ECharacterWeaponBones.weapon_L_IK_marker) != null) present |= PrefabPart.LeftIkMarker;
            if (links.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker) != null) present |= PrefabPart.RightIkMarker;
            if (NativeFireport(prefab) != null) present |= PrefabPart.Fireport;
        }
        if (prefab.ObjectInHands is Firearms firearms) present |= PrefabPart.Firearms | NativeFirearmParts(firearms);
        AssetPoolObject? pool = model.GetComponent<AssetPoolObject>();
        if (pool != null && pool.ContainerCollectionView != null) present |= PrefabPart.ContainerView;
        present |= SoundParts(prefab.GetComponent<WeaponSoundPlayer>());
        foreach (Transform transform in model.GetComponentsInChildren<Transform>(true))
            if (transform.name == "mod_magazine") { present |= PrefabPart.MagazineNode; break; }
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) { present |= PrefabPart.Geometry; break; }
        if (prefab.FirearmsAnimator != null) present |= InspectAnimator(model);
        return present;
    }

    // What Firearms resolved when the pooled model was created: shell port, chamber round visuals
    // ("patron_in_weapon*") and shell ejection data. Shots and ejections index these unguarded.
    private static PrefabPart NativeFirearmParts(Firearms firearms)
    {
        PrefabPart present = PrefabPart.None;
        if (firearms._shellPortsTransforms != null && firearms._shellPortsTransforms.Count > 0 && firearms._shellPortsTransforms[0] != null)
            present |= PrefabPart.Shellport;
        if (firearms._patronInWeaponTransforms != null && firearms._patronInWeaponTransforms.Length > 0)
            present |= PrefabPart.ChamberVisual;
        if (firearms._shellExtractionData != null) present |= PrefabPart.ShellExtraction;
        return present;
    }

    private static PrefabPart SoundParts(WeaponSoundPlayer? sound)
    {
        if (sound == null) return PrefabPart.None;
        PrefabPart present = PrefabPart.SoundPlayer;
        if (sound.Body != null || sound.BodySilenced != null) present |= PrefabPart.SoundBody;
        if (PrefabRequirements.LoopTailAvailable(sound.Non_auto, sound.Body != null, sound.Tail != null,
            sound.BodySilenced != null, sound.TailSilenced != null))
            present |= PrefabPart.SoundTail;
        return present;
    }

    internal const PrefabPart AnimatorParts = PrefabPart.Animator | PrefabPart.IdleState | PrefabPart.DrawStates | PrefabPart.SelectorStates;

    internal static PrefabPart InspectAnimator(GameObject model)
    {
        Animator animator = model.GetComponentInChildren<Animator>(true);
        if (animator == null || animator.runtimeAnimatorController == null) return PrefabPart.None;
        PrefabPart present = PrefabPart.Animator;
        // State queries need an initialised animator; otherwise states stay unverified and are assumed native.
        if (!animator.isInitialized) return present | PrefabPart.IdleState | PrefabPart.DrawStates | PrefabPart.SelectorStates;
        if (animator.layerCount <= HandsLayer) return present;
        string layer = animator.GetLayerName(HandsLayer) + ".";
        if (Has(animator, layer + "IDLE")) present |= PrefabPart.IdleState;
        if (Has(animator, layer + "OUT TO IDLE") && Has(animator, layer + "IDLE TO OUT") && Has(animator, layer + "SPAWN"))
            present |= PrefabPart.DrawStates;
        if (Has(animator, layer + "FIREMODE")) present |= PrefabPart.SelectorStates;
        return present;
    }

    private static bool Has(Animator animator, string fullPath) => animator.HasState(HandsLayer, Animator.StringToHash(fullPath));
}
