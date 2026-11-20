using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Full01DirectAuditTests
{
    [Fact]
    public async Task UnsupportedAndFactoryUnmappedOrdinaryInboundFailClosedBeforeDelivery()
    {
        List<string> trace = [];
        await using var session = new Net7Session(
            new Net7ServerOptions { Host = "127.0.0.1" },
            trace.Add);

        int delivered = 0;
        session.GameplayPacketReceived += _ => delivered++;

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.DispatchGameplayPacketAsync was not found.");

        await Dispatch(dispatch, session, Packet.ForOpcode(0x0063, ReadOnlyMemory<byte>.Empty));
        await Dispatch(dispatch, session, Packet.ForOpcode(0x0039, ReadOnlyMemory<byte>.Empty));

        Assert.Equal(0, delivered);
        Assert.Empty(session.Gameplay.Effects);
        Assert.Empty(session.Gameplay.Messages);
        Assert.Empty(session.World.Objects.Snapshot());
        Assert.Contains(trace, line => line.Contains("FULL-01 unsupported ordinary inbound 0x0063", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("FULL-01 unsupported ordinary inbound 0x0039", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SupportedDefaultInboundStillMaterializesAndDelivers()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x000A)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DispatchGameplayPacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Net7Session.DispatchGameplayPacketAsync was not found.");

        byte[] payload = new byte[Net7EffectsRenderWire.PointEffectPayloadSize];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), 0x11223344);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8, 4), -12.5f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(12, 4), 2048f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(16, 4), .125f);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), 7);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(24, 4), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(28, 4), 90f);

        await Dispatch(dispatch, session, Packet.ForOpcode(0x000A, payload));

        Assert.Equal(1, delivered);
        Assert.True(session.Gameplay.Effects.ContainsKey(0x11223344));
        var effect = session.Gameplay.Effects[0x11223344];
        Assert.Equal(Net7EffectGeometryKind.Point, effect.GeometryKind);
        Assert.Equal(new Vector3(-12.5f, 2048f, .125f), effect.WorldPoint);
        Assert.Equal(2f, effect.Scale);
        Assert.Equal(new Vector3(90f, 0, 0), effect.HsvShift);
        Assert.Equal(0u, effect.SourceId);
        Assert.Equal(0u, effect.TargetId);
    }

    private static async Task Dispatch(MethodInfo dispatch, Net7Session session, Packet packet)
    {
        Task task = (Task)(dispatch.Invoke(
            session,
            [packet, "decoded-inner", CancellationToken.None])
            ?? throw new InvalidOperationException("Gameplay dispatch did not return a Task."));
        await task;
    }
}
