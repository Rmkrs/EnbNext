using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class ShipEmoteTests
{
    [Fact]
    public async Task EmoteNotificationPublishesWhileShipIsStationary()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.MaterializeCreate(new(7, 1, 1620, 1, 0, 0, 0));
        var publisher = new EnbPresentationSnapshotPublisher();
        int publications = 0, movementUpdates = 0;
        session.PresentationStateChanged += time =>
        {
            publications++;
            publisher.Publish(session.World, session.Gameplay.Effects, null, time, gameplay: session.Gameplay);
        };
        session.WorldStateChanged += _ => movementUpdates++;
        var dispatch = typeof(Net7Session).GetMethod("DispatchGameplayPacketAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(session, [Packet.ForOpcode(0x00A2,
            new byte[] { 0, 0, 0, 7, 3, 0, 0, 0 }), "decoded-inner", CancellationToken.None])!;
        Assert.Equal(1, publications);
        Assert.Equal(0, movementUpdates);
        Assert.Equal((uint)3, Assert.Single(publisher.Latest!.ShipEmotes).Emote);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StationaryRollUsesNativeDistancePlacementIncludingCoincidentFirstPerson(bool firstPerson)
    {
        var physical = EnbCameraControlTests.Ship(0, 0);
        var configuration = EnbCameraControlTests.Camera() with
        { Modes = [new(0,0,0,0,MathF.PI/2,0), new(1,0,1,0,MathF.PI/2,0)] };
        var normal = new EnbNormalCameraController(firstPerson);
        var emote = new EnbShipEmoteCamera();
        Assert.True(normal.TryResolveFrame(configuration, physical.Spatial!.Transform, 1, 16,
            new(800,600,1), out var previous));
        var initialOrientation = previous.Camera.WorldOrientation;
        for (uint now = 0; now <= 2000; now += 16)
        {
            var animated = physical with { ShipEmote = new(1,3,now/1000d), Spatial = physical.Spatial with
                { Transform = EnbShipEmoteRuntime.Sample(physical.Spatial.Transform, 3, now/2000f, 75) } };
            var pose = emote.Update(animated, previous.Camera.Position, Vector3.Zero, true);
            Assert.True(normal.TryResolveFrame(configuration, physical.Spatial.Transform, 1, 16,
                new(800,600,1), out var frame, pose));
            Assert.False(frame.IsFirstPerson);
            if(!firstPerson)Assert.True(MathF.Abs(Quaternion.Dot(initialOrientation, frame.Camera.WorldOrientation)) > .9999f);
            if(firstPerson)Assert.Equal(Vector3.Zero,pose!.Value.Position);
            else Assert.True(Vector3.Distance(new Vector3(-300,0,0),pose!.Value.Position!.Value)<.0001f);
            if(firstPerson)Assert.Equal(new Quaternion(.5f,0,0,0),pose.Value.RetailCameraOrientation);
            previous = frame;
        }
        if(firstPerson)Assert.Equal(Vector3.Zero,previous.Camera.Position);
        else Assert.True(previous.Camera.Position.X < -250);
        Assert.Equal(firstPerson, normal.IsFirstPerson);
    }

    [Theory]
    [InlineData("/wave", 0)] [InlineData("/loop", 1)] [InlineData("/dive", 2)]
    [InlineData("/rightroll", 3)] [InlineData("/leftroll", 4)]
    [InlineData("/rightspin", 5)] [InlineData("/leftspin", 6)]
    public void MenuAndTypedCommandsProduceOnlyTheAssignedObjectTrigger(string command, uint id)
    {
        var plan = Net7ChatInput.Plan(new(command), new(42, Net7SelectedChatChannel.Local, false, false),
            42, null, false, 0x4000002A);
        var packet = Assert.Single(plan.Packets);
        Assert.Equal(0x00A1, packet.Opcode);
        Assert.Equal(new byte[] { 0x40, 0, 0, 42, (byte)id, 0, 0, 0 }, packet.Payload);
        var station = Net7ChatInput.Plan(new(command),
            new(42, Net7SelectedChatChannel.Local, false, false), 42, null, true, 0x4000002A);
        Assert.Equal(new ushort[]{0x005E,0x0033},station.Packets.Select(p=>p.Opcode));
    }

    [Theory]
    [InlineData(0, 2000, "wave.wav")] [InlineData(1, 3000, "loop.wav")] [InlineData(2, 3000, "loop.wav")]
    [InlineData(3, 2000, "roll.wav")] [InlineData(4, 2000, "roll.wav")]
    [InlineData(5, 1000, "spin.wav")] [InlineData(6, 1000, "spin.wav")]
    public void AllManeuversReturnToPhysicalPose(uint emote, uint duration, string sound)
    {
        var physical = new EnbSceneTransformSnapshot(0, new(300, 400, 500),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .7f), new(1, 2, 3), 4, Vector3.Zero, Vector3.UnitX);
        Assert.Equal(duration, EnbShipEmoteRuntime.Duration(emote));
        Assert.Equal(sound, EnbShipEmoteRuntime.Sound(emote));
        foreach (float t in new[] { 0f, 1f })
        {
            var pose = EnbShipEmoteRuntime.Sample(physical, emote, t, 75);
            Assert.True(Vector3.Distance(physical.Position, pose.Position) < .001f);
            Assert.True(MathF.Abs(Quaternion.Dot(physical.Orientation, pose.Orientation)) > .99999f);
            Assert.Equal(physical.Velocity, pose.Velocity);
        }
    }

    [Fact]
    public void QuarterTurnAndLoopGeometryMatchRetailAxesAndRadius()
    {
        var pose = new EnbSceneTransformSnapshot(0, Vector3.Zero, Quaternion.Identity, Vector3.Zero, 0, Vector3.Zero, Vector3.UnitX);
        var loop = EnbShipEmoteRuntime.Sample(pose, 1, .25f, 75);
        Assert.True(Vector3.Distance(new(150, 0, 150), loop.Position) < .001f);
        Assert.True(Vector3.Distance(Vector3.UnitZ, loop.OrientationColumn0) < .001f);
        var dive = EnbShipEmoteRuntime.Sample(pose, 2, .25f, 75);
        Assert.True(Vector3.Distance(new(150, 0, -150), dive.Position) < .001f);
        Assert.True(Vector3.Distance(-Vector3.UnitZ, dive.OrientationColumn0) < .001f);
        var roll = EnbShipEmoteRuntime.Sample(pose, 3, .25f, 75);
        Assert.True(Vector3.Distance(Vector3.UnitZ, Vector3.Transform(Vector3.UnitY, roll.Orientation)) < .001f);
        Assert.True(Vector3.Distance(-Vector3.UnitY, EnbShipEmoteRuntime.Sample(pose, 5, .125f, 75).OrientationColumn0) < .001f);
    }

    [Fact]
    public void RepeatedNotificationsQueueAndCannotReplayOnReplacementOrHandoff()
    {
        var world = EnbR11FlightTests.FlightWorld();
        var gameplay = new Net7GameplayState(resolveWorldObject: world.Objects.FindFirst);
        var builder = new EnbPresentationSnapshotBuilder();
        var runtime = new EnbShipEmoteRuntime();
        var packet = Packet.ForOpcode(0x00A2, new byte[] { 0, 0, 0, 7, 3, 0, 0, 0 });
        gameplay.Apply(packet); gameplay.Apply(packet);
        Assert.Equal(2, gameplay.ShipEmoteEvents.Count);
        EnbSceneSnapshot Scene(uint time) => builder.Build(world, null, null, time, gameplay: gameplay);
        var first = runtime.Apply(Scene(1000), 1000);
        Assert.Equal(1, first.Objects[0].ShipEmote!.Sequence);
        Assert.Equal(1, runtime.Apply(Scene(3000), 3000).Objects[0].ShipEmote!.Sequence);
        Assert.Null(runtime.Apply(Scene(3001), 3001).Objects[0].ShipEmote);
        Assert.Equal(2, runtime.Apply(Scene(3017), 3017).Objects[0].ShipEmote!.Sequence);
        Assert.Null(runtime.Apply(Scene(5018), 5018).Objects[0].ShipEmote);
        Assert.Null(runtime.Apply(Scene(5034), 5034).Objects[0].ShipEmote);
        var replacement = Scene(5050) with { Objects = [Scene(5050).Objects[0] with { RenderIdentity = 999, WorldCreationOrdinal = 999 }] };
        Assert.Null(runtime.Apply(replacement, 5050).Objects[0].ShipEmote);
        gameplay.ResetForServerHandoff();
        Assert.Empty(gameplay.ShipEmoteEvents);
        Assert.Equal(2, first.ShipEmotes.Length);
    }

    [Fact]
    public void CameraKeepsFixedPositionTracksShipAndRestoresAfterGesture()
    {
        var ship = EnbCameraControlTests.Ship(0, 0) with { ShipEmote = new(1, 1, 0) };
        var camera = new EnbShipEmoteCamera();
        var first = camera.Update(ship, new(-75, 0, 0), Vector3.Zero, true)!.Value;
        Assert.Equal(new Vector3(-300, 0, 0), first.Position);
        Assert.True(first.ForceThirdPerson);
        var moved = ship with { Spatial = ship.Spatial! with { Transform = ship.Spatial.Transform with { Position = new(-200, 0, 0) } } };
        Assert.Equal(first.Position, camera.Update(moved, Vector3.Zero, new(-200, 0, 0), true)!.Value.Position);
        Assert.Null(camera.Update(ship with { ShipEmote = null }, Vector3.Zero, Vector3.Zero, true));
        Assert.False(camera.Active);
        Assert.Null(camera.Update(ship, Vector3.Zero, Vector3.Zero, false));
    }
}

