using EnbNext.Engine;
using EnbNext.Rendering;
using SDL;

namespace EnbNext.Host.Sdl;

internal sealed class SdlVideoScreen : IDisposable
{
    private readonly SdlGameWindow window;
    private readonly EnbRetailVideoRuntime runtime;
    private readonly EnbVideoRenderer renderer;
    private readonly Action cancelDecode;
    private readonly TaskCompletionSource completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool busy,disposed;
    internal Task Completion=>completion.Task;
    internal bool HasFrame {get;private set;}
    internal Action<string>? Diagnostic {get;set;}
    internal SdlVideoScreen(SdlGameWindow window,EnbRetailVideoRuntime runtime,Func<IEnbVideoFont> font,Action cancelDecode)
    {
        this.window=window;this.runtime=runtime;this.cancelDecode=cancelDecode;renderer=new(font);
        try{renderer.InitializeGl(window.Gl);}catch{Dispose();throw;}
    }
    internal async Task Handle(SDL_Event e)
    {
        if(disposed || (SDL_EventType)e.type is not (SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_WHEEL))return;
        uint now=unchecked((uint)Environment.TickCount64);
        await Task.Run(()=>runtime.Input(1,0,now));
        await Tick();
    }
    internal async Task Tick()
    {
        if(disposed||busy||window.Size.Width<=0||window.Size.Height<=0)return;
        busy=true;
        try
        {
            uint now=unchecked((uint)Environment.TickCount64);
            await Task.Run(()=>runtime.Tick(now));
            if(disposed)return;
            if(renderer.Failure is {} failure){Diagnostic?.Invoke("Video GPU: "+failure);runtime.Dispose();}
            if(runtime.Ended)
            {
                renderer.SetFrame(null,null);
                if(runtime.Failure is {} error)Diagnostic?.Invoke("Video playback: "+error);
                completion.TrySetResult();return;
            }
            renderer.SetFrame(runtime.Frame,runtime.Subtitles.Displayed);
            HasFrame=runtime.Frame?.Rgb565 is not null;
        }
        finally{busy=false;}
    }
    internal void Render()=>renderer.RenderGl(window.Gl,0,window.PixelWidth,window.PixelHeight);
    public void Dispose()
    {
        if(disposed)return;disposed=true;cancelDecode();runtime.Dispose();renderer.SetFrame(null,null);
        renderer.DeinitializeGl(window.Gl);completion.TrySetResult();
    }
}
