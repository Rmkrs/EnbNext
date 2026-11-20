using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailFootstepSoundTests
{
    [Fact]
    public void SurfacesWithoutANameFallBackToSurfaceZero()
    {
        var surfaces = new IReadOnlyDictionary<string, string>[]
        {
            new Dictionary<string, string> { ["SoundWalking"] = "walk0", ["SoundRunning"] = "run0" },
            new Dictionary<string, string> { ["SoundWalking"] = "walk1" },
            new Dictionary<string, string> { ["SoundWalking"] = " " },
        };
        Assert.Equal("walk1", EnbRetailFootstepSounds.Name(surfaces, 1, false));
        Assert.Equal("run0", EnbRetailFootstepSounds.Name(surfaces, 1, true));     // 004428E0: empty -> surface 0
        Assert.Equal(" ", EnbRetailFootstepSounds.Name(surfaces, 2, false));       // a non-empty string is kept
        Assert.Equal("walk0", EnbRetailFootstepSounds.Name(surfaces, 7, false));   // unloaded slot -> surface 0
        Assert.Equal("walk0", EnbRetailFootstepSounds.Name(surfaces, 32, false));  // outside 0..31 -> surface 0
        Assert.Null(EnbRetailFootstepSounds.Name([new Dictionary<string, string>()], 3, false));
    }
}
