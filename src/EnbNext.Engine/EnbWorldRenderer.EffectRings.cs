using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<EffectHlodKey,EffectRingOwner> _effectRingOwners=[];
    private ulong _ringFrameSerial;
    private uint? _ringPreviousTime;
    private uint _ringFrameDelta;

    private sealed class EffectRingOwner(EnbRetailRingAssembly asset,float factoryScale,float scale)
    {
        public EnbRetailRingAssemblyRuntime Runtime {get;}=new(asset,factoryScale);
        public EnbRetailRingAssembly Asset {get;}=asset;
        public EnbResolvedRenderCamera Camera {get;set;}
        public float Scale {get;}=scale;
        public Matrix4x4 World {get;set;}
        public uint Age {get;set;}
        public EnbCurrentRetailRslAnimationSequenceRuntime.Sample? Animation {get;set;}
        public Dictionary<int,int> Levels {get;}=asset.Rings.ToDictionary(r=>r.Ordinal,_=>20);
        public EnbRetailRingPreparedFrame? Before {get;set;}
    }

    private List<(EffectRingOwner Owner,int Ordinal,EnbRenderDetailOwner Candidate)> PrepareEffectRings(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,IEnbSceneFrame snapshot,EnbRetailAssetBundle assets,
        IReadOnlyDictionary<ulong,float> radii,EnbResolvedRenderCamera camera,uint now)
    {
        _ringFrameSerial++;
        _ringFrameDelta=_ringPreviousTime is {} previous ? unchecked(now-previous) : 0;
        _ringPreviousTime=now;
        var result=new List<(EffectRingOwner,int,EnbRenderDetailOwner)>();
        var used=new HashSet<EffectHlodKey>();
        foreach(var node in nodes.Where(n=>n.Active))
        {
            if(!node.TryGetRObjDefinition(out var definition,out _))continue;
            var load=LoadEffectObject(assets,definition.BaseAssetId,definition.HsvShift,node:node,currentClientTime:now);
            if(load?.IsCompletedSuccessfully!=true || load.Result.Rings is not {} asset)continue;
            definition=load.Result.ResolveFactoryDefinition(definition);
            var owner=ResolveCombatResourceOwner(node,SceneObjects(snapshot));
            if(owner?.Spatial?.Transform is not {} spatial)continue;
            var key=EffectHlodKey.For(node,owner);used.Add(key);
            try
            {
                if(!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out var world,out var error))throw new InvalidDataException(error);
                if(definition.ScaleToRadius && !radii.ContainsKey(owner.RenderIdentity))throw new InvalidDataException("Ring source radius unavailable");
                if(!_effectRingOwners.TryGetValue(key,out var state))
                    _effectRingOwners[key]=state=new(asset,definition.AssetScale,
                        ResolveCombatRenderScale(node,definition with {AssetScale=1},radii.GetValueOrDefault(owner.RenderIdentity)));
                state.Before=null;
                state.Camera=camera;
                state.World=PlaceCombatEffect(node,definition,ResolveCombatEffectPose(node,world),camera,radii.GetValueOrDefault(owner.RenderIdentity));
                state.Age=unchecked(now-node.ActualStartTick);
                _effectSequences.TryGetValue(key,out var sequence);
                if(load.Result.Sequence is not null && sequence is null)continue;
                state.Animation=sequence?.Current;
                var before=state.Runtime.BeforeAllocation(_ringFrameSerial,state.Age,state.World,state.Scale,true,state.Animation);
                state.Before=before;
                //965F40 admits the scene root, not each attached primitive.
                //Frustum rejection does not toggle +174 or restart playback.
                if(_effectCulledRoots.Contains(key))continue;
                if(asset.Resource is null && before.Rings.Count==1 && !AdmitEffectBounds(key,before.Rings[0].Bounds,camera))continue;
                var candidates=before.Rings.Select(r=>(state,r.Binding.Ordinal,r.Candidate($"ring/{key}/{r.Binding.Ordinal}",camera,state.Levels[r.Binding.Ordinal]))).ToArray();
                result.AddRange(candidates);
            }
            catch(Exception ex){_effectHlodErrors[key]=ex.Message;}
        }
        foreach(var key in _effectRingOwners.Keys.Where(k=>!used.Contains(k)).ToArray())_effectRingOwners.Remove(key);
        return result;
    }

    private bool ApplyEffectRings(List<EnbW3dDrawTask> packets,EnbCurrentRetailEffectOwner node,IEnbRenderObject owner,
        uint now,EnbResolvedRenderCamera camera,ref int draws,int? forcedLevel)
    {
        // A worker completing after allocation waits until the next frame.
        if(!_effectRingOwners.TryGetValue(EffectHlodKey.For(node,owner),out var state) || state.Before?.Serial!=_ringFrameSerial)return false;
        //0098DD50 dispatches +120 after allocation;009123B0 clamps and stores.
        if(forcedLevel is {} requested)
            foreach(int ordinal in state.Levels.Keys.ToArray())state.Levels[ordinal]=EnbSphereRingDetailBudgetSemantics.ClampRingLevel(requested);
        var drawn=state.Levels.Where(p=>p.Value>0).Select(p=>p.Key).ToHashSet();
        var frame=state.Runtime.Prepare(_ringFrameSerial,state.Age,_ringFrameDelta,state.World,state.Scale,true,drawn,state.Animation);
        foreach(var ring in frame.Rings)
        {
            var sample=ring.Values.Sample(state.Levels[ring.Binding.Ordinal]);
            if(sample is null)continue;
            var binding=ring.Binding;
            packets.Add((EnbRetailRingDrawPacket.Build(binding.Asset,sample,binding.Key,binding.Texture,ring.DrawWorld(camera),true,now,
                owner.RenderIdentity,owner.VisualGeneration,owner.GameId,binding.Ordinal)).ForkTask().SetRenderInstancePath($"effect-ring/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}/{binding.Ordinal}"));
            draws++;
        }
        return true;
    }
}
