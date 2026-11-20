using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class EnbStationConversationTests
{
    [Fact]
    public async Task ActionOnlySpaceVendorTradesAndClosesWithoutATalkTree()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        s.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var player=s.World.LocalPlayerAuxData;
        Set(player,"VendorInventory.2.ItemTemplateID",1001);Set(player,"VendorInventory.2.StackCount",7);Set(player,"VendorInventory.2.Price",120ul);
        var owner=new EnbStationInteractionRequest(7,null,s.World.PresentationHandoffVersion,0,0,0);
        typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(client,[0u]);
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var buy=new EnbStationPurchaseRequest(owner,new(7,identity,4,2,1001),false,false,120,7);
        Assert.False(await client.TradeStationItemAsync(buy));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(5u)));
        Assert.Null(s.Gameplay.TalkTree);
        Assert.False(await client.TradeStationItemAsync(buy with {Owner=owner with {HandoffVersion=owner.HandoffVersion+1}}));
        Assert.True(await client.TradeStationItemAsync(buy));
        Assert.Equal(0x27,Assert.Single(frames).Op);
        var transfer=Net7GameplayWire.DecodeInventoryTransferRequest(frames[0].Body);
        Assert.Equal(0u,transfer.Id);Assert.Equal(4u,transfer.FromInventory);Assert.Equal(2u,transfer.FromSlot);Assert.Equal(1u,transfer.Count);
        var close=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,0,true);
        Assert.True(await client.ReplyStationConversationAsync(close));
        Assert.Equal(Net7GameplayWire.BuildSelectTalkTree(7,0xe6),frames.Last().Body);
        Assert.False(await client.ReplyStationConversationAsync(close));
        Assert.False(await client.TradeStationItemAsync(buy));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(5u)));
        Assert.True(await client.TradeStationItemAsync(buy));
    }

    // 12:06:56 rescue response, capture 100634423Z frame 115, inner 0054.
    internal const string RescueTalkPayload = "VGhhdCB3YXMgb25lIGhlY2sgb2YgYW4gZXhwbG9zaW9uISBBcmUgeW91IGFscmlnaHQgb3ZlciB0aGVyZT8AAAMAAAAASSBuZWVkIGEgdG93AAEAAABUb2dnbGUgZGlzdHJlc3MgYmVhY29uAAIAAABJJ20gT0sAAA==";

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task CapturedRescueTalkPublishesAndRepliesThroughSharedDialog(byte choice)
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        s.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        Set(s.World.Objects.FindFirst(7)!.AuxData!,"IsIncapacitated",(byte)1);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x54,Convert.FromBase64String(RescueTalkPayload)),"captured rescue",CancellationToken.None])!;
        var conversation=client.LatestPresentationSnapshot!.StationConversation!;
        Assert.Equal(new[]{"I need a tow","Toggle distress beacon","I'm OK"},conversation.Tree!.Responses.Select(r=>r.Text));
        Assert.Equal(new uint[]{0,1,2},conversation.Tree.Responses.Select(r=>r.ResponseActionOrId));
        var owner=new EnbStationInteractionRequest(7,null,s.World.PresentationHandoffVersion,0,0,0);
        var request=new EnbStationReplyRequest(owner,conversation.Revision,choice);
        Assert.True(await client.ReplyStationConversationAsync(request));
        Assert.False(await client.ReplyStationConversationAsync(request));
        // The first materialization observing the incapacitated ship queues the
        // 00581670 onset release (Move 4, Turn 0, Tilt 0) before the reply.
        Assert.Equal(new ushort[]{0x14,0x12,0x13},frames.Take(3).Select(f=>f.Op));
        var sent=Assert.Single(frames.Skip(3));Assert.Equal(0x55,sent.Op);
        Assert.Equal(Net7GameplayWire.BuildSelectTalkTree(7,choice),sent.Body);
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x56,Convert.FromHexString("E0FFFFFF")),"captured rescue close",CancellationToken.None])!;
        var close=request with {Revision=s.Gameplay.TalkTreeRevision,Close=true};
        Assert.False(await client.ReplyStationConversationAsync(close with {Close=false}));
        Assert.True(await client.ReplyStationConversationAsync(close));
        Assert.False(await client.ReplyStationConversationAsync(close));
        Assert.Equal(Net7GameplayWire.BuildSelectTalkTree(7,0xe6),frames.Last().Body);
        // 004AAE90 retains the available final response without a NUL.
        byte[] captured = Convert.FromBase64String(RescueTalkPayload);
        var full = EnbNext.Net7.Presentation.Net7UiPresentationWire.DecodeTalkTree(captured);
        var bounded = EnbNext.Net7.Presentation.Net7UiPresentationWire.DecodeTalkTree(captured[..^2]);
        Assert.Equal(full.Responses, bounded.Responses);
    }

    // Retail captures 2026-11-16 185606/191413: Exit on the rescue talk sends 0055, the server answers 0056,
    // then a second 0055 follows (0064D400 events 6/0B queue E6; 0064C8D0 sends E6 on the negative action).
    // Closing after one E6 left the server's talk open and a later tow was refused.
    [Fact]
    public async Task RescueTalkExitQueuesE6AndStillAcknowledgesTheServerClose()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        s.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        Set(s.World.Objects.FindFirst(7)!.AuxData!,"IsIncapacitated",(byte)1);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x54,Convert.FromBase64String(RescueTalkPayload)),"captured rescue",CancellationToken.None])!;
        var owner=new EnbStationInteractionRequest(7,null,s.World.PresentationHandoffVersion,0,0,0);
        var exit=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,0xE6){Exit=true};
        Assert.False(await client.ReplyStationConversationAsync(exit with {Revision=exit.Revision-1}));
        Assert.True(await client.ReplyStationConversationAsync(exit));
        await (Task)dispatch.Invoke(s,[Packet.ForOpcode(0x56,Convert.FromHexString("E0FFFFFF")),"exit close",CancellationToken.None])!;
        var close=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,0,true);
        Assert.True(await client.ReplyStationConversationAsync(close));
        Assert.False(await client.ReplyStationConversationAsync(close));
        Assert.False(await client.ReplyStationConversationAsync(exit with {Revision=close.Revision}));
        var talk=frames.Where(f=>f.Op==0x55).ToArray();
        Assert.Equal(2,talk.Length);
        Assert.All(talk,f=>Assert.Equal(Net7GameplayWire.BuildSelectTalkTree(7,0xe6),f.Body));
    }

    // Capture frame 84, 2026-09-13 07:17 UTC: Vrix scan text then action 6.
    internal const string ScanTextPayload="594F5520415245204245494E47205343414E4E45442E20554E4B4E4F574E20454C454354524943414C204D414C46554E4354494F4E2E20556E7265636F676E697A6564204672657175656E63793A205343314E46345254483256335348313443344432582E2E2E4E32473154335632000000";
    [Fact]
    public async Task UnsolicitedSpaceDialogCloseUsesSharedTalkWireAndRejectsStaleOwners()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        s.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        s.Gameplay.Apply(Packet.ForOpcode(0x54,Convert.FromHexString(ScanTextPayload)));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,Convert.FromHexString("06000000")));
        var owner=new EnbStationInteractionRequest(7,null,s.World.PresentationHandoffVersion,0,0,0);
        var request=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,0,true);
        Assert.False(await client.BeginStationConversationAsync(owner));
        Assert.False(await client.ReplyStationConversationAsync(request with {Owner=owner with {HandoffVersion=owner.HandoffVersion+1}}));
        Assert.False(await client.ReplyStationConversationAsync(request with {Revision=request.Revision-1}));
        Assert.True(await client.ReplyStationConversationAsync(request));
        Assert.False(await client.ReplyStationConversationAsync(request));
        var close=Assert.Single(frames);Assert.Equal(0x55,close.Op);
        Assert.Equal(Net7GameplayWire.BuildSelectTalkTree(7,0xe6),close.Body);
        Assert.StartsWith("YOU ARE BEING SCANNED.",s.Gameplay.TalkTree!.NpcSay);
        Assert.Empty(s.Gameplay.TalkTree.Responses);
    }
}
