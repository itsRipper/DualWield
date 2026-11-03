namespace DualWieldLab;

// Unity-free lifetime token for native callbacks. Cancellation cannot undo an
// already dispatched native transaction, but it must revoke all later actions.
internal sealed class ReloadCallbackGuard
{
    private int _version;
    private bool _active;
    internal int Version => _version;
    internal int Begin() { _active = true; return ++_version; }
    internal bool IsCurrent(int version) => _active && version == _version;
    internal void Invalidate() { _active = false; _version++; }
}
