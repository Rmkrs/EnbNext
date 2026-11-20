using System.Buffers.Binary;
using System.Text;
using EnbNext.App;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbG13ScreenshotTests
{
    [Fact]
    public void GammaMatchesAll256IndependentHighPrecisionValues()
    {
        // Decimal precision 70, exact float32 reciprocal from 0091839D, then floor.
        byte[] expected = Convert.FromHexString("000306080a0c0e101113151618191b1c1e1f212224252628292a2c2d2e2f31323334363738393b3c3d3e3f40424344454647484a4b4c4d4e4f5051525354565758595a5b5c5d5e5f606162636465666768696a6b6c6d6e6f707172737475767778797a7b7c7d7e7f80808182838485868788898a8b8c8d8e8e8f90919293949596979898999a9b9c9d9e9fa0a0a1a2a3a4a5a6a7a7a8a9aaabacadaeaeafb0b1b2b3b4b4b5b6b7b8b9b9babbbcbdbebfbfc0c1c2c3c4c4c5c6c7c8c9c9cacbcccdcececfd0d1d2d2d3d4d5d6d6d7d8d9dadbdbdcdddedfdfe0e1e2e3e3e4e5e6e7e7e8e9eaeaebecedeeeeeff0f1f2f2f3f4f5f5f6f7f8f9f9fafbfcfcfdfeff");
        Assert.Equal(expected, EnbScreenshotOutput.GammaLookup());
    }

    [Theory]
    [InlineData(EnbScreenshotFormat.Tga, 19, 11)]
    [InlineData(EnbScreenshotFormat.Bmp, 19, 11)]
    [InlineData(EnbScreenshotFormat.Jpeg, 16, 8)]
    public void PitchedBgraConvertsChannelsAndPreservesTopLeftExtent(EnbScreenshotFormat format, int width, int height)
    {
        byte[] pixels = Enumerable.Repeat((byte)199, 84 * 11).ToArray();
        for (int y=0; y<11; y++) for(int x=0; x<19; x++)
        { int p=y*84+x*4; pixels[p]=0; pixels[p+1]=255; pixels[p+2]=128; pixels[p+3]=0; }
        var rgb = EnbScreenshotOutput.ConvertBgra(pixels,19,11,84,format);
        Assert.Equal(width*height*3,rgb.Length);
        Assert.Equal((width,height),EnbScreenshotOutput.Extent(19,11,format));
        for(int i=0;i<rgb.Length;i+=3)Assert.Equal(new byte[]{150,255,0},rgb[i..(i+3)]);
        Assert.Equal(199,pixels[83]); // no mutation/padding treated as pixels
    }

    [Theory]
    [InlineData(EnbScreenshotFormat.Tga)]
    [InlineData(EnbScreenshotFormat.Bmp)]
    public void LosslessFilesHaveValidExtentBottomUpBgrPaddingAndFooter(EnbScreenshotFormat format)
    {
        byte[] rgb=[255,0,0, 0,255,0, 0,0,255, 10,20,30, 40,50,60, 70,80,90];
        using var output=new MemoryStream();
        EnbScreenshotOutput.WriteLossless(output,rgb,3,2,format);
        byte[] file=output.ToArray();
        int offset=format==EnbScreenshotFormat.Bmp?54:18;
        int stride=format==EnbScreenshotFormat.Bmp?12:9;
        Assert.Equal(new byte[]{30,20,10,60,50,40,90,80,70},file[offset..(offset+9)]);
        Assert.Equal(new byte[]{0,0,255,0,255,0,255,0,0},file[(offset+stride)..(offset+stride+9)]);
        if(format==EnbScreenshotFormat.Bmp)
        {
            Assert.Equal(file.Length,BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(2)));
            Assert.Equal(3,BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(18)));
            Assert.Equal(2,BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(22)));
            Assert.Equal(new byte[3],file[63..66]);
        }
        else
        {
            Assert.Equal(3,BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12)));
            Assert.Equal(2,BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(14)));
            Assert.Equal(0,file[17]);
            Assert.Equal("TRUEVISION-XFILE.\0",Encoding.ASCII.GetString(file[^18..]));
        }
    }

    [Fact]
    public void InvalidExtentsAndSelectorsFailBeforeProducingBytes()
    {
        Assert.Throws<InvalidDataException>(()=>EnbScreenshotOutput.Extent(7,50,EnbScreenshotFormat.Jpeg));
        Assert.Throws<InvalidDataException>(()=>EnbScreenshotOutput.Extent(65536,1,EnbScreenshotFormat.Tga));
        Assert.Throws<ArgumentOutOfRangeException>(()=>EnbScreenshotOutput.Extent(8,8,(EnbScreenshotFormat)3));
        Assert.Throws<ArgumentException>(()=>EnbScreenshotOutput.ConvertBgra(new byte[20],8,8,32,EnbScreenshotFormat.Bmp));
    }

    [Fact]
    public void CaptureOptionKeepsCredentialsOpaqueAndDefaultsToFrames()
    {
        var defaults=StartupMovieCapture.Parse(["-CREATEMOVIE"]);
        Assert.Equal("Frames",defaults.Prefix);Assert.Empty(defaults.Remaining);
        var parsed=StartupMovieCapture.Parse(["--password","-CREATEMOVIE","-createmovie","folder/Take"]);
        Assert.Equal("folder/Take",parsed.Prefix);Assert.Equal(new[]{"--password","-CREATEMOVIE"},parsed.Remaining);
        Assert.Null(StartupMovieCapture.Parse([]).Prefix);
        Assert.Equal("Take",StartupMovieCapture.Parse(["-CREATEMOVIETake","-CREATEMOVIE","Later"]).Prefix);
    }

    [Fact]
    public void CaptureClockIgnoresWallAndSkipAdvances33_33_34AndHoldsPausePhase()
    {
        using var movie=new EnbRetailMovieRuntime(EnbRetailMovieScript.Parse("0 DEBUG_MODE\r\n"u8.ToArray()),new Scene(),true);
        var clock=new EnbRetailMovieClock();
        clock.Advance(movie,900,true);Assert.Equal(0u,movie.Milliseconds);
        clock.Advance(movie,901,true);Assert.Equal(33u,movie.Milliseconds);
        movie.Input(0x20);clock.Advance(movie,99999,true);Assert.Equal(33u,movie.Milliseconds);
        movie.Input(0x6b);movie.Input(0x20);
        clock.Advance(movie,1,true);Assert.Equal(66u,movie.Milliseconds);
        clock.Advance(movie,800000,true);Assert.Equal(100u,movie.Milliseconds);
        for(int i=0;i<27;i++)clock.Advance(movie,0,true);
        Assert.Equal(1000u,movie.Milliseconds);
    }
    private sealed class Scene:IEnbRetailMovieScene
    {
        public bool Execute(string c,EnbRetailMovieArguments a,bool p,uint m)=>true;
        public void Update(uint m){} public void PlaySoundtrack(string n){} public void ExpireEffect(int id){} public void CycleCamera(){} public void Dispose(){}
    }
}
