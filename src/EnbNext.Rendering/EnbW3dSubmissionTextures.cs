using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
namespace EnbNext.Rendering;

// Captures texture identity for a queued run without copying an array for the
// retail zero/two-stage path. Replacing the source list cannot change this value.
public readonly struct EnbW3dSubmissionTextures : IReadOnlyList<EnbW3dRenderTextureBinding?>, IEquatable<EnbW3dSubmissionTextures>
{
    private readonly EnbW3dRenderTextureBinding? first,second;
    private readonly ImmutableArray<EnbW3dRenderTextureBinding?> extra;
    public int Count {get;}
    public int Length=>Count;
    public EnbW3dSubmissionTextures(IReadOnlyList<EnbW3dRenderTextureBinding?> source)
    {
        Count=source.Count;first=Count>0?source[0]:null;second=Count>1?source[1]:null;
        extra=Count>2?ImmutableArray.CreateRange(source):default;
    }
    public EnbW3dRenderTextureBinding? this[int i] => (uint)i>=(uint)Count
        ?throw new ArgumentOutOfRangeException(nameof(i)):Count>2?extra[i]:i==0?first:second;
    public bool Equals(EnbW3dSubmissionTextures other)=>Count==other.Count &&
        ReferenceEquals(first,other.first)&&ReferenceEquals(second,other.second)&&extra.Equals(other.extra);
    public override bool Equals(object? value)=>value is EnbW3dSubmissionTextures other&&Equals(other);
    public override int GetHashCode()=>HashCode.Combine(Count,first is null?0:RuntimeHelpers.GetHashCode(first),second is null?0:RuntimeHelpers.GetHashCode(second),extra);
    public static implicit operator EnbW3dSubmissionTextures(ImmutableArray<EnbW3dRenderTextureBinding?> values)=>new(values);
    public Enumerator GetEnumerator()=>new(this);
    IEnumerator<EnbW3dRenderTextureBinding?> IEnumerable<EnbW3dRenderTextureBinding?>.GetEnumerator()=>new Enumerator(this);
    IEnumerator IEnumerable.GetEnumerator()=>new Enumerator(this);
    public struct Enumerator(EnbW3dSubmissionTextures values):IEnumerator<EnbW3dRenderTextureBinding?>
    {
        private int index=-1;
        public EnbW3dRenderTextureBinding? Current=>values[index];
        object? IEnumerator.Current=>Current;
        public bool MoveNext()=>++index<values.Count;
        public void Reset()=>index=-1;
        public void Dispose(){}
    }
}
