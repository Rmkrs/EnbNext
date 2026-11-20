using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>One authored BUY_SHIP04 name panel, owned by the render asset session.</summary>
public sealed class EnbShipNameResources
{
    public EnbAuthoredUiAsset Asset { get; }
    public EnbInterfaceLayoutCatalog Layout { get; }
    public EnbInterfaceTextDefinition Definition { get; }
    public EnbAttachedTextStyle Style { get; }
    public float RootHalfWidth { get; }

    public EnbShipNameResources(EnbRetailAssetBundle assets)
    {
        Asset=EnbAuthoredUiAsset.Load(assets,"buy_ship04.w3d")??throw new InvalidDataException("buy_ship04 unavailable");
        Layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_buy_ship04.ini"));
        if(!Layout.TryResolve("NAME_buy_ship04",out var definition))throw new InvalidDataException("NAME_buy_ship04 unavailable");
        Definition=definition;Style=EnbAttachedTextStyle.Load(assets,Asset,definition,Matrix4x4.Identity);
        if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(Asset.Resource,Asset.Resource.HierarchicalLods.Single(),
            Matrix4x4.Identity,null,0,out var bounds,out var error))throw new InvalidDataException(error);
        RootHalfWidth=bounds.LocalBox.Extents.X;
    }
}
