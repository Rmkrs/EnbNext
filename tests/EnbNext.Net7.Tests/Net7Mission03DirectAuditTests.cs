using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Transport;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Mission03DirectAuditTests
{
    [Fact]
    public void MissionDismissPayloadIsExactlyTwoNetworkOrderDwords()
    {
        byte[] payload = Net7GameplayWire.BuildMissionRequest(0x12345678, 0x0A0B0C0D);
        Assert.Equal(Convert.FromHexString("123456780A0B0C0D"), payload);

        Net7MissionDismissRequest decoded = Net7GameplayWire.DecodeMissionDismissRequest(payload);
        Assert.Equal(0x12345678u, decoded.PlayerId);
        Assert.Equal(0x0A0B0C0Du, decoded.InternalMissionId);
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeMissionDismissRequest(payload[..^1]));
        byte[] trailing = [.. payload, (byte)0];
        Assert.Throws<InvalidDataException>(() => Net7GameplayWire.DecodeMissionDismissRequest(trailing));

        byte[] server = new ServerUdpFrame(0x0087, 0xAABBCCDD, 12, payload).ToWireBytes();
        Assert.Equal(20, server.Length);
        Assert.Equal(payload, server.AsSpan(12).ToArray());
    }

    [Fact]
    public void MissionClearRequiresVisibleTerminalMissionAndAuthoritativeMissionId()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.ID", I32(22)),
            ("LocalPlayerAuxData.Missions.0.IsComplete", U8(1)));
        Assert.Throws<InvalidOperationException>(() => domain.RequireClearMissionId(0, 0));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.Name", Lp("Dismiss")));
        Assert.Equal(22u, domain.RequireClearMissionId(0, 0));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.IsComplete", U8(0)),
            ("LocalPlayerAuxData.Missions.0.IsFailed", U8(1)));
        Assert.Equal(22u, domain.RequireClearMissionId(0, 0));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.IsFailed", U8(0)),
            ("LocalPlayerAuxData.Missions.0.IsExpired", U8(1)));
        Assert.Equal(22u, domain.RequireClearMissionId(0, 0));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.IsExpired", U8(0)),
            ("LocalPlayerAuxData.Missions.0.IsForfeitable", U8(1)));
        Assert.Throws<InvalidOperationException>(() => domain.RequireClearMissionId(0, 0));
        Assert.Equal(22u, domain.RequireForfeitMissionId(0, true, 0));

        // 005C66D0 sends the stored ID; 00753DF0's default is -1.
        var unsetWorld = new Net7WorldState();
        var unset = new Net7PlayerSemanticDomain(unsetWorld);
        Apply(unsetWorld.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.1.Name", Lp("No id")),
            ("LocalPlayerAuxData.Missions.1.IsForfeitable", U8(1)));
        Assert.Equal(uint.MaxValue, unset.RequireForfeitMissionId(1, true, 0));
    }

    [Fact]
    public async Task NumericInboundMissionDismissRequestIsNotSurfacedAsCompletion()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0087)
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

        async Task Dispatch(byte[] payload)
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x0087, payload), "decoded-inner", CancellationToken.None])!;
            await task;
        }

        byte[] request = Net7GameplayWire.BuildMissionRequest(0x12345678, 0x0A0B0C0D);
        await Dispatch(request);
        await Dispatch(request[..^1]);
        byte[] trailing = [.. request, (byte)0];
        await Dispatch(trailing);

        Assert.Equal(0, delivered);
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x0087, request)));
        Assert.False(session.World.Apply(Packet.ForOpcode(0x0087, request), 0).PacketMaterialized);
    }

    private static void Apply(Net7AuxDataSchema schema, params (string Path, byte[] Wire)[] values)
    {
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Assert.Single(schema.Properties, p => p.Path == path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, 0);
        WriteU32(delta, 0);
        new Net7AuxDataCodec().Apply(schema, delta.ToArray());
    }

    private static byte[] U8(byte value) => [value];

    private static byte[] I32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Lp(string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        byte[] result = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)text.Length));
        text.CopyTo(result, 2);
        return result;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
