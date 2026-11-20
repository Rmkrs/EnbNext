using System.Reflection;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbRecoveryTests
{
    [Theory]
    [InlineData(-1,0,false)]
    [InlineData(0,0x8f,false)]
    [InlineData(1,0x8e,false)]
    [InlineData(1,0x8e,true)]
    [InlineData(2,0,false)]
    public async Task RecoveryChoiceIsScopedAndUsesRetailPackets(int choice,int opcode,bool beacon)
    {
        await using var client=new EnbClient(new());
        var s=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(EnbClient).GetField("_net7",flags)!.SetValue(client,s);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(s,7u);
        EnbR11FlightTests.FlightWorld(s.World);
        typeof(Net7GameplayState).GetProperty("PresentationMode")!.SetValue(s.Gameplay,0);
        var aux=s.World.Objects.FindFirst(7)!.AuxData!;
        aux.Resolve(28).Value=(byte)1;
        aux.Properties.Single(p=>p.Path=="ShipAuxData.IsRescueBeaconActive").Value=(byte)(beacon?1:0);
        var details=new Net7IncapacitationDetails("",Net7PresentationWireTests.BuildAvatarData("Rescuer"),["Reply","A","B","C","Tow","Beacon","Wait"]);
        typeof(Net7GameplayState).GetProperty("IncapacitationDetails")!.SetValue(s.Gameplay,details);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",flags)!.Invoke(client,[1000u]);
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var owner=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var request=new EnbRecoveryRequest(7,s.World.PresentationHandoffVersion,owner,details,choice);
        Assert.False(await client.ReplyRecoveryAsync(request with{ShipOwner=owner+1}));
        Assert.False(await client.ReplyRecoveryAsync(request with{Handoff=request.Handoff+1}));
        Assert.False(await client.ReplyRecoveryAsync(request with{Details=details with {}}));
        aux.Resolve(28).Value=(byte)0;
        Assert.False(await client.ReplyRecoveryAsync(request));
        aux.Resolve(28).Value=(byte)1;
        Assert.True(await client.ReplyRecoveryAsync(request));
        Assert.False(await client.ReplyRecoveryAsync(request));
        if(opcode==0)Assert.Empty(frames);
        else
        {
            var frame=Assert.Single(frames);Assert.Equal((ushort)opcode,frame.Op);
            Assert.Equal(choice==1?Net7GameplayWire.BuildDistressBeacon(7,!beacon):Net7GameplayWire.BuildIncapacitationRequest(7),frame.Body);
        }
    }
}
