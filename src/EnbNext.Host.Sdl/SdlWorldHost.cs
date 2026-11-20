namespace EnbNext.Host.Sdl;

using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Net7.Gameplay;
using SDL;

// SDL loop services only; all scene/UI state and transitions live in Engine.
internal sealed class SdlWorldHost : IEnbWorldHost, IDisposable
{
    private Action? nextFrame;
    private readonly Queue<Action> posted=new();
    private TaskCompletionSource? presentation;
    private readonly Func<Uri,string,string,IEnbOptionsBrowser> browser;
    private readonly Action<EnbRetailAssetBundle?> cursor;
    private readonly Func<EnbUiPoint,bool> worldDrop;
    private bool stopped;
    private readonly Func<IEnbSceneFrame?> scene;
    private readonly Func<uint,ulong,IReadOnlyList<uint>,Task<bool>>? warp;
    internal EnbWorldController Owner {get;}
    internal EnbWorldFlightCommands<SDL_Scancode> FlightCommands {get;}
    internal event Action? FlightCaptureReleased;
    public EnbUiSize Size {get;private set;}
    public double Scale {get;private set;}
    public bool IssuePanelOpen {get;set;}
    internal Func<EnbUiPoint,bool>? IssueHitTest {get;set;}
    internal Action<bool,LivePerformanceSnapshot?>? PerformanceChanged {get;set;}
    internal long Presents {get;private set;}
    internal SdlWorldHost(Func<IEnbSceneFrame?> scene,EnbRetailAssetBundle assets,Func<uint?> session,Func<uint?> clock,
        Func<uint,CancellationToken,Task<bool>> target,Func<uint,uint,CancellationToken,Task<bool>> verb,
        Func<Uri,string,string,IEnbOptionsBrowser> browser,Action<EnbRetailAssetBundle?> cursor,Func<EnbUiPoint,bool> worldDrop,
        EnbUiSize size,double scale,Func<Net7FlightInput,Task<bool>>? flight=null,
        Func<uint,ulong,IReadOnlyList<uint>,Task<bool>>? warp=null,Func<EnbRenderSceneSubscription>? renderSceneSubscriptionFactory=null)
    {
        this.scene=scene;this.warp=warp;this.browser=browser;this.cursor=cursor;this.worldDrop=worldDrop;Size=size;Scale=scale;
        Owner=new(this,scene,()=>assets.RetailMixResources,()=>assets.BaseAssetResolver,()=>assets,session,clock,target,verb,renderSceneSubscriptionFactory);
        FlightCommands=new(flight,()=>FlightCaptureReleased?.Invoke(),Owner.Ui.Cockpit.SetThrust);
        Owner.Ui.Cockpit.WarpRequested+=async()=>await RequestWarpAsync();
        // Next Modern Local: an approach too far to follow warps to its target (never stops a warp under way).
        Owner.Ui.LocalWarpRequested=()=>FlightCommands.RequestWarpAsync(scene(),Owner.CanUseCockpit,warp);
    }
    internal Task RequestWarpAsync()
    {
        if(Owner.CanUseCockpit && Owner.Cockpit.WarpEngaged)Owner.Autopilot?.Cancel("Manual warp stop");
        return FlightCommands.RequestWarpAsync(scene(),Owner.CanUseCockpit,warp);
    }
    internal void Start(){stopped=false;Owner.Start();}
    internal void Tick()
    {
        if(stopped)return;
        while(posted.TryDequeue(out var action))action();
        var frame=nextFrame;nextFrame=null;frame?.Invoke();
    }
    internal void Resize(EnbUiSize size,double scale,Func<Net7FlightInput,Task<bool>>? flight=null,
        Func<uint,ulong,IReadOnlyList<uint>,Task<bool>>? warp=null){Size=size;Scale=scale;Owner.QueueResizeRefresh();}
    // Called after SDL_GL_SwapWindow succeeds, never merely after GPU drawing.
    internal void Presented(){Presents++;var pending=presentation;presentation=null;pending?.SetResult();}
    public Task WaitForPresentationAsync()=>(presentation??=new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    public void RequestAnimationFrame(Action frame)=>nextFrame=frame;
    public void PostRefresh(Action refresh)=>posted.Enqueue(refresh);
    public bool IssueContains(EnbUiPoint point)=>IssueHitTest?.Invoke(point)??false;
    public bool WorldDropAllowed(EnbUiPoint point)=>worldDrop(point);
    public IEnbOptionsBrowser CreateOptionsBrowser(Uri uri,string profile,string query)=>browser(uri,profile,query);
    public void EnsureCursor(EnbRetailAssetBundle? assets)=>cursor(assets);
    public void PresentationChanged(EnbWorldController owner) { }
    public void UpdatePerformance(bool visible,LivePerformanceSnapshot? snapshot)=>PerformanceChanged?.Invoke(visible,snapshot);
    public void ReleaseUi()
    {
        Owner._stationShipHint.Dispose();Owner.Ui.Cockpit.Dispose();Owner.Ui.Options.Dispose();
        Owner.Ui.Inventory.Dispose();Owner.Ui.Chat.Dispose();Owner.Ui.Map.Dispose();Owner.Ui.GalaxyMap.Dispose();
    }
    public void Dispose()
    {
        if(stopped)return;stopped=true;nextFrame=null;posted.Clear();
        Owner.Stop();presentation?.TrySetCanceled();presentation=null;
    }
}
