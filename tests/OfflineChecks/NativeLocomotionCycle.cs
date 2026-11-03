using System;

namespace DualWieldLab;

// Measures the current native cycle; never advances a second locomotion clock.
internal sealed class NativeLocomotionCycle
{
    private int _kind;
    private double _cycle, _phase;
    private float _minimum, _maximum, _centre, _targetCentre, _offset, _blend, _entryOffset;
    private bool _measured;

    internal float Offset => _offset;
    internal float Centre => _centre;

    internal float Observe(int kind, double nativePhase, float depth, float deltaTime, bool nativeTransition)
    {
        if (!Finite(depth) || double.IsNaN(nativePhase) || double.IsInfinity(nativePhase))
        { Reset(); return 0f; }
        float response = Finite(deltaTime) && deltaTime > 0f
            ? (float)(1d - Math.Exp(-12d * Math.Min(deltaTime, .25f))) : 0f;
        if (kind == 0 || nativeTransition)
        {
            _kind = 0; _measured = false;
            _blend = 0f;
            _offset += (0f - _offset) * response;
            return _offset;
        }
        double cycle = Math.Floor(nativePhase);
        if (!_measured || kind != _kind || nativePhase < _phase)
        {
            _entryOffset = _offset;
            _blend = 0f;
            _kind = kind; _cycle = cycle; _minimum = _maximum = _centre = _targetCentre = depth;
            _measured = true;
        }
        else if (cycle != _cycle)
        {
            _targetCentre = (_minimum + _maximum) * .5f;
            _minimum = _maximum = depth; _cycle = cycle;
        }
        else
        { _minimum = Math.Min(_minimum, depth); _maximum = Math.Max(_maximum, depth); }
        _phase = nativePhase;
        // Right deviation is +(depth-centre), left deviation is its opposite.
        _centre += (_targetCentre - _centre) * response;
        _blend += (1f - _blend) * response;
        _offset = _entryOffset * (1f - _blend) + 2f * (_centre - depth) * _blend;
        return _offset;
    }

    internal void Reset()
    { _kind = 0; _measured = false; _cycle = _phase = 0; _minimum = _maximum = _centre = _targetCentre = _offset = _blend = _entryOffset = 0f; }

    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}
