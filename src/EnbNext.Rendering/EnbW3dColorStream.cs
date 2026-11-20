using System.Collections;
using System.Collections.Immutable;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Immutable COLO source. Expand packed bytes only while writing the
/// upload stream; white has no backing array. Interpolated geometry can retain
/// full precision colors without repacking them.</summary>
public readonly struct EnbW3dColorStream : IReadOnlyList<Vector4>
{
    private readonly ImmutableArray<EnbRenderColor32> _packed;
    private readonly ImmutableArray<Vector4> _expanded;
    private readonly EnbW3dFrameStream<EnbRenderColor32>? _borrowed;
    internal ReadOnlySpan<EnbRenderColor32> PackedSpan => _borrowed is {} borrowed?borrowed.ReadSpan:_packed.AsSpan();
    internal bool SameImmutableSource(EnbW3dColorStream other) => _borrowed is null && other._borrowed is null &&
        Length==other.Length && _packed.Equals(other._packed) && _expanded.Equals(other._expanded);
    public int Length { get; }
    public int Count => Length;
    public bool IsKnownFinite => _expanded.IsDefault;
    private EnbW3dColorStream(int count) { Length=count; }
    private EnbW3dColorStream(ImmutableArray<EnbRenderColor32> packed) { _packed=packed;Length=packed.Length; }
    private EnbW3dColorStream(ImmutableArray<Vector4> expanded) { _expanded=expanded;Length=expanded.Length; }
    private EnbW3dColorStream(EnbW3dFrameStream<EnbRenderColor32> borrowed){_borrowed=borrowed;Length=borrowed.Count;}
    public static EnbW3dColorStream White(int count) => new(count);
    public static EnbW3dColorStream Snapshot(IReadOnlyList<EnbRenderColor32> packed)
        => packed is EnbW3dFrameStream<EnbRenderColor32> borrowed?new(borrowed):new(packed is ImmutableArray<EnbRenderColor32> immutable ? immutable : packed.ToImmutableArray());
    public static implicit operator EnbW3dColorStream(ImmutableArray<Vector4> values) => new(values);
    public Vector4 this[int index]
    {
        get
        {
            if((uint)index>=(uint)Length)throw new ArgumentOutOfRangeException(nameof(index));
            if(!_expanded.IsDefault)return _expanded[index];
            if(_packed.IsDefault && _borrowed is null)return Vector4.One;
            var color=_borrowed is null?_packed[index]:_borrowed[index];const float scale=1f/255f;
            return new(color.R*scale,color.G*scale,color.B*scale,color.A*scale);
        }
    }
    public IEnumerator<Vector4> GetEnumerator()
    { for(int i=0;i<Length;i++)yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator()=>GetEnumerator();
}
