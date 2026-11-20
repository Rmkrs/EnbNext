using EnbNext.Net7.World;
using EnbNext.Presentation;
using System.Runtime.CompilerServices;

namespace EnbNext.Net7.Tests;

public sealed class EnbWorldMembershipFeedTests
{
    private static Net7CreateUpdate Create(uint id) => new(id,1,10,1,0,0,0);
    [Fact]
    public void OrderedChangesReproduceNativeRegistryAcrossCoalescedMutationsAndDuplicateIds()
    {
        var registry=new Net7WorldRegistry();
        registry.Create(Create(1),null);registry.Create(Create(258),null);registry.Create(Create(2),null);
        using var feed=registry.SubscribeMembership();
        var nodes=new List<Net7WorldObject>();
        void Drain()
        {
            while(feed.TryRead(out var change))
                switch(change.Kind)
                {
                    case Net7WorldMembershipChangeKind.Reset:nodes.Clear();break;
                    case Net7WorldMembershipChangeKind.Add:
                        int index=change.After is null?-1:nodes.FindIndex(x=>ReferenceEquals(x,change.After));
                        Assert.True(change.After is null||index>=0);
                        nodes.Insert(index+1,change.Object!);break;
                    case Net7WorldMembershipChangeKind.Remove:
                        Assert.True(nodes.Remove(change.Object!));break;
                }
            Assert.Equal(registry.Snapshot(),nodes);
        }
        Drain();
        var random=new Random(8271);
        for(int batch=0;batch<50;batch++)
        {
            for(int i=0;i<12;i++)
            {
                uint id=(uint)(random.Next(5)+257*random.Next(4));
                if(random.Next(3)!=0)registry.Create(Create(id),null);else registry.RemoveFirst(id);
            }
            if(batch%13==12)registry.Clear();
            Drain();
            Assert.False(feed.TryRead(out _));
        }
    }

    [Fact]
    public void DisposalReleasesPendingRemovedObjectsAndStopsDelivery()
    {
        var (registry,feed,weak)=PendingRemoved();
        feed.Dispose();
        for(int i=0;i<3&&weak.IsAlive;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(weak.IsAlive);
        registry.Create(Create(7),null);
        Assert.False(feed.TryRead(out _));
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Net7WorldRegistry,Net7WorldMembershipSubscription,WeakReference) PendingRemoved()
    {
        var registry=new Net7WorldRegistry();var feed=registry.SubscribeMembership();
        var obj=registry.Create(Create(7),null);var weak=new WeakReference(obj);registry.RemoveFirst(7);
        return(registry,feed,weak);
    }

    [Fact]
    public void PresentationRetiresSameCountReplacementWithoutChangingSurvivorIdentity()
    {
        var world=new Net7WorldState();var builder=new EnbPresentationSnapshotBuilder();
        world.MaterializeCreate(Create(7));world.MaterializeCreate(Create(8));
        var before=builder.Build(world,null,0);
        world.MaterializeRemove(7);world.MaterializeCreate(Create(9));
        var after=builder.Build(world,null,1);
        Assert.Equal(before.Objects.Single(o=>o.GameId==8).RenderIdentity,after.Objects.Single(o=>o.GameId==8).RenderIdentity);
        Assert.DoesNotContain(after.Objects,o=>o.GameId==7);
        var tracking=typeof(EnbPresentationSnapshotBuilder).GetField("_trackedObjects",
            System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(2,((System.Collections.IDictionary)tracking.GetValue(builder)!).Count);
        Assert.Contains(before.Objects,o=>o.GameId==7);
    }
}
