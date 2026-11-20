using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMeshContainerRegistryTests
{
    [Fact]
    public void ModelInstancesShareAdmissionAndRetirementDoesNotReclaimBufferSpace()
    {
        var registry = new EnbCurrentRetailMeshContainerRegistry();
        object a = new(), b = new();
        var first = registry.Register(a, 0x112, false, false, 3000, 3000);
        Assert.Same(first, registry.Register(a, 0x112, false, false, 3000, 3000));
        Assert.Same(first, registry.Register(b, 0x112, false, false, 1000, 3000));
        registry.Remove(a);
        registry.EndFrame();
        Assert.Equal(4000u, first.UsedVertices);
        Assert.Equal(6000u, first.UsedIndices);
        Assert.NotSame(first, registry.Register(new(), 0x112, false, false, 1, 3));
    }

    [Fact]
    public void FirstCompatibleContainerChecksBothCapacitiesAndSortingBranch()
    {
        var registry = new EnbCurrentRetailMeshContainerRegistry();
        var first = registry.Register(new(), 0x112, false, false, 1000, 11997);
        var next = registry.Register(new(), 0x112, false, false, 1000, 6);
        Assert.NotSame(first, next);
        Assert.Same(first, registry.Register(new(), 0x112, false, false, 1000, 3));
        Assert.NotSame(first, registry.Register(new(), 0x112, false, true, 1, 3));
        var large = registry.Register(new(), 0x152, false, false, 5000, 15000);
        Assert.Equal(5000u, large.VertexCapacity);
        Assert.Equal(15000u, large.IndexCapacity);
    }

    [Fact]
    public void EmptyFvfSlotIsReusedBeforeLaterMatchingSlotAndSkinsRenderLast()
    {
        var registry = new EnbCurrentRetailMeshContainerRegistry();
        object old = new();
        var skin = registry.Register(new(), 0, true, false, 9000, 3);
        registry.Register(old, 0x112, false, false, 3, 3);
        var later = registry.Register(new(), 0x152, false, false, 3, 3);
        registry.Remove(old);
        registry.EndFrame();
        var reused = registry.Register(new(), 0x152, false, false, 3, 3);
        Assert.NotSame(later, reused);
        Assert.Equal(new[] { reused, later, skin }, registry.SnapshotFlushOrder());
        Assert.Equal(0u, skin.UsedVertices);
        Assert.Equal(0x112u, skin.Fvf);
    }

    [Fact]
    public void QueuedDestructionInvalidatesLateRegistrationAndClearResetsOrder()
    {
        var registry = new EnbCurrentRetailMeshContainerRegistry();
        object old = new(), late = new();
        var container = registry.Register(old, 0x112, false, false, 3, 3);
        registry.Remove(old);
        Assert.Same(container, registry.Register(late, 0x112, false, false, 3, 3));
        registry.EndFrame();
        Assert.Empty(registry.SnapshotFlushOrder());
        Assert.NotSame(container, registry.Register(late, 0x112, false, false, 3, 3));
        registry.Clear();
        Assert.Empty(registry.SnapshotFlushOrder());
    }
}
