using System.Collections.Immutable;
using EnbNext.Engine;
using EnbNext.Presentation;
using Xunit;
namespace EnbNext.Net7.Tests;
public sealed class TrackedMissionsTests
{
    private static EnbCharacterMissionSnapshot Mission(int id,bool done=false)=>new("M"+id,""){Id=id,Slot=id,CanClear=done};
    [Fact] public void TerminalAndVanishedMissionsDropOut()
    {
        var tracked=new List<int>{1,2,3};ImmutableArray<EnbCharacterMissionSnapshot> before=[Mission(1),Mission(2),Mission(3)];
        Assert.False(EnbTrackedMissions.Update(tracked,before,[],[],false));
        Assert.True(EnbTrackedMissions.Update(tracked,[Mission(1),Mission(2,done:true)],before,[],false));
        Assert.Equal([1],tracked);
        // An empty log (not yet received / resync) never prunes.
        Assert.False(EnbTrackedMissions.Update(tracked,[],[Mission(1)],[],false));
        Assert.Equal([1],tracked);
    }
    [Fact] public void UnseenTrackedMissionWaitsForTheLog()
    {
        var tracked=new List<int>{5};
        Assert.False(EnbTrackedMissions.Update(tracked,[Mission(1)],[],[],false));
        Assert.Equal([5],tracked);
    }
    [Fact] public void NewMissionsTrackOnlyWithAutoTrack()
    {
        var tracked=new List<int>();var log=ImmutableArray.Create(Mission(1),Mission(2));
        Assert.False(EnbTrackedMissions.Update(tracked,log,log,[log[0]],false));
        Assert.Empty(tracked);
        Assert.True(EnbTrackedMissions.Update(tracked,log,log,[log[1]],true));
        Assert.Equal([2],tracked);
        Assert.False(EnbTrackedMissions.Update(tracked,log,log,[log[1]],true));
        Assert.False(EnbTrackedMissions.Update(tracked,[Mission(1),Mission(2),Mission(3,done:true)],log,[Mission(3,done:true)],true));
    }
}
