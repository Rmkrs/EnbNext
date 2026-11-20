using System.Text;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbG09MovieScriptTests
{
    private static EnbRetailMovieScript Parse(string text) => EnbRetailMovieScript.Parse(Encoding.Latin1.GetBytes(text));

    [Fact]
    public void AuthoredFramesPadCreationAndSoundtrackRemainSeparate()
    {
        var script = Parse("0 FRAME_PAD,30\r\n0 CREATE_OBJECT,1,ship\r\n0 PLAY_ANIMATION,1,idle\r\n-60 END_MOVIE\r\n0 SOUNDTRACK,\"music.wav\"\r\n");
        Assert.Equal(new uint[] { 0, 1000, 3000 }, script.Commands.Select(x => x.Milliseconds));
        Assert.Equal("CREATE_OBJECT,1,SHIP", script.Commands[0].Text);
        Assert.Equal("MUSIC.WAV", script.Soundtrack);
        Assert.Equal(1000u, script.FadeAtMilliseconds);
    }

    [Fact]
    public void EqualTimeCreationPrecedenceIsNative()
    {
        var script = Parse("0 END_MOVIE\r\n0 CREATE_DIRECTIONAL_LIGHT_FROM_BONE,0,1,X\r\n0 CREATE_POINT_LIGHT,1\r\n0 CREATE_SHIP_OBJECT,2,S\r\n0 CREATE_OBJECT,1,M\r\n");
        Assert.Equal(new[] { "CREATE_OBJECT", "CREATE_SHIP_OBJECT", "CREATE_POINT_LIGHT", "CREATE_DIRECTIONAL_LIGHT_FROM_BONE", "END_MOVIE" }, script.Commands.Select(x => x.Text.Split(',')[0]));
    }

    [Fact]
    public void LargeEquivalentPartitionIsNotReplacedByStableSorting()
    {
        var script = Parse(string.Concat(Enumerable.Range(0, 17).Select(i => $"1 SHOW_CURRENT_FRAME,{i}\r\n")));
        Assert.Equal(new[] { 16,15,14,13,12,11,10,9,8,7,6,5,4,3,2,1,0 }, script.Commands.Select(x => int.Parse(x.Text.Split(',')[1])));
    }

    [Fact]
    public void PrefixAndQuotedCommaCursorRetainMissingVersusEmpty()
    {
        var args = new EnbRetailMovieArguments("play_animation_suffix,1,\"a,b\", ,0,");
        Assert.False(args.Match("END_MOVIE"));
        Assert.True(args.Match("PLAY_ANIMATION"));
        Assert.Equal("1", args.Read());
        Assert.Equal("A,B", args.Read());
        Assert.Equal("", args.Read());
        Assert.Equal("0", args.Read());
        Assert.Null(args.Read());
    }

    [Fact]
    public void LfOnlyDropsLastByteAsRetailDoes()
    {
        var script = Parse("0 END_MOVIE\n");
        Assert.Equal("END_MOVI", Assert.Single(script.Commands).Text);
    }

    [Fact]
    public void NativeUnterminatedAndOversizeBuffersAreContained()
    {
        Assert.Throws<InvalidDataException>(() => Parse("0 END_MOVIE"));
        Assert.Throws<InvalidDataException>(() => Parse(new string('x',512)+"\n"));
    }

    [Theory]
    [InlineData("-12tail", -12)]
    [InlineData("bad", 0)]
    [InlineData("4294967295", -1)]
    public void IntegerReadsNumericPrefix(string value, int expected) => Assert.Equal(expected, EnbRetailMovieArguments.Integer(value));

    [Theory]
    [InlineData(" -.25suffix", -.25f)]
    [InlineData("1.25e2tail", 125f)]
    [InlineData("2e+garbage", 2f)]
    [InlineData("bad", 0f)]
    public void NumericReadsNumericPrefix(string value, float expected) => Assert.Equal(expected, EnbRetailMovieArguments.Number(value));
}
