using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed class EnbBrowserPanelResources
{
    public readonly struct Piece(string key,EnbAuthoredUiAsset asset,EnbUiRect bounds)
    {
        public readonly string Key=key;
        public readonly EnbAuthoredUiAsset Asset=asset;
        public readonly EnbUiRect Bounds=bounds;
        public EnbRetailMeshPick Query {get;init;}
    }
    private readonly EnbAuthoredUiAsset asset;
    private readonly Matrix4x4[] transforms;
    private Vector3? titlePosition;
    public Piece[] Pieces {get;}
    public int[] DrawOrder {get;}
    public EnbInterfaceLayoutCatalog Layout {get;}
    public EnbW3dWorldBounds? BackgroundSphere {get;}
    internal EnbBrowserPanelResources(EnbRetailAssetBundle assets)
    {
        asset=EnbAuthoredUiAsset.Load(assets,"jweb01.w3d")??throw new InvalidDataException("JWEB01 unavailable.");
        Layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_jweb01.ini"));
        transforms=EnbAuthoredUiAsset.ResolveTransforms(asset);
        if(asset.Resource.HierarchicalLods.SingleOrDefault() is {} hlod)
        {
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(asset.Resource,hlod,Matrix4x4.Identity,null,0,out var bounds,out var error))
                throw new InvalidDataException(error);
            BackgroundSphere=bounds.WorldSphere;
        }
        Pieces=asset.Resource.Meshes.Select(mesh=>new Piece("manual/"+mesh.Name,
            asset with {Resource=asset.Resource.Copy().SetMeshes(new[]{mesh})},EnbAuthoredUiAsset.MeshBounds(asset,mesh.Name))
            {Query=EnbUiModelQuery.BindMainView(mesh,transforms[asset.Hierarchy.Pivots.First(p=>p.Name==mesh.Name).Ordinal])}).ToArray();
        DrawOrder=Enumerable.Range(0,Pieces.Length).Where(i=>Pieces[i].Key!="manual/BROWSER")
            .OrderBy(i=>Pieces[i].Key.EndsWith("FRAME")||Pieces[i].Key.EndsWith("BARS_BOTTOM")?0:Pieces[i].Key.Contains("ICON")?2:1).ToArray();
    }
    public EnbUiRect Design(string key)=>Pieces.First(p=>p.Key==key).Bounds;
    public Vector3 TitlePosition(EnbInterfaceTextDefinition definition)=>titlePosition??=
        transforms[asset.Hierarchy.Pivots.First(p=>p.Name.Equals(definition.Properties["Attach"],StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
}
