namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;

internal sealed unsafe partial class SdlUiGpuSurface
{
    private readonly ImageSlot stationHint=new();
    internal SdlBrowserSnapshots? BrowserSnapshots {get;set;}
    internal void DetachWorld(){DetachGutter();Deinitialize();}
    internal void RenderWorldUi(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbWorldController owner)
    {
        EnbModernText.Renderer??=new SdlModernTextRenderer();
        AttachGutter(owner.Ui.Gutter);
        // Next "Hide user interface": no gutter, scanner text or HUD layer; only space is drawn.
        bool shown=!owner.InterfaceHidden;
        if(owner.GutterVisible&&shown)RenderGutter(gl,framebuffer,width,height,scale);
        if(shown)RenderScannerText(gl,framebuffer,width,height,scale,owner.Ui.Scanner);
        var canvas=Begin(gl,framebuffer,width,height);
        if(owner.LoadingVisible)canvas.Clear(SKColors.Black);
        canvas.Flush();gpu!.Flush();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);
        gl.Disable(0x0c11);gl.DepthMask(1);
        RenderLayoutEditor(gl,framebuffer,width,height,scale,owner.Ui);
        // The loading screen is not interface: it still covers a sector change while the interface is hidden.
        if(owner.HudVisible&&(shown||owner.Hud.LoadingPresented))
        {
            int size=Math.Max(width,height);
            if(tooltip is null||tooltip.Size!=size)
            {
                tooltip?.Destroy(gl);
                if(!EnbOpenGlRenderTexture.TryCreate(gl,size,0x8058,0x1908,0x2600,out tooltip,out var error))throw new InvalidOperationException(error);
                owningGl=gl;
            }
            owner.Hud.RenderSurface(gl,tooltip!.Framebuffer,new(width,height,scale));
            if(owner.Hud.LastRenderFailure is {} failure)throw new InvalidOperationException(failure);
            gl.BindFramebuffer(0x8d40,framebuffer);canvas=Begin(gl,framebuffer,width,height);
            using(var backend=new GRBackendTexture(size,size,false,new GRGlTextureInfo(0x0de1,(uint)tooltip.Texture,0x8058)))
            using(var image=SKImage.FromTexture(gpu,backend,GRSurfaceOrigin.BottomLeft,SKColorType.Rgba8888,SKAlphaType.Premul))
            {
                paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.White;
                canvas.DrawImage(image,new SKRect(0,size-height,width,size),new SKRect(0,0,width,height),new SKSamplingOptions(SKFilterMode.Nearest),paint);
                canvas.Flush();gpu!.Flush();
            }
        }
        // Present browser content above the authored terminal backdrop.
        BrowserSnapshots?.Draw(canvas,gpu!,scale);
        canvas.Flush();gpu!.Flush();
        if(owner._stationShipHint is {IsVisible:true,Raster:{} raster} hint)
        {
            stationHint.Use(gpu!,raster);canvas.Save();canvas.Scale((float)scale);
            paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.White;
            canvas.DrawImage(stationHint.Image!,Bounds(new(owner.StationHintPosition.X,owner.StationHintPosition.Y,hint.Width,hint.Height),scale),new SKSamplingOptions(SKFilterMode.Linear),paint);
            canvas.Restore();canvas.Flush();gpu!.Flush();
        }
        else stationHint.Dispose();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);
        gl.Disable(0x0c11);gl.DepthMask(1);
    }
}
