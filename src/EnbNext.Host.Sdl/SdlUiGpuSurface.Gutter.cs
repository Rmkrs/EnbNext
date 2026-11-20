namespace EnbNext.Host.Sdl;

using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering.OpenGL;
using SkiaSharp;
using static EnbNext.Engine.EnbGutterUi;

internal sealed unsafe partial class SdlUiGpuSurface
{
    private EnbGutterUi? gutter;
    private readonly List<GutterBinding> gutterBindings=[];
    private readonly ImageSlot[] gutterCorners=Enumerable.Range(0,8).Select(_=>new ImageSlot()).ToArray();
    private readonly ImageSlot[] gutterHoverText=Enumerable.Range(0,3).Select(_=>new ImageSlot()).ToArray();
    private readonly SKPath gutterPath=new(){FillType=SKPathFillType.EvenOdd};
    private sealed class GutterBinding(Marker marker)
    {
        internal readonly Marker Marker=marker;
        internal readonly ImageSlot Icon=new();
        internal readonly ImageSlot[] Text=[new(),new(),new()];
        internal Action? Retire;
        internal void Release(){Icon.Dispose();foreach(var slot in Text)slot.Dispose();}
    }
    internal void AttachGutter(EnbGutterUi owner)
    {
        if(ReferenceEquals(gutter,owner))return;
        DetachGutter();gutter=owner;owner.MarkerCreated+=AddGutterMarker;owner.AssetsReleased+=ReleaseGutterUploads;
        // Markers the gutter made before this surface attached (the world's first update) are drawn too.
        foreach(var marker in owner.Markers)AddGutterMarker(marker);
    }
    private void AddGutterMarker(Marker marker)
    {
        var binding=new GutterBinding(marker);
        binding.Retire=()=>{marker.Retiring-=binding.Retire;binding.Release();gutterBindings.Remove(binding);};
        marker.Retiring+=binding.Retire;gutterBindings.Add(binding);
    }
    private void ReleaseGutterUploads()
    {
        foreach(var binding in gutterBindings)binding.Release();
        foreach(var slot in gutterCorners)slot.Dispose();foreach(var slot in gutterHoverText)slot.Dispose();
    }
    private void DetachGutter()
    {
        if(gutter is not null){gutter.MarkerCreated-=AddGutterMarker;gutter.AssetsReleased-=ReleaseGutterUploads;}
        foreach(var binding in gutterBindings)binding.Marker.Retiring-=binding.Retire;
        ReleaseGutterUploads();gutterBindings.Clear();gutter=null;
    }
    internal int GutterDeviceBindings=>gutterBindings.Count;
    internal void RenderGutter(EnbGlApi gl,int framebuffer,int width,int height,double scale)
    {
        if(gutter is not {} owner)return;
        var canvas=Begin(gl,framebuffer,width,height);canvas.Save();canvas.Scale((float)scale);
        canvas.ClipRect(SKRect.Create(0,0,(float)(width/scale),(float)(height/scale)));
        var frame=owner.Frame;
        if(frame.IsVisible&&!owner.RailHidden&&frame.Geometry is {} geometry)
        {
            var r=Rect(frame);float ix=(float)Math.Clamp(geometry.DesignInset*r.Width/EnbGutterFrameGeometry.DesignWidth,0,r.Width*.5),iy=(float)Math.Clamp(geometry.DesignInset*r.Height/EnbGutterFrameGeometry.DesignHeight,0,r.Height*.5);
            gutterPath.Reset();gutterPath.FillType=SKPathFillType.EvenOdd;gutterPath.AddRect(r);gutterPath.AddRect(new(r.Left+ix,r.Top+iy,r.Right-ix,r.Bottom-iy));
            float opacity=float.IsFinite(frame.Opacity)?frame.Opacity:.75f;
            paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.Black.WithAlpha((byte)Math.Clamp((int)MathF.Round(Math.Clamp(opacity,0,1)*255),0,255));canvas.DrawPath(gutterPath,paint);
        }
        foreach(var binding in gutterBindings)
        {
            var marker=binding.Marker;if(!marker.HitTarget.IsVisible||marker.IconHidden||marker.Image.Source is null)continue;
            var hit=Rect(marker.HitTarget);double iw=marker.Image.Width,ih=marker.Image.Height;
            var r=Bounds(new(hit.Left+(hit.Width-iw)*.5,hit.Top+(hit.Height-ih)*.5,iw,ih),scale);
            float fit=Math.Min(r.Width/marker.Image.Source.Width,r.Height/marker.Image.Source.Height);
            float fittedWidth=marker.Image.Source.Width*fit,fittedHeight=marker.Image.Source.Height*fit;
            r=SKRect.Create(hit.Left+(hit.Width-fittedWidth)*.5f,
                hit.Top+(hit.Height-fittedHeight)*.5f,fittedWidth,fittedHeight);
            Draw(binding.Icon,marker.Image.Source,r,marker.Image.Opacity);
        }
        foreach(var binding in gutterBindings)Label(binding.Marker.PersistentLabel,binding.Text);
        Reticle(owner.SelectedReticle,0);Reticle(owner.HoverReticle,4);Label(owner.HoverLabel,gutterHoverText);
        canvas.Restore();canvas.Flush();gpu!.Flush();
        for(uint unit=0;unit<textureUnits;unit++)bindSampler(unit,0);
        gl.Disable(0x0c11);gl.DepthMask(1);
        SKRect Rect(GutterElement e)=>Bounds(new(e.X,e.Y,e.Width,e.Height),scale);
        void Draw(ImageSlot slot,EnbUiRaster raster,SKRect r,double opacity)
        {
            slot.Use(gpu!,raster);paint.Style=SKPaintStyle.Fill;paint.Color=SKColors.White.WithAlpha((byte)(255*opacity));
            canvas.DrawImage(slot.Image!,r,new SKSamplingOptions(SKFilterMode.Linear),paint);
        }
        void Reticle(GutterReticle r,int index)
        {
            if(!r.IsVisible)return;
            Corner(r.UpperLeft,index);Corner(r.UpperRight,index+1);Corner(r.BottomRight,index+2);Corner(r.BottomLeft,index+3);
        }
        void Corner(GutterImage image,int index){if(image.IsVisible&&image.Source is {} source)Draw(gutterCorners[index],source,Rect(image),1);}
        void Label(GutterLabel label,ImageSlot[] slots)
        {
            if(!label.IsVisible)return;
            double x=label.X,y=label.Y,w=label.Width;int index=0;
            // An attacker's label (Modern UI): its name line (level and name) grows and shrinks a little about its own
            // centre; the lines under it keep their size and place.
            float grow=label.Scale is > 1.001f and < 2 ? label.Scale : 1;
            Line(label.First);Line(label.Second);if(label.Third is {} third)Line(third);
            void Line(GutterLine line)
            {
                var slot=slots[index++];if(!line.IsVisible||line.Raster is not {} raster)return;
                double lw=line.Width,lh=line.Height,lx=x+(line.AlignRight?w-lw:0),ly=y;
                if(grow!=1&&ReferenceEquals(line,label.First)){lx-=lw*(grow-1)/2;ly-=lh*(grow-1)/2;lw*=grow;lh*=grow;}
                var r=Bounds(new(lx,ly,lw,lh),scale);
                // Next always-on labels follow moving objects: on whole pixels the small retail font keeps its
                // shape instead of re-blurring at every fractional position (seen as shimmering text).
                if(owner.AlwaysShowLabels){float s=(float)scale;r=SKRect.Create(MathF.Round(r.Left*s)/s,MathF.Round(r.Top*s)/s,r.Width,r.Height);}
                Draw(slot,raster,r,line.Opacity);y+=line.Height+1;
            }
        }
    }
}
