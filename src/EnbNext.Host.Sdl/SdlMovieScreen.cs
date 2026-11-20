using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Rendering;
using SDL;

namespace EnbNext.Host.Sdl;

internal sealed class SdlMovieScreen : IDisposable
{
    private readonly SdlGameWindow window;
    private readonly EnbMovieOverlayRenderer overlay=new();
    private bool disposed;
    internal EnbMovieUi Owner {get;}
    internal SdlMovieScreen(SdlGameWindow window,EnbRetailAssetBundle assets,EnbRetailMovieScript script,bool enableAudio=true)
    {
        this.window=window;Owner=new(assets,script,()=> (window.Size,window.Scale),enableAudio);
        try{Owner.Renderer.InitializeGl(window.Gl);}catch{Dispose();throw;}
    }
    internal void Handle(SDL_Event e)
    {
        if((SDL_EventType)e.type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)
            Owner.MovePointer(new(e.motion.x,e.motion.y),window.Size);
        if((SDL_EventType)e.type!=SDL_EventType.SDL_EVENT_KEY_DOWN)return;
        uint key=e.key.scancode switch
        {
            SDL_Scancode.SDL_SCANCODE_ESCAPE=>0x1b,
            SDL_Scancode.SDL_SCANCODE_PAGEDOWN=>0x22,
            SDL_Scancode.SDL_SCANCODE_SPACE=>0x20,
            SDL_Scancode.SDL_SCANCODE_KP_PLUS=>0x6b,
            _=>0,
        };
        if(key!=0)Owner.Runtime.Input(key);
    }
    internal Task Tick()=>Owner.Pump();
    internal void Render()
    {
        Owner.Renderer.RenderSurface(window.Gl,0,new(window.PixelWidth,window.PixelHeight,window.Scale));
        overlay.Render(window.Gl,0,window.PixelWidth,window.PixelHeight,window.Scale,Owner);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;Owner.Dispose();overlay.Deinitialize(window.Gl);Owner.Renderer.DeinitializeGl(window.Gl);
    }
}
