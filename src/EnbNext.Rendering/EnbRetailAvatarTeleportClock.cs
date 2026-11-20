using R = EnbNext.Rendering.EnbRetailLineArithmetic.Register;

namespace EnbNext.Rendering;

/// <summary>788F90 state 4 and 7897D0's frame-delta countdown.</summary>
public sealed class EnbRetailAvatarTeleportClock
{
    public float RemainingSeconds { get; private set; }
    public bool Active { get; private set; }
    public bool Hidden { get; private set; }
    private float visibilityThreshold;
    private bool hideAtThreshold,visibilityChanged;
    public void StartCreation()
    {
        RemainingSeconds=.5f;Active=true;Hidden=true;
        visibilityThreshold=0;hideAtThreshold=false;visibilityChanged=false;
    }
    // 788F90 states2/3: one-second owner, visibility flips at remaining .5.
    public void StartStation(bool departing)
    {
        RemainingSeconds=1;Active=true;if(!departing)Hidden=true;
        visibilityThreshold=.5f;hideAtThreshold=departing;visibilityChanged=false;
    }
    public void Clear()
    {
        RemainingSeconds=0;Active=false;Hidden=false;
    }
    //788EF0 releases the effect/countdown without changing body/head visibility.
    public void Stop(){RemainingSeconds=0;Active=false;}
    public void Tick(uint milliseconds)
    {
        if(!Active)return;
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        RemainingSeconds=(float)((R)RemainingSeconds-(R)milliseconds*(R).001f);
        if(!visibilityChanged && RemainingSeconds<=visibilityThreshold)
        {Hidden=hideAtThreshold;visibilityChanged=true;}
        if(RemainingSeconds<=0)
        {
            Active=false;RemainingSeconds=0;
        }
    }
}
