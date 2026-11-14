namespace DualWieldLab;

// A native completion callback is necessary but cannot establish item state.
internal static class ReloadOutcomeAudit
{
    internal static bool Verified(bool callbackSucceeded, bool owned, bool replacementInstalled,
        bool oldMagazineStored, int ammunitionBefore, int ammunitionAfter, int otherBefore, int otherAfter)
        => callbackSucceeded && owned && replacementInstalled && oldMagazineStored
            && ammunitionBefore >= 0 && ammunitionBefore == ammunitionAfter
            && otherBefore >= 0 && otherBefore == otherAfter;

    // ReloadExternalMagResult.Run feeds an empty chamber only from a compatible magazine.
    internal static bool NativeCountsMatch(int chamberBefore, int oldBefore, int spareBefore,
        int chamberAfter, int oldAfter, int spareAfter, bool spareCompatible)
    {
        if (chamberBefore < 0 || chamberBefore > 1 || oldBefore < 0 || spareBefore <= 0) return false;
        int fed = NativeFeed.Feeds(chamberBefore == 0, spareBefore, spareCompatible) ? 1 : 0;
        return chamberAfter == chamberBefore + fed && oldAfter == oldBefore && spareAfter == spareBefore - fed;
    }
}
