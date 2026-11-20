using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// Next Modern UI station map and click-to-walk: each room's floor, places and doors come
// from the rooms the station preload already loads; the map lays them out as one station.
internal sealed partial class EnbWorldController
{
    // One build per room type (the station has rooms 0 to 3).
    private readonly Task<EnbStationMapRoom?>?[] _stationMapBuilds=new Task<EnbStationMapRoom?>?[4];
    private string? _stationMapSignature;
    private int _stationMapRoomsReady;
    private Task<EnbStationMap?>? _stationMapCompose;

    /// <summary>The current room's floor and places, once built.</summary>
    private EnbStationMapRoom? StationMapRoom(uint room)
        => room<(uint)_stationMapBuilds.Length && _stationMapBuilds[room] is {IsCompletedSuccessfully:true} build ? build.Result : null;

    private void UpdateStationMap(EnbSceneStationDefinition? definition)
    {
        if(definition is null)return;
        if(_stationMapSignature!=_stationPreloadSignature)
        {
            _stationMapSignature=_stationPreloadSignature;
            Array.Clear(_stationMapBuilds);_stationMapRoomsReady=0;_stationMapCompose=null;
            _ui.Inventory.ModernStationMap=null;
        }
        foreach(var (type,load) in _stationRoomLoads)
            if(type<(uint)_stationMapBuilds.Length && _stationMapBuilds[type] is null && load.IsCompletedSuccessfully)
            {
                var interior=load.Result;
                if(_retailAssetsAccessor() is not {} assets)break;
                _stationMapBuilds[type]=Task.Run(()=>EnbWorldRenderer.BuildStationMapRoom(interior,definition,assets));
            }
        int ready=0;
        foreach(var build in _stationMapBuilds)if(build is {IsCompletedSuccessfully:true,Result:not null})ready++;
        if(ready!=_stationMapRoomsReady)
        {
            _stationMapRoomsReady=ready;
            EnbStationMapRoom[] rooms=[.._stationMapBuilds.Where(b=>b is {IsCompletedSuccessfully:true,Result:not null}).Select(b=>b!.Result!)];
            _stationMapCompose=Task.Run(()=>EnbWorldRenderer.ComposeStationMap(rooms));
        }
        if(_stationMapCompose is {IsCompletedSuccessfully:true} composed && !ReferenceEquals(composed.Result,_ui.Inventory.ModernStationMap) && composed.Result is not null)
            _ui.Inventory.ModernStationMap=composed.Result;
        _ui.Inventory.ModernStationMapSelf=_w3dWorld.StationAvatarPose is {} pose && !_stationRoomChanging ? (_stationRoom,new Vector2(pose.Position.X,pose.Position.Y)) : null;
    }
}
