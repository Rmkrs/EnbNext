using System.Buffers.Binary;
using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbCurrentRetailTgaHeaderStateTests
{
    [Fact]
    public void FileHeaderRequiresFooterReadAndUsesSignedIdLength()
    {
        Assert.False(EnbCurrentRetailTgaHeaderState.TryRead(new byte[25],out _));
        var file=new byte[40];
        file[2]=2;file[16]=24;
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(12),64);
        BinaryPrimitives.WriteInt16LittleEndian(file.AsSpan(14),8);
        Assert.True(EnbCurrentRetailTgaHeaderState.TryRead(file,out var state));
        Assert.NotNull(state);
        Assert.Equal((short)64,state.Width);Assert.Equal((short)8,state.Height);
        Assert.Equal(1u,state.Format);Assert.Equal(18,state.PixelOffset);
        file[0]=255;
        Assert.True(EnbCurrentRetailTgaHeaderState.TryRead(file,out state));
        Assert.Equal(17,state!.PixelOffset);
        file[0]=128;
        Assert.False(EnbCurrentRetailTgaHeaderState.TryRead(file,out _));
    }

    [Fact]
    public void RecognizedFooterNeedsTheCompleteExtensionRecord()
    {
        var file=new byte[600];
        var footer=file.AsSpan(file.Length-26);
        "TRUEVISION-XFILE"u8.CopyTo(footer[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(footer,100);
        Assert.True(EnbCurrentRetailTgaHeaderState.TryRead(file,out var state));
        Assert.Equal(100u,state!.ExtensionOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(footer,106);
        Assert.False(EnbCurrentRetailTgaHeaderState.TryRead(file,out _));
        footer[8]=0;
        Assert.True(EnbCurrentRetailTgaHeaderState.TryRead(file,out state));
        Assert.Equal(0u,state!.ExtensionOffset);
    }

    [Theory]
    [InlineData(64,64,8,8,7)]
    [InlineData(256,256,32,32,9)]
    [InlineData(512,64,16,2,7)]
    [InlineData(1024,1024,32,32,11)]
    [InlineData(257,33,32,4,7)]
    [InlineData(32,4,4,1,3)]
    [InlineData(1,257,1,32,1)]
    [InlineData(0,0,1,1,1)]
    public void ThumbnailShapeUsesQuarterStepsAndTheSmallerDimension(short width,short height,uint expectedWidth,uint expectedHeight,uint expectedLevels)
        =>Assert.Equal((expectedWidth,expectedHeight,expectedLevels),EnbCurrentRetailTgaHeaderState.SelectThumbnailShape(width,height));

    [Theory]
    [InlineData(1,3,8,13,1)]
    [InlineData(0,3,8,14,1)]
    [InlineData(0,11,8,9,1)]
    [InlineData(0,2,16,6,2)]
    [InlineData(0,2,24,1,3)]
    [InlineData(0,2,32,2,4)]
    [InlineData(0,2,7,0,0)]
    public void ThumbnailFormatMappingIsIndependentOfPixelReaderAdmission(byte palette,byte imageType,sbyte depth,uint expectedFormat,int expectedBytes)
        =>Assert.Equal((expectedFormat,expectedBytes),EnbCurrentRetailTgaHeaderState.SelectFormat(palette,imageType,depth));
}
