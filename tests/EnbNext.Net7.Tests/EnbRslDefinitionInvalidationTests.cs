using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRslDefinitionInvalidationTests
{
    private static EnbW3dRenderHierarchy Hierarchy()=>new(0,"fixture",default,false,
        [new(0,"ROOT",uint.MaxValue,default,default,Quaternion.Identity,Matrix4x4.Identity)],[]);

    [Fact]
    public void DefinitionChangesPropagateThroughOwnedEdgesIncludingDuplicateMembership()
    {
        var leaf=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,hasLeafLodBias:true);
        var parent=new EnbCurrentRetailRslBoneRenderObjectRuntime(Hierarchy(),aggregate:[new(0,leaf),new(0,leaf)]);
        var ancestor=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,aggregate:[new(0,parent)],isCollection:true);
        ulong revision=ancestor.DefinitionRevision;
        leaf.SetHidden(true);Assert.True(ancestor.DefinitionRevision>revision);
        revision=ancestor.DefinitionRevision;
        leaf.SetHidden(true);leaf.SetAnimationVisible(false);parent.SetPivotVisibility(0,false);
        Assert.Equal(revision,ancestor.DefinitionRevision);
        Assert.True(parent.RemoveChild(leaf));Assert.Null(leaf.Parent);
        revision=ancestor.DefinitionRevision;
        leaf.SetLodBias(.5f);Assert.True(ancestor.DefinitionRevision>revision);
        Assert.True(parent.RemoveChild(leaf));revision=ancestor.DefinitionRevision;
        leaf.SetHidden(false);leaf.SetLodBias(.8f);Assert.Equal(revision,ancestor.DefinitionRevision);
        Assert.False(parent.RemoveChild(leaf));Assert.Equal(revision,ancestor.DefinitionRevision);
        Assert.True(parent.AddChild(leaf));Assert.True(ancestor.DefinitionRevision>revision);
        revision=ancestor.DefinitionRevision;
        leaf.SetHidden(true);Assert.True(ancestor.DefinitionRevision>revision);
    }

    [Fact]
    public void UnchangedParentSetterStillForwardsRequiredParticleMutation()
    {
        var child=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,isParticleEmitter:true);
        var parent=new EnbCurrentRetailRslBoneRenderObjectRuntime(Hierarchy(),aggregate:[new(0,child)]);
        child.SetHidden(true);ulong revision=parent.DefinitionRevision;
        parent.SetHidden(false);Assert.False(child.Hidden);Assert.True(parent.DefinitionRevision>revision);
        revision=parent.DefinitionRevision;
        parent.SetHidden(false);Assert.Equal(revision,parent.DefinitionRevision);
    }

    [Fact]
    public void RemovedChildDoesNotKeepFormerDefinitionOwnerAlive()
    {
        var child=new EnbCurrentRetailRslBoneRenderObjectRuntime(null);
        var weak=DetachedOwner(child);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();
        Assert.False(weak.IsAlive);GC.KeepAlive(child);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DetachedOwner(EnbCurrentRetailRslBoneRenderObjectRuntime child)
    {
        var owner=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,isCollection:true);
        owner.AddChild(child);owner.AddChild(child);owner.RemoveChild(child);owner.RemoveChild(child);
        return new WeakReference(owner);
    }
}
