using EnbNext.Assets;
using EnbNext.Engine;
using SDL;

namespace EnbNext.Host.Sdl;

// The application owns the screen lifetime; the engine owns all presentation state.
internal sealed class SdlFrontendScreen : IDisposable
{
    private readonly SdlGameWindow window;
    private readonly SdlFrontendInput input;
    private bool disposed;
    internal EnbFrontendController Owner {get;}
    internal unsafe SdlFrontendScreen(SdlGameWindow window,SdlWorldCursor cursor,EnbRetailAssetBundle assets,
        uint? soundSettingsOwner,bool tutorialCompleted)
    {
        this.window=window;
        cursor.SetScale(window.Scale);cursor.Ensure(assets);
        Owner=new(assets,soundSettingsOwner,tutorialCompleted);
        try
        {
            input=new(Owner,window.Window);
            Resize();
            Owner._gpu.InitializeGl(window.Gl);
            Owner._creationTooltipGpu.InitializeGl(window.Gl);
        }
        catch{Dispose();throw;}
    }
    internal void Resize(){Owner.Bounds=new(0,0,window.Size.Width,window.Size.Height);Owner.RenderScaling=window.Scale;}
    internal void CancelPointer()=>input.Cancel();
    /// <summary>Presentation moves to another graphics card: the frontend scene and tooltip release their GL objects.</summary>
    internal void SuspendGpu(){Owner._creationTooltipGpu.SuspendGl(window.Gl);Owner._gpu.SuspendGl(window.Gl);}
    internal void ResumeGpu(){Owner._gpu.InitializeGl(window.Gl);Owner._creationTooltipGpu.InitializeGl(window.Gl);}
    internal bool Handle(SDL_Event e)=>input.Handle(e);
    internal void Tick(){Owner.UpdateFrame();input.Sync();}
    internal void Render(SdlUiGpuSurface surface)
    {
        Owner._gpu.RenderSurface(window.Gl,0,new(window.PixelWidth,window.PixelHeight,window.Scale));
        surface.Render(window.Gl,0,window.PixelWidth,window.PixelHeight,window.Scale,Owner);
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        input?.Dispose();Owner.Composition.Deinitialize();Owner.Dispose();
        Owner._creationTooltipGpu.DeinitializeGl(window.Gl);Owner._gpu.DeinitializeGl(window.Gl);
    }
}
