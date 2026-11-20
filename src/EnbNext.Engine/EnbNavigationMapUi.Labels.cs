using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbNavigationMapUi
{
    private readonly HashSet<ulong> _radarLabelOwners=[];
    private readonly Dictionary<ulong,bool> _radarLabelGroups=[];
    private readonly Dictionary<ulong,bool> _mapLabelUnknown=[];
    private readonly Dictionary<ulong,(ulong Relationship,ulong Pvp)> _labelNotifications=[];
    private readonly Dictionary<ulong,string> _normalLabelStyles=[];
    private readonly Dictionary<ulong,string> _normalLabelInitialNames=[];
    private HashSet<ulong> _gutterLabelOwners=[];

    public void SetRadarLabelContacts(EnbRadarFrame frame)
        => _gutterLabelOwners=frame.Contacts.Select(c=>c.RenderIdentity).ToHashSet();

    public void NotifyGroupLabelChange(uint id,bool group)
    {
        if(_scene?.Objects.FirstOrDefault(o=>o.GameId==id) is {} obj && _radarLabelOwners.Contains(obj.RenderIdentity))
        {
            _radarLabelGroups[obj.RenderIdentity]=group;_labelsInvalidated=true;
        }
    }

    // Next Modern UI: every player is on the map; names are chosen per kind in its side panel.
    private bool MapAdmits(IEnbRenderObject obj)
        => obj.IsLocalShip || obj.Radar.Navigation is not null || obj.ObjectType==1 && (_modern ||
            _state!.ShowsLabel(false,_radarLabelGroups.GetValueOrDefault(obj.RenderIdentity),true));

    private void ResetLabelOwners()
    {
        _radarLabelOwners.Clear();_radarLabelGroups.Clear();_mapLabelUnknown.Clear();_labelNotifications.Clear();
        _normalLabelStyles.Clear();_normalLabelInitialNames.Clear();_rescueLabelAlpha.Clear();_labelsInvalidated=true;
    }

    private void ObserveLabelOwners()
    {
        if(_scene is null || _state is null || _scene.ActiveStarbaseId is not null)return;
        var live=_scene.Objects.Select(o=>o.RenderIdentity).ToHashSet();
        if(_radarLabelOwners.RemoveWhere(id=>!live.Contains(id))!=0)_labelsInvalidated=true;
        foreach(var id in _radarLabelGroups.Keys.Where(id=>!live.Contains(id)).ToArray())_radarLabelGroups.Remove(id);
        foreach(var id in _mapLabelUnknown.Keys.Where(id=>!live.Contains(id)).ToArray())_mapLabelUnknown.Remove(id);
        foreach(var id in _labelNotifications.Keys.Where(id=>!live.Contains(id)).ToArray())_labelNotifications.Remove(id);
        bool localPvp=false, refreshed=false;
        uint? selected=_scene.Hud.HasLocalTargetSelection?_scene.Hud.LocalSelectedTargetId:_scene.Hud.CurrentTargetId;
        foreach(var obj in _scene.Objects)
        {
            var prior=_labelNotifications.GetValueOrDefault(obj.RenderIdentity);
            bool pvp=obj.PvpUpdateRevision!=prior.Pvp && obj.PvpUpdated;
            if(obj.IsLocalShip)localPvp=pvp;
            // SignalMap registration belongs to the retained radar-data owner (7BB1D0).
            if(_radarLabelOwners.Contains(obj.RenderIdentity) && obj.RelationshipNotificationRevision!=prior.Relationship)
                _labelsInvalidated=true;
            _labelNotifications[obj.RenderIdentity]=(obj.RelationshipNotificationRevision,obj.PvpUpdateRevision);
            if(obj.Spatial?.Transform is not {} pose || !EnbNavigationMapProjection.Finite(pose.Position))continue;
            void CreateRadarOwner()
            {
                if(!_radarLabelOwners.Add(obj.RenderIdentity))return;
                _radarLabelGroups[obj.RenderIdentity]=obj.ObjectType==1 && _scene.Hud.GroupMemberIds.Contains(obj.GameId);
                _labelsInvalidated=true;
            }
            if(obj.ObjectType==1)CreateRadarOwner();
            bool map=MapAdmits(obj), drawMap=IsOpen && !_galaxy && map;
            bool gutter=_gutterLabelOwners.Contains(obj.RenderIdentity) || obj.GameId==selected;
            // 7BDC70 asks for player render data even when its map label mode excludes it.
            if(drawMap || gutter)CreateRadarOwner();
            if(map && _mapLabelUnknown.TryAdd(obj.RenderIdentity,false))_labelsInvalidated=true; // 7BE640
            if(drawMap && obj.Radar.Navigation is {} nav && _mapLabelUnknown[obj.RenderIdentity]==nav.PlayerHasVisited)
            {
                _mapLabelUnknown[obj.RenderIdentity]=!nav.PlayerHasVisited;_labelsInvalidated=true; // 7BE970
            }
            if(drawMap || gutter)
            {
                refreshed=true;
                if(pvp)_labelsInvalidated=true; // 74D8B0 tests property state >1, not its Boolean value.
            }
        }
        if(refreshed && localPvp)_labelsInvalidated=true;
    }

    private void RefreshNormalLabels()
    {
        ObserveLabelOwners();
        int mode=_modern ? (ModernMapNames==0 ? 0 : 100+ModernMapNames) : _state!.LabelMode;
        bool rebuild=_labelsInvalidated || _labelMode!=mode ||
            _labelWorldRegistryRevision!=_scene!.WorldRegistryRevision || _normalLabelStyles.Count==0;
        _labelMode=mode;_labelWorldRegistryRevision=_scene!.WorldRegistryRevision;
        if(_labelMode==0)
        {
            _normalLabelStyles.Clear();_normalLabelInitialNames.Clear();_rescueLabelAlpha.Clear();return;
        }
        if(rebuild)
        {
            _labelsInvalidated=false;_normalLabelStyles.Clear();_normalLabelInitialNames.Clear();_rescueLabelAlpha.Clear();
            foreach(var obj in _scene.Objects)
            {
                bool group=obj.ObjectType==1 && _scene.Hud.GroupMemberIds.Contains(obj.GameId);
                if(!_mapLabelUnknown.TryGetValue(obj.RenderIdentity,out bool unknown) || unknown ||
                    !(_modern ? ModernShowsName(obj) : _state!.ShowsLabel(obj.IsLocalShip,group,obj.ObjectType==1)) ||
                    string.IsNullOrEmpty(EnbCurrentRetailRadarCompatibilityAdapter.ResolveContactName(obj)))continue;
                string style=obj.ObjectType==1 && obj.IsRescueBeaconActive ? "RADAR_JUMPSTART" :
                    group ? "RADAR_GROUP" : obj.ObjectType==1 ? "RADAR_PLAYER" : "RADAR_OBJECT";
                _normalLabelStyles[obj.RenderIdentity]=style;
                _normalLabelInitialNames[obj.RenderIdentity]=EnbCurrentRetailRadarCompatibilityAdapter.ResolveContactName(obj)!;
                if(style=="RADAR_JUMPSTART")_rescueLabelAlpha[obj.RenderIdentity]=1;
            }
        }
        // 7C2850's retained branch updates text/position but does not reselect category.
        foreach(var id in _rescueLabelAlpha.Keys.ToArray())
            _rescueLabelAlpha[id]=EnbNext.RetailMath.EnbRetailTextPulse.Advance(_rescueLabelAlpha[id],_labelElapsed);
    }
}
