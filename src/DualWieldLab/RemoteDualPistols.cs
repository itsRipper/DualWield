using System;
using System.Collections.Generic;
using Comfort.Common;
using DualWieldLab.DualHolster;
using EFT;
using EFT.AssetsManager;
using EFT.InventoryLogic;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace DualWieldLab;

// The left pistol of an observed (network) player, as other machines see it.
//
// The pistol shown is a model of the real item in the observed inventory (attachments, magazine,
// suppressor), bound to that item: its rounds, chamber, malfunction, heat and wear follow the
// owner's snapshots, and its shots are replayed through a native shot context on that model. In
// third person the right pistol stays where EFT's animation and IK put it; after that pass the
// right pistol is moved out from the body's mid-plane when the two would overlap, the left pistol
// takes the mirror image of the right one (each pistol oriented by its own geometry), and the left
// arm is solved onto it with the body's own Final IK limb, as in the dual-holster menu preview.
internal static class RemoteDualPistols
{
    private sealed class Remote
    {
        internal Player Player = null!;
        internal OffhandSnapshot State;
        internal bool HasState;
        internal float ReconcileSince = -1f;
        // A copy still behind the owner is checked again (once a second after the mismatch report)
        // until it matches or a newer state arrives: a late inventory operation is then followed.
        internal float NextReconcile;
        internal bool MismatchReported;
        // Bound presentation (null while not shown).
        internal Weapon? Left;
        internal Player.FirearmController? Primary;
        internal GameObject? Model;
        internal WeaponPrefab? Prefab;
        internal GameObject? ContextObject;
        internal OffhandShotContext? Shot;
        internal Transform LeftRoot = null!, LeftMarker = null!, LeftFireport = null!;
        internal Transform? RightModel;
        internal Vector3 RightLocalPosition;
        internal Quaternion RightLocalRotation;
        internal bool RightMoved;
        internal HandMirror? Hands;
        internal GripPose? Grip;
        internal float FireResetAt = -1f;
        internal string? Refusal;
        // Pose diagnostics (logged on change only): which pass posed the pistol, why a pose was
        // skipped, and the left hand's IK target.
        internal string? PoseSource;
        internal string? PoseSkip;
        internal Vector3 PosedHandTarget;
        // A native right-pistol action that uses the left hand (reload, magazine/chamber check,
        // examine, malfunction) is running: the left arm stays native and the left pistol hidden.
        internal Renderer[]? Renderers;
        internal bool[]? RendererEnabled;
        internal string? NativeAction;
    }

    // Grip points closer to the mid-plane than this are moved out, so that the mirrored pistols
    // do not overlap: EFT's third-person pistol is held near the chest centre with both hands.
    private const float HalfSpacing = .17f;
    // A copy still behind the owner after this long is reported (DW_NET_MISMATCH) and then checked
    // once a second until it matches or a newer state arrives.
    private const float ReconcileSeconds = 3f;

    private static readonly Dictionary<Player, Remote> Remotes = new Dictionary<Player, Remote>();
    private static readonly AccessTools.FieldRef<Player, LimbIK[]> Limbs = AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");
    private static readonly List<Player> Stale = new List<Player>();

    internal static Action<string>? Log;

    internal static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.VisualPass)),
            prefix: new HarmonyMethod(typeof(RemoteDualPistols), nameof(BeforeVisualPass)) { priority = Priority.First },
            postfix: new HarmonyMethod(typeof(RemoteDualPistols), nameof(AfterVisualPass)) { priority = Priority.Last });
    }

    internal static void ApplyState(Player player, OffhandSnapshot state)
    {
        if (!Remotes.TryGetValue(player, out Remote remote))
            Remotes[player] = remote = new Remote { Player = player };
        if (remote.HasState && !Newer(state, remote.State)) return;
        remote.State = state;
        remote.HasState = true;
        BeginReconcile(remote);
        Log?.Invoke("DW_NET_STATE player=" + player.ProfileId + " seq=" + state.Sequence + " paired=" + state.Paired + " left=" + state.WeaponId
            + " chamber=" + (state.ChamberAmmo.Length > 0) + " mag=" + state.MagazineCount + " malf=" + (Weapon.EMalfunctionState)state.MalfunctionState
            + " action=" + state.Action);
        Refresh(remote);
    }

    internal static void ApplyShot(Player player, OffhandSnapshot after, OffhandShot shot)
    {
        after.Paired = true; // a shot implies a pair
        bool known = Remotes.TryGetValue(player, out Remote remote) && remote.HasState;
        OffhandSnapshot stored = known ? remote.State : default;
        bool replayed = false;
        if (!RemoteSyncPolicy.AcceptShot(known, stored, after, () => replayed = Replay(player, after, shot), out Exception? replayError))
        {
            Log?.Invoke("DW_NET_SHOT_STALE player=" + player.ProfileId + " seq=" + after.Sequence + " session=" + after.Session
                + " stored_seq=" + stored.Sequence + " stored_session=" + stored.Session);
            return;
        }
        if (replayError != null)
            Log?.Invoke("DW_NET shot replay failed for " + player.ProfileId + " (its state is still applied): " + replayError.Message);
        if (!Remotes.TryGetValue(player, out remote))
            Remotes[player] = remote = new Remote { Player = player };
        if (!remote.HasState || Newer(after, remote.State))
        {
            remote.State = after;
            remote.HasState = true;
            BeginReconcile(remote);
        }
        Refresh(remote);
        Log?.Invoke("DW_NET_SHOT player=" + player.ProfileId + " seq=" + after.Sequence + " replayed=" + replayed
            + " owner_mag=" + after.MagazineCount + " owner_chamber=" + (after.ChamberAmmo.Length > 0) + " copy=" + CopyState(remote));
    }

    // Shows the pair a shot implies, then fires the shot from the left model; true when a projectile
    // was fired. Called once per accepted shot (RemoteSyncPolicy.AcceptShot), which contains failures.
    private static bool Replay(Player player, OffhandSnapshot after, OffhandShot shot)
    {
        if (!Remotes.TryGetValue(player, out Remote remote) || !remote.HasState || !remote.State.Paired)
        {
            ApplyState(player, after);
            Remotes.TryGetValue(player, out remote);
        }
        if (remote?.Shot == null || remote.Left == null || remote.Left.Id.ToString() != after.WeaponId || !player.HealthController.IsAlive)
            return false;
        if (!(Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), shot.AmmoTemplate, null) is Ammo ammo)) return false;
        remote.Shot.ReplayRemoteShot(ammo, shot.Position, shot.Direction, shot.Overheat);
        remote.FireResetAt = Time.time + .1f;
        return true;
    }

    // The observed copy's left pistol: magazine count and chamber, for comparison with the owner.
    private static string CopyState(Remote remote)
    {
        Weapon? left = remote.Left;
        if (left == null)
        {
            var found = remote.Player.FindItemById(remote.State.WeaponId, false, false);
            left = found.Succeeded ? found.Value as Weapon : null;
        }
        if (left == null) return "missing";
        bool chamber = left.Chambers.Length > 0 && left.Chambers[0].ContainedItem is Ammo;
        return "mag:" + (left.GetCurrentMagazine()?.Count.ToString() ?? "none") + ",chamber:" + chamber + ",malf:" + left.MalfState.State;
    }

    internal static bool Newer(in OffhandSnapshot incoming, in OffhandSnapshot stored)
        => RemoteSyncPolicy.Newer(incoming, stored);

    private static void BeginReconcile(Remote remote)
    {
        remote.ReconcileSince = Time.unscaledTime;
        remote.NextReconcile = 0f;
        remote.MismatchReported = false;
    }

    // Per frame, from the runtime's Update: retries reconciliation that waited for an inventory
    // operation, and drops players that left, died or changed what they hold.
    internal static void Tick()
    {
        if (Remotes.Count == 0) return;
        Stale.Clear();
        foreach (var pair in Remotes)
        {
            Remote remote = pair.Value;
            if (remote.Player == null) { Stale.Add(pair.Key); continue; }
            if (remote.FireResetAt >= 0f && Time.time >= remote.FireResetAt) { remote.Shot?.ResetFire(); remote.FireResetAt = -1f; }
            bool bound = remote.Model != null;
            bool shouldShow = ShouldShow(remote);
            if (bound != shouldShow || (bound && !ReferenceEquals(remote.Player.HandsController, remote.Primary))) Refresh(remote);
            else if (remote.ReconcileSince >= 0f && Time.unscaledTime >= remote.NextReconcile) Reconcile(remote);
        }
        foreach (var player in Stale) Release(player);
    }

    internal static void Release(Player player)
    {
        if (player == null || !Remotes.TryGetValue(player, out Remote remote)) return;
        Unbind(remote);
        Remotes.Remove(player);
    }

    internal static void ReleaseAll()
    {
        foreach (var remote in Remotes.Values) Unbind(remote);
        Remotes.Clear();
    }

    private static bool ShouldShow(Remote remote)
    {
        Player player = remote.Player;
        return remote.HasState && remote.State.Paired && player.HealthController != null && player.HealthController.IsAlive
            && player.HandsController is Player.FirearmController controller && controller.Item is Pistol right
            && right.Id.ToString() != remote.State.WeaponId;
    }

    private static void Refresh(Remote remote)
    {
        bool show = ShouldShow(remote);
        if (remote.Model != null && (!show || !ReferenceEquals(remote.Player.HandsController, remote.Primary)
            || remote.Left == null || remote.Left.Id.ToString() != remote.State.WeaponId))
            Unbind(remote);
        if (show && remote.Model == null) Bind(remote);
        Reconcile(remote);
    }

    private static void Bind(Remote remote)
    {
        Player player = remote.Player;
        var primary = (Player.FirearmController)player.HandsController;
        var found = player.FindItemById(remote.State.WeaponId, false, false);
        if (!found.Succeeded || !(found.Value is Pistol left))
        {
            Refuse(remote, "the left pistol " + remote.State.WeaponId + " is not in this copy of the inventory yet");
            return;
        }
        if (!OffhandShotContext.IsEligible(left) || !OffhandShotContext.IsEligible(primary.Item))
        {
            Refuse(remote, "a pistol of the pair is not a supported magazine-fed pistol on this machine");
            return;
        }
        GameObject? model = null;
        try
        {
            model = Singleton<ObjectsFactory>.Instance.CreateItem(left, Player.GetVisibleToCamera(player), null, false);
            var prefab = model != null ? model.GetComponent<WeaponPrefab>() : null;
            if (prefab == null) throw new InvalidOperationException("the left pistol model could not be assembled");
            prefab.Parent(null);
            Transform Bone(ECharacterWeaponBones bone) => prefab.Hierarchy.GetTransform(bone) ?? throw new InvalidOperationException("missing " + bone);
            Transform rightRoot = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root)
                ?? throw new InvalidOperationException("the right pistol has no Weapon_root");
            remote.LeftRoot = Bone(ECharacterWeaponBones.Weapon_root);
            remote.LeftMarker = Bone(ECharacterWeaponBones.weapon_R_IK_marker);
            remote.LeftFireport = FindActive(model!.transform, WeaponPrefab.BONE_FIREPORT) ?? throw new InvalidOperationException("the left pistol has no fireport");
            remote.RightModel = rightRoot.parent ?? rightRoot;
            remote.RightLocalPosition = remote.RightModel.localPosition;
            remote.RightLocalRotation = remote.RightModel.localRotation;
            remote.RightMoved = false;
            int layer = LayerOf(rightRoot, model.layer);
            foreach (var t in model.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            foreach (var collider in model.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            model.transform.SetParent(player.PlayerBones.Ribcage.Original, true);
            model.SetActive(true);

            // Idle pose of the left pistol's own animator; it is advanced explicitly afterwards.
            var firearms = prefab.FirearmsAnimator;
            firearms.Animator.enabled = true;
            prefab.ResetStatesToDefault();
            firearms.SetActiveParam(true, resetLeftHand: false);
            firearms.SetFastHide(false);
            firearms.Animator.Play(firearms.FullIdleStateName, FirearmsAnimator.HANDS_LAYER_INDEX, 0f);
            firearms.Animator.Update(0f);
            firearms.Animator.enabled = false;

            remote.ContextObject = new GameObject("DualWieldLab.RemoteOffhand");
            remote.ContextObject.transform.SetParent(player.Transform.Original, false);
            remote.Shot = remote.ContextObject.AddComponent<OffhandShotContext>();
            remote.Shot.Initialize(player, left, primary, prefab, remote.LeftFireport, EPointOfView.ThirdPerson);
            remote.Model = model;
            remote.Prefab = prefab;
            remote.Left = left;
            remote.Primary = primary;
            remote.Refusal = null;
            if (player.HandPosers != null && player.HandPosers.Length > 1)
            {
                try { remote.Hands = HandMirror.Create(player.PlayerBody.transform, player.HandPosers[1].Children, player.HandPosers[0].Children); }
                catch (Exception e) { remote.Hands = null; Log?.Invoke("DW_NET left fingers not mirrored: " + e.Message); }
            }
            remote.Grip = RightHandGrip(model.transform);
            remote.PoseSource = null; remote.PoseSkip = null;
            remote.Renderers = model.GetComponentsInChildren<Renderer>(true);
            remote.RendererEnabled = Array.ConvertAll(remote.Renderers, r => r.enabled);
            remote.NativeAction = null;
            Log?.Invoke("DW_NET_BIND player=" + player.ProfileId + " right=" + primary.Item.Id + " left=" + left.Id
                + " fingers=" + (remote.Hands?.FingerCount ?? 0));
        }
        catch (Exception e)
        {
            Unbind(remote);
            if (model != null && remote.Model == null) AssetPoolObject.ReturnToPool(model);
            Refuse(remote, e.Message);
        }
    }

    private static void Refuse(Remote remote, string reason)
    {
        if (remote.Refusal == reason) return;
        remote.Refusal = reason;
        Log?.Invoke("DW_NET_UNSHOWN player=" + remote.Player.ProfileId + " reason=" + reason);
    }

    private static void Unbind(Remote remote)
    {
        RestoreRight(remote);
        // The pooled model goes back with its renderers as they were.
        SetLeftVisible(remote, true);
        remote.NativeAction = null; remote.Renderers = null; remote.RendererEnabled = null;
        try { remote.Shot?.DisposeContext(); }
        catch (Exception e) { Log?.Invoke("DW_NET context cleanup: " + e.Message); }
        if (remote.ContextObject != null) UnityEngine.Object.Destroy(remote.ContextObject);
        if (remote.Model != null)
        {
            try { AssetPoolObject.ReturnToPool(remote.Model); }
            catch (Exception e) { Log?.Invoke("DW_NET model return: " + e.Message); }
        }
        remote.Shot = null; remote.ContextObject = null; remote.Model = null; remote.Prefab = null;
        remote.Left = null; remote.Primary = null; remote.RightModel = null; remote.Hands = null; remote.Grip = null;
    }

    // The observed inventory copy follows the owner's snapshot: malfunction, fire mode, wear, and
    // the rounds the owner fired. Rounds are only ever removed here, never created.
    private static void Reconcile(Remote remote)
    {
        if (!remote.HasState || remote.ReconcileSince < 0f || string.IsNullOrEmpty(remote.State.WeaponId)) { remote.ReconcileSince = -1f; return; }
        Player player = remote.Player;
        Weapon? left = remote.Left;
        if (left == null)
        {
            var found = player.FindItemById(remote.State.WeaponId, false, false);
            left = found.Succeeded ? found.Value as Weapon : null;
        }
        bool waiting = false;
        if (left != null)
        {
            OffhandSnapshot s = remote.State;
            if (left.MalfState.State != (Weapon.EMalfunctionState)s.MalfunctionState) left.MalfState.State = (Weapon.EMalfunctionState)s.MalfunctionState;
            if (left.SelectedFireMode != (Weapon.EFireMode)s.FireMode && left.FireMode != null) left.FireMode.FireMode = (Weapon.EFireMode)s.FireMode;
            if (left.Repairable != null && s.Durability > 0f) left.Repairable.Durability = Mathf.Clamp(s.Durability, 0f, left.Repairable.MaxDurability);
            InventoryController inventory = player.InventoryController;
            Magazine? magazine = left.GetCurrentMagazine();
            string magazineId = magazine != null ? magazine.Id.ToString() : "";
            Slot? chamber = left.Chambers.Length > 0 ? left.Chambers[0] : null;
            bool ownerChambered = s.ChamberAmmo.Length > 0;
            // Only the rounds the owner fired are removed (RemoteSyncPolicy.Plan): a magazine swap or
            // chambering this copy has not applied yet is waited for, never compensated by removal.
            var plan = RemoteSyncPolicy.Plan(s.MagazineId, s.MagazineCount, ownerChambered,
                magazineId, magazine?.Count ?? 0, chamber?.ContainedItem is Ammo);
            if (plan.RemoveChambered && chamber != null) chamber.RemoveItemWithoutRestrictions();
            for (int i = 0; i < plan.RemoveFromMagazine && magazine != null && magazine.Cartridges.Last != null; i++)
                if (magazine.Cartridges.PopToNowhere(inventory).Failed) break;
            // Judged by what the copy holds now, so a removal that failed is never reported in sync.
            waiting = magazineId != s.MagazineId || (magazine?.Count ?? 0) != s.MagazineCount
                || (chamber?.ContainedItem is Ammo) != ownerChambered;
        }
        else waiting = true;
        if (waiting && (remote.MismatchReported || Time.unscaledTime - remote.ReconcileSince <= ReconcileSeconds))
        {
            remote.NextReconcile = remote.MismatchReported ? Time.unscaledTime + 1f : 0f;
            return;
        }
        Log?.Invoke((waiting ? "DW_NET_MISMATCH" : "DW_NET_SYNC") + " player=" + player.ProfileId + " seq=" + remote.State.Sequence
            + " owner=mag:" + remote.State.MagazineCount + ",chamber:" + (remote.State.ChamberAmmo.Length > 0) + ",magazine_id:" + remote.State.MagazineId
            + " copy=" + CopyState(remote) + ",magazine_id:" + (left?.GetCurrentMagazine()?.Id.ToString() ?? "none")
            + (waiting ? " (still followed: checked again every second until it matches or a newer state arrives)" : ""));
        if (waiting) { remote.MismatchReported = true; remote.NextReconcile = Time.unscaledTime + 1f; }
        else remote.ReconcileSince = -1f;
    }

    internal static void BeforeVisualPass(Player __instance)
    {
        if (Remotes.Count == 0 || !Remotes.TryGetValue(__instance, out Remote remote)) return;
        RestoreRight(remote);
    }

    private static void RestoreRight(Remote remote)
    {
        if (!remote.RightMoved || remote.RightModel == null) return;
        remote.RightModel.localPosition = remote.RightLocalPosition;
        remote.RightModel.localRotation = remote.RightLocalRotation;
        remote.RightMoved = false;
    }

    internal static void AfterVisualPass(Player __instance) => AfterPass(__instance, "Player.VisualPass");

    internal static void AfterPass(Player player, string source)
    {
        if (Remotes.Count == 0 || !Remotes.TryGetValue(player, out Remote remote) || remote.Model == null || remote.Primary == null) return;
        try
        {
            // Fika replays the owner's native right-pistol actions on this copy (CheckAmmo,
            // ReloadMag, ...). Those animate the magazine along the native left-hand path; a left
            // hand held on the left pistol left the magazine floating. The left arm is left to the
            // native pass and the left pistol hidden until the action ends, as in first person.
            string? native = NativeLeftHandAction(remote.Primary);
            if (native != remote.NativeAction)
            {
                remote.NativeAction = native;
                SetLeftVisible(remote, native == null);
                Log?.Invoke("DW_NET_NATIVE_ACTION player=" + player.ProfileId + " right_operation=" + (native ?? "none") + " left_pistol=" + (native == null ? "shown" : "hidden"));
            }
            if (native != null) return;
            string? skip = Pose(remote);
            if (skip != remote.PoseSkip)
            {
                remote.PoseSkip = skip;
                if (skip != null) Log?.Invoke("DW_NET_POSE_SKIP player=" + player.ProfileId + " source=" + source + " reason=" + skip);
            }
            if (skip == null && remote.PoseSource != source)
            {
                remote.PoseSource = source;
                Log?.Invoke("DW_NET_POSE player=" + player.ProfileId + " source=" + source + " left_hand_error=" + HandError(remote).ToString("F3") + " m");
            }
        }
        catch (Exception e)
        {
            Log?.Invoke("DW_NET pose failed, left pistol hidden: " + e.Message);
            Unbind(remote);
            remote.Refusal = "pose failed";
        }
    }

    // The right pistol's native operation when it uses the left hand, otherwise null. A fire-mode
    // check is one-handed; firing and plain idling keep the dual pose.
    internal static string? NativeLeftHandAction(Player.FirearmController? controller)
    {
        object? operation = controller?.CurrentOperation;
        switch (operation)
        {
            case null:
                return null;
            case Player.FirearmController.UtilityOperation utility:
                // Type names only: this runs every frame of the action and must not allocate.
                return utility._utilityOperationType == Player.FirearmController.UtilityOperation.EUtilityType.None
                    || utility._utilityOperationType == Player.FirearmController.UtilityOperation.EUtilityType.CheckFireMode
                    ? null : nameof(Player.FirearmController.UtilityOperation);
            case Player.FirearmController.MalfunctionOperation:
            case Player.FirearmController.BaseReloadOperation: // includes internal/cylinder magazines
            case Player.FirearmController.PullOutMagOperation:
            case Player.FirearmController.InsertMagOperation:
            case Player.FirearmController.RechamberOperation:
            case Player.FirearmController.DischargeOperation:
                return operation.GetType().Name;
            default:
                return null;
        }
    }

    private static void SetLeftVisible(Remote remote, bool visible)
    {
        if (remote.Renderers == null || remote.RendererEnabled == null) return;
        for (int i = 0; i < remote.Renderers.Length; i++)
            if (remote.Renderers[i] != null) remote.Renderers[i].enabled = visible && remote.RendererEnabled[i];
    }

    // Distance of the solved left hand from the target it was given (0 when the arm reaches it).
    private static float HandError(Remote remote)
    {
        LimbIK[]? limbs = Limbs(remote.Player);
        return limbs != null && limbs.Length > 0 && limbs[0] != null ? Vector3.Distance(limbs[0].solver.bone3.transform.position, remote.PosedHandTarget) : -1f;
    }

    // Returns why the pistol was not posed this frame, or null when it was.
    private static string? Pose(Remote remote)
    {
        Player player = remote.Player;
        if (player.PointOfView != EPointOfView.ThirdPerson) return "point of view " + player.PointOfView;
        LimbIK[]? limbs = Limbs(player);
        if (limbs == null || limbs.Length < 2 || limbs[0] == null || limbs[1] == null) return "no arm IK limbs";
        Player.FirearmController primary = remote.Primary!;
        Transform? rightRoot = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.Weapon_root);
        Transform? rightMarker = primary.HandsHierarchy.GetTransform(ECharacterWeaponBones.weapon_R_IK_marker);
        Transform? rightFireport = primary.CurrentFireport?.Original;
        if (rightRoot == null || rightMarker == null || rightFireport == null || remote.RightModel == null) return "right pistol bones missing";

        float dt = Time.deltaTime;
        remote.Prefab!.FirearmsAnimator.Animator.Update(dt);

        Vector3 leftShoulder = limbs[0].solver.bone1.transform.position, rightShoulder = limbs[1].solver.bone1.transform.position;
        Vector3 origin = (leftShoulder + rightShoulder) * .5f;
        // The mirror plane contains the aim: the shoulder axis without its component along the
        // right barrel. EFT's third-person pistol stance is bladed (shoulders about 33 degrees off the
        // aim); mirroring across the shoulders' own mid-plane turned the left barrel twice that
        // angle across the body (measured 67-69 degrees off the aim in Fika two-client tests).
        Vector3 aim = BestAxis(rightFireport, rightFireport.position - rightMarker.position);
        Vector3 n = Vector3.ProjectOnPlane(rightShoulder - leftShoulder, aim);
        if (n.sqrMagnitude < 1e-6f) return "shoulders along the aim";
        n.Normalize();

        // Right pistol: moved straight out when its grip sits too close to the mid-plane.
        float side = Vector3.Dot(rightMarker.position - origin, n);
        if (side < HalfSpacing)
        {
            remote.RightModel.position += n * (HalfSpacing - side);
            remote.RightMoved = true;
            Solve(limbs[1], rightMarker.position, rightMarker.rotation);
        }

        // Left pistol: placed as if the right hand held it where the right pistol is (grip marker
        // on the right grip marker, barrel and top along the right pistol's), then mirrored about
        // the mid-plane and across its own mid-plane, which gives a proper (unreflected) pose.
        Vector3 barrel = aim;
        Vector3 top = BestAxis(rightFireport, Vector3.ProjectOnPlane(rightFireport.position - rightMarker.position, barrel), barrel);
        Matrix4x4 asRight = RootForRightHand(remote.LeftRoot, remote.LeftMarker, remote.LeftFireport, rightMarker.position, barrel, top, out Matrix4x4 ownMirror);
        Matrix4x4 reflect = Matrix4x4.identity;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) reflect[i, j] = (i == j ? 1f : 0f) - 2f * n[i] * n[j];
        Matrix4x4 world = Matrix4x4.Translate(origin) * reflect * Matrix4x4.Translate(-origin);
        Matrix4x4 palmOnLeft = asRight * (remote.LeftRoot.worldToLocalMatrix * remote.LeftMarker.localToWorldMatrix);
        Place(remote.Model!.transform, remote.LeftRoot, world * asRight * ownMirror);
        // The palm is turned by the same plane as the pistol and the hand position (n), not by the
        // mesh's mid-plane: in the bladed stance those differ and the fingers closed beside the grip.
        Quaternion palmRotation = remote.Hands != null ? remote.Hands.MirrorPalm(palmOnLeft.rotation, n) : rightMarker.rotation;
        remote.PosedHandTarget = world.MultiplyPoint3x4(palmOnLeft.GetColumn(3));
        Solve(limbs[0], remote.PosedHandTarget, palmRotation);
        if (remote.Hands != null)
        {
            // EFT's left hand poser would keep the two-handed support grip on the right pistol.
            player.HandPosers[0].weight = 0f;
            remote.Hands.ApplyFingers(remote.Grip, player.HandPosers[1].Children);
        }
        return null;
    }

    // Weapon_root pose that puts this pistol's grip marker at handPoint with its barrel along
    // barrel and its top along top. ownMirror: the reflection across the pistol's own mid-plane
    // (through its bore), in the returned root frame.
    private static Matrix4x4 RootForRightHand(Transform root, Transform marker, Transform fireport, Vector3 handPoint, Vector3 barrel, Vector3 top, out Matrix4x4 ownMirror)
    {
        Vector3 toMuzzle = fireport.position - marker.position;
        Vector3 ownBarrel = BestAxis(fireport, toMuzzle);
        Vector3 ownTop = BestAxis(fireport, Vector3.ProjectOnPlane(toMuzzle, ownBarrel), ownBarrel);
        Quaternion gunInRoot = Quaternion.Inverse(root.rotation) * Quaternion.LookRotation(ownBarrel, ownTop);
        Quaternion rootRotation = Quaternion.LookRotation(barrel, top) * Quaternion.Inverse(gunInRoot);
        Vector3 markerLocal = Vector3.Scale(root.InverseTransformPoint(marker.position), root.lossyScale);
        Vector3 rootPosition = handPoint - rootRotation * markerLocal;
        Vector3 lateral = gunInRoot * Vector3.right;
        Matrix4x4 reflect = Matrix4x4.identity;
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) reflect[i, j] = (i == j ? 1f : 0f) - 2f * lateral[i] * lateral[j];
        Vector3 boreLocal = Vector3.Scale(root.InverseTransformPoint(fireport.position), root.lossyScale);
        ownMirror = Matrix4x4.Translate(boreLocal) * reflect * Matrix4x4.Translate(-boreLocal);
        return Matrix4x4.TRS(rootPosition, rootRotation, Vector3.one);
    }

    private static void Place(Transform model, Transform root, Matrix4x4 rootPose)
    {
        Matrix4x4 pose = rootPose * (root.worldToLocalMatrix * model.localToWorldMatrix);
        model.SetPositionAndRotation(pose.GetColumn(3), pose.rotation);
    }

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

    private static GripPose? RightHandGrip(Transform model)
    {
        GripPose? best = null;
        foreach (var grip in model.GetComponentsInChildren<GripPose>(true))
            if (grip.Hand == GripPose.EHand.Right && (best == null || (grip.IsAlternative && !best.IsAlternative))) best = grip;
        return best;
    }

    private static int LayerOf(Transform weapon, int fallback)
    {
        foreach (var renderer in weapon.GetComponentsInChildren<Renderer>(false))
            if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer) return renderer.gameObject.layer;
        return fallback;
    }

    private static Transform? FindActive(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(false)) if (t.name == name) return t;
        return null;
    }
}
