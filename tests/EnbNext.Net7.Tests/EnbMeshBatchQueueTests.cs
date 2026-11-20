using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbMeshBatchQueueTests
{
    [Fact]
    public void UnchangedDescriptorQueueFramesAllocateNothingAndReleaseRetiredCategories()
    {
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();object model=new(),container=new();
        var batch=registry.GetOrAdd(model,container,0,null,1,null,null);
        var other=registry.GetOrAdd(model,container,0,null,2,null,null);
        var queue=new EnbCurrentRetailMeshBatchQueue<int>();var output=new List<int>();
        for(int i=0;i<20;i++){queue.Enqueue(0,0,batch,1);queue.Enqueue(0,0,other,2);queue.FlushInto(output);output.Clear();}
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<100;i++){queue.Enqueue(0,0,batch,1);queue.Enqueue(0,0,other,2);queue.FlushInto(output);output.Clear();}
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        Assert.Equal(2,queue.CategoryBindingBuildCount);Assert.Equal(0,queue.CategoryLookupCount);
        var weak=RetireCategory(registry,queue);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.False(weak.IsAlive);
        GC.KeepAlive(registry);GC.KeepAlive(queue);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RetireCategory(EnbCurrentRetailDescriptorBatchRegistry registry,EnbCurrentRetailMeshBatchQueue<int> queue)
    {
        object model=new();var batch=registry.GetOrAdd(model,new(),0,null,1,null,null);
        queue.Enqueue(0,0,batch,1);registry.RemoveModel(model);queue.Clear();return new(batch);
    }

    [Fact]
    public void DescriptorCategoriesRetainBindingsAndKeepIndependentViewQueues()
    {
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();object model=new(),container=new();
        var a=registry.GetOrAdd(model,container,0,null,1,null,null);
        var b=registry.GetOrAdd(model,container,0,null,2,null,null);
        var first=new EnbCurrentRetailMeshBatchQueue<string>();var second=new EnbCurrentRetailMeshBatchQueue<string>();
        for(int frame=0;frame<100;frame++)
        {
            first.Enqueue(0,0,a,"a1");second.Enqueue(0,0,a,"other");first.Enqueue(0,0,b,"b");first.Enqueue(0,0,a,"a2");
            Assert.Equal(new[]{"b","a2","a1"},first.Flush());Assert.Equal(new[]{"other"},second.Flush());
        }
        Assert.Equal(2,first.CategoryBindingBuildCount);Assert.Equal(1,second.CategoryBindingBuildCount);
        Assert.Equal(0,first.CategoryLookupCount);Assert.Equal(0,second.CategoryLookupCount);
        first.Enqueue(0,0,a,"queued before removal");registry.RemoveModel(model);
        var replacement=registry.GetOrAdd(model,container,0,null,1,null,null);Assert.NotSame(a,replacement);
        first.Enqueue(0,0,replacement,"new generation");
        Assert.Equal(new[]{"new generation","queued before removal"},first.Flush());
        first.Enqueue(1,1,a,"different pass");first.Enqueue(0,0,a,"original pass");
        Assert.Equal(new[]{"original pass","different pass"},first.Flush());
    }

    [Fact]
    public void SharedDescriptorBindingsDoNotCrossConcurrentViewQueues()
    {
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();
        var batch=registry.GetOrAdd(new(),new(),0,null,1,null,null);
        Parallel.For(0,4,view=>
        {
            var queue=new EnbCurrentRetailMeshBatchQueue<int>();
            for(int frame=0;frame<500;frame++)
            {
                queue.Enqueue(0,0,batch,view);queue.Enqueue(0,0,batch,view+10);
                Assert.Equal(new[]{view+10,view},queue.Flush());
            }
            Assert.Equal(1,queue.CategoryBindingBuildCount);Assert.Equal(0,queue.CategoryLookupCount);
        });
    }

    [Fact]
    public void DescriptorDoesNotRetainTheViewQueueAfterDrain()
    {
        var registry=new EnbCurrentRetailDescriptorBatchRegistry();var batch=registry.GetOrAdd(new(),new(),0,null,1,null,null);
        var weak=QueueAndRelease(batch);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);GC.KeepAlive(batch);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference QueueAndRelease(object batch)
    {
        var queue=new EnbCurrentRetailMeshBatchQueue<object>();queue.Enqueue(0,0,batch,new());queue.Clear();return new(queue);
    }

    [Fact]
    public void SkinHiddenPassUsesItsNormalExtraQueue()
    {
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object batch = new();
        queue.EnqueueEffectPass(0,false,6,"hidden ordinary");
        queue.EnqueueEffectPass(0,false,0,"ordinary extra");
        queue.EnqueueEffectPass(1,true,6,"hidden skin");
        queue.Enqueue(0,0,batch,"ordinary base");
        queue.Enqueue(1,0,batch,"skin base");
        Assert.Equal(new[] {"ordinary base","ordinary extra","skin base","hidden skin","hidden ordinary"},queue.Flush());
    }

    [Fact]
    public void ContextBitFourPassesWaitForEveryOrdinaryContainer()
    {
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object batch = new();
        queue.EnqueueDeferredAdditionalPass(1, "late1a");
        queue.EnqueueDeferredAdditionalPass(0, "late0a");
        queue.Enqueue(1, 0, batch, "base1");
        queue.EnqueueAdditionalPass(0, "extra0");
        queue.EnqueueDeferredAdditionalPass(1, "late1b");
        queue.Enqueue(0, 0, batch, "base0");
        queue.EnqueueDeferredAdditionalPass(2, "late2");
        Assert.Equal(new[] { "base0", "extra0", "base1", "late0a", "late1a", "late1b", "late2" }, queue.Flush());
        Assert.Empty(queue.Flush());
    }

    [Fact]
    public void AdditionalPassesFollowEachContainersDescriptorsInFifoOrder()
    {
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object a = new(), b = new();
        queue.EnqueueAdditionalPass(1, "container1 extra1");
        queue.EnqueueAdditionalPass(0, "container0 extra1");
        queue.Enqueue(1, 0, a, "container1 base");
        queue.Enqueue(0, 1, a, "container0 pass1");
        queue.EnqueueAdditionalPass(0, "container0 extra2");
        queue.Enqueue(0, 0, a, "container0 base1");
        queue.Enqueue(0, 0, b, "container0 base2");
        queue.EnqueueAdditionalPass(2, "only extra");
        Assert.Equal(new[] { "container0 base2", "container0 base1", "container0 pass1",
            "container0 extra1", "container0 extra2", "container1 base", "container1 extra1", "only extra" }, queue.Flush());
        Assert.Empty(queue.Flush());
    }

    [Fact]
    public void RepeatedBatchKeepsFirstActivationPositionAndReversesItsInstances()
    {
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object a = new(), b = new();
        queue.Enqueue(0, 0, a, "A1");
        queue.Enqueue(0, 0, b, "B1");
        queue.Enqueue(0, 0, a, "A2");
        Assert.Equal(new[] { "B1", "A2", "A1" }, queue.Flush());
        Assert.Empty(queue.Flush());
        // A new static-sort bucket activates batches afresh.
        queue.Enqueue(0, 0, b, "B2");
        queue.Enqueue(0, 0, a, "A3");
        Assert.Equal(new[] { "A3", "B2" }, queue.Flush());
    }

    [Fact]
    public void ContainerTraversalPrecedesPassOrderAndValueEqualityDoesNotMergeIdentities()
    {
        var queue = new EnbCurrentRetailMeshBatchQueue<string>();
        object a = new Identity(1), b = new Identity(1);
        queue.Enqueue(1, 0, a, "later container");
        queue.Enqueue(0, 1, a, "second pass");
        queue.Enqueue(0, 0, a, "first identity");
        queue.Enqueue(0, 0, b, "equal value, distinct batch");
        Assert.Equal(new[] { "equal value, distinct batch", "first identity", "second pass", "later container" }, queue.Flush());
    }

    private sealed record Identity(int Value);
}
