using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private string _cargoFilterValue="All", _vaultFilterValue="All";
    public Action<uint,bool,string>? InventoryFilterPersistence { get; set; }

    private void RestoreInventoryFilters(EnbRetailPlayerOptions? options)
    {
        _cargoFilterValue=options?.InventoryCargoFilter??"All";
        _vaultFilterValue=options?.InventoryVaultFilter??"All";
        // 57AD50 selects the first prefix match, or row zero on failure.
        _filter=FilterSelection(_cargoFilterValue);
        _vaultFilter=FilterSelection(_vaultFilterValue);
    }
    private static int FilterSelection(string value)
    {
        for(int i=0;i<EnbItemInformation.Filters.Length;i++)
            if(EnbItemInformation.Filters[i].StartsWith(value,StringComparison.Ordinal))return i;
        return 0;
    }
    private void SelectInventoryFilter(bool vault,int selection)
    {
        string value=EnbItemInformation.Filters[selection];
        if(vault){_vaultFilter=selection;_vaultFilterValue=value;_vaultFilterOpen=false;}
        else {_filter=selection;_cargoFilterValue=value;_filterOpen=false;}
        if(_owner is not uint owner)return;
        if(InventoryFilterPersistence is {} save)save(owner,vault,value);
        else _assets?.SaveInventoryFilter(owner,vault,value);
    }

    private void EmptyInventorySlotTooltip(string id)
        => _tips[id]=(_inventorySlotTips?.GetValueOrDefault("NORMAL_SLOT","")??"").Replace("\\n","\n")
            .Replace("%s",EnbRetailItemTooltip.EmptyDescription(uint.MaxValue));

    private void LoadInventoryFilterMask(EnbRetailAssetBundle assets)
    {
        // 6A7C00 loads the loose INV_BLANK01 mesh, assigns icon_mask.tga,
        // and 6A8AB0 attaches it at the slot bone. Preserve its UV/material.
        var resources=assets.RetailMixResources!;
        if(!resources.TryReadResource("inv_blank01.w3d",out var bytes,out var location) ||
            !EnbW3dModel.TryDecode(bytes,out var model) ||
            !EnbAuthoredUiAsset.TryCompile(resources,location,model,"INV_BLANK01",out var resource))
            throw new InvalidDataException("Inventory filter mask unavailable");
        EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding texture)=>texture with
            {LogicalName="icon_mask.tga",Location=null,ResolveLogicalNameAtRuntime=true};
        resource=resource.Copy().SetMeshes(resource.Meshes.Select(mesh=>(mesh).Copy().SetMaterialPasses(mesh.MaterialPasses.Select(pass=>(pass).Copy().SetTextureStages(pass.TextureStages.Select((stage,i)=>i==0?(stage).Copy().SetTextures(stage.Textures.Select(Replace).ToArray()):stage).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(binding=>binding with
                    {Textures=binding.Textures.Select((texture,i)=>i==0 && texture is not null?Replace(texture):texture).ToArray()}).ToArray())).ToArray())).ToArray());
        var mesh=resource.Meshes.Single();
        var geometry=new EnbW3dDeformedGeometry(mesh.Positions.ToImmutableArray(),mesh.Normals.ToImmutableArray());
        var min=geometry.Positions.Aggregate(Vector3.Min);var max=geometry.Positions.Aggregate(Vector3.Max);
        // A loose mesh has no hierarchy; explicit geometry bypasses hierarchy evaluation.
        var asset=new EnbAuthoredUiAsset(resource,new(0,"INV_BLANK01",Vector3.Zero,[],[])){Geometry=geometry};
        _art["inventoryFilterMask"]=new(asset,new(min.X,-max.Y,max.X-min.X,max.Y-min.Y));
    }
    private void InventoryFilterMask(EnbSceneItemSnapshot item,Rect slot,string filter)
    {
        // 68EB10 leaves unknown types unmasked; known types compare the filter
        // string, then attach the mask without removing the icon or input target.
        if(filter=="All" || item.ItemType is not (2 or 6 or 7 or 10 or 11 or 13 or 14 or 15 or 16 or 18) ||
            (item.ItemType==18 ? filter=="Overrides" : EnbItemInformation.Matches(item,EnbItemInformation.Filters.IndexOf(filter))))return;
        Flush();
        var mask=_art["inventoryFilterMask"];
        Draw("inventoryFilterMask",mask.Design.Translate(new(slot.Center.X,slot.Center.Y)));
    }
}
