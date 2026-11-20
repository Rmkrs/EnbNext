using System.Runtime.CompilerServices;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly record struct LiveMeshDraw(EnbW3dDrawTask Packet,
        EnbW3dIndexedSubmissionPlan? Submission, object? BatchIdentity, string? Diagnostic)
    {
        public int? PreparedBatchOrdinal {get;init;}
        public int PreparedBatchCount {get;init;}
        // Failed compilation returns a default value snapshot, not a reference
        // null. Keep the existing local-rejection contract at this boundary.
        public EnbW3dIndexedSubmissionPlan? Submission { get; init; } =
            Submission is { Packet: not null } ? Submission : null;
    }
    private sealed record RslMeshPassBinding(EnbW3dRenderMaterialPass Pass,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> Mappers)
    {
        public object Identity { get; } = new();
    }
    private readonly ConditionalWeakTable<EnbW3dRenderMaterialPass,
        Dictionary<EnbW3dRenderMesh, RslMeshPassBinding>> _rslMeshPassBindings = new();

    // One scene/static-bucket flush. Generated render objects draw during
    // traversal; retained MeshClass descriptors draw in the renderer flush.
    private IReadOnlyList<LiveMeshDraw> CompileMeshDrawsLegacy(IReadOnlyList<EnbW3dDrawTask> packets,
        Func<EnbW3dRenderTextureBinding, object?> resolveTexture, uint clock)
    {
        using var reuseCompile = EnbWorkReuseDiagnostics.Measure("mesh.compile");
        if (EnbWorkReuseDiagnostics.Active)
        {
            using var inspection = EnbWorkReuseDiagnostics.Inspect();
            foreach (var packet in packets) EnbWorkReuseDiagnostics.ObservePacket("mesh.input", packet);
        }
        var result = new List<LiveMeshDraw>();
        foreach (var bucket in packets.GroupBy(packet => packet.OrderKey.StaticSort.RuntimeLevel))
        {
            var instances = bucket.Where(packet => packet.CurrentRetailMeshInvocation is not null)
                .ToLookup(packet => packet.CurrentRetailMeshInvocation!, ReferenceEqualityComparer.Instance);
            var pending = new List<(EnbCurrentRetailMeshContainerRegistry.Container Container,
                int Pass, uint? ExtraFlags, LiveMeshDraw Draw)>();
            var seen = new HashSet<EnbCurrentRetailMeshInvocation>(ReferenceEqualityComparer.Instance);
            foreach (var packet in bucket)
            {
                if (packet.CurrentRetailProjectedDecalOwner is not null) continue;
                var invocation = packet.CurrentRetailMeshInvocation;
                //00978B70 keeps requested meshes in the ordinary descriptor
                //path when the device has no N-patch capability. Enabling the
                //optional capability still retains this descriptor admission;
                //subdivision occurs when its GPU streams are prepared.
                if (invocation is null || !packet.Mesh.CurrentRetailUsesMeshRenderer)
                {
                    _indexedSubmissions.TryCompile(packet, out var direct, out var error);
                    result.Add(new(packet, direct, null, error));
                    continue;
                }
                if (!seen.Add(invocation)) continue;
                var instancePackets = instances[invocation].ToArray();
                var model = invocation.Appearance;
                var mesh = model.Mesh;
                uint fvf = EnbCurrentRetailMeshBufferLayout.CalculateFvf(mesh.RetailTextureCoordinateSets.Count,
                    mesh.EffectiveColor1.Count != 0, model.Passes.Any(p => p.SpecularColors.Count != 0),
                    EnbCurrentRetailMeshBufferLayout.RequiresAuthoredNormals(mesh.Attributes, model.Passes, true,
                        _texturedBackend.NPatchCapabilityEnabled, (uint)_texturedBackend.NPatchSegments),
                    mesh.BoneIndices.Count != 0, mesh.GeometrySort.Requested,
                    EnbW3dGeometrySortSemantics.CurrentRetailLowLevelSortingRendererEnabled);
                var container = CameraMeshBatches.Containers.Register(model.Owner.ModelIdentity, fvf,
                    mesh.BoneIndices.Count != 0, false, (uint)mesh.Positions.Count,
                    checked((uint)(EnbW3dNPatchCompiler.PreparedIndexCount(mesh,
                        _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments) * model.Passes.Length)));
                //0093F3ED returns before context consumption. The parent scope
                //has been restored by the later static-sort bucket invocation.
                var context = bucket.Key == 0 ? invocation.Geometry.CurrentRetailRslPassContext : null;
                uint contextFlags = instancePackets.Aggregate(context?.Flags ?? 0,
                    (value, item) => value | (item.CurrentRetailAdditionalMeshPassFlags ?? 0));
                uint primaryShader = model.Passes.FirstOrDefault(p => p.Ordinal == 0)?.CurrentRetailUniformShader
                    ?? EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
                foreach (var source in instancePackets.Where(p => p.CurrentRetailAdditionalMeshPassFlags is null))
                {
                    if (context?.AdmitsBaseMesh(primaryShader) == false) continue;
                    if (!_indexedSubmissions.TryCompile(source, out var indexed, out var error) ||
                        !EnbCurrentRetailMeshSubmissionBatches.TryCompile(indexed, model.Owner.ModelIdentity, container,
                            CameraMeshBatches.Descriptors, model.Mappers, resolveTexture, out var batches, out error,
                            EnbW3dNPatchCompiler.IsEligible(source, _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments)))
                    {
                        result.Add(new(source, null, null, error));
                        continue;
                    }
                    foreach (var batch in batches)
                        pending.Add((container, source.Material.Source.Ordinal, null,
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
                        EnqueueExtra(extra, binding.Identity, contextFlags);
                    }
                foreach (var extra in instancePackets.Where(p => p.CurrentRetailAdditionalMeshPassFlags is not null))
                    EnqueueExtra(extra, extra.Material.Source,
                        contextFlags);

                void EnqueueExtra(EnbW3dDrawTask extra, object identity, uint flags)
                {
                    //0093F620 applies the attached pass's own shader, without
                    //00979180's base-descriptor N-patch request augmentation.
                    extra = (extra).ForkTask().SetCurrentRetailAdditionalMeshPassFlags(flags);
                    _indexedSubmissions.TryCompile(extra, out var indexed, out var error);
                    pending.Add((container, 0, flags, new(extra, indexed, identity, error)));
                }
            }
            var containers = CameraMeshBatches.Containers.SnapshotFlushOrder().ToList();
            var queue = new EnbCurrentRetailMeshBatchQueue<LiveMeshDraw>();
            foreach (var item in pending)
                if (item.ExtraFlags is {} flags)
                    queue.EnqueueEffectPass(containers.IndexOf(item.Container), item.Container.IsSkin, flags, item.Draw);
                else queue.Enqueue(containers.IndexOf(item.Container), item.Pass, item.Draw.BatchIdentity!, item.Draw);
            result.AddRange(queue.Flush());
            foreach (var owner in bucket.Where(p => p.CurrentRetailProjectedDecalOwner is not null)
                         .GroupBy(p => p.CurrentRetailProjectedDecalOwner).Reverse())
                foreach (var decal in owner)
                {
                    _indexedSubmissions.TryCompile(decal, out var indexed, out var error);
                    // A slot replacement owns new clipped topology even when
                    // its render path and source mesh ordinal are unchanged.
                    result.Add(new(decal, indexed, decal.Mesh, error));
                }
        }
        return result;
    }
}
