using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>0094B920 -> 009493F0/00949550: a named missing file binds the
/// executable's failed-load image. Explicit unbound stages remain unbound.</summary>
public static class EnbCurrentRetailMeshTexturePreparation
{
    // A failed-image descriptor projection does not construct another native
    // MeshModel. Retain its exact owner; never resolve a newer same-name head.
    public static EnbW3dRenderMesh ModelSource(EnbW3dRenderMesh mesh)=>mesh.DerivedModelSource??mesh;
    public static EnbW3dRenderTextureBinding PrepareBinding(EnbW3dRenderTextureBinding binding,EnbRetailAssetBundle assets,bool missingResourcesOnly=false)
    {
        if(binding.IsUnbound || binding.CurrentRetailResolvedTexture?.Image is not null)return binding;
        if(missingResourcesOnly && assets.Textures.Resolve(binding.LogicalName).Status!=EnbTextureResolutionStatus.MissingResource)return binding;
        var texture=assets.Textures.ResolveCurrentRetailMissingMeshTexture(binding.LogicalName,assets.Paths.ClientExecutablePath);
        if(texture.Image is null)throw new InvalidDataException($"Mesh texture unavailable: {binding.LogicalName}: {texture.Diagnostic}");
        return texture.LogicalName==binding.LogicalName?binding:binding with {LogicalName=texture.LogicalName,ResolveLogicalNameAtRuntime=true};
    }
    public static EnbW3dRenderResource Prepare(EnbW3dRenderResource source, EnbRetailAssetBundle assets, bool missingResourcesOnly = false)
    {
        bool changed = false;
        bool preparedTable = false;
        EnbW3dRenderTextureBinding Texture(EnbW3dRenderTextureBinding binding)
        {
            var prepared=PrepareBinding(binding,assets,missingResourcesOnly);
            changed|=!ReferenceEquals(prepared,binding);
            return prepared;
        }
        var meshes = source.Meshes.Select(mesh =>
        {
            //0048D1F0 consumes every retained model+80 entry, including
            //textures unused by the visible descriptor. Existing entries
            //retain their image even after another named head is published.
            var loadedTextures=mesh.CurrentRetailLoadedTextureResources;
            if(loadedTextures.Count!=mesh.CurrentRetailLoadedTextureNames.Count)
            {
                loadedTextures=mesh.CurrentRetailLoadedTextureNames.Select(name=>
                    assets.Textures.ResolveCurrentRetailMissingMeshTexture(name,assets.Paths.ClientExecutablePath)).ToArray();
                preparedTable=true;
            }
            var prepared=(mesh).Copy().SetCurrentRetailLoadedTextureResources(loadedTextures).SetMaterialPasses(mesh.MaterialPasses.Select(pass => (pass).Copy().SetTextureStages(pass.TextureStages.Select(stage => (stage).Copy().SetTextures(stage.Textures.Select(Texture).ToArray()).SetCurrentRetailUniformTexture(stage.CurrentRetailUniformTexture is {} uniform ? Texture(uniform) : null)).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(triangle => triangle with {
                Textures = triangle.Textures.Select(t => t is null ? null : Texture(t)).ToArray()
            }).ToArray())).ToArray());
            prepared.DerivedModelSource=ModelSource(mesh);
            return prepared;
        }).ToArray();
        // Separate renderer resource identity from an unprepared world/other consumer.
        return changed || preparedTable ? source.Copy().SetKey(changed ? source.Key with { ResourceName = "robj-missing:" + source.Key.ResourceName } : source.Key).SetMeshes(meshes) : source;
    }
}
