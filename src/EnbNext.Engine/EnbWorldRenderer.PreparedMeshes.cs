using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Temporary migration rollback, read once; ordinary launches use retention.
    private readonly bool _retainedOrdinaryMeshes = Environment.GetEnvironmentVariable("ENB_LEGACY_MESH_PREPARATION") != "1";
    private readonly MeshScratch _meshScratch = new();
    internal long MeshCategoryBindingBuilds=>_meshScratch.Queue.CategoryBindingBuildCount;
    internal long MeshCategoryFallbackLookups=>_meshScratch.Queue.CategoryLookupCount;
    private readonly EnbCurrentRetailMeshVisibility _meshVisibility=new();
    private readonly string? _meshVisibilityMode=Environment.GetEnvironmentVariable("ENB_MESH_FRUSTUM_MODE");
    private static long _meshAdmissionChecked,_meshAdmissionRejected;
    private static readonly string? MeshAdmissionReport=Environment.GetEnvironmentVariable("ENB_MESH_FRUSTUM_REPORT");
    static EnbWorldRenderer()
    {
        if(MeshAdmissionReport is {} path)AppDomain.CurrentDomain.ProcessExit+=(_,_)=>
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new {Checked=_meshAdmissionChecked,Rejected=_meshAdmissionRejected}));
    }
    private readonly struct MeshFrameScope(EnbWorldRenderer view) : IDisposable
    {
        public void Dispose() { view._meshScratch.Clear(); view.ClearDazzleQueryTasks(); if(view._frameDazzles.Count==0)view._dazzleMeshRays.Clear(); view._idleGpuResources.Clear(); view._frameEmitterResidents.ClearFrame(); view.EndRigidOccurrences(); view.ClearFrameMembership(); view._ownedScene?.CompleteConsumption(); view.DrainRetiredRslBindings(); view.DrainOwnedRslPasses(); view._dispatchOwnedRslCallback=null; }
    }
    private sealed class MeshScratch
    {
        public readonly List<EnbCurrentRetailMeshModelOwner.QueuedReference> Leases = [];
        public readonly List<LiveMeshDraw> Result = [];
        public readonly EnbCurrentRetailStaticSortTasks Buckets = new();
        public readonly EnbCurrentRetailMeshInvocationTasks Instances = new();
        public readonly EnbFrameGroups<string, EnbW3dDrawTask> Decals = new(StringComparer.Ordinal);
        public readonly List<(EnbCurrentRetailMeshContainerRegistry.Container Container, int Pass, uint? ExtraFlags, LiveMeshDraw Draw)> Pending = [];
        public readonly EnbCurrentRetailMeshBatchQueue<LiveMeshDraw> Queue = new();
        public void Clear()
        {
            foreach (var lease in Leases) lease.Dispose();
            Leases.Clear();
            Result.Clear(); Buckets.Clear(); Instances.Clear(); Decals.Clear();
            Pending.Clear(); Queue.Clear();
        }
    }
    private IReadOnlyList<LiveMeshDraw> CompileMeshDraws(IEnumerable<EnbW3dDrawTask> packets,
        Func<EnbW3dRenderTextureBinding, object?> resolveTexture, uint clock, Func<EnbW3dDrawTask,EnbResolvedRenderCamera> camera,
        IReadOnlyDictionary<ulong,LivePlanetGpuFrameTracker>? planets=null)
        => _retainedOrdinaryMeshes ? CompileMeshDrawsRetained(packets, resolveTexture, clock, camera, planets)
            : CompileMeshDrawsLegacy(packets.Where(p => p.SpecialMaterial is null&&
                (!p.Mesh.CurrentRetailUsesMeshRenderer||_meshVisibilityMode is "disabled" or "census"||
                    AdmitPlanetMesh(p,camera(p),planets))).ToArray(), resolveTexture, clock);

    private bool AdmitPlanetMesh(EnbW3dDrawTask packet,EnbResolvedRenderCamera camera,
        IReadOnlyDictionary<ulong,LivePlanetGpuFrameTracker>? planets,int count=1)
    {
        bool admitted=_meshVisibility.Admits(packet,camera);
        if(!admitted&&_meshVisibilityMode!="census"&&planets?.TryGetValue(packet.RenderIdentity,out var planet)==true)
            planet.CulledPackets+=count;
        return admitted;
    }

    // One scene/static-bucket flush. Generated render objects draw during
    // traversal; retained MeshClass descriptors draw in the renderer flush.
    private IReadOnlyList<LiveMeshDraw> CompileMeshDrawsRetained(IEnumerable<EnbW3dDrawTask> packets,
        Func<EnbW3dRenderTextureBinding, object?> resolveTexture, uint clock, Func<EnbW3dDrawTask,EnbResolvedRenderCamera> camera,
        IReadOnlyDictionary<ulong,LivePlanetGpuFrameTracker>? planets)
    {
        using var reuseCompile = EnbWorkReuseDiagnostics.Measure("mesh.compile");
        if (EnbWorkReuseDiagnostics.Active)
        {
            using var inspection = EnbWorkReuseDiagnostics.Inspect();
            foreach (var packet in packets) EnbWorkReuseDiagnostics.ObservePacket("mesh.input", packet);
        }
        _meshScratch.Clear();
        var result = _meshScratch.Result;
        foreach (var packet in packets)
            if (packet.SpecialMaterial is null) _meshScratch.Buckets.Add(packet.OrderKey.StaticSort.RuntimeLevel, packet);
        for (int bucketIndex = 0; bucketIndex < _meshScratch.Buckets.Count; bucketIndex++)
        {
            var bucket = _meshScratch.Buckets.Values(bucketIndex);
            int bucketLevel = _meshScratch.Buckets.Key(bucketIndex);
            var instances = _meshScratch.Instances;
            instances.Clear();
            for (int ordinal = 0; ordinal < bucket.Count; ordinal++)
                if (bucket[ordinal].CurrentRetailMeshInvocation is {} instance)
                    instances.Add(instance, bucket[ordinal], ordinal);
            var pending = _meshScratch.Pending; pending.Clear();
            for (int packetOrdinal = 0; packetOrdinal < bucket.Count; packetOrdinal++)
            {
                var packet = bucket[packetOrdinal];
                if (packet.CurrentRetailProjectedDecalOwner is not null) continue;
                var invocation = packet.CurrentRetailMeshInvocation;
                //00978B70 keeps requested meshes in the ordinary descriptor
                //path when the device has no N-patch capability. Enabling the
                //optional capability still retains this descriptor admission;
                //subdivision occurs when its GPU streams are prepared.
                if (invocation is null || !packet.Mesh.CurrentRetailUsesMeshRenderer)
                {
                    if(packet.Mesh.CurrentRetailUsesMeshRenderer&&_meshVisibilityMode is not ("disabled" or "census")&&
                        !AdmitPlanetMesh(packet,camera(packet),planets))continue;
                    _indexedSubmissions.TryCompile(packet, out var direct, out var error);
                    result.Add(new(packet, direct, null, error));
                    continue;
                }
                if (!instances.IsFirst(invocation, packetOrdinal)) continue;
                if(_meshVisibilityMode!="disabled")
                {
                    bool admitted=AdmitPlanetMesh(packet,camera(packet),planets,instances.Values(invocation).Count);
                    if(MeshAdmissionReport is not null){_meshAdmissionChecked++;if(!admitted)_meshAdmissionRejected++;}
                    if(!admitted&&_meshVisibilityMode!="census")continue;
                }
                var instancePackets = instances.Values(invocation);
                var model = invocation.Appearance;
                _meshScratch.Leases.Add(model.Owner.RetainQueued());
                var mesh = model.Mesh;
                EnbCurrentRetailMeshContainerRegistry.Container? container = null;
                bool rigid = mesh.BoneIndices.Count == 0 && packet.DeformedGeometry is null && packet.CurrentRetailViewPositions is null;
                bool retainedSkin=mesh.BoneIndices.Count!=0 && packet.DeformedGeometry is not null &&
                    packet.CurrentRetailViewPositions is null && !EnbW3dNPatchCompiler.IsEligible(packet,
                        _texturedBackend.NPatchCapabilityEnabled,_texturedBackend.NPatchSegments);
                if (rigid) container = CameraMeshBatches.PrepareRigidContainer(model,
                    _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments);
                else if(retainedSkin) container=CameraMeshBatches.PrepareSkinContainer(model,
                    _texturedBackend.NPatchCapabilityEnabled,_texturedBackend.NPatchSegments);
                else
                {
                    uint fvf = EnbCurrentRetailMeshBufferLayout.CalculateFvf(mesh.RetailTextureCoordinateSets.Count,
                        mesh.EffectiveColor1.Count != 0, model.Passes.Any(p => p.SpecularColors.Count != 0),
                        EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(mesh.Attributes, model.Passes, true,
                            _texturedBackend.NPatchCapabilityEnabled, (uint)_texturedBackend.NPatchSegments),
                        mesh.BoneIndices.Count != 0, mesh.GeometrySort.Requested,
                        EnbW3dGeometrySortSemantics.CurrentRetailLowLevelSortingRendererEnabled);
                    container = CameraMeshBatches.Containers.Register(model.Owner.ModelIdentity, fvf,
                        mesh.BoneIndices.Count != 0, false, (uint)mesh.Positions.Count,
                        checked((uint)(EnbW3dNPatchCompiler.PreparedIndexCount(mesh,
                            _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments) * model.Passes.Length)));
                }
                //0093F3ED returns before context consumption. The parent scope
                //has been restored by the later static-sort bucket invocation.
                var context = bucketLevel == 0 ? invocation.Geometry.CurrentRetailRslPassContext : null;
                uint contextFlags = context?.Flags ?? 0;
                foreach (var item in instancePackets) contextFlags |= item.CurrentRetailAdditionalMeshPassFlags ?? 0;
                uint primaryShader = model.Passes.FirstOrDefault(p => p.Ordinal == 0)?.CurrentRetailUniformShader
                    ?? EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
                foreach (var source in instancePackets)
                {
                    if (source.CurrentRetailAdditionalMeshPassFlags is not null) continue;
                    if (context?.AdmitsBaseMesh(primaryShader) == false) continue;
                    if (rigid||retainedSkin)
                    {
                        EnbCurrentRetailPreparedMeshPass prepared; string? prepareError;
                        bool ready=rigid?CameraMeshBatches.TryPrepareRigidPass(source,model,_texturedBackend.NPatchCapabilityEnabled,
                            _texturedBackend.NPatchSegments,resolveTexture,out prepared,out prepareError):
                            CameraMeshBatches.TryPrepareSkinPass(source,model,_texturedBackend.NPatchCapabilityEnabled,
                            _texturedBackend.NPatchSegments,resolveTexture,out prepared,out prepareError);
                        if (!ready)
                        { result.Add(new(source, null, null, prepareError)); continue; }
                        container = prepared.Container;
                        for (int i = 0; i < prepared.Count; i++)
                            pending.Add((container!, source.Material.Source.Ordinal, null,
                                new(source, prepared.Submit(i, source), prepared.BatchIdentity(i), null){PreparedBatchOrdinal=i,PreparedBatchCount=prepared.Count}));
                        continue;
                    }
                    if (!_indexedSubmissions.TryCompile(source, out var indexed, out var error) ||
                        !EnbCurrentRetailMeshSubmissionBatches.TryCompile(indexed, model.Owner.ModelIdentity, container!,
                            CameraMeshBatches.Descriptors, model.Mappers, resolveTexture, out var batches, out error,
                            EnbW3dNPatchCompiler.IsEligible(source, _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments)))
                    {
                        result.Add(new(source, null, null, error));
                        continue;
                    }
                    foreach (var batch in batches)
                        pending.Add((container!, source.Material.Source.Ordinal, null,
                            new(source, batch.Submission, batch.Identity, null)));
                }
                if (context is not null)
                    foreach (var pass in context.Passes)
                    {
                        if (pass is null) continue;
                        var bindings = _rslMeshPassBindings.GetValue(pass, _ => new(ReferenceEqualityComparer.Instance));
                        if (!bindings.TryGetValue(mesh, out var binding))
                        {
                            var material = EnbCurrentRetailRslEffectPassMaterial.BindToMesh(pass, mesh);
                            if (!_prototypeResources.TryRetainMaterialMappers(material.VertexMaterials.Select(v => v.Material),
                                    clock, out var mappers, out var error))
                            { result.Add(new(packet, null, null, error)); continue; }
                            bindings.Add(mesh, binding = new(material, mappers));
                        }
                        var extra = (invocation.Geometry).ForkTask().SetMaterial(new(binding.Pass, binding.Pass.SingleBindingSemantics)).SetLightEnvironment(packet.LightEnvironment).SetCurrentRetailMaterialMappers(binding.Mappers).SetCurrentRetailSharedMaterialMappers(null).SetCurrentRetailRslPassContext(null).SetCurrentRetailMeshInvocation(invocation);
                        EnqueuePreparedExtra(extra, binding.Identity, contextFlags, container!);
                    }
                foreach (var extra in instancePackets)
                    if (extra.CurrentRetailAdditionalMeshPassFlags is not null)
                        EnqueuePreparedExtra(extra, extra.Material.Source, contextFlags, container!);
            }

            var queue = _meshScratch.Queue;
            foreach (var item in pending)
                if (item.ExtraFlags is {} flags)
                    queue.EnqueueEffectPass(item.Container.FlushOrdinal, item.Container.IsSkin, flags, item.Draw);
                else queue.Enqueue(item.Container.FlushOrdinal, item.Pass, item.Draw.BatchIdentity!, item.Draw);
            queue.FlushInto(result);
            var decals = _meshScratch.Decals; decals.Clear();
            foreach (var item in bucket)
                if (item.CurrentRetailProjectedDecalOwner is {} owner) decals.Add(owner, item);
            for (int ownerIndex = decals.Count - 1; ownerIndex >= 0; ownerIndex--)
                foreach (var decal in decals.Values(ownerIndex))
                {
                    _indexedSubmissions.TryCompile(decal, out var indexed, out var error);
                    // A slot replacement owns new clipped topology even when
                    // its render path and source mesh ordinal are unchanged.
                    result.Add(new(decal, indexed, decal.Mesh, error));
                }
        }
        return result;
    }
    private void EnqueuePreparedExtra(EnbW3dDrawTask extra, object identity, uint flags,
        EnbCurrentRetailMeshContainerRegistry.Container container)
    {
        extra = (extra).ForkTask().SetCurrentRetailAdditionalMeshPassFlags(flags);
        _indexedSubmissions.TryCompile(extra, out var indexed, out var error);
        _meshScratch.Pending.Add((container, 0, flags, new(extra, indexed, identity, error)));
    }

}
