using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Transport;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Inv01DirectAuditTests
{
    [Fact]
    public void InventoryRequestCodecsPinExactDirectionSpecificLayouts()
    {
        byte[] transferBytes = Net7GameplayWire.BuildInventoryTransfer(
            0x11223344, 0x01020304, 0xA1A2A3A4, 0x05060708, 0x0A0B0C0D, uint.MaxValue);
        Net7InventoryTransferRequest transfer = Net7GameplayWire.DecodeInventoryTransferRequest(transferBytes);
        Assert.Equal(0x11223344u, transfer.Id);
        Assert.Equal(0x01020304u, transfer.FromInventory);
        Assert.Equal(0xA1A2A3A4u, transfer.FromSlot);
        Assert.Equal(0x05060708u, transfer.ToInventory);
        Assert.Equal(0x0A0B0C0Du, transfer.ToSlot);
        Assert.Equal(uint.MaxValue, transfer.Count);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeInventoryTransferRequest(transferBytes[..^1]));
        byte[] transferWithTrailingByte = [.. transferBytes, (byte)0];
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeInventoryTransferRequest(transferWithTrailingByte));

        byte[] itemStateBytes = Net7GameplayWire.BuildItemStateRequest(0x11223344, 0x2000, true, 2, 7);
        Net7ItemStateRequest itemState = Net7GameplayWire.DecodeItemStateRequest(itemStateBytes);
        Assert.Equal(0x11223344u, itemState.Id);
        Assert.Equal(0x2000u, itemState.State);
        Assert.Equal((byte)1, itemState.BoolValue);
        Assert.Equal((byte)2, itemState.Inventory);
        Assert.Equal((byte)7, itemState.Slot);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeItemStateRequest(itemStateBytes[..^1]));
        byte[] itemStateWithTrailingByte = [.. itemStateBytes, (byte)0];
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeItemStateRequest(itemStateWithTrailingByte));
    }

    [Fact]
    public async Task NumericInboundInventoryRequestsAreNotMaterializedOrSurfacedAsCompletions()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode is 0x0027 or 0x0029)
                delivered++;
        };

        FieldInfo transportField = typeof(Net7Session).GetField(
            "_transportState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var transport = Assert.IsType<Net7TransportState>(transportField.GetValue(session));
        transport.CommitValidatedFixedSessionId(0xAABBCCDD);
        transport.CommitGameplayPort(3501);
        transport.SetGameplayForwardingActive(true);
        typeof(Net7Session).GetField("_gameplayClientTargetActive", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(session, true);

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        async Task Dispatch(ushort opcode, byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(opcode, payload), "direct outer UDP", CancellationToken.None])!;
            await task;
        }

        byte[] transfer = Net7GameplayWire.BuildInventoryTransfer(1, 2, 3, 4, 5, 6);
        byte[] itemState = Net7GameplayWire.BuildItemStateRequest(7, 0x2000, true, 2, 8);
        await Dispatch(0x0027, transfer);
        await Dispatch(0x0029, itemState);
        await Dispatch(0x0027, transfer[..^1]);
        byte[] malformedItemState = [.. itemState, (byte)0];
        await Dispatch(0x0029, malformedItemState);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0027, transfer)));
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0029, itemState)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0027, transfer), 0).PacketMaterialized);
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0029, itemState), 0).PacketMaterialized);
    }

    [Fact]
    public async Task InventoryTransferRequiresExplicitRetailLocalSelectedTargetIdentity()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });

        // The exact ID is usable only when the owning UI/action state supplies
        // the separate retail-local selected-target-cache value explicitly.
        // There is intentionally no implicit overload that can borrow
        // authoritative ShipAuxData.TargetGameID / World.CurrentTargetId.
        bool sent = await session.TransferInventoryAsync(
            new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip),
            1, 0, 3, uint.MaxValue, 1);
        Assert.False(sent); // no route in this unit fixture, after successful construction
    }
}
