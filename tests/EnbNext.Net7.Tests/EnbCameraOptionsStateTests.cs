using EnbNext.Assets;
using EnbNext.Client;

namespace EnbNext.Net7.Tests;

public sealed class EnbCameraOptionsStateTests
{
    [Fact]
    public void DraftDoesNotChangeSourceAndReentryDiscardsUncommittedChanges()
    {
        var source = new EnbRetailPlayerOptions(false, CameraShip: true, CameraWarp: false,
            CameraLoot: true, CameraGate: false, CameraLand: true, CameraDock: false, CameraBridge: true);
        var draft = new EnbCameraOptionsState();
        draft.Begin(source);
        for (int i = 0; i < 7; i++)
        {
            Assert.Equal(i % 2 == 0, draft[i]);
            draft.Toggle(i);
            Assert.Equal(i % 2 != 0, draft[i]);
        }
        EnbRetailCameraOptions? saved = null;
        Assert.True(draft.TrySave(value => saved = value, out var error));
        Assert.Null(error);
        Assert.Equal(new(false, true, false, true, false, true, false), saved);
        draft.Begin(source);
        for (int i = 0; i < 7; i++) Assert.Equal(i % 2 == 0, draft[i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void SaveFailurePreservesDraftForRetry(int failure)
    {
        var draft = new EnbCameraOptionsState();
        draft.Begin(new(false));
        draft.Toggle(3);
        Action<EnbRetailCameraOptions>? save = failure == 0 ? null : _ => throw failure switch
        {
            1 => new IOException("write"),
            2 => new UnauthorizedAccessException("denied"),
            _ => new InvalidOperationException("unavailable"),
        };
        Assert.False(draft.TrySave(save, out var error));
        Assert.StartsWith("Could not save camera settings: ", error);
        Assert.True(draft[3]);
        Assert.True(draft.TrySave(value => Assert.True(value.Gate), out error));
        Assert.Null(error);
    }
}
