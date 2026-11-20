using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed class ProjectedDecalOwner
    {
        public EnbRetailDamageDecalUpdates Updates { get; } = new();
        public List<ProjectedDecalMark> Marks { get; } = [];
        public readonly Dictionary<DecalReceiver,ProjectedDecalReceiver> Receivers=[];
        // RSL base models can exist without a LegoPhysicalModel. Their receiver
        // lifetime is the source owner's factory graph, not a visible task.
        public readonly List<ProjectedDecalReceiver> SourceReceivers=[];
        public void RetireSourceReceivers()
        {
            while(SourceReceivers.Count!=0)SourceReceivers[^1].Retire();
            SourceReceivers.Clear();
        }
        public SceneOwner? Scene;
        public bool Retired;
        public ProjectedDecalReceiver Receiver(DecalReceiver key)
        {
            if(Receivers.TryGetValue(key,out var receiver))return receiver;
            Receivers.Add(key,receiver=new(this,key));
            if(key.Factory is PhysicalFactoryLifetime lifetime)lifetime.DecalReceivers.Add(receiver);
            else SourceReceivers.Add(receiver);
            return receiver;
        }
        public void Remove(int pool,int? slot=null)
        {
            for(int i=Marks.Count-1;i>=0;i--)
            {
                var mark=Marks[i];if(mark.Pool!=pool||(slot is {} wanted&&mark.Slot!=wanted))continue;
                mark.Binding.Marks.Remove(mark);Marks.RemoveAt(i);
            }
        }
        public void Clear()
        {
            foreach(var receiver in Receivers.Values)
            {receiver.Retired=true;receiver.Marks.Clear();if(receiver.Key.Factory is PhysicalFactoryLifetime lifetime)lifetime.DecalReceivers.Remove(receiver);}
            Marks.Clear();Receivers.Clear();SourceReceivers.Clear();
        }
    }
    private readonly record struct DecalReceiver(object Factory,string Path,int Level,int Mesh,int Pivot,string Name,int Occurrence);
    private sealed class ProjectedDecalReceiver(ProjectedDecalOwner owner,DecalReceiver key)
    {
        public readonly ProjectedDecalOwner Owner=owner;
        public readonly DecalReceiver Key=key;
        public readonly List<ProjectedDecalMark> Marks=[];
        public bool Retired;
        public void Retire()
        {
            Retired=true;
            foreach(var mark in Marks)Owner.Marks.Remove(mark);
            Marks.Clear();Owner.Receivers.Remove(Key);Owner.SourceReceivers.Remove(this);
        }
    }
    private sealed record ProjectedDecalMark(int Pool,int Slot,DecalReceiver Receiver,
        EnbRetailDecalGeometry Geometry,EnbW3dRenderMesh Mesh,ProjectedDecalReceiver Binding,
        EnbW3dDrawMaterialBinding Material,string Path,string OwnerLabel);
    // Diagnostic/scanner directory, not ordinary per-frame state lookup.
    private readonly Dictionary<ulong,ProjectedDecalOwner> _projectedDecals=[];
    private readonly HashSet<ProjectedDecalOwner> _dirtyProjectedDecals=[];
    private readonly List<ProjectedDecalOwner> _decalChangesConsumed=[];
    private long _projectedDecalEpoch;
    internal Action<string>? ProjectedDecalDiagnostic { get; set; }

    private void ClearProjectedDecals()
    {
        foreach(var owner in _projectedDecals.Values){owner.Clear();owner.Retired=true;}
        _projectedDecals.Clear();_dirtyProjectedDecals.Clear();_projectedDecalEpoch++;
    }
    private void RetireProjectedDecals(SceneOwner scene)
    {
        if(scene.ProjectedDecals is not {} owner)return;
        owner.Clear();owner.Retired=true;_dirtyProjectedDecals.Remove(owner);
        _projectedDecals.Remove(scene.RenderIdentity);scene.ProjectedDecals=null;
    }
    private void InvalidateProjectedDecals(SceneOwner scene,EnbSceneObjectSnapshot definition)
    {
        if(scene.ProjectedDecals is {} owner&&owner.Updates.HasChanges(definition.DamageDecalPools))_dirtyProjectedDecals.Add(owner);
    }
    private void BindProjectedDecalRoot(LiveRootInstance root,SceneOwner? scene)
    {
        if(root.Object.ObjectType is not (0 or 1 or 25))return;
        var view=_shipExhaustOwner??this;
        if(scene is not null&&_shipExhaustOwner is null&&_ownedSceneFrame&&_ownedFrameSnapshot?.IsTransitioning!=true)
        {
            var owner=scene.ProjectedDecals;
            if(owner is null||owner.Retired)
            {
                scene.ProjectedDecals=owner=new(){Scene=scene};
                _projectedDecals[scene.RenderIdentity]=owner;
                if(owner.Updates.HasChanges(scene.DamageDecalPools))_dirtyProjectedDecals.Add(owner);
            }
            root.ProjectedDecals=owner;root.ProjectedDecalEpoch=view._projectedDecalEpoch;
        }
        else if(root.ProjectedDecals is null||root.ProjectedDecals.Retired||root.ProjectedDecalEpoch!=view._projectedDecalEpoch)
        {
            view._projectedDecals.TryGetValue(root.Object.RenderIdentity,out root.ProjectedDecals);
            root.ProjectedDecalEpoch=view._projectedDecalEpoch;
        }
    }
    // Projection-only traversal: no frame receiver-key derivation in the prepared path.
    private static IEnumerable<(DecalReceiver Key,EnbW3dDrawTask Packet)> DecalReceivers(LiveRootInstance root,EnbW3dDrawInvocation list)
    {
        foreach(var packet in list.Packets)
        {
            if(!IsDecalReceiver(root,packet))continue;
            yield return (ReceiverKey(root,list,packet),packet);
        }
    }
    private static bool IsDecalReceiver(LiveRootInstance root,EnbW3dDrawTask packet)
        =>packet.SpecialMaterial is null&&packet.Mesh.MaterialPasses.Count!=0&&
          packet.Material.Source.Ordinal==packet.Mesh.MaterialPasses[0].Ordinal&&packet.Mesh.CurrentRetailUsesMeshRenderer&&
          (root.Root.AllowedRenderObjectNames is not {Count:>0} allowed||allowed.Contains(packet.RenderObjectName));
    private static DecalReceiver ReceiverKey(LiveRootInstance root,EnbW3dDrawInvocation list,EnbW3dDrawTask packet)
        =>new(PhysicalParticleFactory(root)??root.Root.PrimitiveFactoryIdentity,root.InstancePath,
            list.HlodSelection?.LevelOrdinal??-1,packet.Mesh.Ordinal,packet.PivotIndex,packet.RenderObjectName,
            packet.MeshOccurrence?.Repetition??0);
    private static bool IsReceiverDescendant(LiveRootInstance candidate,LiveRootInstance owner)
    {
        for(var current=candidate;current is not null;current=current.Parent)if(ReferenceEquals(current,owner))return true;
        return false;
    }
    private void PrepareProjectedDecals(LiveFrame frame,RootTaskList roots,EnbRetailAssetBundle assets,EnbW3dLightEnvironment lights)
    {
        if(_shipExhaustOwner is not null)return;
        if(frame.Movie is not null||frame.Snapshot.IsTransitioning)return;
        if(!_ownedSceneFrame){PrepareDiagnosticProjectedDecals(frame,roots,assets,lights);return;}
        foreach(var owner in _dirtyProjectedDecals)
        {
            if(owner.Retired){_decalChangesConsumed.Add(owner);continue;}
            if(owner.Scene is not {} scene)continue;
            LiveRootInstance? root=null;
            for(var cell=scene.RootTasks.First;cell is not null;cell=cell.OwnerNext)
                if(cell.Value.Parent is null){root=cell.Value;break;}
            if(root is null)continue;
            var members=new List<LiveRootInstance>();
            for(var cell=scene.RootTasks.First;cell is not null;cell=cell.OwnerNext)
                if(IsReceiverDescendant(cell.Value,root))members.Add(cell.Value);
            if(members.Count!=0){PrepareProjectedDecalOwner(frame,root,owner,members,assets,lights);_decalChangesConsumed.Add(owner);}
        }
        foreach(var owner in _decalChangesConsumed)_dirtyProjectedDecals.Remove(owner);
        _decalChangesConsumed.Clear();
    }
    // Snapshot-driven diagnostic fixtures lack a production change feed. Keep
    // their lifetime adapter explicit; production uses the SceneOwner callbacks.
    private void PrepareDiagnosticProjectedDecals(LiveFrame frame,RootTaskList roots,EnbRetailAssetBundle assets,EnbW3dLightEnvironment lights)
    {
        var owners=roots.Where(r=>r.Parent is null&&r.Object.ObjectType is 0 or 1 or 25).ToArray();
        var live=owners.Select(r=>r.Object.RenderIdentity).ToHashSet();
        foreach(var identity in _projectedDecals.Keys.Where(k=>!live.Contains(k)).ToArray())
        {var removed=_projectedDecals[identity];removed.Clear();removed.Retired=true;_projectedDecals.Remove(identity);}
        foreach(var root in owners)
        {
            if(!_projectedDecals.TryGetValue(root.Object.RenderIdentity,out var owner))_projectedDecals.Add(root.Object.RenderIdentity,owner=new());
            var members=roots.Where(r=>IsReceiverDescendant(r,root)).ToArray();
            var factories=members.Select(r=>(PhysicalParticleFactory(r)??r.Root.PrimitiveFactoryIdentity,r.InstancePath)).ToHashSet();
            foreach(var receiver in owner.Receivers.Values.Where(r=>!factories.Contains((r.Key.Factory,r.Key.Path))).ToArray())
            {receiver.Retire();if(receiver.Key.Factory is PhysicalFactoryLifetime lifetime)lifetime.DecalReceivers.Remove(receiver);}
            foreach(var member in members){member.ProjectedDecals=owner;member.ProjectedDecalEpoch=_projectedDecalEpoch;}
            if(owner.Updates.HasChanges(root.Object.DamageDecalPools))PrepareProjectedDecalOwner(frame,root,owner,members,assets,lights);
        }
    }
    private void PrepareProjectedDecalOwner(LiveFrame frame,LiveRootInstance root,ProjectedDecalOwner owner,
        IReadOnlyList<LiveRootInstance> members,EnbRetailAssetBundle assets,EnbW3dLightEnvironment lights)
    {
        List<(DecalReceiver Key,EnbW3dDrawTask Packet)>? receivers=null;
        owner.Updates.Apply(root.Object.DamageDecalPools,pool=>owner.Remove(pool),(pool,slot,value)=>
        {
                    //981A40 removes the old slot before projecting its replacement.
                    owner.Remove(pool, slot);
                    if (value.AssetId < 0) return;
                    if (assets.ProjectedDecals is not {} catalog || value.AssetId >= catalog.Entries.Count)
                    { ProjectedDecalDiagnostic?.Invoke($"Projected decal asset {value.AssetId} is unavailable."); return; }
                    var definition = catalog.Entries[value.AssetId];
                    if (root.CurrentRetailWorldSphere is not {} sphere ||
                        assets.Textures.Resolve(definition.Texture).Image is not {} texture ||
                        !(definition.BaseSize > 0) || !float.IsFinite(definition.BaseSize) ||
                        !(value.Scale > 0) || !float.IsFinite(value.Scale) ||
                        !float.IsFinite(value.Direction.X) || !float.IsFinite(value.Direction.Y) || !float.IsFinite(value.Direction.Z))
                    { ProjectedDecalDiagnostic?.Invoke($"Projected decal {value.AssetId} has unavailable texture, bounds or size."); return; }
                    if (receivers is null)
                    {
                        receivers = [];
                        //98F9E0 visits every ordinary level and the aggregate;
                        //creation is independent of current visibility and LOD.
                        foreach (var member in members)
                        {
                            if (member.Root.SpecialMaterial is not null || member.Root.RetainedEmitter is not null) continue;
                            EnbW3dHlodSelection?[] selections = member.Root.Hlod is {} hlod
                                ? hlod.Levels.Select(l => (EnbW3dHlodSelection?)new(hlod.Name,l.Ordinal)).ToArray() : [null];
                            foreach (var selection in selections)
                                if (TryBuildRetainedRootDrawList(member, assets, frame.CurrentClientTime, lights,
                                        out var captured, out var diagnostic, true, selection))
                                    receivers.AddRange(DecalReceivers(member,captured));
                                else ProjectedDecalDiagnostic?.Invoke(diagnostic ?? "Projected decal receiver unavailable.");
                        }
                    }
                    var projection = EnbRetailDecalProjection.Create(value.Direction, value.Scale, definition, sphere);
                    foreach (var receiver in receivers)
                    {
                        if (!EnbRetailProjectedDecal.Admits(receiver.Packet.Mesh)) continue;
                        var geometry = EnbRetailProjectedDecal.Build(projection, receiver.Packet, texture.Width);
                        if (geometry.Indices.Length == 0) continue;
                        var mesh = EnbRetailProjectedDecalMaterial.Build(geometry, definition, $"damage-{pool}-{slot}");
                        var binding=owner.Receiver(receiver.Key);
                        var pass=mesh.MaterialPasses[0];
                        var mark=new ProjectedDecalMark(pool,slot,receiver.Key,geometry,mesh,binding,
                            new(pass,pass.SingleBindingSemantics),
                            $"{receiver.Key.Path}/damage-decal/{receiver.Key.Level}/{receiver.Key.Mesh}/{receiver.Key.Pivot}/{receiver.Key.Occurrence}/{pool}/{slot}",
                            $"{root.Object.RenderIdentity}/{receiver.Key.Path}/{receiver.Key.Level}/{receiver.Key.Mesh}/{receiver.Key.Pivot}/{receiver.Key.Occurrence}");
                        owner.Marks.Add(mark);binding.Marks.Add(mark);
                    }
        });
    }
    private void AppendProjectedDecals(LiveRootInstance root,EnbW3dDrawInvocation list,List<EnbW3dDrawTask> packets,EnbResolvedRenderCamera camera)
    {
        var owner=root.ProjectedDecals;
        if(owner is null||owner.Retired||owner.Marks.Count==0)return;
        var factory=root.ProjectedDecalFactory??=PhysicalParticleFactory(root)??root.Root.PrimitiveFactoryIdentity;
        foreach(var source in list.Packets)
        {
            if(!IsDecalReceiver(root,source))continue;
            var occurrence=source.MeshOccurrence;
            var binding=occurrence?.ProjectedDecalReceiver as ProjectedDecalReceiver;
            if(binding is null||binding.Retired||!ReferenceEquals(binding.Owner,owner)||
                !ReferenceEquals(binding.Key.Factory,factory)||
                binding.Key.Level!=(list.HlodSelection?.LevelOrdinal??-1)||binding.Key.Path!=root.InstancePath)
            {
                binding=owner.Receiver(ReceiverKey(root,list,source));
                if(occurrence is not null)occurrence.ProjectedDecalReceiver=binding;
            }
            if(binding.Marks.Count==0||Vector3.Dot(source.Bounds.Center-camera.Position,camera.Basis.Forward)-source.Bounds.Radius>=1_000_000)continue;
            foreach(var mark in binding.Marks)
            {
                EnbW3dDeformedGeometry? deformed=null;
                if(mark.Geometry.SourceVertices.Length!=0)
                {
                    if(source.DeformedGeometry is not {} current)continue;
                    deformed=EnbRetailProjectedDecal.UpdateSkin(mark.Geometry,current);
                }
                packets.Add(source.ForkTask().SetMesh(mark.Mesh).SetDeformedGeometry(deformed)
                    .SetWorldTransform(EnbRetailProjectedDecal.MeshWorld(source)).SetMaterial(mark.Material)
                    .SetCurrentRetailMeshModelScale(1).SetCurrentRetailDepthBias(8).SetCurrentRetailMeshInvocation(null)
                    .SetRenderInstancePath(mark.Path).SetCurrentRetailProjectedDecalOwner(mark.OwnerLabel));
            }
        }
    }
}
