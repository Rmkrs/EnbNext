using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EnbNext.Rendering;

// A triangle is an index/vertex pair plus immutable shared material state.
// Texture-list identity stays local: consumers can own distinct bindings even
// when their compiled numeric state is equal. Mutable mapper clocks live outside.
public readonly record struct EnbW3dRenderTriangleBinding
{
    public int TriangleOrdinal { get; init; }
    public int MaterialVertexIndex { get; init; }
    internal EnbW3dTriangleMaterialState? SharedState { get; init; }
    public IReadOnlyList<EnbW3dRenderTextureBinding?> Textures { get; init; }
    public bool UsesImplicitVertexMaterial { get; init; }
    public bool UsesImplicitShader { get; init; }
    public bool CurrentRetailNullMaterial { get; init; }
    public EnbW3dRenderVertexMaterialBinding? VertexMaterial
    {
        get => SharedState?.Key.Material;
        init => SharedState = new((SharedState?.Key ?? default) with { Material=value });
    }
    public EnbW3dRenderShaderBinding? Shader
    {
        get => SharedState?.Key.Shader;
        init => SharedState = new((SharedState?.Key ?? default) with { Shader=value });
    }
    public EnbW3dCompiledMaterialSemantics? Semantics
    {
        get => SharedState?.Key.Semantics;
        init => SharedState = new((SharedState?.Key ?? default) with { Semantics=value });
    }
    public EnbW3dRenderTriangleBinding(int TriangleOrdinal,int MaterialVertexIndex,
        EnbW3dRenderVertexMaterialBinding? VertexMaterial,EnbW3dRenderShaderBinding? Shader,
        IReadOnlyList<EnbW3dRenderTextureBinding?> Textures,EnbW3dCompiledMaterialSemantics? Semantics)
    {
        this.TriangleOrdinal=TriangleOrdinal;this.MaterialVertexIndex=MaterialVertexIndex;this.Textures=Textures;
        SharedState=new(new(VertexMaterial,Shader,Semantics));
    }
    internal bool HasSameStorage(EnbW3dRenderTriangleBinding other)
        =>ReferenceEquals(SharedState,other.SharedState)&&ReferenceEquals(Textures,other.Textures)
        &&TriangleOrdinal==other.TriangleOrdinal&&MaterialVertexIndex==other.MaterialVertexIndex
        &&UsesImplicitVertexMaterial==other.UsesImplicitVertexMaterial&&UsesImplicitShader==other.UsesImplicitShader
        &&CurrentRetailNullMaterial==other.CurrentRetailNullMaterial;
}

internal readonly record struct EnbW3dTriangleMaterialKey(EnbW3dRenderVertexMaterialBinding? Material,
    EnbW3dRenderShaderBinding? Shader,EnbW3dCompiledMaterialSemantics? Semantics);
internal sealed record EnbW3dTriangleMaterialState
{
    internal readonly EnbW3dTriangleMaterialKey Key;
    internal EnbW3dTriangleMaterialState(EnbW3dTriangleMaterialKey key)=>Key=key;
}

// Construction-scoped only. No global table, weak handles or per-frame sweep.
// The array's values retain the distinct states; this dictionary dies at publication.
internal sealed class EnbW3dTriangleBindingPool
{
    private readonly Dictionary<EnbW3dTriangleMaterialKey,EnbW3dTriangleMaterialState> states=new(KeyComparer.Instance);
    private EnbW3dTriangleMaterialState? last;
    internal EnbW3dRenderTriangleBinding Create(int ordinal,int vertex,EnbW3dRenderVertexMaterialBinding? material,
        EnbW3dRenderShaderBinding? shader,IReadOnlyList<EnbW3dRenderTextureBinding?> textures,
        EnbW3dCompiledMaterialSemantics? semantics)
        =>new(){TriangleOrdinal=ordinal,MaterialVertexIndex=vertex,Textures=textures,SharedState=Intern(new(material,shader,semantics))};
    private EnbW3dTriangleMaterialState Intern(EnbW3dTriangleMaterialKey key,EnbW3dTriangleMaterialState? existing=null)
    {
        if(last is {} prior && KeyComparer.Instance.Equals(prior.Key,key))return prior;
        if(!states.TryGetValue(key,out var state))states.Add(key,state=existing??new(key));
        return last=state;
    }
    internal static EnbW3dRenderTriangleBinding[] Freeze(IEnumerable<EnbW3dRenderTriangleBinding> source)
    {
        var owned=source.ToArray();CompactOwned(owned);return owned;
    }
    // Caller must own an unpublished array. Never mutate another pass's array.
    internal static void CompactOwned(EnbW3dRenderTriangleBinding[] owned)
    {
        var pool=new EnbW3dTriangleBindingPool();
        for(int i=0;i<owned.Length;i++)if(owned[i].SharedState is {} state)
            owned[i]=owned[i] with {SharedState=pool.Intern(state.Key,state)};
    }
    private sealed class KeyComparer:IEqualityComparer<EnbW3dTriangleMaterialKey>
    {
        internal static readonly KeyComparer Instance=new();
        public bool Equals(EnbW3dTriangleMaterialKey a,EnbW3dTriangleMaterialKey b)
            =>ReferenceEquals(a.Material?.Material,b.Material?.Material) && Nullable.Equals(a.Material,b.Material)
            && Nullable.Equals(a.Shader,b.Shader) && Nullable.Equals(a.Semantics,b.Semantics)
            && (!a.Shader.HasValue || SameBits(a.Shader.Value,b.Shader!.Value))
            && (!a.Semantics.HasValue || SameBits(a.Semantics.Value,b.Semantics!.Value));
        public int GetHashCode(EnbW3dTriangleMaterialKey key)
            =>HashCode.Combine(key.Material?.Material is {} m?RuntimeHelpers.GetHashCode(m):0,key.Material,key.Shader,key.Semantics);
        // Do not merge -0/+0 or distinct NaN payloads. Padding can only miss a
        // sharing opportunity, never substitute a numerically different state.
        private static bool SameBits<T>(in T a,in T b) where T:unmanaged
            =>MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in a),1))
                .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in b),1)));
    }
}
