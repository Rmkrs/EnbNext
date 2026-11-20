using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbBridgeShakeCompositionTests
{
    [Fact]
    public void LocalShakeFollowsLookedCameraAxesAndWorldCompositionWithoutMutatingBase()
    {
        var bridge = Matrix4x4.CreateRotationZ(MathF.PI / 2) * Matrix4x4.CreateTranslation(3, 4, 5);
        var world = Matrix4x4.CreateRotationY(.4f) * Matrix4x4.CreateTranslation(100, 200, 300);
        var original = EnbBridgeCameraComposition.Create(world, bridge);
        var shaken = original.WithShake(new(8, 16, 100));
        Assert.True(Vector3.Distance(new(-.2f, .1f, 0), shaken.BridgeCamera.Translation - bridge.Translation) < .00001f);
        var expected = shaken.BridgeCamera * world;
        Assert.True(Vector3.Distance(expected.Translation, shaken.WorldCamera.Translation) < .0001f);
        Assert.Equal(bridge, original.BridgeCamera);
        Assert.Equal(original, original.WithShake(Vector3.UnitZ));
        Assert.Equal(original, original.WithShake(Vector3.Zero));
    }

    [Fact]
    public void DepthNormalizationPreservesRetailSmallDepthBranch()
    {
        var original = EnbBridgeCameraComposition.Create(Matrix4x4.Identity, Matrix4x4.Identity);
        Assert.Equal(new Vector3(4, 8, 0), original.WithShake(new(1, 2, 3), 0).BridgeCamera.Translation);
        Assert.Equal(new Vector3(.04f, .08f, 0), original.WithShake(new(1, 2, 3), 100).BridgeCamera.Translation);
    }
}
