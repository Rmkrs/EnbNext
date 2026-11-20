using EnbNext.Client;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Net7.Gameplay;
using System.Numerics;

namespace EnbNext.Net7.Tests;
public sealed class EnbSectorAutopilotTests
{
    private static EnbSceneFrame Scene()
    {
        var ship=new EnbSceneObjectSnapshot(1,1,1,true,false,0,175,1,0,0,0,0,false,"Ship",new(true,100,null),
            new(EnbSceneSpatialProviderKind.Fixed,new(0,Vector3.Zero,Quaternion.Identity,Vector3.Zero,0,Vector3.Zero,Vector3.UnitX),null),
            null,null,[],[],[],[],[],[],null,[],null,0,0);
        return new(){ClientSoundStreamId=Guid.NewGuid(),LocalShipObjectId=1,Objects=[ship],
            Environment=new(0,false,1,[],null,0,0,false,false,0,0,0,0),
            Hud={Cockpit=new EnbCockpitFrame {Warp=Ready},CurrentTargetVerbs=[]}};
    }
    private static Net7WarpStatus Ready=>new(0,2,null,false,false,100,0);
    private static void Warp(EnbSceneFrame s,int state)=>((EnbCockpitFrame)s.Hud.Cockpit!).Warp=Ready with {State=state};
    private sealed class Fixture
    {
        public readonly EnbSceneFrame Scene=EnbSectorAutopilotTests.Scene();
        public readonly List<string> Commands=[];
        public readonly EnbSectorAutopilot Pilot;
        public uint? Gate=80,Destination=3;
        public Fixture(Func<CancellationToken,Task<bool>>? select=null)
        {Pilot=new((id,ct)=>{Commands.Add("target:"+id);Scene.Hud.HasLocalTargetSelection=true;Scene.Hud.LocalSelectedTargetId=id;return select?.Invoke(ct)??Task.FromResult(true);},
            (id,owner,route,ct)=>{Commands.Add("warp:"+string.Join(',',route));return Task.FromResult(true);},
            (id,verb,ct)=>{Assert.Equal(0x000A0000u,verb);Commands.Add("gate:"+id);return Task.FromResult(true);});}
        public void Start()=>Assert.True(Pilot.Start(Scene,true,Destination,Gate,false));
        public void Tick(bool enabled=true,bool restricted=false,bool ready=true)=>Pilot.Tick(Scene,enabled,Destination,Gate,restricted,ready,()=>[Gate!.Value]);
    }
    [Fact] public void ExplicitStartThenTwoHandoffsCompleteWithoutDuplicateCommandsOrFinalNav()
    {
        var f=new Fixture();f.Tick();Assert.Empty(f.Commands);f.Start();
        f.Tick();f.Tick();f.Tick();Assert.Equal(new[]{"target:80","warp:80"},f.Commands);
        Warp(f.Scene,2);for(int i=0;i<5;i++)f.Tick();Assert.Equal(2,f.Commands.Count);
        Warp(f.Scene,8);f.Scene.Hud.CurrentTargetVerbs=[0x000A0001];f.Tick();Assert.Equal(2,f.Commands.Count);
        f.Scene.Hud.CurrentTargetVerbs=[0x000A0000];f.Tick();f.Tick();Assert.Equal("gate:80",f.Commands[^1]);
        f.Scene.IsTransitioning=true;f.Gate=null;f.Tick();Assert.True(f.Pilot.Active);
        f.Scene.IsTransitioning=false;f.Scene.Environment=f.Scene.Environment! with {FinalRawValue=2};f.Tick(ready:false);Assert.True(f.Pilot.Active);
        f.Gate=90;f.Scene.Hud.CurrentTargetVerbs=[];Warp(f.Scene,0);f.Tick();f.Tick();
        Assert.Equal(new[]{"target:80","warp:80","gate:80","target:90","warp:90"},f.Commands);
        Warp(f.Scene,8);f.Scene.Hud.CurrentTargetVerbs=[0x000A0000];f.Tick();
        f.Scene.Environment=f.Scene.Environment with {FinalRawValue=3};f.Gate=null;f.Tick();
        Assert.False(f.Pilot.Active);Assert.Equal("Destination reached",f.Pilot.StopReason);Assert.Equal(6,f.Commands.Count);
    }
    [Fact] public void IntermediateWarpTargetsKeepPilotActiveAndFinalGateIsReselected()
    {
        var f=new Fixture();f.Start();f.Tick();f.Tick();
        Warp(f.Scene,2);f.Scene.Hud.LocalSelectedTargetId=42;
        for(int i=0;i<5;i++)f.Tick();
        Assert.True(f.Pilot.Active);Assert.Equal(2,f.Commands.Count);
        Warp(f.Scene,3);f.Scene.Hud.CurrentTargetVerbs=[];f.Tick();
        Assert.Equal("target:80",f.Commands[^1]);
        f.Scene.Hud.CurrentTargetVerbs=[0x000A0000];f.Tick();f.Tick();
        Assert.True(f.Pilot.Active);Assert.Equal(new[]{"target:80","warp:80","target:80","gate:80"},f.Commands);
    }
    [Fact] public void AlreadyInGateRangeDoesNotWarpAndOffRouteHandoffUsesNewResolvedGate()
    {
        var f=new Fixture();f.Start();f.Tick();f.Scene.Hud.CurrentTargetVerbs=[0x000A0000];f.Tick();
        Assert.Equal(new[]{"target:80","gate:80"},f.Commands);
        f.Scene.Environment=f.Scene.Environment! with {FinalRawValue=4};f.Gate=99;f.Scene.Hud.CurrentTargetVerbs=[];f.Tick();f.Tick();
        Assert.Equal("warp:99",f.Commands[^1]);
    }
    [Theory] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(9)] [InlineData(10)] [InlineData(11)]
    public void WarpFailureStopsWithoutRetry(int state)
    {var f=new Fixture();f.Start();f.Tick();f.Tick();Warp(f.Scene,state);f.Tick();f.Tick();Assert.False(f.Pilot.Active);Assert.Equal(2,f.Commands.Count);}
    [Theory] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(21)]
    public void ServerRefusalStopsAndRetainsServerText(int type)
    {var f=new Fixture();f.Start();f.Pilot.ObserveMessage((byte)type,"Server refusal");f.Tick();Assert.False(f.Pilot.Active);Assert.Equal("Server refusal",f.Pilot.StopReason);Assert.Empty(f.Commands);}
    [Theory] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(20)] [InlineData(22)]
    public void OrdinaryMessagesDoNotCancel(int type)
    {var f=new Fixture();f.Start();f.Pilot.ObserveMessage((byte)type,"Ordinary message");Assert.True(f.Pilot.Active);}
    [Theory] [InlineData(17)] [InlineData(18)] [InlineData(19)] [InlineData(20)] [InlineData(21)]
    public void MessagesDuringWarpDoNotInterruptEngineOrAutopilot(int type)
    {
        var f=new Fixture();f.Start();f.Tick();f.Tick();Warp(f.Scene,2);
        f.Pilot.ObserveMessage((byte)type,"Scan or unrelated message");f.Tick();
        Assert.True(f.Pilot.Active);Assert.Equal(2,f.Commands.Count);
    }
    [Fact] public void AggroAllowsStartAndDoesNotInterruptWarpOrGateArrival()
    {
        var f=new Fixture();f.Scene.Objects=[((EnbSceneObjectSnapshot)f.Scene.Objects[0]) with {IsAttacking=true}];
        f.Start();f.Tick();f.Tick();Warp(f.Scene,2);f.Tick();
        Assert.True(f.Pilot.Active);Assert.Equal(2,f.Commands.Count);
        Warp(f.Scene,8);f.Scene.Hud.CurrentTargetVerbs=[0x000A0000];f.Tick();
        Assert.True(f.Pilot.Active);Assert.Equal("gate:80",f.Commands[^1]);
    }
    [Theory] [InlineData("disabled")] [InlineData("restricted")] [InlineData("destination")] [InlineData("gate")] [InlineData("character")] [InlineData("station")]
    public void ChangedAuthorityStopsBeforeAnotherCommand(string change)
    {
        var f=new Fixture();f.Start();f.Tick();
        if(change=="destination")f.Destination=null;
        if(change=="gate")f.Gate=null;
        if(change=="character")f.Scene.ClientSoundStreamId=Guid.NewGuid();
        if(change=="station")f.Scene.ActiveStarbaseId=5;
        f.Tick(change!="disabled",change=="restricted");Assert.False(f.Pilot.Active);Assert.Single(f.Commands);
    }
    [Fact] public async Task CancellationOfOutstandingTargetCannotAdvanceOrRestartOnLateCompletion()
    {
        var pending=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);CancellationToken token=default;
        var f=new Fixture(ct=>{token=ct;return pending.Task;});f.Start();f.Tick();f.Pilot.Cancel();
        Assert.True(token.IsCancellationRequested);Assert.False(f.Pilot.Start(f.Scene,true,3,80,false));
        pending.SetResult(true);await pending.Task;
        for(int i=0;i<100;i++){f.Tick();await Task.Yield();}
        Assert.False(f.Pilot.Active);Assert.Single(f.Commands);
    }
    [Fact] public void TargetRejectionStopsWithoutWarp()
    {var f=new Fixture(_=>Task.FromResult(false));f.Start();f.Tick();f.Tick();Assert.False(f.Pilot.Active);Assert.Single(f.Commands);}
    [Fact] public void TargetExceptionIsContainedAndStops()
    {var f=new Fixture(_=>throw new IOException("send failed"));f.Start();f.Tick();Assert.False(f.Pilot.Active);Assert.Equal("send failed",f.Pilot.StopReason);}
    [Fact] public void StartRequiresEnabledRecognisedUnrestrictedSpaceRouteAndNoExistingWarp()
    {
        var s=Scene();Assert.False(EnbSectorAutopilot.CanStart(s,false,3,80,false));Assert.False(EnbSectorAutopilot.CanStart(s,true,3,null,false));
        Assert.False(EnbSectorAutopilot.CanStart(s,true,3,80,true));Assert.False(EnbSectorAutopilot.CanStart(s,true,1,80,false));
        Warp(s,2);Assert.False(EnbSectorAutopilot.CanStart(s,true,3,80,false));
    }
    [Fact] public void OptionDefaultsOffAndRoundTripsWithoutLosingOtherOptions()
    {
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("").AutopilotEnabled);
        string text=EnbRetailCinematicOptionsWriter.UpdateValues("",[("BOOL","Next_Autopilot","yes"),("FLOAT","Interface_Tooltip_Delay","23")]);
        var options=EnbRetailPlayerOptionsCatalog.ParseText(text);Assert.True(options.AutopilotEnabled);Assert.Equal(23,options.InterfaceTooltipDelay);
        text=EnbRetailCinematicOptionsWriter.UpdateValues(text,[("BOOL","Next_Autopilot","no")]);Assert.False(EnbRetailPlayerOptionsCatalog.ParseText(text).AutopilotEnabled);
    }
}
