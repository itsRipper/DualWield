using EFT;
using UnityEngine;

namespace DualWieldLab;

// R reload: after the left pistol is holstered and the pair released, the native
// reload owns the left arm. This eases it from the holstered pose into the native
// one along the authored "release" curve instead of snapping on the first frame.
internal sealed class LeftArmHandoff
{
    private readonly NativeArmRetarget _arms;
    private readonly Quaternion[] _from;
    private readonly ReloadClip _clip;
    private readonly float _end;
    private float _frame;
    private int _advancedFrame = -1;
    internal bool Finished { get; private set; }

    private LeftArmHandoff(NativeArmRetarget arms, Quaternion[] from, ReloadClip clip)
    {
        _arms = arms; _from = from; _clip = clip;
        _end = clip.Marker("release_end");
    }

    internal static LeftArmHandoff? TryCreate(Player player, Quaternion[] holstered, ReloadClip? clip)
    {
        if (clip == null || holstered == null) return null;
        try
        {
            var arms = NativeArmRetarget.Create(player);
            if (arms.ChainLength != holstered.Length) { arms.Restore(); return null; }
            return new LeftArmHandoff(arms, (Quaternion[])holstered.Clone(), clip);
        }
        catch (System.InvalidOperationException) { return null; }
    }

    internal void BeforeNativePose() => _arms.RestoreBeforeNativePose();

    internal void Apply()
    {
        if (Finished) return;
        if (_advancedFrame != Time.frameCount)
        {
            _advancedFrame = Time.frameCount;
            _frame += Time.deltaTime * _clip.Fps;
        }
        float weight = _clip.Scalar("release", Mathf.Min(_frame, _end));
        if (_frame >= _end || weight >= .999f) { Restore(); return; }
        _arms.BlendLeftFrom(_from, weight);
    }

    internal void Restore()
    {
        if (Finished) return;
        Finished = true;
        _arms.Restore();
    }
}
