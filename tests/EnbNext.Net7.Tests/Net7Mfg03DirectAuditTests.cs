using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg03DirectAuditTests
{
    [Fact]
    public void ManufactureItemRequestIsExactlySelectedFormulaItemIdInTwoNetworkOrderDwords()
    {
        const uint player = 0x11223344;
        const uint itemId = 0x55667788;
        byte[] payload = Net7GameplayWire.BuildManufactureItem(player, itemId);
        Assert.Equal(Convert.FromHexString("1122334455667788"), payload);

        Net7ManufactureItemRequest decoded =
            Net7GameplayWire.DecodeManufactureItemRequest(payload);
        Assert.Equal(player, decoded.PlayerId);
        Assert.Equal(itemId, decoded.ItemId);

        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufactureItemRequest(new byte[7]));
        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufactureItemRequest(new byte[9]));
    }

    [Fact]
    public void KnownAndPreviousAttemptSelectionsShareTheSameExactItemIdContractAndSentinelIsSuppressed()
    {
        new Net7ManufacturingDomainTests()
            .ManufactureAndRefinerySentinelsAreSuppressedBeforeWireConstruction();
        new Net7ManufacturingDomainTests()
            .SelectionPendingClearsWhenManufactureComponentsBecomeUsableNotOnUnrelatedAuxData();
    }

    [Fact]
    public async Task NumericInboundManufactureItemRequestCannotMasqueradeAsAcknowledgement()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x007B)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x007B, payload), "decoded-inner", false, CancellationToken.None])!;
            await task;
        }

        byte[] request = Net7GameplayWire.BuildManufactureItem(0x11223344, 0x55667788);
        await Dispatch(request);
        await Dispatch(request[..^1]);
        await Dispatch([.. request, (byte)0]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x007B, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x007B, request), 0).PacketMaterialized);
    }

    [Fact]
    public void Mfg03OwnsOnlyTheManufactureItemOpcodeWithinGameplayProvenance()
    {
        Assert.Equal(new[] { "MFG-03" }, Net7GameplayProvenance.GetOwners(0x007B));
    }

}
