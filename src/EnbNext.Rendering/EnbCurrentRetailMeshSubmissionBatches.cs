using System.Collections.Immutable;

namespace EnbNext.Rendering;

public sealed record EnbCurrentRetailMeshSubmissionBatch(object Identity, EnbW3dIndexedSubmissionPlan Submission);

/// <summary>00978FD0 descriptor selection, retaining noncontiguous indexed
/// runs in the same native batch. Container and model ownership are supplied
/// by the caller; neither is inferred from material values or asset names.</summary>
public static class EnbCurrentRetailMeshSubmissionBatches
{
    public static bool TryCompile(EnbW3dIndexedSubmissionPlan indexed,
        object modelIdentity, object container, EnbCurrentRetailDescriptorBatchRegistry descriptors,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dSharedMaterialMappers> mappers,
        Func<EnbW3dRenderTextureBinding, object?> resolveTextureIdentity,
        out IReadOnlyList<EnbCurrentRetailMeshSubmissionBatch> result, out string? diagnostic,
        bool nPatchEnabled = false)
    {
        // Most meshes have one immutable run. Keep that plan instead of
        // constructing a grouping list, run list, array and cloned plan.
        List<(object Identity, List<EnbW3dIndexedSubmissionRun> Runs)>? groups = indexed.Runs.Length>1?[]:null;
        result = [];
        diagnostic = null;
        foreach (var run in indexed.Runs)
        {
            EnbCurrentRetailMaterialBatchIdentity? material = null;
            if (run.Binding.VertexMaterial is {} binding)
            {
                if (binding.CurrentRetailRuntimeCoordinates is not {} coordinates ||
                    !mappers.TryGetValue(binding.Material, out var mapper))
                {
                    diagnostic = "Mesh batch material coordinate/mapper ownership is unavailable.";
                    return false;
                }
                // Instance localization can change word64 without replacing the
                // immutable material object. It participates in the cache key.
                uint word64 = EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(binding.Material);
                material=mapper.RetainMaterialIdentity(run.Binding.Semantics.Uniforms,
                    binding.Material.Attributes&5,run.Binding.Semantics.VertexLighting,
                    coordinates.Stage0,coordinates.Stage1,word64,
                    binding.Material.Mapper0 is not null,binding.Material.Mapper1 is not null);
            }
            object? Texture(int stage) => run.Binding.Textures.Length > stage &&
                run.Binding.Textures[stage] is { IsUnbound: false } texture
                ? (object?)texture.CurrentRetailTextureState ?? resolveTextureIdentity(texture) : null;
            var identity = descriptors.GetOrAdd(modelIdentity, container, indexed.Packet.Material.Source.Ordinal,
                material, run.Binding.Semantics.FixedFunction.PackedRetailState | (nPatchEnabled ? 0x20000u : 0u), Texture(0), Texture(1));
            if(groups is null)
            {
                result=[new EnbCurrentRetailMeshSubmissionBatch(identity,indexed)];
                return true;
            }
            (object Identity,List<EnbW3dIndexedSubmissionRun>? Runs) group=default;
            foreach(var candidate in groups)
                if(ReferenceEquals(candidate.Identity,identity)){group=candidate;break;}
            if (group.Runs is null) { group = (identity, []); groups.Add((identity,group.Runs)); }
            group.Runs.Add(run);
        }
        if(groups is null)return true;
        result = groups.Select(group => new EnbCurrentRetailMeshSubmissionBatch(group.Identity,
            indexed with { Runs = group.Runs.ToImmutableArray() })).ToArray();
        return true;
    }
}
