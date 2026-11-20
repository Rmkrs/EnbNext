using System;
using System.Collections.Generic;

namespace EnbNext.Rendering;

/// <summary>Explicit membership of retained appearance slots. Only a slot
/// replacement/removal queues retirement; unchanged frames do not scan models.
/// The owner drains after all appearance consumers have established their slots.</summary>
public sealed class EnbMeshAppearanceMembership
{
    private sealed class Entry(EnbCurrentRetailMeshAppearance appearance, long ordinal)
    {
        internal readonly EnbCurrentRetailMeshAppearance Appearance = appearance;
        internal readonly long Ordinal = ordinal;
        internal int References;
        internal bool Queued;
    }
    private readonly Dictionary<EnbCurrentRetailMeshAppearance,Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _pending = [];
    private long _nextOrdinal;
    public bool HasPendingRetirements => _pending.Count != 0;

    public void Retain(EnbCurrentRetailMeshAppearance appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!_entries.TryGetValue(appearance,out var entry))
            _entries.Add(appearance,entry=new(appearance,_nextOrdinal++));
        entry.References=checked(entry.References+1);
    }
    public void Release(EnbCurrentRetailMeshAppearance appearance)
    {
        if (!_entries.TryGetValue(appearance,out var entry) || entry.References==0)
            throw new InvalidOperationException("Appearance slot released without membership.");
        if (--entry.References!=0 || appearance.PrototypeOwned || entry.Queued) return;
        entry.Queued=true;
        _pending.Add(entry);
    }
    public void Drain(Action<EnbCurrentRetailMeshAppearance> retire)
    {
        ArgumentNullException.ThrowIfNull(retire);
        // Preserve the former creation-order release semantics. Sorting only
        // touched retirements avoids traversing every live model each frame.
        _pending.Sort(static (a,b)=>a.Ordinal.CompareTo(b.Ordinal));
        int completed=0;
        try
        {
            for (;completed<_pending.Count;completed++)
            {
                var entry=_pending[completed];
                if (entry.References==0)
                {
                    retire(entry.Appearance);
                    _entries.Remove(entry.Appearance);
                }
                entry.Queued=false;
            }
        }
        finally
        {
            // Do not keep retired generations alive through reusable scratch.
            // A failing callback leaves its entry queued for the next drain.
            if (completed!=0) _pending.RemoveRange(0,completed);
        }
    }
    public void Clear()
    {
        _entries.Clear();_pending.Clear();_nextOrdinal=0;
    }
}
