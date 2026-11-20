using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z17CurrentRetailHAnimRegistryIdentityTests
{
    [Fact]
    public void RegistryIdentityIsHierarchyDotRawAnimationName()
    {
        Assert.Equal(
            "TSG02.TSG02-1",
            EnbCurrentRetailRslHAnimResolver.BuildRegistryName("TSG02-1", "TSG02"));
    }

    [Fact]
    public void RegistryIdentityDoesNotSmartDeduplicateAlreadyQualifiedRawName()
    {
        Assert.Equal(
            "TSG02.TSG02.TSG02-1",
            EnbCurrentRetailRslHAnimResolver.BuildRegistryName("TSG02.TSG02-1", "TSG02"));
    }

    [Fact]
    public void RetainedRawHeaderAnimationResolvesByCurrentRetailRegistryIdentity()
    {
        EnbW3dRenderHierarchyAnimation rawAnimation = Animation("TSG02-1", "TSG02", 12, 24);
        EnbW3dRenderResource retained = Resource("tsg0201.w3d", rawAnimation);
        var noAuthoredFile = new EnbResolvedRslFileDependency(
            string.Empty,
            EnbRslFileDependencyStatus.NotAuthored);

        Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
            retained,
            noAuthoredFile,
            null,
            "TSG02.TSG02-1",
            out EnbCurrentRetailRslHAnimResolution resolution,
            out string? diagnostic), diagnostic);

        Assert.Same(rawAnimation, resolution.Animation);
        Assert.Equal("TSG02-1", resolution.Animation.Name);
        Assert.Equal("TSG02.TSG02-1", resolution.HAnimName);
        Assert.Equal("retained:tsg0201.w3d", resolution.Source);
    }

    [Fact]
    public void RetainedRawNameThatWouldProduceDifferentRegistryIdentityDoesNotMatch()
    {
        EnbW3dRenderResource retained = Resource(
            "tsg0201.w3d",
            Animation("TSG02.TSG02-1", "TSG02", 12, 24));
        var noAuthoredFile = new EnbResolvedRslFileDependency(
            string.Empty,
            EnbRslFileDependencyStatus.NotAuthored);

        Assert.False(EnbCurrentRetailRslHAnimResolver.TryResolve(
            retained,
            noAuthoredFile,
            null,
            "TSG02.TSG02-1",
            out _,
            out string? diagnostic));

        Assert.Contains("TSG02.TSG02.TSG02-1", diagnostic ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static EnbW3dRenderHierarchyAnimation Animation(
        string name,
        string hierarchyName,
        uint numFrames,
        uint frameRate)
        => new(
            Ordinal: 0,
            Version: 0x0004_0002u,
            Name: name,
            HierarchyName: hierarchyName,
            NumFrames: numFrames,
            FrameRate: frameRate,
            HasLegacySyntheticRootChannelShift: false,
            Channels: Array.Empty<EnbW3dRenderAnimationChannel>(),
            BitChannels: Array.Empty<EnbW3dRenderAnimationBitChannel>());

    private static EnbW3dRenderResource Resource(
        string logicalName,
        params EnbW3dRenderHierarchyAnimation[] animations)
        => new(
            new EnbW3dRenderResourceKey(logicalName, "test.mix", 0, 0),
            logicalName,
            Array.Empty<EnbW3dRenderMesh>(),
            Array.Empty<EnbW3dRenderHierarchy>(),
            animations,
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            Array.Empty<EnbW3dRenderHlod>());
}
