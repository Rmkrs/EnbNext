using System.Reflection;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbEffectEndpointIndexTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private sealed class Owner(EnbSceneObjectSnapshot definition)
    {public EnbSceneObjectSnapshot Definition=definition;public bool Detached,Released;}
    private static EnbSceneObjectSnapshot Template()
    {
        var world=new Net7WorldState();world.MaterializeCreate(new(7,1,10,1,0,0,0));
        return new EnbPresentationSnapshotPublisher().Publish(world,null,0).Objects.Single();
    }
    private static EnbRetainedRenderScene<Owner> Scene()=>new(o=>new(o),(s,o)=>s.Definition=o,s=>s.Released=true,s=>s.Detached=true,indexGameIds:true);
    private static void Send(EnbRetainedRenderScene<Owner> scene,EnbRenderSceneSubscription subscription,ulong sequence,EnbRenderObjectChange change)
    {
        typeof(EnbRenderSceneSubscription).GetMethod("Publish",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(subscription,[new EnbRenderSceneChangeBatch(sequence,[change])]);
        scene.ApplyThrough(subscription,sequence);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (long Bytes,bool AllFound,ulong Sum) MeasureQueries(EnbRetainedRenderScene<Owner> scene)
    {
        bool allFound=true;ulong sum=0;
        long start=GC.GetAllocatedBytesForCurrentThread();
        for(uint i=0;i<100000;i++){allFound &= scene.TryFindByGameId(i%300+1,out var node);sum+=node.Definition.RenderIdentity;}
        return (GC.GetAllocatedBytesForCurrentThread()-start,allFound,sum);
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference RetireOwner(EnbRetainedRenderScene<Owner> scene)
    {
        using var subscription=new EnbRenderSceneSubscription();
        var definition=Template() with {RenderIdentity=1};
        Send(scene,subscription,1,new(EnbRenderObjectChangeKind.Add,1,null,definition));
        Assert.True(scene.TryFindByGameId(definition.GameId,out var node));
        var weak=new WeakReference(node.State);
        Send(scene,subscription,2,new(EnbRenderObjectChangeKind.Remove,1,null,null));
        Assert.False(scene.TryFindByGameId(definition.GameId,out _));
        scene.CompleteConsumption();
        return weak;
    }
    [Fact]
    public void IndexDoesNotKeepRetiredOwnerAlive()
    {
        var scene=Scene();var owner=RetireOwner(scene);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(owner.IsAlive);GC.KeepAlive(scene);
    }
    private static ulong LinearQueries(EnbRetainedRenderScene<Owner> scene)
    {
        ulong sum=0;
        for(uint i=0;i<100000;i++){uint id=i%300+1;sum+=scene.ActiveNodes.First(n=>n.Definition.GameId==id).Definition.RenderIdentity;}
        return sum;
    }
    private static ulong IndexedQueries(EnbRetainedRenderScene<Owner> scene)
    {
        ulong sum=0;
        for(uint i=0;i<100000;i++){scene.TryFindByGameId(i%300+1,out var node);sum+=node.Definition.RenderIdentity;}
        return sum;
    }
    [Fact]
    public void IndexMatchesOrderedQueriesAcrossDuplicatesUpdatesRemovalAndReset()
    {
        var template=Template();var scene=Scene();using var subscription=new EnbRenderSceneSubscription();var random=new Random(15431);ulong identity=0;
        for(ulong sequence=1;sequence<=1000;sequence++)
        {
            var nodes=scene.ActiveNodes.ToArray();int action=random.Next(3);
            if(nodes.Length==0 || action==0)
            {
                var definition=template with {RenderIdentity=++identity,GameId=(uint)random.Next(12)};
                ulong? after=nodes.Length==0 || random.Next(4)==0?null:nodes[random.Next(nodes.Length)].Definition.RenderIdentity;
                Send(scene,subscription,sequence,new(EnbRenderObjectChangeKind.Add,identity,after,definition));
            }
            else
            {
                var chosen=nodes[random.Next(nodes.Length)];
                if(action==1)
                    Send(scene,subscription,sequence,new(EnbRenderObjectChangeKind.Definition,chosen.Definition.RenderIdentity,null,chosen.Definition with {GameId=(uint)random.Next(12),Scale=sequence}));
                else
                {
                    Send(scene,subscription,sequence,new(EnbRenderObjectChangeKind.Remove,chosen.Definition.RenderIdentity,null,null));
                    Assert.True(chosen.State.Detached);Assert.False(chosen.State.Released);
                    scene.CompleteConsumption();Assert.True(chosen.State.Released);
                }
            }
            for(uint id=0;id<14;id++)
            {
                var expected=scene.ActiveNodes.FirstOrDefault(n=>n.Definition.GameId==id);
                Assert.Equal(expected is not null,scene.TryFindByGameId(id,out var actual));Assert.Same(expected,actual);
            }
        }
    }
    [Fact]
    public void EndpointQueriesDoNotRebuildMembershipOrAllocateAndReturnCurrentDefinition()
    {
        var template=Template();var scene=Scene();using var subscription=new EnbRenderSceneSubscription();
        for(ulong id=1;id<=300;id++)Send(scene,subscription,id,new(EnbRenderObjectChangeKind.Add,id,id==1?null:id-1,template with {RenderIdentity=id,GameId=(uint)id}));
        long mutations=scene.GameIdIndexMutations,changes=scene.AppliedChanges;
        MeasureQueries(scene);
        var measured=MeasureQueries(scene);
        Assert.True(measured.AllFound);Assert.Equal(0,measured.Bytes);Assert.True(measured.Sum>0);
        Assert.Equal(mutations,scene.GameIdIndexMutations);Assert.Equal(changes,scene.AppliedChanges);
        Assert.True(scene.TryFindByGameId(7,out var retained));
        Send(scene,subscription,301,new(EnbRenderObjectChangeKind.Definition,7,null,retained.Definition with {Scale=9}));
        Assert.True(scene.TryFindByGameId(7,out var updated));Assert.Same(retained,updated);Assert.Equal(9,updated.State.Definition.Scale);
        Assert.Equal(mutations,scene.GameIdIndexMutations);
        var results=new List<object>();
        foreach(bool indexed in new[]{false,true,true,false})
        {
            const int queries=100000;long before=GC.GetAllocatedBytesForCurrentThread();long ticks=System.Diagnostics.Stopwatch.GetTimestamp();ulong checksum=0;
            checksum=indexed?IndexedQueries(scene):LinearQueries(scene);
            double ms=System.Diagnostics.Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
            results.Add(new{Indexed=indexed,Queries=queries,Population=300,Milliseconds=ms,BytesPerQuery=(GC.GetAllocatedBytesForCurrentThread()-before)/(double)queries,Checksum=checksum});
        }
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(results));
    }
}
