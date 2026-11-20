using EnbNext.Assets;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbBaseAssetPredecodeTests
{
    private static EnbRetailBaseAssetResolver Resolver()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadBaseAssetsText("[BASE-48]\nRSLID=2\nLINKID=7\nRSID=3");
        return new(catalog,null);
    }
    [Fact]
    public void MetadataOnlySectionReturnsNoRenderObjectRatherThanMissingOrNullPrototype()
    {
        var catalog=new EnbRetailPresentationCatalog();
        catalog.LoadBaseAssetsText("[BASE-1]\nIcon=icon.tga\nDescription=Icon\n[BASE-2]\nDescription=Unused\n[BASE-3]\nNULLOBJECT=1");
        var resolver=new EnbRetailBaseAssetResolver(catalog,null);
        foreach(int id in new[]{1,2})
        {
            var result=resolver.Resolve(id);
            Assert.Equal(EnbBaseAssetDefinitionKind.NoRenderObject,result.Definition.Kind);
            Assert.Equal(EnbBaseAssetResolutionStatus.NoRenderObject,result.Status);
            Assert.True(result.IsResolved);Assert.Null(result.W3d);Assert.Null(result.Diagnostic);
        }
        Assert.Equal(EnbBaseAssetResolutionStatus.NullObject,resolver.Resolve(3).Status);
        Assert.Equal(EnbBaseAssetResolutionStatus.MissingDefinition,resolver.Resolve(4).Status);
    }
    [Fact]
    public async Task DecodeOrderIsSerialAndConsumptionRemainsDemandOrdered()
    {
        var order=new List<int>();var resolver=Resolver();
        using var queue=new EnbBaseAssetPredecode(id=>{order.Add(id);return resolver.Resolve(id);});
        queue.Request(48);queue.Request(7);queue.Request(48);queue.Request(9);
        await queue.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[]{48,7,9},order);
        // Completion order does not select or publish any render object.
        foreach(var id in new[]{9,48,7})
        {Assert.True(queue.TryGetCompleted(id,out var result));Assert.Equal(id,result.Selector);Assert.Equal(resolver.Resolve(id).Status,result.Status);Assert.Equal(resolver.Resolve(id).Rsl?.InitialStateCommands,result.Rsl?.InitialStateCommands);}
    }
    [Fact]
    public async Task PendingAndFailedSpeculationNeverBlockOrPoisonDemand()
    {
        using var release=new ManualResetEventSlim();
        var resolver=Resolver();
        using var queue=new EnbBaseAssetPredecode(id=>{release.Wait(TimeSpan.FromSeconds(5));if(id==7)throw new IOException();return resolver.Resolve(id);});
        queue.Request(7);queue.Request(48);
        Assert.False(queue.TryGetCompleted(7,out _));
        release.Set();await queue.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(queue.TryGetCompleted(7,out _));Assert.True(queue.TryGetCompleted(48,out _));
        Assert.Equal(7,resolver.Resolve(7).Selector);
    }
    [Fact]
    public async Task RetiredQueueCannotPublishAndSkipsQueuedWork()
    {
        using var started=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var decoded=new List<int>();var resolver=Resolver();
        using var queue=new EnbBaseAssetPredecode(id=>{decoded.Add(id);started.Set();release.Wait(TimeSpan.FromSeconds(5));return resolver.Resolve(id);});
        queue.Request(48);Assert.True(started.Wait(TimeSpan.FromSeconds(5)));queue.Request(7);
        queue.Dispose();release.Set();await queue.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[]{48},decoded);Assert.False(queue.TryGetCompleted(48,out _));
    }
}
