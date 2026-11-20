using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record StationBoothPick(EnbSceneStationNpc Npc,EnbRetailMeshPick[] Meshes,bool InRange,System.Numerics.Vector3 Origin);
    private StationBoothPick[] _stationBoothPicks=[];
    private sealed class StationBoothAnimation(EnbW3dRenderResource resource)
    {
        public EnbW3dRenderResource Resource=resource;
        public EnbW3dRenderHierarchyAnimation? Animation;
        public string? Failure;
        public bool Pending;
        public readonly EnbCurrentRetailHierarchyAnimationState Clock=new()
            {Mode=2,HasHierarchy=true,HasAnimation=true};
        public EnbW3dAnimationSample? Sample(uint now)
        {
            if(Animation is null)return null;
            // 69F710 starts the admitted slot at zero on its first render callback.
            if(Pending){Pending=false;Clock.PreviousTime=now;Clock.CurrentFrame=0;Clock.Playback=2;}
            Clock.RefreshSingle(()=>now,()=>Animation.FrameRate,()=>checked((int)Animation.NumFrames),_=>{},()=>0,()=>0,(_,_,_)=>0);
            return new(Animation.Name,Clock.CurrentFrame);
        }
        public void Reset(){Pending=false;Clock.CurrentFrame=0;Clock.Playback=0;}
    }
    private readonly System.Collections.Concurrent.ConcurrentQueue<(uint Room,uint Slot,bool Open)> _stationBoothCommands=new();
    private readonly Dictionary<(uint Room,uint Slot),StationBoothAnimation> _stationBooths=[];
    private StationBoothAnimation? _stationBoothSlot;
    private string StationFixtureWarnings=>string.Join("",(_stationInterior?.Doorways.Select(d=>d.AnimationDiagnostic)??[])
        .Concat(_stationInterior?.Machinery.Select(m=>m.AnimationDiagnostic)??[])
        .Concat(new[]{_stationInterior?.ArrivalDiagnostic,_stationInterior?.DepartureDiagnostic})
        .Concat(_stationBooths.Where(b=>b.Key.Room==_stationInterior?.RoomType).Select(b=>b.Value.Failure)).Where(s=>s is not null).Distinct().Select(s=>" · fixture animation unavailable: "+s))
        +(_stationShadowDiagnostic is {} shadow?" · avatar shadow unavailable: "+shadow:"")
        +(_stationInterior?.AttachmentDiagnostic is {} attachments?" · "+attachments:"")
        +(_stationShipPickDiagnostic is {} pick?" · ship interaction unavailable: "+pick:"")
        +(_stationCameraCollisionDiagnostic is {} camera?" · camera collision: "+camera:"");
    private void QueueStationBooth(EnbStationInteractionRequest? owner,bool open)
    {
        if(owner is not null && _stationScene?.StationDefinition?.RoomNpcs(owner.Room).Any(n=>n.Slot==owner.Slot && n.Role==7 && n.Variant==0)==true)
            _stationBoothCommands.Enqueue((owner.Room,owner.Slot,open));
    }
    private void UpdateStationBooths(EnbCurrentRetailStationInterior room)
    {
        while(_stationBoothCommands.TryDequeue(out var command))
        {
            if(command.Room!=room.RoomType)continue;
            // 63B8CF/638E53 share slot zero; replacement first resets its old
            // object to the previous animation's frame zero (69F480).
            _stationBoothSlot?.Reset();_stationBoothSlot=null;
            if(!_stationBooths.TryGetValue((command.Room,command.Slot),out var booth))
                _stationBooths[(command.Room,command.Slot)]=booth=new(StationFixtureAsset("VB_BLACKMKT"));
            string animation="vb_blackmkt.vb_blackmkt-"+(command.Open?"1":"2");
            BindStationBoothAnimation(booth,animation);
        }
    }
    private void BindStationBoothAnimation(StationBoothAnimation booth,string animation)
    {
            var assets=_retailAssetsAccessor()!;
            try
            {
                if(EnbCurrentRetailRslHAnimResolver.TryResolve(booth.Resource,new("",EnbRslFileDependencyStatus.NotAuthored),
                    assets.Paths,assets.MixResources,assets.RetailMixResources!,animation,out var resolved,out var error))
                {
                    booth.Resource=resolved.DrawResource;booth.Animation=resolved.Animation;
                    booth.Failure=null;booth.Pending=true;_stationBoothSlot=booth;
                }
                else booth.Failure=error;
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            {booth.Failure=ex.Message;}
    }
    private void DetachStationBoothSlot(){_stationBoothSlot?.Reset();_stationBoothSlot=null;Volatile.Write(ref _stationBoothPicks,[]);}
    private void ResetStationBooths(){DetachStationBoothSlot();_stationBooths.Clear();}
}
