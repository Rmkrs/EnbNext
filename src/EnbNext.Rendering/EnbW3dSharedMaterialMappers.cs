namespace EnbNext.Rendering;

/// <summary>
/// Retained mapper identity for descriptor material references shared by swatch
/// manager 0075BE00 and copied by 0075CB10. GPU allocation/retirement is not a
/// native mapper construction/reset event. The material owner supplies its
/// actual creation clock (00932B40), independently of its first visible draw.
/// </summary>
public sealed record EnbW3dSharedMaterialMappers(
    EnbW3dMapperRuntimeState Stage0,
    EnbW3dMapperRuntimeState Stage1)
{
    // Bounded owner-local cache: words and these mapper identities only. It
    // never retains source materials, mesh generations or a binding side table.
    private EnbCurrentRetailMaterialBatchIdentity? materialIdentity;
    internal EnbCurrentRetailMaterialBatchIdentity RetainMaterialIdentity(EnbW3dMaterialUniforms uniforms,
        uint flags,EnbW3dVertexLightingSemantics lighting,int coordinate0,int coordinate1,uint word64,bool use0,bool use1)
    {
        object? mapper0=use0?Stage0:null,mapper1=use1?Stage1:null;
        var current=Volatile.Read(ref materialIdentity);
        if(current is not null && current.Matches(uniforms,flags,lighting,coordinate0,coordinate1,word64,mapper0,mapper1))return current;
        var next=EnbCurrentRetailMaterialBatchIdentity.Create(uniforms,flags,lighting,
            [coordinate0,coordinate1,2,3,4,5,6,7],word64,[mapper0,mapper1,null,null,null,null,null,null]);
        Volatile.Write(ref materialIdentity,next);return next;
    }
    // Cache population is not mapper identity and must not change record equality.
    public bool Equals(EnbW3dSharedMaterialMappers? other)=>ReferenceEquals(this,other)||other is not null &&
        EqualityComparer<EnbW3dMapperRuntimeState>.Default.Equals(Stage0,other.Stage0)&&
        EqualityComparer<EnbW3dMapperRuntimeState>.Default.Equals(Stage1,other.Stage1);
    public override int GetHashCode()=>HashCode.Combine(Stage0,Stage1);

    public static bool TryCreate(EnbW3dRenderVertexMaterial material, uint creationMilliseconds,
        out EnbW3dSharedMaterialMappers mappers, out string? diagnostic,
        EnbW3dRetailMapperRandomSource? randomSource=null)
    {
        ArgumentNullException.ThrowIfNull(material);
        mappers = default!;
        bool Create(EnbW3dRenderMapper? definition,EnbW3dRetainedMapperOwner? retained,
            out EnbW3dMapperRuntimeState state,out string? error)
        {
            if(retained is not null)return retained.TryRetain(creationMilliseconds,randomSource,out state,out error);
            state=null!;
            return EnbW3dMapperRuntimeProgramCompiler.TryCompile(definition,out var program,out error) &&
                EnbW3dMapperRuntimeState.TryCreate(program,creationMilliseconds,randomSource,out state,out error);
        }
        if (!Create(material.Mapper0,material.CurrentRetailMapper0Owner,out var stage0,out diagnostic) ||
            !Create(material.Mapper1,material.CurrentRetailMapper1Owner,out var stage1,out diagnostic))
            return false;
        mappers = new(stage0, stage1);
        return true;
    }
}
