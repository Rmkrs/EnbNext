using System.Runtime.CompilerServices;
namespace EnbNext.Rendering;

// Construction-local sharing of immutable stage lists. The elements retain their
// original object identity; no texture acquisition, mapper clock or lease moves.
internal sealed class EnbW3dTextureBindingListPool
{
    private readonly Dictionary<Key,IReadOnlyList<EnbW3dRenderTextureBinding?>> lists=new();
    private readonly struct Key(int count,EnbW3dRenderTextureBinding? first,EnbW3dRenderTextureBinding? second) : IEquatable<Key>
    {
        private readonly int count=count;
        private readonly EnbW3dRenderTextureBinding? first=first,second=second;
        public bool Equals(Key other)=>count==other.count && ReferenceEquals(first,other.first) && ReferenceEquals(second,other.second);
        public override bool Equals(object? other)=>other is Key key && Equals(key);
        public override int GetHashCode()=>HashCode.Combine(count,first is null?0:RuntimeHelpers.GetHashCode(first),second is null?0:RuntimeHelpers.GetHashCode(second));
    }
    internal IReadOnlyList<EnbW3dRenderTextureBinding?> Get(int count,EnbW3dRenderTextureBinding? first=null,EnbW3dRenderTextureBinding? second=null)
    {
        if(count==0)return Array.Empty<EnbW3dRenderTextureBinding?>();
        if(count is <1 or >2)throw new ArgumentOutOfRangeException(nameof(count));
        var key=new Key(count,first,count==2?second:null);
        if(!lists.TryGetValue(key,out var result))
        {
            result=Array.AsReadOnly(count==1?new[]{first}:new[]{first,second});lists.Add(key,result);
        }
        return result;
    }
    internal IReadOnlyList<EnbW3dRenderTextureBinding?> Freeze(IReadOnlyList<EnbW3dRenderTextureBinding?> source)
        => source.Count<=2?Get(source.Count,source.Count>0?source[0]:null,source.Count>1?source[1]:null):Array.AsReadOnly(source.ToArray());
}
