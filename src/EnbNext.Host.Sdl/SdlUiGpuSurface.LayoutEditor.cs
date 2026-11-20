using EnbNext.Engine;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;
namespace EnbNext.Host.Sdl;
internal sealed unsafe partial class SdlUiGpuSurface
{
    private void RenderLayoutEditor(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbWorldUi ui)
    {
        if(!ui.Layout.Editing)return;
        var canvas=Begin(gl,framebuffer,width,height);canvas.Save();canvas.Scale((float)scale);
        foreach(int tool in ui.Editor.ToolOrder)if(ui.Editor.ToolVisible(tool))canvas.ClipRect(Bounds(ui.Editor.ToolRect(tool),scale),SKClipOperation.Difference);
        if(ui.Editor.Dropdown)for(int row=0;row<ui.Editor.ProfileRows;row++)canvas.ClipRect(Bounds(ui.Editor.ProfileRow(row),scale),SKClipOperation.Difference);
        if(ui.Editor.Managing)canvas.ClipRect(Bounds(ui.Editor.Manager,scale),SKClipOperation.Difference);
        if(ui.Editor.ConfirmDelete)canvas.ClipRect(Bounds(ui.Options.LayoutDeleteBounds(0),scale),SKClipOperation.Difference);
        if(ui.Editor.GridVisible)
        {
            canvas.Save();
            foreach(var pair in ui.Editor.Groups)
            {canvas.ClipRect(Bounds(ui.Layout.Apply(pair.Key,pair.Value),scale),SKClipOperation.Difference);canvas.ClipRect(Bounds(ui.Editor.ScaleBar(pair.Key),scale),SKClipOperation.Difference);}
            paint.Style=SKPaintStyle.Stroke;paint.StrokeWidth=(float)(1/scale);paint.Color=new SKColor(110,170,195,55);
            double step=ui.Editor.GridSpacing/scale,w=width/scale,h=height/scale;
            for(double x=step;x<w;x+=step)canvas.DrawLine((float)x,0,(float)x,(float)h,paint);
            for(double y=step;y<h;y+=step)canvas.DrawLine(0,(float)y,(float)w,(float)y,paint);
            paint.Color=new SKColor(145,210,235,125);
            canvas.DrawLine((float)(w/2),0,(float)(w/2),(float)h,paint);canvas.DrawLine(0,(float)(h/2),(float)w,(float)(h/2),paint);
            canvas.Restore();
        }
        foreach(var pair in ui.Editor.Groups)
        {
            var rect=ui.Layout.Apply(pair.Key,pair.Value);
            paint.Style=SKPaintStyle.Stroke;paint.StrokeWidth=1;paint.Color=pair.Key==ui.Editor.Selected?SKColors.Gold:SKColors.Turquoise;
            canvas.DrawRect(Bounds(rect,scale),paint);
        }
        canvas.Restore();canvas.Flush();gpu!.Flush();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);gl.Disable(0x0c11);gl.DepthMask(1);
    }
}
