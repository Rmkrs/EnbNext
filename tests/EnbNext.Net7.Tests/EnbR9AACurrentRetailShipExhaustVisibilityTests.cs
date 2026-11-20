using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AACurrentRetailShipExhaustVisibilityTests
{
    [Theory]
    [InlineData("EXHAUST01", true)]
    [InlineData("exhaust", true)]
    [InlineData("Exhaustion", true)]
    [InlineData("EXHAUS", false)]
    [InlineData("ENGINE01", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ExhaustBoneMatchUsesRetailSevenCharacterCaseInsensitivePrefix(string? boneName, bool expected)
        => Assert.Equal(expected, EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(boneName));

    [Fact]
    public void InitializationAndInactiveStateHideWhileProvenActivationBranchesShow()
    {
        EnbCurrentRetailShipExhaustVisibility initial =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(null, 0);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityDecision.Hidden, initial.Decision);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityReason.InitializedHidden, initial.Reason);
        Assert.False(initial.ShouldDraw);

        EnbCurrentRetailShipExhaustVisibility inactive =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(0, 0);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityDecision.Hidden, inactive.Decision);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityReason.Inactive, inactive.Reason);

        EnbCurrentRetailShipExhaustVisibility thrust =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(1, 0);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityReason.EngineThrust, thrust.Reason);
        Assert.True(thrust.ShouldDraw);

        EnbCurrentRetailShipExhaustVisibility warp =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(0, 2);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityReason.WarpEnter, warp.Reason);
        Assert.True(warp.ShouldDraw);

        EnbCurrentRetailShipExhaustVisibility transition =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(0, 0, retailEmoteOrCameraOverrideActive: true);
        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityReason.EmoteOrCameraOverride, transition.Reason);
        Assert.True(transition.ShouldDraw);
    }

    [Fact]
    public void CachedExitHidesExhaust()
    {
        EnbCurrentRetailShipExhaustVisibility result =
            EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(0, 3);

        Assert.Equal(EnbCurrentRetailShipExhaustVisibilityDecision.Hidden, result.Decision);
        Assert.False(result.ShouldDraw);
    }
    [Fact]
    public void ObjectTypeAndOrganicTermsFollowTheOnlyRetailTogglePaths()
    {
        var scene = new EnbPresentationSnapshotBuilder().Build(EnbR11FlightTests.FlightWorld(), null, 1100);
        var ship = Assert.Single(scene.Objects);
        ship = ship with { ShipPresentation = ship.ShipPresentation! with { EngineThrustState = 0, EngineWarpState = 2 } };
        bool Draws(EnbSceneObjectSnapshot o) => EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(o).ShouldDraw;

        // 006FDBD0: the cached warp term applies to types 0 and 1 only.
        Assert.True(Draws(ship with { ObjectType = 1 }));
        Assert.False(Draws(ship with { ObjectType = 25 }));
        var thrusting = ship with { ShipPresentation = ship.ShipPresentation with { EngineThrustState = 1 } };
        Assert.True(Draws(thrusting with { ObjectType = 25 }));
        // Organic: 006FCB70 hides, 006FDBD0 never re-shows.
        Assert.False(Draws(thrusting with { ObjectType = 1, ShipPresentation = thrusting.ShipPresentation with { IsOrganic = true } }));
        // Other types are never passed to 00810750.
        var idle = ship with { ShipPresentation = ship.ShipPresentation with { EngineWarpState = 0 } };
        Assert.True(Draws(idle with { ObjectType = 3 }));
        Assert.True(Draws(idle with { ObjectType = 3, ShipPresentation = null }));
    }
}
