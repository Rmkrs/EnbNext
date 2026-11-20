using System.Collections;
using System.Runtime.CompilerServices;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetainedOwnerRegistryTests
{
    [Fact]
    public void ScopedTraversalMatchesWholeMembershipOrderAfterMixedOwnerReplacement()
    {
        var registry=new EnbRetainedOwnerRegistry<(ulong Owner,int Slot),object>(static key=>key.Owner);
        var oracle=new Dictionary<(ulong Owner,int Slot),object>();
        var random=new Random(703);
        for(int mutation=0;mutation<500;mutation++)
        {
            var key=((ulong)random.Next(4),random.Next(12));
            if(random.Next(3)==0){Assert.Equal(oracle.Remove(key),registry.Remove(key));}
            else if(!oracle.ContainsKey(key)){var value=new object();oracle.Add(key,value);registry.Add(key,value);}
            for(ulong owner=0;owner<5;owner++)
            {
                var actual=new List<KeyValuePair<(ulong Owner,int Slot),object>>();
                foreach(var pair in registry.ForOwner(owner))actual.Add(pair);
                Assert.Equal(oracle.Where(pair=>pair.Key.Owner==owner),actual);
            }
            Assert.Equal(oracle.Keys.Select(key=>key.Owner).Distinct().Count(),registry.OwnerCount);
        }
        long preparations=registry.GroupPreparations;
        for(int repeat=0;repeat<100;repeat++)foreach(var pair in registry.ForOwner(0))Assert.NotNull(pair.Value);
        Assert.Equal(preparations,registry.GroupPreparations);
    }

    [Fact]
    public void QueuedSelectionSurvivesRemovalButNewSelectionsSeeCurrentMembership()
    {
        var registry=new EnbRetainedOwnerRegistry<(ulong Owner,int Slot),object>(static key=>key.Owner);
        var first=new object();var second=new object();
        registry.Add((1,0),first);
        var queued=registry.ForOwner(1);
        registry.Remove((1,0));registry.Add((1,1),second);
        foreach(var pair in queued)Assert.Same(first,pair.Value);
        foreach(var pair in registry.ForOwner(1))Assert.Same(second,pair.Value);
        registry.Clear();Assert.Equal(0,registry.OwnerCount);Assert.Empty(registry);
    }

    [Fact]
    public void DiagnosticRestorationMaintainsOwnerMembershipAndInvalidatesCachedValues()
    {
        var registry=new EnbRetainedOwnerRegistry<(ulong Owner,int Slot),object>(static key=>key.Owner);
        IDictionary diagnostic=registry;
        diagnostic.Add((1UL,0),new object());foreach(var _ in registry.ForOwner(1)){}
        var replacement=new object();diagnostic[(1UL,0)]=replacement;
        foreach(var pair in registry.ForOwner(1))Assert.Same(replacement,pair.Value);
        diagnostic.Remove((1UL,0));Assert.Equal(0,registry.OwnerCount);
    }

    [Fact]
    public void RemovedOwnerGroupDoesNotKeepItsValuesAlive()
    {
        var registry=new EnbRetainedOwnerRegistry<(ulong Owner,int Slot),object>(static key=>key.Owner);
        var weak=AddAndRemove(registry);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);Assert.Equal(0,registry.OwnerCount);GC.KeepAlive(registry);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference AddAndRemove(EnbRetainedOwnerRegistry<(ulong Owner,int Slot),object> registry)
    {
        var value=new object();var weak=new WeakReference(value);registry.Add((9,0),value);
        foreach(var pair in registry.ForOwner(9))Assert.Same(value,pair.Value);
        registry.Remove((9,0));return weak;
    }
}
