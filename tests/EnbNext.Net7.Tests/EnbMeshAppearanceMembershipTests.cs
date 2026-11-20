using System.Runtime.CompilerServices;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMeshAppearanceMembershipTests
{
    private static EnbCurrentRetailMeshAppearance Model(bool prototype=false)
        => new(null!,[],null!,null!,prototype);

    [Fact]
    public void SharedSlotsAndReacquisitionKeepTheModelAliveUntilFinalRelease()
    {
        var membership=new EnbMeshAppearanceMembership();var model=Model();
        var retired=new List<EnbCurrentRetailMeshAppearance>();
        membership.Retain(model);membership.Retain(model);
        membership.Release(model);membership.Drain(retired.Add);Assert.Empty(retired);
        membership.Release(model);membership.Retain(model);
        membership.Drain(retired.Add);Assert.Empty(retired);Assert.False(membership.HasPendingRetirements);
        membership.Release(model);membership.Drain(retired.Add);
        Assert.Same(model,Assert.Single(retired));Assert.False(membership.HasPendingRetirements);
    }
    [Fact]
    public void RetirementUsesCreationOrderAndPhysicalIdentity()
    {
        var membership=new EnbMeshAppearanceMembership();var first=Model();var second=first with {};
        Assert.Equal(first,second);Assert.NotSame(first,second);
        membership.Retain(first);membership.Retain(second);
        var retired=new List<EnbCurrentRetailMeshAppearance>();
        membership.Release(second);membership.Release(first);membership.Drain(retired.Add);
        Assert.Equal(2,retired.Count);Assert.Same(first,retired[0]);Assert.Same(second,retired[1]);
    }
    [Fact]
    public void PrototypeOwnershipSurvivesAnEmptyVisibleSlotSet()
    {
        var membership=new EnbMeshAppearanceMembership();var model=Model(true);
        membership.Retain(model);membership.Release(model);
        membership.Drain(_=>throw new Exception("Prototype was retired with an ordinary slot"));
        Assert.False(membership.HasPendingRetirements);
        membership.Retain(model);membership.Release(model);membership.Clear();
        Assert.Throws<InvalidOperationException>(()=>membership.Release(model));
    }
    [Fact]
    public void FailedRetirementCanBeRetriedWithoutRepeatingCompletedReleases()
    {
        var membership=new EnbMeshAppearanceMembership();var first=Model();var second=Model();
        membership.Retain(first);membership.Retain(second);membership.Release(second);membership.Release(first);
        var retired=new List<EnbCurrentRetailMeshAppearance>();
        Assert.Throws<InvalidOperationException>(()=>membership.Drain(model=>
        {if(ReferenceEquals(model,second))throw new InvalidOperationException();retired.Add(model);}));
        membership.Drain(retired.Add);
        Assert.Equal(2,retired.Count);Assert.Same(first,retired[0]);Assert.Same(second,retired[1]);
    }
    [Fact]
    public void DrainedScratchDoesNotRetainRetiredGenerations()
    {
        var membership=new EnbMeshAppearanceMembership();var weak=Retire(membership);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);GC.KeepAlive(membership);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Retire(EnbMeshAppearanceMembership membership)
    {
        var model=Model();var weak=new WeakReference(model);
        membership.Retain(model);membership.Release(model);membership.Drain(static _=>{});
        return weak;
    }
}
