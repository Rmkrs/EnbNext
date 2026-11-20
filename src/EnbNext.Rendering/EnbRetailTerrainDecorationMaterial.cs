namespace EnbNext.Rendering;

/// <summary>47A2F0 retains table texture0/material0 and descriptor shader(0,0).</summary>
public sealed record EnbRetailTerrainDecorationMaterial(object? TextureIdentity,
    EnbW3dRenderTextureBinding? Texture,EnbW3dRenderVertexMaterialBinding Material,
    EnbW3dCompiledMaterialSemantics Semantics,EnbCurrentRetailMaterialBatchIdentity Identity)
{
    public static EnbRetailTerrainDecorationMaterial Create(EnbRetailShadowObject meshObject)
    {
        var mesh=EnbRetailTerrainShadowObjectFactory.Descriptor(meshObject);var owner=EnbRetailTerrainShadowObjectFactory.ModelOwner(meshObject);
        if(mesh.CurrentRetailLoadedMaterialRuntime.Count==0)
            throw new InvalidDataException("Decoration has no loaded material0.");
        var runtime=mesh.CurrentRetailLoadedMaterialRuntime[0];var material=runtime.Material;
        var pass=mesh.MaterialPasses[0];
        uint shader=pass.ShaderCardinality==EnbW3dRenderBindingCardinality.PerTriangle
            ?pass.Shaders[0].FixedFunction.PackedRetailState:pass.CurrentRetailUniformShader;
        if(!EnbW3dMaterialCompiler.TryCompilePackedRetailState(shader,material,1,runtime.Lighting,out var semantics,out var reason))
            throw new InvalidDataException(reason);
        object? texture=owner.LoadedTextureCount==0?null:owner.TextureIdentity(0);
        var binding=texture is null?null:new EnbW3dRenderTextureBinding(0,mesh.CurrentRetailLoadedTextureNames[0],null)
            {ResolveLogicalNameAtRuntime=true,CurrentRetailTextureState=texture as EnbCurrentRetailTextureObjectState};
        var identity=EnbCurrentRetailMaterialBatchIdentity.Create(semantics.Uniforms,material.Attributes&5,runtime.Lighting,
            [runtime.Coordinates.Stage0,runtime.Coordinates.Stage1,2,3,4,5,6,7],owner.MaterialRuntimeWord64At(0),
            [owner.MapperIdentityAt(0,0),owner.MapperIdentityAt(0,1),null,null,null,null,null,null]);
        return new(texture,binding,new(0,material){CurrentRetailRuntimeCoordinates=runtime.Coordinates},semantics,identity);
    }

    public bool Matches(EnbRetailTerrainDecorationMaterial other,object? sharedTexture)=>
        ReferenceEquals(TextureIdentity,sharedTexture)&&Semantics.FixedFunction.PackedRetailState==other.Semantics.FixedFunction.PackedRetailState&&Identity.Equals(other.Identity);
}
