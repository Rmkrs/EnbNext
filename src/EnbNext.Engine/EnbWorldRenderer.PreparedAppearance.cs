using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbFrameGroups<(EnbW3dRenderMesh Mesh, int Binding), int> _appearanceGroups = new(
        EqualityComparer<(EnbW3dRenderMesh Mesh,int Binding)>.Create(
            static (a,b)=>ReferenceEquals(a.Mesh,b.Mesh)&&a.Binding==b.Binding,
            static key=>HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Mesh),key.Binding)));
    private void RetainShadowAppearance(List<EnbW3dDrawTask> packets, int start, uint clock)
    {
        if (_retainedOrdinaryMeshes) RetainShadowAppearancePrepared(packets, start, clock);
        else RetainShadowAppearanceLegacy(packets, start, clock);
    }
    private long _appearanceEpoch;
    private long _ownedAppearancePreparations;
    private sealed record OwnedAppearanceGroup(int[] Indices,EnbCurrentRetailMeshAppearance Appearance);
    private sealed record OwnedAppearanceLayout(object Preparation,long Revision,
        ulong Generation,int Count,EnbWorldRenderer Owner,long Epoch,OwnedAppearanceGroup[] Groups);
    private void RetainOwnedAppearance(List<EnbW3dDrawTask> packets,int start,uint clock,LiveRootInstance instance)
    {
        // SurfaceLayoutRevision includes asynchronous name-texture completion
        // as well as material mutations; both invalidate this owner-held layout.
        if(!_retainedOrdinaryMeshes || instance.CurrentAppearancePreparation is null)
        {RetainShadowAppearance(packets,start,clock);return;}
        var owner=_sharedMaterialOwner??this;
        var layout=instance.AppearanceLayout;
        if(layout is null || !ReferenceEquals(layout.Preparation,instance.CurrentAppearancePreparation) ||
            layout.Revision!=instance.SurfaceLayoutRevision || layout.Generation!=instance.Object.VisualGeneration ||
            layout.Count!=packets.Count-start || !ReferenceEquals(layout.Owner,owner) || layout.Epoch!=owner._appearanceEpoch)
        {
            RetainShadowAppearancePrepared(packets,start,clock);
            for(int i=start;i<packets.Count;i++)
                if(packets[i].SpecialMaterial is null && packets[i].CurrentRetailMeshInvocation is null)
                {instance.AppearanceLayout=null;return;}
            var groups=new Dictionary<EnbCurrentRetailMeshInvocation,List<int>>(ReferenceEqualityComparer.Instance);
            for(int i=start;i<packets.Count;i++)
                if(packets[i].CurrentRetailMeshInvocation is {} invocation)
                {
                    if(!groups.TryGetValue(invocation,out var indices))groups.Add(invocation,indices=[]);
                    indices.Add(i-start);
                }
            instance.AppearanceLayout=new(instance.CurrentAppearancePreparation!,instance.SurfaceLayoutRevision,
                instance.Object.VisualGeneration,packets.Count-start,owner,owner._appearanceEpoch,
                groups.Select(group=>new OwnedAppearanceGroup(group.Value.ToArray(),group.Key.Appearance)).ToArray());
            _ownedAppearancePreparations++;
            return;
        }
        foreach(var group in layout.Groups)
        {
            var entry=group.Appearance;
            var invocation=EnbCurrentRetailMeshInvocation.Create(packets[start+group.Indices[0]],entry);
            foreach(int index in group.Indices)
            {
                var packet=packets[start+index];
                packets[start+index]=(packet).ForkTask().SetCurrentRetailMaterialMappers(entry.Mappers).SetCurrentRetailMeshInvocation(invocation);
            }
        }
    }
    private static bool AppearanceMatches(EnbCurrentRetailMeshAppearance appearance,
        EnbW3dRenderMesh mesh, List<int> indices, List<EnbW3dDrawTask> packets)
    {
        if (!ReferenceEquals(appearance.Mesh, mesh) || appearance.Passes.Length != indices.Count) return false;
        for (int i = 0; i < indices.Count; i++)
            if (!ReferenceEquals(appearance.Passes[i], packets[indices[i]].Material.Source)) return false;
        return true;
    }

    private void RetainShadowAppearancePrepared(List<EnbW3dDrawTask> packets, int start, uint clock)
    {
        var appearanceOwner = _sharedMaterialOwner ?? this;
        _appearanceGroups.Clear();
        try
        {
            for (int index = start; index < packets.Count; index++)
            {
                var packet = packets[index];
                if (packet.SpecialMaterial is null)
                    _appearanceGroups.Add((packet.Mesh, packet.OrderKey.BindingOrdinal - packet.Material.Source.Ordinal), index);
            }
            for (int groupIndex = 0; groupIndex < _appearanceGroups.Count; groupIndex++)
            {
                var group = _appearanceGroups.Values(groupIndex);
                var first = packets[group[0]];
                var mesh = first.Mesh;
                var identity = (first.RenderIdentity, first.VisualGeneration);
                var slot = new LiveMeshAppearanceSlot(mesh, first.RenderInstancePath, _appearanceGroups.Key(groupIndex).Binding);
                appearanceOwner._meshAppearanceBindings.TryGetValue(identity, out var bindings);
                EnbCurrentRetailMeshAppearance? entry = bindings?.GetValueOrDefault(slot);
                if (entry is not null && !AppearanceMatches(entry, mesh, group, packets)) entry = null;
                if (entry is null)
                    foreach (var candidate in appearanceOwner._shadowAppearanceModels)
                        if (AppearanceMatches(candidate, mesh, group, packets)) { entry = candidate; break; }
                if (entry is null)
                {
                    var passes = group.Select(index => packets[index].Material.Source).ToArray();
                    bool effectLocal=(_shipExhaustOwner??this)._effectRslMeshModels.TryGetValue(mesh,out var configured);
                    if(effectLocal || _configuredDirectMeshModels.TryGetValue(mesh,out configured) ||
                        _shipExhaustOwner is {} primary && primary._configuredDirectMeshModels.TryGetValue(mesh,out configured))
                    {
                        var retained=configured!.CreateAppearance(mesh,passes);
                        entry=retained with {Owner=retained.Owner.CloneRenderObject(),PrototypeOwned=!effectLocal};
                    }
                    else
                    {
                    var materials = passes.SelectMany(pass => pass.VertexMaterials.Select(b => b.Material)
                        .Concat(pass.TriangleBindings.Where(b => b.VertexMaterial.HasValue).Select(b => b.VertexMaterial!.Value.Material))
                        .Concat(pass.CurrentRetailUniformMaterial is {} uniform ? [uniform.Material] : Array.Empty<EnbW3dRenderVertexMaterial>()));
                    if (!_prototypeResources.TryRetainMaterialMappers(materials, clock, out var mappers, out _)) continue;
                    // Retain the actual post-mutation table, including unused entries
                    // and duplicate pointers. Swatch/default descriptor references
                    // do not become loaded entries merely because they are drawn.
                    var loadedMaterials = first.CurrentRetailLoadedMaterials ?? mesh.CurrentRetailLoadedMaterials;
                    if (!appearanceOwner._shadowMeshBatches.TryCreateModel(_prototypeResources, mesh, loadedMaterials, clock, out var owner, out _,first.CurrentRetailLoadedTextures)) continue;
                    entry = new(mesh, passes, owner, mappers, passes.All(pass =>
                        mesh.MaterialPasses.Any(original => ReferenceEquals(original, pass)) || _overbrightMaterials.OwnsDescriptor(pass)));
                    }
                    appearanceOwner._shadowAppearanceModels.Add(entry);
                }
                if (bindings is null)
                    bindings=appearanceOwner.CreateMeshAppearanceBindings(identity);
                appearanceOwner.BindMeshAppearance(bindings,slot,entry);
                var invocation = EnbCurrentRetailMeshInvocation.Create(first, entry);
                foreach (int index in group)
                {
                    var packet=packets[index];
                    var retained = (packet).ForkTask().SetCurrentRetailMaterialMappers(entry.Mappers).SetCurrentRetailMeshInvocation(invocation);
                    packets[index] = retained;
                }
            }
        }
        finally { _appearanceGroups.Clear(); }
    }

}
