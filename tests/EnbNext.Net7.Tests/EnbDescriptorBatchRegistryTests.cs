using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbDescriptorBatchRegistryTests
{
    [Fact]
    public void SharedDescriptorsMergeAcrossModelsAndRetireOnLastOwner()
    {
        var registry = new EnbCurrentRetailDescriptorBatchRegistry();
        object a = new(), b = new(), container = new(), texture = new();
        var first = registry.GetOrAdd(a, container, 0, null, 12, texture, null);
        Assert.Same(first, registry.GetOrAdd(b, container, 0, null, 12, texture, null));
        registry.RemoveModel(a);
        Assert.Same(first, registry.GetOrAdd(b, container, 0, null, 12, texture, null));
        registry.RemoveModel(b);
        Assert.NotSame(first, registry.GetOrAdd(a, container, 0, null, 12, texture, null));
    }

    [Fact]
    public void TextureSlotsShaderPassAndContainerSeparateBatches()
    {
        var registry = new EnbCurrentRetailDescriptorBatchRegistry();
        object model = new(), container = new(), texture = new();
        var first = registry.GetOrAdd(model, container, 0, null, 12, texture, null);
        Assert.NotSame(first, registry.GetOrAdd(model, container, 0, null, 12, new object(), null));
        Assert.NotSame(first, registry.GetOrAdd(model, container, 0, null, 12, null, texture));
        Assert.NotSame(first, registry.GetOrAdd(model, container, 0, null, 13, texture, null));
        Assert.NotSame(first, registry.GetOrAdd(model, container, 1, null, 12, texture, null));
        Assert.NotSame(first, registry.GetOrAdd(model, new object(), 0, null, 12, texture, null));
        registry.RemoveContainer(container);
        Assert.NotSame(first, registry.GetOrAdd(model, container, 0, null, 12, texture, null));
    }

    [Fact]
    public void RemovingOneContainerPreservesOtherContainerLinks()
    {
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();
        object model=new(),otherModel=new(),a=new(),b=new();
        registry.GetOrAdd(model,a,0,null,0,null,null);
        var retained=registry.GetOrAdd(model,b,0,null,0,null,null);
        registry.GetOrAdd(otherModel,b,0,null,0,null,null);
        registry.RemoveContainer(a);
        Assert.True(registry.HasModelLinks(model));
        registry.RemoveModel(model);
        Assert.False(registry.HasModelLinks(model));
        Assert.Same(retained,registry.GetOrAdd(otherModel,b,0,null,0,null,null));
        registry.RemoveContainer(b);
        Assert.False(registry.HasModelLinks(otherModel));
    }

    [Fact]
    public void CrossModelBatchActivationChangesFlushOrder()
    {
        var registry = new EnbCurrentRetailDescriptorBatchRegistry();
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object a = new(), b = new(), container = new(), texture = new();
        queue.Enqueue(0, 0, registry.GetOrAdd(a,container,0,null,1,texture,null), "a-shared");
        queue.Enqueue(0, 0, registry.GetOrAdd(a,container,0,null,2,texture,null), "a-other");
        queue.Enqueue(0, 0, registry.GetOrAdd(b,container,0,null,1,texture,null), "b-shared");
        Assert.Equal(new[] { "a-other", "b-shared", "a-shared" }, queue.Flush());
    }
}
