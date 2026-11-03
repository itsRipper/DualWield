using System;
using System.Collections.Generic;
using Comfort.Common;
using Diz.Binding;
using EFT;
using EFT.AssetsManager;
using AnimationSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace DualWieldLab.DualHolster;

// Character previews (MenuPlayerPoser): when the pistol in hands is the HOLSTER pistol and
// HOLSTERLEFT also holds a pistol, the character holds both in a two-pistol ready stance.
// The left pistol is its own item model (attachments, magazine, suppressor).
//
// While active the preview owns the arms, hands and the menu's random animations:
// - MenuPlayerPoser.OnRandomizeAnimation randomly starts EFT's menu weapon operations (look,
//   check chamber, check ammo, reload) and left-hand gestures on the right pistol's weapon
//   animator. They animate the magazine and hands for the one-pistol pose (the magazine would
//   travel to the native left hand), so they are suppressed; the body idle (breathing, sway)
//   remains.
// - The stance is defined in the body frame, not on EFT's one-pistol pose. Each pistol is
//   oriented by its own geometry and placed so its native grip marker is at the stance hand
//   point; each arm is solved onto it with the preview's Final IK limbs.
// - The left hand takes the left pistol's own right-hand GripPose (the grip EFT's HandPoser
//   gives the right hand), mirrored through the skeleton's bind poses (HandMirror). EFT's left
//   HandPoser would otherwise keep the two-handed support grip.
internal static class MenuDualPistols
{
    private sealed class State
    {
        internal GameObject Model = null!;
        internal Transform LeftRoot = null!, LeftMarker = null!, LeftFireport = null!;
        internal Transform RightModel = null!, RightRoot = null!, RightMarker = null!, RightFireport = null!;
        internal Vector3 RightModelLocalPosition;
        internal Quaternion RightModelLocalRotation;
        internal HandMirror Hands = null!;
        internal GripPose? LeftGrip;
        internal Transform[] RightHand = Array.Empty<Transform>();
        internal Slot LeftSlot = null!;
        internal Pistol LeftItem = null!;
    }

    // Right hand (grip marker) relative to the shoulder mid-point, metres: out along the shoulder
    // line, up, forward. Chest height, arms reaching forward with the elbows bent.
    private static readonly Vector3 HandPoint = new Vector3(.19f, -.14f, .44f);
    // Barrels turn slightly inward (negative = toward the centre) and down; the slides cant inward.
    // Seen from the frontal menu camera a level, outward or forward-pointing pistol collapses into
    // an upright slab; the downward pitch keeps the slide visible.
    private const float OutwardYaw = -3f, DownPitch = 14f, InwardCant = 10f;

    private static readonly AccessTools.FieldRef<MenuPlayerPoser, Transform[]> Markers = AccessTools.FieldRefAccess<MenuPlayerPoser, Transform[]>("_markers");
    private static readonly AccessTools.FieldRef<PlayerBody, BindableState<Item>> ItemInHands = AccessTools.FieldRefAccess<PlayerBody, BindableState<Item>>("_itemInHands");
    private static readonly Dictionary<MenuPlayerPoser, State> Active = new();

    internal static Action<string>? Log;
    internal static Action<string>? Warn;
    internal static Func<bool> Enabled = () => false;

    internal static void Patch(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.ChangeWeapon)),
            postfix: new HarmonyMethod(typeof(MenuDualPistols), nameof(AfterChangeWeapon)));
        harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.LateUpdate)),
            prefix: new HarmonyMethod(typeof(MenuDualPistols), nameof(BeforeLateUpdate)),
            postfix: new HarmonyMethod(typeof(MenuDualPistols), nameof(AfterLateUpdate)));
        harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.OnRandomizeAnimation)),
            prefix: new HarmonyMethod(typeof(MenuDualPistols), nameof(BeforeRandomizeAnimation)));
        harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.OnDestroy)),
            prefix: new HarmonyMethod(typeof(MenuDualPistols), nameof(Release)));
    }

    private static void AfterChangeWeapon(MenuPlayerPoser __instance)
    {
        Release(__instance);
        if (!Enabled()) return;
        try { Attach(__instance); }
        catch (Exception e) { Release(__instance); Warn?.Invoke("Two-pistol character preview unavailable: " + e.Message); }
    }

    // EFT poses the in-hands weapon from its current transforms each frame. Left at last
    // frame's stance pose, that feedback walks Weapon_root_anim and the model apart without
    // bound, so the native pass always starts from the model's native local pose.
    private static void BeforeLateUpdate(MenuPlayerPoser __instance)
    {
        if (!Active.TryGetValue(__instance, out var s) || s.RightModel == null) return;
        s.RightModel.localPosition = s.RightModelLocalPosition;
        s.RightModel.localRotation = s.RightModelLocalRotation;
    }

    private static bool BeforeRandomizeAnimation(MenuPlayerPoser __instance)
    {
        if (!Active.ContainsKey(__instance)) return true;
        __instance.PlayerAnimatorController?.SetInteger(PlayerAnimator.UI_RANDOM_TRANSITION, 0);
        return false;
    }

    private static void Attach(MenuPlayerPoser poser)
    {
        PlayerBody? body = poser.GetComponentInParent<PlayerBody>() ?? poser.GetComponentInChildren<PlayerBody>();
        if (body?.Equipment == null || !DualHolsterPlugin.HasLeftHolster(body.Equipment)) return;
        Item? inHands = ItemInHands(body)?.Value;
        if (!(inHands is Pistol right) || right.Parent?.Container?.ID != "Holster") return;
        if (!(body.Equipment.GetSlot(DualHolsterPlugin.LeftHolster).ContainedItem is Pistol left)) return;
        Transform weaponParent = poser.PBones.WeaponRoot.Original;
        // The poser has gathered the right pistol's IK markers from its TransformLinks.
        Transform? rightMarker = Markers(poser)[1];
        Transform? rightRoot = rightMarker;
        while (rightRoot != null && rightRoot.name != "Weapon_root") rightRoot = rightRoot.parent;
        Transform? rightFireport = rightRoot != null ? FindActive(rightRoot, "fireport") ?? FindNamed(rightRoot, "fireport") : null;
        if (rightRoot == null || rightMarker == null || rightFireport == null)
            throw new InvalidOperationException("the right pistol has no Weapon_root/grip marker/fireport");
        WeaponPrefab prefab = CreatePreviewWeapon(left, poser.PBones.Ribcage.Original);
        GameObject model = prefab.gameObject;
        try { Bind(poser, body, right, left, prefab, rightRoot, rightMarker, rightFireport); }
        catch
        {
            // Not registered yet, so Release cannot find it: return the pooled model here.
            AssetPoolObject.ReturnToPool(model);
            throw;
        }
    }

    private static void Bind(MenuPlayerPoser poser, PlayerBody body, Pistol right, Pistol left, WeaponPrefab prefab,
        Transform rightRoot, Transform rightMarker, Transform rightFireport)
    {
        GameObject model = prefab.gameObject;
        Transform Bone(ECharacterWeaponBones bone) => prefab.Hierarchy.GetTransform(bone)
            ?? throw new InvalidOperationException("missing " + bone);
        int layer = rightRoot.GetComponentInChildren<Renderer>()?.gameObject.layer ?? model.layer;
        foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
        var state = new State
        {
            Model = model,
            LeftRoot = Bone(ECharacterWeaponBones.Weapon_root), LeftMarker = Bone(ECharacterWeaponBones.weapon_R_IK_marker),
            LeftFireport = FindActive(model.transform, "fireport") ?? throw new InvalidOperationException("the left pistol has no fireport"),
            RightModel = rightRoot.parent ?? rightRoot, RightRoot = rightRoot, RightMarker = rightMarker, RightFireport = rightFireport,
            LeftSlot = body.Equipment.GetSlot(DualHolsterPlugin.LeftHolster), LeftItem = left,
        };
        state.RightModelLocalPosition = state.RightModel.localPosition;
        state.RightModelLocalRotation = state.RightModel.localRotation;
        state.RightHand = poser.RightHandPoser.Children;
        state.Hands = HandMirror.Create(body.transform, state.RightHand, poser.LeftHandPoser.Children);
        state.LeftGrip = RightHandGrip(model.transform);
        // A weapon operation already running belongs to the one-pistol presentation.
        poser.PlayerAnimatorController?.SetBool(PlayerAnimator.UI_WEAPON_OPERATION, false);
        poser.PlayerAnimatorController?.SetInteger(PlayerAnimator.UI_RANDOM_TRANSITION, 0);
        Active[poser] = state;
        Log?.Invoke("Menu dual-pistol preview: right=" + right.ShortName.Localized() + " left=" + left.ShortName.Localized()
            + " left_fingers=" + state.Hands.FingerCount + " left_grip=" + (state.LeftGrip != null ? state.LeftGrip.GripType.ToString() : "right_hand_copy"));
    }

    // As PlayerModelLoader.CreateWeapon builds the in-hands weapon: a pooled model comes back
    // inactive, and the weapon animator must be posed (PATROL, stock fold, magazine state) or the
    // slide, magazine and stock keep their default transforms.
    private static WeaponPrefab CreatePreviewWeapon(Weapon weapon, Transform ribcage)
    {
        GameObject? model = Singleton<ObjectsFactory>.Instance?.CreateItem(weapon, isAnimated: true);
        WeaponPrefab? prefab = model != null ? model.GetComponent<WeaponPrefab>() : null;
        if (prefab == null)
        {
            if (model != null) AssetPoolObject.ReturnToPool(model);
            throw new InvalidOperationException("the left pistol model could not be created");
        }
        prefab.transform.SetParent(ribcage, worldPositionStays: false);
        prefab.transform.localPosition = Vector3.zero;
        prefab.transform.localRotation = Quaternion.identity;
        prefab.gameObject.SetActive(true);
        prefab.Init(null, parent: true);
        Animator? animatorComponent = prefab.Hierarchy.GetComponent<Animator>();
        if (animatorComponent != null)
        {
            IAnimator animator = UnityAnimatorWrapper.Create(animatorComponent);
            animator.enabled = true;
            animator.Play("PATROL", 1, 0f);
            int stock = animator.GetLayerIndex("Stock");
            if (stock >= 0 && stock < animator.layerCount) animator.SetLayerWeight(stock, weapon.Folded ? 1 : 0);
            AnimationControllerParametersTable.SetStockFolded(animator, weapon.Folded);
            AnimationControllerParametersTable.SetAmmoInMag(animator, weapon.GetMaxMagazineCount());
            Magazine? magazine = weapon.GetCurrentMagazine();
            AnimationControllerParametersTable.SetMagInWeapon(animator, magazine != null);
            AnimationControllerParametersTable.SetAmmoInChamber(animator, weapon.ChamberAmmoCount);
            if (magazine != null)
            {
                AnimationControllerParametersTable.SetRelTypeOld(animator, magazine.magAnimationIndex);
                AnimationControllerParametersTable.SetRelTypeNew(animator, magazine.magAnimationIndex);
            }
            animator.Update(3f);
        }
        return prefab;
    }

    // The grip EFT's menu poser gives a right hand on this weapon (UpdatePersonGrip's choice).
    private static GripPose? RightHandGrip(Transform model)
    {
        GripPose? best = null;
        foreach (var grip in model.GetComponentsInChildren<GripPose>(true))
            if (grip.Hand == GripPose.EHand.Right && (best == null || (grip.IsAlternative && !best.IsAlternative))) best = grip;
        return best;
    }

    private static Transform? FindNamed(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true)) if (t.name == name) return t;
        return null;
    }

    private static Transform? FindActive(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(false)) if (t.name == name) return t;
        return null;
    }

    // After the native preview pose (EFT places the weapon on the hand and solves both arms inside
    // LateUpdate), both pistols and arms are put into the two-pistol stance.
    private static void AfterLateUpdate(MenuPlayerPoser __instance)
    {
        if (!Active.TryGetValue(__instance, out var s)) return;
        // The left pistol can leave HOLSTERLEFT without a weapon change (drag to the stash).
        if (s.Model == null || s.RightRoot == null || s.RightMarker == null || s.LeftSlot.ContainedItem != s.LeftItem) { Release(__instance); return; }
        PlayerBones bones = __instance.PBones;
        // The limbs' upper-arm bones: PlayerBones' "shoulder" bones sit at the clavicles near the neck.
        Vector3 leftShoulder = __instance.Limbs[0].solver.bone1.transform.position, rightShoulder = __instance.Limbs[1].solver.bone1.transform.position;
        // Facing from the character itself: the native one-pistol idle twists the shoulders, and a
        // stance built on that twist pushes the right arm out to the side.
        Vector3 up = Vector3.up, origin = (rightShoulder + leftShoulder) * .5f;
        Vector3 forward = Vector3.ProjectOnPlane(__instance.transform.forward, up).normalized;
        Vector3 n = Vector3.Cross(up, forward);
        if (Vector3.Dot(n, rightShoulder - leftShoulder) < 0f) n = -n;

        // Right pistol: EFT places its model on the right hand each LateUpdate; it is re-placed here.
        Matrix4x4 rightRoot = StanceRoot(s.RightRoot, s.RightMarker, s.RightFireport, origin, n, up, forward, out _);
        Place(s.RightModel, s.RightRoot, rightRoot);
        Solve(__instance.Limbs[1], s.RightMarker.position, s.RightMarker.rotation);

        // Left pistol: the same stance computed for the left pistol's own geometry, mirrored about
        // the body's mid-plane; the left palm is the mirror image of where a right palm would hold it.
        Vector3 lateral = s.Hands.RightAxis;
        Matrix4x4 reflect = Matrix4x4.identity;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) reflect[i, j] = (i == j ? 1f : 0f) - 2f * lateral[i] * lateral[j];
        Matrix4x4 world = Matrix4x4.Translate(origin) * reflect * Matrix4x4.Translate(-origin);
        Matrix4x4 leftStance = StanceRoot(s.LeftRoot, s.LeftMarker, s.LeftFireport, origin, n, up, forward, out Matrix4x4 ownMirror);
        Matrix4x4 rightPalmOnLeftPistol = leftStance * (s.LeftRoot.worldToLocalMatrix * s.LeftMarker.localToWorldMatrix);
        Place(s.Model.transform, s.LeftRoot, world * leftStance * ownMirror);
        Solve(__instance.Limbs[0], world.MultiplyPoint3x4(rightPalmOnLeftPistol.GetColumn(3)), s.Hands.MirrorPalm(rightPalmOnLeftPistol.rotation));
        s.Hands.ApplyFingers(s.LeftGrip, s.RightHand);
        foreach (var twist in __instance.Twists) twist.Relax();
    }

    // Weapon_root pose that puts this pistol's grip marker at the right-hand stance point, barrel
    // along the stance direction and slide up (canted inward). The pistol's own axes come from
    // its geometry: barrel = the fireport axis closest to grip->muzzle, top = the muzzle's
    // offset above the grip, perpendicular to the barrel.
    // ownMirror: the reflection across the pistol's own mid-plane, in the returned root frame. The
    // plane passes through the fireport (on the bore axis), not the grip marker, which is the
    // right palm's contact beside the mid-plane. Combined with the body mirror it gives the left
    // pistol a proper (non-reflected) pose.
    // The weapon animator offsets Weapon_root_anim from Weapon_root, so neither the root's origin
    // nor its x axis is the pistol's mid-plane.
    private static Matrix4x4 StanceRoot(Transform root, Transform marker, Transform fireport, Vector3 origin, Vector3 n, Vector3 up, Vector3 forward, out Matrix4x4 ownMirror)
    {
        Vector3 toMuzzle = fireport.position - marker.position;
        Vector3 barrel = BestAxis(fireport, toMuzzle);
        // The grip marker is the right palm's contact, beside the gun's mid-plane; the top is
        // snapped to the fireport axis nearest the muzzle's offset so that offset adds no cant.
        Vector3 top = BestAxis(fireport, Vector3.ProjectOnPlane(toMuzzle, barrel), barrel);
        Quaternion gunWorld = Quaternion.LookRotation(barrel, top);
        Quaternion gunInRoot = Quaternion.Inverse(root.rotation) * gunWorld;

        Quaternion yaw = Quaternion.AngleAxis(OutwardYaw, up);
        Vector3 aim = Quaternion.AngleAxis(DownPitch, Vector3.Cross(up, yaw * forward)) * (yaw * forward);
        Vector3 slide = Quaternion.AngleAxis(InwardCant, aim) * up;
        Quaternion rootRotation = Quaternion.LookRotation(aim, slide) * Quaternion.Inverse(gunInRoot);

        Vector3 hand = origin + n * HandPoint.x + up * HandPoint.y + forward * HandPoint.z;
        Vector3 markerInRoot = root.InverseTransformPoint(marker.position);
        Vector3 markerLocal = Vector3.Scale(markerInRoot, root.lossyScale);
        Vector3 rootPosition = hand - rootRotation * markerLocal;
        Vector3 lateral = gunInRoot * Vector3.right;
        Matrix4x4 reflect = Matrix4x4.identity;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) reflect[i, j] = (i == j ? 1f : 0f) - 2f * lateral[i] * lateral[j];
        Vector3 boreLocal = Vector3.Scale(root.InverseTransformPoint(fireport.position), root.lossyScale);
        ownMirror = Matrix4x4.Translate(boreLocal) * reflect * Matrix4x4.Translate(-boreLocal);
        return Matrix4x4.TRS(rootPosition, rootRotation, Vector3.one);
    }

    // Moves a pistol model so that its Weapon_root takes the given pose.
    private static void Place(Transform model, Transform root, Matrix4x4 rootPose)
    {
        Matrix4x4 pose = rootPose * (root.worldToLocalMatrix * model.localToWorldMatrix);
        model.SetPositionAndRotation(pose.GetColumn(3), pose.rotation);
    }

    // The transform's principal axis (+/-) nearest a direction, optionally excluding one axis.
    // Runs every preview frame, so the six candidates are scored in order without an array.
    private static Vector3 BestAxis(Transform t, Vector3 direction, Vector3 exclude = default)
    {
        Vector3 forward = t.forward, up = t.up, right = t.right;
        Vector3 best = forward;
        float score = float.NegativeInfinity;
        Consider(forward); Consider(-forward); Consider(up); Consider(-up); Consider(right); Consider(-right);
        return best;

        void Consider(Vector3 axis)
        {
            if (exclude != default && Mathf.Abs(Vector3.Dot(axis, exclude)) > .5f) return;
            float d = Vector3.Dot(axis, direction);
            if (d > score) { score = d; best = axis; }
        }
    }

    private static void Solve(LimbIK limb, Vector3 position, Quaternion rotation)
    {
        limb.solver.IKPositionWeight = limb.solver.IKRotationWeight = 1f;
        limb.solver.SetIKPosition(position);
        limb.solver.SetIKRotation(rotation);
        limb.solver.Update();
    }

    private static void Release(MenuPlayerPoser __instance)
    {
        if (!Active.TryGetValue(__instance, out var s)) return;
        Active.Remove(__instance);
        if (s.Model != null) AssetPoolObject.ReturnToPool(s.Model);
        // Hand the right pistol back at its native pose (the stance moved it every frame).
        if (s.RightModel != null)
        {
            s.RightModel.localPosition = s.RightModelLocalPosition;
            s.RightModel.localRotation = s.RightModelLocalRotation;
        }
    }
}
