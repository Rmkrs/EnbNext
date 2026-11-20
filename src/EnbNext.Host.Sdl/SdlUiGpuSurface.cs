namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;

// Device resources only. The frontend controller remains the sole UI owner.
internal sealed unsafe partial class SdlUiGpuSurface : IDisposable
{
    private GRGlInterface? api;
    private delegate* unmanaged<uint,uint,void> bindSampler;
    private int textureUnits;
    private GRContext? gpu;
    private GRBackendRenderTarget? target;
    private SKSurface? surface;
    private EnbOpenGlRenderTexture? tooltip;
    private EnbGlApi? owningGl;
    private (int Framebuffer,int Width,int Height) extent;
    private readonly SKPaint paint=new(){IsAntialias=true};
    private sealed class ImageSlot : IDisposable
    {
        private object? source;
        internal SKImage? Image;
        internal void Use(GRContext context,EnbUiRaster raster)
        {
            if(ReferenceEquals(source,raster))return;
            Dispose();source=raster;
            using var cpu=SKImage.FromPixelCopy(new SKImageInfo(raster.Width,raster.Height,SKColorType.Bgra8888,SKAlphaType.Unpremul),raster.BgraPixels.AsSpan());
            Image=cpu.ToTextureImage(context)??throw new InvalidOperationException("Frontend text GPU upload failed.");
        }
        internal void Use(GRContext context,EnbTextureImage texture)
        {
            if(ReferenceEquals(source,texture))return;
            Dispose();source=texture;
            using var cpu=SKImage.FromPixelCopy(new SKImageInfo(texture.Width,texture.Height,SKColorType.Rgba8888,SKAlphaType.Unpremul),MemoryMarshal.AsBytes(texture.Pixels.Span));
            Image=cpu.ToTextureImage(context)??throw new InvalidOperationException("Frontend image GPU upload failed.");
        }
        public void Dispose(){Image?.Dispose();Image=null;source=null;}
    }
    private SKCanvas Begin(EnbGlApi gl,int framebuffer,int width,int height)
    {
        if(gpu is null)
        {
            gl.GetIntegerv(0x8872,out textureUnits);bindSampler=(delegate* unmanaged<uint,uint,void>)gl.GetProcAddress("glBindSampler");
            api=GRGlInterface.Create(gl.GetProcAddress);gpu=GRContext.CreateGl(api)??throw new InvalidOperationException("Frontend GPU compositor unavailable.");
        }
        gpu.ResetContext();
        if(surface is null||extent!=(framebuffer,width,height))
        {
            surface?.Dispose();target?.Dispose();extent=(framebuffer,width,height);
            target=new(width,height,0,0,new GRGlFramebufferInfo((uint)framebuffer,0x8058));
            using var properties=new SKSurfaceProperties(SKSurfacePropsFlags.None,SKPixelGeometry.RgbHorizontal);
            surface=SKSurface.Create(gpu,target,GRSurfaceOrigin.BottomLeft,SKColorType.Rgba8888,null,properties)??throw new InvalidOperationException("Frontend GPU surface unavailable.");
        }
        return surface.Canvas;
    }
    private static SKRect Bounds(EnbUiRect r,double scale)=>
        SKRect.Create((float)r.X,(float)r.Y,(float)r.Width,(float)r.Height);
    private EnbFrontendGpu? frontend;
    internal void Render(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbFrontendController owner)
    {
        if(!ReferenceEquals(frontend,owner.Composition)){frontend?.Deinitialize();frontend=owner.Composition;}
        frontend.Render(gl,framebuffer,width,height,scale,owner);
    }
    internal void Deinitialize(bool contextLost=false)
    {
        if(contextLost)gpu?.AbandonContext();
        frontend?.Deinitialize(contextLost);frontend=null;
        ReleaseGutterUploads();foreach(var slot in scannerText)slot.Dispose();stationHint.Dispose();
        tooltip?.Destroy(contextLost?null:owningGl);tooltip=null;owningGl=null;
        BrowserSnapshots?.ReleaseGpu(contextLost);
        surface?.Dispose();surface=null;target?.Dispose();target=null;gpu?.Dispose();gpu=null;api?.Dispose();api=null;
    }
    public void Dispose(){foreach(var text in diagnosticText)text?.Dispose();focusLabel?.Dispose();DetachGutter();Deinitialize();gutterPath.Dispose();paint.Dispose();}


}
