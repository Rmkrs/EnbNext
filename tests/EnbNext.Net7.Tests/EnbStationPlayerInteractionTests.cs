using System.Numerics;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class EnbStationConversationTests
{
    [Fact]
    public async Task StationPlayerActionsUseRoomPeerNotSelectedTargetAndRejectStaleOwners()
    {
        await using var client=new EnbClient(new());var session=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x2c)frames.Add(frame.Payload.ToArray());};
        void Pose(uint room,uint action)=>session.World.Apply(Packet.ForOpcode(0x9e,
            Net7PresentationWireTests.BuildStarbaseAvatarChange(42,room,0,Vector3.Zero,action)),0);
        Pose(0,0);
        var request=new EnbStationPlayerInteraction(7,10651,session.World.PresentationHandoffVersion,0,42,"NPC",EnbStationPlayerAction.Trade);
        Assert.True(await client.StationPlayerAsync(request));
        Assert.True(await client.StationPlayerAsync(request with {Action=EnbStationPlayerAction.GroupInvite}));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.TradeRequest,42,0),frames[0]);
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.GroupInvite,42,0),frames[1]);
        Assert.False(await client.StationPlayerAsync(request with {Character=8}));
        Assert.False(await client.StationPlayerAsync(request with {HandoffVersion=request.HandoffVersion+1}));
        Assert.False(await client.StationPlayerAsync(request with {StarbaseId=1}));
        Assert.False(await client.StationPlayerAsync(request with {AvatarId=99}));
        Pose(1,0);Assert.False(await client.StationPlayerAsync(request));
        Pose(0,2);Assert.False(await client.StationPlayerAsync(request));
        Pose(0,0);Assert.False(await client.StationPlayerAsync(request with {Action=EnbStationPlayerAction.Chat}));
        Assert.Equal(2,frames.Count);
        uint? selected=session.Gameplay.LocalSelectedTargetId;
        Assert.True(await client.InviteOwnCharacterAsync(99));
        Assert.Equal(Net7GameplayWire.BuildAction(7,Net7PlayerAction.GroupInvite,99,0),frames[2]);
        Assert.Equal(selected,session.Gameplay.LocalSelectedTargetId);
        Assert.False(await client.InviteOwnCharacterAsync(7));
        Assert.False(await client.InviteOwnCharacterAsync(0));
        Assert.Equal(3,frames.Count);
    }
}
