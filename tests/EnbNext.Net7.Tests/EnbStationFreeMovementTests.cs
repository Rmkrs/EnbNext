using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

/// <summary>Next free station movement (option): keys move the way the camera faces; retail movement is unchanged.</summary>
public sealed class EnbStationFreeMovementTests
{
    private static Vector3 Free(EnbStationMovement movement, Vector2 wish, int frames = 60)
    {
        movement.Free = wish;
        for (int i = 0; i < frames; i++) movement.Advance(1 / 60.0, (from, delta) => from + delta);
        return movement.Position;
    }

    [Fact]
    public void StrafingTurnsTheAvatarAndKeepsTheView()
    {
        var movement = new EnbStationMovement(Matrix4x4.Identity); // Facing +X, the camera behind it.
        float view = movement.Heading + movement.CameraYaw;
        var end = Free(movement, new(1, 0));
        // Right of the camera is -Y; the avatar now faces that way, walking forward, and the camera still looks along +X.
        Assert.True(end.Y < -5 && MathF.Abs(end.X) < 2);
        Assert.InRange(MathF.IEEERemainder(movement.Heading - (view - MathF.PI / 2), MathF.Tau), -.01f, .01f);
        Assert.InRange(MathF.IEEERemainder(movement.Heading + movement.CameraYaw - view, MathF.Tau), -.01f, .01f);
        Assert.Equal(1, movement.Moving);
    }

    [Fact]
    public void DiagonalsAndBackingUp()
    {
        var movement = new EnbStationMovement(Matrix4x4.Identity);
        var diagonal = Free(movement, new(-1, 1));
        Assert.True(diagonal.X > 3 && diagonal.Y > 3);
        movement = new EnbStationMovement(Matrix4x4.Identity);
        var back = Free(movement, new(0, -1));
        // Backing up keeps facing the camera and plays the backward walk.
        Assert.True(back.X < -3 && MathF.Abs(back.Y) < .01f);
        Assert.Equal(0, movement.Heading, 3);
        Assert.Equal(-1, movement.Moving);
    }

    [Fact]
    public void FirstPersonStepsSidewaysWithoutTurning()
    {
        var movement = new EnbStationMovement(Matrix4x4.Identity) { FreeFirstPerson = true };
        var end = Free(movement, new(1, 0));
        Assert.True(end.Y < -5);
        Assert.Equal(0, movement.Heading, 3);
    }

    [Fact]
    public void TheMouseTurnsTheViewAndWithTheRightButtonTheAvatar()
    {
        var movement = new EnbStationMovement(Matrix4x4.Identity);
        // The left button: the view swings around the avatar, which keeps its facing.
        movement.TurnFree(.5f, -.2f, avatar: false);
        Assert.Equal(0, movement.Heading, 4);
        Assert.Equal(.5f, movement.CameraYaw, 4);
        Assert.Equal(-.2f, movement.CameraPitch, 4);
        // The right button: the avatar turns to face the view at once, and with the mouse after that.
        movement.TurnFree(.1f, 0, avatar: true);
        Assert.Equal(.6f, movement.Heading, 4);
        Assert.Equal(0, movement.CameraYaw, 4);
        // Looking is held within the retail tilt range.
        movement.TurnFree(0, -5, avatar: true);
        Assert.Equal(-.8f, movement.CameraPitch, 4);
    }

    [Fact]
    public void RetailMovementIsUnchangedWithoutTheOption()
    {
        var movement = new EnbStationMovement(Matrix4x4.Identity) { Thrust = 1 };
        for (int i = 0; i < 60; i++) movement.Advance(1 / 60.0, (from, delta) => from + delta);
        Assert.True(movement.Position.X > 5 && movement.Position.Y == 0);
        Assert.Equal(0, movement.CameraYaw);
    }
}
