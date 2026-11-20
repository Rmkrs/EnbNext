using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class EnbStationConversationTests
{
    [Fact]
    public async Task NewsTerminalIsLocalAndRejectsGameplayActions()
    {
        await using var client = new EnbClient(new()); var s = Attach(client); var owner = TerminalOwner(s,4);
        var (_,channel,receiver) = await Net7R11FlightWireTests.AttachLoopback(s); using var lifetime = receiver;
        var frames = new List<ushort>(); channel.FrameSent += (f,_) => frames.Add(f.Opcode);
        Assert.Null(client.GetStationBrowserQuery(owner));
        Assert.False(await client.StationTerminalAsync(new(owner with {Slot=9},EnbTerminalCommand.Open)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        typeof(Net7Session).GetProperty("CharacterSelection")!.SetValue(s,
            new Net7CharacterSelectionResult(0,null!,null!,7,0x6f,EnbNext.Net7.Protocol.Net7SectorJoinClientData.CreateSuccessful(7,42,0)));
        Assert.Equal("characterid=117440512&lkey=",client.GetStationBrowserQuery(owner));
        Assert.Null(client.GetStationBrowserQuery(owner with {HandoffVersion=owner.HandoffVersion+1}));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Action,2)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.Null(client.GetStationBrowserQuery(owner));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.Empty(frames);
    }
    private static EnbStationTerminalOwner TerminalOwner(Net7Session s,uint type)
    {
        typeof(Net7GameplayState).GetProperty("LoungeDefinition")!.SetValue(s.Gameplay,
            s.Gameplay.LoungeDefinition! with {Facilities=[new(0,2,type,0)]});
        return new(7,10651,s.World.PresentationHandoffVersion,0,2,type);
    }
    [Theory]
    [InlineData(0,4)] [InlineData(1,2)] [InlineData(2,1)]
    public async Task TerminalsOpenTransferAndCloseWithCurrentOwner(uint type,uint mode)
    {
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,type);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        Assert.False(await client.StationTerminalAsync(new(owner with{Slot=9},EnbTerminalCommand.Open)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        var open=frames.Single(f=>f.Op==0x79).Body;
        Assert.Equal(7u,BinaryPrimitives.ReadUInt32BigEndian(open));Assert.Equal(mode,BinaryPrimitives.ReadUInt32BigEndian(open.AsSpan(4)));
        var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",8);
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var move=new EnbInventoryMoveRequest(new(7,identity,1,1,1001),8,12,0,null,3);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Move){Move=move}));
        var transfer=Net7GameplayWire.DecodeInventoryTransferRequest(frames.Single(f=>f.Op==0x27).Body);
        Assert.Equal(0u,transfer.Id);Assert.Equal(1u,transfer.FromInventory);Assert.Equal(12u,transfer.ToInventory);Assert.Equal(3u,transfer.Count);
        Set(ship,"Inventory.Cargo.1.StackCount",2);
        // 00690D20 sends the retained quantity without resampling the source count (INV-D35).
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Move){Move=move}));
        Assert.False(await s.TransferManufacturingItemAsync(1,1,13,0,1));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.Equal(new ushort[]{0x7e,0x79},frames.TakeLast(2).Select(f=>f.Op));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Move){Move=move}));
    }
    [Fact]
    public async Task JobsAccumulateBatchesAndOnlyAcceptAnAvailableListedJob()
    {
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,3);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x4e)frames.Add(f.Payload.ToArray());};
        void Batch(uint id)=>s.Gameplay.Apply(Packet.ForOpcode(0x93,Net7UiPresentationWireTests.Build(w=>{w.U32(1);w.U32(id);w.U32(0);w.U32(0);w.U32(50);w.CString("Job");w.CString("Sponsor");w.CString("Reward");})));
        Batch(999);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));Assert.Empty(s.Gameplay.JobList);
        Batch(42);Batch(43);Assert.Equal(2,s.Gameplay.JobList.Count);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobAccept,42)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobDescription,42)));
        Assert.True(client.LatestPresentationSnapshot!.StationJobRequestPending);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobDescription,43)));
        s.Gameplay.Apply(Packet.ForOpcode(0x94,Net7UiPresentationWireTests.Build(w=>{w.U32(42);w.U8(1);w.CString("Job");w.CString("Details");})));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobAccept,42)));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobAccept,42)));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobDescription,43)));
        s.Gameplay.Apply(Packet.ForOpcode(0x95,BitConverter.GetBytes(42u)));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobAccept,42)));
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.False(s.Gameplay.StationJobRequestPending);
        Assert.Equal(new[]{Net7GameplayWire.BuildStarbaseRequest(7,0,6),Net7GameplayWire.BuildStarbaseRequest(7,42,7),Net7GameplayWire.BuildStarbaseRequest(7,42,8),Net7GameplayWire.BuildStarbaseRequest(7,0,9)},frames);
    }
    [Theory]
    [InlineData(0,true)] [InlineData(1,true)] [InlineData(2,false)] [InlineData(3,false)]
    public void JobAcceptanceReplyRemovesOnlyAcceptedOrUnavailableJob(uint status,bool removed)
    {
        var state=new Net7GameplayState();
        state.BeginStationJobRequest(42,true);
        state.Apply(Packet.ForOpcode(0x93,Net7UiPresentationWireTests.Build(w=>{w.U32(1);w.U32(42);w.U32(0);w.U32(0);w.U32(50);w.CString("Job");w.CString("Sponsor");w.CString("1000");})));
        state.Apply(Packet.ForOpcode(0x96,Net7UiPresentationWireTests.Build(w=>{w.U32(42);w.U32(status);w.U32(60);})));
        Assert.Equal(removed,state.JobList.Count==0);Assert.Equal(status,state.LastJobAcceptReply!.AcceptStatus);
        Assert.False(state.StationJobRequestPending);
        state.BeginStationJobDescription();Assert.Null(state.LastJobAcceptReply);Assert.Null(state.JobDescription);
    }
    [Fact]
    public void PendingJobSelectionWaitsForItsReplyAndClearsAtHandoff()
    {
        var state=new Net7GameplayState();
        void Description(uint id)=>state.Apply(Packet.ForOpcode(0x94,Net7UiPresentationWireTests.Build(w=>{w.U32(id);w.U8(1);w.CString("Job");w.CString("Details");})));
        state.BeginStationJobRequest(42,false);
        Description(41);Assert.True(state.StationJobRequestPending);
        Description(42);Assert.False(state.StationJobRequestPending);
        state.BeginStationJobRequest(42,true);
        state.ResetForServerHandoff();Assert.False(state.StationJobRequestPending);
    }
    [Fact]
    public async Task EquipmentAndReloadUseMappedSlotsAndRevalidateBeforeSending()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x27)frames.Add(f.Payload.ToArray());};
        var template=Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        template=template with {Info=template.Info with {Attributes=[]}};
        s.ItemTemplates.Materialize(template with {ItemTemplateId=1001,ItemType=16});
        s.ItemTemplates.Materialize(template with {ItemTemplateId=1002,ItemType=16});
        s.ItemTemplates.Materialize(template with {ItemTemplateId=1003,ItemType=10});
        var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.CargoSpace",30);
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",1);
        Set(ship,"Inventory.Cargo.2.ItemTemplateID",1003);Set(ship,"Inventory.Cargo.2.StackCount",80);
        Set(ship,"Inventory.Equipped.8.ItemTemplateID",1002);Set(ship,"Inventory.Equipped.8.StackCount",1);
        foreach(var (slot,mask) in new[]{(3,1u<<16),(8,1u<<16),(9,4u)})
        {var raw=new byte[12];BinaryPrimitives.WriteUInt32LittleEndian(raw,mask);Set(ship,$"Inventory.Mounts.{slot}",raw);}
        Set(ship,"Inventory.FutureWeapons",5);
        await client.BeginStationConversationAsync(new(7,10651,s.World.PresentationHandoffVersion,0,3,123));
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var move=new EnbInventoryMoveRequest(new(7,identity,1,1,1001),1,2,3,null,1);
        Assert.True(await client.MoveInventoryItemAsync(move));
        Assert.Equal(Convert.FromHexString("000000000000000100000001000000020000000300000001"),frames.Last());
        Assert.True(await client.MoveInventoryItemAsync(move with {DestinationSlot=8,DestinationTemplate=1002}));
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,2,8,1002),1,1,1,1001,1)));
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,2,8,1002),1,3,0,null,1)));
        //005A0740 ->005A0D00 dispatches to an existing mount without checking
        //the dragged item's type against its artwork/placement mask.
        Assert.True(await client.MoveInventoryItemAsync(move with {DestinationSlot=9}));
        Assert.Equal(Convert.FromHexString("000000000000000100000001000000020000000900000001"),frames.Last());
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,2,8,1002),1,1,2,1003,1)));
        Assert.Equal(Convert.FromHexString("000000000000000200000008000000010000000200000001"),frames.Last());
        int sent=frames.Count;
        Assert.False(await client.MoveInventoryItemAsync(move with {DestinationSlot=4}));
        Assert.Equal(sent,frames.Count);
        var reload=new EnbInventoryMoveRequest(new(7,identity,1,2,1003),80,2,8,1002,23);
        Assert.True(await client.MoveInventoryItemAsync(reload));
        Assert.Equal(Convert.FromHexString("000000000000000100000002000000020000000800000017"),frames.Last());
        //00690D20 sends the retained quantity without resampling the source count (INV-D35/D14).
        Assert.True(await client.MoveInventoryItemAsync(reload with {SourceCount=79}));
        sent=frames.Count;
        Set(ship,"Inventory.Ammo.8.ItemTemplateID",1003);Set(ship,"Inventory.Ammo.8.StackCount",4);
        Assert.False(await client.MoveInventoryItemAsync(reload));Assert.Equal(sent,frames.Count);
        Assert.False(await client.MoveInventoryItemAsync(new(new(7,identity,2,8,1002),1,1,0,null,1)));
        // 005A0D00 starts the move onto a loaded weapon mount without inspecting its ammunition.
        Assert.True(await client.MoveInventoryItemAsync(move with {DestinationSlot=8,DestinationTemplate=1002}));
        Assert.Equal(Convert.FromHexString("000000000000000100000001000000020000000800000001"),frames.Last());
        var unload=new EnbInventoryMoveRequest(new(7,identity,2,8,1003){AmmoLayer=true},4,1,0,null,3);
        Assert.True(await client.MoveInventoryItemAsync(unload));
        Assert.Equal(Convert.FromHexString("000000000000000200000008000000010000000000000003"),frames.Last());
        Set(ship,"Inventory.Ammo.8.StackCount",0);
        Assert.False(await client.MoveInventoryItemAsync(unload));
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,2,8,1002),1,1,0,null,1)));
        var inventory=EnbSceneInventorySnapshotBuilder.Build(ship,s.World.LocalPlayerAuxData,s.ItemTemplates,null);
        Assert.Equal(new[]{3,8,9},inventory.Mounts.Select(m=>m.Slot));
        Assert.Equal(5,inventory.FutureWeapons);
        Assert.Equal(80,inventory.Cargo.Single(i=>i.Slot==2).Count);
    }

    [Fact]
    public async Task ItemRequirementOverlayFollowsCurrentCharacterRatherThanTransferEligibility()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",1);
        var inventory=EnbSceneInventorySnapshotBuilder.Build(ship,s.World.LocalPlayerAuxData,null,null);
        inventory=inventory with {Skills=[new(1,"Shield Tech","",2,9,"")],CharacterStats=inventory.CharacterStats.SetItem("Race","0").SetItem("Profession","2").SetItem("CombatLevel","10")};
        inventory=inventory with {RequirementState=new(true,true,
            ImmutableDictionary<string,int>.Empty.Add("Shield Tech",2),
            ImmutableDictionary<string,int>.Empty.Add("Race",0).Add("Profession",2).Add("CombatLevel",10))};
        var item=inventory.Cargo.Single() with {Level=3,Attributes=[new(11,"Shield Tech")]};
        Assert.True(EnbItemInformation.HasUnmetRequirements(item,inventory));
        Assert.False(EnbItemInformation.HasUnmetRequirements(item with{Level=2},inventory));
        Assert.True(EnbItemInformation.HasUnmetRequirements(item with{Attributes=[new(18,"1")]},inventory));
        Assert.False(EnbItemInformation.HasUnmetRequirements(item with{Attributes=[new(18,"4")]},inventory));
        Assert.True(EnbItemInformation.HasUnmetRequirements(item with{Attributes=[new(3,"4")]},inventory));
        Assert.True(EnbItemInformation.HasUnmetRequirements(item with{Attributes=[new(4,"11")]},inventory));
    }
    [Fact]
    public async Task InventoryMovesSwapsQuickTransfersAndConfirmedDumpReachWireWithoutOptimisticMutation()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x27)frames.Add(f.Payload.ToArray());};
        var player=s.World.LocalPlayerAuxData;var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.CargoSpace",30);Set(ship,"Inventory.Cargo.30.ItemTemplateID",-2);
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",7);
        Set(player,"SecureInventory.2.ItemTemplateID",1002);Set(player,"SecureInventory.2.StackCount",3);
        // Publish the current local object exactly as the normal client does.
        await client.BeginStationConversationAsync(new(7,10651,s.World.PresentationHandoffVersion,0,3,123));
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var move=new EnbInventoryMoveRequest(new(7,identity,1,1,1001),7,1,2,null,7);
        Assert.True(await client.MoveInventoryItemAsync(move));
        Assert.Equal(2u,Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last()).ToSlot);
        Assert.True(await client.MoveInventoryItemAsync(move with{Destination=3,DestinationSlot=2,DestinationTemplate=1002}));
        Assert.True(await client.MoveInventoryItemAsync(move with{Destination=3,DestinationSlot=-1,Count=1}));
        var wire=Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last());
        Assert.Equal(uint.MaxValue,wire.ToSlot);Assert.Equal(1u,wire.Count);Assert.Equal(0u,wire.Id);
        Assert.False(await client.MoveInventoryItemAsync(move with{Destination=0x0B,DestinationSlot=-1}));
        Assert.True(await client.MoveInventoryItemAsync(move with{Destination=0x0B,DestinationSlot=-1,DestroyConfirmed=true}));
        Assert.Equal(Convert.FromHexString("0000000000000001000000010000000BFFFFFFFF00000007"),frames.Last());
        // 005A0EB0 starts the dump for equipment as well (UI_TRANSACTION_DUMP_ITEM confirms it).
        Set(ship,"Inventory.Equipped.4.ItemTemplateID",1001);Set(ship,"Inventory.Equipped.4.StackCount",1);
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,2,4,1001),1,0x0B,-1,null,1){DestroyConfirmed=true}));
        Assert.Equal(Convert.FromHexString("0000000000000002000000040000000BFFFFFFFF00000001"),frames.Last());
        Assert.True(await client.MoveInventoryItemAsync(new(new(7,identity,3,2,1002),3,1,3,null,3)));
        int sent=frames.Count;
        Set(ship,"Inventory.Cargo.2.ItemTemplateID",-2);
        Assert.False(await client.MoveInventoryItemAsync(move)); // formerly empty destination became unavailable
        Assert.Equal(sent,frames.Count);
        Assert.False(await client.MoveInventoryItemAsync(move with{Source=move.Source with{ShipOwner=identity+1}}));
        Assert.False(await client.MoveInventoryItemAsync(move with{DestinationSlot=30}));
        Set(ship,"Inventory.Cargo.1.StackCount",6);
        Assert.False(await client.MoveInventoryItemAsync(move));Assert.Equal(sent,frames.Count);
        Assert.Equal(1001,EnbSceneInventorySnapshotBuilder.Build(ship,player,null,null).Cargo.Single(i=>i.Slot==1).TemplateId);
    }
    [Fact]
    public async Task QuickTransferNeedsTheStationInterfaceButNotAnOpenVaultPane()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.CargoSpace",30);
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",7);
        var scene=new EnbSceneFrame();scene.Hud.Inventory=EnbSceneInventorySnapshotBuilder.Build(ship,s.World.LocalPlayerAuxData,null,null);
        using var ui=new EnbNext.Engine.EnbInventoryUi(()=>123);
        typeof(EnbNext.Engine.EnbInventoryUi).GetField("_scene",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(ui,scene);
        var moves=new List<EnbInventoryMoveRequest>();ui.MoveItemRequested=r=>{moves.Add(r);return Task.FromResult(true);};
        var source=new EnbInventoryItemRequest(123,0,1,1,1001);
        // 005A0740 transfers only while the station interface is active (SClient+0x1350 == +0x135C).
        await ui.QuickTransfer(source,true);
        Assert.Empty(moves);
        scene.ActiveStarbaseId=10651;
        await ui.QuickTransfer(source,true);
        var stack=Assert.Single(moves);
        Assert.Equal((byte)3,stack.Destination);Assert.Equal(-1,stack.DestinationSlot);Assert.Equal(7,stack.Count);
        await ui.QuickTransfer(source,false);
        Assert.Equal(1,moves[^1].Count);
    }

    [Fact]
    public async Task CapturedKerrSoundPublishesIndependentlyAndIsNotLostByTheSilentTalkTree()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        // Normal-session capture, 2026-09-08 19:45:41: sound 006A precedes talk tree 0054.
        var frame=Convert.FromBase64String("ggAWIApCAEA0AAAAGwBqAA4AAABrZXJyX2RlZmF1bHQyAAAAAAABWwBUAEVybW0uLi5QcmFjdGljaW5nLi4uIGp1c3QgZXhlcmNpc2luZyBteSB2b2ljZS4uLmhlaCBoZWggaGVoLi4udW1tbS4AAAEDAAAAUHJhY3RpY2luZyA/AA==");
        for(int offset=12;offset<frame.Length;)
        {
            int size=BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset));
            ushort opcode=BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset+2));
            await (Task)dispatch.Invoke(s,[Packet.ForOpcode(opcode,frame[(offset+4)..(offset+size)]),"offline Kerr capture",CancellationToken.None])!;
            Assert.Equal("kerr_default2",Assert.Single(client.LatestPresentationSnapshot!.ClientSoundEvents).Name);
            offset+=size;
        }
        var scene=client.LatestPresentationSnapshot!;
        Assert.Equal(0u,scene.ClientSoundEvents[0].Channel);Assert.Equal((byte)1,scene.ClientSoundEvents[0].Queue);
        Assert.Contains("Practicing",scene.StationConversation!.Tree!.NpcSay);
        Assert.Same(s.Gameplay.ClientSoundEvents,scene.ClientSoundEvents);
        var stream=scene.ClientSoundStreamId;s.Gameplay.ResetForServerHandoff();
        Assert.Single(s.Gameplay.ClientSoundEvents);Assert.Equal(stream,s.Gameplay.ClientSoundStreamId);
        s.Gameplay.ResetForCharacterSelection();
        Assert.Empty(s.Gameplay.ClientSoundEvents);Assert.NotEqual(stream,s.Gameplay.ClientSoundStreamId);
        Assert.Single(scene.ClientSoundEvents);
    }

    [Theory]
    [InlineData("/salute Welcome!", "Welcome!")]
    [InlineData("Thanks /give and goodbye.", "Thanks and goodbye.")]
    [InlineData("Ready?.", "Ready?")]
    [InlineData("Hello /salute", "Hello ")]
    public void DialogueDisplayConsumesRetailCommands(string raw,string expected)
    {
        Assert.Equal(expected,EnbStationDialogueText.Display(raw));
    }

    [Fact]
    public async Task DispatchedDialogueAndTradeActionsPublishWithoutAnUnrelatedWorldPacket()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        async Task Receive(ushort opcode,byte[] payload)=>await (Task)dispatch.Invoke(s,[Packet.ForOpcode(opcode,payload),"offline station reply",CancellationToken.None])!;
        // The 16:50:16 normal-session reply, inside outer 2016 sequence 51.
        var captured=Convert.FromBase64String("gAAWIApCAEAzAAAAdABUAEdyZWV0aW5ncyBQcm9nZW4uIEkgYW0gQ2hpZWYgVGVjaG5pY2FuIEJvcnMgU29sdXMuAAABAgAAAEdyZWV0aW5ncyBJIGFtIEh1cm9uaW1vdXMuIFdoYXQgc2VydmljZXMgZG8geW91IG9mZmVyPwA=");
        await Receive(0x54,captured[16..]);
        var first=client.LatestPresentationSnapshot!.StationConversation!;
        Assert.Contains("Bors Solus",first.Tree!.NpcSay);
        Assert.Equal(2u,Assert.Single(first.Tree.Responses).ResponseActionOrId);
        await Receive(0x56,BitConverter.GetBytes(3u));
        var trade=client.LatestPresentationSnapshot!.StationConversation!;
        Assert.True(trade.TradeAvailable);Assert.True(trade.Revision>first.Revision);
        Assert.False(first.TradeAvailable);
        await Receive(0x56,BitConverter.GetBytes(5u));
        var openRevision=client.LatestPresentationSnapshot!.StationConversation!.TradeOpenRevision;
        Assert.NotEqual(0ul,openRevision);
        await Receive(0x56,BitConverter.GetBytes(3u));
        Assert.Equal(openRevision,client.LatestPresentationSnapshot!.StationConversation!.TradeOpenRevision);
        Assert.Equal(3u,client.LatestPresentationSnapshot!.StationConversation!.Action!.Action);
        await Receive(0x56,BitConverter.GetBytes(4u));
        Assert.False(client.LatestPresentationSnapshot!.StationConversation!.TradeAvailable);
        await Receive(0x56,BitConverter.GetBytes(uint.MaxValue));
        Assert.Equal(uint.MaxValue,client.LatestPresentationSnapshot!.StationConversation!.Action!.Action);
    }
    [Fact]
    public async Task StationTradeDragWithoutSelectionSendsAndPublishesTheServerOffer()
    {
        await using var client=new EnbClient(new());var session=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(frame,_)=>{if(frame.Opcode==0x27)frames.Add(frame.Payload.ToArray());};
        var ship=session.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.CargoSpace",30);Set(ship,"Inventory.Cargo.0.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.0.StackCount",1);
        session.Gameplay.TradeDomain.ApplyCoordinator(new(999,0));
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        publish.Invoke(client,[100u]);
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var request=new EnbInventoryMoveRequest(new(7,identity,1,0,1001),1,16,0,null,1){TradeRevision=session.Gameplay.TradeDomain.SessionRevision};
        Assert.Null(session.Gameplay.LocalSelectedTargetId);
        Assert.True(await client.MoveInventoryItemAsync(request));
        Assert.Equal(Net7GameplayWire.BuildInventoryTransfer(0,1,0,16,0,1),Assert.Single(frames));
        Assert.Null(client.LatestPresentationSnapshot.Hud.Inventory!.Find(16,0));
        Set(ship,"Inventory.PlayerTrade.0.ItemTemplateID",1001);Set(ship,"Inventory.PlayerTrade.0.StackCount",1);
        Set(ship,"Inventory.Cargo.0.ItemTemplateID",-1);publish.Invoke(client,[101u]);
        Assert.Equal(1001,client.LatestPresentationSnapshot!.Hud.Inventory!.Find(16,0)!.TemplateId);
        Assert.False(await client.MoveInventoryItemAsync(request));Assert.Single(frames);
    }
    private static Net7Session Attach(EnbClient client, Action<string>? trace = null)
    {
        var s=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"}, trace);
        typeof(EnbClient).GetField("_net7",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,s);
        typeof(EnbClient).GetProperty("State")!.SetValue(client,ClientLifecycleState.InWorld);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(s,7u);s.World.SetLocalPlayerId(7);
        var create=new byte[23];BinaryPrimitives.WriteUInt32LittleEndian(create,7);BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(4),1);
        s.World.Apply(Packet.ForOpcode(4,create),0);
        var bytes=Net7PresentationWireTests.BuildAvatarData("NPC");var avatar=new byte[bytes.Length+4];BinaryPrimitives.WriteUInt32LittleEndian(avatar,42);bytes.CopyTo(avatar,4);
        s.World.Apply(Packet.ForOpcode(0x61,avatar),0);
        typeof(Net7GameplayState).GetProperty("LoungeDefinition")!.SetValue(s.Gameplay,new Net7LoungeDefinitionUpdate(2,[],[],[new(0,3,123,0,2,[],"NPC",s.World.AvatarDescriptions[42])]));
        s.PlayerSemantics.ObserveStarbaseSet(new(10651,0,0));return s;
    }
    private static void Tree(Net7Session s,uint choice=329)=>s.Gameplay.Apply(Packet.ForOpcode(0x54,Net7UiPresentationWireTests.Build(w=>{w.CString("Offer");w.CString("");w.U8(1);w.U32(choice);w.CString("Accept mission");})));
    private static void Set(Net7AuxDataSchema schema,string path,object value)
    {
        var p=schema.Properties.Single(p=>p.Path==schema.Name+"."+path);
        typeof(Net7AuxDataProperty).GetProperty("Value")!.SetValue(p,value);typeof(Net7AuxDataProperty).GetProperty("State")!.SetValue(p,2);
    }
    [Fact]
    public async Task NpcDefinitionIdentityReplyValueCloseAndStaleGuardsReachTheWire()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var owner=new EnbStationInteractionRequest(7,10651,s.World.PresentationHandoffVersion,0,3,123);
        Assert.False(await client.BeginStationConversationAsync(owner with{NpcId=3}));
        Assert.True(await client.BeginStationConversationAsync(owner));
        Assert.Equal(Net7GameplayWire.BuildStarbaseRequest(7,123,4),frames.Single(f=>f.Op==0x4e).Body);
        Tree(s);var reply=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,329);
        Assert.False(await client.ReplyStationConversationAsync(reply with{Selection=0}));
        Assert.False(await client.ReplyStationConversationAsync(reply with{Revision=0}));
        Assert.True(await client.ReplyStationConversationAsync(reply));
        Assert.False(await client.ReplyStationConversationAsync(reply));
        Assert.Equal(Convert.FromHexString("0700000049"),frames.Single(f=>f.Op==0x55).Body);
        Assert.True(await client.ReplyStationConversationAsync(reply with{Close=true}));
        Assert.Equal(Convert.FromHexString("07000000E6"),frames.Last().Body);
        Assert.False(await client.ReplyStationConversationAsync(reply));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task NpcOnlyContinuationSendsZeroWithoutClosingAndKeepsStaleGuards(bool space)
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        if(space)s.PlayerSemantics.ObserveStarbaseSet(new(10651,1,0));
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x55)frames.Add(f.Payload.ToArray());};
        var owner=new EnbStationInteractionRequest(7,space?null:10651u,s.World.PresentationHandoffVersion,0,3,123);
        if(!space)Assert.True(await client.BeginStationConversationAsync(owner));
        s.Gameplay.Apply(Packet.ForOpcode(0x54,Net7UiPresentationWireTests.Build(w=>{w.CString("Last page");w.CString("");w.U8(0);})));
        var request=new EnbStationReplyRequest(owner,s.Gameplay.TalkTreeRevision,0);
        Assert.False(await client.ReplyStationConversationAsync(request with{Selection=1}));
        Assert.False(await client.ReplyStationConversationAsync(request with{Revision=0}));
        Assert.False(await client.ReplyStationConversationAsync(request with{Owner=owner with{HandoffVersion=owner.HandoffVersion+1}}));
        Assert.True(await client.ReplyStationConversationAsync(request));
        Assert.False(await client.ReplyStationConversationAsync(request));
        Assert.Equal(Convert.FromHexString("0700000000"),Assert.Single(frames));
        Tree(s);Assert.True(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,329)));
        Assert.Equal(Convert.FromHexString("0700000049"),frames.Last());
        Assert.True(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,0,true)));
        Assert.Equal(Convert.FromHexString("07000000E6"),frames.Last());
    }

    [Fact]
    public void DoneCaptionSurvivesUnrelatedActionsAndResetsForANewConversation()
    {
        var state=new Net7GameplayState();
        void Action(uint value)=>state.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(value)));
        Action(6);Action(3);Assert.True(state.TalkTreeDoneCaption);
        state.BeginStationConversation();Assert.False(state.TalkTreeDoneCaption);
        Action(7);Assert.True(state.TalkTreeDoneCaption);
        Action(uint.MaxValue);
        state.Apply(Packet.ForOpcode(0x54,Net7UiPresentationWireTests.Build(w=>{w.CString("New talk");w.CString("");w.U8(0);})));
        Assert.False(state.TalkTreeDoneCaption);
    }

    [Theory]
    [InlineData(0u,false)] [InlineData(7u,false)] [InlineData(8u,true)] [InlineData(uint.MaxValue,true)]
    public void TalkActionExitBranchMatchesRetailDispatch(uint action,bool closes)
        =>Assert.Equal(closes,new Net7TalkTreeActionUpdate(action).EndsConversation);

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(2u)] [InlineData(3u)]
    [InlineData(4u)] [InlineData(5u)] [InlineData(6u)] [InlineData(7u)]
    public void LaterActionAndTextCannotEraseUnconsumedDialogueClose(uint laterAction)
    {
        var state=new Net7GameplayState();
        state.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(uint.MaxValue)));
        ulong close=state.TalkTreeEndRevision;
        state.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(laterAction)));
        state.Apply(Packet.ForOpcode(0x54,Net7UiPresentationWireTests.Build(w=>{w.CString("Still active");w.CString("");w.U8(0);})));
        var publisher=new EnbPresentationSnapshotPublisher();
        var snapshot=publisher.Publish(new Net7WorldState(),null,null,0,gameplay:state);
        Assert.NotEqual(0ul,close);
        Assert.Equal(close,snapshot.StationConversation!.EndRevision);
        Assert.Equal(laterAction,snapshot.StationConversation.Action!.Action);
        Assert.Equal(laterAction is 6 or 7 ? close+1 : 0ul,snapshot.StationConversation.DoneRevision);
        state.BeginStationConversation();
        Assert.Equal(0ul,state.TalkTreeEndRevision);
        Assert.Equal(0ul,state.TalkTreeDoneRevision);
    }

    [Fact]
    public async Task SkillControlUsesItsAuthoredEighthResponseWithoutExposingExtraReplyRows()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x55)frames.Add(f.Payload.ToArray());};
        var owner=new EnbStationInteractionRequest(7,10651,s.World.PresentationHandoffVersion,0,3,123);
        Assert.True(await client.BeginStationConversationAsync(owner));
        s.Gameplay.Apply(Packet.ForOpcode(0x54,Net7UiPresentationWireTests.Build(w=>
        {w.CString("Offer");w.CString("");w.U8(8);for(uint i=0;i<8;i++){w.U32(300+i);w.CString("Choice");}})));
        Assert.False(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,307)));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(1u)));
        Assert.True(s.Gameplay.TalkTreeSkillAvailable);
        Assert.False(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,305)));
        Assert.True(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,307)));
        Assert.Equal(Convert.FromHexString("0700000033"),Assert.Single(frames));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(2u)));
        Assert.False(s.Gameplay.TalkTreeSkillAvailable);
        Assert.False(await client.ReplyStationConversationAsync(new(owner,s.Gameplay.TalkTreeRevision,307)));
    }

    [Fact]
    public async Task VendorUsesEmptyTargetCacheAndServerPricesAndProtectsChangedItems()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(f,_)=>{if(f.Opcode==0x27)frames.Add(f.Payload.ToArray());};
        var player=s.World.LocalPlayerAuxData;var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(player,"VendorInventory.2.ItemTemplateID",1001);Set(player,"VendorInventory.2.StackCount",7);Set(player,"VendorInventory.2.Price",120ul);
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1002);Set(ship,"Inventory.Cargo.1.StackCount",3);Set(ship,"Inventory.Cargo.1.Price",50ul);Set(ship,"Inventory.Cargo.1.AveCost",100f);
        //006531F0 warns only for commodity template type13, not every item with AveCost.
        var commodity=Net7ItemTemplateWire.Decode(Net7CDataCatalogTests.BuildSessionItemTemplatePayload());
        s.ItemTemplates.Materialize(commodity with {ItemTemplateId=1002,ItemType=13,Info=commodity.Info with {Attributes=[]}});
        var owner=new EnbStationInteractionRequest(7,10651,s.World.PresentationHandoffVersion,0,3,123);
        Assert.True(await client.BeginStationConversationAsync(owner));
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(5u)));
        Assert.False(s.Gameplay.TalkTreeTradeAvailable);
        Assert.True(s.Gameplay.TalkTreeVendorAuthorized);
        s.Gameplay.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(4u)));
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var buy=new EnbStationPurchaseRequest(owner,new(7,identity,4,2,1001),false,false,120,7);
        Assert.False(await client.TradeStationItemAsync(buy with{Price=119}));
        Assert.False(await client.TradeStationItemAsync(buy with{Item=buy.Item with{ShipOwner=identity+1}}));
        Assert.True(await client.TradeStationItemAsync(buy));
        var wire=Net7GameplayWire.DecodeInventoryTransferRequest(Assert.Single(frames));
        Assert.Equal(0u,wire.Id);Assert.Equal(4u,wire.FromInventory);Assert.Equal(2u,wire.FromSlot);Assert.Equal(1u,wire.ToInventory);Assert.Equal(uint.MaxValue,wire.ToSlot);Assert.Equal(1u,wire.Count);
        Assert.False(await client.TradeStationItemAsync(buy with{Stack=true}));
        Assert.True(await client.TradeStationItemAsync(buy with{Stack=true,Confirmed=true}));
        // 005A02E0: without a client item definition the stack request is one item.
        Assert.Equal(1u,Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last()).Count);
        // With one, Num is the definition MaxStack (+0x50), not the vendor slot StackCount.
        s.ItemTemplates.Materialize(commodity with {ItemTemplateId=1001,MaxStack=20,Info=commodity.Info with {Attributes=[]}});
        Assert.True(await client.TradeStationItemAsync(buy with{Stack=true,Confirmed=true}));
        Assert.Equal(20u,Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last()).Count);
        var sell=new EnbStationPurchaseRequest(owner,new(7,identity,1,1,1002),false,false,50,3);
        Assert.False(await client.TradeStationItemAsync(sell));
        Assert.True(await client.TradeStationItemAsync(sell with{Confirmed=true}));
        wire=Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last());Assert.Equal(1u,wire.FromInventory);Assert.Equal(4u,wire.ToInventory);
        Set(player,"VendorInventory.2.ItemTemplateID",1003);
        Assert.False(await client.TradeStationItemAsync(buy));Assert.Equal(4,frames.Count);
        Assert.Equal(7,player.Properties.Single(p=>p.Path.EndsWith("VendorInventory.2.StackCount")).Value);
        // Retail 00689DA0 reads an omitted Price as zero, not an unsellable item.
        Set(ship,"Inventory.Cargo.2.ItemTemplateID",1004);Set(ship,"Inventory.Cargo.2.StackCount",1);
        var zeroPrice=new EnbStationPurchaseRequest(owner,new(7,identity,1,2,1004),false,false,0,1);
        Assert.True(await client.TradeStationItemAsync(zeroPrice));
        wire=Net7GameplayWire.DecodeInventoryTransferRequest(frames.Last());
        Assert.Equal(2u,wire.FromSlot);Assert.Equal(1u,wire.Count);Assert.Equal(5,frames.Count);
    }
    [Fact]
    public async Task OwnedCountsAuthoritativeCargoEquipmentAmmoAndVaultWithoutCountingVendorStock()
    {
        await using var client=new EnbClient(new());var s=Attach(client);
        var player=s.World.LocalPlayerAuxData;var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.Cargo.1.ItemTemplateID",1001);Set(ship,"Inventory.Cargo.1.StackCount",2);
        Set(player,"VendorInventory.2.ItemTemplateID",1001);Set(player,"VendorInventory.2.StackCount",240);
        EnbSceneInventorySnapshot Snapshot()=>EnbSceneInventorySnapshotBuilder.Build(ship,player,null,null);
        Assert.Equal("Owned: 2",Snapshot().OwnedText(1001));Assert.Equal("",Snapshot().OwnedText(999));
        Set(ship,"Inventory.Equipped.1.ItemTemplateID",1001);Set(ship,"Inventory.Equipped.1.StackCount",1);
        Set(ship,"Inventory.Ammo.1.ItemTemplateID",1001);Set(ship,"Inventory.Ammo.1.StackCount",5);
        Set(player,"SecureInventory.2.ItemTemplateID",1001);Set(player,"SecureInventory.2.StackCount",3);
        Assert.Equal("Owned: 11 in 4 slots",Snapshot().OwnedText(1001));
    }
    [Fact]
    public void ConversationProjectionResetsAcrossHandoff()
    {
        var s=new Net7GameplayState();s.Apply(Packet.ForOpcode(0x56,BitConverter.GetBytes(3u)));
        ulong revision=s.TalkTreeRevision;Assert.True(s.TalkTreeTradeAvailable);
        s.ResetForServerHandoff();Assert.False(s.TalkTreeTradeAvailable);Assert.Null(s.TalkTreeAction);Assert.True(s.TalkTreeRevision>revision);
    }
}
