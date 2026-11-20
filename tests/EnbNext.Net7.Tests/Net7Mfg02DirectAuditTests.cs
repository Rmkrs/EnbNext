using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg02DirectAuditTests
{
    [Fact]
    public void CategoryRequestIsExactlySelectedLeafIdInTwoNetworkOrderDwords()
    {
        const uint player = 0x11223344;
        const uint leafId = 0x55667788;
        byte[] payload = Net7GameplayWire.BuildManufacturingCategory(player, leafId);
        Assert.Equal(Convert.FromHexString("1122334455667788"), payload);

        Net7ManufacturingCategoryRequest decoded =
            Net7GameplayWire.DecodeManufacturingCategoryRequest(payload);
        Assert.Equal(player, decoded.PlayerId);
        Assert.Equal(leafId, decoded.SelectedSubcategoryId);

        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufacturingCategoryRequest(new byte[7]));
        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufacturingCategoryRequest(new byte[9]));
    }

    [Fact]
    public void BrowserProducerIsLevelOrderedAndDirectArbitraryLeafSenderDoesNotExist()
    {
        new Net7ManufacturingDomainTests()
            .CategoryBrowserIsLocalUntilVisibleAuthoritativeLeafIsSelected();

        MethodInfo? arbitraryLeafSender = typeof(Net7Session).GetMethod(
            "SelectManufacturingCategoryAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.Null(arbitraryLeafSender);
        Assert.NotNull(typeof(Net7Session).GetMethod(
            nameof(Net7Session.SelectManufacturingSubcategoryAsync),
            BindingFlags.Instance | BindingFlags.Public));
    }

    [Fact]
    public async Task NumericInboundCategoryRequestCannotMasqueradeAsAcknowledgement()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x007A)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x007A, payload), "decoded-inner", false, CancellationToken.None])!;
            await task;
        }

        byte[] request = Net7GameplayWire.BuildManufacturingCategory(0x11223344, 0x55667788);
        await Dispatch(request);
        await Dispatch(request[..^1]);
        await Dispatch([.. request, (byte)0]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x007A, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x007A, request), 0).PacketMaterialized);
    }

    [Fact]
    public void Mfg02OwnsOnlyTheCategoryOpcodeWithinGameplayProvenance()
    {
        Assert.Equal(new[] { "MFG-02" }, Net7GameplayProvenance.GetOwners(0x007A));
    }
}
