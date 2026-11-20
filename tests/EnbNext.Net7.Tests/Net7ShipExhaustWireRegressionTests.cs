using System.Buffers.Binary;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class Net7ShipExhaustWireRegressionTests
{
    // Passive capture 20260906T142621740Z, frames 76/99,
    // reliable sequences 45/46. Inner size 23, declared delta 19, actual 13.
    // Preserve the server's bytes, not a fixture produced by our encoder.
    private const string On = "17001B000A420040130001020000000000040001000000";
    private const string Off = "17001B000A420040130001020000000000040000000000";
    private const uint Ship = 0x4000420A;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedOnOffPacketsReachLocalAndRemoteExhaustGate(bool local)
    {
        var world = CreateWorld(local);
        var builder = new EnbPresentationSnapshotBuilder();
        var initial = builder.Build(world, null, 0).Objects.Single();
        Assert.False(EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(initial).ShouldDraw);
        foreach (string hex in new[] { On, Off, On, On, Off })
        {
            world.Apply(PacketFor(hex), 10);
            var snapshot = builder.Build(world, null, 10).Objects.Single();
            Assert.Equal(hex == On ? 1 : 0, snapshot.ShipPresentation!.EngineThrustState);
            Assert.Equal(hex == On, EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(snapshot).ShouldDraw);
            Assert.Equal(initial.VisualGeneration, snapshot.VisualGeneration);
            Assert.Equal(initial.RenderIdentity, snapshot.RenderIdentity);
            Assert.Equal(local, snapshot.IsLocalShip);
        }
        Assert.False(EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(initial).ShouldDraw);
        Assert.Equal(0, world.Objects.FindFirst(Ship)!.AuxData!.Resolve(1922).State); // Not EngineTrailType.
    }

    [Fact]
    public void IncomingDeltaRespectsDeclaredBoundaryWithoutRequiringFullConsumption()
    {
        var packet = PacketFor(On);
        byte[] payload = packet.Payload.ToArray();
        var envelope = Net7WorldWire.DecodeAuxDataEnvelope(0x001B, payload);
        Assert.Equal(13, envelope.Delta.Length);
        Assert.Equal(payload[6..], envelope.Delta);
        var world = CreateWorld(true);
        world.Apply(packet, 10);
        var property = world.Objects.FindFirst(Ship)!.AuxData!.Resolve(1921);
        Assert.Equal(1, property.Value);
        // Short reads stop at the incomplete value; never synthesize padding.
        // The previously completed value must survive.
        for (int count = 0; count < 13; count++)
        {
            var truncated = payload[..(6 + count)];
            world.Apply(Packet.ForOpcode(0x001B, truncated), 11);
            Assert.Equal(1, property.Value);
        }
        // A too-small declaration still bounds the view, and unexpected bytes
        // inside a larger view do not invalidate completed updates.
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 12);
        world.Apply(Packet.ForOpcode(0x001B, payload), 12);
        Assert.Equal(1,property.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 19);
        byte[] extra = [..payload, 0xff];
        world.Apply(Packet.ForOpcode(0x001B, extra), 12);
        Assert.Equal(1, property.Value);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4), 0x8000);
        Assert.Throws<InvalidDataException>(() => world.Apply(Packet.ForOpcode(0x001B, payload), 12));
        Assert.Equal(1, property.Value);
        world.Apply(PacketFor(Off), 13);
        Assert.Equal(0, property.Value);
    }

    private static Net7WorldState CreateWorld(bool local)
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(Ship, 1, 1620, 1, 0, 0, 0));
        if (local) world.SetLocalPlayerId(Ship);
        return world;
    }

    private static Packet PacketFor(string hex) => Packet.ForOpcode(0x001B, Convert.FromHexString(hex)[4..]);
}
