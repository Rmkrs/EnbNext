using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>Multiply projector material created by 0093D2E0 and updated by
/// 0093E1E0. Source capture and scene lifetime are owned by the projector.</summary>
public static class EnbCurrentRetailShadowMaterial
{
    public static EnbW3dRenderMaterialPass Bind(EnbW3dRenderMesh mesh,
        EnbW3dRenderTextureBinding shadow, EnbW3dRenderTextureBinding gradient, float intensity)
    {
        var black = new EnbRenderPackedRgb8(0,0,0,0);
        var material = new EnbW3dRenderVertexMaterial(0,"scene shadow",0,
            black,black,black,black,0,1,0,null,null);
        // Material +60=1 reaches D3DRS_LIGHTING in 00930910. All reflectance
        // terms are zero; only the projector's exact float emissive remains.
        var lighting = new EnbW3dVertexLightingSemantics(true,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(0x490853,material,2,lighting,
                out var semantics,out var error)) throw new InvalidDataException(error);
        semantics = semantics with { Uniforms = semantics.Uniforms with { Emissive = new(1-intensity) } };
        var sampling = EnbW3dTextureSamplingSemantics.RetailDefault with {
            AddressU=EnbRenderTextureAddressMode.Clamp, AddressV=EnbRenderTextureAddressMode.Clamp };
        EnbW3dRenderTextureBinding[] textures = [shadow with { Sampling=sampling }, gradient with { Sampling=sampling }];
        var uv = new Vector2[mesh.Positions.Count];
        var stages = textures.Select((texture,i)=>new EnbW3dRenderTextureStage(i,[texture],uv,EnbW3dRenderBindingCardinality.Uniform)
            {EffectiveTextureCoordinateSetIndex=i,EffectiveRuntimeTexCoords=uv}).ToArray();
        var triangleStates = new EnbW3dTriangleBindingPool();
        var bindings = Enumerable.Range(0,mesh.Indices.Count/3).Select(i=>triangleStates.Create(i,
            (int)mesh.Indices[i*3],null,null,textures,semantics) with {UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
        return new(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,
            [],[],[],stages,bindings,semantics);
    }
}
