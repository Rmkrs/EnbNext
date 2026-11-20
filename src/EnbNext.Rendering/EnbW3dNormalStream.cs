using System.Collections;
using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Authored immutable normals, implicit zero particle normals, or a
/// checked range owned by one rendering invocation. No frame range outlives
/// the final flush; detached callers retain ordinary immutable snapshots.</summary>
[System.Runtime.CompilerServices.CollectionBuilder(typeof(EnbW3dNormalStream),nameof(Create))]
public readonly struct EnbW3dNormalStream : IReadOnlyList<Vector3>, IEquatable<EnbW3dNormalStream>
{
    private readonly ImmutableArray<Vector3> _values;
    private readonly EnbW3dFrameStream<Vector3>? _borrowed;
    public int Length {get;}
    public int Count=>Length;
    internal bool HasFiniteFrameProof=>_borrowed?.HasFiniteVector3Proof==true;
    public bool IsKnownFinite=>_values.IsDefault && _borrowed is null;
    internal ReadOnlySpan<Vector3> ReadSpan=>_borrowed is {} frame?frame.ReadSpan:_values.AsSpan();
    private EnbW3dNormalStream(int count){ArgumentOutOfRangeException.ThrowIfNegative(count);Length=count;}
    private EnbW3dNormalStream(ImmutableArray<Vector3> values){_values=values;Length=values.Length;}
    private EnbW3dNormalStream(EnbW3dFrameStream<Vector3> frame){_borrowed=frame;Length=frame.Count;}
    public static EnbW3dNormalStream Zero(int count)=>new(count);
    public static EnbW3dNormalStream Create(ReadOnlySpan<Vector3> values)=>new(ImmutableArray.Create(values));
    public static EnbW3dNormalStream Snapshot(IReadOnlyList<Vector3> values)=>values switch
    {
        EnbW3dNormalStream stream=>stream,
        EnbW3dFrameStream<Vector3> frame=>new(frame),
        ImmutableArray<Vector3> immutable=>new(immutable),
        _=>new(values.ToImmutableArray())
    };
    public static implicit operator EnbW3dNormalStream(ImmutableArray<Vector3> values)=>new(values);
    public Vector3 this[int index]
    {
        get
        {
            if((uint)index>=(uint)Length)throw new ArgumentOutOfRangeException(nameof(index));
            return _borrowed is {} frame?frame[index]:_values.IsDefault?Vector3.Zero:_values[index];
        }
    }
    public bool Equals(EnbW3dNormalStream other)=>Length==other.Length && _values.Equals(other._values) && ReferenceEquals(_borrowed,other._borrowed);
    public static bool operator ==(EnbW3dNormalStream left,EnbW3dNormalStream right)=>left.Equals(right);
    public static bool operator !=(EnbW3dNormalStream left,EnbW3dNormalStream right)=>!left.Equals(right);
    public override bool Equals(object? other)=>other is EnbW3dNormalStream stream&&Equals(stream);
    public override int GetHashCode()=>HashCode.Combine(Length,_values,_borrowed);
    public IEnumerator<Vector3> GetEnumerator(){for(int i=0;i<Length;i++)yield return this[i];}
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
