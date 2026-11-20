using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;

namespace EnbNext.App;

internal sealed class SdlOptionsBrowserSurface(Func<CefBrowserSurface> factory) : IEnbOptionsBrowser
{
    private CefBrowserSurface? surface;
    private readonly TaskCompletionSource<CefBrowserSurface> attached=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EnbUiRect bounds;
    private bool visible=true,disposed;
    public event Action<string>? Failed;
    public event Action? PageLoaded,CloseRequested,EscapeRequested;
    public bool IsVisible {get=>visible;set{visible=value;if(surface is {} child)child.IsVisible=value;}}
    public void Attach()
    {
        if(disposed||surface is not null)return;
        try
        {
            surface=factory();surface.Failed+=message=>Failed?.Invoke(message);surface.PageLoaded+=()=>PageLoaded?.Invoke();
            surface.CloseRequested+=()=>CloseRequested?.Invoke();surface.EscapeRequested+=()=>EscapeRequested?.Invoke();
            surface.Bounds=bounds;surface.IsVisible=visible;attached.TrySetResult(surface);
        }
        catch(Exception ex){attached.TrySetException(ex);throw;}
    }
    public void Arrange(EnbUiRect value){bounds=value;if(surface is {} child)child.Bounds=value;}
    public void Reload()=>surface?.Reload();
    public async Task<IEnbOptionsBrowserSnapshot> CaptureAsync()
    {
        var child=await attached.Task;return await child.CaptureAsync();
    }
    public void Dispose(){if(disposed)return;disposed=true;attached.TrySetCanceled();surface?.Dispose();surface=null;}
}
