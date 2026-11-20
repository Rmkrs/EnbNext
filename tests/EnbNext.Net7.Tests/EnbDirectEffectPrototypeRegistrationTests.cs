using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbDirectEffectPrototypeRegistrationTests
{
    [Fact]
    public void MissileKeepsFirstQualifiedPrototypeAndResolvesEveryLod()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var source = assets.BaseAssetResolver!.Resolve(232).W3d!;
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var raw, out var error), error);
        var prepared = EnbCurrentRetailRObjEffectAsset.Load(assets, 232).Resource!;
        Assert.Equal(59, raw.Meshes.Count);
        Assert.Equal(53, prepared.Meshes.Count);
        raw=EnbCurrentRetailMeshTexturePreparation.Prepare(raw,assets);
        foreach (var group in raw.Meshes.GroupBy(m => m.ContainerName.Length == 0 ? m.Name : m.ContainerName + "." + m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var chosen = Assert.Single(prepared.Meshes, m => (m.ContainerName.Length == 0 ? m.Name : m.ContainerName + "." + m.Name).Equals(group.Key, StringComparison.OrdinalIgnoreCase));
            AssertSamePrototype(group.First(), chosen);
        }
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(prepared,
            Assert.Single(prepared.HierarchicalLods), _ => true, out _, out error), error);
    }

    internal static void AssertSamePrototype(EnbW3dRenderMesh first, EnbW3dRenderMesh chosen)
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        Assert.Equal(first.Ordinal, chosen.Ordinal);
        Assert.Equal(first.Positions, chosen.Positions);
        Assert.Equal(first.Normals, chosen.Normals);
        Assert.Equal(first.Indices, chosen.Indices);
        Assert.Equal(first.BoneIndices, chosen.BoneIndices);
        Assert.Equal(first.TriangleMetadata, chosen.TriangleMetadata);
        Assert.Equal(first.Attributes, chosen.Attributes);
        Assert.Equal(first.BoundingBoxMin, chosen.BoundingBoxMin);
        Assert.Equal(first.BoundingBoxMax, chosen.BoundingBoxMax);
        Assert.Equal(first.BoundingRadius, chosen.BoundingRadius);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.MaterialPasses, options), System.Text.Json.JsonSerializer.Serialize(chosen.MaterialPasses, options));
        if(first.CurrentRetailLoadedGeometry is {} geometry)
        {
            Assert.NotNull(chosen.CurrentRetailLoadedGeometry);
            Assert.Equal(geometry.Positions, chosen.CurrentRetailLoadedGeometry.Positions);
            Assert.Equal(geometry.TriangleIndices.ToArray(), chosen.CurrentRetailLoadedGeometry.TriangleIndices.ToArray());
        }
        else Assert.Null(chosen.CurrentRetailLoadedGeometry);
    }
}
