using EnbNext.Net7.Gameplay;
namespace EnbNext.Net7.Tests;

public sealed class Net7TradeNoTargetTests
{
    [Fact]
    public async Task TradeOfferReorderAndRetractionUseZeroWhenSelectionIsEmpty()
    {
        await using var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        EnbR11FlightTests.FlightWorld(session.World);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);using var lifetime=receiver;
        var frames=new List<byte[]>();channel.FrameSent+=(frame,_)=>{Assert.Equal(0x0027,frame.Opcode);frames.Add(frame.Payload.ToArray());};
        Assert.Null(session.Gameplay.LocalSelectedTargetId);
        Assert.True(await session.OfferTradeItemAsync(1,2,0,3));
        Assert.True(await session.ReorderTradeItemAsync(0,1,2));
        Assert.True(await session.RetractTradeItemAsync(1,2));
        Assert.Equal(Net7GameplayWire.BuildInventoryTransfer(0,1,2,16,0,3),frames[0]);
        Assert.Equal(Net7GameplayWire.BuildInventoryTransfer(0,16,0,16,1,2),frames[1]);
        Assert.Equal(Net7GameplayWire.BuildInventoryTransfer(0,16,1,1,uint.MaxValue,2),frames[2]);
    }
}
