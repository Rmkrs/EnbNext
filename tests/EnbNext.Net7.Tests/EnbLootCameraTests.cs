using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLootCameraTests
{
    private static readonly EnbSceneObjectSnapshot Ship = EnbCameraControlTests.Ship(0, 0);
    private static EnbSceneObjectSnapshot Target(float distance) => Ship with
    { GameId = 77, RenderIdentity = 77, Spatial = Ship.Spatial! with
        { Transform = Ship.Spatial.Transform with { Position = new(distance, 0, 0) } } };
    private static EnbCameraPoseOverride? Step(EnbLootCameraRuntime runtime, ulong sequence, uint now,
        float? distance = 1000, bool enabled = true, bool available = true, EnbSceneObjectSnapshot? ship = null)
        => runtime.Update(new(sequence, 77), ship ?? Ship, distance is {} d ? Target(d) : null,
            new(distance ?? 0, 0, 0), Vector3.Zero, 100, now, enabled, available);

    [Fact]
    public void FixedCameraTracksTractorThenReturnsToShipForOneSecond()
    {
        var runtime = new EnbLootCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var first = Step(runtime, 1, 100)!.Value;
        Assert.Equal(new Vector3(-225, 225, 0), first.Position);
        Assert.True(first.ForceThirdPerson);
        var moved = Step(runtime, 1, 200, 500)!.Value;
        Assert.Equal(first.Position, moved.Position);
        Assert.NotEqual(first.Orientation, moved.Orientation);
        // sqrt(100^2 + 75^2) = 125: strict less-than starts the return.
        Assert.NotNull(Step(runtime, 1, 300, 125));
        var returning = Step(runtime, 1, 400, 124)!.Value;
        Assert.NotEqual(moved.Orientation, returning.Orientation);
        Assert.NotNull(Step(runtime, 1, 1400, null));
        Assert.Null(Step(runtime, 1, 1401, null));
        Assert.Null(Step(runtime, 1, 1402));
    }

    [Fact]
    public void RepeatedPacketDoesNotRestartTimeoutAndMissingTargetHasReturnTail()
    {
        var runtime = new EnbLootCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        Step(runtime, 1, 0);
        Assert.NotNull(Step(runtime, 2, 15000));
        Assert.Null(Step(runtime, 2, 15001));
        Step(runtime, 3, 20000);
        Assert.NotNull(Step(runtime, 3, 20010, null));
        Assert.NotNull(Step(runtime, 3, 21010, null));
        Assert.Null(Step(runtime, 3, 21011, null));
    }

    [Fact]
    public void DisabledBusyAndReplacementRequestsDoNotReplay()
    {
        var runtime = new EnbLootCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        Assert.Null(Step(runtime, 1, 0, enabled: false));
        Assert.Null(Step(runtime, 1, 1));
        Assert.Null(Step(runtime, 2, 2, available: false));
        Assert.Null(Step(runtime, 2, 3));
        Assert.NotNull(Step(runtime, 3, 4));
        Assert.Null(Step(runtime, 3, 5, ship: Ship with { RenderIdentity = 999 }));
        Assert.Null(Step(runtime, 3, 6, ship: Ship with { RenderIdentity = 999 }));
    }

    [Fact]
    public void NewTractorRetargetsAnActiveCamera()
    {
        var runtime = new EnbLootCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var first = Step(runtime, 1, 100)!.Value;
        var other = Target(1000) with { GameId = 88, Spatial = Ship.Spatial! with
            { Transform = Ship.Spatial.Transform with { Position = new(0, 1000, 0) } } };
        var second = runtime.Update(new(2, 88), Ship, other, new(0, 1000, 0), Vector3.Zero,
            100, 200, true, true)!.Value;
        Assert.NotEqual(first.Position, second.Position);
        Assert.Equal(new Vector3(-225, -225, 0), second.Position);
    }

    [Fact]
    public void NotificationsReachImmutableCameraSnapshotAndClearOnHandoff()
    {
        var state = new Net7GameplayState();
        var world = EnbR11FlightTests.FlightWorld();
        var builder = new EnbPresentationSnapshotBuilder();
        state.AddCameraEvent(3, 77);
        var first = builder.Build(world, null, null, 100, gameplay: state);
        state.AddCameraEvent(3, 88);
        Assert.Equal((uint)77, first.Camera.LootRequest!.TargetId);
        var second = builder.Build(world, null, null, 101, gameplay: state);
        Assert.Equal((uint)88, second.Camera.LootRequest!.TargetId);
        Assert.True(second.Camera.LootRequest.Sequence > first.Camera.LootRequest.Sequence);
        state.ResetForServerHandoff();
        Assert.Null(builder.Build(world, null, null, 102, gameplay: state).Camera.LootRequest);
    }
}
