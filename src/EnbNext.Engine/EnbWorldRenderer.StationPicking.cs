using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(EnbW3dRenderMesh Mesh,float Scale),EnbRetailDazzleMeshRay> _stationPickMeshRays=[];
    private EnbRetailDazzleMeshRay StationPickMesh(EnbW3dRenderMesh mesh,float scale=1)
    {
        var key=(mesh,scale);
        //940470 sets normal render-hidden (+184).940530 checks the distinct
        //hierarchy-hidden bit (+188); animation-hidden packets are absent.
        if(!_stationPickMeshRays.TryGetValue(key,out var ray)||!ray.IsCurrentGeometry)
            _stationPickMeshRays[key]=ray=new(mesh,scale,hidden:false,queryMask:1);
        return ray;
    }
    private sealed record StationInteractionHit(bool InRange, bool Ship=false,
        EnbSceneStationNpc? Npc=null, StationPlayerPick? Player=null,
        EnbSceneStationFacility? Facility=null, uint? Door=null);
    private sealed record StationInteractionCandidate((int Kind,uint Id) Key,Vector3 Origin,
        EnbRetailMeshPick[] Meshes,StationInteractionHit Hit);
    private StationInteractionCandidate[] _stationInteractionPicks=[];
    private readonly Dictionary<(int Kind,uint Id),int> _stationInteractionPriorities=[];
    private long _stationInteractionSortTick;

    private void PublishStationInteractionPicks()
    {
        var candidates=new List<StationInteractionCandidate>();
        // Binding insertion order remains the tie-breaker (687D30/6871D0).
        foreach(var p in Volatile.Read(ref _stationPlayerPicks))
            candidates.Add(new((0,p.Id),p.Origin,p.Meshes,new(p.InRange,Player:p)));
        if(_stationInterior is {RoomType:0} room && _stationShipRenderer is {} ship)
        {
            bool proxy=room.HangarAttachments is not null;
            var origin=proxy && _stationShipPickSource.Length>0?_stationShipPickSource[0].WorldTransform.Translation:
                room.PivotTransform("SHIP",_stationElapsedMilliseconds).Translation;
            candidates.Add(new((1,0),origin,proxy?Volatile.Read(ref _stationShipPickProxy):
                Volatile.Read(ref ship._stationShipGeometryPicks),
                new(_stationMovement is {} avatar && Vector3.Distance(avatar.Position,origin)<106,Ship:true)));
        }
        foreach(var p in Volatile.Read(ref _stationDoorPicks))
            candidates.Add(new((2,p.Destination),p.Origin,p.Meshes,new(p.InRange,Door:p.Destination)));
        foreach(var p in Volatile.Read(ref _stationTerminalPicks))
            candidates.Add(new((3,p.Facility.Slot),p.Origin,p.Meshes,new(p.InRange,Facility:p.Facility)));
        foreach(var p in Volatile.Read(ref _stationNpcPicks))
            candidates.Add(new((4,p.Npc.Slot),p.Origin,p.Meshes,new(p.InRange,Npc:p.Npc)));
        // 63FF80 refreshes after the half-second timer expires. 641FA0
        // detaches, writes trunc(distance) clamped to 1..999, and reattaches;
        // 661700 inserts ascending, retaining binding order for equal keys.
        if(_stationElapsedMilliseconds-_stationInteractionSortTick>500 && _stationMovement is {} movement)
        {
            _stationInteractionSortTick=_stationElapsedMilliseconds;
            _stationInteractionPriorities.Clear();
            foreach(var candidate in candidates)
                _stationInteractionPriorities[candidate.Key]=Math.Clamp((int)Vector3.Distance(movement.Position,candidate.Origin),1,999);
        }
        Volatile.Write(ref _stationInteractionPicks,candidates.OrderBy(p=>_stationInteractionPriorities.GetValueOrDefault(p.Key)).ToArray());
    }

    private StationInteractionHit? PickStationInteraction(double x,double y)
    {
        if(!StationArrivalComplete || _stationDepartureStarted is not null)return null;
        // Black-market booths use the ordinary group (6348C5); the other
        // station bindings use LastSortGroup (660070/661BB0).
        foreach(var p in Volatile.Read(ref _stationBoothPicks))
            if(p.Meshes.Any(m=>m.Contains(x,y)))return new(p.InRange,Npc:p.Npc);
        // 65F780's geometric hover owner is selected before proximity gating.
        foreach(var p in Volatile.Read(ref _stationInteractionPicks))
            if(p.Meshes.Any(m=>m.Contains(x,y)))return p.Hit;
        return null;
    }
}
