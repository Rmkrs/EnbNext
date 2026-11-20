using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbR11FlightTests
{
    private const string Definitions = "[Move Forward]\nPSW_1=A\nPSW_2=\n[Turn Left]\nPSW_1=Left\nPSW_2=\n[Target Self]\nPSW_1=`\nPSW_2=";
    // The retail key map alone; Next actions (EnbNextControls) come from EnbNext's own file or default.
    private static EnbRetailKeyBinding[] Retail(EnbRetailKeyBindings map) => [.. map.Bindings.Where(b => !EnbNextControls.IsNext(b.Action))];

    [Fact]
    public void KeymapUsesCharacterThenDefaultThenAuthoredPsw()
    {
        var definitions = EnbRetailIniDocument.Parse(Definitions);
        var saved = EnbRetailIniDocument.Parse("[Default]\nUser=common\n[common]\n1_Move Forward=^W\n2_Move Forward=@$Up\n[4294967295]\n1_Turn Left=J");
        var character = EnbRetailKeyBindings.Resolve(definitions, saved, uint.MaxValue);
        Assert.Equal("4294967295", character.SourceSection);
        Assert.Equal("J", Assert.Single(Retail(character)).Key);
        var fallback = EnbRetailKeyBindings.Resolve(definitions, saved, 23);
        Assert.Equal(2, Retail(fallback).Length);
        Assert.Equal(EnbKeyModifiers.Control, Retail(fallback)[0].Modifiers);
        Assert.Equal(EnbKeyModifiers.Alt | EnbKeyModifiers.Shift, Retail(fallback)[1].Modifiers);
        var authored = EnbRetailKeyBindings.Resolve(definitions, EnbRetailIniDocument.Parse(""), null);
        Assert.Equal("PSW", authored.SourceSection);
        Assert.Contains(authored.Bindings, b => b.Action == "Move Forward" && b.Key == "A");
    }

    [Fact]
    public void KeymapSelectorByteSwapsAndEmptySavedMapDoesNotRestoreDefaults()
    {
        var selected = EnbRetailKeyBindings.Resolve(EnbRetailIniDocument.Parse(Definitions),
            EnbRetailIniDocument.Parse("[Default]\nUser=common\n[common]\n1_Move Forward=W\n[16909060]\n1_Move Forward=\n2_Turn Left="), 0x04030201);
        Assert.Equal("16909060", selected.SourceSection);
        Assert.Empty(Retail(selected));
    }

    [Fact]
    public void LocalThrustUsesMaterializedShipPropertiesAndStopsSmoothly()
    {
        var world = FlightWorld();
        Assert.True(world.ApplyLocalFlightControls(7, 1000, null, null, 1));
        Assert.True(world.TrySampleObject(7, 2000, out var forward));
        Assert.InRange(forward.Speed, .14999f, .15001f); // MaxSpeed=150 units/s.
        Assert.True(forward.Position.X > 100);
        world.ApplyLocalFlightControls(7, 2000, null, null, 0);
        world.TrySampleObject(7, 2100, out var slowing);
        Assert.InRange(slowing.Speed, 0.001f, forward.Speed - .001f);
        world.TrySampleObject(7, 3000, out var stopped);
        Assert.Equal(0, stopped.Speed);
        world.ApplyLocalFlightControls(7, 3000, null, null, -1);
        world.TrySampleObject(7, 4000, out var reverse);
        Assert.InRange(reverse.Speed, -.05001f, -.04999f);
    }

    [Fact]
    public void SteeringSignsReleaseAndAutoLevelFollowRetailMath()
    {
        var world = FlightWorld();
        world.ApplyLocalFlightControls(7, 1000, 1, null, null);
        world.TrySampleObject(7, 1500, out var right);
        Assert.True(right.OrientationColumn0.Y < 0);
        world.ApplyLocalFlightControls(7, 1500, 0, null, null);
        world.TrySampleObject(7, 2500, out var released);
        Assert.Equal(right.Orientation, released.Orientation);
        world.ApplyLocalFlightControls(7, 2500, null, 1, null);
        world.TrySampleObject(7, 3000, out var up);
        Assert.True(up.OrientationColumn0.Z > 0);
        world.ApplyLocalFlightControls(7, 3000, null, 0, null);
        world.TrySampleObject(7, 4000, out var level);
        Assert.InRange(MathF.Abs(level.OrientationColumn0.Z), 0, .00001f);
    }

    [Fact]
    public void SubTickFramesAdvanceWithoutPackets()
    {
        var world=FlightWorld();
        world.ApplyLocalFlightControls(7,1000,.7f,.3f,1);
        world.TrySampleObject(7,1100,out var previous);
        for(uint time=1116;time<12000;time+=16)
        {
            Assert.True(world.TrySampleObject(7,time,out var sample));
            Assert.NotEqual(previous.Position,sample.Position);
            previous=sample;
        }
        // Earlier primary queries need not rewind; native cache/command tests
        // cover that branch. Do not assert arbitrary query-order independence.
    }

    [Fact]
    public void MissingAttributesDoNotInventEngineOrTurnRates()
    {
        var world = FlightWorld();
        foreach (int id in new[] {17,18,20,21,22}) world.Objects.FindFirst(7)!.AuxData!.Resolve(id).State = 0;
        Assert.False(world.ApplyLocalFlightControls(7, 1000, 1, 1, 1));
        world.TrySampleObject(7, 2000, out var sample);
        Assert.Equal(new Vector3(100,0,0), sample.Position);
        Assert.Equal(Quaternion.Identity, sample.Orientation);
    }

    [Theory]
    [InlineData(17, float.NaN)]
    [InlineData(18, float.PositiveInfinity)]
    [InlineData(20, float.PositiveInfinity)]
    [InlineData(21, float.NegativeInfinity)]
    [InlineData(22, float.NaN)]
    public void ReceivedNonFiniteFlightPropertiesStillAdmitTheControl(int propertyId, float value)
    {
        var world = FlightWorld();
        var ship = world.Objects.FindFirst(7)!;
        ship.AuxData!.Resolve(propertyId).Value = value;
        uint before = (ship.PendingLocalMovementId ?? ship.Spatial!.MovementId)!.Value;
        bool applied = world.ApplyLocalFlightControls(7, 1000,
            propertyId == 18 ? 1 : null,
            propertyId == 17 ? 1 : null,
            propertyId >= 20 ? propertyId == 21 ? -1 : 1 : null);
        Assert.True(applied);
        Assert.Equal(unchecked(before + 1), ship.PendingLocalMovementId);
        Assert.True(world.TrySampleObject(7, 1000, out _));
    }

    [Fact]
    public void InvalidInputDoesNotEnterTheFlightPipeline()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Net7FlightInput(float.NaN, 0, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Net7FlightInput(0, 2, 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Net7FlightInput(0, 0, 2).Validate());
    }

    internal static Net7WorldState FlightWorld(Net7WorldState? existing = null)
    {
        var world = existing ?? new Net7WorldState();
        byte[] create = new byte[23];
        BinaryPrimitives.WriteUInt32LittleEndian(create,7);
        BinaryPrimitives.WriteSingleLittleEndian(create.AsSpan(4),1);
        BinaryPrimitives.WriteInt16LittleEndian(create.AsSpan(8),1);
        world.Apply(Packet.ForOpcode(4,create),1000);
        world.SetLocalPlayerId(7);
        byte[] position = new byte[42];
        BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(2),7);
        BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(6),1000);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(10),100);
        BinaryPrimitives.WriteSingleLittleEndian(position.AsSpan(34),1);
        BinaryPrimitives.WriteUInt32LittleEndian(position.AsSpan(38),1);
        world.Apply(Packet.ForOpcode(0x3e,position),1000);
        var aux = world.Objects.FindFirst(7)!.AuxData!;
        foreach (var (id,value) in new (int,object)[] { (17,.7f),(18,.9f),(20,150f),(21,-50f),(22,300f),(25,true) })
        { aux.Resolve(id).Value = value; aux.Resolve(id).State = 1; }
        return world;
    }
}
