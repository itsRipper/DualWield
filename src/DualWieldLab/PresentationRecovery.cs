namespace DualWieldLab;

internal enum PresentationRecoveryDecision { Defer, Report, Reject }

// A narrowly classified unavailable native pose can settle on a later frame.
// This policy neither validates nor excuses controller or inventory failures.
internal sealed class PresentationRecovery
{
    internal const int MaximumUnavailableFrames = 120;
    private const int ReportIntervalFrames = 60;
    private int _lastUnavailableFrame = -1;
    internal int UnavailableFrames { get; private set; }

    internal PresentationRecoveryDecision ObserveUnavailable(int frame)
    {
        // VisualPass and ComplexLateUpdate may both observe the same native frame.
        if (frame == _lastUnavailableFrame)
            return UnavailableFrames >= MaximumUnavailableFrames
                ? PresentationRecoveryDecision.Reject : PresentationRecoveryDecision.Defer;
        _lastUnavailableFrame = frame;
        UnavailableFrames++;
        if (UnavailableFrames >= MaximumUnavailableFrames) return PresentationRecoveryDecision.Reject;
        return UnavailableFrames == 1 || UnavailableFrames % ReportIntervalFrames == 0
            ? PresentationRecoveryDecision.Report : PresentationRecoveryDecision.Defer;
    }

    internal void ObserveValid()
    {
        _lastUnavailableFrame = -1;
        UnavailableFrames = 0;
    }
}
