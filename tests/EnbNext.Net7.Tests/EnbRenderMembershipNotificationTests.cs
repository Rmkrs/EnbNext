using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbRenderMembershipNotificationTests
{
    [Fact]
    public void RetainedConsumerObservesActualMembershipEdgesAndCanReleaseSubscription()
    {
        var parent=new EnbCurrentRetailRslBoneRenderObjectRuntime(null,isCollection:true);
        var child=new EnbCurrentRetailRslBoneRenderObjectRuntime(null);
        int changes=0;void Changed()=>changes++;
        parent.DefinitionChanged+=Changed;
        Assert.True(parent.AddChild(child));Assert.True(parent.AddChild(child));
        Assert.Equal(2,changes);
        child.SetHidden(true);Assert.Equal(4,changes);
        child.SetHidden(true);Assert.Equal(4,changes);
        Assert.True(parent.RemoveChild(child));Assert.Equal(5,changes);
        child.SetHidden(false);Assert.Equal(6,changes);
        Assert.True(parent.RemoveChild(child));Assert.Equal(7,changes);
        child.SetHidden(true);Assert.Equal(7,changes);
        parent.DefinitionChanged-=Changed;
        Assert.True(parent.AddChild(child));Assert.Equal(7,changes);
    }
}
