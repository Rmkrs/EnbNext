using System.Collections.Immutable;
using EnbNext.Net7.World;

namespace EnbNext.Presentation;

/// <summary>Source-side owner of bindings and recursive sampling workspace.
/// Binding arrays change only on source membership mutation; a display frame samples
/// direct references. Numeric network samples are copied into leased owner-held buffers.</summary>
internal sealed class EnbPresentationFrameSampler
{
    private Net7WorldState? _world;
    private readonly List<EnbSceneFrame> _frames=[new(),new()];
    private int _nextFrame;
    internal int BufferCount=>_frames.Count;
    private int[] _poseSlots=[];
    private ulong? _membershipRevision;
    private Net7WorldObjectBinding[] _bindings=[];
    internal long BindingPreparations {get;private set;}
    public void Clear()
    {
        foreach(var binding in _bindings)binding.Dispose();
        _bindings=[];_membershipRevision=null;_poseSlots=[];_world=null;
        _frames.Clear();_frames.Add(new());_frames.Add(new());_nextFrame=0;
    }
    private void Bind(Net7WorldState world,EnbSceneSnapshot snapshot,EnbRenderSceneFeed feed)
    {
        if(!ReferenceEquals(_world,world))
        {Clear();_world=world;}
        if(_membershipRevision!=snapshot.SourceMembershipRevision)
        {
            foreach(var binding in _bindings)binding.Dispose();
            _membershipRevision=snapshot.SourceMembershipRevision;
            _bindings=new Net7WorldObjectBinding[snapshot.Objects.Length];
            _poseSlots=new int[_bindings.Length];
            // Diagnostic/HUD snapshot membership is independent of the renderer.
            // Resolve its pose slots only when that membership changes.
            var sources=world.Objects.Snapshot();
            for(int i=0;i<_bindings.Length;i++)
            {_bindings[i]=world.Objects.BindFirst(snapshot.Objects[i].GameId);_poseSlots[i]=feed.SlotFor(sources[i]);}
            BindingPreparations++;
        }
    }
    internal EnbSceneSnapshot BindPublicationSlots(Net7WorldState world,EnbSceneSnapshot snapshot,EnbRenderSceneFeed feed)
    {
        if(snapshot.IsTransitioning)return snapshot;
        Bind(world,snapshot,feed);
        var objects=ImmutableArray.CreateBuilder<EnbSceneObjectSnapshot>(snapshot.Objects.Length);
        for(int i=0;i<snapshot.Objects.Length;i++)
        {
            var obj=snapshot.Objects[i];
            objects.Add(obj.RenderPoseSlot==_poseSlots[i]?obj:obj with {RenderPoseSlot=_poseSlots[i]});
        }
        return snapshot with {Objects=objects.MoveToImmutable()};
    }
    // A returned frame owns one lease. The consumer must release it after
    // replacing every queued/retained invocation. Two buffers cover the normal
    // handoff; independently retained diagnostic/nested views may extend the pool.
    public EnbSceneFrame Acquire(Net7WorldState world,EnbSceneSnapshot snapshot,uint time,int? presentationMode,EnbRenderSceneFeed feed)
    {
        if(!snapshot.IsTransitioning)Bind(world,snapshot,feed);
        EnbSceneFrame? frame=null;
        for(int attempt=0;attempt<_frames.Count;attempt++)
        {
            int index=(_nextFrame+attempt)%_frames.Count;
            if(_frames[index].IsRetained)continue;
            frame=_frames[index];_nextFrame=(index+1)%_frames.Count;break;
        }
        if(frame is null){frame=new();_frames.Add(frame);}
        frame.Prepare(snapshot);
        if(snapshot.IsTransitioning){frame.RenderPoses=snapshot.RenderPoses;return frame.Retain();}
        var local=world.LocalShipObjectId is uint id?world.Objects.FindFirst(id):null;
        uint? formationParent=local?.Spatial?.Formation?.LeaderId;
        var renderPoses=feed.SamplePoses(world,time);
        for(int i=0;i<snapshot.Objects.Length;i++)
        {
            var owner=frame.SampleObject(i);var pose=renderPoses[_poseSlots[i]];
            owner.Sample(pose,_poseSlots[i]);
            if(owner.Spatial is not null && pose.HasSample && owner.GameId==formationParent && _bindings[i].Target?.Spatial is {} liveSpatial)
                owner.SetFormationAudit(liveSpatial.DescribeModel(time),liveSpatial.ClientTimestamp,liveSpatial.ReceiptTime);
        }
        frame.ClientTime=time;frame.RenderPoses=renderPoses;
        frame.SampleCockpit(local,time,presentationMode);
        return frame.Retain();
    }

}
