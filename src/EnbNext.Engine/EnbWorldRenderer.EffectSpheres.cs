using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<EffectHlodKey,EffectSphereOwner> _effectSphereOwners=[];

    private sealed class EffectSphereOwner(EnbRetailSphereAssembly asset,float factoryScale,float scale)
    {
        public EnbRetailSphereAssemblyRuntime Runtime {get;}=new(asset,factoryScale);
        public EnbRetailSphereAssembly Asset {get;}=asset;
        public EnbResolvedRenderCamera Camera {get;set;}
        public float Scale {get;}=scale;
        public Matrix4x4 World {get;set;}
        public uint Age {get;set;}
        public EnbCurrentRetailRslAnimationSequenceRuntime.Sample? Animation {get;set;}
        public Dictionary<int,int> Levels {get;}=asset.Spheres.ToDictionary(r=>r.Ordinal,_=>10);
        public EnbRetailSpherePreparedFrame? Before {get;set;}
    }

    private List<(EffectSphereOwner Owner,int Ordinal,EnbRenderDetailOwner Candidate)> PrepareEffectSpheres(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,IEnbSceneFrame snapshot,EnbRetailAssetBundle assets,
        IReadOnlyDictionary<ulong,float> radii,EnbResolvedRenderCamera camera,uint now)
    {
        // PrepareEffectRings establishes the common serial/delta immediately
        // before this call, including frames without any ring candidates.
        var result=new List<(EffectSphereOwner,int,EnbRenderDetailOwner)>();
        var used=new HashSet<EffectHlodKey>();
        foreach(var node in nodes.Where(n=>n.Active))
        {
            if(!node.TryGetRObjDefinition(out var definition,out _))continue;
            var load=LoadEffectObject(assets,definition.BaseAssetId,definition.HsvShift,node:node,currentClientTime:now);
            if(load?.IsCompletedSuccessfully!=true || load.Result.Spheres is not {} asset)continue;
            definition=load.Result.ResolveFactoryDefinition(definition);
            var owner=ResolveCombatResourceOwner(node,SceneObjects(snapshot));
            if(owner?.Spatial?.Transform is not {} spatial)continue;
            var key=EffectHlodKey.For(node,owner);used.Add(key);
            try
            {
                if(!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(spatial,out var world,out var error))throw new InvalidDataException(error);
                if(definition.ScaleToRadius && !radii.ContainsKey(owner.RenderIdentity))throw new InvalidDataException("Sphere source radius unavailable");
                if(!_effectSphereOwners.TryGetValue(key,out var state))
                    _effectSphereOwners[key]=state=new(asset,definition.AssetScale,
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
                if(_effectCulledRoots.Contains(key))continue;
                if(asset.Resource is null && before.Spheres.Count==1 && !AdmitEffectBounds(key,before.Spheres[0].Bounds,camera))continue;
                var candidates=before.Spheres.Select(r=>(state,r.Binding.Ordinal,r.Candidate($"sphere/{key}/{r.Binding.Ordinal}",camera,state.Levels[r.Binding.Ordinal]))).ToArray();
                result.AddRange(candidates);
            }
            catch(Exception ex){_effectHlodErrors[key]=ex.Message;}
        }
        foreach(var key in _effectSphereOwners.Keys.Where(k=>!used.Contains(k)).ToArray())_effectSphereOwners.Remove(key);
        return result;
    }

    private bool ApplyEffectSpheres(List<EnbW3dDrawTask> packets,EnbCurrentRetailEffectOwner node,IEnbRenderObject owner,
        uint now,EnbResolvedRenderCamera camera,ref int draws,int? forcedLevel)
    {
        // A worker completing after allocation waits until the next frame.
        if(!_effectSphereOwners.TryGetValue(EffectHlodKey.For(node,owner),out var state) || state.Before?.Serial!=_ringFrameSerial)return false;
        //0098DD50 dispatches +120 after allocation;0090D270 clamps and stores.
        if(forcedLevel is {} requested)
            foreach(int ordinal in state.Levels.Keys.ToArray())state.Levels[ordinal]=EnbSphereRingDetailBudgetSemantics.ClampSphereLevel(requested);
        var drawn=state.Levels.Where(p=>p.Value>0).Select(p=>p.Key).ToHashSet();
        var frame=state.Runtime.Prepare(_ringFrameSerial,state.Age,_ringFrameDelta,state.World,state.Scale,true,drawn,state.Animation);
        foreach(var sphere in frame.Spheres)
        {
            var sample=EnbRetailSphereGeometry.ForDetail(state.Levels[sphere.Binding.Ordinal]);
            if(sample is null)continue;
            var binding=sphere.Binding;
            packets.Add((EnbRetailSphereDrawPacket.Build(binding.Asset.Header,sphere.Values,sample,binding.Key,binding.Texture,sphere.DrawWorld(camera),true,now,
                owner.RenderIdentity,owner.VisualGeneration,owner.GameId,binding.Ordinal)).ForkTask().SetRenderInstancePath($"effect-sphere/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}/{binding.Ordinal}"));
            draws++;
        }
        return true;
    }
}


