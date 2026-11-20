using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4MMeshBoundsStaticSortTests
{
    [Fact]
    public void MeshHeader3PreservesAuthoredBoundsAndSortLevelThroughRendererCompilation()
    {
        var boundingBoxMin = new Vector3(-11f, -22f, -33f);
        var boundingBoxMax = new Vector3(44f, 55f, 66f);
        var boundingSphereCenter = new Vector3(7f, 8f, 9f);
        const uint authoredSortLevel = 0xA5A5_0020u;

        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            authoredSortLevel: authoredSortLevel,
            boundingBoxMin: boundingBoxMin,
            boundingBoxMax: boundingBoxMax,
            boundingSphereCenter: boundingSphereCenter);

        EnbW3dMesh decoded = Assert.Single(source.Model.Meshes);
        Assert.Equal(authoredSortLevel, decoded.AuthoredSortLevel);
        Assert.Equal(boundingBoxMin, decoded.BoundingBoxMin);
        Assert.Equal(boundingBoxMax, decoded.BoundingBoxMax);
        Assert.Equal(boundingSphereCenter, decoded.BoundingSphereCenter);
        Assert.Equal(5.5f, decoded.BoundingRadius, 3);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal(authoredSortLevel, mesh.AuthoredSortLevel);
        Assert.Equal(boundingBoxMin, mesh.BoundingBoxMin);
        Assert.Equal(boundingBoxMax, mesh.BoundingBoxMax);
        Assert.Equal(boundingSphereCenter, mesh.BoundingSphereCenter);
        Assert.Equal(5.5f, mesh.BoundingRadius, 3);
        Assert.Equal((sbyte)32, mesh.StaticSort.RuntimeLevel);
        Assert.Equal(EnbW3dStaticSortDisposition.DeferredBucket, mesh.StaticSort.Disposition);
    }

    [Theory]
    [InlineData(0x0000_0000u, (sbyte)0, EnbW3dStaticSortDisposition.Immediate)]
    [InlineData(0x0000_0001u, (sbyte)1, EnbW3dStaticSortDisposition.DeferredBucket)]
    [InlineData(0xFFFF_FF20u, (sbyte)32, EnbW3dStaticSortDisposition.DeferredBucket)]
    [InlineData(0x0000_0021u, (sbyte)33, EnbW3dStaticSortDisposition.RejectedByCurrentRetailBucketRange)]
    [InlineData(0x0000_00FFu, (sbyte)-1, EnbW3dStaticSortDisposition.RejectedByCurrentRetailBucketRange)]
    [InlineData(0x0000_0100u, (sbyte)0, EnbW3dStaticSortDisposition.Immediate)]
    public void StaticSortUsesRetailLowSignedByteAndFiniteOneThroughThirtyTwoBuckets(
        uint authoredValue,
        sbyte runtimeLevel,
        EnbW3dStaticSortDisposition disposition)
    {
        EnbW3dStaticSortSemantics semantics = EnbW3dStaticSortSemantics.FromAuthoredValue(authoredValue);

        Assert.Equal(authoredValue, semantics.AuthoredValue);
        Assert.Equal(runtimeLevel, semantics.RuntimeLevel);
        Assert.Equal(disposition, semantics.Disposition);
        Assert.Equal(disposition == EnbW3dStaticSortDisposition.DeferredBucket, semantics.IsDeferred);
        int? expectedFlushOrdinal = disposition == EnbW3dStaticSortDisposition.DeferredBucket
            ? EnbW3dStaticSortSemantics.MaximumDeferredLevel - runtimeLevel
            : null;
        Assert.Equal(expectedFlushOrdinal, semantics.DeferredFlushOrdinal);
        Assert.Equal(1, EnbW3dStaticSortSemantics.MinimumDeferredLevel);
        Assert.Equal(32, EnbW3dStaticSortSemantics.MaximumDeferredLevel);
    }

    [Fact]
    public void R4MOrderingContractDoesNotTurnBlendClassIntoInventedDepthSortPolicy()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dDrawPackets.cs"));

        Assert.Contains("EnbW3dStaticSortSemantics", source, StringComparison.Ordinal);
        Assert.Contains("MeshGeometryClass::SORT", source, StringComparison.Ordinal);
        Assert.DoesNotContain("cameraDistance", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("depth-sort blended", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
