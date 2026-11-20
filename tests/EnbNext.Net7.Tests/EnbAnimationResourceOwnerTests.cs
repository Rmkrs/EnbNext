using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbAnimationResourceOwnerTests
{
    [Fact]
    public void ConcurrentLibraryReplacementPublishesOneCoherentResource()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(EnbR4W3dRenderResourceTests.BuildResolvedResource(0),out var source,out var error),error);
        source=source.Copy().SetAnimations([]);
        var sources=new[]{source,source.Copy()};
        var a=new EnbW3dRenderHierarchyAnimation(0,0x40001,"a","tree",2,30,false,[],[]);
        var b=a.Copy().SetName("b");
        var owner=new EnbW3dAnimationResourceOwner();
        var queued=new EnbW3dRenderResource[1024];
        Parallel.For(0,queued.Length,i=>queued[i]=owner.Resolve(sources[i%2],[new(i%2==0?a:b,i,.5f)]));
        for(int i=0;i<queued.Length;i++)Assert.Same(i%2==0?a:b,Assert.Single(queued[i].Animations));
    }

    [Fact]
    public void FrameAndWeightChangesRetainResourceButMembershipReplacementDoesNot()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(EnbR4W3dRenderResourceTests.BuildResolvedResource(0),out var resource,out var error),error);
        resource=resource.Copy().SetAnimations([]);
        var clip=new EnbW3dRenderHierarchyAnimation(0,0x40001,"clip","tree",2,30,false,[],[]);
        var owner=new EnbW3dAnimationResourceOwner();
        var first=owner.Resolve(resource,[new(clip,0,1)]);
        for(int i=0;i<64;i++)Assert.Same(first,owner.Resolve(resource,[new(clip,i,.5f)]));
        Assert.Same(clip,Assert.Single(first.Animations));
        var replacement=clip.Copy().SetNumFrames(9);
        var next=owner.Resolve(resource,[new(replacement,3,1)]);
        Assert.NotSame(first,next);Assert.Same(replacement,Assert.Single(next.Animations));
        Assert.Same(clip,Assert.Single(first.Animations));
        Assert.Same(resource,owner.Resolve(resource,[]));
        var changed=resource.Copy().SetAnimations([clip]);
        Assert.Same(changed,owner.Resolve(changed,[new(replacement,4,1)]));
        // Existing name/hierarchy wins, exactly as the former ordered merge.
        Assert.Same(clip,Assert.Single(changed.Animations));
    }
    [Fact]
    public void OrderedDistinctClipMembershipAndSourceReplacementArePreserved()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(EnbR4W3dRenderResourceTests.BuildResolvedResource(0),out var resource,out var error),error);
        resource=resource.Copy().SetAnimations([]);
        var a=new EnbW3dRenderHierarchyAnimation(0,0x40001,"a","tree",2,30,false,[],[]);
        var b=a.Copy().SetName("b");var duplicate=a.Copy().SetName("A").SetHierarchyName("TREE");
        var owner=new EnbW3dAnimationResourceOwner();
        var first=owner.Resolve(resource,[new(b,0,1),new(a,0,1),new(duplicate,0,1)]);
        Assert.Equal(new[]{b,a},first.Animations);
        var changed=resource.Copy();
        Assert.NotSame(first,owner.Resolve(changed,[new(b,0,1),new(a,0,1),new(duplicate,0,1)]));
    }
}
