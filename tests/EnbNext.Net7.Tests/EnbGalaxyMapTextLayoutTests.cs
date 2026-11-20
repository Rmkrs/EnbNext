using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbGalaxyMapTextLayoutTests
{
    [Theory]
    [InlineData(1,16,32)]
    [InlineData(129,16,64)]
    [InlineData(250,30,128)]
    [InlineData(1,65,128)]
    public void AtlasFitsHorizontalChunksInSquareRows(int width,int height,int expected)
    {
        int size=EnbGalaxyMapTextLayout.AtlasSize(width,height);
        Assert.Equal(expected,size);
        Assert.True(((width+size-1)/size)*height<=size);
        Assert.Equal(256,EnbGalaxyMapTextLayout.AtlasSize(width,height,256));
    }

    [Fact]
    public void RenderOriginUsesFloorForNegativeCoordinatesAndFixedDepth()
    {
        var at=EnbGalaxyMapTextLayout.RenderOrigin(new(-.1f,1.7f,8),.8f,.8f);
        Assert.Equal(-.4f,at.X); Assert.Equal(2f,at.Y); Assert.Equal(.001f,at.Z);
        var fine=EnbGalaxyMapTextLayout.RenderOrigin(new(-.1f,1.7f,8),.4f,.4f);
        Assert.Equal(-.2f,fine.X); Assert.Equal(1.8000001f,fine.Y); Assert.Equal(.001f,fine.Z);
    }

    [Fact]
    public void ShortLastChunkKeepsItsPixelOffsetButHasMinimumWorldWidth()
    {
        var chunks=EnbGalaxyMapTextLayout.Chunks(129,64,.08f,-4).ToArray();
        Assert.Equal(3,chunks.Length);
        Assert.Equal(128,chunks[2].PixelOffset);
        Assert.Equal(1,chunks[2].PixelWidth);
        Assert.Equal(-4+128*.08f,chunks[2].Left);
        Assert.Equal(.5f,chunks[2].Width);
        Assert.Equal(64*.08f,chunks[0].Width);
    }
}
