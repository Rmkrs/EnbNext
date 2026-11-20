using System.Reflection;
using System.Text;
using EnbNext.App;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class MultiboxDockLaunchTests
{
    [Fact]
    public void DockLandAndLaunchFollowTheirOwnOptInAndContext()
    {
        var space=new EnbMultiboxContext(1,8,8,12,4,3);
        var station=new EnbMultiboxContext(1,8,0,0,0x5000,4);
        var alt=new EnbMultiboxContext(2,7,7,21,4,8);
        var altStation=new EnbMultiboxContext(2,7,0,0,0x5000,9);
        var leader=new MultiboxState(Guid.NewGuid(),"test",Guid.NewGuid(),1,"Pilot",8,null,false,true,[7],"Sector",null,false)
            {Context=space,StationContext=station};
        var options=new EnbMultiboxOptions(true);
        foreach(string action in new[]{"dock","land"})
        {
            var input=new EnbMultiboxInput(Guid.NewGuid(),action,99,space);
            var command=new MultiboxMessage(MultiboxPeer.Version,"command",leader.Instance,input.Id,action,null,input,alt);
            Assert.False(command.Allows(leader,alt,"test",options));
            Assert.False(command.Allows(leader,alt,"test",options with {FollowLeaderLaunch=true,FollowLeaderTarget=true}));
            Assert.True(command.Allows(leader,alt,"test",options with {FollowLeaderDock=true}));
            Assert.False(command.Allows(leader with {Context=space with {Owner=1}},alt,"test",options with {FollowLeaderDock=true}));
        }
        var launch=new EnbMultiboxInput(Guid.NewGuid(),"launch",0x5000,station);
        var message=new MultiboxMessage(MultiboxPeer.Version,"command",leader.Instance,launch.Id,"launch",null,launch,altStation);
        Assert.False(message.Allows(leader,altStation,"test",options with {FollowLeaderDock=true}));
        Assert.True(message.Allows(leader,altStation,"test",options with {FollowLeaderLaunch=true}));
        // A space identity of the leader never authorizes a station command.
        Assert.False(message.Allows(leader with {StationContext=null},altStation,"test",options with {FollowLeaderLaunch=true}));
        Assert.False(message.Allows(leader,altStation with {Sector=0x5001},"test",options with {FollowLeaderLaunch=true}));
        var peer=leader with {FollowDock=true,FollowLaunch=false};
        Assert.Equal(station,peer.ContextFor("launch"));Assert.Equal(space,peer.ContextFor("dock"));
        Assert.True(peer.Follows("land"));Assert.False(peer.Follows("launch"));
    }

    [Fact]
    public async Task ReceivedDockPressesTheAltsOwnEnabledDockVerbForTheSharedTarget()
    {
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        await using var client=new EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,7u);
        EnbR11FlightTests.FlightWorld(session.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(session.Gameplay,0);
        session.World.Apply(Packet.ForOpcode(0x0042,new byte[Net7PresentationWire.ServerParametersPayloadSize]),1000);
        session.World.MaterializeCreate(new Net7CreateUpdate(99,1,0,1,0,0,0));
        void Set(string path,object value)
        {var p=session.World.LocalPlayerAuxData.Properties.Single(p=>p.Path=="LocalPlayerAuxData.GroupInfo."+path);p.Value=value;p.State=2;p.UpdateRevision++;}
        Set("Members.0.Name",new Net7AuxDataString(Encoding.Latin1.GetBytes("Leader")));Set("Members.0.GameID",8);Set("IsGroupLeader",(byte)0);
        Set("Formation",BitConverter.GetBytes(1u));Set("FormationName",new Net7AuxDataString(Encoding.Latin1.GetBytes("Block")));
        void Publish()=>typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.Invoke(client,[1000u]);
        var verbs=(Dictionary<uint,IReadOnlyList<uint>>)typeof(Net7GameplayState).GetField("_verbLists",flags)!.GetValue(session.Gameplay)!;
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        Publish();var context=Assert.IsType<EnbMultiboxContext>(client.MultiboxContext);
        int inputs=0;client.MultiboxInput+=(_,_)=>inputs++;
        var frames=new List<(ushort Opcode,byte[] Payload)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var dock=new EnbMultiboxInput(Guid.NewGuid(),"dock",99,context with {Avatar=8});
        // No shared target yet: nothing is pressed.
        verbs[99]=[0x00080000u];
        Assert.False(await client.ApplyMultiboxInputAsync(dock,context));
        Assert.True(await client.ApplyMultiboxInputAsync(dock with {Action="target"},context));
        frames.Clear();
        // A server-disabled button (nonzero reason word) is not pressed.
        verbs[99]=[0x00080003u];
        Assert.False(await client.ApplyMultiboxInputAsync(dock,context));
        Assert.False(await client.ApplyMultiboxInputAsync(dock with {Action="land"},context));
        Assert.Empty(frames);
        verbs[99]=[0x00080000u];
        Assert.True(await client.ApplyMultiboxInputAsync(dock,context));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.DockRequest,99,0),frames[^1].Payload);
        Assert.Equal(0,inputs);
        // Leaving formation stops following.
        Set("Formation",new byte[4]);Publish();frames.Clear();
        Assert.False(await client.ApplyMultiboxInputAsync(dock,context));
        Assert.Empty(frames);
        // The leader's manual Dock first dispatches the alts (as N7CM: alts first, leader last),
        // then sends its own request; other verbs are not dispatched.
        Set("Formation",BitConverter.GetBytes(1u));Publish();frames.Clear();
        var order=new List<string>();
        client.MultiboxLeadAsync=lead=>{order.Add(lead.Action+":"+lead.Target+":"+frames.Count);return Task.CompletedTask;};
        Assert.True(await client.ExecuteTargetVerbAsync(99,0x00080000u));
        Assert.Equal(["dock:99:0"],order);
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.DockRequest,99,0),frames[^1].Payload);
        Assert.True(await client.ExecuteTargetVerbAsync(99,0x000D0000u));Assert.Single(order);
        Assert.Equal(0,inputs);
    }

    [Fact]
    public void LaunchCreatesTheLeadersFormationAndJoinsMembersOnceInSpace()
    {
        var state=new Net7AutomaticActions();
        (bool Form,uint Create) Observe(bool docked=false,bool space=true,int aux=2,bool group=true,bool leader=true,string name="",uint formation=0,bool formUp=true,int type=3)
            =>state.ObserveDeparture(docked,space,aux,group,leader,name,formation,formUp,type);
        Assert.Equal((false,0u),Observe());
        Assert.Equal((false,0u),Observe(docked:true));
        Assert.Equal((false,0u),Observe(space:false));
        Assert.Equal((false,0u),Observe(aux:0));
        Assert.Equal((false,5u),Observe());
        Assert.Equal((false,0u),Observe());
        foreach(var (type,request) in new[]{(1,6u),(2,4u),(0,0u)})
        {Observe(docked:true);Assert.Equal((false,request),Observe(type:type));}
        Observe(docked:true);Assert.Equal((false,0u),Observe(formation:1));
        Observe(docked:true);Assert.Equal((true,0u),Observe(leader:false,name:"Block"));
        Observe(docked:true);Assert.Equal((false,0u),Observe(leader:false,name:"Block",formUp:false));
        Observe(docked:true);Assert.Equal((false,0u),Observe(leader:false));
        Observe(docked:true);Assert.Equal((false,0u),Observe(group:false));
    }

    [Fact]
    public void LeaderWaitsForDockedMembersAndReoffersItsFormationToArrivals()
    {
        var state=new Net7AutomaticActions();
        // Launch formation waits while a multibox member is still docked.
        state.ObserveDeparture(true,false,2,true,true,"",0,true,3);
        Assert.Equal((false,0u),state.ObserveDeparture(false,true,2,true,true,"",0,true,3,membersDocked:true));
        Assert.Equal((false,0u),state.ObserveDeparture(false,true,2,true,true,"",0,true,3,membersDocked:true));
        Assert.Equal((false,5u),state.ObserveDeparture(false,true,2,true,true,"",0,true,3,membersDocked:false));
        // Members never wait on each other.
        state.ObserveDeparture(true,false,2,true,false,"",0,true,3);
        Assert.Equal((true,0u),state.ObserveDeparture(false,true,2,true,false,"Block",0,true,3,membersDocked:true));
        // An arrival makes the leader break (CTA 9) and re-create the same type once it is gone.
        Assert.Equal(0u,state.ObserveFormationRefresh(true,true,true,5,3));
        state.RequestFormationRefresh();
        Assert.Equal(0u,state.ObserveFormationRefresh(false,true,true,5,3));
        Assert.Equal(9u,state.ObserveFormationRefresh(true,true,true,5,3));
        Assert.Equal(0u,state.ObserveFormationRefresh(true,true,true,5,3));
        Assert.Equal(5u,state.ObserveFormationRefresh(true,true,true,0,3));
        Assert.Equal(0u,state.ObserveFormationRefresh(true,true,true,0,3));
        state.RequestFormationRefresh();
        Assert.Equal(9u,state.ObserveFormationRefresh(true,true,true,6,1));
        Assert.Equal(6u,state.ObserveFormationRefresh(true,true,true,0,1));
        foreach(var (leader,formation,option) in new[]{(false,5u,3),(true,5u,0),(true,0u,3)})
        {
            state.RequestFormationRefresh();
            Assert.Equal(0u,state.ObserveFormationRefresh(true,true,leader,formation,option));
            Assert.Equal(0u,state.ObserveFormationRefresh(true,true,true,5,3));
        }
    }

    [Fact]
    public void FormationAnnouncedWhileDockedWaitsUntilTheShipIsInSpace()
    {
        var state=new Net7AutomaticActions();
        bool Observe(string name,bool space)
            =>state.Observe(0,false,0,false,false,false,true,false,name,0,false,false,true,space).Form;
        Assert.False(Observe("Block",false));
        Assert.False(Observe("Block",false));
        Assert.True(Observe("Block",true));
        Assert.False(Observe("Block",true));
    }

    [InstalledRetailFact]
    public void InterfaceSettingsOnlyEnableMultiboxing()
    {
        // The multibox settings live in the in-game menu; Interface keeps the switch.
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var state=new EnbSettingsState(assets){Multibox=new(FollowLeaderDock:true,StationFollowDistance:7)};
        Assert.False(state.ConvenienceCheck(12));
        state.ToggleConvenience(12);
        Assert.True(state.Multibox.Enabled);Assert.True(state.ConvenienceCheck(12));
        Assert.True(state.Multibox.FollowLeaderDock);Assert.Equal(7,state.Multibox.StationFollowDistance);
        state.ToggleConvenience(12);Assert.False(state.Multibox.Enabled);
    }

    [InstalledRetailFact]
    public void InterfaceSettingsLootAllIsOffUntilEnabled()
    {
        // The original loot window stays the default; Loot all is an Interface opt-in.
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var state=new EnbSettingsState(assets);
        Assert.False(state.ConvenienceCheck(13));Assert.False(new EnbQualityOfLifeOptions().LootAll);
        state.ToggleConvenience(13);
        Assert.True(state.QualityOfLife.LootAll);Assert.True(state.ConvenienceCheck(13));
        Assert.False(state.Multibox.Enabled);
    }
}
