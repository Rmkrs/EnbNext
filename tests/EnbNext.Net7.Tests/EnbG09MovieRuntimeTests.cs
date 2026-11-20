using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbG09MovieRuntimeTests
{
    [Theory]
    [InlineData(0u, new int[] { -14, -63, -41, 19, 9, -86, 48, -83, 34, 17, 44, -26 }, new int[] { -38, -76, 23, -26, 93, -34 })]
    [InlineData(41u, new int[] { 83, -12, 36, 58, -62, 19, 79, -10, 91, -36, 27, 16 }, new int[] { -13, 58, -70, 38, 57, 26 })]
    public void NativeRandomRejectionAndCircularStateMatchRawInstructionVectors(uint seed, int[] start, int[] end)
    {
        var random = new EnbRetailMovieRandom(seed);
        Assert.Equal(7, random.Next(7, 7)); // Equal endpoints consume no state.
        Assert.Equal(int.MinValue, random.Next(int.MinValue, int.MaxValue));
        var values = Enumerable.Range(0, 600).Select(_ => random.Next(100, -100)).ToArray();
        Assert.Equal(start, values[..12]);
        Assert.Equal(end, values[^6..]);
    }

    private static EnbRetailMovieScript Script(string text) => EnbRetailMovieScript.Parse(Encoding.Latin1.GetBytes(text.Replace("\n", "\r\n")));
    private sealed class Scene : IEnbRetailMovieScene
    {
        public List<string> Events { get; } = [];
        public string? Fail { get; set; }
        public bool Execute(string name, EnbRetailMovieArguments arguments, bool preload, uint time)
        { Events.Add($"{name}:{preload}:{time}"); return name != Fail; }
        public void Update(uint milliseconds) => Events.Add($"update:{milliseconds}");
        public void PlaySoundtrack(string name) => Events.Add($"music:{name}");
        public void ExpireEffect(int id) => Events.Add($"expire:{id}");
        public void CycleCamera() => Events.Add("camera");
        public void Dispose() => Events.Add("dispose");
    }

    [Fact]
    public void PreloadIsSourceOrderedAndFailureDoesNotAbortAdmission()
    {
        var scene = new Scene { Fail = "CREATE_SFX_ON_OBJECT" };
        using var movie = new EnbRetailMovieRuntime(Script("90 CREATE_SFX_ON_OBJECT,1,2\n30 CREATE_SFX_ON_OBJECT,2,3\n0 SOUNDTRACK,test.wav\n"), scene, true);
        Assert.Equal(2, scene.Events.Count);
        Assert.All(scene.Events, e => Assert.Equal("CREATE_SFX_ON_OBJECT:True:0", e));
        movie.Update(0);
        Assert.True(movie.Started); Assert.False(movie.Ended);
        Assert.Equal("music:TEST.WAV", scene.Events[^1]);
        Assert.DoesNotContain(scene.Events, e => e.StartsWith("update:"));
        movie.Update(1000);
        Assert.True(movie.Ended); Assert.NotNull(movie.FailedCommand);
        Assert.Equal(2, movie.RemainingCommands);
    }

    [Fact]
    public void EndStillExecutesOtherDueCommandsButFailureRetainsCursor()
    {
        var scene = new Scene();
        using var movie = new EnbRetailMovieRuntime(Script("0 END_MOVIE\n0 SHOW_CURRENT_FRAME\n0 UNKNOWN\n0 DEBUG_MODE\n"), scene, true);
        movie.Update(0);
        Assert.True(movie.Ended); Assert.True(movie.ShowCurrentFrame); Assert.False(movie.DebugMode);
        Assert.Equal("UNKNOWN", movie.FailedCommand!.Text); Assert.Equal(2, movie.RemainingCommands);
        movie.Update(1000); Assert.Equal(0u, movie.Milliseconds);
    }

    [Fact]
    public void DeadlineRemovalPrecedesDueCommandsAndIncludesEquality()
    {
        var scene = new Scene();
        using var movie = new EnbRetailMovieRuntime(Script("3 TURN_OFF_FOG\n"), scene, false);
        movie.Update(0); movie.RetireEffectAt(-4, 100); movie.Update(99);
        Assert.DoesNotContain("expire:-4", scene.Events);
        movie.Update(1);
        Assert.Equal(new[] { "expire:-4", "TURN_OFF_FOG:False:100", "update:1" }, scene.Events.TakeLast(3));
    }

    [Fact]
    public void ClockExcludesPauseAndAppliesCumulativeSkipOnce()
    {
        using var movie = new EnbRetailMovieRuntime(Script("0 DEBUG_MODE\n"), new Scene(), true);
        var clock = new EnbRetailMovieClock();
        clock.Advance(movie, 100); clock.Advance(movie, 150); clock.Advance(movie, 180);
        Assert.Equal(30u, movie.Milliseconds);
        movie.Input(0x20); clock.Advance(movie, 200); clock.Advance(movie, 1200);
        movie.Input(0x6b); clock.Advance(movie, 1300); Assert.Equal(30u, movie.Milliseconds);
        movie.Input(0x20); clock.Advance(movie, 1400); Assert.Equal(5030u, movie.Milliseconds);
        clock.Advance(movie, 1420); Assert.Equal(5050u, movie.Milliseconds);
    }

    [Fact]
    public void DebugGateAndKeyUpGuardDoNotSuppressEscapeOrCameraCycling()
    {
        var scene = new Scene();
        var movie = new EnbRetailMovieRuntime(Script("0 DEBUG_MODE\n0 SKIP_FRAMES\n"), scene, false);
        movie.Update(0); Assert.False(movie.Ended);
        movie.Input(0x20); movie.Input(0x6b); Assert.False(movie.Paused); Assert.Equal(0, movie.RequestedSkipMilliseconds);
        movie.Input(0x1000001b); Assert.False(movie.Ended);
        movie.Input(0x22); Assert.Contains("camera", scene.Events);
        movie.Input(0x1b); Assert.True(movie.Ended);
        movie.Dispose(); movie.Dispose(); Assert.Single(scene.Events, e => e == "dispose");
    }

    [Fact]
    public void ShakeUsesAbsoluteWallDeadlineInclusiveRandomAndPixelGrid()
    {
        var shake = new EnbRetailMovieShake();
        shake.Add(.25f, 1, 100);
        Assert.Equal(new Vector3(1, 1, 0), shake.Sample(100, 640, 480, () => 100));
        Assert.Equal(new Vector3(-1, -1, 0), shake.Sample(1099, 640, 480, () => -100));
        Assert.Equal(Vector3.Zero, shake.Sample(1100, 640, 480, () => 100));
        Assert.Equal(0, shake.Count);
        shake.Add(.005f, 1, 2000);
        Assert.Equal(Vector3.Zero, shake.Sample(2000, 640, 480, () => 100)); // floor
        Assert.Equal(new Vector3(.5f, .5f, 0), shake.Sample(2000, 1280, 960, () => 100));
        Assert.Equal(new Vector3(-1,-1,0),shake.Sample(2000,640,480,()=>-100));
    }

    [Fact]
    public void OverlayKeepsNativeRateAfterOvershootAndFogDividesAtZero()
    {
        var transition = new EnbRetailMovieTransition();
        transition.Set(1, 1); transition.Update(2);
        Assert.Equal(1, transition.Current); Assert.Equal(1, transition.Rate);
        var fog = new EnbRetailMovieColorTransition(); fog.Set(Vector3.One, 0, false);
        Assert.True(float.IsPositiveInfinity(fog.Rate.X)); fog.Update(.001f);
        Assert.Equal(Vector3.One, fog.Current); Assert.Equal(Vector3.Zero, fog.Rate);
    }

    [Fact]
    public void MovieColorPackingAcceptsNativeInvalidAndUnclampedChannels()
    {
        Assert.Equal(0x0000ffu, EnbRetailMovieFrame.PackRgb(new(float.NaN, float.NaN, 1)));
        Assert.Equal(0xfe0000u, EnbRetailMovieFrame.PackRgb(new(2, 0, 0)));
        Assert.Equal(0xffffffu, EnbRetailMovieFrame.PackRgb(new(0, 0, -1f / 255)));
    }

    [Fact]
    public void PadStartsBlackAndFadeChecksPreviousMovieClock()
    {
        using var movie = new EnbRetailMovieRuntime(Script("0 FRAME_PAD,30\n"), new Scene(), true);
        Assert.Equal(1, movie.Overlay.Opacity.Current);
        movie.Update(0); movie.Update(1000); Assert.Equal(1, movie.Overlay.Opacity.Current);
        movie.Update(500); Assert.Equal(.5f, movie.Overlay.Opacity.Current);
        movie.Update(500); Assert.Equal(0, movie.Overlay.Opacity.Current);
    }
}
