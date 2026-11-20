using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMeshBatchStateTests
{
    [Fact]
    public void DrawnSoleOwnerCopiesButResetRemovesTheForceCopyCondition()
    {
        var state = new EnbCurrentRetailMeshBatchState();
        object material = new(), mapper = new();
        using var owner = state.CreateModel([(material, mapper, null)]);
        var first = owner.ModelIdentity;
        state.PrepareRetainedMaterialMutation(owner,0);
        Assert.Same(first, owner.ModelIdentity);
        Admit(state, owner);
        state.PrepareRetainedMaterialMutation(owner,0);
        Assert.NotSame(first, owner.ModelIdentity);
        Assert.False(state.Descriptors.HasModelLinks(first));
        var second = owner.ModelIdentity;
        Admit(state, owner);
        state.ObserveResetRevision(1);
        Assert.Empty(state.Containers.SnapshotFlushOrder());
        state.PrepareRetainedMaterialMutation(owner,0);
        Assert.Same(second, owner.ModelIdentity);
        Admit(state, owner);
        state.ObserveResetRevision(1);
        Assert.True(state.Descriptors.HasModelLinks(second));
        state.ObserveResetRevision(2);
        Assert.False(state.Descriptors.HasModelLinks(second));
    }

    [Fact]
    public void DeferredContainerDestructionInvalidatesLateModelLinks()
    {
        var state = new EnbCurrentRetailMeshBatchState();
        var first = state.CreateModel([]);
        var container = Admit(state, first);
        first.Dispose();
        using var late = state.CreateModel([]);
        Assert.Same(container, Admit(state, late));
        Assert.True(state.Descriptors.HasModelLinks(late.ModelIdentity));
        state.EndFrame();
        Assert.False(state.Descriptors.HasModelLinks(late.ModelIdentity));
        Assert.NotSame(container, Admit(state, late));
    }

    private static EnbCurrentRetailMeshContainerRegistry.Container Admit(
        EnbCurrentRetailMeshBatchState state, EnbCurrentRetailMeshModelOwner owner)
    {
        var container = state.Containers.Register(owner.ModelIdentity, 0x112, false, false, 3, 3);
        state.Descriptors.GetOrAdd(owner.ModelIdentity, container, 0, null, 0, null, null);
        return container;
    }
}
