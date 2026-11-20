namespace EnbNext.Rendering;

// Per-instance state for native 00912620/00912680. Each admitted Render call consumes the global frame delta, including another view of the same object.
public sealed class EnbRetailRingPlayback
{
    public bool Enabled { get; private set; }
    public float Progress { get; private set; }

    public void Reset() { Enabled=false;Progress=0; }

    public void Advance(EnbRetailRingAsset asset,bool renderAdmitted,uint frameDeltaMilliseconds)
    {
        ReconcileVisibility(renderAdmitted);
        if(!Enabled || !asset.Tracks.Values.Any(track=>track.Count>0))return;
        float duration=asset.Header.Duration;
        // Native x87 uses float 0.001, divides by duration, adds stored progress,
        // then stores float before the strict >1 loop comparison.
        float next=duration<=0?1:(float)(Progress+frameDeltaMilliseconds*(double).001f/duration);
        if((asset.Header.Flags&2)!=0 && next>1)next-=1;
        if(!float.IsFinite(next))throw new InvalidDataException("Ring progress overflow");
        Progress=next;
    }

    public void ReconcileVisibility(bool visible)
    {
        if(Enabled!=visible){Enabled=visible;Progress=0;}
    }
}
