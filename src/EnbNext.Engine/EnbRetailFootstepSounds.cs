namespace EnbNext.Engine;

public static class EnbRetailFootstepSounds
{
    /// <summary>00442650 keeps one name per footsteps.ini section (32 slots, an
    /// empty string stored as NULL); 00442930 (walking) / 004428E0 (running) map a
    /// surface outside 0..31 or with no name to surface 0, and play nothing only
    /// when that entry is empty too.</summary>
    public static string? Name(IReadOnlyList<IReadOnlyDictionary<string,string>> surfaces,uint surface,bool running)
    {
        string? Surface(uint index)=>index<surfaces.Count &&
            surfaces[(int)index].GetValueOrDefault(running?"SoundRunning":"SoundWalking") is {Length:>0} value?value:null;
        return Surface(surface<32?surface:0)??Surface(0);
    }
}
