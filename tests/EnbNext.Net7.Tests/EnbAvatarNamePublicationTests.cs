using EnbNext.Net7.Protocol;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbAvatarNamePublicationTests
{
    [Fact]
    public void CachedNamesPublishWithoutWorldObjectsAndOlderSnapshotsRemainDetached()
    {
        var world=new Net7WorldState();
        var (avatar,_)=Net7AvatarRosterCodec.DecodeAvatarData(Net7PresentationWireTests.BuildAvatarData(""),"fixture");
        world.ReplaceAvatarAppearance(42,avatar with {FirstName="Cached",LastName="Unused"});
        var publisher=new EnbPresentationSnapshotPublisher();
        var first=publisher.Publish(world,null,null,0);
        Assert.Empty(first.Objects);Assert.Equal("Cached",first.AvatarFirstNames[42]);
        world.ReplaceAvatarAppearance(42,avatar with {FirstName=""});
        var second=publisher.Publish(world,null,null,0);
        Assert.Equal("",second.AvatarFirstNames[42]);Assert.Equal("Cached",first.AvatarFirstNames[42]);
    }
}
