using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class EnbGateCameraTests
{
    [Fact]
    public void NearbyGateCanFinishInLessThanOneSecondWithoutTimeout()
    {
        var runtime = new EnbGateCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var ship = EnbCameraControlTests.Ship(0, 1);
        var gate = ship with { ObjectType = 11, Spatial = ship.Spatial! with
            { Transform = ship.Spatial.Transform with { Position = new(3000, 0, 0) } } };
        var request = new Net7GateCameraTransition(1, 99, 1000);
        runtime.Update(request, ship, gate, 1925, Vector3.Zero, 1000, out var commit);
        Assert.False(commit);
        runtime.Update(request, ship, gate, 1925, Vector3.Zero, 1500, out commit);
        Assert.False(commit); // Exactly at the threshold is not close enough.
        runtime.Update(request, ship, gate, 1925, Vector3.Zero, 1516, out commit);
        Assert.True(commit);
        Assert.Contains("proximity after 516ms", runtime.CompletionDiagnostic);
        Assert.Contains("threshold 1000", runtime.CompletionDiagnostic);
    }
    [Fact]
    public void RetailApproachMovesFourUnitsPerMillisecondAndTriggersProximity()
    {
        var runtime = new EnbGateCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var ship = EnbCameraControlTests.Ship(0, 1);
        var gate = ship with { ObjectType = 11, Spatial = ship.Spatial! with
            { Transform = ship.Spatial.Transform with { Position = new(10000, 0, 0) } } };
        var request = new Net7GateCameraTransition(1, 99, 1000);
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 1000, out _);
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 2000, out var commit);
        Assert.False(commit);
        Assert.Equal(new Vector3(4000,0,0), runtime.ShipTransform!.Position);
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 3480, out commit);
        Assert.True(commit); // 80 units from gate, threshold 87.5.
        Assert.Equal(Vector3.Zero, ship.Spatial!.Transform.Position); // No server state mutation.
    }
    [Fact]
    public void GateCameraUsesFixedApproachThenOneCommitAndResetsAfterHandoff()
    {
        var runtime = new EnbGateCameraRuntime(new EnbRetailCameraRandomSource(() => 0u));
        var ship = EnbCameraControlTests.Ship(0, 1);
        var gate = ship with { ObjectType = 11, Spatial = ship.Spatial! with
            { Transform = ship.Spatial.Transform with { Position = new(10000, 0, 0) } } };
        var request = new Net7GateCameraTransition(1, 99, 1000);
        var initial = runtime.Update(request, ship, gate, 100, Vector3.Zero, 1000, out var commit);
        Assert.False(commit);
        Assert.Equal(new Vector3(225,112.5f,0), initial!.Value.Position);
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 8000, out commit);
        Assert.False(commit);
        var exit = runtime.Update(request, ship, gate, 100, Vector3.Zero, 8001, out commit);
        Assert.True(commit);
        Assert.Equal(10375, exit!.Value.Position!.Value.X);
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 9000, out commit);
        Assert.False(commit);
        Assert.Null(runtime.Update(null, ship, gate, 100, Vector3.Zero, 9001, out commit));
        Assert.Equal("normal", runtime.Phase);
    }

    [Fact]
    public void GateProximityUsesRadiusAndClockWrapDoesNotCommitEarly()
    {
        var runtime = new EnbGateCameraRuntime(new EnbRetailCameraRandomSource(() => 0x80000000u));
        var ship = EnbCameraControlTests.Ship(0, 1);
        var gate = ship with { ObjectType = 11 };
        var request = new Net7GateCameraTransition(1, 99, uint.MaxValue - 1000);
        runtime.Update(request, ship, null, 0, Vector3.Zero, 100, out var commit);
        Assert.False(commit);
        runtime.Reset();
        runtime.Update(request, ship, gate, 100, Vector3.Zero, 100, out commit);
        Assert.True(commit);
    }
}
