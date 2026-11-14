namespace DualWieldLab;

// What the player gets when a reload is refused before anything starts.
internal readonly struct ReloadFeedback
{
    internal const string RightNoSpare = "No loaded spare magazine for the right pistol in your rig or pockets.";
    internal const string LeftNoSpare = "No loaded spare magazine for the left pistol in your rig or pockets.";
    internal const string LeftUnavailable = "The left pistol cannot be reloaded right now.";

    internal readonly string Message;
    // EFT's "need ammo" voice line (FirearmController.ReloadMagNotFound).
    internal readonly bool NeedAmmoPhrase;

    private ReloadFeedback(string message, bool needAmmoPhrase) { Message = message; NeedAmmoPhrase = needAmmoPhrase; }

    internal static ReloadFeedback Refused(string refusal)
        => new ReloadFeedback(refusal, IsNoSpare(refusal));

    // Mechanism details go to the log only; the player sees the generic text.
    internal static ReloadFeedback LeftRefused(string refusal)
        => IsNoSpare(refusal) ? Refused(refusal) : new ReloadFeedback(LeftUnavailable, false);

    private static bool IsNoSpare(string refusal) => refusal is RightNoSpare or LeftNoSpare;
}
