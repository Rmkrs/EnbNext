using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
namespace EnbNext.Rendering;

// A UV source is either immutable asset data or an invocation-owned mutable
// range. The latter survives queued draws until that invocation is drained.
public readonly struct EnbW3dUvStream : IReadOnlyList<Vector2>, IEquatable<EnbW3dUvStream>
{
    private readonly ImmutableArray<Vector2> immutable;
    private readonly EnbW3dUvOwner? owner;
    private readonly int length;
    private EnbW3dUvStream(ImmutableArray<Vector2> values){immutable=values;length=values.IsDefault?0:values.Length;}
    internal EnbW3dUvStream(EnbW3dUvOwner value){owner=value;length=value.Count;}
    public int Length => length;
    internal bool IsMutable => owner is not null;
    internal ReadOnlySpan<Vector2> AsSpan() => owner is {} current?current.ReadSpan(length):immutable.AsSpan();
    public bool Equals(EnbW3dUvStream other)=>ReferenceEquals(owner,other.owner)&&length==other.length&&immutable.Equals(other.immutable);
    public override bool Equals(object? value)=>value is EnbW3dUvStream other&&Equals(other);
    public override int GetHashCode()=>HashCode.Combine(owner,length,immutable);
    public int Count => Length;
    public Vector2 this[int index] => (uint)index<(uint)length?AsSpan()[index]:throw new ArgumentOutOfRangeException(nameof(index));
    internal bool SameImmutableSource(EnbW3dUvStream other) => owner is null && other.owner is null && immutable.Equals(other.immutable);
    public static implicit operator EnbW3dUvStream(ImmutableArray<Vector2> values)=>new(values);
    public IEnumerator<Vector2> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}

internal sealed class EnbW3dUvOwner : IReadOnlyList<Vector2>
{
    private Vector2[] storage=[];
    private int fixedCorners;
    private int initialized;
    internal ReadOnlySpan<Vector2> ReadSpan(int count)=>storage.AsSpan(0,count);
    public int Count {get;private set;}
    internal Span<Vector2> Prepare(int count,int corners,ReadOnlySpan<Vector2> fixedPattern)
    {
        if(storage.Length<count)Array.Resize(ref storage,Math.Max(count,storage.Length*2));
        Count=count;
        if(fixedPattern.IsEmpty){fixedCorners=0;initialized=0;}
        else
        {
            if(fixedCorners!=corners){fixedCorners=corners;initialized=0;}
            for(int i=initialized;i<count;i++)storage[i]=fixedPattern[i%corners];
            initialized=Math.Max(initialized,count);
        }
        return storage.AsSpan(0,count);
    }
    public Vector2 this[int index] => (uint)index<(uint)Count?storage[index]:throw new ArgumentOutOfRangeException(nameof(index));
    public IEnumerator<Vector2> GetEnumerator(){for(int i=0;i<Count;i++)yield return storage[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
