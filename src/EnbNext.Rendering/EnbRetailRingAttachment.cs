using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// One explicitly selected 0x742 child. This loader never discards sibling
// emitters or other primitive kinds; the aggregate owner must handle them.
public sealed record EnbRetailRingAttachment(int Ordinal,int PivotIndex,string BoneName,
    EnbW3dRenderResourceKey Key,EnbRetailRingAsset Asset,EnbW3dRenderTextureBinding? Texture)
{
    public EnbRetailRingAttachment ApplyHsv(EnbRetailAssetBundle assets,Vector3 shift)
    {
        if(shift==Vector3.Zero)return this;
        var texture=Texture;
        if(texture is not null && texture.CurrentRetailResolvedTexture?.Image?.Format!=EnbTextureFormat.CurrentRetailMissingArgb8)
        {
            var recolored=EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,
                texture.CurrentRetailResolvedTexture??assets.Textures.Resolve(Asset.Header.Texture),shift,texture.CurrentRetailTextureState);
            texture=texture with {LogicalName=recolored.LogicalName,CurrentRetailResolvedTexture=recolored,
                CurrentRetailTextureState=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(recolored)};
        }
        return this with {Asset=Asset.ShiftColors(shift),Texture=texture,
            Key=Key with {ResourceName=$"ring-hsv:{BitConverter.SingleToInt32Bits(shift.X):X8}:{BitConverter.SingleToInt32Bits(shift.Y):X8}:{BitConverter.SingleToInt32Bits(shift.Z):X8}:{Key.ResourceName}"}};
    }

    public static EnbRetailRingAttachment Load(EnbRetailAssetBundle assets,EnbW3dRenderHierarchy tree,
        EnbCurrentRetailW3dAggregateAttachment attachment,int ordinal,Vector3 hsvShift=default,EnbResolvedW3dResource? source=null)
    {
        if(ordinal<0)throw new ArgumentOutOfRangeException(nameof(ordinal));
        int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(tree,attachment.BoneName);
        if(pivot>=tree.Pivots.Count)throw new InvalidDataException("Ring attachment requires a nonempty hierarchy");
        if(source is not null)return LoadFromSource(assets,source,ordinal,pivot,attachment.BoneName,hsvShift);
        return LoadResource(assets,attachment.SubobjectName+".w3d",attachment.SubobjectName,ordinal,pivot,attachment.BoneName,hsvShift);
    }

    internal static EnbRetailRingAttachment LoadStandalone(EnbRetailAssetBundle assets,EnbBaseAssetResolution resolved)
        => LoadResource(assets,resolved.Definition.File ?? throw new InvalidDataException("Standalone ring FILE missing"),
            resolved.Definition.Name ?? throw new InvalidDataException("Standalone ring NAME missing"),0,0,string.Empty,Vector3.Zero,authoredFile:true);

    private static EnbRetailRingAttachment LoadResource(EnbRetailAssetBundle assets,string file,string name,
        int ordinal,int pivot,string bone,Vector3 hsvShift,bool authoredFile=false)
    {
        byte[] bytes;EnbMixResourceLocation location;
        if(!(authoredFile?assets.FileResources.TryReadResource(file,out bytes,out location):
            assets.RetailMixResources!.TryReadResource(file,out bytes,out location)))
            throw new InvalidDataException("Ring attachment resource missing");
        var ring=EnbRetailRingAsset.Decode(bytes);
        if(!ring.Header.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ring attachment name mismatch");
        return Create(assets,ring,location,ordinal,pivot,bone,hsvShift);
    }

    public static EnbRetailRingAttachment LoadFromSource(EnbRetailAssetBundle assets,EnbResolvedW3dResource source,
        int ordinal=0,int pivot=0,string bone="",Vector3 hsvShift=default)
    {
        var entry=source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype;
        string name=source.CurrentRetailAggregate?.BaseRequestedObjectName??source.RequestedObjectName;
        if(entry is null)
            foreach(var candidate in source.Model.PrototypeEntries)
                if(candidate.Kind==EnbW3dPrototypeKind.Ring && source.Model.Rings[candidate.Ordinal].Header.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
                {entry=candidate;break;}
        if(entry is not {Kind:EnbW3dPrototypeKind.Ring} selected)
            throw new InvalidDataException("Selected prototype is not a ring");
        var asset=EnbRetailRingAsset.FromDefinition(source.Model.Rings[selected.Ordinal]);
        if(source.CurrentRetailAggregate is {} aggregate)asset=asset.WithName(aggregate.RenderDefinition.Name);
        return Create(assets,asset,source.CurrentRetailAggregate?.BaseResourceLocation??source.ResourceLocation,ordinal,pivot,bone,hsvShift);
    }

    private static EnbRetailRingAttachment Create(EnbRetailAssetBundle assets,EnbRetailRingAsset ring,
        EnbMixResourceLocation location,int ordinal,int pivot,string bone,Vector3 hsvShift)
    {
        ring=ring.ShiftColors(hsvShift);
        EnbW3dRenderTextureBinding? texture=null;
        if(ring.Header.Texture.Length>0)
        {
            //00910D56..71 acquires every nonempty name through manager+30.
            //Its failed load keeps009AF0F0's image rather than rejecting the ring.
            var resolved=assets.Textures.ResolveCurrentRetailMissingTexture(ring.Header.Texture,assets.Paths.ClientExecutablePath);
            if(hsvShift!=Vector3.Zero)
                resolved=EnbCurrentRetailEffectMeshHsvRuntime.ResolveRetainedTexture(assets.Textures,resolved,hsvShift);
            if(resolved.Image is null)throw new InvalidDataException($"Ring attachment texture unavailable: {resolved.Diagnostic}");
            texture=new(0,resolved.LogicalName,resolved.ResourceLocation is {} textureLocation?EnbW3dRenderTextureLocation.From(textureLocation):null)
                {
                    ResolveLogicalNameAtRuntime=resolved.ResourceLocation is null,
                    Sampling=EnbW3dTextureSamplingSemantics.RetailDefault,
                    CurrentRetailTextureState=EnbCurrentRetailEffectMeshHsvRuntime.GetRecoloredTextureState(resolved),
                    CurrentRetailResolvedTexture=resolved,
                };
        }
        var key=EnbW3dRenderResourceKey.From(location);
        if(hsvShift!=Vector3.Zero)key=key with {ResourceName=$"ring-hsv:{BitConverter.SingleToInt32Bits(hsvShift.X):X8}:{BitConverter.SingleToInt32Bits(hsvShift.Y):X8}:{BitConverter.SingleToInt32Bits(hsvShift.Z):X8}:{key.ResourceName}"};
        return new(ordinal,pivot,bone,key,ring,texture);
    }
}
