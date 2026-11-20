using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>A retained class15 occurrence, not a visible-frame draw-list entry.
/// 0098C420 constructs every level in order; secondary children remain attached
/// independently of the selected level. Each occurrence owns a separate buffer.</summary>
public sealed record EnbRObjCompositeEmitter(EnbCurrentRetailRObjParticleAsset Asset, int LevelOrdinal, int MemberOrdinal)
{
    public string? Diagnostic { get; init; }
    public EnbRetailParticleRandomOwner? FactoryRandomOwner { get; init; }

    public static IReadOnlyList<EnbRObjCompositeEmitter> Prepare(EnbRetailAssetBundle assets, EnbW3dRenderResource resource)
    {
        var result = new List<EnbRObjCompositeEmitter>();
        void Add(string name, uint pivot, int level, int ordinal)
        {
            var external = resource.ExternalRenderObjects.FirstOrDefault(e =>
                e.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && e.ParticleEmitterDefinition is not null);
            if (external is null) return;
            var particle = new EnbCurrentRetailRObjParticleAsset(resource.Key, external.ParticleTextureLocation,
                external.ParticleEmitterDefinition!, checked((int)pivot)) { RetainedTexture = external.ParticleRetainedTexture };
            string? diagnostic = null;
            try { particle = particle.PrepareTexture(assets); }
            catch (InvalidDataException ex) { diagnostic = ex.Message; }
            result.Add(new(particle, level, ordinal) { Diagnostic = diagnostic });
        }
        foreach (var hlod in resource.HierarchicalLods)
        {
            foreach (var level in hlod.Levels)
                for (int i = 0; i < level.Members.Count; i++)
                    Add(level.Members[i].Name, level.Members[i].ReferenceIndex, level.Ordinal, i);
            if (hlod.Aggregate is {} aggregate)
                for (int i = 0; i < aggregate.Members.Count; i++)
                    Add(aggregate.Members[i].Name, aggregate.Members[i].ReferenceIndex, -1, i);
        }
        foreach (var model in resource.HierarchyModels)
            for (int i = 0; i < model.Nodes.Count; i++)
                Add(model.Nodes[i].RenderObjectName, model.Nodes[i].PivotIndex, 0, i);
        return result;
    }
}
