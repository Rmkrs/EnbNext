using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Binary observer006E7ED0 clears endpoint pointers, retaining their
/// captured coordinates. Lifetime decisions remain with the effect scheduler.</summary>
public sealed class EnbCurrentRetailLineBindingRuntime
{
    public sealed record Binding(IEnbRenderObject Source,IEnbRenderObject? Target,
        Vector3 Start,Vector3 End,bool SourceDetached=false,bool TargetDetached=false,
        bool UnaryStartToTarget=false,bool UnaryNextToTarget=false,bool CapturedAtActivation=false,
        bool CameraSourceIsLocal=false,bool CameraTargetIsLocal=false);
    private readonly Dictionary<(uint,ulong,ulong),Binding> _bindings=[];
    private readonly Dictionary<(uint,ulong,ulong),string> _errors=[];
    public int Count=>_bindings.Count;
    public void Clear(){_bindings.Clear();_errors.Clear();}
    public Vector3? SoundPosition(EnbCurrentRetailEffectSoundNode node,Vector3 listener)
        => !_errors.ContainsKey((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence)) &&
            _bindings.TryGetValue((node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence),out var binding)
            ? EnbCurrentRetailEffectSoundRuntime.ClosestLinePosition(binding.Start,binding.End,listener) : null;
    private static (uint,ulong,ulong) Key(EnbCurrentRetailEffectOwner n)=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence);

    public Binding? Resolve(EnbCurrentRetailEffectOwner node,out string? error)
    {
        error=_errors.GetValueOrDefault(Key(node));
        return error is null?_bindings.GetValueOrDefault(Key(node)):null;
    }

    public Binding? ResolveParent(EnbCurrentRetailEffectOwner node)
        => node.ParentSequence is {} parent ? _bindings.GetValueOrDefault((node.Root.EffectId,node.Root.InstanceGeneration,parent)) : null;

    public void Update(IReadOnlyList<EnbCurrentRetailEffectOwner> nodes,IReadOnlyList<IEnbRenderObject> objects,
        Func<IEnbRenderObject,string,Matrix4x4> sourcePose,
        Func<EnbCurrentRetailEffectOwner,Binding?>? externalParent=null,
        Func<IEnbRenderObject,float>? targetRadius=null,
        Func<EnbCurrentRetailEffectOwner,bool>? updateNode=null)
    {
        // Ended beam parents leave the visual list before their impact is
        // placed. Keep the parent's endpoint record while a child references it.
        var live=nodes.Where(n=>n.ParentSequence.HasValue)
            .Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.ParentSequence!.Value)).ToHashSet();
        // Observer callbacks also run on the frame a beam ends, when only its
        // child remains in the visual list. They do not refresh coordinates.
        foreach(var key in _bindings.Keys.ToArray())
        {
            var saved=_bindings[key];
            _bindings[key]=saved with {
                SourceDetached=saved.SourceDetached || !objects.Any(o=>o.GameId==saved.Source.GameId && o.RenderIdentity==saved.Source.RenderIdentity),
                TargetDetached=saved.TargetDetached || saved.Target is null || !objects.Any(o=>o.GameId==saved.Target.GameId && o.RenderIdentity==saved.Target.RenderIdentity)};
        }
        foreach(var node in nodes.Where(n=>n.Definition.EffectClassKind==EnbEffectClassKind.LineEffect &&
            (n.HasUnboundEndpoints || n.Root.IsObjectToObject || n.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint)))
        {
            var key=Key(node);live.Add(key);
            if(updateNode is not null && !updateNode(node))continue;
            _errors.Remove(key);
            var source=objects.FirstOrDefault(o=>o.GameId==node.Root.SourceGameId);
            var target=objects.FirstOrDefault(o=>o.GameId==node.Root.TargetGameId);
            _bindings.TryGetValue(key,out var binding);
            try
            {
                if(node.HasUnboundEndpoints)
                {
                    if(binding is null)
                    {
                        var parent=ResolveParent(node) ?? externalParent?.Invoke(node);
                        binding=new(node.CreateUnboundResourceOwner(),null,parent?.Start??Vector3.Zero,parent?.End??Vector3.Zero,
                            SourceDetached:true,TargetDetached:true,CapturedAtActivation:node.Active);
                    }
                    _bindings[key]=binding;
                    continue;
                }
                bool pointTarget=node.Root.GeometryKind==EnbSceneEffectGeometryKind.ObjectToPoint;
                if(pointTarget && (!float.IsFinite(node.Root.WorldPoint.X) || !float.IsFinite(node.Root.WorldPoint.Y) || !float.IsFinite(node.Root.WorldPoint.Z)))
                    throw new InvalidDataException("Line world endpoint is non-finite");
                if(binding is null)
                {
                    var parent=ResolveParent(node) ?? externalParent?.Invoke(node);
                    bool sourceDetached=parent?.SourceDetached==true;
                    bool targetDetached=pointTarget || parent?.TargetDetached==true;
                    // A paused renderer may first observe this root after endpoint
                    // removal. Packet-boundary receipts carry the coordinates that
                    // 006E7ED0 retains; never bind a replacement with the same ID.
                    if(parent is null && node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && !pointTarget &&
                        node.Root.PublishedEndpoints is {} published)
                    {
                        var captured=node.Root.LastSourceObject ?? published.Source;
                        if(source is null || source.RenderIdentity!=captured.RenderIdentity)
                        {source=captured;sourceDetached=true;}
                        if(target is null || target.RenderIdentity!=published.Target.RenderIdentity)
                        {target=published.Target;targetDetached=true;}
                    }
                    if(pointTarget && !sourceDetached)
                    {
                        if(node.Root.EndpointReceipt is {SourceCreationOrdinal:null})continue;
                        if(node.Root.LastSourceObject is {} captured && (source is null || source.RenderIdentity!=captured.RenderIdentity))
                        {source=captured;sourceDetached=true;}
                    }
                    // 006E8B60 passes a surviving endpoint's GameID, but after
                    // observer loss uses the parent's coordinates via +30/+38.
                    // Resource snapshots retain identity only in that branch.
                    if(sourceDetached && parent is not null)source=parent.Source;
                    if(targetDetached && parent is not null)target=parent.Target;
                    if(source?.Spatial?.Transform is null || !pointTarget && target?.Spatial?.Transform is null)continue;
                    // A packet-time observation must not bind a reused GameID.
                    var receipt=node.Root.EndpointReceipt;
                    if(receipt?.SourceCreationOrdinal is {} sourceOrdinal && source.WorldCreationOrdinal!=sourceOrdinal ||
                        !pointTarget && receipt?.TargetCreationOrdinal is {} targetOrdinal && target!.WorldCreationOrdinal!=targetOrdinal)continue;
                    binding=new(source,target,sourceDetached && parent is not null?parent.Start:sourcePose(source,node.Root.BoneName).Translation,
                        pointTarget?parent?.End ?? node.Root.WorldPoint:targetDetached && parent is not null?parent.End:target!.Spatial!.Transform.Position+node.Root.SpatialOffset,sourceDetached,targetDetached,
                        UnaryStartToTarget:node.Definition.UnaryStartLinkToTarget,UnaryNextToTarget:node.Definition.UnaryNextLinkToTarget,CapturedAtActivation:node.Active,
                        CameraSourceIsLocal:!sourceDetached&&source.IsLocalShip,CameraTargetIsLocal:!targetDetached&&target?.IsLocalShip==true);
                }
                else
                {
                    bool sourceGone=binding.SourceDetached || source is null || source.RenderIdentity!=binding.Source.RenderIdentity;
                    bool targetGone=binding.TargetDetached || target is null || target.RenderIdentity!=binding.Target?.RenderIdentity;
                    // Once an observer has detached it never reacquires by ID,
                    // even if the old object later appears in a snapshot again.
                    binding=binding with {SourceDetached=sourceGone,TargetDetached=targetGone};
                    _bindings[key]=binding;
                    bool Tracks(string property)=>!node.Definition.Properties.ContainsKey(property) || EnbRetailIniDocument.ReadBool(node.Definition.Properties,property);
                    if(node.Active && !sourceGone && source!.Spatial?.Transform is not null && (Tracks("TrackSource") || !binding.CapturedAtActivation))
                        binding=binding with {Source=source,Start=sourcePose(source,node.Root.BoneName).Translation};
                    if(node.Active && !targetGone && target!.Spatial?.Transform is not null && (Tracks("TrackTarget") || !binding.CapturedAtActivation))
                        binding=binding with {Target=target,End=target.Spatial.Transform.Position+node.Root.SpatialOffset};
                }
                if(node.Active)
                {
                    binding=binding with {CapturedAtActivation=true};
                    //006E5960 activates through006E8800;006E59B0 then invokes
                    //006E8890. Intersection uses the live target independently
                    //of TrackTarget;006E8B60 does not inherit the packet flag.
                    if(node.LinkKind==EnbCurrentRetailEffectLinkKind.Root && node.Root.DescriptorFlag &&
                        !binding.TargetDetached && target?.Spatial?.Transform is {} center)
                    {
                        if(targetRadius is null)throw new InvalidDataException("Line surface intersection requires target radius context");
                        binding=binding with {End=EnbRetailSeekEffectPath.IntersectTarget(binding.Start,binding.End,center.Position,targetRadius(target))};
                    }
                }
                _bindings[key]=binding;
            }
            catch(Exception ex){_errors[key]=ex.Message;}
        }
        foreach(var key in _bindings.Keys.Where(k=>!live.Contains(k)).ToArray())_bindings.Remove(key);
        foreach(var key in _errors.Keys.Where(k=>!live.Contains(k)).ToArray())_errors.Remove(key);
    }
}
