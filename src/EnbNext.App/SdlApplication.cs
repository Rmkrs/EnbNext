using System.Diagnostics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using EnbNext.Rendering;
using SDL;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private readonly EnbUiThreadContext dispatch=new();
    private readonly SdlGameWindow window;
    private readonly bool hidden;
    private Task startupTask=Task.CompletedTask;
    private readonly Action<SdlApplication>? inspect;
    private readonly SdlUiGpuSurface surface=new();
    private readonly SdlBrowserSnapshots browserSnapshots=new();
    private readonly CefBrowserService browserService;
    private SdlWorldCursor? cursor;
    private SdlFrontendScreen? frontendScreen;
    private SdlWorldScreen? worldScreen;
    private SdlMovieScreen? movieScreen;
    private SdlVideoScreen? videoScreen;
    private EnbFrontendController? _frontend=>frontendScreen?.Owner;
    private EnbFrontendController.SessionState? _frontendSession;
    private string? _frontendUsername;
    private EnbClient? _client;
    private CharacterSummary? _selectedCharacter;
    private StartupLogin? _startupLogin;
    private bool _busy,_frontendClosed,_shortcutActive,windowShown;
    private readonly bool parameterLaunch;
    private readonly string? MovieCapturePrefix;
    private readonly LiveIssueLog _runtimeIssues=new(DiagnosticFiles.Directory);
    private readonly SessionDiagnosticLog _sessionLog=new(DiagnosticFiles.Directory);
    private readonly object _diagnosticsSync=new();
    private readonly StringBuilder _diagnostics=new();
    private const int MaxDiagnosticTranscriptCharacters=256*1024;
    private int CurrentFrameRate=>_benchmark is not null&&hidden?60:EnbFrameRatePolicy.Resolve(worldScreen is not null,window.Focused,_benchmark is null?diagnosticForeground:1000,Math.Clamp(diagnosticBackground??_preferences.BackgroundFpsLimit,0,1000));
    private SdlApplication(string[] args,bool hidden,Action<SdlApplication>? inspect)
    {
        this.hidden=hidden;this.inspect=inspect;
        var capture=StartupMovieCapture.Parse(args);MovieCapturePrefix=capture.Prefix;_startupLogin=StartupLogin.Parse(capture.Remaining);
        parameterLaunch=_startupLogin is not null;
        ReadPreferences();ReadStartupGroupDisplay();
        try
        {
            window=CreateWindow(ActiveDisplayPreferences.DisplayWidth,ActiveDisplayPreferences.DisplayHeight,_preferences.GraphicsAdapter,out activeGraphicsAdapter);
            surface.BrowserSnapshots=browserSnapshots;
            browserService=new(window,browserSnapshots,Post);
            InitializePreferences();InitializeDiagnostics();
            EnbNext.Engine.EnbWorldRenderer.ShipDamageDiagnostic=message=>Post(()=>AppendDiagnostic(message));
            LiveWorldOpenAlAudio.SetFocusMuted(_preferences.QualityOfLife?.MuteBackgroundAudio==true && !window.Focused);
            AppendDiagnostic("SDL presentation: "+window.ContextDescription);
        }
        catch{window?.Dispose();_runtimeIssues.Flush();_sessionLog.Dispose();throw;}
    }
    internal static int Run(string[] args,bool hidden=false,Action<SdlApplication>? inspect=null)
    {
        var previous=SynchronizationContext.Current;
        SdlApplication? app=null;
        try
        {
            // Construction of presentation owners occurs only after installing this context.
            app=new(args,hidden,inspect);SynchronizationContext.SetSynchronizationContext(app.dispatch);
            app.dispatch.Observe(app.startupTask=app.StartBenchmarkOrFrontendAsync());
            app.Loop();return 0;
        }
        // The player's client: a failure to start or to shut down ends with a message and a log, never an
        // unhandled exception. Probe hosts (inspect) keep the exception as their result.
        catch(Exception ex) when(inspect is null)
        {ReportFatal("EnbNext could not continue",ex);return 1;}
        finally
        {
            try{if(app is not null)app.Shutdown();}
            catch(Exception ex) when(inspect is null){ReportFatal("EnbNext shutdown",ex,quiet:true);}
            finally{SynchronizationContext.SetSynchronizationContext(previous);}
        }
    }
    private static unsafe void ReportFatal(string title,Exception ex,bool quiet=false)
    {
        // A crash report is written only with --logs; otherwise the message box and the console carry it.
        string? path=null;
        if(DiagnosticFiles.Directory is {} directory)
        {
            path=Path.Combine(directory,$"fatal-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            try{Directory.CreateDirectory(directory);File.WriteAllText(path,ex.ToString());}catch{path="(log could not be written)";}
        }
        try{Console.Error.WriteLine(title+": "+ex);}catch{}
        if(!quiet)try{SDL.SDL3.SDL_ShowSimpleMessageBox(SDL.SDL_MessageBoxFlags.SDL_MESSAGEBOX_ERROR,title,ex.Message+(path is null?"":"\n\nDetails: "+path),null);}catch{}
    }
    private void Post(Action action)=>dispatch.Post(_=>action(),null);
    private void Loop()
    {
        long nextUi=0,nextMovie=0,nextDiagnostics=0;
        while(!_frontendClosed)
        {
            long begin=Stopwatch.GetTimestamp();long handled=begin,drained=begin,ticked=begin,rendered=begin;
            // One frame is the fault boundary: a failure in event handling, queued async work, ticking or
            // rendering is logged and contained (FaultRecovery), never allowed to end the process.
            try
            {
                while(window.Poll(out var raw))
                {
                    var e=window.UiEvent(raw);
                    var type=(SDL_EventType)e.type;
                    if(type is SDL_EventType.SDL_EVENT_QUIT or SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
                    {
                        // Native close asks for confirmation only on the displayed
                        // frontend, matching the existing window-close handler.
                        if(videoScreen is null&&movieScreen is null&&worldScreen is null&&_frontend is {} front)dispatch.Observe(front.ConfirmQuitAsync());
                        else _frontendClosed=true;
                        continue;
                    }
                    HandleDisplayEvent(type);
                    LiveWorldOpenAlAudio.SetFocusMuted(_preferences.QualityOfLife?.MuteBackgroundAudio==true && !window.Focused);
                    if(videoScreen is null&&movieScreen is null&&HandleDiagnostics(e))continue;
                    // Next panels take no input while the interface is hidden.
                    if(worldScreen?.Owner.InterfaceHidden!=true&&(HandleMapNoteEditor(e)||HandleModernMenu(e)||HandleFocusThumbnail(e)||HandleMultiboxMenu(e)||HandleWaypointArrow(e)||HandleMissionTracker(e)||HandleModernHud(e)||HandleDamagePanel(e)||HandleGroupPanel(e)))continue;
                    if(browserService.Handle(e))continue;
                    if(videoScreen is {} video)dispatch.Observe(video.Handle(e));
                    else if(movieScreen is {} movie)movie.Handle(e);
                    else if(worldScreen is {} world)dispatch.Observe(world.Handle(e));
                    else
                    {
                        if(frontendScreen is not null&&type==SDL_EventType.SDL_EVENT_KEY_UP&&e.key.scancode==SDL_Scancode.SDL_SCANCODE_SCROLLLOCK)
                            dispatch.Observe(TakeFrontendScreenshot());
                        else frontendScreen?.Handle(e);
                    }
                }
                handled=Stopwatch.GetTimestamp();
                browserService.Pump();
                dispatch.Drain();drained=Stopwatch.GetTimestamp();
                TickPreferences();TickSettingsScopes();TickGroupLaunch();TickMultibox();TickFocusMode();TickMultiboxMenu();TickCombatMeter();TickWaypointArrow();TickMissionTracker();TickModernUi();TickStuckAnswer();TickLagReports();TickDiscordPresence();
                LiveWorldOpenAlAudio.SetFocusMuted(_preferences.QualityOfLife?.MuteBackgroundAudio==true && !window.Focused);
                long now=Environment.TickCount64;
                // The world advances once per rendered frame, like retail SClientRunFrame 00742B60: one frame time
                // (SClient+0x10BC, 00918200), every object sampled at it (SClientProcessAI 00744260 -> object +0x20
                // UpdateSpaceObjectRuntime 006F3C60 -> +0x28 006F4610), then the scene render whose particle emitters
                // (009661C0 -> 0097E530/0097EB80) use the same frame time. An 8 ms TickCount64 gate here ran the scene
                // pump at the 15.6 ms timer resolution (64 Hz) while frames rendered faster, so emitters advanced on
                // frames whose positions had not moved and then received several frames of movement at once.
                if(videoScreen is null&&movieScreen is null&&worldScreen is {} ticking)ticking.Tick();
                else if(now>=nextUi)
                {
                    nextUi=now+8;
                    if(videoScreen is {} video)dispatch.Observe(video.Tick());
                    else if(movieScreen is {} movie){if(now>=nextMovie){nextMovie=now+16;dispatch.Observe(movie.Tick());}}
                    else frontendScreen?.Tick();
                }
                ticked=Stopwatch.GetTimestamp();
                // The issues badge is a development aid, shown only when diagnostic files are written (--logs).
                diagnostics.Resize(window.Size);diagnostics.IssueCount=DiagnosticFiles.Enabled?_runtimeIssues.Count:0;diagnostics.Tick();
                if(now>=nextDiagnostics){nextDiagnostics=now+2000;SavePresentationDiagnostics();SavePreferences();}
                if(!window.Minimized)
                {
                    window.Clear();
                    if(videoScreen is {HasFrame:true} video)video.Render();
                    else if(videoScreen is not null)frontendScreen?.Render(surface);
                    else if(movieScreen is {} movie)movie.Render();
                    else if(worldScreen is {} world)world.Render(surface);
                    else frontendScreen?.Render(surface);
                    if(videoScreen is null&&movieScreen is null)surface.RenderDiagnostics(window.Gl,window.PixelWidth,window.PixelHeight,window.Scale,diagnostics);
                    // Next Focus mode: a thumbnail names its character.
                    if(focusActive&&focusThumbnail&&worldScreen is not null&&_selectedCharacter is {} focusSelf)
                        surface.RenderFocusLabel(window.Gl,window.PixelWidth,window.PixelHeight,window.PixelDensity,focusSelf.FullName,FocusNameSize);
                    rendered=Stopwatch.GetTimestamp();
                    CompleteGlScreenshot();
                    if(inspect is not null)try{inspect(this);}catch(Exception ex){_inspectFailure=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex);throw;}
                    // Wayland maps a window with its first presented frame and never
                    // releases buffers of an unmapped one, so a hidden swap blocks.
                    // There the window is shown with the frontend's first frame.
                    if(!hidden&&!windowShown&&window.MapsOnPresent&&(_frontend is not null||worldScreen is not null)){window.Show();windowShown=true;}
                    if(windowShown||hidden||!window.MapsOnPresent){window.Swap();worldScreen?.Host.Presented();}
                    if(!hidden&&!windowShown&&(_frontend?.SplashPresented==true||worldScreen is not null)){window.Show();windowShown=true;}
                }
                FrameSucceeded();
                RecordSlowFrame(begin,handled,drained,ticked,rendered);
                ObserveLagFrame(begin,handled,drained,ticked,rendered);
            }
            catch(Exception ex) when(_inspectFailure is null)
            {RecoverFromFault(ex);}
            // A probe's own inspection failure is the probe's result, not a game fault.
            _inspectFailure?.Throw();
            int fps=CurrentFrameRate;
            SdlGameWindow.SleepUntil(begin+Stopwatch.Frequency/fps);
        }
    }
    private void Shutdown()
    {
        Exception? failure=null;
        void Release(Action action){try{action();}catch(Exception ex){failure??=ex;}}
        _frontendClosed=true;_benchmarkStop.Cancel();RecordedViewInput.Recorder=null;
        Release(()=>{CaptureNormalPlacement();SavePreferences();});
        // The window goes at once: closing work below must never leave a frozen last frame on screen.
        Release(window.Hide);
        Release(()=>videoScreen?.Dispose());videoScreen=null;
        Release(()=>movieScreen?.Dispose());movieScreen=null;
        Release(DisposeSavedAccounts);Release(StopMultibox);Release(()=>damagePanel?.Dispose());Release(()=>_client?.CollectCombat(false));
        Release(()=>groupLaunch?.Dispose());Release(()=>launchPanel?.Dispose());
        if(groupReporter is {} reporter)dispatch.Observe(reporter.DisposeAsync().AsTask());
        Release(()=>frontendScreen?.Dispose());frontendScreen=null;
        pendingGlScreenshot?.TrySetCanceled();pendingGlScreenshot=null;
        Release(diagnostics.Dispose);Release(diagnosticCapture.Dispose);
        Task dispose=DisposeClientAsync();
        Task presenceStop=_discordPresence?.DisposeAsync().AsTask()??Task.CompletedTask;
        // Closing work gets a few seconds; whatever is still pending then is logged and left behind, so quitting always quits.
        long deadline=Environment.TickCount64+5000;
        bool Pending()=>!presenceStop.IsCompleted||!dispose.IsCompleted||!startupTask.IsCompleted||!benchmarkLatency.IsCompleted||dispatch.PendingOperations>0;
        while(Pending()&&Environment.TickCount64<deadline)
        {Release(()=>{while(window.Poll(out _)){} });Release(browserService.Pump);Release(dispatch.Drain);Thread.Sleep(1);}
        if(Pending())Release(()=>AppendDiagnostic($"SHUTDOWN: still pending after 5 s and left behind: Discord presence {(presenceStop.IsCompleted?"done":"pending")}, client {(dispose.IsCompleted?"done":"pending")}, startup {(startupTask.IsCompleted?"done":"pending")}, benchmark {(benchmarkLatency.IsCompleted?"done":"pending")}, queued operations {dispatch.PendingOperations}"));
        if(dispose.IsCompleted)Release(()=>dispose.GetAwaiter().GetResult());if(startupTask.IsCompleted)Release(()=>startupTask.GetAwaiter().GetResult());
        if(benchmarkLatency.IsCompleted)Release(()=>benchmarkLatency.GetAwaiter().GetResult());
        if(presenceStop.IsCompleted)Release(()=>presenceStop.GetAwaiter().GetResult());
        FrameIntervalClock.FrameStarted-=UpdateFrameTitle;
        Release(browserService.Dispose);Release(surface.Dispose);Release(browserSnapshots.Dispose);Release(()=>cursor?.Dispose());
        Release(_runtimeIssues.Flush);Release(_sessionLog.Dispose);Release(_benchmarkStop.Dispose);Release(window.Dispose);
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    /// <summary>A world frame that held the interface thread a quarter second or more is written to the session log
    /// with where the time went (for pauses, such as the galaxy map's first opening).</summary>
    private void RecordSlowFrame(long begin,long handled,long drained,long ticked,long rendered)
    {
        double total=Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
        if(total<250||worldScreen is not {} world)return;
        static double Ms(long from,long to)=>to>=from?Stopwatch.GetElapsedTime(from,to).TotalMilliseconds:0;
        string map=world.Owner.Hud.TakeSlowMapRender() is {} slow?"; "+slow:"";
        AppendTranscript(FormattableString.Invariant($"SLOW FRAME {total:0} ms: input {Ms(begin,handled):0}, queued work {Ms(handled,drained):0}, ticks {Ms(drained,ticked):0}, render {Ms(ticked,rendered):0} (world {world.LastWorldRenderMs:0}, interface {world.LastInterfaceRenderMs:0}), present {Ms(rendered,Stopwatch.GetTimestamp()):0}{map}"));
    }
    private void SetBusy(bool busy){_busy=busy;_frontend?.SetBusy(busy);}
    private void SetStatus(string message,bool isError,bool success=false)=>_frontend?.SetStatus(isError?message:"");
    private void AppendDiagnostic(string message)
    {
        _runtimeIssues.ObserveDiagnostic(message);AppendTranscript(message);
    }
    private void AppendTranscript(string message)
    {
        string line=$"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";_sessionLog.Append(line);
        lock(_diagnosticsSync)
        {
            _diagnostics.Append(line);if(_diagnostics.Length<=MaxDiagnosticTranscriptCharacters)return;
            int overflow=_diagnostics.Length-MaxDiagnosticTranscriptCharacters,newline=-1;
            for(int i=overflow;i<_diagnostics.Length;i++)if(_diagnostics[i]=='\n'){newline=i;break;}
            _diagnostics.Remove(0,newline>=0?newline+1:overflow);
        }
    }
    private void SavePresentationDiagnostics()
    {
        if(worldScreen is {} world){_sessionLog.Presentation($"{DateTimeOffset.Now:O}{Environment.NewLine}{world.Owner.DiagnosticsText}");_sessionLog.PerformanceScene(world.Owner.PerformanceScene);}
        _sessionLog.Flush();
    }
    private async Task DisposeClientAsync()
    {
        CloseWorldWindow();_runtimeIssues.Flush();if(_client is not {} client)return;_client=null;
        try{await client.DisposeAsync();}catch(Exception ex){AppendDiagnostic("DISCONNECT ERROR: "+ex);}finally{_runtimeIssues.Flush();}
    }
}
