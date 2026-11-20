namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

internal sealed unsafe class SdlWorldCursor : IDisposable
{
    private readonly SDL_Cursor* previous=SDL_GetCursor();
    private readonly bool previousVisible=SDL_CursorVisible();
    private readonly Dictionary<int,SdlInventoryCursor.SurfaceCursor> cursors=[];
    private EnbRetailAssetBundle? assets;
    private readonly Dictionary<string,SdlInventoryCursor.SurfaceCursor> mapCursors=[];
    private SdlInventoryCursor.SurfaceCursor? preview;
    private EnbUiRaster? previewRaster;
    private EnbUiPoint previewHotspot;
    private bool restoring;
    internal Func<bool>? RestoreOverlay {get;set;}
    private bool disposed;
    internal int? Selected {get;private set;}
    internal bool Hidden {get;private set;}
    internal double Scale {get;private set;}=1;
    internal void SetScale(double value)
    {
        if(!double.IsFinite(value)||value<=0)throw new ArgumentOutOfRangeException(nameof(value));
        if(Scale==value)return;
        var selected=Selected; bool hidden=Hidden; Scale=value;
        NativePins.Check(SDL_SetCursor(previous),"release scaled cursor");
        foreach(int index in new[]{-1,99,100,101})
            if(cursors.Remove(index,out var old))old.Dispose();
        if(selected is int active)Select(active);else Restore();
        if(hidden)Hide();
    }
    internal static EnbUiRaster ScaleOrdinary(EnbUiRaster source,double scale)
    {
        if(scale==1)return source;
        int width=Math.Max(1,(int)Math.Round(source.Width*scale)),height=Math.Max(1,(int)Math.Round(source.Height*scale));
        var pixels=new byte[checked(width*height*4)];
        var original=source.BgraPixels.AsSpan();
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
            original.Slice((Math.Min(source.Height-1,y*source.Height/height)*source.Width+Math.Min(source.Width-1,x*source.Width/width))*4,4).CopyTo(pixels.AsSpan((y*width+x)*4,4));
        return new(width,height,System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsImmutableArray(pixels));
    }
    internal int Count=>cursors.Count+mapCursors.Count+(preview is null?0:1);
    internal SdlWorldCursor(EnbRetailAssetBundle assets)
    {NativePins.Check(SDL_SetHint("SDL_MOUSE_DPI_SCALE_CURSORS","0"),"physical cursor pixels");try{Ensure(assets);}catch{Dispose();throw;}}
    internal void Ensure(EnbRetailAssetBundle? next)
    {
        if(disposed||ReferenceEquals(assets,next))return;
        NativePins.Check(SDL_SetCursor(previous),"release world cursor");
        ReleaseMapCursors();ReleasePreview();foreach(var cursor in cursors.Values)cursor.Dispose();cursors.Clear();assets=next;Restore();
    }
    internal void Select(int index)
    {
        if(disposed)return;
        if(!cursors.TryGetValue(index,out var cursor))
        {
            if(assets?.RetailMixResources is not {} resources)return;
            string name;EnbUiPoint hotspot;
            if(index==-1){name="ARROW.dds";hotspot=new(3,1);}
            else if(index is 99 or 100 or 101){name=index==101?"c_terminal.tga":index==100?"c_talk.tga":"c_exit.tga";hotspot=new(10,0);}
            else
            {
                string direction=(index%7) switch {0=>"bl",1=>"l",2=>"tl",3=>"t",4=>"tr",5=>"r",_=>"br"};
                name=$"arr_{direction}{(index<7?3:4)}.tga";hotspot=new(0,index%7 is 0 or 6?32:16);
            }
            var image=new EnbRetailTextureResolver(resources).Resolve(name).Image;if(image is null)return;
            if(index is >=0 and <99)hotspot=new(hotspot.X,Math.Min(image.Height-1,hotspot.Y));
            var raster=EnbUiRaster.FromPixels(image.Width,image.Height,image.Pixels.Span);
            if(index is -1 or 99 or 100 or 101)
            {raster=ScaleOrdinary(raster,Scale);hotspot=new(Math.Round(hotspot.X*Scale),Math.Round(hotspot.Y*Scale));}
            cursor=new(raster,hotspot);cursors.Add(index,cursor);
        }
        NativePins.Check(SDL_SetCursor(cursor.Handle),"world cursor");NativePins.Check(SDL_ShowCursor(),"show world cursor");Selected=index;Hidden=false;
    }
    internal void Restore()
    {
        if(disposed)return;
        if(!restoring)
        {restoring=true;try{if(RestoreOverlay?.Invoke()==true)return;}finally{restoring=false;}}
        Select(-1);
        if(!cursors.ContainsKey(-1)){NativePins.Check(SDL_SetCursor(previous),"restore preceding cursor");NativePins.Check(SDL_ShowCursor(),"show preceding cursor");Selected=null;Hidden=false;}
    }
    internal bool Map(string resource,bool select)
    {
        if(!mapCursors.TryGetValue(resource,out var value))
        {
            if(assets?.RetailMixResources is not {} resources)return false;
            var image=new EnbRetailTextureResolver(resources).Resolve(resource).Image;if(image is null)return false;
            value=new(EnbUiRaster.FromPixels(image.Width,image.Height,image.Pixels.Span),new(0,0));mapCursors.Add(resource,value);
        }
        if(select){NativePins.Check(SDL_SetCursor(value.Handle),"map cursor");NativePins.Check(SDL_ShowCursor(),"show map cursor");Selected=null;Hidden=false;}
        return true;
    }
    internal void ReleaseMapCursors()
    {foreach(var value in mapCursors.Values){if(SDL_GetCursor()==value.Handle)Select(-1);value.Dispose();}mapCursors.Clear();}
    internal void Preview(EnbUiRaster raster,EnbUiPoint hotspot)
    {
        if(!ReferenceEquals(previewRaster,raster)||previewHotspot!=hotspot)
        {
            var next=new SdlInventoryCursor.SurfaceCursor(raster,hotspot);
            try{NativePins.Check(SDL_SetCursor(next.Handle),"preview cursor");}catch{next.Dispose();throw;}
            preview?.Dispose();preview=next;previewRaster=raster;previewHotspot=hotspot;
        }
        ShowPreview();
    }
    internal bool ShowPreview()
    {
        if(preview is null)return false;NativePins.Check(SDL_SetCursor(preview.Handle),"restore preview cursor");
        NativePins.Check(SDL_ShowCursor(),"show preview cursor");Selected=null;Hidden=false;return true;
    }
    internal void ReleasePreview()
    {if(preview is not null&&SDL_GetCursor()==preview.Handle)Select(-1);preview?.Dispose();preview=null;previewRaster=null;}
    internal void Hide(){if(disposed)return;NativePins.Check(SDL_HideCursor(),"hide world cursor");Hidden=true;}
    public void Dispose()
    {
        if(disposed)return;disposed=true;NativePins.Check(SDL_SetCursor(previous),"retire world cursor");
        NativePins.Check(previousVisible?SDL_ShowCursor():SDL_HideCursor(),"restore cursor visibility");
        ReleaseMapCursors();ReleasePreview();foreach(var cursor in cursors.Values)cursor.Dispose();cursors.Clear();assets=null;
    }
}
