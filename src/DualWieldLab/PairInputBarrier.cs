namespace DualWieldLab;

// A shortcut may be sampled in Update before EFT delivers its buffered native
// commands in FixedUpdate. Consume that delivery even across a frame boundary.
internal sealed class PairInputBarrier
{
    private int _frame = -1;
    internal bool Pending { get; private set; }
    internal bool Begin(int frame)
    {
        if (_frame == frame || Pending) return false;
        _frame = frame;
        Pending = true;
        return true;
    }
    internal bool Blocks(int frame) => Pending || _frame == frame;
    internal void Delivered() => Pending = false;
    internal void Reset() { Pending = false; _frame = -1; }
}
