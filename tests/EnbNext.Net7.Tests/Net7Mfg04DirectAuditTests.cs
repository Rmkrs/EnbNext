using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg04DirectAuditTests
{
    [Fact]
    public void RefineryItemRequestIsExactlySelectedKnownFormulaItemIdInTwoLittleEndianDwords()
    {
        const uint player = 0x11223344;
        const uint itemId = 0x55667788;

        byte[] refinery = Net7GameplayWire.BuildRefineryItem(player, itemId);
        Assert.Equal(Convert.FromHexString("4433221188776655"), refinery);

        Net7RefineryItemRequest decoded =
            Net7GameplayWire.DecodeRefineryItemRequest(refinery);
        Assert.Equal(player, decoded.PlayerId);
        Assert.Equal(itemId, decoded.ItemId);

        byte[] manufacture = Net7GameplayWire.BuildManufactureItem(player, itemId);
        Assert.Equal(Convert.FromHexString("1122334455667788"), manufacture);
        Assert.NotEqual(manufacture, refinery);

        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeRefineryItemRequest(new byte[7]));
        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeRefineryItemRequest(new byte[9]));
    }

    [Fact]
    public void RefinerySelectionUsesKnownFormulaOnlySuppressesMinusOneAndWaitsForComponents()
    {
        new Net7ManufacturingDomainTests()
            .ManufactureAndRefinerySentinelsAreSuppressedBeforeWireConstruction();
        new Net7ManufacturingDomainTests()
            .SelectionPendingClearsWhenManufactureComponentsBecomeUsableNotOnUnrelatedAuxData();
        new Net7ManufacturingDomainTests()
            .RefineryOnceAndStackStayDistinctAndRefreshRememberedRefinerySelectionOnAttemptResult();
    }

    [Fact]
    public async Task NumericInboundRefineryItemRequestCannotMasqueradeAsAcknowledgement()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x007C)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x007C, payload), "decoded-inner", false, CancellationToken.None])!;
            await task;
        }

        byte[] request = Net7GameplayWire.BuildRefineryItem(0x11223344, 0x55667788);
        await Dispatch(request);
        await Dispatch(request[..^1]);
        await Dispatch([.. request, (byte)0]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x007C, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x007C, request), 0).PacketMaterialized);
    }

    [Fact]
    public void Mfg04OwnsOnlyTheRefineryItemOpcodeWithinGameplayProvenance()
    {
        Assert.Equal(new[] { "MFG-04" }, Net7GameplayProvenance.GetOwners(0x007C));
    }
}
