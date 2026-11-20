using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7WarpRouteStateTests
{
    [Fact]
    public void SelectionAndCaptureRetainOrderingAcrossCoalescedSnapshots()
    {
        var state=new Net7GameplayState();
        state.SetLocalSelectedTarget(63);
        ulong selected=state.TargetSelectionSequence;
        state.CaptureWarpRoute([91,72,63]);
        ulong captured=state.WarpRoute!.CaptureSequence;
        Assert.True(captured>selected);
        ApplyIndex(state,1);
        Assert.Equal(captured,state.WarpRoute!.CaptureSequence);
        state.SetLocalSelectedTarget(72);
        Assert.True(state.TargetSelectionSequence>captured);
        selected=state.TargetSelectionSequence;
        state.SetLocalSelectedTarget(72);
        Assert.Equal(selected,state.TargetSelectionSequence);
        state.CaptureWarpRoute([]);
        Assert.True(state.WarpRoute!.CaptureSequence>selected);
        captured=state.WarpRoute.CaptureSequence;
        state.CaptureWarpRoute([]);
        Assert.True(state.WarpRoute!.CaptureSequence>captured);
    }

    [Fact]
    public void CaptureOwnsRouteAndDoesNotInventServerProgress()
    {
        var state = new Net7GameplayState();
        uint[] route = [91, 72, 63];
        state.CaptureWarpRoute(route);
        route[0] = 999;
        Assert.Equal(new uint[] { 91, 72, 63 }, state.WarpRoute!.ObjectIds);
        Assert.Null(state.WarpRoute.ReportedIndex);
        Assert.Null(state.WarpRoute.ReportedObjectId);
        Assert.Null(state.WarpIndex);
    }

    [Fact]
    public void ReceiptsUseZeroBasedIndicesAndAllowRepeatedOrEarlierIndices()
    {
        var state = new Net7GameplayState();
        state.CaptureWarpRoute(new uint[] { 91, 72, 63 });
        var original = state.WarpRoute;
        foreach (uint index in new uint[] { 0, 2, 2, 1 })
        {
            ApplyIndex(state, index);
            Assert.Equal(index, state.WarpRoute!.ReportedIndex);
            Assert.Equal(new uint[] { 91, 72, 63 }[index], state.WarpRoute.ReportedObjectId);
        }
        Assert.Null(original!.ReportedIndex); // Published snapshots stay detached.
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void AnyOutOfRangeIndexClearsRouteWithoutRejectingReceipt(uint index)
    {
        var state = new Net7GameplayState();
        state.CaptureWarpRoute(new uint[] { 91, 72, 63 });
        ApplyIndex(state, index);
        Assert.Equal(index, state.WarpIndex);
        Assert.Null(state.WarpRoute);
        ApplyIndex(state, 0);
        Assert.Null(state.WarpRoute); // Later packets cannot resurrect a cleared vector.
    }

    [Fact]
    public void EmptyOrAbsentRouteAcceptsReceiptAndReplacementDoesNotReuseOldIndex()
    {
        var state = new Net7GameplayState();
        ApplyIndex(state, 7);
        Assert.Null(state.WarpRoute);
        state.CaptureWarpRoute(Array.Empty<uint>());
        ApplyIndex(state, 0);
        Assert.Null(state.WarpRoute);
        state.CaptureWarpRoute(new uint[] { 42 });
        Assert.Null(state.WarpRoute!.ReportedIndex);
        Assert.Equal(0u, state.WarpIndex); // Raw last receipt is a separate diagnostic.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerBoundariesDiscardRouteAndProgress(bool characterSelection)
    {
        var state = new Net7GameplayState();
        state.CaptureWarpRoute(new uint[] { 42 });
        ApplyIndex(state, 0);
        if (characterSelection) state.ResetForCharacterSelection();
        else state.ResetForServerHandoff();
        Assert.Null(state.WarpRoute);
        Assert.Null(state.WarpIndex);
    }

    [Fact]
    public void PartialReceiptAppliesInitializedIndexAndClearsInvalidRoute()
    {
        var state = new Net7GameplayState();
        state.CaptureWarpRoute(new uint[] { 42 });
        ApplyIndex(state, 0);
        Assert.True(state.Apply(Packet.ForOpcode(0x009C, new byte[3])));
        Assert.Null(state.WarpRoute);
        Assert.Equal(0xFF000000u, state.WarpIndex);
        state.CaptureWarpRoute(new uint[] { 43 });
        ApplyIndex(state, 0);
        Assert.Equal(0u, state.WarpRoute!.ReportedIndex);
    }

    private static void ApplyIndex(Net7GameplayState state, uint index)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, index);
        Assert.True(state.Apply(Packet.ForOpcode(0x009C, payload)));
    }
}
