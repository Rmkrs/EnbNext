namespace EnbNext.Engine;

internal sealed class EnbMovieOverlayRenderer
{
    private readonly EnbOpenGlSolidRenderer solid=new();
    private readonly EnbOpenGlBackdropRenderer[] text=[new(),new(),new()];
    public void Render(EnbGlApi gl,int framebuffer,int width,int height,double scale,EnbMovieUi owner)
    {
        solid.Render(gl,framebuffer,width,height,new(0,0,width,height),owner.FadeColor);
        float bar=(float)(owner.LetterboxHeight*scale);
        if(bar>0)
        {
            solid.Render(gl,framebuffer,width,height,new(0,0,width,bar),0xff000000);
            solid.Render(gl,framebuffer,width,height,new(0,height-bar,width,height),0xff000000);
        }
        double y=0;
        for(int i=0;i<text.Length;i++)
        {
            var label=owner.DebugLines[i];
            if(!label.IsVisible)continue;
            if(label.Raster is {} raster)
            {
                var renderer=text[i];if(!renderer.IsInitialized)renderer.Initialize(gl);
                int w=Math.Max(1,(int)Math.Round(label.Width*scale)),h=Math.Max(1,(int)Math.Round(label.Height*scale));
                renderer.RenderOverlay(gl,framebuffer,0,height-(int)Math.Round(y*scale)-h,w,h,raster);
            }
            y+=label.Height;
        }
    }
    public void Deinitialize(EnbGlApi? gl){solid.Deinitialize(gl);foreach(var renderer in text)renderer.Deinitialize(gl);}
}
