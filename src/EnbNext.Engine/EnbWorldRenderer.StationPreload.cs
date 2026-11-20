using System.Collections.Concurrent;
using System.Diagnostics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record PreparedStationRoom(EnbCurrentRetailStationInterior Room,(string? Fixture,int Index,LiveLanePlan Lane)[] Lanes,CancellationToken Token);
    private readonly ConcurrentQueue<PreparedStationRoom> _stationPreparedRooms=new();
    private readonly Dictionary<EnbW3dRenderResource,List<(LiveLanePlan Lane,LiveGpuResource Gpu)>> _stationResidentRooms=new(ReferenceEqualityComparer.Instance);
    private PreparedStationRoom? _stationUploadingRoom;
    private int _stationUploadIndex;

    // CPU-only preparation. Archive indices are immutable and texture resolution
    // uses its concurrent cache. All GPU creation remains on the GL thread.
    public void PrepareStationRoom(EnbCurrentRetailStationInterior room,EnbRetailAssetBundle assets,CancellationToken token,EnbNext.Presentation.EnbSceneStationDefinition definition)
    {
        var camera=room.Camera(800,600);
        if(!EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld,room.BuildDraws(0).Packets.Where(p=>p.SpecialMaterial is null).ToArray(),out var order,out var error))
            throw new InvalidDataException(error);
        var lanes=new List<(string? Fixture,int Index,LiveLanePlan Lane)>();
        void Compile(IEnumerable<EnbW3dDrawTask> packets,string? fixture)
        {
            int index=0;
            foreach(var packet in packets)
            {
                token.ThrowIfCancellationRequested();
                if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out error) ||
                    !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
                        assets.Textures,new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out var lane,out error))
                    throw new InvalidDataException(error);
                lanes.Add((fixture,index++,lane));
            }
        }
        Compile(order.Packets,null);
        // Lanes in the order RenderStationFixtures submits them (its static-sort order, special materials apart).
        foreach(var door in room.Doorways)Compile(StationStaticSortOrder(EnbCurrentRetailStationInterior.BuildModelDraws(door.Resource,door.Transform,0,
            door.Animation is {} animation?new(animation.Name,0):null).Packets.Where(p=>p.SpecialMaterial is null).ToArray(),null),door.Pivot);
        foreach(var fixture in StationFixturePlacements(room,definition))
        {
            EnbW3dRenderResource resource;
            try{resource=LoadStationFixtureAsset(fixture.Name,assets);}
            catch(InvalidDataException) when(fixture.Key.EndsWith("/sign",StringComparison.Ordinal)){continue;} // An unavailable sign is skipped.
            Compile(StationStaticSortOrder(EnbCurrentRetailStationInterior.BuildModelDraws(resource,fixture.World,0).Packets
                .Where(p=>p.SpecialMaterial is null).ToArray(),null),fixture.Key);
        }
        token.ThrowIfCancellationRequested();
        _stationPreparedRooms.Enqueue(new(room,lanes.ToArray(),token));
    }

    private void WarmStationGpu(EnbGlApi gl)
    {
        long started=Stopwatch.GetTimestamp();
        do
        {
            if(_stationUploadingRoom is null)
            {if(!_stationPreparedRooms.TryDequeue(out _stationUploadingRoom))return;_stationUploadIndex=0;}
            var next=_stationUploadingRoom;
            if(next.Token.IsCancellationRequested || ReferenceEquals(next.Room.Resource,_stationGpuAsset?.Resource))
            { _stationUploadingRoom=null;continue; }
            if(_stationUploadIndex>=next.Lanes.Length){_stationUploadingRoom=null;continue;}
            var item=next.Lanes[_stationUploadIndex++];
            List<(LiveLanePlan Lane,LiveGpuResource Gpu)>? draws;
            if(item.Fixture is {} fixture)
            {
                if(!_stationResidentFixtures.TryGetValue(next.Room.Resource,out var fixtures))_stationResidentFixtures[next.Room.Resource]=fixtures=[];
                if(!fixtures.TryGetValue(fixture,out draws))fixtures[fixture]=draws=[];
            }
            else if(!_stationResidentRooms.TryGetValue(next.Room.Resource,out draws))_stationResidentRooms[next.Room.Resource]=draws=[];
            if(draws.Count>item.Index)continue;
            if(!TryCreateGpuResource(gl,item.Lane,LiveLaneKind.Textured,0,out var gpu,out _))
            { _stationUploadingRoom=null;return; } // Active loading retains its strict error path.
            draws.Add((item.Lane,gpu));
        } while(Stopwatch.GetElapsedTime(started).TotalMilliseconds<2);
    }
}
