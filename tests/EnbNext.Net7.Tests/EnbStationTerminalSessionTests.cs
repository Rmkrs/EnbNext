using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using EnbNext.Client;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed partial class EnbStationConversationTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(null)]
    public async Task JobsPublishWithoutAnUnrelatedWorldPacket(uint? status)
    {
        string directory=Path.Combine(Path.GetTempPath(),"enb-jobs-"+Guid.NewGuid().ToString("N"));
        await using var client=new EnbClient(new(){WireCaptureDirectory=directory});
        var trace=typeof(EnbClient).GetMethod("Trace",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var s=Attach(client,message=>trace.Invoke(client,[message])); var owner=TerminalOwner(s,3);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s); using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        async Task Receive(ushort op,byte[] body)=>await (Task)dispatch.Invoke(s,[Packet.ForOpcode(op,body),"offline jobs",CancellationToken.None])!;
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        await Receive(0x93,Net7UiPresentationWireTests.Build(w=>{w.U32(1);w.U32(42);w.U32(0);w.U32(0);w.U32(50);w.CString("Combat job");w.CString("Sponsor");w.CString("2000 XP");}));
        Assert.Equal(42u,Assert.Single(client.LatestPresentationSnapshot!.StationJobs).Field0);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobDescription,42)));
        Assert.True(client.LatestPresentationSnapshot.StationJobRequestPending);
        await Receive(0x94,Net7UiPresentationWireTests.Build(w=>{w.U32(42);w.U8(1);w.CString("Combat job");w.CString("Details");}));
        Assert.Equal("Details",client.LatestPresentationSnapshot!.StationJobDescription!.Description);
        Assert.False(client.LatestPresentationSnapshot.StationJobRequestPending);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.JobAccept,42)));
        Assert.Equal(Net7GameplayWire.BuildStarbaseRequest(7,42,8),frames.Last().Body);
        Assert.True(client.LatestPresentationSnapshot.StationJobRequestPending);
        await Receive(0x96,Net7UiPresentationWireTests.Build(w=>{w.U32(42);if(status is {} code){w.U32(code);w.U32(30);}}));
        Assert.False(client.LatestPresentationSnapshot!.StationJobRequestPending);
        Assert.Equal(status<2?0:1,client.LatestPresentationSnapshot.StationJobs.Length);
        Assert.Equal(status,client.LatestPresentationSnapshot.StationJobReply!.AcceptStatus);
        // A following server update must still be processed after the short reply.
        await Receive(0x95,BitConverter.GetBytes(42u));
        Assert.Empty(client.LatestPresentationSnapshot.StationJobs);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        string log=File.ReadAllText(Assert.Single(Directory.GetFiles(directory)));
        Assert.Contains("Command JobAccept completed=True",log);
        Assert.Contains("payload=070000002A00000008 socket-send-completed",log);
        Assert.Contains($"RX opcode=0096 bytes={(status is null?4:12)}",log);
        Assert.Contains("applied opcode=0096 pending=False",log);
        File.Delete(Assert.Single(Directory.GetFiles(directory)));Directory.Delete(directory);
    }
    [Theory]
    [InlineData(0,4,3,0x7c)]
    [InlineData(1,3,2,0x7e)]
    [InlineData(1,2,2,0x7e)]
    [InlineData(2,1,2,0x7b)]
    public async Task TerminalSessionConsumesLabDeltasAndCompletesAnAttempt(uint facility,int labMode,uint action,ushort resultOpcode)
    {
        await using var client=new EnbClient(new());var s=Attach(client);var owner=TerminalOwner(s,facility);
        var publish=typeof(EnbClient).GetMethod("OnNet7PresentationStateChanged",BindingFlags.Instance|BindingFlags.NonPublic)!;
        s.PresentationStateChanged+=time=>publish.Invoke(client,[time]);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(s);using var lifetime=receiver;
        var frames=new List<(ushort Op,byte[] Body)>();channel.FrameSent+=(f,_)=>frames.Add((f.Opcode,f.Payload.ToArray()));
        var dispatch=typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
        async Task Receive(ushort op,byte[] body)=>await (Task)dispatch.Invoke(s,[Packet.ForOpcode(op,body),"offline terminal session",CancellationToken.None])!;
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Action,action)));
        const uint labId=900;
        s.World.MaterializeCreate(new Net7CreateUpdate(labId,1f,0,0x27,0,0,0));
        byte[] id=new byte[4];BinaryPrimitives.WriteUInt32BigEndian(id,labId);await Receive(0x7f,id);
        var schema=s.World.Objects.FindFirst(labId)!.AuxData!;
        async Task Delta(params (string Path,object Value)[] values)=>await Receive(0x1b,LabDelta(labId,schema,values));
        await Delta(("Mode",labMode),("KnownFormula.0.ItemID",500),("KnownFormula.0.ItemName","Test recipe"),
            ("PrimaryCategories.0.Name","Items"),("PrimaryCategories.0.Categories.0.Name","Weapons"),
            ("PrimaryCategories.0.Categories.0.Subcategories.0.Name","Beam"),("PrimaryCategories.0.Categories.0.Subcategories.0.ID",77),
            ("PrimaryCategories.0.Categories.0.Subcategories.0.Visible",(byte)1),
            ("PrimaryCategories.0.Categories.0.Subcategories.1.ID",78),("PrimaryCategories.0.Categories.0.Subcategories.1.Visible",(byte)1));
        if(facility==2)
        {
            await Delta(("TechLevelFilterBitfield",0));
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Primary,0)));
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Category,0)));
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Subcategory,0)));
            Assert.Equal(Net7GameplayWire.BuildManufacturingCategory(7,77),frames.Last(f=>f.Op==0x7a).Body);
            Assert.Equal(0x80,frames.Last().Op);
            Assert.Equal(Net7GameplayWire.BuildManufacturingFilter(7,true,511),frames.Last().Body);
            Assert.False(s.Gameplay.ManufacturingDomain.InitialCategoryFilterPending);
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.FilterOff,1)));
            int filterCount=frames.Count(f=>f.Op==0x80);
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Browse,2)));
            // 005B1C20 and the first Recent click end in 005B4850: ManufactureAction 0,
            // with the local action state left alone.
            Assert.Equal(Net7GameplayWire.BuildManufacturingAction(7,0),frames.Last().Body);
            int actions=frames.Count(f=>f.Op==0x7e);
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Recent)));
            Assert.Equal(actions+1,frames.Count(f=>f.Op==0x7e));
            Assert.Equal(Net7ManufacturingActionContext.Idle,s.Gameplay.ManufacturingDomain.ActionContext);
            Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Subcategory,1)));
            Assert.Equal(filterCount,frames.Count(f=>f.Op==0x80));
            Assert.Equal(510u,s.Gameplay.ManufacturingDomain.LocalTechLevelFilterBitfield);
        }
        if(facility!=1)Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Formula,500)));
        await Delta(("Validity",13),("NegotiatedCost",100ul),("ManufactureTarget.0.ItemTemplateID",500),("ManufactureTarget.0.StackCount",1),
            ("ManufactureComponents.0.ItemTemplateID",600),("AdditionalIterations",1)); // the selection completes (+BA4/+AF0 = 0)
        // The world packet publishes before its manufacturing reconcile; read the domain directly.
        Assert.True(EnbStationTerminalSnapshot.Build(s.Gameplay.ManufacturingDomain,null).CanAct);
        var ship=s.World.Objects.FindFirst(7)!.AuxData!;
        Set(ship,"Inventory.CargoSpace",16);
        Set(ship,"Inventory.Cargo.2.ItemTemplateID",123);Set(ship,"Inventory.Cargo.2.StackCount",1);
        ulong identity=client.LatestPresentationSnapshot!.Objects.Single(o=>o.IsLocalShip).RenderIdentity;
        var staleDrop=new EnbInventoryMoveRequest(new(7,identity,12,0,500),1,1,2,null,1);
        int beforeDrop=frames.Count;
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Move){Move=staleDrop}));
        Assert.Equal(beforeDrop,frames.Count);
        Set(ship,"Inventory.Cargo.2.ItemTemplateID",-2);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Move){Move=staleDrop}));
        Assert.Equal(beforeDrop,frames.Count);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Action,action)));
        Assert.Equal(Net7GameplayWire.BuildManufacturingAction(7,action),frames.Last(f=>f.Op==0x7e).Body);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Action,action)));
        await Delta(("Validity",facility==1?15:16));
        typeof(EnbClient).GetField("_terminalTick",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,Environment.TickCount64-3000);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Tick)));
        Assert.Equal(resultOpcode,frames.Last().Op);
        var result=Assert.IsType<Net7ManufacturingAttemptResult>(client.LatestPresentationSnapshot!.StationTerminal!.Result);
        Assert.Equal(labMode,result.Mode);
        Assert.Equal((Net7ManufacturingValidity)(facility==1?15:16),result.Validity);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Tick)));
        Assert.Equal(result,client.LatestPresentationSnapshot!.StationTerminal!.Result);
        if(facility==1)Assert.Equal(Net7GameplayWire.BuildManufacturingAction(7,1),frames.Last().Body);
        else Assert.Equal(facility==0?Net7GameplayWire.BuildRefineryItem(7,500):Net7GameplayWire.BuildManufactureItem(7,500),frames.Last().Body);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
        Assert.Null(client.LatestPresentationSnapshot!.StationTerminal!.Result);
        Assert.False(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Tick)));
        int closedCount=frames.Count;
        await Delta(("Validity",16));
        Assert.Equal(closedCount,frames.Count);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Open)));
        Assert.Equal(0,s.Gameplay.ManufacturingDomain.BrowserDepth);
        int reopenedCount=frames.Count;
        typeof(EnbClient).GetField("_terminalTick",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(client,Environment.TickCount64-3000);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Tick)));
        Assert.Equal(reopenedCount,frames.Count);
        Assert.True(await client.StationTerminalAsync(new(owner,EnbTerminalCommand.Close)));
    }

    private static byte[] LabDelta(uint id,Net7AuxDataSchema schema,(string Path,object Value)[] values)
    {
        using var delta=new MemoryStream();using var writer=new BinaryWriter(delta,Encoding.Latin1,true);
        writer.Write((byte)0);writer.Write((uint)values.Length);
        foreach(var (path,value) in values)
        {
            writer.Write((uint)schema.Properties.Single(p=>p.Path==schema.Name+"."+path).Id);
            switch(value)
            {
                case int number:writer.Write(number);break;
                case ulong number:writer.Write(number);break;
                case byte number:writer.Write(number);break;
                case string text:var bytes=Encoding.Latin1.GetBytes(text);writer.Write((ushort)bytes.Length);writer.Write(bytes);break;
                default:throw new InvalidOperationException();
            }
        }
        writer.Write(0u);writer.Write(0u);
        byte[] result=new byte[6+delta.Length];BinaryPrimitives.WriteUInt32LittleEndian(result,id);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(4),(ushort)delta.Length);delta.ToArray().CopyTo(result,6);return result;
    }
}
