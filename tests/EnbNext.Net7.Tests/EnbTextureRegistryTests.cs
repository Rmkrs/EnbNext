using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbTextureRegistryTests
{
    [Fact]
    public void ExactTextureRetainSurvivesNameRemovalAndDoesNotSelectAReplacementEntry()
    {
        var registry=new EnbCurrentRetailTextureRegistry();
        using var original=registry.Acquire("decal",EnbW3dTextureSamplingSemantics.RetailDefault,null);
        var state=original.State;
        registry.Clear();
        using var fresh=registry.Acquire("decal",EnbW3dTextureSamplingSemantics.RetailDefault,null);
        Assert.True(registry.TryRetain(state,out var retained));
        using(retained)
        {
            Assert.Same(state,retained.State);
            Assert.NotSame(fresh.State,retained.State);
            original.Dispose();
            using var model=new EnbCurrentRetailMeshModelOwner([],loadedTextures:[retained,retained]);
            Assert.Same(state,model.TextureIdentity(0));
            Assert.Same(state,model.TextureIdentity(1));
        }
        Assert.False(registry.TryRetain(new(EnbW3dTextureSamplingSemantics.RetailDefault),out _));
    }
    [Fact]
    public void ConcurrentWorkerCopiesKeepTextureAliveUntilEveryLeaseRetires()
    {
        var registry=new EnbCurrentRetailTextureRegistry();
        using var original=registry.Acquire("terrain-shadow",EnbW3dTextureSamplingSemantics.RetailDefault,null);
        Parallel.For(0,8,_=>
        {
            for(int i=0;i<10000;i++){using var copy=original.Retain();Assert.Same(original.State,copy.State);}
        });
        Assert.Equal(0,registry.CollectUnused());
        original.Dispose();Assert.Equal(1,registry.CollectUnused());
    }
    [Theory]
    [InlineData(0u, 0x11u)]
    [InlineData(0x11u, 0u)]
    [InlineData(0x13u, 0x12u)]
    public void CacheHitsPreserveFirstCreationFormat(uint firstFormat, uint laterFormat)
    {
        var registry = new EnbCurrentRetailTextureRegistry();
        var sampling = EnbW3dTextureSamplingSemantics.RetailDefault;
        using var first = registry.Acquire("shared", new EnbW3dTextureCreationRequest(sampling, firstFormat), null);
        using var later = registry.Acquire("shared", new EnbW3dTextureCreationRequest(sampling, laterFormat), new(8, 0, 0));
        Assert.Same(first.State, later.State);
        Assert.Equal(firstFormat, later.State.RetailCreationFormat);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, first.State.Sampling.AddressU);
        first.Dispose();
        later.Dispose();
        Assert.Equal(1, registry.CollectUnused());
        using var recreated = registry.Acquire("shared", new EnbW3dTextureCreationRequest(sampling, laterFormat), null);
        Assert.Equal(laterFormat, recreated.State.RetailCreationFormat);
    }

    [Fact]
    public void LoadedTextureReferencesLiveUntilLastModelCopyRetires()
    {
        var registry = new EnbCurrentRetailTextureRegistry();
        using var acquisition = registry.Acquire("texture", EnbW3dTextureSamplingSemantics.RetailDefault, null);
        var state = acquisition.State;
        using var prototype = new EnbCurrentRetailMeshModelOwner([], loadedTextures: [acquisition, acquisition]);
        using var copy = prototype.CloneRenderObject();
        copy.EnsureUniqueModel(0);
        acquisition.Dispose();
        prototype.Dispose();
        Assert.Equal(0, registry.CollectUnused());
        Assert.Same(state, copy.TextureIdentity(0));
        Assert.Same(state, copy.TextureIdentity(1));
        copy.Dispose();
        Assert.Equal(1, registry.CollectUnused());
    }

    [Fact]
    public void AcquisitionSharesStateUntilExplicitUnusedCollection()
    {
        var registry = new EnbCurrentRetailTextureRegistry();
        var first = registry.Acquire("lights_all.tga", EnbW3dTextureSamplingSemantics.RetailDefault, null);
        var original = first.State;
        using var second = registry.Acquire("lights_all.tga",
            EnbW3dTextureSamplingSemantics.RetailDefault with { RetailCreationSelector = 1 }, new(12, 0, 0));
        Assert.Same(original, second.State);
        Assert.Equal(0, original.Sampling.RetailCreationSelector);
        Assert.Equal(EnbRenderTextureMipFilter.None, original.Sampling.CurrentRetailMipOverride);
        first.Dispose();
        first.Dispose();
        Assert.Equal(0, registry.CollectUnused());
        second.Dispose();
        Assert.Equal(1, registry.Count);
        Assert.Equal(1, registry.CollectUnused());
        using var next = registry.Acquire("lights_all.tga", EnbW3dTextureSamplingSemantics.RetailDefault, null);
        Assert.NotSame(original, next.State);
        Assert.Null(next.State.Sampling.CurrentRetailMipOverride);
    }

    [Fact]
    public void ModelCopyRetainSurvivesRegistryClearAndIndependentReacquisition()
    {
        var registry = new EnbCurrentRetailTextureRegistry();
        var loaded = registry.Acquire("texture", EnbW3dTextureSamplingSemantics.RetailDefault, new(8, 0, 0));
        using var copy = loaded.Retain();
        var original = loaded.State;
        loaded.Dispose();
        registry.Clear();
        using var replacement = registry.Acquire("texture", EnbW3dTextureSamplingSemantics.RetailDefault, null);
        Assert.Same(original, copy.State);
        Assert.NotSame(original, replacement.State);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, copy.State.Sampling.AddressU);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, replacement.State.Sampling.AddressU);
        copy.Dispose();
        Assert.Equal(0, registry.CollectUnused());
        Assert.Throws<ObjectDisposedException>(() => loaded.Retain());
    }
}
