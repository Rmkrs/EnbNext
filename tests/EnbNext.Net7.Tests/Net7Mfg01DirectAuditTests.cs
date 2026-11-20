using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mfg01DirectAuditTests
{
    [Fact]
    public void ModeRequestIsExactlyTwoNetworkOrderDwordsAndSenderSubsetStaysFinite()
    {
        const uint player = 0x11223344;

        Assert.Equal(Convert.FromHexString("1122334400000001"),
            Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Manufacture));
        Assert.Equal(Convert.FromHexString("1122334400000002"),
            Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Analyze));
        Assert.Equal(Convert.FromHexString("1122334400000004"),
            Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.Refining));
        Assert.Equal(Convert.FromHexString("1122334400000000"),
            Net7GameplayWire.BuildManufacturingMode(player, Net7ManufacturingMode.None));

        Net7ManufacturingModeRequest decoded = Net7GameplayWire.DecodeManufacturingModeRequest(
            Convert.FromHexString("1122334400000004"));
        Assert.Equal(player, decoded.PlayerId);
        Assert.Equal(4u, decoded.Mode);

        // Structural decode and current sender reachability are deliberately
        // separate. Retail can instantiate mode 3/5 packet objects even though
        // no current 0x0079 value-constructor producer emits those modes.
        Assert.Equal(3u, Net7GameplayWire.DecodeManufacturingModeRequest(
            Convert.FromHexString("1122334400000003")).Mode);
        Assert.Equal(5u, Net7GameplayWire.DecodeManufacturingModeRequest(
            Convert.FromHexString("1122334400000005")).Mode);

        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufacturingModeRequest(new byte[7]));
        Assert.Throws<InvalidDataException>(() =>
            Net7GameplayWire.DecodeManufacturingModeRequest(new byte[9]));
    }

    [Fact]
    public async Task UnsupportedEmittedModesFailBeforeTransportAndActiveLabDoesNotOwnModeIntent()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.SetManufacturingModeAsync((Net7ManufacturingMode)3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.SetManufacturingModeAsync((Net7ManufacturingMode)5));

        var domain = new Net7ManufacturingDomain();
        domain.SetActiveObject(0);
        domain.RecordModeSent(Net7ManufacturingMode.Manufacture);
        Assert.True(domain.IsTerminalOpen);
        Assert.Equal(Net7ManufacturingMode.Manufacture, domain.RequestedMode);

        domain.SetActiveObject(0xDEADBEEF);
        Assert.Null(domain.CurrentLab);
        domain.RecordModeSent(Net7ManufacturingMode.Refining);
        Assert.True(domain.IsTerminalOpen);
        Assert.Equal(Net7ManufacturingMode.Refining, domain.RequestedMode);
    }

    [Fact]
    public async Task SwitchingManufacturingFamilyTerminalClosesTheActiveModeBeforeOpeningAnother()
    {
        var trace = new List<string>();
        await using var session = new Net7Session(
            new Net7ServerOptions { Host = "127.0.0.1" }, trace.Add);

        PropertyInfo playerId = typeof(Net7Session).GetProperty(nameof(Net7Session.PlayerId))!;
        playerId.SetValue(session, 0x11223344u);
        session.Gameplay.ManufacturingDomain.RecordModeSent(Net7ManufacturingMode.Manufacture);

        // With no active route, the first attempted send exposes the lifecycle
        // ordering without putting anything on the wire. A direct mode switch
        // must attempt the proven close reset first, not jump straight to 0x0079 Analyze.
        Assert.False(await session.SetManufacturingModeAsync(Net7ManufacturingMode.Analyze));
        Assert.Contains(trace, line => line.Contains("gameplay 0x007E suppressed", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, line => line.Contains("gameplay 0x0079 suppressed", StringComparison.Ordinal));
        Assert.Equal(Net7ManufacturingMode.Manufacture, session.Gameplay.ManufacturingDomain.RequestedMode);
    }

    [Fact]
    public async Task NumericInboundModeRequestCannotMasqueradeAsAcknowledgement()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0079)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x0079, payload), "decoded-inner", false, CancellationToken.None])!;
            await task;
        }

        byte[] request = Net7GameplayWire.BuildManufacturingMode(
            0x11223344, Net7ManufacturingMode.Manufacture);
        await Dispatch(request);
        await Dispatch(request[..^1]);
        await Dispatch([.. request, (byte)0]);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0079, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0079, request), 0).PacketMaterialized);
        Assert.Equal(Net7ManufacturingMode.None, session.Gameplay.ManufacturingDomain.RequestedMode);
    }

    [Fact]
    public void Mfg01OwnsOnlyTheModeOpcodeWithinGameplayProvenance()
    {
        Assert.Equal(new[] { "MFG-01" }, Net7GameplayProvenance.GetOwners(0x0079));
    }
}
