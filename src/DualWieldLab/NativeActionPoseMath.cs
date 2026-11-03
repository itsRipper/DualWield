using System;
using System.Numerics;

namespace DualWieldLab;

internal static class NativeActionPoseMath
{
    internal static Quaternion LocalDelta(Quaternion idleLocal, Quaternion animatedLocal,
        Quaternion sourceBindBody, Quaternion targetBindBody)
    {
        if (!NativeArmRetargetMath.IsValid(idleLocal) || !NativeArmRetargetMath.IsValid(animatedLocal)
            || !NativeArmRetargetMath.IsValid(sourceBindBody) || !NativeArmRetargetMath.IsValid(targetBindBody))
            throw new ArgumentException("Native action pose reference is invalid.");
        Quaternion bodyDelta = sourceBindBody * Quaternion.Inverse(idleLocal) * animatedLocal * Quaternion.Inverse(sourceBindBody);
        return Quaternion.Normalize(Quaternion.Inverse(targetBindBody)
            * NativeArmRetargetMath.ReflectBodyDelta(bodyDelta) * targetBindBody);
    }
}
