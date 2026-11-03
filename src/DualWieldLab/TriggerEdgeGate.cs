namespace DualWieldLab;

// Both native commands and optional diagnostics pass this gate. A missed edge
// is discarded, never queued to fire after a menu closes or cooldown expires.
internal sealed class TriggerEdgeGate
{
    private int _lastFrame = -1;
    private bool _awaitRelease = true;
    private bool _holding;
    private PistolTriggerMode _mode;
    internal bool Holding => _holding;

    internal void Observe(bool gameplayAllowed, bool held)
    {
        if (!gameplayAllowed || !held)
        { _holding = false; _awaitRelease = !gameplayAllowed; }
    }

    internal bool ClaimNative(int frame, bool gameplayAllowed)
        => ClaimNative(frame, gameplayAllowed, true, PistolTriggerMode.Single);

    internal bool ClaimNative(int frame, bool gameplayAllowed, bool physicalHeld, PistolTriggerMode mode)
    {
        if (_awaitRelease || !physicalHeld || !PistolMechanics.IsSupportedMode(mode)
            || !Claim(frame, gameplayAllowed, true)) return false;
        _awaitRelease = true;
        _holding = true;
        _mode = mode;
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
        return _holding && _mode == PistolTriggerMode.FullAutomatic && now >= deadline
            && Claim(frame, gameplayAllowed, true);
    }

    internal void CancelHold() { _holding = false; _awaitRelease = true; }

    internal bool Claim(int frame, bool gameplayAllowed, bool freshPress)
    {
        if (!gameplayAllowed || !freshPress || _lastFrame == frame) return false;
        _lastFrame = frame;
        return true;
    }

    internal void Reset() { _lastFrame = -1; _awaitRelease = true; _holding = false; _mode = PistolTriggerMode.Unsupported; }
}
