using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Retains a snapshot of the 00489830 mutation stream. Callers keep
/// separate load-prefix and current states; evaluating a new time never rebuilds
/// the database. Returned databases are read-only to consumers.</summary>
public sealed class EnbRetainedSceneSplineState
{
    private EnbRetailScalarSplineDatabase? _source, _resolved;
    private ImmutableArray<EnbSceneSplineUpdateSnapshot> _updates;
    private int _count;

    public void Clear()
    {
        _source=_resolved=null;
        _updates=default;
        _count=0;
    }

    public bool TryResolve(EnbRetailScalarSplineDatabase source,
        ImmutableArray<EnbSceneSplineUpdateSnapshot> updates, int count,
        out EnbRetailScalarSplineDatabase resolved, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(source);
        if(count<0 || count>updates.Length)throw new ArgumentOutOfRangeException(nameof(count));
        resolved=default!;
        diagnostic=null;
        bool samePrefix=ReferenceEquals(source,_source) && _resolved is not null && count>=_count;
        if(samePrefix && !_updates.Equals(updates))
            for(int i=0;i<_count;i++)
                if(!_updates[i].Data.AsSpan().SequenceEqual(updates[i].Data.AsSpan()))
                {samePrefix=false;break;}
        if(samePrefix && count==_count)
        {
            resolved=_resolved!;
            return true;
        }
        // Publish only a complete successful revision. A malformed update may
        // partially mutate its candidate, but cannot corrupt a queued revision.
        var candidate=(samePrefix?_resolved!:source).Clone();
        try
        {
            for(int i=samePrefix?_count:0;i<count;i++)
                candidate.ApplyRuntimeUpdate(updates[i].Data.AsSpan());
        }
        catch(InvalidDataException error)
        {
            diagnostic=error.Message;
            return false;
        }
        _source=source;
        _updates=updates;
        _count=count;
        resolved=_resolved=candidate;
        return true;
    }
}
