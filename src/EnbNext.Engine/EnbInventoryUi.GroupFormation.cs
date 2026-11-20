using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Presentation;

namespace EnbNext.Engine;
internal sealed partial class EnbInventoryUi
{
    private EnbGroupFormationVisual _groupFormationVisual=new();
    private EnbSceneGroupSnapshot? _groupFormationSource;
    private uint _groupFormationTime;
    private readonly HashSet<string> _missingFormationArt=[];
    private void RefreshGroupFormation(EnbSceneGroupSnapshot group,bool force=false)
    {
        // Station constructor builds once; 598370 returns immediately for +F1.
        if(StationGroup && _groupFormationSource is not null)return;
        if(!force && _groupFormationSource is {} previous && previous.AuxRevision==group.AuxRevision &&
            previous.Formation==group.Formation && previous.FormationName==group.FormationName &&
            previous.Position==group.Position && previous.AuxDataState==group.AuxDataState &&
            previous.Members.Select(m=>m.Position).SequenceEqual(group.Members.Select(m=>m.Position)))return;
        if(!StationGroup)_spaceGroupVisual.FormationAttention.ReplaceSource();
        _groupFormationVisual.Rebuild(group);_groupFormationSource=group;
        _groupFormationTime=_groupFrameClock.Time;
    }
    private string? FormationArt(string name)
    {
        string key="groupFormation/"+name;
        if(_art.ContainsKey(key))return key;
        if(_missingFormationArt.Contains(key) || _assets is not {} assets)return null;
        try
        {
            var asset=EnbAuthoredUiAsset.Load(assets,name+".w3d");
            if(asset is null || asset.Resource.Meshes.Count==0){_missingFormationArt.Add(key);return null;}
            var bounds=asset.Resource.Meshes.Select(m=>EnbCockpitUi.MeshBounds(asset,m.Name)).Aggregate((a,b)=>a.Union(b));
            _art[key]=new(asset,bounds.Translate(new(-320,-240)));return key;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { _missingFormationArt.Add(key);return null; }
    }
    private void DrawGroupFormation(long now,bool available)
    {
        uint tick=unchecked((uint)now);_groupFormationVisual.Advance(unchecked(tick-_groupFormationTime));_groupFormationTime=tick;
        _animatingText|=_groupFormationVisual.Animating;
        if(_groupFormationVisual.Hidden || _groupFormationVisual.Asset is not {} name || FormationArt(name) is not {} key)return;
        var anchor=_groupAnchors["group/FORMATION_BONE"];
        double depth=_groupDepths["group/FORMATION_BONE"];
        Rect Place(string art,Vector3 bone)
        {
            var r=Native(art).Translate(new(anchor.X-320+bone.X,anchor.Y-240-bone.Y));
            double perspective=320/(320-depth-bone.Z);
            return new(320+(r.X-320)*perspective,240+(r.Y-240)*perspective,r.Width*perspective,r.Height*perspective);
        }
        Draw(key,Place(key,Vector3.Zero),StationGroup?null:"group/Toggle",available && !StationGroup);
        ApplyGroupAttention(_spaceGroupVisual.FormationAttention,"group/Toggle",GroupFrameTime());
        var formationHighlight=_draws[^1].HighlightEmissive;
        bool formationHovered=_draws[^1].Hovered;
        _tips["group/Toggle"]=_groupTips?.GetValueOrDefault("FORMATION","")??"";
        var asset=_art[key].Asset;
        var transforms=EnbCockpitUi.ResolveTransforms(asset);
        foreach(var ship in _groupFormationVisual.Ships)
        {
            var pivot=asset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name.Equals("ship_bone"+ship.Position.ToString("00"),StringComparison.OrdinalIgnoreCase));
            if(pivot is null || FormationArt(ship.Asset) is not {} child)continue;
            var bone=Vector3.Transform(Vector3.Zero,transforms[pivot.Ordinal]);
            Draw(child,Place(child,bone));
            _draws[^1]=_draws[^1] with {EmissiveOverride=ship.Emissive,Hovered=formationHovered,HighlightEmissive=formationHighlight,SuppressHoverEvent=true};
        }
    }
}
