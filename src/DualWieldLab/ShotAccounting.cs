namespace DualWieldLab;

// FirearmController.InitiateShot registers one Shot per projectile of the round; the left shot
// expects exactly that many registrations.
internal static class ShotAccounting
{
    // 0: not a live projectile round (no projectile or no muzzle velocity); refused before mutation.
    internal static int ExpectedProjectiles(int projectileCount, float initialSpeed)
        => projectileCount >= 1 && initialSpeed > 0f ? projectileCount : 0;

    internal static bool Complete(int expected, int registered) => expected > 0 && registered == expected;
}
