using EnbNext.Presentation;
using EnbNext.App;
using System.Text.Json;
namespace EnbNext.Net7.Tests;

public sealed class Net7AutomaticActionsTests
{
    [Fact]
    public void DefaultsAndPersistenceKeepFourOptionsIndependent()
    {
        var legacy=JsonSerializer.Deserialize<EnbStartupPreferences>("{}")!;
        Assert.Equal(new EnbQualityOfLifeOptions(),legacy.QualityOfLife??new());
        Assert.Equal(60,legacy.BackgroundGameplayFps);Assert.False(legacy.BackgroundFpsCap);Assert.Equal(0,legacy.BackgroundFpsLimit);
        Assert.Equal(60,(legacy with {BackgroundFpsCap=true,BackgroundGameplayFps=0}).BackgroundFpsLimit);
        var enabled=legacy with {QualityOfLife=new(true,false,true,false)};
        Assert.Equal(enabled,JsonSerializer.Deserialize<EnbStartupPreferences>(JsonSerializer.Serialize(enabled)));
    }
    [Fact]
    public void EventsAreConsumedOnceAndOffDoesNotQueueOldActions()
    {
        var state=new Net7AutomaticActions();
        (bool Invite,bool Trade,bool Form) Observe(long invite=1,ulong revision=1,bool confirmed=true,bool waiting=false,bool group=true,bool leader=false,string name="Pipe",uint formation=0,bool enabled=true)
            =>state.Observe(invite,true,revision,true,confirmed,waiting,group,leader,name,formation,enabled,enabled,enabled);
        Assert.Equal((false,false,false),Observe(enabled:false));
        Assert.Equal((false,false,false),Observe());
        Assert.Equal((true,false,false),Observe(invite:2));
        Assert.Equal((false,true,false),Observe(invite:2,revision:2));
        Assert.Equal((false,false,false),Observe(invite:2,revision:2));
        Assert.Equal((false,false,true),Observe(invite:2,revision:2,name:"Block"));
        Assert.Equal((false,false,false),Observe(invite:2,revision:2,name:"Block"));
    }
    [Fact]
    public void TradeNeverConfirmsOurOwnWaitingAndRearmsAfterPeerWithdrawal()
    {
        var state=new Net7AutomaticActions();
        bool Observe(bool confirmed,bool waiting=false,bool partner=true)
            =>state.Observe(0,false,1,partner,confirmed,waiting,false,false,"",0,false,true,false).Trade;
        Assert.False(Observe(false,true));
        Assert.False(Observe(true,true));
        Assert.False(Observe(true));
        Assert.False(Observe(false));
        Assert.True(Observe(true));
        Assert.False(Observe(true));
        Assert.False(Observe(true,partner:false));
    }
    [Fact]
    public void FormationNeverTogglesAnExistingFormationOrRejoinsAfterManualLeave()
    {
        var state=new Net7AutomaticActions();
        bool Observe(string name,uint formation=0,bool group=true,bool leader=false)
            =>state.Observe(0,false,0,false,false,false,group,leader,name,formation,false,false,true).Form;
        Assert.False(Observe("Pipe",group:false));
        Assert.False(Observe("Pipe",leader:true));
        Assert.True(Observe("Pipe"));
        Assert.False(Observe("Pipe",formation:1));
        Assert.False(Observe("Pipe"));
        Assert.True(Observe("Block"));
        Assert.False(Observe(""));
        Assert.True(Observe("Block"));
    }
}
