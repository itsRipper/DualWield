namespace DualWieldLab;

// When EFT moves the magazine's top round into the chamber (after a shot, a magazine reload or a feed
// repair): only into an empty chamber, from a magazine with rounds the chamber accepts. No magazine or
// an incompatible one never feeds.
internal static class NativeFeed
{
    internal static bool Feeds(bool chamberEmpty, int magazineCount, bool ammoCompatible)
        => chamberEmpty && magazineCount > 0 && ammoCompatible;
}
