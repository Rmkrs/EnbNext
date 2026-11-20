using SDL;
using static SDL.SDL3;

namespace EnbNext.Host.Sdl;

internal readonly record struct SdlScreenRect(int X,int Y,int Width,int Height)
{
    internal int Right=>X+Width;
    internal int Bottom=>Y+Height;
    internal bool Contains(int x,int y)=>x>=X&&x<Right&&y>=Y&&y<Bottom;
}
internal sealed record SdlScreen(SDL_DisplayID Id,SdlScreenRect Bounds,SdlScreenRect WorkingArea,int PixelWidth,int PixelHeight,double Scale,double Density);
internal static unsafe class SdlDisplays
{
    internal static SdlScreen Read(SDL_DisplayID id)
    {
        SDL_Rect bounds,work;NativePins.Check(SDL_GetDisplayBounds(id,&bounds),"display bounds");NativePins.Check(SDL_GetDisplayUsableBounds(id,&work),"display work area");
        var mode=SDL_GetDesktopDisplayMode(id);if(mode==null)throw new InvalidOperationException(SDL_GetError());
        double density=bounds.w>0?(double)mode->w/bounds.w:1;
        return new(id,new(bounds.x,bounds.y,bounds.w,bounds.h),new(work.x,work.y,work.w,work.h),mode->w,mode->h,SDL_GetDisplayContentScale(id)*density,density);
    }
    internal static SdlScreen[] All()
    {
        int count;var ids=SDL_GetDisplays(&count);if(ids==null)throw new InvalidOperationException(SDL_GetError());
        try{var screens=new SdlScreen[count];for(int i=0;i<count;i++)screens[i]=Read(ids[i]);return screens;}
        finally{SDL_free(ids);}
    }
    internal static IReadOnlyList<EnbNext.Presentation.EnbDisplayChoice> Choices(SdlGameWindow window,EnbNext.Presentation.EnbDisplayChoice current)
    {
        var screen=Current(window);
        int count;var modes=SDL_GetFullscreenDisplayModes(screen.Id,&count);
        if(modes==null)throw new InvalidOperationException(SDL_GetError());
        var sizes=new SortedSet<(int Width,int Height)>();
        try
        {
            for(int i=0;i<count;i++)
            {
                int width=modes[i]->w,height=modes[i]->h;
                if(width>=1024&&height>=768&&width<=screen.PixelWidth&&height<=screen.PixelHeight&&
                    (long)width*screen.PixelHeight==(long)height*screen.PixelWidth)
                    sizes.Add((width,height));
            }
        }
        finally{SDL_free(modes);}
        sizes.Add((1024,768));sizes.Add((screen.PixelWidth,screen.PixelHeight));
        if(current.Mode==EnbNext.Presentation.EnbDisplayMode.Windowed)sizes.Add((current.Width,current.Height));
        var choices=new List<EnbNext.Presentation.EnbDisplayChoice>{new(screen.PixelWidth,screen.PixelHeight,EnbNext.Presentation.EnbDisplayMode.BorderlessFullscreen)};
        foreach(var (width,height) in sizes)choices.Add(new(width,height,EnbNext.Presentation.EnbDisplayMode.Windowed));
        choices.Sort((a,b)=>{int order=b.Width.CompareTo(a.Width);if(order==0)order=b.Height.CompareTo(a.Height);return order!=0?order:b.Mode.CompareTo(a.Mode);});
        return choices;
    }
    internal static SdlScreen Primary()=>Read(SDL_GetPrimaryDisplay());
    internal static SdlScreen Current(SdlGameWindow window)
    {
        int x,y,width,height;
        NativePins.Check(SDL_GetWindowPosition(window.Window,&x,&y),"window position");
        NativePins.Check(SDL_GetWindowSize(window.Window,&width,&height),"window size");
        var extent=new SdlScreenRect(x,y,width,height);
        var selected=Read(SDL_GetDisplayForWindow(window.Window));long largest=Overlap(selected.Bounds);
        foreach(var screen in All()){long overlap=Overlap(screen.Bounds);if(overlap>largest){selected=screen;largest=overlap;}}
        return selected;
        long Overlap(SdlScreenRect bounds)=>Math.Max(0L,Math.Min((long)extent.X+extent.Width,bounds.Right)-Math.Max(extent.X,bounds.X))*
            Math.Max(0L,Math.Min((long)extent.Y+extent.Height,bounds.Bottom)-Math.Max(extent.Y,bounds.Y));
    }
}
