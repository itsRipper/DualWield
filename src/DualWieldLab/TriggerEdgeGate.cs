namespace DualWieldLab;

// Both native commands and optional diagnostics pass this gate. A missed edge
// is discarded, never queued to fire after a menu closes or cooldown expires.
internal sealed class TriggerEdgeGate
{
    private int _lastFrame = -1;
    private bool _awaitRelease = true;
    private bool _holding;
    private int _burstRemaining;
    private PistolTriggerMode _mode;
    internal bool Holding => _holding;

    // Held, or a burst still completing after release (AutomaticFireOperation keeps its trigger pressed).
    internal bool Repeating => _holding && (_mode == PistolTriggerMode.FullAutomatic || _burstRemaining > 0);

    internal void Observe(bool gameplayAllowed, bool held)
    {
        if (!gameplayAllowed) { _holding = false; _burstRemaining = 0; _awaitRelease = true; return; }
        // A burst in progress completes after release.
        if (!held && _burstRemaining == 0) { _holding = false; _awaitRelease = false; }
        else if (!held) _awaitRelease = false;
    }

    internal bool ClaimNative(int frame, bool gameplayAllowed)
        => ClaimNative(frame, gameplayAllowed, true, PistolTriggerMode.Single);

    // burstCount: the pistol's native BurstShotsCount; used only for Burst.
    internal bool ClaimNative(int frame, bool gameplayAllowed, bool physicalHeld, PistolTriggerMode mode, int burstCount = 1)
    {
        if (_awaitRelease || _burstRemaining > 0 || !physicalHeld || !PistolMechanics.IsSupportedMode(mode)
            || !Claim(frame, gameplayAllowed, true)) return false;
        _awaitRelease = true;
        _holding = true;
        _mode = mode;
        _burstRemaining = mode == PistolTriggerMode.Burst ? PistolMechanics.BurstFollowUps(burstCount) : 0;
        return true;
    }

    internal void ObserveMode(PistolTriggerMode mode)
    {
        if (_holding && mode != _mode) CancelHold();
    }

    internal bool ClaimRepeat(int frame, bool gameplayAllowed, bool physicalHeld, PistolTriggerMode mode, double now, double deadline)
    {
        Observe(gameplayAllowed, physicalHeld);
        ObserveMode(mode);
        if (!Repeating || now < deadline || !Claim(frame, gameplayAllowed, true)) return false;
        if (_burstRemaining > 0 && --_burstRemaining == 0 && !physicalHeld) _holding = false;
        return true;
    }

    // Empty magazine, malfunction, refusal or unpair: a running burst stops with the hold.
    internal void CancelHold() { _holding = false; _burstRemaining = 0; _awaitRelease = true; }

    internal bool Claim(int frame, bool gameplayAllowed, bool freshPress)
    {
        if (!gameplayAllowed || !freshPress || _lastFrame == frame) return false;
        _lastFrame = frame;
        return true;
    }

    internal void Reset() { _lastFrame = -1; _awaitRelease = true; _holding = false; _burstRemaining = 0; _mode = PistolTriggerMode.Unsupported; }
}
