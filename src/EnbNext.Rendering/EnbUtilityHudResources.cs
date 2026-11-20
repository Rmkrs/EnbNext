using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>00693230/0058C450 authored TSI14A pieces, prepared by the asset session.</summary>
public sealed class EnbUtilityHudResources
{
    public sealed record Piece(string Key, EnbAuthoredUiAsset Asset, EnbUiRect Bounds, int RasterIndex)
    { public EnbRetailMeshPick Query { get; init; } }
    public Piece[] Pieces { get; }
    public EnbRetailMeshPick[] BackgroundQueries { get; }

    internal EnbUtilityHudResources(EnbRetailAssetBundle assets)
    {
        var asset = EnbAuthoredUiAsset.Load(assets, "tsi14a.w3d") ?? throw new InvalidDataException("TSI14A unavailable.");
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(asset);
        EnbW3dWorldBounds? sphere = null;
        if (asset.Resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(asset.Resource, hlod,
                System.Numerics.Matrix4x4.Identity, null, 0, out var bounds, out var error)) throw new InvalidDataException(error);
            sphere = bounds.WorldSphere;
        }
        EnbRetailMeshPick Query(EnbW3dRenderMesh mesh) => EnbUiModelQuery.BindMainView(mesh,
            transforms[asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name).Ordinal]);
        BackgroundQueries = asset.Resource.Meshes.Select(m => Query(m) with { ParentSphere = sphere }).ToArray();
        var meshes = asset.Resource.Meshes.Where(m => m.Name != "BARS_TOP")
            .OrderBy(m => m.Name == "BARS_BOTTOM" ? 0 : m.Name.Contains("ICON") ? 2 : 1).ToArray();
        Pieces = new Piece[meshes.Length];
        for (int i = 0; i < meshes.Length; i++)
        {
            var mesh = meshes[i];
            var drawable = mesh;
            if (mesh.Name == "BARS_BOTTOM")
            {
                // The untextured background's inert exported mapper must not request a textured shader.
                EnbW3dMapperRuntimeProgramCompiler.TryCompile(null, out var disabled, out _);
                EnbW3dCompiledMaterialSemantics Change(EnbW3dCompiledMaterialSemantics s) => s with
                { Mapper0Program = disabled, Mapper1Program = disabled,
                  Uniforms = s.Uniforms with { Mapper0 = EnbW3dMapperUniforms.Identity, Mapper1 = EnbW3dMapperUniforms.Identity },
                  PermutationKey = s.PermutationKey with { Mapper0 = default, Mapper1 = default } };
                drawable = mesh.Copy().SetMaterialPasses(mesh.MaterialPasses.Select(p => p.Copy()
                    .SetSingleBindingSemantics(p.SingleBindingSemantics is { } s ? Change(s) : null)
                    .SetTriangleBindings(p.TriangleBindings.Select(t => t with { Semantics = t.Semantics is { } s ? Change(s) : null }).ToArray())).ToArray());
            }
            string rasterName = mesh.Name switch
            { "ICON03" => "SHIP_BUT", "ICON05" => "CHARACTER_BUT", "ICON02" => "MAP_BUT", "ICON06" => "OPTIONS_BUT", _ => mesh.Name };
            int rasterIndex = Array.FindIndex(meshes, m => m.Name == rasterName);
            if (rasterIndex < 0) throw new InvalidDataException("Missing utility raster anchor: " + rasterName);
            Pieces[i] = new("hud/" + mesh.Name, asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { drawable }) },
                EnbAuthoredUiAsset.MeshBounds(asset, mesh.Name), rasterIndex) { Query = Query(mesh) };
        }
    }
}
