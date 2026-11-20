using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Globalization;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    //006A2DA0 table order;006A2ED0 retains zero-initialized baselines.
    private static readonly string[] EquipmentArrowFields=["MaxHullPoints","Defense","MissileDefense","Speed","WarpSpeed","TurnRate","ScanRange","Visibility","ResistImpact","ResistExplosive","ResistPlasma","ResistEnergy","ResistEMP","ResistChemical","ResistPsionic"];
    private readonly int[] _equipmentInitialStats=new int[15],_equipmentArrowDirections=new int[15];
    private bool _equipmentStatsInitialized;

    private void ResetEquipmentStatArrows()
    {
        Array.Clear(_equipmentInitialStats);Array.Clear(_equipmentArrowDirections);_equipmentStatsInitialized=false;
    }

    private void LoadEquipmentStatArrows()
    {
        ResetEquipmentStatArrows();
        for(int index=0;index<15;index++)
        {
            string key=$"equip/UD{index:00}";
            if(!_art.TryGetValue(key,out var art))continue;
            //00495FD0 replaces the texture at resource index zero wherever used.
            string? original=art.Asset.Resource.Meshes.SelectMany(m=>m.MaterialPasses).SelectMany(p=>p.TextureStages)
                .SelectMany(s=>s.Textures).FirstOrDefault(t=>t.SourceId==0)?.LogicalName;
            if(original is null)continue;
            foreach(string direction in new[]{"up","dn"})
            {
                EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding texture)=>
                    texture.LogicalName.Equals(original,StringComparison.OrdinalIgnoreCase)
                    ?texture with {LogicalName=$"if_check_{direction}.tga",Location=null,ResolveLogicalNameAtRuntime=true}:texture;
                _art[key+"/"+direction]=art with {Asset=art.Asset with {Resource=art.Asset.Resource.Copy().SetMeshes(art.Asset.Resource.Meshes.Select(m=>(m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p=>(p).Copy().SetTextureStages(p.TextureStages.Select(s=>(s).Copy().SetTextures(s.Textures.Select(Replace).ToArray())).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t=>t with {Textures=t.Textures.Select(b=>b is null?null:Replace(b)).ToArray()}).ToArray())).ToArray())).ToArray())}};
            }
        }
    }

    private void EquipmentStatArrows()
    {
        if(Data is not {} data||_scene?.Objects.Any(o=>o.IsLocalShip)!=true)return;
        bool Number(string key,out double value)=>double.TryParse(data.ShipStats.GetValueOrDefault(key,""),NumberStyles.Float,CultureInfo.InvariantCulture,out value);
        for(int i=0;i<EquipmentArrowFields.Length;i++)
        {
            string field=EquipmentArrowFields[i];
            //006A34E0 requires both AuxData values before mutating this field.
            if(Number(field,out double current)&&Number(i==0?"HullPoints":"Base."+field,out _))
            {
                int value=(int)current;
                if(!_equipmentStatsInitialized)_equipmentInitialStats[i]=value;
                _equipmentArrowDirections[i]=value.CompareTo(_equipmentInitialStats[i]);
            }
            string key=$"equip/UD{i:00}";
            if(_art.ContainsKey(key))Draw(key+(_equipmentArrowDirections[i]>0?"/up":_equipmentArrowDirections[i]<0?"/dn":""),Native(key));
        }
        //Native clears +C8 after the first update with a ship, including absent fields.
        _equipmentStatsInitialized=true;
    }
}
