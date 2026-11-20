using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>00435390/00435610/00435E50: independent start-to-start timers and finite-voice room handoff.</summary>
public sealed class EnbStationAmbientRuntime(Func<int,int,int> random)
{
    public EnbStationAmbientRuntime(Random random):this((low,high)=>(int)random.NextInt64(low,(long)high+1)) {}
    private EnbStationAmbientRoom? _current;
    private long[] _next = [];
    public bool ChangedRoom { get; private set; }

    public IReadOnlyList<EnbStationAmbientEntry> Update(EnbStationAmbientRoom requested, long now, bool finiteVoicePlaying)
    {
        ChangedRoom = false;
        if (!ReferenceEquals(requested, _current))
        {
            // Stop scheduling the old room while a finite ambient voice finishes.
            if (finiteVoicePlaying) return [];
            _current = requested; ChangedRoom = true;
            _next = requested.Entries.Select(e => now + Delay(e)).ToArray();
            return requested.Entries.Where(e => e.OnEntry).ToArray();
        }
        var due = new List<EnbStationAmbientEntry>();
        for (int i = 0; i < _next.Length; i++)
        {
            var entry = requested.Entries[i];
            if (entry.OnEntry || now < _next[i]) continue;
            due.Add(entry);
            _next[i] = now + Delay(entry); // No catch-up burst after a stalled frame.
        }
        return due;
    }

    private long Delay(EnbStationAmbientEntry e)
        => random(e.MinDelayMilliseconds,e.MaxDelayMilliseconds);
}
