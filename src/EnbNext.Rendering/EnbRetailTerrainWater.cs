using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbRetailTerrainWater
{
    //00469DEE creates through the ordinary manager before00469E01 tests
    //the resulting class. There is no swatch-manager explicit FILE retry.
    public static bool TryAcquireSwatch(EnbW3dRenderResourceCache prototypes,EnbRetailFileResolver files,
        EnbMixResourceCatalog resources,string name,uint clock,out EnbW3dRenderMesh swatch,out string? diagnostic)
    {
        swatch=default!;diagnostic=null;
        if(EnbCurrentRetailBuiltinRenderObjectSemantics.IsNullRenderObject(name))return false;
        if(!prototypes.TryFindPrototypeSource(name,out var source))
        {
            if(!new EnbRetailW3dFileResolver(files,resources).TryResolveLazyFile(name,out var loaded,out diagnostic) ||
                !prototypes.TryLoadFile(loaded,out _,out diagnostic,clock))return false;
            if(!prototypes.TryFindPrototypeSource(name,out source))
            {
                if(prototypes.CapturePrototypeSources(out _).Values.Any(value=>value.PrototypeFile?.HasUnrepresentedRenderPrototypes==true ||
                    value.AcquisitionPublications.Any(publication=>publication.PrototypeFile?.HasUnrepresentedRenderPrototypes==true)))
                    diagnostic="Sea swatch name absence is unproven because retained FILE publications contain undecoded render prototypes.";
                return false;
            }
        }
        if(source.AggregatePrototype is not null && !EnbCurrentRetailSwatchCatalog.TryCreateAggregateBase(source,files,resources,
            prototypes,clock,out source,out diagnostic,out _))return false;
        if(source.RegisteredPrototype is not {Kind:EnbW3dPrototypeKind.Mesh} entry ||
            !prototypes.TryGetRegisteredPrototype(source,out var table))return false;
        swatch=table.Meshes[entry.Ordinal];return true;
    }

    /// <summary>00469DF1..00469E15 admits only the registered class0 mesh.
    /// The source file may publish other meshes and non-mesh prototypes.</summary>
    public static bool TrySelectSwatch(EnbW3dRenderResourceCache prototypes,string name,
        out EnbW3dRenderMesh swatch)
    {
        swatch=default!;
        if(!prototypes.TryFindPrototypeSource(name,out var source) ||
            source.RegisteredPrototype is not {Kind:EnbW3dPrototypeKind.Mesh} entry ||
            !prototypes.TryGetRegisteredPrototype(source,out var resource))return false;
        swatch=resource.Meshes[entry.Ordinal];
        return true;
    }

    public static EnbW3dRenderMesh Build(EnbRetailTerrain terrain, EnbW3dRenderMesh swatch,
        IReadOnlyList<EnbRetailTerrainPatch> patches)
    {
        float step=terrain.HorizontalScale/terrain.Width;
        var geometry=EnbRetailTerrainSeaGeometry.Build(terrain.Width,terrain.Width/terrain.PatchGridWidth,
            terrain.PatchGridWidth,step,step,terrain.SeaLevel,patches);
        var positions=geometry.Positions;var indices=geometry.Indices;
        float[] scales = [terrain.SeaDetailScales.X,terrain.SeaDetailScales.Y,terrain.SeaDetailScales.Z,terrain.SeaDetailScales.W];
        var passes = swatch.MaterialPasses.Take(2).Select(pass => {
            var original = pass.TriangleBindings[0];
            var semantics = original.Semantics ?? throw new InvalidDataException("Sea swatch has no material semantics.");
            // 004691D0 copies each swatch pass, forces scene fog, and disables culling.
            uint packed = (semantics.FixedFunction.PackedRetailState & 0xfff7fdffu) | 0x100u;
            if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(packed,
                    swatch.VertexMaterials[0], pass.TextureStages.Count, semantics.VertexLighting,
                    out var compiled, out var reason)) throw new InvalidDataException(reason);
            // Preserve the authored pass's material and mapper programs; only raster state changes.
            compiled = compiled with { Uniforms = semantics.Uniforms, Mapper0Program = semantics.Mapper0Program,
                Mapper1Program = semantics.Mapper1Program, PermutationKey = compiled.PermutationKey with {
                    Mapper0 = semantics.PermutationKey.Mapper0, Mapper1 = semantics.PermutationKey.Mapper1 } };
            return (pass).Copy().SetShaders([]).SetShaderCardinality(EnbW3dRenderBindingCardinality.None).SetTextureStages(pass.TextureStages.Select(stage => {
                    var coords = geometry.Coordinates(terrain.Width,terrain.Width/2,step,step,
                        scales[pass.Ordinal*2+stage.Ordinal]);
                    return (stage).Copy().SetRuntimeTexCoords(coords).SetEffectiveRuntimeTexCoords(coords).SetEffectiveTextureCoordinateSetIndex(stage.Ordinal);
                }).ToArray()).SetTriangleBindings(Enumerable.Range(0,indices.Length/3).Select(i => original with {
                    TriangleOrdinal=i, MaterialVertexIndex=(int)indices[i*3], Shader=null,
                    UsesImplicitShader=true, Semantics=compiled }).ToArray()).SetSingleBindingSemantics(compiled);
        }).ToArray();
        Vector3 min=positions.Aggregate(Vector3.Min),max=positions.Aggregate(Vector3.Max),center=(min+max)*0.5f;
        // 00474B30 emits generated sea buffers directly (00476CCC), not
        // through MeshClass admission. The swatch supplies materials only;
        // its loaded model bounds must not cull the generated lake.
        return (swatch).Copy().SetCurrentRetailUsesMeshRenderer(false).SetCurrentRetailLoadedGeometry(null).SetName("terrain.water").SetOrdinal(0).SetPositions(positions).SetNormals(Enumerable.Repeat(Vector3.UnitZ,positions.Length).ToArray()).SetIndices(indices).SetBoneIndices([]).SetTriangleMetadata(new EnbW3dRenderTriangleMetadata[indices.Length/3]).SetMaterialPasses(passes).SetBoundingBoxMin(min).SetBoundingBoxMax(max).SetBoundingSphereCenter(center).SetBoundingRadius(Vector3.Distance(min,max)*0.5f).SetEffectiveColor1([]).SetRetailTextureCoordinateSets([]);
    }
}
