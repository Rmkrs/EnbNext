namespace EnbNext.Rendering;

// Native 0090B470 initialization, visibility reconciliation 0090B0E0,
// and actual-draw update 0090D6E0. Detail-zero draws must not call Advance.
public sealed class EnbRetailSpherePlayback
{
    public bool Enabled { get; private set; }=true;
    public float Progress { get; private set; }

    public void ReconcileVisibility(bool visible)
    {
        if(Enabled!=visible){Enabled=visible;Progress=0;}
    }

    public void Advance(float duration,uint flags,bool hasPropertyKeys,uint frameDeltaMilliseconds)
    {
        if(!Enabled||!hasPropertyKeys)return;
        if(!float.IsFinite(duration))throw new InvalidDataException("Nonfinite sphere duration");
        float next=duration<=0?1:(float)(Progress+frameDeltaMilliseconds*(double).001f/duration);
        // Strict comparison and a single subtraction, not modulo or catch-up.
        if((flags&8)!=0&&next>1)next-=1;
        if(!float.IsFinite(next))throw new InvalidDataException("Sphere progress overflow");
        Progress=next;
    }
}
