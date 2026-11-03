namespace DualWieldLab;

internal sealed class PresentationFrameGate
{
    private int _presentedFrame = -1;
    internal void Mark(int frame)
    {
        if (frame >= 0 && _presentedFrame < 0) _presentedFrame = frame;
    }
    internal bool Ready(int frame) => _presentedFrame >= 0 && frame > _presentedFrame;
    internal void Reset() => _presentedFrame = -1;
}
