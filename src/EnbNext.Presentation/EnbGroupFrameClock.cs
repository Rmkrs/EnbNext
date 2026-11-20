namespace EnbNext.Presentation;

/// <summary>6B4860 active-owner dispatch: only active frames reach group timer consumers.</summary>
public sealed class EnbGroupFrameClock
{
    private uint? _previous;
    public uint Time {get;private set;}
    public void Advance(uint frameTime,bool active)
    {
        if(_previous is uint previous && active)Time=unchecked(Time+frameTime-previous);
        _previous=frameTime;
    }
}
