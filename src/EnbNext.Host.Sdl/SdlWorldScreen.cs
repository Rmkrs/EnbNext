using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
using static SDL.SDL3;

namespace EnbNext.Host.Sdl;

// Application composition of the validated owners/transports. No scene copies.
internal sealed class SdlWorldScreen : IDisposable
{
    private readonly SdlGameWindow window;
    private readonly SdlWorldFlightPointer pointer;
    private readonly SdlWorldKeyboardTransport keyboard;
    private readonly SdlWorldUiInput input;
    private readonly SdlLayoutEditorInput layoutInput;
    private bool disposed;
    internal SdlWorldHost Host {get;}
    internal EnbWorldController Owner=>Host.Owner;
    internal unsafe SdlWorldScreen(SdlGameWindow window,SdlWorldCursor cursor,Func<EnbClient?> client, EnbNext.Client.Models.CharacterSummary character,
        Func<Uri,string,string,IEnbOptionsBrowser> browser,Func<Task> screenshot,Action fullscreen,Action<string> report)
    {
        this.window=window;
        var current=client()??throw new InvalidOperationException("The character session ended.");
        var assets=current.RetailAssets??throw new InvalidOperationException("Retail assets are unavailable.");
        cursor.SetScale(window.Scale);cursor.Ensure(assets);
        Host=new(()=>client()?.SamplePresentationSnapshot(),assets,()=>client()?.SelectedFixedSessionId,()=>client()?.CurrentClientTime,
            (target,stop)=>client()?.RequestTargetAsync(target,stop)??Task.FromResult(false),
            (target,verb,stop)=>client()?.ExecuteTargetVerbAsync(target,verb,stop)??Task.FromResult(false),
            browser,cursor.Ensure,point=>input?.WorldDropAllowed(point)==true,window.Size,window.Scale,
            flight=>client()?.UpdateFlightInputAsync(flight)??Task.FromResult(false),
            (ship,owner,route)=>client()?.RequestWarpAsync(ship,owner,route)??Task.FromResult(false),
            ()=>current.CreateRenderSceneSubscription());
        try
        {
            assets.CharacterInterface = EnbCharacterInterfaceSettings.Load(character.AccountId, character.AvatarId);
            Owner.Ui.Layout.OpenStore(EnbUiLayoutProfiles.DefaultPath, assets.CharacterInterface);
            Owner.FrameSnapshotAccessor=()=>client()?.SampleLivePresentationFrame();
            Owner.World.CollectNativeEventsDuringLoading=SDL_PumpEvents;
            Owner.Hud.CollectNativeEventsDuringLoading=SDL_PumpEvents;
            pointer=new(Host,assets,()=>client()?.SelectedFixedSessionId,window.Window,cursor,
                ()=>keyboard?.Station.OwnsCursor==true,()=>keyboard!.UpdateFlight());
            keyboard=new(Host,assets,()=>client()?.SelectedFixedSessionId,()=>client()?.SamplePresentationSnapshot(),window.Window,
                pointer.Release,pointer.Resolve,pointer.EndCamera,screenshot,fullscreen,cursor.Select,cursor.Restore,report,pointer);
            input=new(Host,keyboard,pointer,cursor,window.Window);
            layoutInput=new(Owner.Ui,input.Capture,keyboard.BeginLayoutEditing,window.Window);
            EnbWorldClientBinding.Bind(Owner,keyboard.Owner,client);
            EnbWorldClientBinding.BindShortcuts(Owner,r=>client()?.ActivateShortcutSkillAsync(r)??Task.FromResult(false),
                (r,ability)=>client()?.SelectShortcutSkillLevel(r,ability)??false,r=>client()?.ActivateShortcutEquipmentAsync(r)??Task.FromResult(false));
            Owner.Options.ManualEscapeRequested+=ManualEscape;

        }
        catch{Dispose();throw;}
    }
    private unsafe void ManualEscape(){keyboard.Owner.Keys.Add(SDL_Scancode.SDL_SCANCODE_ESCAPE);SDL_RaiseWindow(window.Window);}
    internal void Start()
    {
        // Entry audio transfers before device initialization, which creates the
        // ordinary world/HUD audio owners. Match native attach ordering.
        try{Owner.World.InitializeGl(window.Gl);Owner.Hud.InitializeGl(window.Gl);Host.Start();}
        catch{Dispose();throw;}
    }
    /// <summary>Presentation moves to another graphics card: the world and HUD release their GL objects and keep the scene.</summary>
    internal void SuspendGpu(){Owner.Hud.SuspendGl(window.Gl);Owner.World.SuspendGl(window.Gl);}
    internal void ResumeGpu(){Owner.World.InitializeGl(window.Gl);Owner.Hud.InitializeGl(window.Gl);}
    internal SdlPointerCapture Capture=>input.Capture;
    internal Action? DiagnosticCaptureLost {set=>input.DiagnosticCaptureLost=value;}
    internal void SuspendGameplay(){keyboard.SuspendGameplay();}
    internal Task<bool> Handle(SDL_Event e)
    {
        return layoutInput.Handle(e)?Task.FromResult(true):input.HandleAsync(e);
    }
    internal void Resize()=>Host.Resize(window.Size,window.Scale);
    internal void Tick(){if(Owner.Ui.Layout.Docked!=Owner.IsStationLayout){Owner.Ui.Editor.CancelDrag();input.Capture.Release(SdlPointerOwner.Layout);}Owner.Ui.Layout.SetContext(Owner.IsStationLayout,window.Size);Host.Tick();input.Sync();if(Owner.Ui.Layout.Editing)Owner.Ui.Editor.Refresh();layoutInput.Sync();}
    /// <summary>The last frame's world and interface render times (the session log names them in a slow frame).</summary>
    internal double LastWorldRenderMs{get;private set;}
    internal double LastInterfaceRenderMs{get;private set;}
    internal void Render(SdlUiGpuSurface surface)
    {
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        Owner.World.RenderSurface(window.Gl,0,new(window.PixelWidth,window.PixelHeight,window.Scale));
        long world=System.Diagnostics.Stopwatch.GetTimestamp();
        surface.RenderWorldUi(window.Gl,0,window.PixelWidth,window.PixelHeight,window.Scale,Owner);
        LastWorldRenderMs=System.Diagnostics.Stopwatch.GetElapsedTime(started,world).TotalMilliseconds;
        LastInterfaceRenderMs=System.Diagnostics.Stopwatch.GetElapsedTime(world).TotalMilliseconds;
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        Owner.Options.ManualEscapeRequested-=ManualEscape;
        if(Owner.Ui.Layout.Editing)Owner.Ui.Editor.Cancel();layoutInput?.Sync();
        input?.Dispose();keyboard?.Dispose();pointer?.Dispose();Host.Dispose();
        Owner.Hud.DeinitializeGl(window.Gl);Owner.World.DeinitializeGl(window.Gl);
    }
}
