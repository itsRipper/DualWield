using System;
using System.Collections.Generic;
using UnityEngine;

namespace DualWieldLab.DualHolster;

// Mirrors a right-hand pose onto the left hand of the third-person skeleton. The left and right
// hand/finger bones do not share axis conventions, so a right-hand rotation is reflected in the
// skinned mesh's space and corrected by the bind-pose difference of the two bones:
//   Delta = mirror(R_bind)^-1 * L_bind             (per bone pair, mesh space)
//   L_world = mirror(R_world) * Delta              (palm)
//   L_local = Delta_parent^-1 * mirror(R_local) * Delta   (fingers)
// so the bind pose maps onto itself and any right pose onto its mirror image.
internal sealed class HandMirror
{
    private readonly struct Finger
    {
        internal readonly Transform Left;
        internal readonly int RightIndex;
        internal readonly Quaternion ParentDelta, Delta;
        internal Finger(Transform left, int rightIndex, Quaternion parentDelta, Quaternion delta)
        { Left = left; RightIndex = rightIndex; ParentDelta = parentDelta; Delta = delta; }
    }

    private readonly Transform _mesh;
    private readonly int _axis;
    private readonly float _rightSide;
    private readonly Quaternion _palmDelta;
    private readonly Finger[] _fingers;

    internal int FingerCount => _fingers.Length;

    private HandMirror(Transform mesh, int axis, float rightSide, Quaternion palmDelta, Finger[] fingers)
    {
        _mesh = mesh; _axis = axis; _rightSide = rightSide; _palmDelta = palmDelta; _fingers = fingers;
    }

    // right/left: the hand posers' bone lists (index 0 = palm), as EFT's HandPoser indexes grips.
    internal static HandMirror Create(Transform body, Transform[] right, Transform[] left)
    {
        SkinnedMeshRenderer? skin = null;
        foreach (var candidate in body.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (Array.IndexOf(candidate.bones, right[0]) >= 0 && Array.IndexOf(candidate.bones, left[0]) >= 0) { skin = candidate; break; }
        if (skin == null || skin.sharedMesh == null) throw new InvalidOperationException("no skinned mesh carries both hands");
        Transform[] bones = skin.bones;
        Matrix4x4[] bindposes = skin.sharedMesh.bindposes;
        bool Rest(Transform bone, out Matrix4x4 rest)
        {
            int i = Array.IndexOf(bones, bone);
            rest = i >= 0 && i < bindposes.Length ? bindposes[i].inverse : default;
            return i >= 0 && i < bindposes.Length;
        }
        if (!Rest(right[0], out Matrix4x4 rightPalm) || !Rest(left[0], out Matrix4x4 leftPalm))
            throw new InvalidOperationException("the palms have no bind pose");
        Vector3 across = (Vector3)rightPalm.GetColumn(3) - (Vector3)leftPalm.GetColumn(3);
        int axis = Mathf.Abs(across.x) >= Mathf.Abs(across.y) ? (Mathf.Abs(across.x) >= Mathf.Abs(across.z) ? 0 : 2) : (Mathf.Abs(across.y) >= Mathf.Abs(across.z) ? 1 : 2);
        var deltas = new Dictionary<Transform, Quaternion>();
        Quaternion Delta(Matrix4x4 r, Matrix4x4 l) => Quaternion.Inverse(Reflect(r.rotation, axis)) * l.rotation;
        deltas[right[0]] = Delta(rightPalm, leftPalm);
        var byName = new Dictionary<string, Transform>();
        foreach (var bone in left) byName[bone.name] = bone;
        var fingers = new List<Finger>();
        for (int i = 1; i < right.Length; i++)
        {
            Transform r = right[i];
            if (!byName.TryGetValue(r.name.Replace("RDigit", "LDigit"), out Transform l)) continue;
            if (!Rest(r, out Matrix4x4 rb) || !Rest(l, out Matrix4x4 lb)) continue;
            deltas[r] = Delta(rb, lb);
        }
        for (int i = 1; i < right.Length; i++)
        {
            Transform r = right[i];
            if (!deltas.TryGetValue(r, out Quaternion delta) || r.parent == null || !deltas.TryGetValue(r.parent, out Quaternion parentDelta)) continue;
            fingers.Add(new Finger(byName[r.name.Replace("RDigit", "LDigit")], i, parentDelta, delta));
        }
        return new HandMirror(skin.transform, axis, Mathf.Sign(across[axis]), deltas[right[0]], fingers.ToArray());
    }

    // The body's lateral axis in world space, pointing to its right side.
    internal Vector3 RightAxis
    {
        get
        {
            Vector3 local = Vector3.zero;
            local[_axis] = _rightSide;
            return (_mesh.rotation * local).normalized;
        }
    }

    internal Quaternion MirrorPalm(Quaternion rightPalm)
        => _mesh.rotation * Reflect(Quaternion.Inverse(_mesh.rotation) * rightPalm, _axis) * _palmDelta;

    // The same, mirrored across the plane with this world normal instead of the mesh's own
    // mid-plane: the mesh frame is turned so its lateral axis lies on the normal, which makes the
    // frame reflection the plane reflection (the bone-local correction is unchanged). A palm
    // placed by a mirror across another plane must be turned by that same plane, or the hand sits
    // at the grip but rotated off it.
    internal Quaternion MirrorPalm(Quaternion rightPalm, Vector3 mirrorNormal)
    {
        Vector3 right = RightAxis;
        if (mirrorNormal.sqrMagnitude < 1e-8f) return MirrorPalm(rightPalm);
        mirrorNormal.Normalize();
        if (Vector3.Dot(mirrorNormal, right) < 0f) mirrorNormal = -mirrorNormal;
        Quaternion frame = Quaternion.FromToRotation(right, mirrorNormal) * _mesh.rotation;
        return frame * Reflect(Quaternion.Inverse(frame) * rightPalm, _axis) * _palmDelta;
    }

    // The right-hand finger rotation per hand-poser index comes from the grip pose when there is
    // one, otherwise from the right hand's live fingers. Runs every preview frame.
    internal void ApplyFingers(GripPose? grip, Transform[] rightHand)
    {
        foreach (var finger in _fingers)
        {
            if (finger.Left == null) continue;
            Quaternion right = grip != null ? grip[finger.RightIndex] : rightHand[finger.RightIndex].localRotation;
            finger.Left.localRotation = Quaternion.Inverse(finger.ParentDelta) * Reflect(right, _axis) * finger.Delta;
        }
    }

    // Conjugation by the reflection across one mesh axis.
    private static Quaternion Reflect(Quaternion q, int axis) => axis switch
    {
        0 => new Quaternion(q.x, -q.y, -q.z, q.w),
        1 => new Quaternion(-q.x, q.y, -q.z, q.w),
        _ => new Quaternion(-q.x, -q.y, q.z, q.w),
    };
}
