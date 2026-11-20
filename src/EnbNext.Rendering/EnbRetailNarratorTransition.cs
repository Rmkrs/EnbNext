namespace EnbNext.Rendering;

/// <summary>67A6C0/67A8D0 desired state and 679930 post-avatar update.</summary>
public sealed class EnbRetailNarratorTransition
{
    public bool Hidden { get; private set; } = true;
    public bool RequestedHidden { get; private set; }
    public int RevealUpdates { get; private set; } = 3;

    public bool Request(bool hidden)
    {
        if (Hidden != RequestedHidden || Hidden == hidden) return false;
        RequestedHidden = hidden;
        if (!hidden) RevealUpdates = 3;
        return true;
    }

    public void Update(bool actorIdle)
    {
        if (Hidden == RequestedHidden) return;
        if (RequestedHidden && actorIdle) Hidden = true;
        if (!RequestedHidden)
        {
            if (RevealUpdates == 0) Hidden = false;
            else RevealUpdates--;
        }
    }
}
