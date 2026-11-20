namespace EnbNext.Host.Sdl;

using System.Runtime.InteropServices;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

internal sealed unsafe class SdlInventoryCursor : IDisposable
{
    private readonly EnbInventoryUi owner;
    private readonly SDL_Window* window;
    private readonly SDL_Cursor* previous;
    private readonly bool previousVisible;
    private readonly SurfaceCursor game;
    private SurfaceCursor? preview;
    private EnbUiRaster? previewRaster;
    private EnbUiPoint previewHotspot;
    internal bool IsGameCursor=>SDL_GetCursor()==game.Handle;
    internal bool IsPreviewCursor=>preview is not null&&SDL_GetCursor()==preview.Handle;
    internal bool HasPreview=>preview is not null;
    internal bool Hidden {get;private set;}
    internal int PreviewCreations {get;private set;}
    internal int WarpRequests {get;private set;}
    internal SdlInventoryCursor(EnbInventoryUi owner,SDL_Window* window,EnbUiRaster raster,EnbUiPoint hotspot)
    {
        this.owner=owner;this.window=window;previous=SDL_GetCursor();previousVisible=SDL_CursorVisible();
        NativePins.Check(SDL_SetHint("SDL_MOUSE_DPI_SCALE_CURSORS","0"),"physical cursor pixels");
        game=new(raster,hotspot);
        try{Restore();}catch{SDL_SetCursor(previous);game.Dispose();throw;}
        owner.PreviewCursorRequested+=Preview;owner.HideCursorRequested+=Hide;
        owner.RestoreCursorRequested+=Restore;owner.AssetsReleased+=ReleasePreview;
        owner.EquipmentPointerWarpRequested=Warp;
    }
    private void Warp(EnbUiPoint p)
    {
        WarpRequests++;
        if(((ulong)SDL_GetWindowFlags(window)&SDL_WINDOW_INPUT_FOCUS)!=0)SDL_WarpMouseInWindow(window,(float)p.X,(float)p.Y);
    }
    private void Hide(){NativePins.Check(SDL_HideCursor(),"hide inventory cursor");Hidden=true;}
    private void Restore(){NativePins.Check(SDL_SetCursor(game.Handle),"restore inventory cursor");NativePins.Check(SDL_ShowCursor(),"show inventory cursor");Hidden=false;}
    private void Preview(EnbUiRaster raster,EnbUiPoint hotspot)
    {
        if(!ReferenceEquals(previewRaster,raster)||previewHotspot!=hotspot)
        {
            var next=new SurfaceCursor(raster,hotspot);
            try {NativePins.Check(SDL_SetCursor(next.Handle),"preview inventory cursor");}
            catch {next.Dispose();throw;}
            preview?.Dispose();preview=next;previewRaster=raster;previewHotspot=hotspot;PreviewCreations++;
        }
        else NativePins.Check(SDL_SetCursor(preview!.Handle),"reuse inventory cursor");
        NativePins.Check(SDL_ShowCursor(),"show inventory preview cursor");Hidden=false;
    }
    private void ReleasePreview(){Restore();preview?.Dispose();preview=null;previewRaster=null;}
    public void Dispose()
    {
        owner.PreviewCursorRequested-=Preview;owner.HideCursorRequested-=Hide;
        owner.RestoreCursorRequested-=Restore;owner.AssetsReleased-=ReleasePreview;owner.EquipmentPointerWarpRequested=null;
        NativePins.Check(SDL_SetCursor(previous),"restore preceding cursor");
        NativePins.Check(previousVisible?SDL_ShowCursor():SDL_HideCursor(),"restore preceding cursor visibility");
        preview?.Dispose();preview=null;game.Dispose();
    }
    internal sealed class SurfaceCursor : IDisposable
    {
        private byte* pixels;
        private SDL_Surface* surface;
        internal SDL_Cursor* Handle {get;private set;}
        internal SurfaceCursor(EnbUiRaster raster,EnbUiPoint hotspot)
        {
            if(!raster.IsValid)throw new ArgumentException("Cursor raster is invalid",nameof(raster));
            try
            {
                pixels=(byte*)NativeMemory.Alloc((nuint)raster.BgraPixels.Length);
                if(pixels==null)throw new OutOfMemoryException();
                raster.BgraPixels.AsSpan().CopyTo(new Span<byte>(pixels,raster.BgraPixels.Length));
                surface=SDL_CreateSurfaceFrom(raster.Width,raster.Height,SDL_PIXELFORMAT_BGRA32,(nint)pixels,raster.Width*4);
                if(surface==null)throw new InvalidOperationException(SDL_GetError());
                Handle=SDL_CreateColorCursor(surface,(int)hotspot.X,(int)hotspot.Y);
                if(Handle==null)throw new InvalidOperationException(SDL_GetError());
            }
            catch{Dispose();throw;}
        }
        public void Dispose()
        {
            if(Handle!=null){SDL_DestroyCursor(Handle);Handle=null;}
            if(surface!=null){SDL_DestroySurface(surface);surface=null;}
            if(pixels!=null){NativeMemory.Free(pixels);pixels=null;}
        }
    }
}
