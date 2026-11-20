using System.Text.Json;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Engine;
using EnbNext.Net7.Diagnostics;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private sealed record ReplayBenchmarkOptions(string Mode, string OutputDirectory, string? Capture=null,
        long StartMicroseconds=0, long EndMicroseconds=0, int CaptureSeconds=900, bool AutoRecordOnWorldReady=false, string Label="stationary", int WarmupTrials=0, int MeasuredTrials=1, bool PrewarmBeforeRecording=false, int ReplayAfterRecordingTrials=0, int ExpectedPhysicalWidth=1920, int ExpectedPhysicalHeight=1080, string? MultiboxBarrierDirectory=null, int MultiboxClientIndex=0, EnbNext.Rendering.EnbRenderDetailRuntimeSettings? ComparisonDetailSettings=null);
    private ReplayBenchmarkOptions? _benchmark;
    private Task benchmarkLatency=Task.CompletedTask;
    private readonly List<object> _benchmarkStateSamples=new();
    private readonly CancellationTokenSource _benchmarkStop=new();
    private TimeSpan BenchmarkCaptureDuration => TimeSpan.FromSeconds(_benchmark?.Mode=="record" ? _benchmark.CaptureSeconds : 30);
    private async Task StartBenchmarkOrFrontendAsync()
    {
        string? manifest=Environment.GetEnvironmentVariable("ENBNEXT_FULL_CLIENT_BENCHMARK");
        if(string.IsNullOrWhiteSpace(manifest)){await InitializeFrontendAsync();return;}
        try
        {
            _benchmark=JsonSerializer.Deserialize<ReplayBenchmarkOptions>(File.ReadAllText(manifest))
                ?? throw new InvalidDataException("Empty full-client benchmark manifest.");
            if(_benchmark.Mode is not ("record" or "replay") || _benchmark.CaptureSeconds is < 30 or > 1800 ||
                _benchmark.ReplayAfterRecordingTrials is <0 or >3 ||
                (_benchmark.ReplayAfterRecordingTrials!=0 && (_benchmark.Mode!="record" || !_benchmark.AutoRecordOnWorldReady)))
                throw new InvalidDataException("Invalid bounded benchmark mode or capture duration.");
            if (_benchmark.ComparisonDetailSettings is {} detail &&
                (_benchmark.Mode != "replay" || detail.PolyBudget is <8000 or >40000 ||
                 detail.LandPolyBudget is <8000 or >40000 || detail.StarBudget != 2000 ||
                 detail.LandLod is <0 or >7 || detail.Texture.TextureReduction is <0 or >3 ||
                 (int)detail.Texture.TextureFilter is <0 or >2 || !float.IsFinite(detail.GammaFactor)))
                throw new InvalidDataException("Invalid explicit replay comparison settings.");
            Directory.CreateDirectory(_benchmark.OutputDirectory);
            FullClientBenchmarkMeter.StartupSignalPath=Path.Combine(_benchmark.OutputDirectory,"rendered-30-frames");

            benchmarkLatency = ObserveBenchmarkInputLatencyAsync(_benchmarkStop.Token);
            if(_benchmark.Mode=="record")
            {
                if(_benchmark.PrewarmBeforeRecording)_startupLogin=null;
                await InitializeFrontendAsync();
                if(_benchmark.PrewarmBeforeRecording)
                {
                    if(_frontend is null)throw new InvalidOperationException("Production frontend initialization failed.");
                    _frontend.IsEnabled=false;
                    await RunReplayTrialAsync(_benchmark,"initialization",true,0);
                    EnsureFrontend()!.IsEnabled=true;
                    ShowLoginScreen();
                    FullClientBenchmarkMeter.ResetStartupSignal(Path.Combine(_benchmark.OutputDirectory,"rendered-30-frames"));
                    window.Title("EnbNext — initialized; log in for automatic recording");
                    File.WriteAllText(Path.Combine(_benchmark.OutputDirectory,"ready-for-login.txt"),DateTime.UtcNow.ToString("O"));
                }
                RecordedViewInput.Recorder=input=>_client?.MarkRecordedBenchmark("replay-view-input-v1",JsonSerializer.Serialize(input));
                await WatchLiveBenchmarkAsync(_benchmarkStop.Token);
                return;
            }
            // Live entry has already initialized the normal frontend and its
            // audio/render resources. Replay must preserve that local lifecycle,
            // while never logging in or opening a remote transport.
            _startupLogin=null;
            window.SetPixelSize(_benchmark.ExpectedPhysicalWidth,_benchmark.ExpectedPhysicalHeight);window.RefreshSize();
            await InitializeFrontendAsync();
            if(_frontend is null)throw new InvalidOperationException("Production frontend initialization failed.");
            _frontend.IsEnabled=false;
            if(_benchmark.WarmupTrials is <0 or >2 || _benchmark.MeasuredTrials is <1 or >5)
                throw new InvalidDataException("Invalid bounded replay trial counts.");
            for(int trial=0;trial<_benchmark.WarmupTrials+_benchmark.MeasuredTrials;trial++)
            {
                bool warmup=trial<_benchmark.WarmupTrials;
                string receipt=warmup ? "warmup-"+(trial+1) : _benchmark.MeasuredTrials==1 ? "replay" : "replay-"+(trial-_benchmark.WarmupTrials+1);
                await RunReplayTrialAsync(_benchmark,receipt,warmup,trial);
            }
            _frontendClosed=true;
        }
        catch(OperationCanceledException)when(_benchmarkStop.IsCancellationRequested){FullClientBenchmarkMeter.Active=null;}
        catch(Exception ex)
        {
            FullClientBenchmarkMeter.Active=null;
            if(_benchmark is {} options)File.WriteAllText(Path.Combine(options.OutputDirectory,"failure.txt"),ex.ToString());
            AppendDiagnostic("Full-client benchmark failed: "+ex.Message);
            if(_benchmark?.Mode!="record"){_frontendClosed=true;}
        }
    }
    private async Task RunReplayTrialAsync(ReplayBenchmarkOptions options,string receipt,bool warmup,int trial)
    {
        // Repeated offline sessions can complete disposal synchronously. Yield
        // between expensive setup phases so Win32 input is serviced even then.
        await Task.Delay(1,_benchmarkStop.Token);
        using var preparationLease=await AcquireReplayPreparationAsync(options);
        ReplayPhase("frontend-create-start");
        EnbFrontendController? frontend=EnsureFrontend()??throw new InvalidOperationException("Frontend not initialized.");
        ReplayPhase("frontend-create-end");
        await Task.Delay(1,_benchmarkStop.Token);
        frontend.IsEnabled=false;
        _client=new EnbClient(new EnbClientOptions {TextureOverrideDirectory=EnbNext.Assets.EnbTextureOverridePack.ConfiguredDirectory,ServerHost="127.0.0.1",WireCaptureDirectory=null});
        _client.Diagnostic+=AppendDiagnostic;

        ReplayPhase("replay-prepare-start");
        await using var replay=await _client.PrepareRecordedReplayAsync(options.Capture
            ?? throw new InvalidDataException("Replay capture required."),options.StartMicroseconds,
            options.EndMicroseconds,_benchmarkStop.Token);
        ReplayPhase("replay-prepare-end");
        await Task.Delay(1,_benchmarkStop.Token);
        {
            // Every live login reattaches the character screen and runs its
            // existing asset/GPU prewarm and entry-audio handoff. Repeated
            // diagnostic sessions must not skip that lifecycle after warm-up.

            frontend.PreparationTrace=ReplayPhase;
            ReplayPhase("character-prewarm-start");
            await PrepareRecordedCharacterEntryAsync(frontend,_client.Characters,_benchmarkStop.Token);
            ReplayPhase("character-prewarm-end");
            frontend.PreparationTrace=null;
        }
        _selectedCharacter=_client.SelectedCharacter;
        if(_selectedCharacter is {} selected)ActivateCharacterDisplay(selected);
        var entryAudio=await frontend.TakeEntryAudioAsync();
        frontend=null; // Do not retain a detached frontend across the playback await.
        if(replay.ViewBootstrap is {} viewBootstrap)
        {using var state=JsonDocument.Parse(viewBootstrap);EnbNext.Rendering.EnbRetailCrtRandomSource.Current.State=state.RootElement.GetProperty("CrtState").GetUInt32();}
        ShowWorldWindowWithAudio(entryAudio, _selectedCharacter ?? throw new InvalidOperationException("Replay character unavailable."));
        // Read-only retail comparisons may override resolved graphics policy.
        // This is scoped to this replay view and never writes player preferences.
        worldScreen!.Owner.SetComparisonDetailSettings(options.ComparisonDetailSettings);
        preparationLease?.Dispose(); // No setup serialization inside playback or measurement.
        window.Title("EnbNext — replay warm-up (not measured)");
        replay.ViewInput=async json=>await DispatchAsync(()=>
            ApplyRecordedViewInput(JsonSerializer.Deserialize<RecordedViewInput>(json)
                ??throw new InvalidDataException("Invalid recorded view input.")));
        if(options.MultiboxBarrierDirectory is {} barrier)
        {
            if(options.MultiboxClientIndex is <0 or >5)throw new InvalidDataException("Invalid multibox index.");
            string phase=Path.Combine(barrier,"trial-"+trial);
            Directory.CreateDirectory(phase);
            File.WriteAllText(Path.Combine(phase,"ready-"+options.MultiboxClientIndex),DateTimeOffset.UtcNow.ToString("O"));
            string release=Path.Combine(phase,"release.txt");
            var deadline=DateTimeOffset.UtcNow.AddSeconds(90);
            while(!File.Exists(release))
            {
                if(DateTimeOffset.UtcNow>deadline)throw new TimeoutException("Multibox barrier timed out.");
                await Task.Delay(50,_benchmarkStop.Token);
            }
            var start=DateTimeOffset.Parse(File.ReadAllText(release),System.Globalization.CultureInfo.InvariantCulture);
            var delay=start-DateTimeOffset.UtcNow;
            if(delay.TotalMilliseconds<0)throw new InvalidDataException("Missed multibox release.");
            await Task.Delay(delay,_benchmarkStop.Token);
        }
        if(hidden)
        {
            // Off-desktop windows can start under a different non-client DPI.
            // Settle the physical client viewport before the replay clock starts.
            for(int attempt=0;attempt<4;attempt++)
            {
                await Task.Delay(100,_benchmarkStop.Token);
                int width=window.PixelWidth;
                int height=window.PixelHeight;
                if(width==options.ExpectedPhysicalWidth && height==options.ExpectedPhysicalHeight)break;
                window.SetPixelSize(options.ExpectedPhysicalWidth,options.ExpectedPhysicalHeight);window.RefreshSize();
            }
            await Task.Delay(100,_benchmarkStop.Token);
            ValidateBenchmarkViewport();
        }
        Task playback=replay.RunAsync(_benchmarkStop.Token);
        Task entry=_client.CompleteRecordedReplayEntryAsync(_benchmarkStop.Token);
        while(replay.ElapsedMicroseconds<options.StartMicroseconds && !playback.IsCompleted)
            await Task.Delay(10,_benchmarkStop.Token);
        if(!entry.IsCompletedSuccessfully)throw new InvalidDataException("Replay has not entered the world before measurement begins.");
        await entry;
        long actualStartMicroseconds=replay.ElapsedMicroseconds;
        object metrics=await MeasureBenchmarkSegmentAsync(playback,_benchmarkStop.Token);
        long actualEndMicroseconds=replay.ElapsedMicroseconds;
        WriteBenchmarkReceipt(receipt,metrics,new {Trial=trial+1,Warmup=warmup,replay.InboundDatagrams,replay.OutboundDatagrams,
            replay.MaximumDeliveryLatenessMicroseconds,options.StartMicroseconds,options.EndMicroseconds,
            ActualStartMicroseconds=actualStartMicroseconds,ActualEndMicroseconds=actualEndMicroseconds});
        // Concurrent load tests retain and report recoverable fence backpressure.
        // A retry submits no third frame; it is not a GL error/reset. Single-client
        // promotion remains fail-closed on any timeout, as before.
        if(_runtimeIssues.Count!=0 || (options.MultiboxBarrierDirectory is null && EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.TimeoutCount!=0) || worldScreen?.Owner.BenchmarkRendererFailure is not null)
            throw new InvalidDataException("Replay trial failed runtime/fence validation; no subsequent trial will start.");
        await Task.Delay(1,_benchmarkStop.Token);
        ReplayPhase("world-close-start");
        CloseWorldWindow();
        ReplayPhase("world-close-end");
        await DisposeClientAsync();
        ReplayPhase("client-dispose-end");
    }

    private async Task<FileStream?> AcquireReplayPreparationAsync(ReplayBenchmarkOptions options)
    {
        if(options.MultiboxBarrierDirectory is not {} directory)return null;
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"frontend-preparation.lock");
        var wait=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            _benchmarkStop.Token.ThrowIfCancellationRequested();
            try { return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
            catch(IOException)when(wait.Elapsed<TimeSpan.FromSeconds(90))
            { await Task.Delay(50,_benchmarkStop.Token); }
        }
    }
    private void ReplayPhase(string phase)
        => File.AppendAllText(Path.Combine(_benchmark!.OutputDirectory,"lifecycle-phases.log"),DateTimeOffset.UtcNow.ToString("O")+" "+phase+Environment.NewLine);
    private async Task ObserveBenchmarkInputLatencyAsync(CancellationToken token)
    {
        // A render callback or WM_NULL can remain responsive while Input-priority
        // work starves. Observe that queue independently, without injecting keys.
        var samples = new List<double>();
        var startup = new List<double>();
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(100, token).ConfigureAwait(false);
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                bool ready=FullClientBenchmarkMeter.StartupSignalPath is null;
                await DispatchAsync(() =>
                    (ready ? samples : startup).Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            samples.Sort();
            File.WriteAllText(Path.Combine(_benchmark!.OutputDirectory, "input-latency.json"),
                JsonSerializer.Serialize(new { StartupCount=startup.Count, StartupMaxMs=startup.Count==0 ? 0 : startup.Max(), Count=samples.Count,
                    MedianMs=samples.Count==0 ? 0 : samples[samples.Count/2],
                    P95Ms=samples.Count==0 ? 0 : samples[(samples.Count-1)*95/100],
                    MaxMs=samples.Count==0 ? 0 : samples[^1] }));
        }
    }
    private async Task WatchLiveBenchmarkAsync(CancellationToken cancellationToken)
    {
        // Operator creates this control file only once the client is stationary.
        // No new in-game control or login automation is introduced.
        string trigger=Path.Combine(_benchmark!.OutputDirectory,"measure.request");
        while(!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(250,cancellationToken);
            string finish=Path.Combine(_benchmark.OutputDirectory,"finish.request");
            if(File.Exists(finish))
            {
                File.Delete(finish);
                if(_client is {} client)await client.CompleteBenchmarkCaptureAsync();
                File.WriteAllText(Path.Combine(_benchmark.OutputDirectory,"capture-complete.txt"),_client?.LastWireCapturePath??"No capture");
                RecordedViewInput.Recorder=null;return;
            }
            bool automatic=_benchmark.AutoRecordOnWorldReady && _client?.State==ClientLifecycleState.InWorld &&
                worldScreen?.Owner.BenchmarkWorldReady==true && File.Exists(Path.Combine(_benchmark.OutputDirectory,"rendered-30-frames"));
            if(!automatic && !File.Exists(trigger))continue;
            string label=automatic ? _benchmark.Label : File.ReadAllText(trigger).Trim();
            if(File.Exists(trigger))File.Delete(trigger);
            if(_client?.State!=ClientLifecycleState.InWorld || _client.RecordedCaptureElapsedMicroseconds is not long start)
                throw new InvalidOperationException("Live measurement requires InWorld and an active bounded wire recording.");
            _client.MarkRecordedBenchmark("benchmark-segment-start",label);
            object metrics=await MeasureBenchmarkSegmentAsync(Task.Delay(TimeSpan.FromSeconds(20),cancellationToken),cancellationToken);
            long? end=_client.RecordedCaptureElapsedMicroseconds;
            _client.MarkRecordedBenchmark("benchmark-segment-end",label);
            WriteBenchmarkReceipt("live-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"),metrics,new {Label=label,StartMicroseconds=start,EndMicroseconds=end,Capture=_client.LastWireCapturePath});
            if(automatic)
            {
                await _client.CompleteBenchmarkCaptureAsync();
                File.WriteAllText(Path.Combine(_benchmark.OutputDirectory,"capture-complete.txt"),_client.LastWireCapturePath??"No capture");
                RecordedViewInput.Recorder=null;
                string completedCapture=_client.LastWireCapturePath??throw new InvalidDataException("Completed recording path is missing.");
                int pairedTrials=_benchmark.ReplayAfterRecordingTrials;
                CloseWorldWindow();await DisposeClientAsync();
                if(pairedTrials>0)
                {
                    if(_runtimeIssues.Count!=0 || EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.TimeoutCount!=0)
                        throw new InvalidDataException("Live recording failed runtime/fence validation; paired replay will not start.");
                    // Keep the host/process conditions while replacing the entire
                    // client/session/world each time. The completed wire recording
                    // remains authoritative; no live scene is reused as a replay.
                    _benchmark=_benchmark with {Mode="replay",Capture=completedCapture,
                        StartMicroseconds=start,EndMicroseconds=end??throw new InvalidDataException("Recording end is missing."),
                        AutoRecordOnWorldReady=false,PrewarmBeforeRecording=false,ReplayAfterRecordingTrials=0};
                    EnsureFrontend()!.IsEnabled=false;
                    for(int trial=0;trial<pairedTrials;trial++)
                        await RunReplayTrialAsync(_benchmark,"replay-"+(trial+1),false,trial);
                }
                _frontendClosed=true;return;
            }
        }
    }
    private async Task<object> MeasureBenchmarkSegmentAsync(Task stop,CancellationToken token)
    {
        ValidateBenchmarkViewport();
        _benchmarkStateSamples.Clear();
        if(DeveloperRenderDiagnostics.SessionUsed)throw new InvalidOperationException("Restart before calibration: developer diagnostic session.");
        window.Title("EnbNext — measuring fixed 20-second segment");
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            while(!stop.IsCompleted)
            {
                ValidateBenchmarkViewport();
                var scene=worldScreen?.Owner.PerformanceScene;
                _benchmarkStateSamples.Add(new {Focused=window.Focused,HostActive=window.Focused,FrameCap=CurrentFrameRate,WorkingSetBytes=System.Diagnostics.Process.GetCurrentProcess().WorkingSet64,ManagedLiveEstimateBytes=GC.GetTotalMemory(false),ClientTime=_client?.CurrentClientTime,Objects=scene?.Scene.Objects.Count,
                    Effects=scene?.Scene.Effects.Length,Camera=scene?.Camera,RendererStatus=scene?.RendererStatus});
                await Task.WhenAny(stop,Task.Delay(250,token));token.ThrowIfCancellationRequested();
            }
            await stop;
        }
        finally{FullClientBenchmarkMeter.Active=null;window.Title("EnbNext");}
        ValidateBenchmarkViewport();
        return meter.Finish();
    }
    private void ValidateBenchmarkViewport()
    {
        int width=window.PixelWidth;
        int height=window.PixelHeight;
        if(width!=_benchmark!.ExpectedPhysicalWidth || height!=_benchmark.ExpectedPhysicalHeight)
            throw new InvalidDataException($"Benchmark viewport {width}x{height} differs from required {_benchmark.ExpectedPhysicalWidth}x{_benchmark.ExpectedPhysicalHeight}; measurement rejected.");
    }
    private void WriteBenchmarkReceipt(string name,object metrics,object timeline)
    {
        File.WriteAllText(Path.Combine(_benchmark!.OutputDirectory,name+".metrics"),JsonSerializer.Serialize(new {Metrics=metrics,Timeline=timeline}));
        var scene=worldScreen?.Owner.PerformanceScene;
        var receipt=new {Version=1,Calibrated=false,OffscreenAnalysis=hidden,TimingEligible=!hidden,Mode=_benchmark!.Mode,Backend="SDL3/EGL",
            GraphicsAdapters=worldScreen?.Owner.BenchmarkGraphicsAdapters,
            PhysicalWidth=window.PixelWidth,PhysicalHeight=window.PixelHeight,
            RenderScaling=window.Scale,Timeline=timeline,Metrics=metrics,FullProductionWorld=worldScreen is not null,
            RecordedIssues=_runtimeIssues.Count,IssueReport=_runtimeIssues.ReportPath,
            FenceTimeoutCount=EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.TimeoutCount,
            FenceTimeoutEvents=EnbNext.Rendering.OpenGL.EnbOpenGlFrameQueue.SnapshotTimeoutEvents(),
            BuildModule=typeof(SdlApplication).Module.ModuleVersionId,
            BuildModules=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name?.StartsWith("EnbNext.",StringComparison.Ordinal)==true).OrderBy(a=>a.GetName().Name).ToDictionary(a=>a.GetName().Name!,a=>a.ManifestModule.ModuleVersionId),Tempo=1.0,FrameCap=CurrentFrameRate,FrameTimer="SDL main-thread deadline",EglHostPresentation=window.ContextDescription,
            Objects=scene?.Scene.Objects.Count,Effects=scene?.Scene.Effects.Length,StateSamples=_benchmarkStateSamples.ToArray(),
            DetailSettings=scene?.DetailSettings,ComparisonDetailSettings=_benchmark.ComparisonDetailSettings,FirstPerson=scene?.FirstPerson,
            Preferences=ActiveDisplayPreferences,
            LocalShipPose=scene?.Scene.Objects.Where(o=>o.IsLocalShip).Select(o=>new {
                o.GameId,Position=o.Spatial?.Transform.Position,Orientation=o.Spatial?.Transform.Orientation,
                Velocity=o.Spatial?.Transform.Velocity,Rotation=o.Spatial?.Transform.Rotation }).ToArray(),
            Camera=scene?.Camera,ClientTime=_client?.CurrentClientTime,RendererFailure=worldScreen?.Owner.BenchmarkRendererFailure,
            AudioMuted=Environment.GetEnvironmentVariable("ENB_AUDIO_MUTE_OUTPUT")=="1",AudioDisabled=Environment.GetEnvironmentVariable("ENB_AUDIO_SILENT")=="1",
            Limitation="Calibration pending: camera/UI inputs and retained world counts must match the live segment; no performance decision is authorized by this receipt alone."};
        File.WriteAllText(Path.Combine(_benchmark.OutputDirectory,name+".json"),JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals}));
    }
    private Task DispatchAsync(Action action,CancellationToken token=default)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch.Post(_=>{try{token.ThrowIfCancellationRequested();action();completion.TrySetResult();}catch(Exception ex){completion.TrySetException(ex);}},null);
        return completion.Task;
    }
    private void ApplyRecordedViewInput(RecordedViewInput input)
    {
        var owner=worldScreen?.Owner??throw new InvalidOperationException("World is unavailable.");
        switch(input.Kind)
        {
            case "pointer":owner.ApplyCameraPointer(new(input.X,input.Y),input.Elapsed,input.Initial,input.Distance,input.Control);break;
            case "end":owner.EndManualCameraInput();break;
            case "wheel":owner.ZoomCameraWheel(input.Initial);break;
            case "action":owner.HandleCameraAction(input.Action,null);break;
            default:throw new InvalidDataException("Unknown recorded camera input.");
        }
    }
    private static async Task PrepareRecordedCharacterEntryAsync(EnbFrontendController frontend,IReadOnlyList<CharacterSummary> characters,CancellationToken token)
    {
        await frontend.ShowCharactersAsync(characters);
        while(frontend._gpu.FrontendLastRenderedScreen!="characters"||frontend.SelectionEntering||frontend._gpu.FrontendPrewarmRemaining>0)
        {
            if(frontend.PresentationError is {} error)throw new InvalidOperationException(error);
            await Task.Delay(16,token);
        }
    }
}
