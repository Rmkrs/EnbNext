using EnbNext.Engine;
using EnbNext.Presentation;
using SkiaSharp;
namespace EnbNext.Host.Sdl;

internal interface ISdlBrowserLayer
{
    void Draw(SKCanvas canvas,GRContext gpu,double scale);
    void ReleaseGpu(bool contextLost);
}
// Browser pages and explicit Quit history share one UI-owned composition list.
internal sealed class SdlBrowserSnapshots : IDisposable
{
    private readonly List<ISdlBrowserLayer> layers=[];
    internal void Add(ISdlBrowserLayer layer)=>layers.Add(layer);
    internal void Remove(ISdlBrowserLayer layer)=>layers.Remove(layer);
    internal void Draw(SKCanvas canvas,GRContext gpu,double scale)
    {
        canvas.Save();canvas.Scale((float)scale);
        for(int i=0;i<layers.Count;i++)layers[i].Draw(canvas,gpu,scale);
        canvas.Restore();
    }
    internal IEnbOptionsBrowserSnapshot Capture(GRContext gpu,SKImage source,int width,int height)
    {
        using var target=SKSurface.Create(gpu,false,new SKImageInfo(width,height,SKColorType.Rgba8888,SKAlphaType.Premul))??throw new IOException("Browser GPU history allocation failed");
        target.Canvas.Clear(SKColors.Transparent);target.Canvas.DrawImage(source,0,0);target.Canvas.Flush();gpu.Flush();
        using var rendered=target.Snapshot();return new Snapshot(this,rendered.ToRasterImage());
    }
    // Diagnostic import for exact historical Quit-composition replay; live pages use Capture.
    internal IEnbOptionsBrowserSnapshot Create(Stream png)=>new Snapshot(this,SKImage.FromEncodedData(png)??throw new IOException("Invalid browser capture"));
    internal void ReleaseGpu(bool contextLost=false){foreach(var layer in layers)layer.ReleaseGpu(contextLost);}
    public void Dispose(){while(layers.Count>0){var layer=layers[^1];layers.RemoveAt(layers.Count-1);(layer as IDisposable)?.Dispose();}}
    private sealed class Snapshot(SdlBrowserSnapshots owner,SKImage image):IEnbOptionsBrowserSnapshot,ISdlBrowserLayer
    {
        private EnbUiRect bounds,occlusion;private bool attached,disposed;private SKImage? texture;private readonly SKPath clip=new();
        public void Attach(){if(disposed||attached)return;attached=true;owner.Add(this);}
        public void Arrange(EnbUiRect b,EnbUiRect o){if(bounds==b&&occlusion==o)return;bounds=b;occlusion=o;using var outer=new SKPath();using var inner=new SKPath();outer.AddRect(SKRect.Create(0,0,(float)b.Width,(float)b.Height));inner.AddRect(SKRect.Create((float)o.X,(float)o.Y,(float)o.Width,(float)o.Height));if(!outer.Op(inner,SKPathOp.Difference,clip))throw new IOException("Browser history clipping failed");}
        public void Draw(SKCanvas canvas,GRContext gpu,double scale)
        {
            if(disposed||bounds.Width<=0||bounds.Height<=0)return;
            canvas.Save();canvas.Translate((float)bounds.X,(float)bounds.Y);
            canvas.ClipPath(clip,SKClipOperation.Intersect,true);
            texture??=image.ToTextureImage(gpu)??throw new IOException("Browser history GPU upload failed");
            canvas.DrawImage(texture,SKRect.Create(0,0,(float)bounds.Width,(float)bounds.Height),new SKSamplingOptions(SKFilterMode.Linear));canvas.Restore();
        }
        public void ReleaseGpu(bool contextLost){texture?.Dispose();texture=null;}
        public void Dispose(){if(disposed)return;disposed=true;if(attached)owner.Remove(this);texture?.Dispose();image.Dispose();clip.Dispose();}
    }
}
