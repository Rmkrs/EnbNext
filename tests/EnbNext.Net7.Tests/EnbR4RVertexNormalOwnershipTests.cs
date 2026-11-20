using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4RVertexNormalOwnershipTests
{
    [Fact]
    public void R4RMissingAuthoredNormalChunkMaterializesRetailZeroNormalStream()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            includeNormals: false);

        EnbW3dMesh decoded = Assert.Single(source.Model.Meshes);
        Assert.Empty(decoded.Normals);
        Assert.Null(decoded.AuthoredNormalChunkType);

        Assert.True(
            EnbW3dRenderResourceCompiler.TryCompile(
                source,
                out EnbW3dRenderResource resource,
                out string? diagnostic),
            diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal(mesh.Positions.Count, mesh.Normals.Count);
        Assert.All(mesh.Normals, static normal => Assert.Equal(Vector3.Zero, normal));
        Assert.Equal(EnbW3dRenderNormalSource.RetailZeroInitializedMissingChunk, mesh.NormalSource);
        Assert.Null(mesh.AuthoredNormalChunkType);
    }

    [Fact]
    public void R4RAuthoredNormalChunkRemainsAuthoritativeAtRenderBoundary()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0);

        Assert.True(
            EnbW3dRenderResourceCompiler.TryCompile(
                source,
                out EnbW3dRenderResource resource,
                out string? diagnostic),
            diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ], mesh.Normals);
        Assert.Equal(EnbW3dRenderNormalSource.AuthoredNormalChunk, mesh.NormalSource);
        Assert.Equal<uint?>(0x0003u, mesh.AuthoredNormalChunkType);
    }
}
