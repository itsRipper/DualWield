using System;
using System.Numerics;

namespace DualWieldLab;

internal static class NativeArmRetargetMath
{
    internal static Quaternion ReflectBodyDelta(Quaternion delta)
        => new Quaternion(delta.X, -delta.Y, -delta.Z, delta.W);

    internal static Quaternion TransferBody(Quaternion sourcePoseBody, Quaternion sourceBindBody, Quaternion targetBindBody)
    {
        if (!IsValid(sourcePoseBody) || !IsValid(sourceBindBody) || !IsValid(targetBindBody))
            throw new ArgumentException("Native arm rotation/reference is invalid.");
        Quaternion delta = Quaternion.Normalize(sourcePoseBody * Quaternion.Inverse(sourceBindBody));
        return Quaternion.Normalize(ReflectBodyDelta(delta) * targetBindBody);
    }

    internal static bool SameRotation(Quaternion first, Quaternion second)
        => Math.Abs(Quaternion.Dot(Quaternion.Normalize(first), Quaternion.Normalize(second))) > .999999f;

    internal static Quaternion ApplyGripMotion(Quaternion sourceGrip, Quaternion targetGrip, Quaternion liveSource)
        => Quaternion.Normalize(targetGrip * Quaternion.Inverse(sourceGrip) * liveSource);

    internal static Quaternion TransferLook(Quaternion sourcePoseBody, Quaternion sourceBindBody,
        Quaternion targetBindBody, Quaternion sourceLookBody, Quaternion currentLookBody)
    {
        // Mouse-look is a shared rigid motion, not an action to mirror.
        Quaternion residual = Quaternion.Inverse(sourceLookBody) * sourcePoseBody;
        return Quaternion.Normalize(currentLookBody * TransferBody(residual, sourceBindBody, targetBindBody));
    }

    internal static Vector3 ReflectPalm(Vector3 rightPalmInLook)
        => new Vector3(-rightPalmInLook.X, rightPalmInLook.Y, rightPalmInLook.Z);

    internal static bool IsValid(Quaternion value)
        => Finite(value.X) && Finite(value.Y) && Finite(value.Z) && Finite(value.W)
            && Finite(value.LengthSquared()) && value.LengthSquared() > .000000000001f;

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
