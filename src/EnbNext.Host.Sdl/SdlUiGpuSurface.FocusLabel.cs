using EnbNext.Rendering.OpenGL;
using SkiaSharp;

namespace EnbNext.Host.Sdl;

internal sealed unsafe partial class SdlUiGpuSurface
{
    private StatusText? focusLabel;private float focusLabelSize;
    /// <summary>
    /// Next multibox Focus mode: a thumbnail's character name, centred along its top edge over the game. It is sized in
    /// the window's own pixels (<paramref name="density"/> per point), not by the thumbnail's reduced UI scale, so it
    /// stays readable however small the thumbnail is; a name wider than the thumbnail is cut at its edges.
    /// </summary>
    internal void RenderFocusLabel(EnbGlApi gl,int width,int height,double density,string label,float size=24)
    {
        if(label.Length==0||width<=0||height<=0||density<=0)return;
        var canvas=Begin(gl,0,width,height);canvas.Save();canvas.Scale((float)density);
        if(focusLabel is null||focusLabelSize!=size){focusLabel?.Dispose();focusLabel=new(size);focusLabelSize=size;}
        var text=focusLabel;text.Use(label,100000);
        float pad=size/2,margin=4,top=6;
        float available=(float)(width/density)-2*margin;
        float boxWidth=Math.Min(text.Width+2*pad,available),boxHeight=text.Height+size/3;
        float x=((float)(width/density)-boxWidth)/2;
        paint.Style=SKPaintStyle.Fill;paint.Color=new(8,14,22,205);
        canvas.DrawRoundRect(x,top,boxWidth,boxHeight,size/4,size/4,paint);
        canvas.ClipRect(SKRect.Create(x+2,top,boxWidth-4,boxHeight));
        paint.Color=new(255,209,89); // the player frame's name colour
        text.Draw(canvas,x+(boxWidth-Math.Min(text.Width,boxWidth-2*pad))/2,top+size/6,paint);
        canvas.Restore();canvas.Flush();gpu!.Flush();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);
        gl.Disable(0x0c11);gl.DepthMask(1);
    }
}
