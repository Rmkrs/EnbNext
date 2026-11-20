using EnbNext.Assets;
using EnbNext.Rendering;

internal static class SphereMeshParentFixture
{
    public static EnbCurrentRetailRObjEffectAsset Load(EnbRetailAssetBundle assets,bool multilevel=false,bool includeRings=true,bool forceChild=false,bool missingMeshTexture=false,bool proxyMetadata=false)
    {
        var resolved=multilevel?RingMeshLevelsFixture.Resolve(assets,proxyMetadata):assets.BaseAssetResolver!.Resolve(1877);
        if(proxyMetadata && !multilevel)resolved=HlodProxyMetadataFixture.Resolve(assets,resolved);
        if(missingMeshTexture)
        {
            if(multilevel)throw new InvalidDataException("Missing texture fixture uses the authored single-level parent");
            resolved=MeshTextureFailureFixture.Resolve(assets,resolved);
        }
        var source=resolved.W3d!;var aggregate=source.CurrentRetailAggregate!;
        var definition=aggregate.RenderDefinition;
        var first=definition.Attachments[0];
        // Keep the installed mesh parent and all ring siblings; append one
        // installed sphere at the first authored ring pivot.
        var combined=definition with {Flags=forceChild ? definition.Flags|1u : definition.Flags,
            Attachments=[..(includeRings?definition.Attachments:[]),new("sk_clx01p2",first.BoneName)]};
        var binding=new EnbCurrentRetailW3dAggregateResolution(combined,aggregate.BaseLogicalFileName,
            aggregate.BaseRequestedObjectName,aggregate.BaseResourceLocation);
        return EnbCurrentRetailRObjEffectAsset.Load(assets,resolved with {W3d=source with {CurrentRetailAggregate=binding}});
    }
}
