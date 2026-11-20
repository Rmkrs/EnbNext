using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbBridgeAvatarSteeringTests
{
    [Fact]
    public void ReactionsUseRetailPrioritiesAndCompleteBeforeSteeringResumes()
    {
        var playback = new EnbBridgeAvatarSteering();
        Assert.True(playback.Request(EnbBridgeAvatarReaction.UseControls, 4));
        playback.SetSteering(Vector3.Zero);
        Assert.Equal("use_controls4", playback.Tag);
        playback.SetSteering(new(0, 0, 1));
        Assert.Equal("left_turn_small", playback.Tag);
        Assert.True(playback.Request(EnbBridgeAvatarReaction.StartWarp, 2));
        Assert.False(playback.Request(EnbBridgeAvatarReaction.StartWarp, 3));
        Assert.False(playback.Request(EnbBridgeAvatarReaction.GetHit, 1));
        playback.SetSteering(new(0, 0, -1));
        Assert.Equal("start_warp", playback.Tag);
        Assert.True(playback.Request(EnbBridgeAvatarReaction.StopHard, 3));
        Assert.Equal("stop_hard", playback.Tag);
        playback.ObserveControlledBody(true);
        Assert.Equal("stop_hard",playback.Tag);
        playback.ObserveControlledBody(false);
        Assert.Null(playback.Tag);
        playback.SetSteering(new(0, 0, -1));
        Assert.Equal("right_turn_small", playback.Tag);
    }
    [Fact]
    public void TurnsAdvanceToHeldEndpointAndReturnWithoutWrapping()
    {
        var steering = new EnbBridgeAvatarSteering();
        steering.SetSteering(new(0, 0, 1));
        Assert.Equal("left_turn_small", steering.Tag);
        Assert.Equal(0, steering.Target);
        steering.SetSteering(new(0, 0, 1));
        Assert.Equal(1,steering.Target);
        Assert.Equal(2u,steering.Flags);
        steering.ObserveControlledBody(true);
        Assert.Equal("left_turn_small",steering.Tag);
        steering.SetSteering(Vector3.Zero);
        Assert.Equal(0,steering.Target);
        Assert.Equal(0u,steering.Flags);
        steering.ObserveControlledBody(false);
        Assert.Null(steering.Tag);
    }

    [Fact]
    public void DirectionChangesResetAndPitchModulatesNormalizedTarget()
    {
        var steering = new EnbBridgeAvatarSteering();
        steering.SetSteering(new(0, 0, -.1f));
        steering.SetSteering(new(0, -.1f, -.1f));
        Assert.Equal("right_turn_small", steering.Tag);
        Assert.Equal(.05f * 5.7295775f, steering.Target, 5);
        long version=steering.Version;
        steering.SetSteering(new(0, 0, .1f));
        Assert.Equal("left_turn_small", steering.Tag);
        Assert.True(steering.Version>version);
        Assert.Equal(0, steering.Target);
        steering.SetSteering(new(0, -1, .1f));
        Assert.Equal(0, steering.Target);
        Assert.False(steering.Returning);
        steering.SetSteering(new(0, 0, .0001f));
        Assert.True(steering.Returning);
    }
}
