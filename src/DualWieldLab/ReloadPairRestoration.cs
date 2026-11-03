namespace DualWieldLab;

// Native inventory completion and a usable paired presentation are separate boundaries.
internal sealed class ReloadPairRestoration
{
    internal enum Phase { Idle, WaitingForNative, Binding, Recovering, VerifyingPair }
    private readonly PresentationFrameGate _nativeFrame = new PresentationFrameGate();
    internal Phase State { get; private set; }
    internal int Version { get; private set; }
    internal bool Active => State != Phase.Idle;
    internal bool Waiting => State == Phase.WaitingForNative;
    internal bool Recovering => State == Phase.Recovering;
    internal bool Verifying => State == Phase.VerifyingPair;

    internal int Begin()
    {
        Version++;
        State = Phase.WaitingForNative;
        _nativeFrame.Reset();
        return Version;
    }

    internal void ObserveNativeFrame(int frame, bool ready)
    {
        if (!Waiting && !Verifying) return;
        if (ready) _nativeFrame.Mark(frame);
        else _nativeFrame.Reset();
    }

    internal bool TryBind(int frame, bool ready)
    {
        if (!Waiting) return false;
        if (!ready) { _nativeFrame.Reset(); return false; }
        if (!_nativeFrame.Ready(frame)) return false;
        State = Phase.Binding;
        return true;
    }

    internal bool BeginRecovery(int version)
    {
        if (version != Version || State != Phase.Binding) return false;
        State = Phase.Recovering;
        return true;
    }

    internal void PresentationFinished()
    {
        if (!Recovering) return;
        State = Phase.VerifyingPair;
        _nativeFrame.Reset();
    }

    internal bool TryComplete(int frame, bool ready)
    {
        if (!Verifying) return false;
        if (!ready) { _nativeFrame.Reset(); return false; }
        if (!_nativeFrame.Ready(frame)) return false;
        State = Phase.Idle;
        _nativeFrame.Reset();
        return true;
    }

    internal void Cancel()
    {
        Version++;
        State = Phase.Idle;
        _nativeFrame.Reset();
    }
}
