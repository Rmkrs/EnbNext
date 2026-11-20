using EnbNext.Assets;

namespace EnbNext.Rendering;

// One selected sphere child. Its assembly owner must preserve every sibling.
public sealed record EnbRetailSphereAttachment(int Ordinal,int PivotIndex,string BoneName,
    EnbW3dRenderResourceKey Key,EnbRetailSphereAsset Asset,EnbW3dRenderTextureBinding? Texture)
{
    public EnbRetailSphereAttachment ApplyHsv(EnbRetailAssetBundle assets,System.Numerics.Vector3 shift)
    {
        if(shift==System.Numerics.Vector3.Zero)return this;
        var texture=Texture;
        if(texture is not null && texture.CurrentRetailResolvedTexture?.Image?.Format!=EnbTextureFormat.CurrentRetailMissingArgb8)
        {
            var recolored=EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,
                texture.CurrentRetailResolvedTexture??assets.Textures.Resolve(Asset.Header.Texture),shift,texture.CurrentRetailTextureState);
            texture=texture with {LogicalName=recolored.LogicalName,CurrentRetailResolvedTexture=recolored,
                CurrentRetailTextureState=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(recolored)};
        }
        return this with {Asset=Asset.ShiftColors(shift),Texture=texture,
            Key=Key with {ResourceName=$"sphere-hsv:{BitConverter.SingleToInt32Bits(shift.X):X8}:{BitConverter.SingleToInt32Bits(shift.Y):X8}:{BitConverter.SingleToInt32Bits(shift.Z):X8}:{Key.ResourceName}"}};
    }

    public static EnbRetailSphereAttachment Load(EnbRetailAssetBundle assets,EnbW3dRenderHierarchy tree,
        EnbCurrentRetailW3dAggregateAttachment attachment,int ordinal,EnbResolvedW3dResource? source=null)
    {
        if(ordinal<0)throw new ArgumentOutOfRangeException(nameof(ordinal));
        int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(tree,attachment.BoneName);
        if(pivot>=tree.Pivots.Count)throw new InvalidDataException("Sphere attachment requires a nonempty hierarchy");
        if(source is not null)return LoadFromSource(assets,source,ordinal,pivot,attachment.BoneName);
        return LoadResource(assets,attachment.SubobjectName+".w3d",attachment.SubobjectName,ordinal,pivot,attachment.BoneName);
    }

    internal static EnbRetailSphereAttachment LoadStandalone(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
        => LoadResource(assets,resolved.Definition.File ?? throw new InvalidDataException("Standalone sphere FILE missing"),
            resolved.Definition.Name ?? throw new InvalidDataException("Standalone sphere NAME missing"),0,0,string.Empty,authoredFile:true);

    private static EnbRetailSphereAttachment LoadResource(EnbRetailAssetBundle assets,string file,string requestedName,int ordinal,int pivot,string bone,bool authoredFile=false)
    {
        byte[] bytes;EnbMixResourceLocation location;
        if(!(authoredFile?assets.FileResources.TryReadResource(file,out bytes,out location):
            assets.RetailMixResources!.TryReadResource(file,out bytes,out location)))
            throw new InvalidDataException("Sphere attachment resource missing");
        var sphere=EnbRetailSphereAsset.Decode(bytes);
        if(!sphere.Header.Name.Equals(requestedName,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Sphere attachment name mismatch");
        return Create(assets,sphere,location,ordinal,pivot,bone);
    }

    public static EnbRetailSphereAttachment LoadFromSource(EnbRetailAssetBundle assets,EnbResolvedW3dResource source,
        int ordinal=0,int pivot=0,string bone="")
    {
        var entry=source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype;
        string name=source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName;
        if(entry is null)
            foreach(var candidate in source.Model.PrototypeEntries)
                if(candidate.Kind==EnbW3dPrototypeKind.Sphere && source.Model.Spheres[candidate.Ordinal].Header.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
                {entry=candidate;break;}
        if(entry is not {Kind:EnbW3dPrototypeKind.Sphere} selected)
            throw new InvalidDataException("Selected prototype is not a sphere");
        var asset=EnbRetailSphereAsset.FromDefinition(source.Model.Spheres[selected.Ordinal]);
        if(source.CurrentRetailAggregate is {} aggregate)asset=asset with {Header=asset.Header with {Name=aggregate.RenderDefinition.Name}};
        return Create(assets,asset,source.CurrentRetailAggregate?.BaseResourceLocation??source.ResourceLocation,ordinal,pivot,bone);
    }

    private static EnbRetailSphereAttachment Create(EnbRetailAssetBundle assets,EnbRetailSphereAsset sphere,
        EnbMixResourceLocation location,int ordinal,int pivot,string bone)
    {
        EnbW3dRenderTextureBinding? texture=null;
        if(sphere.Header.Texture.Length>0)
        {
            //0090B6B9..D5 uses the same named TextureClass acquisition as rings.
            var resolved=assets.Textures.ResolveCurrentRetailMissingTexture(sphere.Header.Texture,assets.Paths.ClientExecutablePath);
            if(resolved.Image is null)throw new InvalidDataException($"Sphere attachment texture unavailable: {resolved.Diagnostic}");
            texture=new(0,resolved.LogicalName,resolved.ResourceLocation is {} textureLocation?EnbW3dRenderTextureLocation.From(textureLocation):null)
                {Sampling=EnbW3dTextureSamplingSemantics.RetailDefault,
                 ResolveLogicalNameAtRuntime=resolved.ResourceLocation is null,CurrentRetailResolvedTexture=resolved};
        }
        return new(ordinal,pivot,bone,EnbW3dRenderResourceKey.From(location),sphere,texture);
    }
}
