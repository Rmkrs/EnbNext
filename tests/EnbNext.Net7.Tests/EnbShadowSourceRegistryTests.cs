using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbShadowSourceRegistryTests
{
    private sealed record Source(int Value);

    [Fact]
    public void TerrainVisitsDynamicThenStationaryInReverseRegistrationOrder()
    {
        var registry = new EnbCurrentRetailShadowSourceRegistry<Source>();
        Source dynamic1 = new(1), dynamic2 = new(2), stationary1 = new(3), stationary2 = new(4);
        registry.Register(stationary1, true);
        registry.Register(dynamic1, false);
        registry.Register(stationary2, true);
        registry.Register(dynamic2, false);
        Assert.False(registry.Register(dynamic1, true));
        Assert.Equal(new[] { dynamic2, dynamic1, stationary2, stationary1 }, registry.SnapshotTerrainSources());
    }

    [Fact]
    public void MembershipUsesIdentityAndFrameReferencesSurviveRemoval()
    {
        var registry = new EnbCurrentRetailShadowSourceRegistry<Source>();
        Source first = new(1), equalValue = new(1);
        registry.Register(first, false);
        registry.Register(equalValue, false);
        var frame = registry.SnapshotTerrainSources();
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Remove(first));
        Assert.Same(equalValue, Assert.Single(registry.SnapshotTerrainSources()));
        Assert.Same(first, frame[1]);
        Assert.False(registry.Remove(first));
        registry.Clear();
        Assert.Empty(registry.SnapshotTerrainSources());
        Assert.Equal(2, frame.Count);
    }

    [Fact]
    public void RemoveAndReaddRetainsWrapperProjectionWithoutInvalidatingIt()
    {
        var source = new EnbCurrentRetailShadowSourceProjection();
        var bounds = new EnbW3dAxisAlignedBounds(Vector3.Zero, Vector3.One);
        source.Update(bounds, Matrix4x4.Identity, Vector3.UnitZ, 64, false);
        var projection = source.Projection;
        var registry = new EnbCurrentRetailShadowSourceRegistry<EnbCurrentRetailShadowSourceProjection>();
        registry.Register(source, true);
        registry.Remove(source);
        registry.Register(source, true);
        Assert.Same(source, Assert.Single(registry.SnapshotTerrainSources()));
        Assert.Same(projection, source.Projection);
        Assert.False(source.TransformDirty);
    }
}
