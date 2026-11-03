using EFT;
using UnityEngine;

namespace DualWieldLab;

// Coordinated draw/holster. The pair follows EFT's own controller lifecycle: it is built at the
// start of the native pistol draw (the left arm mirrors the right arm's draw, so both pistols
// come up together) and its input binds once the native controller reaches idle. A switch away
// keeps it through the native hide animation (DropCurrentController) and releases it when EFT
// destroys the controller.
internal sealed class DualTransition
{
    private const float HandoverSeconds = .2f;
    private const float DrawTimeoutSeconds = 10f, HolsterTimeoutSeconds = 5f;
    private readonly PresentationFrameGate _drawReady = new PresentationFrameGate();
    private float _drawStartedAt, _holsterStartedAt;
    // NaN: the draw is ready but the right hands have not settled in native idle yet.
    private float _handoverStartedAt = float.NegativeInfinity;

    internal bool DrawPending { get; private set; }
    internal bool Holstering { get; private set; }
    internal bool Active => DrawPending || Holstering;
    // The player's input owner while the draw has not bound native input yet.
    internal PlayerOwner? DrawOwner { get; private set; }
    internal bool DrawTimedOut => DrawPending && Time.unscaledTime - _drawStartedAt > DrawTimeoutSeconds;
    internal bool HolsterTimedOut => Holstering && Time.unscaledTime - _holsterStartedAt > HolsterTimeoutSeconds;
    internal float DrawSeconds => Time.unscaledTime - _drawStartedAt;

    internal void BeginPair(bool drawing, PlayerOwner? drawOwner)
    {
        DrawPending = drawing;
        DrawOwner = drawOwner;
        _drawStartedAt = Time.unscaledTime;
        _drawReady.Reset();
    }

    // Native readiness must hold for consecutive frames before the draw counts as finished.
    internal bool ObserveDrawReady(bool ready)
    {
        if (!ready) { _drawReady.Reset(); return false; }
        _drawReady.Mark(Time.frameCount);
        return _drawReady.Ready(Time.frameCount);
    }

    internal void CompleteDraw()
    {
        DrawPending = false;
        DrawOwner = null;
        _handoverStartedAt = float.NaN;
    }

    internal void BeginHolster()
    {
        Holstering = true;
        _holsterStartedAt = Time.unscaledTime;
    }

    internal void Reset()
    {
        DrawPending = Holstering = false;
        DrawOwner = null;
        _handoverStartedAt = float.NegativeInfinity;
    }

    // 1 during the native draw/holster and until the right hands settle in native idle (the
    // operation can be Idling while the draw clip still blends out); then it eases to the
    // palm-driven ready pose, which matters only for a pair drawn before its offset was stored.
    internal float MirrorPlacementWeight(OffhandVisualRig? rig)
    {
        if (Active) return 1f;
        if (float.IsNaN(_handoverStartedAt))
        {
            if (rig?.RightHandsIdle() != true) return 1f;
            _handoverStartedAt = Time.unscaledTime;
        }
        float t = (Time.unscaledTime - _handoverStartedAt) / HandoverSeconds;
        return t >= 1f ? 0f : 1f - Mathf.SmoothStep(0f, 1f, t);
    }
}
