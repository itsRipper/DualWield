using System;
using System.Collections.Generic;
using EFT;
using AnimationSystem;
using UnityEngine;
using NQuaternion = System.Numerics.Quaternion;

namespace DualWieldLab;

// Only an evaluated, nonfinite pose is recoverable. Destroyed bones, ownership
// changes and programming errors still take the ordinary fail-safe path.
internal sealed class NativePoseUnavailableException : Exception
{
    internal NativePoseUnavailableException(string invariant) : base(invariant) { }
}

internal sealed class NativeArmRetarget
{
    private struct BoneState
    {
        internal Transform Bone;
        internal Quaternion Saved;
        internal Quaternion Applied;
    }

    private static readonly string[] ArmNames = { "Upperarm", "Forearm1", "Forearm2", "Forearm3", "Palm" };
    // Zero-based digit index of the index (trigger) finger: HumanLDigit2x.
    internal const int TriggerFingerIndex = 1;
    private readonly Transform _body;
    private readonly Player _player;
    private readonly Transform[] _left, _right;
    private readonly Quaternion[] _leftBindBody, _rightBindBody, _leftSourceBody, _rightSourceBody;
    private readonly Quaternion[] _rightLocalSource, _rightGripSourceBody, _rightGripReference, _ownGripReference;
    private readonly Transform[] _ownGripDonors;
    private readonly Transform[] _ownArmDonors = new Transform[ArmNames.Length];
    private readonly Quaternion[] _ownArmReference = new Quaternion[ArmNames.Length];
    private readonly BoneState[] _states;
    private readonly SkinnedMeshRenderer[] _renderers;
    private readonly bool[] _offscreen;
    private bool _leftApplied, _rightApplied, _restored;
    private bool _ownGrip;
    private bool _isolatePrimaryAction;
    private bool _ownSelector, _alternateLocomotion, _nativeLocomotionActive;
    private bool _nativeLocomotionRequested;
    private float _nativeLocomotionWeight;
    private Quaternion _sourceLookBody;
    private Vector3 _rightPalmInLook;
    private Vector3 _leftPalmInLook;
    // While the source is held (a right fire-mode clip) EFT still animates
    // both shoulders, so a held pose re-applied around them drifts. The held target keeps the last
    // live frame's shoulder centre relative to the shoulders' common parent (body motion and sway,
    // not the right arm's own motion) and its palm offset in the look frame, so it turns with the
    // view exactly as the live target does: the ribcage follows yaw but only part of a pitch.
    private readonly Transform _shoulderParent;
    private Vector3 _centreInParent, _palmInLookHeld, _releaseCentreInParent, _releasePalmInLook;
    private bool _palmTargetStored, _sourceHeld;
    // A hold ends by easing from the held target back to the live one.
    private const float ReleaseSeconds = .15f;
    private const float MaxHeldDeviation = .2f;
    internal int HeldTargetRejections { get; private set; }
    private float _releaseStartedAt = float.NegativeInfinity;
    private bool _releasePending;
    internal void IsolatePrimaryAction(bool value) => _isolatePrimaryAction = value;
    internal void OwnSelector(bool value) => _ownSelector = value;
    internal void EnableAlternatingLocomotion() => _alternateLocomotion = true;
    internal float LocomotionDepthCorrection => 0f;
    internal bool NativeLocomotionActive => _nativeLocomotionActive;
    internal float NativeLocomotionPhase { get; private set; }
    internal float NativeLocomotionWeight => _nativeLocomotionWeight;
    internal Vector3 LeftPalmTarget
    {
        get
        {
            if (_ownSelector) return LeftPalm.position;
            Vector3 target = LivePalmTarget();
            if (_sourceHeld && _palmTargetStored)
            {
                Vector3 held = _shoulderParent.TransformPoint(_centreInParent) + _player.TargetHandsRotation * _palmInLookHeld;
                // Never let a held target pull the hand far from where the live pose would put it.
                if ((held - target).sqrMagnitude <= MaxHeldDeviation * MaxHeldDeviation) return held;
                HeldTargetRejections++;
            }
            float release = ReleaseProgress();
            if (release < 1f)
                target = Vector3.Lerp(_shoulderParent.TransformPoint(_releaseCentreInParent) + _player.TargetHandsRotation * _releasePalmInLook,
                    target, Mathf.SmoothStep(0f, 1f, release));
            return target;
        }
    }

    private float ReleaseProgress()
    {
        if (_releasePending) { _releaseStartedAt = Time.time; _releasePending = false; }
        return (Time.time - _releaseStartedAt) / ReleaseSeconds;
    }

    private Vector3 LivePalmTarget()
    {
        Vector3 centre = (_left[0].position + _right[0].position) * .5f;
        System.Numerics.Vector3 reflected = NativeArmRetargetMath.ReflectPalm(
            new System.Numerics.Vector3(_rightPalmInLook.x, _rightPalmInLook.y, _rightPalmInLook.z));
        Vector3 local = Vector3.Lerp(new Vector3(reflected.X, reflected.Y, reflected.Z), _leftPalmInLook, _nativeLocomotionWeight);
        return centre + _player.TargetHandsRotation * local;
    }
    internal bool HasSnapshot { get; private set; }
    internal int SnapshotFrame { get; private set; } = -1;
    internal int BoneCount => _left.Length;
    internal int FingerCount => _left.Length - ArmNames.Length;
    internal Transform LeftUpperArm => _left[0];
    internal Transform LeftElbow => _left[1];
    internal Transform LeftPalm => _left[4];
    internal Transform RightPalm => _right[4];
    // finger 1 = thumb .. 5; segment 1..3 (native HumanLDigit<finger><segment>).
    internal Transform LeftDigit(int finger, int segment) => _left[ArmNames.Length + (finger - 1) * 3 + segment - 1];
    internal Transform RightUpperArm => _right[0];
    internal Transform RightElbow => _right[1];
    internal int ChainLength => _left.Length;
    internal SkinnedMeshRenderer[] Renderers => _renderers;

    private NativeArmRetarget(Player player, Transform body, Transform[] left, Transform[] right,
        Quaternion[] leftBind, Quaternion[] rightBind, SkinnedMeshRenderer[] renderers)
    {
        _player = player; _body = body; _left = left; _right = right;
        _shoulderParent = CommonParent(left[0], right[0]) ?? body;
        _leftBindBody = leftBind; _rightBindBody = rightBind;
        _leftSourceBody = new Quaternion[left.Length]; _rightSourceBody = new Quaternion[right.Length];
        _rightLocalSource = new Quaternion[right.Length]; _rightGripSourceBody = new Quaternion[right.Length];
        _rightGripReference = new Quaternion[right.Length]; _ownGripReference = new Quaternion[right.Length];
        _ownGripDonors = new Transform[right.Length];
        _states = new BoneState[left.Length * 2];
        for (int i = 0; i < left.Length; i++)
        {
            _states[i].Bone = left[i];
            _states[i + left.Length].Bone = right[i];
        }
        _renderers = renderers;
        _offscreen = new bool[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        { _offscreen[i] = renderers[i].updateWhenOffscreen; renderers[i].updateWhenOffscreen = true; }
    }

    private static Transform? CommonParent(Transform a, Transform b)
    {
        for (Transform? x = a.parent; x != null; x = x.parent)
            if (b.IsChildOf(x)) return x;
        return null;
    }

    internal static NativeArmRetarget Create(Player player)
    {
        if (player == null || !player.FirstPersonPointOfView || player.PlayerBody == null
            || !player.PlayerBody.BodySkins.TryGetValue(EBodyModelPart.Hands, out var skin) || skin == null)
            throw new InvalidOperationException("The native first-person Hands skin is unavailable.");
        var bones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        var bindWorld = new Dictionary<string, Quaternion>(StringComparer.Ordinal);
        var renderers = new List<SkinnedMeshRenderer>();
        bool visible = false;
        foreach (Renderer renderer in skin.GetRenderers())
        {
            if (!(renderer is SkinnedMeshRenderer skinned)) continue;
            renderers.Add(skinned);
            visible |= skinned.enabled && skinned.gameObject.activeInHierarchy;
            Transform[] array = skinned.bones;
            Matrix4x4[] bind = skinned.sharedMesh != null ? skinned.sharedMesh.bindposes : Array.Empty<Matrix4x4>();
            for (int i = 0; i < array.Length; i++)
            {
                Transform bone = array[i];
                if (bone == null) continue;
                if (!bones.ContainsKey(bone.name)) bones.Add(bone.name, bone);
                if (i < bind.Length && !bindWorld.ContainsKey(bone.name))
                    bindWorld.Add(bone.name, (skinned.transform.localToWorldMatrix * bind[i].inverse).rotation);
            }
        }
        if (!visible) throw new InvalidOperationException("No active native first-person hand renderer is available.");
        var suffixes = new List<string>(ArmNames);
        for (int finger = 1; finger <= 5; finger++)
            for (int segment = 1; segment <= 3; segment++) suffixes.Add("Digit" + finger + segment);
        var left = new Transform[suffixes.Count]; var right = new Transform[suffixes.Count];
        var leftBind = new Quaternion[left.Length]; var rightBind = new Quaternion[right.Length];
        Transform body = player.Transform.Original;
        Quaternion inverseBody = Quaternion.Inverse(body.rotation);
        for (int i = 0; i < suffixes.Count; i++)
        {
            left[i] = FindNamed(bones, "HumanL" + suffixes[i]);
            right[i] = FindNamed(bones, "HumanR" + suffixes[i]);
            if (ReferenceEquals(left[i], right[i]) || left[i].IsChildOf(right[0]) || right[i].IsChildOf(left[0]))
                throw new InvalidOperationException("The native arm branches are not independent.");
            if (!bindWorld.TryGetValue(left[i].name, out Quaternion lb) || !bindWorld.TryGetValue(right[i].name, out Quaternion rb))
                throw new InvalidOperationException("The native hand lacks a bind reference for " + suffixes[i]);
            leftBind[i] = inverseBody * lb; rightBind[i] = inverseBody * rb;
            if (!NativeArmRetargetMath.IsValid(ToNumerics(leftBind[i])) || !NativeArmRetargetMath.IsValid(ToNumerics(rightBind[i])))
                throw new InvalidOperationException("The native hand has an invalid bind rotation for " + suffixes[i]);
        }
        for (int i = 1; i < ArmNames.Length; i++)
            if (!left[i].IsChildOf(left[i - 1]) || !right[i].IsChildOf(right[i - 1]))
                throw new InvalidOperationException("The native forearm/twist/palm chain differs from the inspected rig.");
        for (int i = ArmNames.Length; i < left.Length; i++)
        {
            int parent = (i - ArmNames.Length) % 3 == 0 ? 4 : i - 1;
            if (left[i].parent != left[parent] || right[i].parent != right[parent])
                throw new InvalidOperationException("The native finger segment hierarchy differs from the inspected rig.");
        }
        return new NativeArmRetarget(player, body, left, right, leftBind, rightBind, renderers.ToArray());
    }

    private static Transform FindNamed(Dictionary<string, Transform> bones, string suffix)
    {
        foreach (var pair in bones)
            if (pair.Key.EndsWith(suffix, StringComparison.Ordinal)) return pair.Value;
        throw new InvalidOperationException("Missing native first-person bone " + suffix);
    }

    internal void ConfigureFingerGrip(Transform donorPalm)
    {
        for (int i = 0; i < ArmNames.Length; i++)
        {
            Transform? donor = donorPalm;
            while (donor != null && !donor.name.EndsWith("HumanR" + ArmNames[i], StringComparison.Ordinal)) donor = donor.parent;
            if (donor == null) throw new InvalidOperationException("The native selector arm chain is unavailable: " + ArmNames[i]);
            _ownArmDonors[i] = donor;
            _ownArmReference[i] = donor.localRotation;
        }
        var donorBones = new Dictionary<string, Transform>(StringComparer.Ordinal);
        foreach (Transform bone in donorPalm.GetComponentsInChildren<Transform>(true)) donorBones[bone.name] = bone;
        for (int i = ArmNames.Length; i < _right.Length; i++)
        {
            int finger = (i - ArmNames.Length) / 3 + 1, segment = (i - ArmNames.Length) % 3 + 1;
            Transform donor = FindNamed(donorBones, "HumanRDigit" + finger + segment);
            _rightGripReference[i] = _right[i].localRotation;
            _ownGripReference[i] = donor.localRotation;
            _ownGripDonors[i] = donor;
            if (!NativeArmRetargetMath.IsValid(ToNumerics(_rightGripReference[i])) || !NativeArmRetargetMath.IsValid(ToNumerics(_ownGripReference[i])))
                throw new InvalidOperationException("The native pistol finger grip contains an invalid reference rotation.");
        }
        _ownGrip = true;
    }

    // The left fingers follow the right fingers' motion away from this reference, so it must be
    // the right hand's native IDLE grip. Captured at pairing it is only valid when the pair is
    // made at idle; a pair built during the native draw re-captures it at the first idle frame
    // (or starts from a template's earlier idle capture). Call after the native evaluation.
    internal Quaternion[] CaptureRightGripReference()
    {
        var reference = new Quaternion[_right.Length];
        for (int i = ArmNames.Length; i < _right.Length; i++) reference[i] = _right[i].localRotation;
        UseRightGripReference(reference);
        return reference;
    }

    internal void UseRightGripReference(Quaternion[] reference)
    {
        for (int i = ArmNames.Length; i < _right.Length; i++)
            if (NativeArmRetargetMath.IsValid(ToNumerics(reference[i]))) _rightGripReference[i] = reference[i];
    }

    internal void CaptureNativePose()
    {
        if (_restored) return;
        if (_leftApplied || _rightApplied)
            throw new InvalidOperationException("Restore borrowed arm overrides before capturing native source poses.");
        Quaternion inverseBody = Quaternion.Inverse(_body.rotation);
        bool holdSource = _isolatePrimaryAction && HasSnapshot;
        Quaternion look = _player.TargetHandsRotation;
        if (!NativeArmRetargetMath.IsValid(ToNumerics(_body.rotation))
            || !NativeArmRetargetMath.IsValid(ToNumerics(look))
            || !Finite(_left[0].position) || !Finite(_right[0].position)
            || !Finite(_left[4].position) || !Finite(_right[4].position))
        {
            HasSnapshot = false; SnapshotFrame = -1;
            throw new NativePoseUnavailableException("nonfinite_native_frame body=" + _body.name
                + " palms=" + _left[4].name + "/" + _right[4].name
                + " movement=" + _player.MovementContext.CurrentState.Name);
        }
        // The left mirrors the right arm without EFT's procedural recoil on the right weapon root.
        bool recoil = PrimaryRecoil(out Vector3 recoilPivot, out Quaternion recoilUndo, out Vector3 recoilOffset);
        if (_sourceHeld && !holdSource) { _releaseCentreInParent = _centreInParent; _releasePalmInLook = _palmInLookHeld; _releasePending = true; }
        _sourceHeld = holdSource;
        if (!holdSource)
        {
            _sourceLookBody = inverseBody * look;
            Vector3 centre = (_left[0].position + _right[0].position) * .5f;
            Vector3 rightPalm = recoil ? recoilPivot + recoilUndo * (_right[4].position - recoilPivot) - recoilOffset : _right[4].position;
            _rightPalmInLook = Quaternion.Inverse(look) * (rightPalm - centre);
            _leftPalmInLook = Quaternion.Inverse(look) * (_left[4].position - centre);
        }
        for (int i = 0; i < _left.Length; i++)
        {
            if (!holdSource)
            {
                _leftSourceBody[i] = inverseBody * _left[i].rotation;
                // The palm and fingers move rigidly with the recoiling weapon root; the upper arm and
                // forearm only bend with it, and the arm solve re-aims them at the target.
                _rightSourceBody[i] = inverseBody * (recoil && i >= 4 ? recoilUndo * _right[i].rotation : _right[i].rotation);
                _rightLocalSource[i] = _right[i].localRotation;
            }
            if (!NativeArmRetargetMath.IsValid(ToNumerics(_leftSourceBody[i])) || !NativeArmRetargetMath.IsValid(ToNumerics(_rightSourceBody[i]))
                || !NativeArmRetargetMath.IsValid(ToNumerics(_rightLocalSource[i])))
            {
                HasSnapshot = false;
                SnapshotFrame = -1;
                throw new NativePoseUnavailableException("nonfinite_arm_rotation bone=" + _left[i].name
                    + "/" + _right[i].name + " movement=" + _player.MovementContext.CurrentState.Name);
            }
        }
        HasSnapshot = true;
        SnapshotFrame = Time.frameCount;
        if (_alternateLocomotion)
        {
            EPlayerState state = _player.MovementContext.CurrentState.Name;
            bool requested = state == EPlayerState.Sprint || state == EPlayerState.ProneMove;
            IAnimator animator = _player.BodyAnimatorCommon;
            NativeLocomotionPhase = animator.GetCurrentAnimatorStateInfo(PlayerAnimator.BASE_LAYER_INDEX).normalizedTime;
            if (animator.IsInTransition(PlayerAnimator.BASE_LAYER_INDEX))
            {
                // EFT's UnityAnimatorWrapper interface method throws unconditionally.
                // Use its supported Unity Animator, retaining the native fast-animator
                // implementation for wrappers that actually implement this contract.
                float transition = Mathf.Clamp01(animator is UnityAnimatorWrapper unity
                    ? unity.Animator.GetAnimatorTransitionInfo(PlayerAnimator.BASE_LAYER_INDEX).normalizedTime
                    : animator.GetTransitionNormalizedTime(PlayerAnimator.BASE_LAYER_INDEX));
                if (float.IsNaN(transition) || float.IsInfinity(transition)) transition = 0f;
                _nativeLocomotionWeight = requested ? transition
                    : _nativeLocomotionRequested || _nativeLocomotionWeight > 0f ? 1f - transition : 0f;
            }
            else _nativeLocomotionWeight = requested ? 1f : 0f;
            _nativeLocomotionRequested = requested;
            _nativeLocomotionActive = _nativeLocomotionWeight > 0f;
        }
        // Only the native pass stores the target a later hold keeps; other readers (diagnostics)
        // may see the arms in another state.
        if (!holdSource)
        {
            Vector3 centre = (_left[0].position + _right[0].position) * .5f;
            _centreInParent = _shoulderParent.InverseTransformPoint(centre);
            _palmInLookHeld = Quaternion.Inverse(_player.TargetHandsRotation) * (LivePalmTarget() - centre);
            _palmTargetStored = true;
        }
    }

    // EFT's first-person recoil (ProceduralWeaponAnimation.ApplyPosition, ApplyComplexRotation):
    // the weapon root is offset by the shot effect's hand + weapon position recoil, then rotated
    // about the recoil pivot by its hand + weapon rotation recoil. The right hand follows that root.
    // Out: the world rotation undoing the recoil about the pivot, then the world offset to remove.
    private bool PrimaryRecoil(out Vector3 pivot, out Quaternion undo, out Vector3 offset)
    {
        pivot = offset = Vector3.zero; undo = Quaternion.identity;
        var pwa = _player.ProceduralWeaponAnimation;
        Transform? root = pwa?.HandsContainer?.WeaponRootAnim;
        if (pwa == null || root == null || root.parent == null || pwa.IsMountedState
            || !PrimaryRecoilSample.TryGet(pwa, out Vector3 rotation, out Vector3 position)) return false;
        if (!Finite(rotation) || !Finite(position) || (rotation.sqrMagnitude < 1e-8f && position.sqrMagnitude < 1e-10f)) return false;
        // EFT builds the rotation from the pre-recoil root rotation; one refinement from the final one.
        Quaternion final = root.rotation;
        Quaternion kick = Quaternion.Euler(final * rotation);
        kick = Quaternion.Euler((Quaternion.Inverse(kick) * final) * rotation);
        // The pivot rides on the root, so it is where it was before the recoil rotation.
        pivot = root.position + final * pwa.HandsContainer.RecoilPivot;
        undo = Quaternion.Inverse(kick);
        offset = root.parent.TransformVector(position);
        return true;
    }

    internal void ApplyRightToLeft()
    {
        if (_restored || !HasSnapshot) return;
        RestoreDriven();
        SaveSide(0);
        Quaternion[] source = _rightSourceBody;
        if (_ownGrip)
        {
            for (int i = 0; i < ArmNames.Length; i++) _rightGripSourceBody[i] = source[i];
            for (int i = ArmNames.Length; i < _right.Length; i++)
            {
                int parent = (i - ArmNames.Length) % 3 == 0 ? 4 : i - 1;
                // The index finger (digit 2) is the trigger finger: the right trigger pull must not
                // pull the left trigger finger too, so it keeps its own grip (no right motion).
                bool triggerFinger = (i - ArmNames.Length) / 3 == TriggerFingerIndex;
                NQuaternion local = NativeArmRetargetMath.ApplyGripMotion(ToNumerics(_rightGripReference[i]),
                    ToNumerics(_ownSelector && _ownGripDonors[i] != null ? _ownGripDonors[i].localRotation : _ownGripReference[i]),
                    ToNumerics(triggerFinger ? _rightGripReference[i] : _rightLocalSource[i]));
                _rightGripSourceBody[i] = _rightGripSourceBody[parent] * new Quaternion(local.X, local.Y, local.Z, local.W);
            }
            source = _rightGripSourceBody;
        }
        ApplySide(_left, source, _rightBindBody, _leftBindBody);
        if (_nativeLocomotionActive)
        {
            Quaternion bodyRotation = _body.rotation;
            Quaternion lookBody = Quaternion.Inverse(bodyRotation) * _player.TargetHandsRotation;
            Quaternion commonLook = lookBody * Quaternion.Inverse(_sourceLookBody);
            for (int i = 0; i < ArmNames.Length; i++)
                _left[i].rotation = Quaternion.Slerp(_left[i].rotation,
                    bodyRotation * commonLook * _leftSourceBody[i], _nativeLocomotionWeight);
        }
        if (_ownSelector)
        {
            for (int i = 0; i < ArmNames.Length; i++)
            {
                NQuaternion delta = NativeActionPoseMath.LocalDelta(ToNumerics(_ownArmReference[i]),
                    ToNumerics(_ownArmDonors[i].localRotation), ToNumerics(_rightBindBody[i]), ToNumerics(_leftBindBody[i]));
                _left[i].localRotation *= new Quaternion(delta.X, delta.Y, delta.Z, delta.W);
            }
        }
        _leftApplied = true;
        CommitOverrides();
    }

    // Right-arm writes are bracketed like the left: saved after native evaluation,
    // restored before the next one. Call after ApplyRightToLeft in the same pass.
    internal void BeginRightOverride()
    {
        if (_restored || _rightApplied) return;
        SaveSide(_left.Length);
        _rightApplied = true;
    }

    // 0 = bind-pose (open) fingers, 1 = the live native grip.
    internal void ApplyRightFingerCurl(float curl)
    {
        if (!_rightApplied) return;
        curl = Mathf.Clamp01(curl);
        for (int i = ArmNames.Length; i < _right.Length; i++)
        {
            int parent = (i - ArmNames.Length) % 3 == 0 ? 4 : i - 1;
            Quaternion open = Quaternion.Inverse(_rightBindBody[parent]) * _rightBindBody[i];
            _right[i].localRotation = Quaternion.Slerp(open, _right[i].localRotation, curl);
        }
    }

    internal void CopyLeftLocal(Quaternion[] buffer)
    {
        for (int i = 0; i < _left.Length; i++) buffer[i] = _left[i].localRotation;
    }

    // weight 0 = the supplied pose, 1 = whatever the left arm currently holds.
    internal void BlendLeftFrom(Quaternion[] from, float weight)
    {
        if (_restored) return;
        if (!_leftApplied) { SaveSide(0); _leftApplied = true; }
        weight = Mathf.Clamp01(weight);
        for (int i = 0; i < _left.Length; i++)
            _left[i].localRotation = Quaternion.Slerp(from[i], _left[i].localRotation, weight);
        CommitOverrides();
    }

    private void ApplySide(Transform[] targets, Quaternion[] source, Quaternion[] sourceBind, Quaternion[] targetBind)
    {
        Quaternion bodyRotation = _body.rotation;
        Quaternion lookBody = Quaternion.Inverse(bodyRotation) * _player.TargetHandsRotation;
        for (int i = 0; i < targets.Length; i++)
        {
            NQuaternion result = NativeArmRetargetMath.TransferLook(ToNumerics(source[i]), ToNumerics(sourceBind[i]),
                ToNumerics(targetBind[i]), ToNumerics(_sourceLookBody), ToNumerics(lookBody));
            targets[i].rotation = bodyRotation * new Quaternion(result.X, result.Y, result.Z, result.W);
        }
    }

    private void SaveSide(int offset)
    {
        for (int i = offset; i < offset + _left.Length; i++) _states[i].Saved = _states[i].Bone.localRotation;
    }

    internal void CommitOverrides()
    {
        for (int i = 0; i < _states.Length; i++)
            if ((i < _left.Length && _leftApplied) || (i >= _left.Length && _rightApplied))
                _states[i].Applied = _states[i].Bone.localRotation;
    }

    internal void RestoreBeforeNativePose()
    {
        RestoreDriven();
    }

    private void RestoreDriven()
    {
        for (int i = 0; i < _states.Length; i++)
        {
            if ((i < _left.Length && !_leftApplied) || (i >= _left.Length && !_rightApplied)) continue;
            Transform bone = _states[i].Bone;
            if (bone != null && SameWrittenRotation(bone.localRotation, _states[i].Applied))
                bone.localRotation = _states[i].Saved;
        }
        _leftApplied = _rightApplied = false;
    }

    internal void Restore()
    {
        if (_restored) return;
        RestoreBeforeNativePose();
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].updateWhenOffscreen = _offscreen[i];
        HasSnapshot = false;
        SnapshotFrame = -1;
        _nativeLocomotionActive = false;
        _nativeLocomotionRequested = false;
        _nativeLocomotionWeight = 0f;
        _restored = true;
    }

    private static NQuaternion ToNumerics(Quaternion q) => new NQuaternion(q.x, q.y, q.z, q.w);

    private static bool Finite(Vector3 v) => !(float.IsNaN(v.x) || float.IsInfinity(v.x)
        || float.IsNaN(v.y) || float.IsInfinity(v.y) || float.IsNaN(v.z) || float.IsInfinity(v.z));

    private static bool SameWrittenRotation(Quaternion current, Quaternion written)
    {
        float sign = Quaternion.Dot(current, written) < 0f ? -1f : 1f;
        float x = current.x - written.x * sign, y = current.y - written.y * sign;
        float z = current.z - written.z * sign, w = current.w - written.w * sign;
        return x * x + y * y + z * z + w * w <= .0000000001f;
    }
}
