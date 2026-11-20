namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;

internal sealed unsafe partial class SdlUiGpuSurface
{
    private readonly ImageSlot[] scannerText=Enumerable.Range(0,13).Select(_=>new ImageSlot()).ToArray();
    internal void RenderScannerText(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbScannerUi owner)
    {
        if(!owner.IsVisible||owner.Replaced||owner.UiLayout?.Visible(EnbNext.Presentation.EnbUiGroup.Scanner)==false)return;
        var canvas=Begin(gl,framebuffer,width,height);canvas.Save();canvas.Scale((float)scale);
        canvas.ClipRect(SKRect.Create(0,0,(float)(width/scale),(float)(height/scale)));
        var panel=Bounds(owner.PanelBounds,scale);canvas.Translate(panel.Left,panel.Top);canvas.Scale(panel.Width/128,panel.Height/138);
        for(int i=0;i<owner.Texts.Length;i++)
        {
            var text=owner.Texts[i];var slot=scannerText[i];if(!text.IsVisible||text.Raster is not {} raster){slot.Dispose();continue;}
            slot.Use(gpu!,raster);var bounds=Bounds(new(text.Left,text.Top,text.Width,text.Height),scale);
            canvas.Save();if(text.Clip is {} clip)canvas.ClipRect(new(bounds.Left+(float)clip.X,bounds.Top+(float)clip.Y,bounds.Left+(float)clip.Right,bounds.Top+(float)clip.Bottom),SKClipOperation.Intersect,true);
            paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.White;canvas.DrawImage(slot.Image!,bounds,new SKSamplingOptions(SKFilterMode.Linear),paint);canvas.Restore();
        }
        canvas.Restore();canvas.Flush();gpu!.Flush();for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);gl.Disable(0x0c11);gl.DepthMask(1);
    }
}
