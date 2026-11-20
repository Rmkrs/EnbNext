using EnbNext.Assets;
using EnbNext.Presentation;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EngineUiTextTests
{
    private static EnbRetailFontFace Face(int spacing = 0)
    {
        var pixels = Enumerable.Repeat(new EnbColor32(100, 150, 200, 85), 256).ToArray();
        var image = EnbTextureImage.CreateCurrentRetailRuntimeArgb8(256, 1, pixels);
        var glyphs = Enumerable.Range(0, 256).Select(i => new EnbRetailFontGlyph(i, 0, 0, 0, 0, 0, 1)).ToArray();
        return new("fixture", image, 1, 1, spacing, 2, glyphs);
    }

    [Fact]
    public void UnchangedTextRetainsUploadAndDisplayScale()
    {
        var text = new EnbRetailText(); var face = Face();
        text.SetText("AB", face, new(1, 1, 1));
        var raster = text.Raster;
        text.SetDisplayScale(.5, .75);
        text.SetText("AB", face, new(1, 1, 1));
        Assert.Same(raster, text.Raster);
        Assert.Equal(2, text.Width); Assert.Equal(1.5, text.Height);
        text.SetText("AC", face, new(1, 1, 1));
        Assert.NotSame(raster, text.Raster);
        Assert.Equal(4, text.Width);
    }

    [Fact]
    public void EmptyOptionalTextReleasesUpload()
    {
        var text = new EnbRetailText(); var face = Face();
        text.SetText("A", face, new(1, 1, 1));
        text.SetText("   ", face, new(1, 1, 1));
        Assert.Null(text.Raster); Assert.False(text.IsVisible);
        Assert.Equal(0, text.Width); Assert.Equal(0, text.Height);
        text.SetText("A", null, new(1, 1, 1));
        Assert.Null(text.Raster);
    }

    [Fact]
    public void UploadKeepsStraightAlphaBgraAndNativeNullTermination()
    {
        var raster = Face().RenderText("A\0B", new(.5f, 1, .25f));
        Assert.Equal(1, raster.Width); Assert.Equal(1, raster.Height);
        Assert.Equal(new byte[] {50, 150, 50, 85}, raster.BgraPixels.ToArray());
        var overlap = Face(-1).RenderText("AB", new(1, 1, 1));
        Assert.Equal(1, overlap.Width);
        Assert.Equal(new byte[] {200, 150, 100, 85}, overlap.BgraPixels.ToArray());
    }
    [Fact]
    public void SharedLineMetricsKeepAscendersAndDescendersAtTheSameSize()
    {
        var pixels=new EnbColor32[256*8];
        var glyphs=Enumerable.Range(0,256).Select(i=>new EnbRetailFontGlyph(i,0,0,0,-1,-1,1)).ToArray();
        glyphs['H']=new('H',0,0,1,0,4,1);
        glyphs['p']=new('p',0,0,3,0,6,1);
        foreach(char c in "Hp")for(int y=glyphs[c].MinY;y<=glyphs[c].MaxY;y++)pixels[y*256+c]=new(255,255,255,255);
        var face=new EnbRetailFontFace("fixture",EnbTextureImage.CreateCurrentRetailRuntimeArgb8(256,8,pixels),1,8,0,2,glyphs);
        Assert.Equal(1,face.LineInkTop);Assert.Equal(7,face.LineInkBottom);Assert.Equal(5,face.Baseline);
        var cap=new EnbRetailText();cap.SetText("HH",face,new(1,1,1));
        var descender=new EnbRetailText();descender.SetText("pp",face,new(1,1,1));
        double capTop=cap.FitLineTo(new(0,0,100,10),new(10,5),10,2,false,false);
        double descenderTop=descender.FitLineTo(new(0,0,100,10),new(10,5),10,2,false,false);
        Assert.Equal(cap.Height,descender.Height);Assert.Equal(cap.AppliedUiScale,descender.AppliedUiScale);
        Assert.Equal(capTop,descenderTop);
        Assert.True(descenderTop+(face.LineInkBottom+.5)*descender.Height/8<=10+1e-9);
        var longText=new EnbRetailText();longText.SetText("HHHHHHHHHHHHHHHHHHHH",face,new(1,1,1));
        double longTop=longText.FitLineTo(new(0,0,20,10),new(10,5),10,2,false,false);
        Assert.True(longText.Height<cap.Height);
        Assert.Equal(capTop+face.Baseline*cap.Height/8,longTop+face.Baseline*longText.Height/8,10);
    }
}
