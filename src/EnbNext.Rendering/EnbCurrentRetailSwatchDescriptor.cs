using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// The material references retained by decal manager 0075BE00. A descriptor
/// shares mapper objects with its source prototype; copying it to another
/// RenderObj or replacing a GPU resource does not create a new mapper epoch.
/// </summary>
public sealed class EnbCurrentRetailSwatchDescriptor
{
    public IReadOnlyList<EnbW3dRenderMaterialPass> Passes { get; }
    private readonly IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> _mappers;
    private readonly IReadOnlyList<EnbCurrentRetailTextureObjectState>? _loadedTextures;

    private EnbCurrentRetailSwatchDescriptor(IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> mappers)
    {
        Passes = passes;
        _mappers = mappers;
        var textures=passes.SelectMany(pass=>Enumerable.Range(0,2).Select(stage=>
            pass.TextureStages.FirstOrDefault(value=>value.Ordinal==stage)?.CurrentRetailUniformTexture))
            .Where(texture=>texture is not null && !texture.IsUnbound).ToArray();
        // Published prototype descriptors have concrete shared TextureClass
        // objects. Standalone unowned descriptors cannot establish that table.
        _loadedTextures=textures.All(texture=>texture!.CurrentRetailTextureState is not null)
            ? textures.Select(texture=>texture!.CurrentRetailTextureState!).ToArray() : null;
    }

    public static bool TryCreate(IReadOnlyList<EnbW3dRenderMaterialPass> passes,
        uint constructionClock,
        IDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> prototypeMappers,
        out EnbCurrentRetailSwatchDescriptor descriptor, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(prototypeMappers);
        descriptor = default!;
        diagnostic = null;
        var retained = new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers>(
            ReferenceEqualityComparer.Instance);
        foreach (var pass in passes)
        {
            if (pass.CurrentRetailUniformMaterial?.Material is not {} material) continue;
            if (!prototypeMappers.TryGetValue(material, out var pair))
            {
                if (!EnbW3dSharedMaterialMappers.TryCreate(material, constructionClock, out pair, out diagnostic))
                    return false;
                prototypeMappers.Add(material, pair);
            }
            retained[material] = pair;
        }
        descriptor = new(passes.ToArray(), retained);
        return true;
    }

    /// <summary>Replace a complete target pass group after its earlier material
    /// mutations, preserving expanded target bindings. The returned packets
    /// retain target geometry, instance identity, transforms and lighting.</summary>
    public bool TryApply(IReadOnlyList<EnbW3dDrawTask> target, int applyRevision,
        out IReadOnlyList<EnbW3dDrawTask> packets, out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(target);
        packets = Array.Empty<EnbW3dDrawTask>();
        diagnostic = null;
        if (target.Count == 0) return true;
        var first = target[0];
        int bindingBase = first.OrderKey.BindingOrdinal - first.Material.Source.Ordinal;
        foreach (var packet in target)
        {
            if (!ReferenceEquals(packet.Mesh, first.Mesh) ||
                packet.RenderIdentity != first.RenderIdentity ||
                packet.VisualGeneration != first.VisualGeneration ||
                packet.RenderInstancePath != first.RenderInstancePath ||
                packet.OrderKey.ResourceKey != first.OrderKey.ResourceKey ||
                packet.OrderKey.BindingOrdinal - packet.Material.Source.Ordinal != bindingBase)
                throw new ArgumentException("Packets must belong to one target mesh binding.", nameof(target));
        }
        var effectiveMesh = (first.Mesh).Copy().SetMaterialPasses(target.Select(p => p.Material.Source).ToArray());
        if (!EnbW3dRenderResourceCompiler.TryReplaceUniformDescriptor(effectiveMesh, Passes,
                out var replaced, out diagnostic))
            return false;
        var result = new List<EnbW3dDrawTask>(replaced.Count);
        //0080ED35/0080EDD0 rebuild the table from incoming descriptor slots,
        //not from the target's surviving expanded bindings. Preserve order,
        //duplicates and a null slot when the native descriptor contains one.
        var loadedMaterials = Passes.Select(pass => pass.CurrentRetailUniformMaterial?.Material!).ToArray();
        foreach (var pass in replaced)
        {
            var original = target.FirstOrDefault(p => p.Material.Source.Ordinal == pass.Ordinal) ?? first;
            EnbW3dSharedMaterialMappers? shared = pass.VertexMaterialCardinality == EnbW3dRenderBindingCardinality.PerVertex ? original.CurrentRetailSharedMaterialMappers : null;
            // Expanded target material arrays keep their original material
            // objects. Only a uniform incoming material owns these mappers.
            if (pass.VertexMaterialCardinality != EnbW3dRenderBindingCardinality.PerVertex &&
                pass.CurrentRetailUniformMaterial?.Material is {} material)
                _mappers.TryGetValue(material, out shared);
            result.Add((original).ForkTask().SetMaterial(new(pass, pass.SingleBindingSemantics)).SetCurrentRetailLoadedMaterials(loadedMaterials).SetCurrentRetailLoadedTextures(_loadedTextures).SetCurrentRetailSharedMaterialMappers(shared).SetCurrentRetailDecalRevision(applyRevision).SetOrderKey(original.OrderKey with
                {
                    MaterialPassOrdinal = pass.Ordinal,
                    BindingOrdinal = bindingBase + pass.Ordinal,
                    BlendClass = EnbW3dDrawPacketCompiler.ClassifyBlend(pass),
                }));
        }
        packets = result;
        return true;
    }
}
