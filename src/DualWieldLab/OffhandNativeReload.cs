namespace DualWieldLab;

// Records the left pistol's native reload events as they are dispatched. The plugin reacts
// in Update: the swap is submitted at OnMagOut (an insert as soon as the clip runs), the clip holds at
// OnMagShow until it settled, and the chamber feed is released at OnAddAmmoInChamber.
// OnMagHide/OnMagShow only toggle the old magazine's renderers.
internal sealed class OffhandNativeReload : OffhandShotContext.IReloadEvents
{
    private readonly OffhandNativeAction _action;

    internal bool MagOutSeen { get; private set; }
    internal bool MagShown { get; private set; }
    internal bool MagInSeen { get; private set; }
    internal bool ChamberEvent { get; private set; }
    internal bool Ended { get; private set; }
    // An empty-well clip has no OnMagOut, so any magazine event proves the clip started.
    internal bool MagazineEventSeen => MagOutSeen || MagShown || MagInSeen;

    internal OffhandNativeReload(OffhandNativeAction action) { _action = action; }

    public void MagOut() => MagOutSeen = true;

    public void MagHide() => _action.SetMagazineVisible(false);

    public void MagShow()
    {
        MagShown = true;
        _action.SetMagazineVisible(true);
    }

    public void MagIn() => MagInSeen = true;

    public void AddAmmoInChamber() => ChamberEvent = true;

    public bool IdleStart()
    {
        if (!MagShown && !MagInSeen) return false;
        Ended = true;
        return true;
    }
}
