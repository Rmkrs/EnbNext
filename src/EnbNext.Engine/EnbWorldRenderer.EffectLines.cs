using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(int,Vector3),Task<EnbRetailTractorLineAsset>> _lineAssets=[];
    private readonly EnbCurrentRetailLineBindingRuntime _lineBindings=new();
    private readonly Dictionary<(uint,ulong,ulong),int> _effectLineLevels=[];
    private readonly Dictionary<(uint,ulong,ulong),EnbRetailLineEffectTiling> _effectLineTiling=[];
    private readonly Dictionary<(uint,ulong,ulong),PreparedEffectLine> _preparedEffectLines=[];
    private sealed record PreparedEffectLine(EnbRetailTractorLineAsset Asset,IEnbRenderObject Source,Vector3 Start,Vector3 End,uint Age,float Scale)
    {
        public EnbDazzleCollisionRoot? CollisionRoot { get; init; }
    }

    private static EnbDazzleCollisionRoot LineDazzleCollision(EnbW3dWorldBounds bounds, EnbResolvedRenderCamera camera)
        // 006E5960/006E5B70 register/remove the line itself. 00941880 leaves
        // mask 1; configuration and 00968FD0 preserve it. 00969CA0 rejects
        // mask 16, but 0075DEE0 still admits its sphere before ray casting.
        => new(new(bounds.Center, bounds.Radius),
            EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera, bounds), false,
            static (_, _, fraction, inside) => (fraction, inside));
    private (int Pending,int Blocked,string? Failure) _effectLinePreparation;
    private string? RejectIncompatibleLineFactory(EnbEffectDefinition effect,EnbSceneEffectSnapshot snapshot,EnbRetailAssetBundle assets)
    {
        if(effect.BaseAssetId is not int id)return null;
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(id);
        if(definition.Kind==EnbBaseAssetDefinitionKind.ProceduralWidth)return null;
        try
        {
            //6E9490 constructs before StartDelay;6EAC50 calls6F51D0
            //before its class1C gate. The scheduler invokes this once before
            //installing any links or SFX, retaining ordinary BASE side effects.
            float authoredScale=1;
            if(effect.Properties.ContainsKey("RObjScale") && !EnbRetailIniDocument.TryReadFloat(effect.Properties,"RObjScale",out authoredScale))
                throw new InvalidDataException("Line authored scale unavailable");
            if(!EnbCurrentRetailEffectFactoryHsv.TryRead(effect.Properties,snapshot.HsvShift+snapshot.EffectOffset,out var hsv,out var error,applyPrototypeThreshold:false))
                throw new InvalidDataException(error);
            float scale=snapshot.Scale*authoredScale;
            if(definition.Kind==EnbBaseAssetDefinitionKind.Rsl && assets.BaseAssetResolver!.Resolve(id).Rsl is {} rsl)
                ConstructTransientRslObjectEffect(effect,rsl,scale,hsv,assets);
            else
            {
                var request=new EnbSceneObjectSnapshot(0,0,0,false,false,byte.MaxValue,id,scale,hsv.X,hsv.Y,hsv.Z,
                    0,false,null,null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
                var factory=CreateOrdinaryBaseFactory(request,assets,definition);
                if(factory.Root is null && factory.Diagnostic is {} diagnostic)return diagnostic;
            }
            return "LineEffect BASE is not class 1C";
        }
        catch(Exception error) when(error is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {return error.Message;}
    }
    private List<((uint,ulong,ulong) Key,EnbRenderDetailOwner Candidate)> PrepareEffectLines(
        IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,EnbRetailAssetBundle assets,IReadOnlyDictionary<ulong,float> radii,
        EnbResolvedRenderCamera camera,uint now,out float fixedCost)
    {
        var candidates=new List<((uint,ulong,ulong),EnbRenderDetailOwner)>();
        var used=new HashSet<(uint,ulong,ulong)>();
        _preparedEffectLines.Clear();fixedCost=0;
        int pending=0,blocked=0;string? failure=null;

        var required=new HashSet<(int,Vector3)>();
        foreach(var node in nodes.Where(n=>n.Definition.EffectClassKind==EnbEffectClassKind.LineEffect))
            if(node.Definition.BaseAssetId is int requiredId && EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var requiredHsv,out _,applyPrototypeThreshold:false))
                required.Add((requiredId,requiredHsv));
        foreach(var node in nodes.Where(n=>n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.LineEffect))
        {
            try
            {
                var d=node.Definition;
                float authoredScale=1;
                if(d.Properties.ContainsKey("RObjScale") && !EnbRetailIniDocument.TryReadFloat(d.Properties,"RObjScale",out authoredScale))
                    throw new InvalidDataException("Line authored scale unavailable");
                //006E961B multiplies packet and authored scale.006F5653 sends
                //it directly to procedural Width/Noise; no mesh prototype gate.
                float scale=node.Root.Scale*authoredScale;
                // Radius offsets precede growth/shrink and length-based tiling.
                if(d.BaseAssetId is not int id ||
                    !(node.HasUnboundEndpoints || node.Root.IsObjectToObject || node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint))throw new InvalidDataException("Unsupported LineEffect definition");
                if(!EnbCurrentRetailEffectFactoryHsv.TryRead(node,out var hsv,out var hsvError,applyPrototypeThreshold:false))
                    throw new InvalidDataException(hsvError);
                var assetKey=(id,hsv);
                if(!_lineAssets.TryGetValue(assetKey,out var load))
                {
                    EnbEffectAssetCacheAdmission.TrimForAdmission(_lineAssets,required,32);
                    _lineAssets[assetKey]=load=LoadVisualAsset(()=>EnbRetailTractorLineAsset.Load(assets,id,hsv));
                }
                if(!load.IsCompleted){pending++;continue;}
                //006EADD7 multiplies the cloned line's UV rate by factory speedup.
                var asset=load.GetAwaiter().GetResult();
                asset=asset with {Rate=asset.Rate*node.FactorySpeedup};

                var binding=_lineBindings.Resolve(node,out var bindingError);
                if(bindingError is not null)throw new InvalidDataException(bindingError);
                if(binding is null)continue;
                var source=binding.Source;var target=binding.Target;
                var start=binding.Start;var end=binding.End;
                EnbRetailLineEffectOffsets.Apply(d,scale,
                    radii.TryGetValue(source.RenderIdentity,out var sourceRadius)?sourceRadius:null,
                    target is not null && radii.TryGetValue(target.RenderIdentity,out var targetRadius)?targetRadius:null,ref start,ref end,
                    binding.SourceDetached,binding.TargetDetached);
                uint age=unchecked(now-node.ActualStartTick);
                EnbRetailLineEffectTiming.Apply(d,age,node.DurationMilliseconds,ref start,ref end,node.ActualStartTick);
                var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
                if(!_effectLineTiling.TryGetValue(key,out var tiling))
                    _effectLineTiling[key]=tiling=new(d,asset.Tile,asset.Subdivisions);
                tiling.Update(start,end);
                //006E59B0 stores length before006E6030 moves an endpoint.
                //Tiling consumes that original length; geometry consumes these points.
                EnbRetailLineCameraDisplacement.Apply(camera,asset.Width*scale,asset.Noise*scale,
                    binding.CameraSourceIsLocal,binding.CameraTargetIsLocal,ref start,ref end);
                asset=asset with {Tile=tiling.Tile,Subdivisions=tiling.Subdivisions};
                var bounds=EnbSegLineDetailBudgetSemantics.Bounds([start,end],asset.Width*scale,asset.Noise*scale,asset.Subdivisions);
                if(!EnbSegLineDetailBudgetSemantics.TryComputeProjectedDetail(camera,bounds,out float detail))throw new InvalidDataException("Line projected detail unavailable");
                used.Add(key);
                _effectLineLevels.TryAdd(key,EnbSegLineDetailBudgetSemantics.CurrentRetailInitialLevelOrdinal);
                if(asset.Subdivisions==0)fixedCost+=EnbSegLineDetailBudgetSemantics.FixedRenderCost(2);
                else candidates.Add((key,EnbSegLineDetailBudgetSemantics.CreateCandidate($"effect-line/{key}",2,(uint)asset.Subdivisions,_effectLineLevels[key],detail)));
                _preparedEffectLines[key]=new(asset,source,start,end,age,scale) { CollisionRoot=LineDazzleCollision(bounds,camera) };
            }
            catch(Exception ex){blocked++;failure??=ex.Message;}
        }
        foreach(var key in _effectLineLevels.Keys.Where(k=>!used.Contains(k)).ToArray())_effectLineLevels.Remove(key);
        foreach(var key in _effectLineTiling.Keys.Where(k=>!used.Contains(k)).ToArray())_effectLineTiling.Remove(key);
        _effectLinePreparation=(pending,blocked,failure);
        return candidates;
    }

    private void DrawEffectLines(List<EnbW3dDrawTask> packets,IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,
        LiveFrame frame,EnbRetailAssetBundle assets,IReadOnlyList<LiveRootInstance> roots)
    {
        int drawn=0,recovered=0;var (pending,blocked,failure)=_effectLinePreparation;
        foreach(var node in nodes.Where(n=>n.Active && n.Definition.EffectClassKind==EnbEffectClassKind.LineEffect))
        {
            var key=(node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence);
            // A worker finishing after allocation waits for the next frame.
            if(!_preparedEffectLines.TryGetValue(key,out var prepared))continue;
            try
            {
                var asset=prepared.Asset;var source=prepared.Source;
                if(asset.RecoveryDiagnostic is {} recovery){recovered++;failure??=recovery;}
                var packet=asset.Build(source.RenderIdentity,source.VisualGeneration,source.GameId,prepared.Start,prepared.End,
                    frame.Camera,prepared.Age,prepared.Scale,_effectLineLevels[key]);
                if(packet is not null){packets.Add((packet).ForkTask().SetRenderInstancePath($"effect-line/{node.Root.EffectId}/{node.Root.InstanceGeneration}/{node.Sequence}"));drawn++;}
            }
            catch(Exception ex){blocked++;failure??=ex.Message;}
        }
        _effectObjectVisualStatus+=$" · effect lines draws {drawn} pending {pending} blocked {blocked}"+
            (recovered==0?"":$" recovered {recovered}")+(failure is null?"":" · "+failure);
    }
}
