using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4TLegacyMeshInfluenceTests
{
    [Fact]
    public void RenderCompilerAppliesLegacyMeshInfluenceShiftAfterPreservingAuthoredIndices()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            meshVersion: 0x0002_FFFFu,
            vertexInfluences: [0, 1, 2]);

        EnbW3dMesh sourceMesh = Assert.Single(source.Model.Meshes);
        Assert.Equal(
            new ushort[] { 0, 1, 2 },
            sourceMesh.VertexInfluences.Select(static influence => influence.BoneIndex).ToArray());

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.True(mesh.HasLegacySyntheticRootInfluenceShift);
        Assert.Equal(new ushort[] { 1, 2, 3 }, mesh.BoneIndices);
    }

    [Fact]
    public void RenderCompilerDoesNotShiftMeshInfluencesAtVersion30000Boundary()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            meshVersion: 0x0003_0000u,
            vertexInfluences: [0, 1, 2]);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.False(mesh.HasLegacySyntheticRootInfluenceShift);
        Assert.Equal(new ushort[] { 0, 1, 2 }, mesh.BoneIndices);
    }

    [Fact]
    public void LegacyInfluenceShiftUsesRetailU16WrapSemantics()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            meshVersion: 0x0002_FFFFu,
            vertexInfluences: [ushort.MaxValue, 0, 1]);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal(new ushort[] { 0, 1, 2 }, mesh.BoneIndices);
    }
}
