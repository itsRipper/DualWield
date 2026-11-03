using System;

namespace DualWieldLab;

// Pure policy so ownership and ammunition invariants can be tested without Unity.
public static class PairPolicy
{
    public static bool IsDistinctPair(string primaryId, string secondaryId, string primaryTemplate, string secondaryTemplate)
        => !string.IsNullOrEmpty(primaryId) && !string.IsNullOrEmpty(secondaryId)
        && !string.Equals(primaryId, secondaryId, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(primaryTemplate) && !string.IsNullOrEmpty(secondaryTemplate);

    public static bool IsValidShotDelta(int primaryBefore, int primaryAfter, int offhandBefore, int offhandAfter)
        => primaryBefore >= 0 && offhandBefore > 0 && primaryAfter == primaryBefore
        && offhandAfter == offhandBefore - 1;

    // The shared mechanism policy mirrors the native semiautomatic getter.
    public static double ShotInterval(int singleFireRate) => PistolMechanics.ShotInterval(singleFireRate);
    public static double ShotInterval(double effectiveRate) => PistolMechanics.EffectiveInterval(effectiveRate);

    public static bool IsCooldownReady(double gameTime, double nextTime) => gameTime >= nextTime;

    public static double NextShotTime(double emittedGameTime, int singleFireRate)
        => emittedGameTime + ShotInterval(singleFireRate);
    public static double NextShotTime(double emittedGameTime, double effectiveRate)
        => emittedGameTime + ShotInterval(effectiveRate);
}
