using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetainedMembershipSetTests
{
    [Fact]
    public void SourceMembershipSurvivesFramesButGeneratedMembershipDoesNot()
    {
        var members=new EnbRetainedMembershipSet<(ulong,ulong)>();
        members.Attach((1,1));members.Attach((2,1));
        for(int frame=0;frame<100;frame++)
        {
            members.BeginFrame(true);
            Assert.True(members.SetEquals([(1,1),(2,1)]));
            members.UnionWith([(1,1),(3,1)]);
            Assert.Equal(3,members.Count);
            members.ClearFrame();
        }
        Assert.Equal(2,members.PersistentMutations);
        members.Detach((1,1));members.Attach((1,2));
        members.BeginFrame(true);
        Assert.False(members.Contains((1,1)));Assert.True(members.Contains((1,2)));
        members.BeginFrame(false);members.Add((9,4));
        Assert.True(members.SetEquals([(9,4)]));
        members.BeginFrame(true);
        Assert.True(members.SetEquals([(1,2),(2,1)]));
        members.Detach((1,2));members.Detach((2,1));
        Assert.Empty(members);
    }
}
