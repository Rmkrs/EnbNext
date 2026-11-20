using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbG10VideoTests
{
    private static byte[] Source(string value) => [0, 0, ..Encoding.Unicode.GetBytes(value)];
    private sealed class Decoder(params EnbRetailVideoFrame[] frames) : IEnbRetailVideoDecoder
    {
        private int _index;
        public bool Disposed;
        public EnbRetailVideoFrame Poll() => frames[Math.Min(_index++, frames.Length - 1)];
        public void Dispose() => Disposed = true;
    }
    private static EnbRetailVideoFrame Frame(uint number = 2, byte[]? pixels = null, bool complete = false) => new(2, 2, number, 5, 15, 1, pixels, complete);

    [Fact]
    public void ParserKeepsAuthoredOrderDefaultsQuotedFieldsAndNativeNumericConversions()
    {
        var entries = EnbRetailVideoSubtitles.Parse(Source("; ignored\nBeginMovieExtra SAMPLE\n<Time=00:00:02:50,Duration=00:00:00:00,Position=0x10:Right,Color=257:-1:258,Text=\"a, b\",Unknown=yes>\n<Text=\"earlier\",Time=00:00:01:00>\nEndMovieExtra\n"), @"C:\movies\sample.bik");
        Assert.Equal(2, entries.Count); Assert.Equal(150u, entries[0].Time); Assert.Equal(1200u, entries[0].Duration);
        Assert.Equal(16, entries[0].Row); Assert.Equal(1, entries[0].Alignment); Assert.Equal(0x01ff02u, entries[0].Color); Assert.Equal("a, b", entries[0].Text);
        Assert.Equal(60u, entries[1].Time); Assert.Equal(15, entries[1].Row); Assert.Equal(2, entries[1].Alignment);
    }
    [Fact]
    public void OptionalMalformedSubtitlesAreContainedAndAbsentMovieHasNoEvents()
    {
        Assert.Empty(EnbRetailVideoSubtitles.Parse(Source("BeginMovie other\nEndMovie\n"), "wanted.bik"));
        Assert.Throws<InvalidDataException>(() => EnbRetailVideoSubtitles.Parse(Source("BeginMovie wanted\n<Text=x>"), "wanted.bik"));
        var entries = EnbRetailVideoSubtitles.Parse(Source("BeginMovie wanted\n<Text=\"unterminated>\n<Time=broken>\n<Text=valid>\nEndMovie\n"), "wanted.bik");
        Assert.Single(entries); Assert.Equal("valid", entries[0].Text);
    }
    [Theory]
    [InlineData(15u,60u)]
    [InlineData(899u,3595u)]
    [InlineData(900u,0u)]
    [InlineData(54000u,3600u)]
    public void SubtitleClockPreservesNativeMinuteDiscontinuity(uint frame, uint expected) => Assert.Equal(expected, EnbRetailVideoSubtitleState.Clock(frame, 15, 1));
    [Fact]
    public void EventExpiryRetainsGeometryAndLaterEventsCannotBypassFileOrder()
    {
        var first = new EnbRetailVideoSubtitle(100, 2, 0xffffff, 15, 2, "first");
        var second = first with { Time=50,Text="second" };
        var state = new EnbRetailVideoSubtitleState([first,second]);
        state.Update(60); Assert.Null(state.Displayed); Assert.Equal(0, state.NextIndex);
        state.Update(100); Assert.Same(second, state.Displayed); Assert.Null(state.Current); Assert.Equal(2, state.NextIndex);
        state.Update(0); Assert.Same(second, state.Displayed);
    }
    [Fact]
    public void PlaylistAdvancesFailureAndCompletionDisposingEachOwner()
    {
        var one = new Decoder(Frame(pixels: new byte[8]),Frame(5,complete:true)); var two = new Decoder(Frame(5,complete:true));
        var opened = new List<string>();
        using var runtime = new EnbRetailVideoRuntime([new("missing"),new("one"),new("two")], e => { opened.Add(e.File); return e.File switch { "one" => one,"two" => two,_ => null }; }, _ => []);
        runtime.Tick(0); Assert.Equal(1,runtime.Index); Assert.False(runtime.Ended);
        runtime.Tick(1); Assert.NotNull(runtime.Frame);
        runtime.Tick(2); Assert.True(one.Disposed); Assert.Null(runtime.Frame);
        runtime.Tick(3); Assert.True(two.Disposed); Assert.True(runtime.Ended); Assert.Equal(3,runtime.Index);
        Assert.Equal(new[] {"missing","one","two"},opened);
    }
    [Fact]
    public void SkipStartsAtFirstActiveRenderUsesUnsignedElapsedAndRemovesWholePlaylist()
    {
        var decoder = new Decoder(Frame());
        using var runtime = new EnbRetailVideoRuntime([new("one"),new("two")], _ => decoder, _ => []);
        Assert.False(runtime.Input(1,0,10000)); runtime.Tick(uint.MaxValue-100, false); Assert.Null(runtime.Frame);
        runtime.Tick(uint.MaxValue-100);
        Assert.False(runtime.Input(0,0,4000)); Assert.False(runtime.Input(1,0x1000,4000));
        Assert.False(runtime.Input(1,0,2898)); Assert.True(runtime.Input(1,0,2899));
        Assert.True(runtime.Skipped); Assert.True(runtime.Ended); Assert.True(decoder.Disposed); Assert.Equal(0,runtime.Index);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WaitingFrameRetainsPixelsAndMalformedSubtitleDoesNotStopPlayback(bool readFailure)
    {
        byte[] pixels = new byte[8];var decoder = new Decoder(Frame(pixels:pixels),Frame());
        using var runtime = new EnbRetailVideoRuntime([new("one")],_=>decoder,_=>throw (readFailure ? new IOException("bad subtitle") : new InvalidDataException("bad subtitle")));
        runtime.Tick(0); runtime.Tick(1); Assert.Same(pixels,runtime.Frame!.Rgb565); Assert.False(runtime.Ended);Assert.Equal("bad subtitle",runtime.Failure);
        runtime.Dispose(); Assert.Null(runtime.Frame); Assert.True(decoder.Disposed);
    }
    [Fact]
    public void SlowOpenDoesNotConsumeTheThreeSecondSkipGate()
    {
        uint now = 100;
        using var runtime = new EnbRetailVideoRuntime([new("one")], _ => { now = 5100; return new Decoder(Frame()); }, _ => [], () => now);
        runtime.Tick(100);
        Assert.False(runtime.Input(1, 0, 8099));
        Assert.True(runtime.Input(1, 0, 8100));
    }
    [Fact]
    public void CodecFailureAdvancesWithoutRetry()
    {
        int opens=0;
        using var runtime = new EnbRetailVideoRuntime([new("bad")],_=> { opens++;throw new IOException("bad codec"); },_=>[]);
        runtime.Tick(0);runtime.Tick(1);Assert.True(runtime.Ended);Assert.Equal(1,opens);Assert.Equal("bad codec",runtime.Failure);
    }
    [Fact]
    public void TileBordersUseNativeOverlapAndAsymmetricCroppedUvs()
    {
        var tiles=EnbRetailVideoTiles.Create(800,600,256,256);Assert.Equal(12,tiles.Count);
        var last=tiles[^1];Assert.Equal(765,last.X);Assert.Equal(510,last.Y);Assert.Equal(35,last.Width);Assert.Equal(90,last.Height);
        Assert.Equal(34f/256,last.Uv.Z);Assert.Equal(91f/256,last.Uv.W);Assert.Equal(1,last.Destination.Z);Assert.Equal(1,last.Destination.W);
        byte[] source=Enumerable.Range(0,800*600*2).Select(x=>(byte)x).ToArray();byte[] tile=EnbRetailVideoTiles.Rgb565(last,source,800);
        Assert.Equal(source.AsSpan((510*800+765)*2,70).ToArray(),tile[..70]);Assert.All(tile[70..512],b=>Assert.Equal(0,b));
    }
}
