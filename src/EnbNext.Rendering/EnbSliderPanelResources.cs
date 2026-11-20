using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Shared authored OPT04A pieces and 0066FD80 inner slider endpoints.</summary>
public sealed class EnbSliderPanelResources
{
    public readonly record struct Piece(string Name, EnbAuthoredUiAsset Asset, EnbUiRect Bounds) { public EnbRetailMeshPick Query { get; init; } }
    private readonly EnbRetailAssetBundle assets;
    public EnbW3dWorldBounds? BackgroundSphere { get; }
    public System.Numerics.Vector3 BackgroundExtents { get; }
    public EnbAttachedTextStyle LabelStyle(EnbInterfaceTextDefinition definition, EnbUiPoint offset)
        => EnbAttachedTextStyle.Load(assets,Asset,definition,System.Numerics.Matrix4x4.CreateTranslation((float)offset.X,-(float)offset.Y,0));
    public EnbAuthoredUiAsset Asset { get; }
    public Piece[] Pieces { get; }
    public EnbAuthoredUiAsset Checked { get; }
    public double ArrowSpan { get; }
    public EnbUiRect[] Tracks { get; } = new EnbUiRect[5];
    public EnbUiRect[] WheelBounds { get; } = new EnbUiRect[5];

    internal EnbSliderPanelResources(EnbRetailAssetBundle assets)
    {
        this.assets=assets;
        Asset = EnbAuthoredUiAsset.Load(assets, "opt04a.w3d") ?? throw new InvalidDataException("OPT04A unavailable.");
        if (Asset.Resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(Asset.Resource,hlod,System.Numerics.Matrix4x4.Identity,null,0,out var bounds,out var error))
                throw new InvalidDataException(error);
            BackgroundSphere=bounds.WorldSphere; BackgroundExtents=bounds.LocalBox.Extents;
            var b=bounds.LocalBox;
            Array.Fill(WheelBounds,new(b.Center.X-b.Extents.X+320,240-b.Center.Y-b.Extents.Y,b.Extents.X*2,b.Extents.Y*2));
        }
        var arrows = EnbRenderSession.Of(assets).HorizontalSlider;
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(Asset);
        double arrowSpan = ArrowSpan = EnbAuthoredUiAsset.MeshBounds(arrows.Left, arrows.Left.Resource.Meshes.Single().Name).Width - 2;
        for (int i = 0; i < Tracks.Length; i++)
        {
            var a = transforms[Asset.Hierarchy.Pivots.First(p => p.Name.Equals($"slider_{i:00}_min", StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
            var b = transforms[Asset.Hierarchy.Pivots.First(p => p.Name.Equals($"slider_{i:00}_max", StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
            Tracks[i] = new(Math.Min(a.X, b.X) + arrowSpan, a.Y, Math.Abs(b.X-a.X) - 2*arrowSpan, 16);
            if(Asset.Resource.Meshes.FirstOrDefault(m=>m.Name.Equals($"slider_{i:00}_regio",StringComparison.OrdinalIgnoreCase)) is { } region)
            {
                var transform=transforms[Asset.Hierarchy.Pivots.First(p=>p.Name.Equals(region.Name,StringComparison.OrdinalIgnoreCase)).Ordinal];
                var min=new System.Numerics.Vector3(float.PositiveInfinity);var max=new System.Numerics.Vector3(float.NegativeInfinity);
                for(int corner=0;corner<8;corner++)
                {
                    var point=System.Numerics.Vector3.Transform(new((corner&1)==0?region.BoundingBoxMin.X:region.BoundingBoxMax.X,(corner&2)==0?region.BoundingBoxMin.Y:region.BoundingBoxMax.Y,(corner&4)==0?region.BoundingBoxMin.Z:region.BoundingBoxMax.Z),transform);
                    min=System.Numerics.Vector3.Min(min,point);max=System.Numerics.Vector3.Max(max,point);
                }
                WheelBounds[i]=new(320+min.X,240-max.Y,max.X-min.X,max.Y-min.Y);
            }
        }
        Pieces = Asset.Resource.Meshes.Select(mesh => new Piece(mesh.Name,
            Asset with { Resource = Asset.Resource.Copy().SetMeshes(new[] { mesh }) },
            EnbAuthoredUiAsset.MeshBounds(Asset, mesh.Name)) { Query=EnbUiModelQuery.BindMainView(mesh,transforms[Asset.Hierarchy.Pivots.First(p=>p.Name==mesh.Name).Ordinal]) }).ToArray();
        var graphicsCheck = Pieces.First(p => p.Name == "CHECK00").Asset;
        EnbW3dRenderTextureBinding On(EnbW3dRenderTextureBinding t) => t with
            { LogicalName = "if_check_on.tga", Location = null, ResolveLogicalNameAtRuntime = true };
        Checked = graphicsCheck with { Resource = graphicsCheck.Resource.Copy().SetMeshes(graphicsCheck.Resource.Meshes.Select(m => (m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p => (p).Copy().SetTextureStages(p.TextureStages.Select(s => (s).Copy().SetTextures(s.Textures.Select(On).ToArray())).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t => t with
            { Textures = t.Textures.Select(b => b is null ? null : On(b)).ToArray() }).ToArray())).ToArray())).ToArray()) };
    }
}
