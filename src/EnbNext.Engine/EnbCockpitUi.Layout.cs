using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbCockpitUi
{
    internal EnbUiRect LayoutBounds(bool buffs)
    {
        var asset=buffs?_buffFrame:_asset;
        if(asset is null)return default;
        EnbUiRect result=default;bool found=false;
        foreach(var mesh in asset.Resource.Meshes)
        {
            if(mesh.Name.EndsWith("_UV",StringComparison.Ordinal)||mesh.Name is "LEFT" or "RIGHT")continue;
            var r=EnbAuthoredUiAsset.MeshBounds(asset,mesh.Name);
            if(!r.Intersects(new EnbUiRect(0,0,640,480)))continue;
            result=found?result.Union(r):r;found=true;
        }
        if(!buffs && AutopilotDraw is not null)result=result.Union(new(_autopilotBounds.X*640/_size.Width,_autopilotBounds.Y*480/_size.Height,_autopilotBounds.Width*640/_size.Width,_autopilotBounds.Height*480/_size.Height));
        if(!buffs && DamageMeterDraw is not null)result=result.Union(new(_damageMeterBounds.X*640/_size.Width,_damageMeterBounds.Y*480/_size.Height,_damageMeterBounds.Width*640/_size.Width,_damageMeterBounds.Height*480/_size.Height));
        return new(result.X*_size.Width/640,result.Y*_size.Height/480,result.Width*_size.Width/640,result.Height*_size.Height/480);
    }

    private EnbBuffDraw? _layoutBuffPreview;
    internal EnbBuffDraw? LayoutBuffPreview()
    {
        if(_layoutBuffPreview is not null)return _layoutBuffPreview;
        if(_buffFrame is null||_buffDefinitions is null||_assets is null)return null;
        var icons=System.Collections.Immutable.ImmutableArray.CreateBuilder<EnbBuffIcon>();
        foreach(var definition in _buffDefinitions.Definitions.OrderBy(p=>p.Key,StringComparer.Ordinal).Select(p=>p.Value))
        {
            EnbAuthoredUiAsset? asset;
            try{asset=EnbAuthoredUiAsset.Load(_assets,definition.Asset+".w3d");}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){continue;}
            if(asset is null)continue;
            for(int slot=0;slot<15;slot++)
            {
                var anchor=_buffFrame.Hierarchy.Pivots.FirstOrDefault(p=>p.Name=="BUFF"+slot.ToString("00"));
                if(anchor is not null)icons.Add(new(slot,asset,_buffAnchors[anchor.Ordinal],false));
            }
            break;
        }
        return _layoutBuffPreview=new(_buffFrame,icons.ToImmutable(),[]);
    }
}
