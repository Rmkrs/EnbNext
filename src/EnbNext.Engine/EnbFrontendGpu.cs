using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering.OpenGL;

namespace EnbNext.Engine;

/// <summary>Frontend composition; authored state stays on the frontend owner.</summary>
internal sealed class EnbFrontendGpu
{
    private readonly EnbOpenGlUiCompositor compositor=new();
    private EnbOpenGlUiCompositor.Image[] labels=[];
    private readonly EnbOpenGlUiCompositor.Image[] decals=Enumerable.Range(0,10).Select(_=>new EnbOpenGlUiCompositor.Image()).ToArray();
    private EnbOpenGlRenderTexture? tooltip;
    private EnbGlApi? context;
    private static Vector4 Bounds(EnbUiRect r,double scale)=>
        new((float)(r.X*scale),(float)(r.Y*scale),(float)(r.Right*scale),(float)(r.Bottom*scale));
    internal void Render(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbFrontendController owner)
    {
        context=gl;compositor.Initialize(gl);
        compositor.MakeOpaque(gl,framebuffer,width,height);
        Fill(owner._caret,0x7f00b219);
        if(labels.Length<owner.Owner.Text.Labels.Count)
        {int old=labels.Length;Array.Resize(ref labels,owner.Owner.Text.Labels.Count);for(int i=old;i<labels.Length;i++)labels[i]=new();}
        DrawLabels(false);
        Border(owner._creationSkillTrackVisual,0x60010c14,0xff123f60);
        Border(owner._creationSkillThumbVisual,0xff031b2b,0xff113952);
        for(int i=0;i<decals.Length;i++)
        {
            var visual=owner._creationDecalImages[i];if(!visual.IsVisible||visual.Texture is null){compositor.Release(gl,decals[i]);continue;}
            compositor.Use(gl,decals[i],visual.Texture);
            compositor.Draw(gl,framebuffer,width,height,decals[i],Bounds(visual.Bounds,scale),Opacity(visual.Opacity),new(0,0,width,height));
        }
        DrawLabels(true);Fill(owner._creationFadeCover,0xff000000);
        int size=Math.Max(width,height);
        if(tooltip is null||tooltip.Size!=size)
        {
            tooltip?.Destroy(gl);
            if(!EnbOpenGlRenderTexture.TryCreate(gl,size,0x8058,0x1908,0x2600,out tooltip,out var error))throw new InvalidOperationException(error);
        }
        owner._creationTooltipGpu.RenderSurface(gl,tooltip!.Framebuffer,new(width,height,scale));
        if(owner._creationTooltipGpu.LastRenderFailure is {} failure)throw new InvalidOperationException(failure);
        compositor.DrawCaptured(gl,framebuffer,width,height,tooltip.Texture,size);
        static float Opacity(double opacity)=>(byte)(255*opacity)/255f;
        void Fill(EnbFrontendVisual visual,uint color)
        {
            if(!visual.IsVisible)return;
            color=(color&0xffffff)|((uint)(byte)((color>>24)*visual.Opacity)<<24);
            compositor.Fill(gl,framebuffer,width,height,Bounds(visual.Bounds,scale),color);
        }
        void Border(EnbFrontendVisual visual,uint fill,uint border)
        {
            if(!visual.IsVisible)return;var bounds=Bounds(visual.Bounds,scale);
            float stroke=(float)scale,half=stroke*.5f;
            compositor.Fill(gl,framebuffer,width,height,new(bounds.X+half,bounds.Y+half,bounds.Z-half,bounds.W-half),fill);
            compositor.Fill(gl,framebuffer,width,height,bounds,border,new(bounds.X+stroke,bounds.Y+stroke,bounds.Z-stroke,bounds.W-stroke));
        }
        void DrawLabels(bool upper)
        {
            int index=0;
            foreach(var label in owner.Owner.Text.Labels.Values)
            {
                var slot=labels[index++];if((label.ZIndex>0)!=upper)continue;
                if(!label.IsVisible||label.Raster is null){compositor.Release(gl,slot);continue;}
                compositor.Use(gl,slot,label.Raster);var r=Bounds(new(label.X,label.Y,label.Width,label.Height),scale);
                Vector4 clip=new(0,0,width,height);
                if(label.Clip is {} c)clip=new(Math.Max(0,r.X+(float)(c.X*scale)),Math.Max(0,r.Y+(float)(c.Y*scale)),Math.Min(width,r.X+(float)(c.Right*scale)),Math.Min(height,r.Y+(float)(c.Bottom*scale)));
                compositor.Draw(gl,framebuffer,width,height,slot,r,Opacity(label.Opacity),clip);
            }
            for(;index<labels.Length;index++)compositor.Release(gl,labels[index]);
        }
    }
    internal void Deinitialize(bool contextLost=false)
    {
        var gl=contextLost?null:context;
        foreach(var slot in labels)compositor.Release(gl,slot);foreach(var slot in decals)compositor.Release(gl,slot);
        tooltip?.Destroy(gl);tooltip=null;compositor.Deinitialize(gl);context=null;
    }
}
