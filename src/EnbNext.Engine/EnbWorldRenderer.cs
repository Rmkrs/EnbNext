using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Rendering.OpenGL;
namespace EnbNext.Engine;
internal sealed record LiveInfluenceSkyPresentation(
    bool OverrideActive,
    ulong SnapshotVersion,
    uint? SectorNum,
    uint? GameId,
    EnbCurrentRetailInfluenceNebulaRegion Region,
    Vector3? SkyBoxColor)
{
    public static LiveInfluenceSkyPresentation Inactive { get; } = new(
        OverrideActive: false,
        SnapshotVersion: 0,
        SectorNum: null,
        GameId: null,
        Region: EnbCurrentRetailInfluenceNebulaRegion.Outside,
        SkyBoxColor: null);
}
internal readonly record struct LivePlanetGpuIdentity(
    ulong RenderIdentity,
    ulong VisualGeneration);
internal sealed record LivePlanetGpuPresentation(
    ulong SnapshotVersion,
    ImmutableHashSet<LivePlanetGpuIdentity> Owned,
    ImmutableHashSet<LivePlanetGpuIdentity> Presented,
    ImmutableDictionary<LivePlanetGpuIdentity, float> DayNightPhasesByRenderIdentity,
    ImmutableDictionary<LivePlanetGpuIdentity, uint> MapperStartMillisecondsByRenderIdentity,
    string Status)
{
    public static LivePlanetGpuPresentation Waiting { get; } = new(
        SnapshotVersion: 0,
        Owned: ImmutableHashSet<LivePlanetGpuIdentity>.Empty,
        Presented: ImmutableHashSet<LivePlanetGpuIdentity>.Empty,
        DayNightPhasesByRenderIdentity: ImmutableDictionary<LivePlanetGpuIdentity, float>.Empty,
        MapperStartMillisecondsByRenderIdentity: ImmutableDictionary<LivePlanetGpuIdentity, uint>.Empty,
        Status: "planet: R8A current-retail camera-geometry presentation waiting for an authored planet draw");
    public bool Contains(IEnbRenderObject obj)
        => Presented.Contains(new LivePlanetGpuIdentity(obj.RenderIdentity, obj.VisualGeneration));
    public bool Owns(IEnbRenderObject obj)
        => Owned.Contains(new LivePlanetGpuIdentity(obj.RenderIdentity, obj.VisualGeneration));
}
/// <summary>
/// Production OpenGL owner for the live-world framebuffer.
/// backdrop rasters are presented first, then ordinary W3D uses the same color
/// and depth target so destination-dependent authored blends remain native. The
/// renderer consumes the ordered render-owner scene and camera boundary;
/// the host supplies the context, framebuffer and physical viewport.
/// </summary>
internal sealed partial class EnbWorldRenderer
{
    private readonly EnbRenderDetailBudgetAllocator.Workspace _detailWorkspace=new();
    private readonly List<EnbRenderDetailOwner> _sharedDetailOwners=[];
    private readonly List<EnbHlodDetailOwner> _hlodDetailOwners=[];
    private readonly RootTaskList _hlodCandidateRoots=new();
    private readonly List<LiveShipHlodDecision?> _hlodCandidateDecisions=[];
    private static readonly string[] SubmissionStageNames=["submission.backend.setup","submission.backend.uniform","submission.backend.state","submission.backend.mapper","submission.backend.mapper-uniform","submission.backend.texture","submission.backend.draw","submission.backend.cleanup"];
    private const int GlDepthBufferBit = 0x00000100;
    private readonly Func<EnbRetailBaseAssetResolver?> _baseAssetResolverAccessor;
    private readonly Func<EnbRetailAssetBundle?> _retailAssetsAccessor;
    private readonly Func<uint?> _fixedSessionIdAccessor;
    private readonly bool _enableAudio;
    private readonly bool _useMainViewEnvironment;
    private readonly bool _continuousPresentation;
    private readonly Vector4 _clearColor;
    private readonly EnbOpenGlFirstW3dShaderBackend _unlitBackend = new();
    private readonly EnbOpenGlFixedFunctionLightingBackend _litBackend = new();
    private readonly EnbOpenGlStage0TextureBackend _texturedBackend = new()
    {
        MeasureSubmissionStages = Environment.GetEnvironmentVariable("ENB_RENDER_SUBMISSION_TIMING") == "1",
        NPatchCapabilityEnabled = Environment.GetEnvironmentVariable("ENB_NPATCHES") == "1",
    };
    private readonly EnbOpenGlBackdropRenderer _backdropRenderer = new();
    // MainView130 is initialized to one at 50B43C; it is independent of SwapBackdrop.
    internal bool SpaceStarsEnabled { get; set; } = true;
    internal Action<EnbSpaceScenePass>? SpacePassObserved { get; set; }
    // Offline parity controls asset readiness independently of worker timing.
    // Normal clients always use the asynchronous task path.
    internal bool CompleteVisualAssetLoadsInline { get; set; }
    private Task<T> LoadVisualAsset<T>(Func<T> load)
    {
        if(EnbHitchEvents.Enabled)
        {
            var original=load;
            load=()=>{using var timing=EnbHitchEvents.Measure("asset.decode");return original();};
        }
        if(!CompleteVisualAssetLoadsInline)return Task.Run(load);
        try {return Task.FromResult(load());}
        catch(Exception error){return Task.FromException<T>(error);}
    }
    internal Action? SpaceGeometryObserved { get; set; }
    internal Action<EnbW3dDrawTask>? SubmittedMeshObserved { get; set; }
    internal Action<EnbW3dIndexedSubmissionPlan>? SubmittedRangesObserved { get; set; }
    private bool IsSpaceComposition(LiveFrame? frame) => _useMainViewEnvironment &&
        !_embeddedScanner && frame?.Snapshot.Environment?.PresentationMode is 0 or 5;
    private readonly Dictionary<int, EnbBaseAssetResolution> _baseAssets = [];
    private readonly Dictionary<LiveCpuRootKey, LiveCpuRoot> _cpuRoots = [];
    private readonly Dictionary<LiveGpuKey, LiveGpuResource> _gpuResources = [];
    private readonly object _preparedGpuContext=new();
    private long _preparedGpuBindingHits,_preparedGpuBindingMisses;
    private sealed class PreparedGpuBindings(object context,int count)
    {
        public readonly object Context=context;
        public OwnedMapperContext? MapperOwner;
        public readonly PreparedGpuBinding?[] Slots=new PreparedGpuBinding?[count];
    }
    private sealed record PreparedGpuBinding(object Context,LiveGpuKey Key,LiveGpuResource Resource,LiveCachedLanePlan Lane)
    {
        public bool Matches(object context,long generation,LiveLaneKind lane,int reduction,EnbRenderTextureFilterMode filter,
            string? reflection,uint? mapper,object? batch)=>ReferenceEquals(Context,context) && !Resource.IsDestroyed &&
            Key.AssetGeneration==generation && Key.Lane==lane && Key.TextureReduction==reduction && Key.TextureFilter==filter &&
            Key.EnvironmentReflectionTexture==reflection && Key.MapperStartMillisecondsOverride==mapper && ReferenceEquals(Key.BatchIdentity,batch);
    }
    private readonly Dictionary<LiveGpuKey, EnbOpenGlTextureMeshRecoveryState> _gpuRecoveryState = [];

    private static EnbOpenGlTextureMeshRecoveryState RetainGpuState(LiveGpuResource gpu)
        => EnbOpenGlTextureMeshRecoveryState.Retain(gpu.Textured!);
    private long _gpuResidencyFrame;
    private readonly Dictionary<LiveGpuKey, LiveCachedLanePlan> _lanePlans = [];
    private readonly Dictionary<LivePlanetGpuIdentity, LivePlanetMapperStateOwnership> _planetMapperStateOwnership = [];
    private readonly Dictionary<LiveParticleEmitterKey, long> _particleEmitterStartTimes = [];
    private readonly Dictionary<LiveParticleEmitterKey, (EnbCurrentRetailAnimatedParticleRuntime.Emitter State, uint Age)> _ordinaryParticleBirths = [];
    private readonly Dictionary<LiveNameDecalTextureKey, EnbCurrentRetailShipNameDecalTexture> _nameDecalTextures = [];
    private readonly Dictionary<string,EnbTextureImage> _ownedNameDecalImages=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<LiveHlodKey, int> _hlodLevels = [];
    private readonly Dictionary<LiveStaticHlodKey, EnbW3dHlodBoundsOwner> _staticHlodBounds = [];
    private readonly Dictionary<LivePostTransformHlodKey, LivePostTransformHlodBounds> _postTransformHlodBounds = [];
    private readonly Dictionary<LiveStaticHlodKey, LiveHlodCompiledCosts> _hlodCompiledCosts = [];
    private readonly Dictionary<string, ExternalHierarchyResolution> _externalHierarchies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveHlodPrototypeEvidence> _hlodPrototypeEvidence = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<EnbW3dRenderResourceKey, LiveHAnimFamilyCensus> _hAnimFamilyCensuses = [];
    private readonly Dictionary<LiveRslPacketNextOnceTransitionKey, LiveRslPacketNextOnceTransition> _rslPacketNextOnceTransitions = [];
    private readonly Dictionary<LiveRslAutomaticNextTransitionKey, LiveRslAutomaticNextTransition> _rslAutomaticNextTransitions = [];
    private readonly Dictionary<LiveRslSoundInstanceKey, LiveRslSoundCompletionEvent> _rslSoundCompletions = [];
    private readonly HashSet<LiveRslLinkedStateActivationKey> _rslLinkedStateActivations = [];
    private readonly EnbCurrentRetailEffectRslSchedulerRuntime _effectRslScheduler = new();
    private readonly EnbCurrentRetailEffectRslObjectRuntime _effectRslObjectRuntime = new();
    private readonly EnbCurrentRetailSceneCallbackList<(ulong Identity, ulong Generation)> _ordinaryRslCallbacks;
    private readonly HashSet<(ulong Identity, ulong Generation)> _ordinaryRslCallbackMembers = [];
    private readonly EnbCurrentRetailRslHAnimRegistryRuntime _hAnimRegistry = new();
    private EnbCurrentRetailBaseAssetOverbrightCache _overbrightMaterials = new();
    private EnbShipMaterialCache _shipMaterials = new();
    private EnbWorldRenderer? _sharedMaterialOwner;
    private readonly EnbW3dIndexedSubmissionCache _indexedSubmissions = new();
    // One monotonic mapper sample per frame; injectable for deterministic GPU receipts.
    internal Func<uint> MapperClock { get; set; } = static () => unchecked((uint)Environment.TickCount);
    [ThreadStatic] private static uint? s_frameMapperClock;
    private uint ReadMapperClock() => s_frameMapperClock ?? MapperClock();
    internal EnbRetailRenderBudgetSourceSettings? RenderBudgetSourceSettingsOverride { get; set; }
    internal bool FrustumCullingEnabled { get; set; } = true;
    internal bool ParticleStreamingEnabled { get; set; } = true;
    // 764030/45C1B0 retain the same library template across BASE selectors.
    private readonly Dictionary<EnbResolvedRslStateResource,
        (LiveCpuRoot? Root, string? Diagnostic)> _rslObjectPrototypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, IReadOnlyList<EnbCurrentRetailRslEffectiveAction>> _rslEffectiveActionsByRenderIdentity = [];
    private long _rslRenderStructureUses, _rslRenderStructureFallbacks;
    private IReadOnlyDictionary<ulong, EnbRslObjectState> _frameRslObjectUpdates =
        new Dictionary<ulong, EnbRslObjectState>();
    private readonly HashSet<string> _capturedHlodResearchResources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, EnbRetailOrbitalSceneDefinition> _orbitalScenes = [];
    private readonly Dictionary<uint, EnbRetailMainViewLightSectorDefinition> _mainViewLightSectors = [];
    private EnbRetailSceneFogData? _sceneFogData;
    private readonly EnbRetainedSceneSplineState _loadSceneSplines=new();
    private readonly EnbRetainedSceneSplineState _currentSceneSplines=new();
    private string? _sceneFogDataFailure;
    private string? _environmentAtlasReflectionTexture;
    private int _environmentAtlasTextureReduction = -1;
    private EnbCurrentRetailEnvironmentAtlasSet? _environmentAtlasSet;
    private string? _environmentAtlasDiagnostic;
    private bool _environmentAtlasAttempted;
    private EnbRetailBaseAssetResolver? _lastResolver;
    private EnbRetailAssetBundle? _lastRetailAssets;
    private LiveWorldOpenAlAudio? _audio;
    /// <summary>Next music player: the window's requests and what plays (see EnbMusicPlayer).</summary>
    internal EnbMusicPlayer MusicPlayer { get; } = new();
    private LiveWorldOpenAlAudio.SpeechScope _speechScope = new();
    internal void ShareSpeechScope(EnbWorldRenderer owner) => _speechScope = owner._speechScope;
    internal void UseSpeechScope(LiveWorldOpenAlAudio.SpeechScope scope) => _speechScope = scope;
    private long _assetGeneration,_textureRevision;

    private LiveFrame? _pendingFrame;
    private LiveWorldW3dStatus _status = LiveWorldW3dStatus.Waiting;
    private LiveInfluenceSkyPresentation _influenceSkyPresentation = LiveInfluenceSkyPresentation.Inactive;
    private readonly Dictionary<(ulong Identity, ulong Generation, int Base), EnbCurrentRetailInfluenceNebulaState> _influenceStates = [];
    private readonly Dictionary<(ulong Identity, ulong Generation, int Base), EnbCurrentRetailInfluenceNebulaEvaluation> _frameInfluenceEvaluations = [];
    private LivePlanetGpuPresentation _planetGpuPresentation = LivePlanetGpuPresentation.Waiting;
    private LiveCompletedRenderQueries _projectedObjectBounds = LiveCompletedRenderQueries.Empty;
    private readonly List<LiveCompletedRenderQueries> _completedQueryStorage=[];
    private static readonly bool CaptureBodyQueries=Environment.GetEnvironmentVariable("ENB_DIAG_BODY_QUERIES")=="1";
    private LiveFrameCadenceStatus _cadence = LiveFrameCadenceStatus.Waiting;
    private long _presentedFrameToken;
    private long _cadenceWindowStartTimestamp;
    private long _lastRenderTimestamp;
    private int _cadenceWindowFrames;
    private double _cadenceWindowCpuMilliseconds;
    private double _cadenceWindowWorstCpuMilliseconds;
    private readonly List<double> _cadenceIntervals = [];
    private long _lastRenderedAnchorTimestamp;
    private int _cadenceRepeatedAnchors;
    private readonly LiveFrameStallHistory _stallHistory = new();
    private int _lastFrameGen0Collections;
    private int _frameGpuCreates;
    private readonly List<string> _frameGpuCreateEvidence = new(4);
    private int _frameGpuEvictions;
    private long _frameGpuEvictedBytes;
    private string _lastFrameProfile = "world not rendered";
    private LivePerformanceSnapshot? _performance;
    public LivePerformanceSnapshot? Performance=>Volatile.Read(ref _performance);
    private double[]? _framePerformanceStages;
    private string _framePerformanceScene="Space",_performanceStageScene="Space";
    private readonly double[] _performanceStageTotals=new double[7];
    private int _performanceStageFrames;
    private long _performanceAllocation;
    private int _performanceGc;
    private double _performanceGap;
    private bool _initialized;

    public EnbWorldRenderer(
        Func<EnbRetailBaseAssetResolver?> baseAssetResolverAccessor,
        Func<EnbRetailAssetBundle?> retailAssetsAccessor,
        Func<uint?> fixedSessionIdAccessor,
        bool enableAudio = true,
        bool useMainViewEnvironment = true,
        Vector4? clearColor = null,
        bool continuousPresentation = true)
    {
        _baseAssetResolverAccessor = baseAssetResolverAccessor ?? throw new ArgumentNullException(nameof(baseAssetResolverAccessor));
        _retailAssetsAccessor = retailAssetsAccessor ?? throw new ArgumentNullException(nameof(retailAssetsAccessor));
        _fixedSessionIdAccessor = fixedSessionIdAccessor ?? throw new ArgumentNullException(nameof(fixedSessionIdAccessor));
        _enableAudio = enableAudio;
        _useMainViewEnvironment = useMainViewEnvironment;
        _continuousPresentation = continuousPresentation;
        _clearColor = clearColor ?? Vector4.Zero;
        _retainedEmitterCallbacks=new(_particleCallbacks);
        _ordinaryRslCallbacks=new(_particleCallbacks);
        _effectRslCallbacks=new(_particleCallbacks);
        _exhaustCallbacks=new(_particleCallbacks);
        _attachmentCallbacks=new(_particleCallbacks);
        _compositeCallbacks=new(_particleCallbacks);
        _particleJetCallbacks=new(_particleCallbacks);
        _tunnelCallbacks=new(_particleCallbacks);
    }

    private EnbWorldRenderer? _scannerRenderer;
    private bool _embeddedScanner;
    private readonly EnbOpenGlScannerFramebuffer _scannerFramebuffer = new();
    private ScannerPresentation? _scannerPresentation;

    public void AttachScanner(EnbWorldRenderer scanner)
    {
        if (_initialized) throw new InvalidOperationException("Attach the scanner before GPU initialization.");
        _scannerRenderer = scanner;
        scanner._embeddedScanner = true;
        scanner._shipExhaustOwner = this;
        scanner._graphicsOptionsOwner = this;
    }


    //744434..744472 updates the shared helper list once using MainView's
    //camera. A secondary render consumes that result, not its own camera.
    private EnbCurrentRetailInfluenceNebulaEvaluation? EvaluateInfluence(
        (ulong Identity, ulong Generation, int Base) key,
        EnbCurrentRetailInfluenceNebulaBoundarySource source, Vector3 camera,
        EnbRetailNebulaEnvironmentCatalog? catalog)
    {
        if (_shipExhaustOwner is {} primary)
            return primary._frameInfluenceEvaluations.GetValueOrDefault(key);
        if (!_influenceStates.TryGetValue(key, out var state))
            _influenceStates.Add(key, state = new(source));
        var evaluation = state.Evaluate(camera, catalog);
        _frameInfluenceEvaluations[key] = evaluation;
        return evaluation;
    }
    public LiveInfluenceSkyPresentation InfluenceSkyPresentation =>
        Volatile.Read(ref _influenceSkyPresentation);

    public LivePlanetGpuPresentation PlanetGpuPresentation =>
        Volatile.Read(ref _planetGpuPresentation);

    /// <summary>Next "Hide user interface": the scanner, cockpit and damage numbers are not drawn.</summary>
    internal bool InterfaceHidden { get; set; }
    public LiveCompletedRenderQueries ProjectedObjectBounds =>
        Volatile.Read(ref _projectedObjectBounds);

    public long PresentedFrameToken => Volatile.Read(ref _presentedFrameToken);

    public string StatusText
    {
        get
        {
            LiveFrameCadenceStatus cadence = Volatile.Read(ref _cadence);
            return ReadCombinedStatus(cadence, Volatile.Read(ref _graphicsDeviceStatus), Volatile.Read(ref _status));
        }
    }

    public void UpdateFrame(
        IEnbSceneFrame snapshot,
        EnbResolvedRenderCamera camera,
        bool isFirstPerson,
        uint currentClientTime,
        EnbBackdropFrame? backdrop,
        long presentationToken = 0,
        ImmutableDictionary<LivePlanetGpuIdentity, float>? dayNightPhaseOverrides = null,
        ImmutableDictionary<LivePlanetGpuIdentity, uint>? mapperStartMillisecondsOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(snapshot is EnbSceneFrame ownedFrame)ownedFrame.Retain();
        var previous=Interlocked.Exchange(
            ref _pendingFrame,
            new LiveFrame(
                snapshot,
                camera,
                isFirstPerson,
                currentClientTime,
                Stopwatch.GetTimestamp(),
                backdrop,
                presentationToken,
                dayNightPhaseOverrides ?? ImmutableDictionary<LivePlanetGpuIdentity, float>.Empty,
                mapperStartMillisecondsOverrides ?? ImmutableDictionary<LivePlanetGpuIdentity, uint>.Empty)
                { Bridge = _nextBridgeFrame });
        if(previous?.Snapshot is EnbSceneFrame previousOwner)previousOwner.Release();
        if (!_embeddedScanner && Volatile.Read(ref _initialized))
            RequestFrame();
    }

    public void ClearFrame()
    {
        Volatile.Write(ref _dockAnchors, ImmutableDictionary<uint,EnbDockCameraAnchor>.Empty);
        Volatile.Write(ref _cameraCollisionFrame, null);
        _cameraQueryScenes.Clear();
        PublishInterfaceShake(Vector3.Zero, 0, 0);
        _nextBridgeFrame = null;
        var previous=Interlocked.Exchange(ref _pendingFrame,null);
        if(previous?.Snapshot is EnbSceneFrame owner)owner.Release();
        Volatile.Write(ref _presentedFrameToken, 0);
        Volatile.Write(ref _status, LiveWorldW3dStatus.Waiting);
        Volatile.Write(ref _influenceSkyPresentation, LiveInfluenceSkyPresentation.Inactive);
        Volatile.Write(ref _planetGpuPresentation, LivePlanetGpuPresentation.Waiting);
        _projectedObjectBounds.Release();
        Volatile.Write(ref _projectedObjectBounds, LiveCompletedRenderQueries.Empty);
        Volatile.Write(ref _cadence, LiveFrameCadenceStatus.Waiting);
        ResetCadenceCounters();
        if (!_embeddedScanner && Volatile.Read(ref _initialized))
            RequestFrame();
    }

    internal event Action? FrameRequested;
    internal event Action? PresentationStarted;
    internal event Action? PresentationCompleted;
    private EnbRenderViewport? _viewport;
    internal void SetViewport(EnbRenderViewport viewport) => _viewport = viewport;
    private void RequestFrame() => FrameRequested?.Invoke();

    internal void InitializeGl(EnbGlApi gl)
    {

        Volatile.Write(ref _graphicsDeviceStatus, $"GPU context: {gl.Renderer}; vendor {gl.Vendor}; version {gl.Version}");
        InitializeTerrainCapabilities(gl);
        _unlitBackend.Initialize(gl);
        _litBackend.Initialize(gl);
        _texturedBackend.Initialize(gl);
        // Frontend/tooltip draws never use special-material programs. Avoid
        // relinking that unused shader family whenever frontend ownership returns.
        if(!_tooltipOnly)_specialMaterialBackend.Initialize(gl);
        _backdropRenderer.Initialize(gl);
        if (_scannerRenderer is { } scanner)
        {
            scanner.InitializeGl(gl);
        }
        _stationShipRenderer?.InitializeGl(gl);
        _mapModelRenderer?.InitializeGl(gl);
        _mapPlanetRenderer?.InitializeGl(gl);
        _mapHelpRenderer?.InitializeGl(gl);
        _inventoryModelRenderer?.InitializeGl(gl);
        if (_enableAudio || EnableClientAudio)
            _audio ??= new LiveWorldOpenAlAudio { Speech = _speechScope };
        ResetCadenceCounters();
        Volatile.Write(ref _initialized, true);
    }

    internal string GraphicsDeviceStatus => Volatile.Read(ref _graphicsDeviceStatus);
    private string _graphicsDeviceStatus = "GPU context not initialized";

    private readonly EnbOpenGlScannerFramebuffer _sceneFramebuffer = new();
    private readonly EnbOpenGlBloom _bloom = new(); private readonly EnbOpenGlSceneSupersampling _supersampling = new();
    internal EnbEnhancedGraphicsOptions EnhancedGraphics {get;set;} = EnbEnhancedGraphicsOptions.Default;

    private EnbGlApi? _frameQueueContext;
    internal void RenderGl(EnbGlApi gl, int fb)
        => RenderGlCore(gl,fb,_viewport ?? throw new InvalidOperationException("Renderer viewport has not been supplied."));

    internal void RenderSurface(EnbGlApi gl,int fb,EnbRenderViewport surface)
        => RenderGlCore(gl,fb,surface);

    private void RenderGlCore(EnbGlApi gl,int fb,EnbRenderViewport surface)
    {
        using var benchmarkScope = FullClientBenchmarkMeter.Enter(_tooltipOnly ? "hud-overlays" : _embeddedScanner ? "scanner" : _useMainViewEnvironment ? "world" : "auxiliary", _useMainViewEnvironment && !_tooltipOnly && !_embeddedScanner);
        _texturedBackend.RetainBlendProcedure=RetainHudProcedures;
        _frameQueueContext = gl;
        EnbOpenGlFrameQueue.Scope enteredFrame;
        bool frameAccepted;
        using(FullClientBenchmarkMeter.Enter(_tooltipOnly ? "hud-frame-queue" : _embeddedScanner ? "scanner-frame-queue" : "world-frame-queue"))
            frameAccepted=EnbOpenGlFrameQueue.TryEnter(gl,out enteredFrame,_tooltipOnly?"hud":_embeddedScanner?"scanner":_useMainViewEnvironment?"world":"auxiliary");
        if(!frameAccepted)
        {
            PresentationCompleted?.Invoke();
            RequestFrame();
            return;
        }
        using var frameQueue = enteredFrame;
        if (_useMainViewEnvironment) PresentationStarted?.Invoke();
        // 00742DA3 publishes BFAE3C once before packet processing/render work.
        // Nested views on this render thread observe the same mapper epoch.
        PollFrozenInputExperiment();
        uint? previousClock = s_frameMapperClock;
        s_frameMapperClock ??= _frozenInputMode == 2 ? _frozenMapperClock : Volatile.Read(ref _pendingFrame)?.Movie is { } movie
            ? unchecked((uint)movie.Snapshot.ClientTime) : MapperClock();
        try
        {
            using(FullClientBenchmarkMeter.Enter(_tooltipOnly ? "hud-render-body" : _embeddedScanner ? "scanner-render-body" : "world-render-body"))
                RenderFrameCore(gl, fb,surface);
        }
        finally { s_frameMapperClock = previousClock; if(_useMainViewEnvironment && !_tooltipOnly && !_embeddedScanner && PresentedFrameToken!=0)FullClientBenchmarkMeter.RenderCompleted(); }
    }

    private void RenderFrameCore(EnbGlApi gl, int fb,EnbRenderViewport surface)
    {
        using (_tooltipOnly ? FullClientBenchmarkMeter.Enter("hud-options") : default)
            if (_retailAssetsAccessor() is { } graphicsAssets) ResolveGraphicsOptions(graphicsAssets);
        BeginDazzleFrame(ReadMapperClock());
        long renderStart = Stopwatch.GetTimestamp();
        long allocatedStart = GC.GetAllocatedBytesForCurrentThread();
        _lastFrameProfile = "world not rendered";
        _framePerformanceStages=null;
        _frameGpuCreates = 0;
        LiveFrame? anchoredFrame = Volatile.Read(ref _pendingFrame);
        LiveFrame? frame = SelectFrozenInputFrame(anchoredFrame, renderStart);
        if (frame is not null) _effectRslObjectRuntime.BeginFrame(frame.CurrentClientTime);
        _renderingMovie = frame?.Movie;
        double scaling = surface.RenderScale;
        int pixelWidth = surface.PixelWidth;
        int pixelHeight = surface.PixelHeight;

        int destinationFramebuffer=fb;
        bool previousHdr=gl.HighDynamicRange;bool previousAnisotropy=gl.SceneAnisotropicFiltering;gl.SceneAnisotropicFiltering=!_tooltipOnly&&!_embeddedScanner&&EnhancedGraphics.AnisotropicFilteringActive;
        try
        {
            if (_bridgeGpuAsset is not null && (frame?.Bridge is null || Volatile.Read(ref _stationInterior) is not null))
                DestroyBridgeResources(gl);
            // The host ANGLE surface can supply only D16. Own the scene's
            // D24 target; embedded scanners already own their D24 surface.
            if(!_embeddedScanner && !_tooltipOnly)
                {fb=_bloom.BeginScene(gl,_sceneFramebuffer,fb,pixelWidth,pixelHeight,EnhancedGraphics.HdrActive,out var failure);if(failure is not null)LastRenderFailure=failure;fb=_supersampling.Begin(gl,fb,pixelWidth,pixelHeight,EnhancedGraphics.SupersamplingActive,out var sampleFailure);if(sampleFailure is not null)LastRenderFailure=sampleFailure;}
            using (_tooltipOnly ? FullClientBenchmarkMeter.Enter("hud-target-clear") : default)
            {
            gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER, fb);
            gl.Viewport(0, 0, pixelWidth, pixelHeight);
            gl.DepthMask(1);
            gl.ClearColor(_clearColor.X, _clearColor.Y, _clearColor.Z, _clearColor.W);
            gl.ClearDepth(1f);
            // Clear every frame: an absent/rejected backdrop must not expose
            // retained colour from previous frames. Failed assets remain omitted.
            if (IsSpaceComposition(frame)) gl.ClearColor(0, 0, 0, 1);
            gl.Clear(GlDepthBufferBit | EnbGlConstants.GL_COLOR_BUFFER_BIT);
            }
            using (_tooltipOnly ? FullClientBenchmarkMeter.Enter("hud-audio-update") : default)
            {
            if (_audio is not null && _retailAssetsAccessor() is { } volumeAssets)
                _audio.DialogGain = volumeAssets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundDialog / 100f;
            if(EnableClientAudio)
            {
                // The HUD compositor may finish GL initialization before the
                // owning view enables client audio. It must still own a sound
                // instance when the first live scene or UI click is rendered.
                _audio ??= new LiveWorldOpenAlAudio { Speech = _speechScope };
                if (_retailAssetsAccessor() is { } initialVolumeAssets)
                    _audio.DialogGain = initialVolumeAssets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundDialog / 100f;
                FlushInteractionSounds();
                if (_retailAssetsAccessor() is {} audioAssets)
                {
                    float gain = audioAssets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundSfx / 100f;
                    if (Volatile.Read(ref _clientAudioScene) is {} audioScene)
                        _audio.UpdateClientSounds(audioScene, audioAssets, gain);
                    else
                        // Entry clicks must decode/play before the first network scene exists.
                        _audio.UpdateInteractionSounds(audioAssets, gain);
                }
            }
            else if (_audio is not null && (!_pendingInteractionSounds.IsEmpty || _audio.ClientSoundsNeedUpdate))
            {
                // The world owns chat UI cues as well as station/space audio.
                // Drain those cues without replaying the HUD client-event stream.
                FlushInteractionSounds();
                if (_retailAssetsAccessor() is {} audioAssets)
                    _audio.UpdateInteractionSounds(audioAssets,
                        audioAssets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundSfx / 100f);
            }
            }
            if (_tooltipOnly)
            {
                using (FullClientBenchmarkMeter.Enter("hud-map-prepare"))
                    PreparePendingMap(gl,fb,pixelWidth,pixelHeight,scaling);
                using (FullClientBenchmarkMeter.Enter("hud-animation-tails"))
                {
                    if (Volatile.Read(ref _frontendFrame) is {} frontend) RenderFrontend(gl, fb, pixelWidth, pixelHeight, frontend);
                    else if(EnableClientAudio)UpdateClientAnimationTails();
                }
                if (Volatile.Read(ref _loadingPresentation) is { } loading)
                {
                    RenderLoading(gl,fb,pixelWidth,pixelHeight,scaling,loading);
                    return;
                }
                if((DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Hud) && Volatile.Read(ref _frontendFrame) is null)return;
                RenderWithInterfaceShake(gl, fb, pixelWidth, pixelHeight, target =>
                {
                    using (FullClientBenchmarkMeter.Enter("hud-verbs-draw"))
                        RenderVerbs(gl, target, pixelWidth, pixelHeight, scaling);
                    using (FullClientBenchmarkMeter.Enter("hud-tooltip-draw"))
                        RenderTooltip(gl, target, pixelWidth, pixelHeight, scaling);
                });
                return;
            }
            if(!EnhancedGraphics.BloomActive && !EnhancedGraphics.HdrActive && (_bloom.IsAllocated || _bloom.Failure is not null))_bloom.Reset(gl);
            if (Volatile.Read(ref _stationInterior) is { } station)
            {
                PublishInterfaceShake(Vector3.Zero, 0, 0);
                if (frame is not null) PauseShakeFrame(frame.Snapshot, frame.CurrentClientTime);
                RenderStation(gl, fb, pixelWidth, pixelHeight, station, scaling);
                if (_useMainViewEnvironment && !_embeddedScanner) CompleteCaptureLightingFrame();
                return;
            }
            if (_stationGpuAsset is not null || _stationGpuContextLost is not null || _stationResidentRooms.Count>0 || _stationResidentFixtures.Count>0) DestroyStationResources(gl);
            _stationClockAsset = null;
            if (frame is null)
            {
                PublishInterfaceShake(Vector3.Zero, 0, 0);
                ClearShipWarpAnimations();
                _audio?.StopAll();
                Volatile.Write(ref _presentedFrameToken, 0);
                return;
            }

            // A compositor resize can precede the scene pump. Do not stretch an old
            // camera/scanner layout into a new framebuffer and acknowledge it as ready.
            if (frame.Camera.Viewport.PixelWidth != pixelWidth || frame.Camera.Viewport.PixelHeight != pixelHeight)
            {
                Volatile.Write(ref _presentedFrameToken, 0);
                return;
            }
            var selectedCamera = frame.Camera;
            frame = PrepareShakeFrame(frame, pixelWidth, pixelHeight);
            if (_frozenInputMode != 0) frame = frame with { Camera = selectedCamera };
            // A movie may intentionally show only its fade/letterbox, or no models.
            // The gameplay loading gate requires ordinary draws; movie capture must
            // still acknowledge a completed blank frame so its script can advance.
            if (!RenderFrame(gl, fb, pixelWidth, pixelHeight, frame, includeBridge:true) && frame.Movie is null)
            {
                Volatile.Write(ref _presentedFrameToken, 0);
                return;
            }
            gl.SceneAnisotropicFiltering=false;fb=_supersampling.Complete(gl,fb);if(_useMainViewEnvironment && !_embeddedScanner && _bloom.CompleteScene(gl,fb,pixelWidth,pixelHeight,EnhancedGraphics.BloomActive) is {} postFailure)LastRenderFailure=postFailure;
            if (!InterfaceHidden) RenderWithInterfaceShake(gl, fb, pixelWidth, pixelHeight, target =>
            {
                RenderScanner(gl, target, pixelWidth, pixelHeight, scaling, frame);
                if(!DeveloperRenderDiagnostics.Available || DeveloperRenderDiagnostics.Hud) RenderCockpit(gl, target, pixelWidth, pixelHeight, scaling, frame.CurrentClientTime);
                RenderDamageText(gl, target, pixelWidth, pixelHeight, frame);
            });
            // Keep the existing loading cover until the current terrain result
            // has been published (or its local failure has been handled).
            Volatile.Write(ref _presentedFrameToken, _terrainPreparation is null ? frame.PresentationToken : 0);
            if (_useMainViewEnvironment && !_embeddedScanner) CompleteCaptureLightingFrame();
        }
        catch (Exception ex)
        {
            CancelFrontendEntryAudio();
            _audio?.StopAll();
            LastRenderFailure = ex.ToString();
            Volatile.Write(ref _presentedFrameToken, 0);
            Volatile.Write(ref _influenceSkyPresentation, LiveInfluenceSkyPresentation.Inactive);
            Volatile.Write(ref _planetGpuPresentation, LivePlanetGpuPresentation.Waiting);
            Volatile.Write(ref _status, new LiveWorldW3dStatus(
                $"R6Q live W3D: render failure · {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            _supersampling.Complete(gl,fb);gl.HighDynamicRange=previousHdr;gl.SceneAnisotropicFiltering=previousAnisotropy;
            using (_tooltipOnly ? FullClientBenchmarkMeter.Enter("hud-audio-arbitration") : default)
                _audio?.AdvanceVoiceArbitration();
            _setOrbitalDepthRange?.Invoke(0, 1);
            if(fb!=destinationFramebuffer)_sceneFramebuffer.End(gl,destinationFramebuffer,0,0);
            long renderEnd = Stopwatch.GetTimestamp();
            int gen0 = GC.CollectionCount(0);
            _performanceAllocation=GC.GetAllocatedBytesForCurrentThread()-allocatedStart;
            _performanceGc=gen0-_lastFrameGen0Collections;
            _performanceGap=_lastRenderTimestamp==0?0:Math.Max(0,Stopwatch.GetElapsedTime(_lastRenderTimestamp,renderStart).TotalMilliseconds);
            if (!_tooltipOnly)
                _stallHistory.Record(new(renderEnd / (double)Stopwatch.Frequency,
                    Stopwatch.GetElapsedTime(renderStart, renderEnd).TotalMilliseconds,
                    _lastRenderTimestamp == 0 ? 0 : Math.Max(0, Stopwatch.GetElapsedTime(_lastRenderTimestamp, renderStart).TotalMilliseconds),
                    GC.GetAllocatedBytesForCurrentThread() - allocatedStart,
                    gen0 - _lastFrameGen0Collections, _frameGpuCreates, frame?.CurrentClientTime, _lastFrameProfile)
                {
                    SceneAgeAtCallbackMs = _lastRenderTimestamp == 0 || anchoredFrame is null ? null : Math.Max(0,
                        Stopwatch.GetElapsedTime(anchoredFrame.ClientTimeAnchorTimestamp, renderStart).TotalMilliseconds),
                });
            _lastFrameGen0Collections = gen0;
            RecordCadence(renderStart, renderEnd, anchoredFrame);
            FrameTimingObserver?.Invoke(renderStart, renderEnd);
            if (Volatile.Read(ref _initialized) &&
                (_continuousPresentation && (anchoredFrame is not null || Volatile.Read(ref _stationInterior) is not null) ||
                 // The frontend supplies animated frames from its 16 ms UI timer.
                 // A click voice must not start an unrestricted second render loop.
                 Volatile.Read(ref _frontendFrame) is null &&
                 _audio?.ClientSoundsNeedUpdate==true))
                RequestFrame();
            if (_useMainViewEnvironment) PresentationCompleted?.Invoke();
        }
    }

    // Optional offline observer measures real compositor callbacks, independently
    // of swap-chain presents. Unset in normal gameplay.
    internal Action<long, long>? FrameTimingObserver { get; set; }

    internal void DeinitializeGl(EnbGlApi gl) => ReleaseGpuContext(gl);
    /// <summary>Presentation moves to another graphics card: every GL object goes, scene state and audio stay, and
    /// <see cref="InitializeGl"/> on the new context follows.</summary>
    internal void SuspendGl(EnbGlApi gl) => ReleaseGpuContext(gl, keepAudio: true);

    // Null means the driver already destroyed this context. Retain scene state,
    // discard handles and delegates, and never call GL against the lost device.
    private void ReleaseGpuContext(EnbGlApi? gl, bool keepAudio = false)
    {
        if (_frameQueueContext is { } queueContext) EnbOpenGlFrameQueue.Release(queueContext, gl is null);
        _frameQueueContext = null;
        ReleaseHudProcedures();
        ClearDazzleQueryTasks();
        if (!keepAudio) CancelFrontendEntryAudio();
        _terrainPreparationCancellation?.Cancel();
        _terrainPreparation=null;_terrainKey=null;
        _terrain=null;_terrainDisplacement=null;
        DestroyFrontend(gl);
        DestroyBridgeResources(gl);
        _bloom.Reset(gl);
        _supersampling.Deinitialize(gl);_sceneFramebuffer.Deinitialize(gl);
        _interfaceShakeFramebuffer.Deinitialize(gl);
        DeinitializeDazzles(gl);
        _setOrbitalDepthRange = null;
        DestroyChat(gl);
        DestroyLoadingResources(gl);
        _stationGpuContextLost = _stationClockAsset;
        DestroyStationResources(gl, preserveScene:true);
        DestroyCockpitResources(gl);
        DestroyDamageText(gl);
        DestroyMapResources(gl, preserveScene:true);
        DestroyGaugeResources(gl);
        DestroyVerbResources(gl, preserveScene:true);
        _verbFramebuffer.Deinitialize(gl);
        _gaugeFramebuffer.Deinitialize(gl);
        DestroyTooltipResources(gl);
        _tooltipTitleRenderer.Deinitialize(gl);
        _tooltipDetailRenderer.Deinitialize(gl);
        _tooltipThirdRenderer.Deinitialize(gl);
        foreach (var line in _tooltipLines) line.Deinitialize(gl);
        _tooltipFramebuffer.Deinitialize(gl);
        if (_scannerRenderer is { } scanner)
        {
            scanner.ReleaseGpuContext(gl);
            _scannerFramebuffer.Deinitialize(gl);
        }
        Volatile.Write(ref _initialized, false);
        ResetCadenceCounters();
        Volatile.Write(ref _cadence, LiveFrameCadenceStatus.Waiting);
        // Graphics loss does not destroy the independent audio device/voices.
        // Normal surface teardown retains its existing audio shutdown behavior.
        if (gl is not null && !keepAudio)
        {
            if (_audio is not null) _lastClientAudioStatus = _audio.ClientSoundStatus;
            _audio?.Dispose();
            _audio = null;
        }
        DestroyAllGpuResources(gl,preserveScene:true);
        // Retail 00924890/00921C50 release device resources, not scene-owned
        // particle buffers. Keep owners, clocks, random streams and detached
        // tails intact; asset-session replacement below owns their teardown.
        _backdropRenderer.Deinitialize(gl);
        _unlitBackend.Deinitialize(gl);
        _litBackend.Deinitialize(gl);
        _texturedBackend.Deinitialize(gl);
        _specialMaterialBackend.Deinitialize(gl);
    }

    private LiveFrame? _modernScannerFrame;
    private bool _modernTargetPass;
    /// <summary>Modern UI: the scanner's live target model, drawn by the HUD pass over the target frame's portrait backing.</summary>
    internal void RenderModernTarget(EnbGlApi gl, int fb, int width, int height, double scaling)
    {
        if (!ScannerChromeHidden || _modernScannerFrame is not { } frame) return;
        _modernTargetPass = true;
        try { RenderScanner(gl, fb, width, height, scaling, frame); }
        finally { _modernTargetPass = false; }
    }

    private void RenderScanner(EnbGlApi gl, int fb, int width, int height, double scaling, LiveFrame worldFrame)
    {
        if(UiLayout?.Visible(EnbUiGroup.Scanner)==false||(DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Scanner))return;
        using var benchmarkScope=FullClientBenchmarkMeter.Enter("scanner");
        if (_scannerRenderer is not { } scanner ||
            (_frozenInputMode != 0 ? _frozenScannerPresentation : Volatile.Read(ref _scannerPresentation)) is not { } presentation)
            return;
        var panel = presentation.PanelBounds;
        // Authored chrome blends directly against the world in the GPU pass.
        // Modern UI keeps only the model, inside its target-frame portrait.
        if(!ScannerChromeHidden && (!DeveloperRenderDiagnostics.Available || DeveloperRenderDiagnostics.Hud))
            RenderGauges(gl, fb, width, height, scaling, panel, worldFrame.CurrentClientTime);
        try
        {
        // Modern UI draws the model later, in the HUD pass, so it lies over the portrait backing.
        if (ScannerChromeHidden && !_modernTargetPass) { _modernScannerFrame = worldFrame; return; }
        _modernScannerFrame = null;
        if ((_frozenInputMode != 0 ? _frozenScanner : Volatile.Read(ref scanner._pendingFrame)) is not { } pending)
            return;
        // Never combine a newly staged target with an older in-flight world.
        if (pending.Snapshot.Version != worldFrame.Snapshot.Version)
            return;
        var bounds = presentation.ModelBounds;
        int x = (int)Math.Round(bounds.X * scaling);
        int y = height - (int)Math.Round(bounds.Bottom * scaling);
        int w = pending.Camera.Viewport.PixelWidth, h = pending.Camera.Viewport.PixelHeight;
        if (x < 0 || y < 0 || w < 1 || h < 1 || x + w > width || y + h > height)
            return;
        var planet = PlanetGpuPresentation;
        LiveFrame frame = pending with
        {
            // 005C2E60 binds the target RenderObj to the scanner proxy;
            // 005C25C0 inserts that proxy into its private scene. Independent
            // world Effect objects are not part of that RenderObj. Replaying
            // their graphs here also mistakes absent scene peers for destroyed
            // endpoints. Model attachments and shared RSL state remain below.
            Snapshot = PrepareScannerScene(pending.Snapshot),
            CurrentClientTime = worldFrame.CurrentClientTime,
            DayNightPhaseOverrides = planet.DayNightPhasesByRenderIdentity,
            MapperStartMillisecondsOverrides = planet.MapperStartMillisecondsByRenderIdentity,
            RslObjectUpdateOverrides = _frameRslObjectUpdates.ToDictionary(p=>p.Key,p=>p.Value),
            RslPassOwnerOverrides = _frameRslPassOwners.ToDictionary(p=>p.Key,p=>p.Value),
        };
        var scannerMaterials = scanner._shipMaterials;
        var scannerOverbright = scanner._overbrightMaterials;
        var previousMaterialOwner = scanner._sharedMaterialOwner;
        scanner._shipMaterials = _shipMaterials;
        scanner._overbrightMaterials = _overbrightMaterials;
        scanner._sharedMaterialOwner = this;
        try
        {
            int scannerFb = _scannerFramebuffer.Begin(gl, fb, x, y, w, h);
            scanner.RenderFrame(gl, scannerFb, w, h, frame);
            _scannerFramebuffer.End(gl, fb, x, y);
            Volatile.Write(ref scanner._presentedFrameToken, frame.PresentationToken);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref scanner._presentedFrameToken, 0);
            Volatile.Write(ref scanner._status, new LiveWorldW3dStatus($"Direct scanner failure: {ex}"));
            gl.BindFramebuffer(EnbGlConstants.GL_FRAMEBUFFER, fb);
        }
        finally
        {
            scanner._shipMaterials = scannerMaterials;
            scanner._overbrightMaterials = scannerOverbright;
            scanner._sharedMaterialOwner = previousMaterialOwner;
        }
        gl.Viewport(0, 0, width, height);
        }
        finally
        {
            // 0060E6A0 gives the attached interruption resource layer 8,
            // above the scanner's target presentation.
            // Modern UI shows the target's skill activation under its target frame instead.
            if (!_modernTargetPass && !ScannerChromeHidden) RenderGaugeLayer(gl,fb,width,height,scaling,panel,worldFrame.CurrentClientTime,true);
        }
    }

    private void ResetCadenceCounters()
    {
        _cadenceAllocatedBytes = 0;
        Volatile.Write(ref _performance,null);
        Array.Clear(_performanceStageTotals);_performanceStageFrames=0;
        _stallHistory.Clear();
        _lastFrameGen0Collections = GC.CollectionCount(0);
        _cadenceIntervals.Clear();
        _lastRenderedAnchorTimestamp = 0;
        _cadenceRepeatedAnchors = 0;
        _cadenceWindowStartTimestamp = 0;
        _lastRenderTimestamp = 0;
        _cadenceWindowFrames = 0;
        _cadenceWindowCpuMilliseconds = 0.0;
        _cadenceWindowWorstCpuMilliseconds = 0.0;
    }

    private void RecordCadence(long renderStart, long renderEnd, LiveFrame? anchoredFrame)
    {
        double cpuMilliseconds = Math.Max(0.0, (renderEnd - renderStart) * 1000.0 / Stopwatch.Frequency);
        long previousRender = _lastRenderTimestamp;
        _lastRenderTimestamp = renderEnd;
        if (previousRender != 0)
            _cadenceIntervals.Add(Stopwatch.GetElapsedTime(previousRender, renderEnd).TotalMilliseconds);
        if (anchoredFrame is not null)
        {
            if (_lastRenderedAnchorTimestamp == anchoredFrame.ClientTimeAnchorTimestamp) _cadenceRepeatedAnchors++;
            _lastRenderedAnchorTimestamp = anchoredFrame.ClientTimeAnchorTimestamp;
        }
        if (_cadenceWindowStartTimestamp == 0)
            _cadenceWindowStartTimestamp = renderStart;

        _cadenceWindowFrames++;
        _cadenceAllocatedBytes += _performanceAllocation;
        if(_framePerformanceStages is {} stages)
        {
            if(_performanceStageScene!=_framePerformanceScene){Array.Clear(_performanceStageTotals);_performanceStageFrames=0;_performanceStageScene=_framePerformanceScene;}
            for(int i=0;i<stages.Length;i++)_performanceStageTotals[i]+=stages[i];
            _performanceStageFrames++;
        }
        _cadenceWindowCpuMilliseconds += cpuMilliseconds;
        _cadenceWindowWorstCpuMilliseconds = Math.Max(_cadenceWindowWorstCpuMilliseconds, cpuMilliseconds);

        long windowTicks = renderEnd - _cadenceWindowStartTimestamp;
        if (windowTicks < Stopwatch.Frequency)
            return;

        double windowSeconds = windowTicks / (double)Stopwatch.Frequency;
        double framesPerSecond = _cadenceWindowFrames / windowSeconds;
        double averageCpuMilliseconds = _cadenceWindowFrames == 0
            ? 0.0
            : _cadenceWindowCpuMilliseconds / _cadenceWindowFrames;
        double intervalMilliseconds = previousRender == 0
            ? 0.0
            : Math.Max(0.0, (renderEnd - previousRender) * 1000.0 / Stopwatch.Frequency);
        double anchorAgeMilliseconds = anchoredFrame is null
            ? 0.0
            : Math.Max(0.0, (renderEnd - anchoredFrame.ClientTimeAnchorTimestamp) * 1000.0 / Stopwatch.Frequency);

        var chronologicalIntervals=_cadenceIntervals.ToArray();
        Volatile.Write(ref _cadence, new LiveFrameCadenceStatus(
            $"frame loop: R6_CLOSEOUT_CONTINUOUS_PRESENTATION_ACTIVE · render {framesPerSecond:0.0}Hz · " +
            $"cpu {averageCpuMilliseconds:0.00}ms avg/{_cadenceWindowWorstCpuMilliseconds:0.00}ms worst · " +
            $"interval {intervalMilliseconds:0.00}ms · interval p95/max {CadencePercentile(.95):0.00}/{CadencePercentile(1):0.00}ms · " +
            $"reused scene {_cadenceRepeatedAnchors}/{_cadenceWindowFrames} · snapshot-anchor {anchorAgeMilliseconds:0}ms · allocation-window {_cadenceAllocatedBytes} bytes/{_cadenceWindowFrames} frames\n" +
            _stallHistory.Describe(renderEnd / (double)Stopwatch.Frequency)));

        Volatile.Write(ref _performance,new(framesPerSecond,averageCpuMilliseconds,
            _cadenceWindowWorstCpuMilliseconds,CadencePercentile(.95),CadencePercentile(1),_performanceGap,
            _performanceAllocation/1024d,_performanceGc,_frameGpuCreates,
            _performanceStageFrames==0?[]:_performanceStageTotals.Select(value=>value/_performanceStageFrames).ToArray(),
            chronologicalIntervals,_stallHistory.Worst(renderEnd/(double)Stopwatch.Frequency),_performanceStageScene));

        _cadenceIntervals.Clear();
        _cadenceRepeatedAnchors = 0;
        Array.Clear(_performanceStageTotals);_performanceStageFrames=0;
        _cadenceWindowStartTimestamp = renderEnd;
        _cadenceAllocatedBytes = 0;
        _cadenceWindowFrames = 0;
        _cadenceWindowCpuMilliseconds = 0.0;
        _cadenceWindowWorstCpuMilliseconds = 0.0;
    }

    private double CadencePercentile(double fraction)
    {
        if (_cadenceIntervals.Count == 0) return 0;
        _cadenceIntervals.Sort();
        return _cadenceIntervals[Math.Min(_cadenceIntervals.Count - 1, (int)Math.Ceiling(_cadenceIntervals.Count * fraction) - 1)];
    }

    private bool RenderFrame(
        EnbGlApi gl,
        int fb,
        int pixelWidth,
        int pixelHeight,
        LiveFrame frame, bool includeBridge=false)
    {
        using var worldStages=FullClientBenchmarkMeter.BeginStages(
            FullClientBenchmarkMeter.WorldStagesEnabled && _useMainViewEnvironment && !_embeddedScanner,
            "world-phase.ownership");
        using var retainedMeshFrame = new MeshFrameScope(this);
        using var particleStreamFrame = new ParticleStreamFrameScope(this);
        using var packetTasks = RentPacketTasks();
        BeginOwnedSceneFrame(frame);
        BeginRigidOccurrences(frame);
        BeginFrameMembership(frame);
        using var reuseFrame = EnbWorkReuseDiagnostics.Begin(this, _useMainViewEnvironment ? "world" : "auxiliary", frame.CurrentClientTime);
        if (reuseFrame is not null) EnbWorkReuseDiagnostics.Context(new { pixelWidth, pixelHeight,
            Camera = new[] {frame.Camera.Position.X,frame.Camera.Position.Y,frame.Camera.Position.Z,
                frame.Camera.WorldOrientation.X,frame.Camera.WorldOrientation.Y,frame.Camera.WorldOrientation.Z,frame.Camera.WorldOrientation.W},
            Objects=SceneObjects(frame.Snapshot).Count, Effects=frame.Snapshot.Effects.Length,
            InputMode=_frozenInputMode, InputToken=_frozenInputToken });
        ObserveFrozenInputBoundary(frame);
        long profileStart = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.setup");
        bool detailedDiagnosticsDue = DetailedDiagnosticsDue(profileStart);
        _backdropRenderer.BeginWorldStatistics();
        _frameDazzles.Clear();
        _frameInfluenceEvaluations.Clear();
        _dazzleCollisionRoots.Clear();
        _effectDazzleCollision.Clear();_effectEmitterCollision.Clear();_jetBufferCollision.Clear();
        _frontendDazzleCollisionPrepared=false;_frontendDazzleCollision.Clear();
        _frameGpuCreateEvidence.Clear();
        _frameGpuEvictions = 0;
        _frameGpuEvictedBytes = 0;
        bool spaceComposition = IsSpaceComposition(frame);
        EnbRetailBaseAssetResolver? resolver = _baseAssetResolverAccessor();
        EnbRetailAssetBundle? retailAssets = _retailAssetsAccessor();
        if (resolver is null || retailAssets is null)
        {
            ClearShipWarpAnimations();
            _audio?.StopAll();
            Volatile.Write(ref _influenceSkyPresentation, LiveInfluenceSkyPresentation.Inactive);
            Volatile.Write(ref _status, new LiveWorldW3dStatus("R6Q live W3D: installed retail assets are not loaded"));
            return false;
        }

        if (!ReferenceEquals(resolver, _lastResolver) || !ReferenceEquals(retailAssets, _lastRetailAssets) || _textureRevision!=retailAssets.TextureRevision)
        {
            _lastResolver = resolver;
            _lastRetailAssets = retailAssets;
            _assetGeneration++;
            DestroyAllGpuResources(gl);
            // Replacing the retail asset/resolver pair reconstructs the
            // RenderObjs themselves. Texture changes retire the shared registry,
            // so lagging views must skip unregistering its old names below.
            ClearOwnedPlanetMappers();_planetMapperStateOwnership.Clear();
            DeinitializeDazzles(gl);
            _dazzleHistory.Clear();_dazzleCreationClocks.Clear();_compatDazzleRoots.Clear();_compatDazzleMembers.Clear();_ownedDazzleEpoch++;
            _baseAssets.Clear();
            _ordinaryBaseFactories.Clear();
            InvalidateOwnedSceneAssets();
            _fixedShipParticleFactories.Clear();
            _legoParticleFactories.Clear();
            _legoBaseFactories.Clear();ClearLegoFactoryBindings();
            _legoControllers.Clear();
            _legoPhysicalOwners.Clear();
            _legoSourceInstances.Clear();
            ClearLegoRslOwners();
            InvalidateLegoRslIndex();
            _influenceStates.Clear();
            _influenceHelperFactories.Clear();
            _influenceParticleRoots.Clear();
            ClearDebris();
            foreach(var cachedRoot in _cpuRoots.Values)cachedRoot.SpecialMaterial?.Dispose();
            _cpuRoots.Clear();
            if(_textureRevision==retailAssets.TextureRevision)foreach(var name in _configuredDirectMeshVariants.Keys.Concat(_configuredParticleVariants.Keys).Concat(_configuredPrimitiveVariants.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                _prototypeResources.RemovePrototype(name);
            _configuredDirectMeshRoots.Clear();
            _directConfigurations.Clear();
            foreach(var assembly in _configuredEffectAssemblies.Values)
                foreach(var model in assembly.Models)model.Dispose();
            _configuredEffectAssemblies.Clear();
            foreach(var model in _configuredDirectMeshModels.Values.Distinct())model.Dispose();
            _configuredDirectMeshModels.Clear();
            _rslConfiguredRoots.Clear();_compatRslConfiguredRoots.Clear();_rslFactoryPrepared.Clear();_rslFactoryDiagnostic=null;
            _rslParticleTemplates.Clear();_configuredParticleVariants.Clear();
            _configuredPrimitiveVariants.Clear();
            _configuredDirectMeshVariants.Clear();
            foreach(var special in _specialCompositeInstances.Values)special.Dispose();
            _specialCompositeInstances.Clear();
            _specialCompositeUsed.Clear();
            _specialDeviceLights=null;
            _worldSpheres.Clear();
            _worldSphereObserved.Clear();
            _worldSphereRslUpdates.Clear();
            _worldRings.Clear();
            _worldRingObserved.Clear();
            _worldRingRslUpdates.Clear();
            _harvestColors.Clear();
            _particleEmitterStartTimes.Clear();
            _ordinaryParticleBirths.Clear();
            _shipExhaustInstances.Clear();
            _exhaustCallbacks.Clear();
            _aggregateLines.Clear();
            _retainedEmitterCallbacks.Clear();_retainedEmitters.Clear();_retiringEmitterBuffers.Clear();_dazzleRetainedBuffers.Clear();_discardedParticleFactories.Clear();
            ResetPhysicalFactoryMembership();
            _removedRetainedEmitters.Clear();ClearCompositeMembership();_retainedCompositeFrameRoots.Clear();
            _retainedEmitterRslUpdates.Clear();
            ClearRetainedWrapperAttachmentHistory();
            _aggregateLineBindings.Clear();
            _nameDecalTextures.Clear();
            _hlodLevels.Clear();_physicalHlodLevels.Clear();
            _staticHlodBounds.Clear();
            _postTransformHlodBounds.Clear();
            _hlodCompiledCosts.Clear();
            _rigidOccurrences.Clear();
            _externalHierarchies.Clear();
            _hlodPrototypeEvidence.Clear();
            _hAnimFamilyCensuses.Clear();
            ClearRslAuxiliaryOwnership();_rslPacketNextOnceTransitions.Clear();
            _rslAutomaticNextTransitions.Clear();
            _rslSoundCompletions.Clear();
            ClearRslSoundSelections();
            _rslLinkedStateActivations.Clear();
            _effectRslScheduler.Clear();
            ClearEffectVisuals();
            _effectRslObjectRuntime.Clear();
            foreach(var retiredScene in _legoScenes.Values)ReleaseLegoSceneDependencies(retiredScene);
            ClearLegoScenes();
            ClearOrdinaryRslCallbacks();
            _ordinaryRslCallbackMembers.Clear();
            _hAnimRegistry.Clear();
            if (_sharedMaterialOwner is null)
            {
                _overbrightMaterials.Clear();
                _shipMaterials.Clear();
            }
            _indexedSubmissions.Clear();
            _rslObjectPrototypes.Clear();
            ClearMeshAppearances();
            ClearRslPassOwners();
            ClearRslBoneOwners();
            ClearEffectiveActions();
            _capturedHlodResearchResources.Clear();
            _orbitalScenes.Clear();
            _orbitalFileFactories.Clear();_orbitalFactoryScene=null;_orbitalFactoryLoading=false;
            _mainViewLightSectors.Clear();
            _sceneFogData = null;
            _loadSceneSplines.Clear();
            _currentSceneSplines.Clear();
            _sceneFogDataFailure = null;
            _environmentAtlasReflectionTexture = null;
            _environmentAtlasTextureReduction = -1;
            _environmentAtlasSet = null;
            _environmentAtlasDiagnostic = null;
            _environmentAtlasAttempted = false;
            _audio?.Reset();
        }

        EnsureSwatchCatalog(retailAssets);_textureRevision=retailAssets.TextureRevision;
        if (frame.Movie is { } movieResources)
            foreach (var entry in movieResources.Visuals)
            {
                int selector = unchecked(int.MinValue + (int)entry.Key);
                var source = entry.Value.Source;
                _baseAssets[selector] = new(selector, EnbBaseAssetResolutionStatus.W3d,
                    new(selector, EnbBaseAssetDefinitionKind.FileName, false, File: source.LogicalFileName, Name: source.RequestedObjectName), source);
            }
        EnbRetailPlayerOptionsSelection playerOptions = retailAssets.ResolvePlayerOptions(_fixedSessionIdAccessor());
        EnbRenderDetailRuntimeSettings detailSettings = ResolveGraphicsOptions(retailAssets);
        bool anisotropic = EnbOpenGlStage0TextureBackend.SupportsAnisotropicFiltering(gl.ContextInfo.Extensions);
        var textureCapabilities = new EnbRenderTextureBackendCapabilities(anisotropic);
        _backdropRenderer.ConfigureStarTextures(gl, retailAssets, detailSettings.Texture);
        if ((!DeveloperRenderDiagnostics.Available || DeveloperRenderDiagnostics.Backdrop) && frame.Movie is null && !spaceComposition)
            _backdropRenderer.Render(gl, fb, pixelWidth, pixelHeight, frame.Backdrop, frame.Camera);
        bool sceneEnvironmentResolved;
        EnbRetailOrbitalFogEvaluation fogEvaluation;
        EnbRetailMainViewLightingEvaluation? mainViewLighting;
        bool fogResolved;
        string? fogDiagnostic;
        if (_useMainViewEnvironment)
        {
            sceneEnvironmentResolved = TryResolveLiveSceneEnvironment(
                frame,
                resolver,
                retailAssets,
                out fogEvaluation,
                out mainViewLighting,
                out fogResolved,
                out fogDiagnostic);
        }
        else
        {
            // The active MFD scanner owns a separate SimpleScene (005C2000),
            // not the old RObj2Texture scene. Sector fog and the four world
            // directional lights are not members of this scene.
            if (_embeddedScanner && frame.Snapshot.Environment is { } scannerEnvironment)
            {
                if (!_mainViewLightSectors.TryGetValue(scannerEnvironment.SectorNum, out var authored))
                {
                    authored = retailAssets.Paths.TryResolveMainViewLightSectorIniPath(
                        scannerEnvironment.SectorNum, out string lightPath)
                        ? EnbRetailMainViewLightSectorDefinition.Load(lightPath)
                        : EnbRetailMainViewLightSectorDefinition.Empty;
                    _mainViewLightSectors[scannerEnvironment.SectorNum] = authored;
                }
                // 005C25C0 -> 004F50D0(0) copies OrbitalManager entry zero
                // (KEY RGBA at +88), without MainView's override threshold.
                _scannerLightingScene.AdoptMetadata(frame.Snapshot);
                _scannerLightingScene.Lighting=frame.Snapshot.Lighting with
                {
                    Lights=frame.Snapshot.Lighting.Lights.Select(light =>
                        light with {Diffuse=authored.Key.Rgb*authored.Key.Intensity}).ToImmutableArray(),
                };
                frame=frame with {Snapshot=_scannerLightingScene};
            }
            sceneEnvironmentResolved = false;
            fogEvaluation = new EnbRetailOrbitalFogEvaluation(
                new EnbW3dSceneFogState(false, 0u, 0f, 1f),
                Vector3.Zero,
                frame.Snapshot.Lighting.GlobalAmbientRgb24,
                0,
                0,
                0);
            mainViewLighting = null;
            fogResolved = true;
            fogDiagnostic = "Direct MFD scanner scene";
        }
        _currentOrbitalEvaluation = sceneEnvironmentResolved ? fogEvaluation : default;
        _currentOrbitalEvaluationResolved = sceneEnvironmentResolved;
        EnbW3dSceneFogState sceneFog = fogResolved
            ? fogEvaluation.SceneFog
            : new EnbW3dSceneFogState(false, 0u, 0f, 1f);
        if (frame.Movie is { } movieFog) sceneFog = movieFog.Fog;
        if (_useMainViewEnvironment && frame.Snapshot.Environment is { PresentationMode: 1, SwapBackdrop: false } && fogResolved)
        {
            // 0050E6E0 starts black; 00510D40 adds fog RGB after the orbitals.
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(EnbGlConstants.GL_COLOR_BUFFER_BIT);
        }
        uint orbitalAmbientRgb24 = sceneEnvironmentResolved
            ? fogEvaluation.GlobalAmbientRgb24
            : frame.Snapshot.Lighting.GlobalAmbientRgb24;
        uint effectiveAmbientRgb24 = mainViewLighting?.GlobalAmbientRgb24 ?? orbitalAmbientRgb24;
        _bridgeSpecialAmbient=mainViewLighting?.SpecialMaterialAmbient??fogEvaluation.SpecialMaterialAmbient??
            new Vector3((effectiveAmbientRgb24>>16&255)/255f,(effectiveAmbientRgb24>>8&255)/255f,(effectiveAmbientRgb24&255)/255f);
        // Bridge owns copies of MainView's four directional lights, not server point lights.
        if (frame.Bridge is not null)
            _bridgeLights = EnbW3dLightEnvironment.CreateCurrentRetail(effectiveAmbientRgb24,
                mainViewLighting?.DirectionalLights ?? []);
        EnbW3dLightEnvironment lightEnvironment = _customizationLightEnvironment ?? BuildLiveLightEnvironment(
            frame.Snapshot.Lighting,
            effectiveAmbientRgb24,
            mainViewLighting);
        if (frame.Movie is { } movieLights) lightEnvironment = movieLights.Lighting;
        if (_useMainViewEnvironment && fogResolved && EnbRetailMainViewLightingEvaluator.EvaluateOrbitalRig(
                frame.Snapshot.Environment?.PresentationMode, effectiveAmbientRgb24, fogEvaluation) is { } orbitalLights)
            lightEnvironment = orbitalLights;
        _specialSceneAmbient=_bridgeSpecialAmbient;
        // The existing OpenGL fog shader computes Euclidean camera range, so it
        // implements the current-retail range-fog-capable branch.
        // This is a capability declaration for EnbNext's backend, not a
        // claim about whichever D3D capability bit a particular retail GPU used.
        EnbW3dFogPipelineSemantics fogPipeline =
            EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(true);
        long profileSetupEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.rsl-update");

        EnbCurrentRetailEffectRslSchedulerUpdate? effectRslSchedulerUpdate = null;
        PrepareShipWarpAnimations(frame.Snapshot, retailAssets, frame.CurrentClientTime);
        IEnbRenderObject? radarObserver = _ownedSceneFrame?_localSceneOwner:SceneObjects(frame.Snapshot).FirstOrDefault(static obj => obj.IsLocalShip);
        var rslRuntimeUpdates = _ownedSceneFrame?_ownedRslPublications:new OwnerPublicationMap<EnbRslObjectState>();
        rslRuntimeUpdates.BeginFrame();
        var callbackUpdates = new Dictionary<(ulong Identity, ulong Generation), Action>();
        var effectRootBuilders = new Dictionary<ulong,Action>();
        var effectSoundCollectors = new List<Action>();
        var rslPassOwners = _ownedSceneFrame?_ownedPassPublications:new OwnerPublicationMap<EnbCurrentRetailRslEffectPassLibrary>();
        rslPassOwners.BeginFrame();
        _frameRslPassOwners = rslPassOwners;
        _frameRslObjectUpdates = rslRuntimeUpdates;
        var liveRslRenderIdentities=_liveRslIdentities;liveRslRenderIdentities.BeginFrame(_ownedSceneFrame);
        var liveRslOwners=_liveRslGenerations;liveRslOwners.BeginFrame(_ownedSceneFrame);
        var consumedEffectEventSequences = new HashSet<ulong>();
        _rslInvocationCounts=default;
        var pendingWorldSceneAdmissions=_worldSceneAdmissions;pendingWorldSceneAdmissions.Clear();
        _boneSceneTime=frame.CurrentClientTime;
        _boneSceneErrors.Clear();
        if (PrepareParticleScene(frame.Snapshot))
        {
            // A server/sector handoff is a hard client effect-context boundary.
            // Do not let an old tree retire into a new sector where GameIDs may
            // be reused and no retail effect object would survive the handoff.
            _effectRslScheduler.Clear();
            // OrbitalManager unload removes its retained objects (4F3310).
            // A persistent handoff can arrive without a rendered loading frame.
            _orbitalRenderIdentities.Clear();
            _effectParticleInstances.Clear();
            ClearCompositeCallbacks();_effectCompositeParticles.Clear();_effectCompositeBounds.Clear();_compositeRemovalSerial=0;_compositeBufferCollision.Clear();
            _effectStandaloneStreakInstances.Clear();_effectStreakInstances.Clear();
            _effectRslObjectRuntime.Clear();
            foreach(var retiredScene in _legoScenes.Values)ReleaseLegoSceneDependencies(retiredScene);
            ClearLegoScenes();
            ClearOrdinaryRslCallbacks();
            _ordinaryRslCallbackMembers.Clear();
            ClearLegoRslOwners();
            InvalidateLegoRslIndex();
            ClearEffectiveActions();
        }
        if (!frame.Snapshot.IsTransitioning && retailAssets.PresentationCatalog is EnbRetailPresentationCatalog presentationCatalog)
        {
            effectRslSchedulerUpdate = _effectRslScheduler.Update(
                frame.Snapshot.Effects,
                presentationCatalog,
                frame.CurrentClientTime,
                worldObjects: SceneObjects(frame.Snapshot),
                lineFactoryRejection:(definition,snapshot)=>RejectIncompatibleLineFactory(definition,snapshot,retailAssets));
            IReadOnlyDictionary<uint, EnbCurrentRetailRenderStateEffectEvent[]> eventsByGameId =
                effectRslSchedulerUpdate.RenderStateEvents
                    .GroupBy(static effectEvent => effectEvent.BoundGameId)
                    .ToDictionary(static group => group.Key, static group => group.OrderBy(static item => item.Sequence).ToArray());

            var callbackCandidates = new List<OrdinaryRslCallbackOwner>();
            var radiusQueryUpdates = _ownedSceneFrame?_ownedRslQueryPublications:new OwnerPublicationMap<EnbRslObjectState>();
            radiusQueryUpdates.BeginFrame();
            // RenderStateEffect is a persistent object-state mutation, not a visual
            // side effect of whichever objects happen to survive this frame's draw
            // filters. Advance every ordinary RSL object before visibility/spatial
            // filtering so one-shot effect callbacks cannot be lost.
            foreach (var (obj,sceneOwner) in SceneOwnerObjects(frame.Snapshot))
            {
                using var rslOwnerScope=new SceneOwnerScope(this,sceneOwner);
                if (obj.ObjectType == 0x08)
                    continue;

                EnbBaseAssetResolution resolution = sceneOwner is null?ResolveBaseAsset(resolver,obj.BaseAsset)
                    :sceneOwner.Resolution??=ResolveBaseAsset(resolver,obj.BaseAsset);
                if (resolution.Status != EnbBaseAssetResolutionStatus.RslReference ||
                    resolution.Rsl is not EnbResolvedRslReference resolvedRsl)
                {
                    continue;
                }

                if(sceneOwner is not null)AttachOwnedRslMembership(sceneOwner);
                else {liveRslRenderIdentities.Add(obj.RenderIdentity);liveRslOwners.Add((obj.RenderIdentity,obj.RenderObjectGeneration));}
                var passOwner = frame.RslPassOwnerOverrides?.TryGetValue(obj.RenderIdentity, out var sharedPassOwner) == true
                    ? sharedPassOwner : ResolveOwnedRslPass(sceneOwner,obj,resolvedRsl,retailAssets);
                if(sceneOwner is not null)sceneOwner.FrameRslPassOwner=passOwner;
                var boundOwner=sceneOwner is null?null:sceneOwner.RslOwner??=_effectRslObjectRuntime.BindOwner(obj.RenderIdentity,sceneOwner.RslModelChanged,sceneOwner.RslSceneChanged);
                if (sceneOwner is null && passOwner is not null) rslPassOwners.Add(obj.RenderIdentity, passOwner);
                PrepareOwnedRslFactories(sceneOwner,obj,resolvedRsl,retailAssets);
                IReadOnlyList<EnbCurrentRetailRenderStateEffectEvent> objectEvents =
                    eventsByGameId.TryGetValue(obj.GameId, out EnbCurrentRetailRenderStateEffectEvent[]? matched)
                        ? matched
                        : [];
                foreach (EnbCurrentRetailRenderStateEffectEvent effectEvent in objectEvents)
                    consumedEffectEventSequences.Add(effectEvent.Sequence);

                EnbCurrentRetailRslDirectStateSelection? directStateSelection =
                    EnbCurrentRetailStaticSectorObjectRuntime.ResolveDirectRslSelection(obj, resolvedRsl);
                // The scanner draws the world's current RSL state, including
                // its original animation activation tick; selecting a target
                // must not create a new animation lifetime in the child view.
                var callback=sceneOwner is null?new OrdinaryRslCallbackOwner(this,null):
                    sceneOwner.RslCallback??=new OrdinaryRslCallbackOwner(this,sceneOwner);
                callback.Bind(obj,resolvedRsl,retailAssets,frame.CurrentClientTime,boundOwner,
                    sceneOwner is null?rslRuntimeUpdates:null);
                EnbRslObjectState objectUpdate =
                    frame.RslObjectUpdateOverrides?.TryGetValue(obj.RenderIdentity, out var sharedUpdate) == true
                    ? sharedUpdate
                    : _effectRslObjectRuntime.Advance(
                    obj,
                    resolvedRsl,
                    objectEvents,
                    frame.CurrentClientTime,
                    directStateSelection,
                    callback.Animation,
                    //Construction/packet selection precedes the retained scene walk.
                    advanceCallbacks:false,
                    soundCompletedAtActivation:callback.SoundCompleted,
                    ownerBinding:boundOwner);
                callback.SetInitial(objectUpdate);
                if(sceneOwner is not null)sceneOwner.QueryRslPublication.Value=objectUpdate;else radiusQueryUpdates[obj.RenderIdentity]=objectUpdate;
                bool shared=frame.RslObjectUpdateOverrides?.ContainsKey(obj.RenderIdentity)==true;
                //7BCA70 ->942160 ->965DC0 ->461650 unregisters on radar exit.
                //Hidden/null current models retain registration;460E80 gates them.
                if(!shared)
                {
                    ConsumeRslBoneActions(obj,resolvedRsl,retailAssets,objectUpdate,sceneOwner);
                    if(sceneOwner is not null)sceneOwner.RslCandidateReady=true;
                    else callbackCandidates.Add(callback);
                }
                else callback.Inactive();
            }
            IReadOnlyDictionary<ulong,float>? callbackRadii=null;
            if(_ownedSceneFrame?_ownedRadiusSources.Count!=0:SceneObjects(frame.Snapshot).Any(HasCurrentRetailShipLegoComposition))
            {
                var publishedUpdates=_frameRslObjectUpdates;
                try
                {
                    _frameRslObjectUpdates=radiusQueryUpdates;
                    ObserveLegoControllers(frame.Snapshot,retailAssets,resolver,frame.CurrentClientTime);
                    PrepareLegoRslOwners(frame,retailAssets,resolver,radiusQueryUpdates,rslPassOwners,liveRslRenderIdentities,liveRslOwners);
                    callbackRadii=PrepareLegoRadiusQueries(frame,retailAssets,resolver,radiusQueryUpdates);
                }
                finally{_frameRslObjectUpdates=publishedUpdates;}
            }
            //The outer object enters the scene before its selected model and
            //external children. FILE parents have children too, but no callback.
            var candidates=callbackCandidates.ToDictionary(c=>c.Object.RenderIdentity);
            foreach(var wrapper in _legoRslOwners.Values)
            {
                var current=wrapper.Object;
                callbackUpdates[(current.RenderIdentity,current.RenderObjectGeneration)]=()=>
                {
                    wrapper.Update=_effectRslObjectRuntime.Advance(wrapper.Object,wrapper.Reference,[],frame.CurrentClientTime,null,
                        (animationId,objectId)=>ResolveRetainedRslAnimationClip(wrapper.Object,wrapper.Reference,retailAssets,animationId,objectId),
                        advanceCallbacks:true,
                        soundCompletedAtActivation:(id,tick,ordinal)=>_rslSoundCompletions.TryGetValue(RslSoundKey(wrapper.Object,id,tick),out var completion) && completion.PlaybackOrdinal==ordinal,ownerBinding:wrapper.RuntimeBinding);
                    rslRuntimeUpdates[current.RenderIdentity]=wrapper.Update;
                    _retainedEmitterRslUpdates[(current.RenderIdentity,current.RenderObjectGeneration)]=wrapper.Update;
                    PublishEffectiveActions(current,wrapper.Update.Actions);
                    ConsumeRslBoneActions(wrapper.Object,wrapper.Reference,retailAssets,wrapper.Update);
                    SynchronizeMountedLegoScene(wrapper,retailAssets);
                };
            }
            var activeObjects=_frameActiveObjects;
            activeObjects.UnionWith(_effectRslRootOwners.Values.Select(o=>(o.Object.RenderIdentity,o.Object.RenderObjectGeneration)));
            foreach(var stale in _compatLegoScenes.Where(k=>!activeObjects.Contains(k)).ToArray())
            {
                ChangeLegoSceneTree(_legoScenes[stale].Tree,null);
                ReleaseLegoSceneDependencies(_legoScenes[stale]);
                _legoScenes.Remove(stale);_compatLegoScenes.Remove(stale);
            }
            foreach(var (obj,sceneOwner) in SceneOwnerObjects(frame.Snapshot))
            {
                var candidate=sceneOwner?.RslCallback;
                bool hasCandidate=sceneOwner is not null?sceneOwner.RslCandidateReady:candidates.TryGetValue(obj.RenderIdentity,out candidate);
                bool hasLego=sceneOwner is not null?sceneOwner.PhysicalOwner is not null:_legoPhysicalOwners.ContainsKey(obj.RenderIdentity);
                if(!hasCandidate&&!hasLego&&obj.ObjectType==0x08)continue;
                var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
                bool attached=sceneOwner is not null?ShouldAttachOwnedRadar(sceneOwner,radarObserver,frame.Snapshot.Hud.ScanRange,true):
                    EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(radarObserver,obj,frame.Snapshot.Hud.ScanRange,callbackRadii);
                if(attached)
                {
                    bool newCallback=hasCandidate&&(sceneOwner is not null?!sceneOwner.RslCallbackRegistered:_ordinaryRslCallbackMembers.Add(key));
                    if(hasCandidate)
                    {
                        if(sceneOwner is not null)
                        {
                            if(newCallback){sceneOwner.RslCallbackRegistered=true;SetOwnedWrapperAttached(sceneOwner,true);_ordinaryRslCallbackMembers.Add(key);}
                            sceneOwner.RslAdvanceReady=true;
                        }
                        else {callbackUpdates.Add(key,candidate!.Advance);_compatRslCallbackMembers.Add(key);}
                        candidate!.Prepare();
                    }
                    //7BCA70 admits concrete roots in global registry order.
                    //Bind that order after all roots are available, before any
                    //particle preparation can register a later FILE emitter.
                    pendingWorldSceneAdmissions.Add((obj,sceneOwner,newCallback,sceneOwner is null?radiusQueryUpdates:null));
                }
                else
                {
                    SynchronizeLegoScene(obj,null,retailAssets,false,sceneOwner);
                    if(sceneOwner is not null)UnregisterOwnedRslCallback(sceneOwner);
                    if(hasCandidate)candidate!.Inactive();
                }
            }
            foreach(var removed in _compatRslCallbackMembers.Where(key=>!callbackUpdates.ContainsKey(key)).ToArray())
            {
                _ordinaryRslCallbacks.Remove(removed);
                _ordinaryRslCallbackMembers.Remove(removed);_compatRslCallbackMembers.Remove(removed);
            }
            //Construction/packet setters precede root preparation. Advance the
            //controller later, in the same9661C0 walk as retained buffers.

        }
        else
        {
            _effectRslScheduler.Clear();
            _effectRslObjectRuntime.Clear();
            foreach(var retiredScene in _legoScenes.Values)ReleaseLegoSceneDependencies(retiredScene);
            ClearLegoScenes();
            ClearOrdinaryRslCallbacks();
            _ordinaryRslCallbackMembers.Clear();
            ClearEffectiveActions();
        }
        int unmatchedEffectRslEvents = effectRslSchedulerUpdate is null
            ? 0
            : effectRslSchedulerUpdate.RenderStateEvents.Count - consumedEffectEventSequences.Count;
        long profileRslEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.object-roots");

        using var roots = _frameRootTasks.Rent(this);
        var ownerSetupTiming=EnbWorkReuseDiagnostics.Measure("owners.setup");
        _hiddenCombatSourceRoots.Clear();
        _hiddenRslEmitterRoots.Clear();
        _emptyRslSourcePoses.Clear();
        _frameShipExhaust.Clear();
        PreparePlanetExhaust(frame, retailAssets);
        var rootRejections = new List<LiveRootRejection>();
        var hlodRejections = new List<LiveHlodRejection>();
        using var animationHistory=_animationDiagnosticScratch.Rent();
        var animationReceipts=animationHistory.Rows;
        _worldSphereObserved.Clear();
        _worldRingObserved.Clear();
        var primitiveOwners=_framePrimitiveOwners;
        ObserveLegoControllers(frame.Snapshot,retailAssets,resolver,frame.CurrentClientTime);
        PruneLegoParticleFactories(frame.Snapshot);
        if(!_ownedSceneFrame)PruneOrdinaryBaseFactories(frame.Snapshot);
        var fixedOwners=_frameFixedOwners;
        foreach(var key in _fixedShipParticleFactories.Keys.Where(key=>!fixedOwners.Contains((key.Identity,key.BaseRevision,key.Revision))).ToArray())
            _fixedShipParticleFactories.Remove(key);
        var influenceOwners=_frameObjectIdentities;
        foreach(var key in _influenceHelperFactories.Keys.Where(key=>!influenceOwners.Contains(key)).ToArray())
            _influenceHelperFactories.Remove(key);
        foreach(var key in _influenceStates.Keys.Where(key=>!influenceOwners.Contains(key.Identity)).ToArray())
            _influenceStates.Remove(key);
        foreach(var key in _influenceParticleRoots.Keys.Where(key=>!influenceOwners.Contains(key.Identity)).ToArray())
            _influenceParticleRoots.Remove(key);
        var liveEffectKeys=(effectRslSchedulerUpdate?.VisualNodes??[]).Select(n=>(n.Root.EffectId,n.Root.InstanceGeneration,n.Sequence)).ToHashSet();
        primitiveOwners.UnionWith((_shipExhaustOwner??this)._effectRslOwners.Where(p=>liveEffectKeys.Contains(p.Key))
            .Select(p=>(p.Value.Object.RenderIdentity,p.Value.Object.VisualGeneration)));
        var soundRequests = new List<LiveRslSoundRequest>();
        PrepareLegoRslOwners(frame,retailAssets,resolver,rslRuntimeUpdates,rslPassOwners,liveRslRenderIdentities,liveRslOwners);
        primitiveOwners.UnionWith(liveRslOwners.FrameExtensions);
        ReconcileWorldSphereOwners(primitiveOwners);
        ReconcileWorldRingOwners(primitiveOwners);
        var shipLegoTracker = new LiveShipLegoFrameTracker();
        shipLegoTracker.RenderTextureReduction = detailSettings.Texture.TextureReduction;
        shipLegoTracker.RenderTextureFilter = detailSettings.Texture.TextureFilter;
        EnbResolvedTextureResource? nameDecalFont = (_ownedSceneFrame?_ownedNameDecalSources!=0:SceneObjects(frame.Snapshot).Any(static obj => obj.NameDecal is not null))
            ? retailAssets.Textures.Resolve(EnbCurrentRetailShipNameDecalRuntime.RetailFontTextureName)
            : null;
        var frameNameDecalTextures = _ownedSceneFrame?_ownedNameDecalImages:new Dictionary<string, EnbTextureImage>(StringComparer.OrdinalIgnoreCase);
        bool environmentMapResolved = EnbCurrentRetailEnvironmentMapRuntime.TryResolveReflection(
            retailAssets.EnvironmentMaps,
            frame.Snapshot.Environment,
            out EnbEnvironmentReflectionDefinition environmentReflection,
            out string? environmentMapDiagnostic);
        if(_customizationPreview)
        {
            environmentMapResolved=retailAssets.EnvironmentMaps?.TryResolveReflection(
                EnbCurrentRetailStationInterior.ReflectionBaseAsset,out environmentReflection!)==true;
            environmentMapDiagnostic=environmentMapResolved?null:"Station reflection environment unavailable.";
        }
        if (frame.Movie?.Skybox is { } movieSky)
        {
            environmentMapResolved = retailAssets.EnvironmentMaps?.TryResolveReflection(movieSky.Selector, out environmentReflection!) == true;
            environmentMapDiagnostic = environmentMapResolved ? null : "Movie reflection environment unavailable.";
        }
        shipLegoTracker.EnvironmentMapBaseAsset = frame.Snapshot.Environment?.BaseAsset;
        shipLegoTracker.EnvironmentMapTexture = environmentMapResolved ? environmentReflection.Texture : null;
        shipLegoTracker.EnvironmentMapAxis = environmentMapResolved ? environmentReflection.Axis : null;
        shipLegoTracker.EnvironmentMapUsesDefaultTexture = environmentMapResolved && environmentReflection.UsesDefaultTexture;
        shipLegoTracker.EnvironmentMapDiagnostic = environmentMapDiagnostic;
        EnbCurrentRetailEnvironmentAtlasSet? environmentAtlasSet = null;
        if (environmentMapResolved && retailAssets.EnvironmentMaps is EnbEnvironmentMapCatalog environmentMapCatalog)
        {
            environmentAtlasSet = ResolveEnvironmentAtlasSet(
                retailAssets,
                environmentMapCatalog,
                environmentReflection,
                detailSettings.Texture.TextureReduction,
                out string? environmentAtlasDiagnostic);
            shipLegoTracker.EnvironmentAtlasExpectedTextures = environmentMapCatalog.ProceduralTextures.Count;
            shipLegoTracker.EnvironmentAtlasBuiltTextures = environmentAtlasSet?.ProceduralTextures.Count ?? 0;
            shipLegoTracker.EnvironmentAtlasTextureReduction = detailSettings.Texture.TextureReduction;
            shipLegoTracker.EnvironmentAtlasTextureFilter = detailSettings.Texture.TextureFilter;
            shipLegoTracker.EnvironmentAtlasDiagnostic = environmentAtlasDiagnostic;
        }
        else
        {
            shipLegoTracker.EnvironmentAtlasDiagnostic = environmentMapDiagnostic ?? "current-retail reflection selector is unresolved";
        }
        shipLegoTracker.HlodOptionSelector = playerOptions.Selector;
        shipLegoTracker.HlodUsesPersistedOptions = playerOptions.UsesPersistedFile;
        shipLegoTracker.HlodPolyBudget = detailSettings.PolyBudget;
        int missingOrUnsupported = 0;
        int fileDefinitionObjects = 0;
        int fileResolvedResources = 0;
        int fileRootedObjects = 0;
        int rslDefinitionObjects = 0;
        int rslResolvedReferences = 0;
        int rslRootedObjects = 0;
        int rslRoots = 0;
        int influenceObjects = 0;
        int influenceBoundaryRejected = 0;
        int radarRangeDetached = 0;
        string? firstInfluenceDiagnostic = null;
        var influenceBoundarySources = new List<(IEnbRenderObject Object, LiveCpuRoot Root, EnbCurrentRetailInfluenceNebulaBoundarySource Source)>();
        void BuildObjectRoots(IEnbRenderObject obj,SceneOwner? sceneOwner=null)
        {
            if(HideHulks && obj.ObjectType==0x19 && frame.Movie is null)return; using var ownerBodyTiming=EnbWorkReuseDiagnostics.Enabled?EnbWorkReuseDiagnostics.Measure("owner.build."+obj.BaseAsset):default;
            using var sceneOwnerScope=new SceneOwnerScope(this,sceneOwner);
            if(sceneOwner is not null){if(sceneOwner.RslCallbackTree is {} callbackTree)callbackTree.Frame=-1;}
            ObserveR9ShipPresentation(obj, shipLegoTracker);
            var preparedRoot=sceneOwner?.PreparedFileRoot??(sceneOwner is {RslStructureDirty:false}?sceneOwner.PreparedRslRoot:null);
            if(sceneOwner is not null && preparedRoot is {} preparedFile && obj.Spatial is not null)
            {
                // The occurrence and all static factory/material inputs belong to
                // this node. A frame changes only pose, animation and admission.
                var inputs=sceneOwner.PreparedFileInputs!.Value;
                var posed=obj; // The retained occurrence keeps its factory-local appearance.
                preparedFile.RefreshSimple(posed,ResolveShipWarpAnimation(posed,preparedFile.Root,"root",posed.Spatial!.Transform.Position));
                if(TryBuildObjectWorld(posed,out var world,1))preparedFile.PrimitiveFactoryWorld=world;
                preparedFile.PrimitiveFactoryScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)?inputs.Scale:1;
                var shift=new Vector3(inputs.HsvH,inputs.HsvS,inputs.HsvV);
                preparedFile.PrimitiveFactoryHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(shift)?shift:Vector3.Zero;
                roots.Add(preparedFile);
                if(preparedFile.IsRsl){rslDefinitionObjects++;rslResolvedReferences++;rslRootedObjects++;rslRoots++;}
                else {fileDefinitionObjects++;fileResolvedResources++;fileRootedObjects++;}
                return;
            }
            if(sceneOwner is not null)sceneOwner.StructuralPreparations++;
            if (frame.Movie is { } movie && !movie.Visuals.ContainsKey(obj.RenderIdentity)) return;
            bool coalescedInfluenceCreation=obj.ObjectType==EnbCurrentRetailInfluenceNebulaRuntime.ObjectType &&
                obj.BaseAssetMaterializationRevision!=0 && obj.CreatedBaseAsset is not null;
            if(coalescedInfluenceCreation && !(_shipExhaustOwner??this)._influenceHelperFactories.ContainsKey(obj.RenderIdentity))
            {
                //7338E4 happens before a later737400 even when both packets
                //arrive before this renderer observes the object for the first time.
                var created=obj.CreatedBaseAsset!;
                var initial=new EnbRenderObjectState(obj) {BaseAsset=created.BaseAsset,Scale=created.Scale,HsvH=created.HsvH,
                    HsvS=created.HsvS,HsvV=created.HsvV,BaseAssetMaterializationRevision=0};
                var initialFactory=ResolveSceneBaseFactory(initial,retailAssets);
                string? initialDiagnostic=initialFactory?.Diagnostic;
                if(initialFactory?.Root is not {} initialRoot || !TryPrepareInfluenceHelper(initial,initialRoot,
                    "creation BASE "+initial.BaseAsset,out _,out initialDiagnostic))
                {
                    influenceBoundaryRejected++;
                    firstInfluenceDiagnostic??=initialDiagnostic??"Initial influence factory is not an admitted HLOD.";
                }
            }
            //733816 constructs before the object's spatial/radar scene admission.
            var ordinaryFactory=frame.Movie is null && obj.ObjectType!=0x08?ResolveOwnedSceneFactory(sceneOwner,obj,retailAssets):null;
            bool isInfluenceObject = obj.ObjectType == EnbCurrentRetailInfluenceNebulaRuntime.ObjectType;
            if (isInfluenceObject)
                influenceObjects++;
            if(isInfluenceObject && (_shipExhaustOwner??this)._influenceHelperFactories.TryGetValue(obj.RenderIdentity,out var helper))
            {
                influenceBoundarySources.Add((helper.Object,helper.Root,helper.Source));
                //73392C..733943 replaces the original base with NULL after
                //creating the helper. A later737400 installs an ordinary base
                //alongside the independently retained influence room clones.
                if(obj.BaseAssetMaterializationRevision==helper.Object.BaseAssetMaterializationRevision)return;
                isInfluenceObject=false;
            }
            else if(coalescedInfluenceCreation)isInfluenceObject=false;
            if (obj.Spatial?.Transform is null)
            {
                if (isInfluenceObject)
                {
                    influenceBoundaryRejected++;
                    firstInfluenceDiagnostic ??= $"type 0x0A object {obj.GameId} has no materialized spatial transform.";
                }
                return;
            }

            if (obj.ObjectType == 0x08 || !MainViewAdmitsRenderObject(obj.ObjectType))
                return; // 0x08 bypasses ordinary basset.ini resolution; 514270 never scene-admits 0x1C/0x1D.
            // First person hides exterior drawing, not the ship hierarchy:
            // weapon effects and spatial audio still need its animated mounts.

            EnbBaseAssetResolution resolution = ordinaryFactory?.Resolution ?? (sceneOwner is null
                ?ResolveBaseAsset(resolver,obj.BaseAsset):sceneOwner.Resolution??=ResolveBaseAsset(resolver,obj.BaseAsset));
            if (resolution.Definition.Kind == EnbBaseAssetDefinitionKind.FileName)
                fileDefinitionObjects++;
            else if (resolution.Definition.Kind == EnbBaseAssetDefinitionKind.Rsl)
                rslDefinitionObjects++;

            EnbResolvedW3dResource? resolvedW3d = resolution.W3d;
            if (resolution.Status == EnbBaseAssetResolutionStatus.W3d && (resolvedW3d is not null || ordinaryFactory?.Root is not null))
            {
                fileResolvedResources++;
                string factorySource=sceneOwner?.FactorySource ?? (resolvedW3d is not null?$"{resolvedW3d.LogicalFileName}::{resolvedW3d.RequestedObjectName}":
                    $"registered {ordinaryFactory!.Root!.FactoryLookupName}");
                if(sceneOwner is not null)sceneOwner.FactorySource=factorySource;
                LiveCpuRoot root=ordinaryFactory?.Root!;
                string? rootDiagnostic=ordinaryFactory?.Diagnostic;
                var movieFactory=frame.Movie?.Visuals.GetValueOrDefault(obj.RenderIdentity)?.RenderObjectOwner as MovieModelFactory;
                if(movieFactory is not null)root=movieFactory.Root;
                bool rootReady = movieFactory is not null || (ordinaryFactory is not null?ordinaryFactory.Root is not null:TryGetCpuRoot(
                    new LiveCpuRootKey(obj.BaseAsset, -1, resolvedW3d!.LogicalFileName, resolvedW3d.RequestedObjectName),
                    resolvedW3d,
                    retailAssets,
                    out root,
                    out rootDiagnostic));
                string? aggregateDiagnostic = null;
                if (isInfluenceObject)
                {
                    string? influenceDiagnostic=rootDiagnostic;
                    InfluenceHelperFactory retainedHelper=null!;
                    bool influenceReady=rootReady && TryPrepareInfluenceHelper(obj,root,
                        factorySource,out retainedHelper,out influenceDiagnostic);
                    if(influenceReady)
                    {
                        fileRootedObjects++;
                        influenceBoundarySources.Add((retainedHelper.Object,retainedHelper.Root,retainedHelper.Source));
                    }
                    else
                    {
                        influenceBoundaryRejected++;
                        missingOrUnsupported++;
                        firstInfluenceDiagnostic ??= influenceDiagnostic ?? "type 0x0A FILE root is not a one-level HLOD boundary source";
                        rootRejections.Add(new LiveRootRejection(
                            obj.BaseAsset,
                            obj.ObjectType,
                            "INFLUENCE-FILE",
                            factorySource,
                            firstInfluenceDiagnostic));
                    }
                    // Retail hands the ordinary BaseAsset RenderObject to the
                    // influence helper as boundary authority. It is not submitted
                    // as ordinary world geometry after that ownership transfer.
                    return;
                }

                using var rootScratch=(sceneOwner?.RootScratch??new RootStagingScratch()).Rent();
                var objectRoots = rootScratch.Roots;
                var shipFixedSearchNodes = rootScratch.FixedSearchNodes;
                if (obj.IsLocalShip)
                {
                    shipLegoTracker.LocalShipRootKind = "FILE";
                    shipLegoTracker.LocalShipAttachmentTopRoots = rootReady ? 1 : 0;
                    shipLegoTracker.LocalShipAttachmentRootName = rootReady ? root.RootRenderObjectName : null;
                    shipLegoTracker.LocalShipAttachmentAnimationSamples = 0;
                }
                if (rootReady)
                {
                    rootReady = TryAppendRootInstanceTree(
                        obj,
                        root,
                        isRsl: false,
                        objectRoots,
                        MovieAnimation(frame.Movie, obj.RenderIdentity, root),
                        out aggregateDiagnostic);
                    if(rootReady && frame.Movie is not null && objectRoots.Count!=0)
                        InitializeMovieSceneRoot(objectRoots[0]);
                }
                if (rootReady && HasCurrentRetailShipLegoComposition(obj))
                {
                    //7294D0/732880 child failure does not destroy the owning RenderObj.
                    _ = TryAppendCurrentRetailShipLegoInstances(
                        obj,
                        root,
                        parentAnimationBlend: null,
                        retailAssets,
                        resolver,
                        objectRoots,
                        shipFixedSearchNodes,
                        shipLegoTracker,
                        out aggregateDiagnostic);
                }
                if (rootReady && frame.Movie is null && EnbCurrentRetailShipFixedVisualRuntime.IsEligible(obj))
                {
                    TryAppendCurrentRetailFixedShipVisualInstances(
                        obj,
                        root,
                        parentAnimationBlend: MovieAnimation(frame.Movie, obj.RenderIdentity, root),
                        retailAssets,
                        resolver,
                        objectRoots,
                        shipFixedSearchNodes,
                        shipLegoTracker);
                }

                if (rootReady)
                {
                    if(sceneOwner is not null && frame.Movie is null && objectRoots.Count==1 &&
                        !HasCurrentRetailShipLegoComposition(obj) && !EnbCurrentRetailShipFixedVisualRuntime.IsEligible(obj) &&
                        root.AggregateAttachments.Count==0 && root.Collection is null && root.HmodelChildren.Count==0 &&
                        root.Sphere is null && root.Ring is null && root.RetainedEmitter is null)
                    {
                        sceneOwner.PreparedFileRoot=objectRoots[0];
                        sceneOwner.PreparedFileInputs=objectRoots[0].Appearance;
                    }
                    roots.AddRange(objectRoots);
                    fileRootedObjects++;
                }
                else
                {
                    missingOrUnsupported++;
                    rootRejections.Add(new LiveRootRejection(
                        obj.BaseAsset,
                        obj.ObjectType,
                        "FILE",
                        factorySource,
                        aggregateDiagnostic ?? rootDiagnostic ?? "unknown CPU-root rejection"));
                }
                return;
            }

            EnbResolvedRslReference? resolvedRsl = resolution.Rsl;
            if (resolution.Status == EnbBaseAssetResolutionStatus.RslReference && resolvedRsl is not null)
            {
                rslResolvedReferences++;
                if (isInfluenceObject)
                {
                    //7E87C0 requires the actual source's class 19. RSL's
                    //4633C0 returns10001; delegated child getters do not
                    //authorize unwrapping its current state into a helper.
                    const string detail = "type 0x0A requires an HLOD RenderObject (class 0x19); the RSL wrapper is class 0x10001.";
                    influenceBoundaryRejected++;
                    missingOrUnsupported++;
                    firstInfluenceDiagnostic ??= detail;
                    rootRejections.Add(new LiveRootRejection(obj.BaseAsset, obj.ObjectType,
                        "INFLUENCE-RSL", $"RSLID {resolvedRsl.RslId}", detail));
                    return;
                }
                EnbRslObjectState? effectRslObjectUpdate=sceneOwner?.RslUpdate;
                if(sceneOwner is null)rslRuntimeUpdates.TryGetValue(obj.RenderIdentity,out effectRslObjectUpdate);
                //00461000..00461015 / 00460E91..00460EB1 skip a null or
                //hidden current RenderObj. Sound-only controllers are valid;
                //their retained sound requests were gathered above.
                if(effectRslObjectUpdate is { CurrentObjectStateId: null } or { CurrentObjectHidden: true })
                {
                    var queryRoot=ResolveRslSourceQueryRoot(obj,resolvedRsl,effectRslObjectUpdate,retailAssets,frame.CurrentClientTime,out _,_hiddenRslEmitterRoots);
                    if(queryRoot is not null)
                    {
                        var queryRoots=new RootTaskList{queryRoot};
                        if(HasCurrentRetailShipLegoComposition(obj))
                            TryAppendCurrentRetailShipLegoInstances(obj,queryRoot.Root,queryRoot.AnimationBlend,retailAssets,resolver,
                                queryRoots,[],shipLegoTracker,out _);
                        _hiddenCombatSourceRoots.AddRange(queryRoots);
                    }
                    else if(effectRslObjectUpdate.CurrentObjectStateId is null && obj.Spatial is {} querySpatial &&
                        EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(querySpatial.Transform,out var queryWorld,out _))
                    {
                        if(HasCurrentRetailShipLegoComposition(obj))
                            TryAppendCurrentRetailShipLegoInstances(obj,null,null,retailAssets,resolver,[],[],shipLegoTracker,out _);
                        if(sceneOwner is not null)sceneOwner.EmptyRadiusCandidate=true;
                        _emptyRslSourcePoses[obj.RenderIdentity]=(obj,new(queryWorld,
                            ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.Ordinal),
                            ImmutableDictionary.Create<string,Matrix4x4>(StringComparer.OrdinalIgnoreCase),
                            ImmutableDictionary.Create<string,EnbNext.Net7.Gameplay.Net7EffectSourceControllerPose>(StringComparer.Ordinal)));
                    }
                    return;
                }
                // Current retail's RSL RenderObject wrapper delegates child-count, child-get,
                // child-attach, hierarchy-name, hierarchy-transform and hierarchy-state
                // virtuals to the retained current RenderObject. Ship Lego and fixed-visual
                // composition therefore belongs to the current RSL object root exactly as it
                // does to a FILE/W3D root. Keep the RSL roots object-local until that
                // composition has been applied.
                int added = AddRslSceneRoots(
                    sceneOwner,
                    obj,
                    resolvedRsl,
                    retailAssets,
                    out var localRslRoots,
                    animationReceipts,
                    soundRequests,
                    frame.CurrentClientTime,
                    effectRslObjectUpdate,
                    out string? rslDiagnostic,
                    out bool auditPending);
                rslRoots += added;
                if (added > 0)
                {
                    rslRootedObjects++;
                }
                else
                {
                    missingOrUnsupported++;
                    rootRejections.Add(new LiveRootRejection(
                        obj.BaseAsset,
                        obj.ObjectType,
                        isInfluenceObject ? "INFLUENCE-RSL" : "RSL",
                        $"RSLID {resolvedRsl.RslId} LINK {resolvedRsl.LinkId?.ToString() ?? "-"} RS {resolvedRsl.RsId?.ToString() ?? "-"}",
                        rslDiagnostic ?? "RSL produced no live render root", auditPending));
                }

                if (added > 0)
                {
                    bool needsLego = HasCurrentRetailShipLegoComposition(obj);
                    bool needsFixed = EnbCurrentRetailShipFixedVisualRuntime.IsEligible(obj);
                    if (needsLego || needsFixed)
                    {
                        LiveRootInstance[] topRoots = localRslRoots
                            .Where(static instance => string.Equals(instance.InstancePath, "root", StringComparison.Ordinal))
                            .ToArray();
                        TrackR9RslShipRoot(obj, resolvedRsl, topRoots, shipLegoTracker);

                        if (topRoots.Length == 1)
                        {
                            shipLegoTracker.RslAttachmentReady++;
                            LiveRootInstance currentRoot = topRoots[0];
                            var shipFixedSearchNodes = new List<ShipAttachmentOwner>();

                            if (needsLego)
                            {
                                // A rejected attachment never suppresses the already-materialized
                                // current RSL RenderObject. Retail keeps the parent alive even when a
                                // child factory/attachment cannot contribute visible geometry.
                                _ = TryAppendCurrentRetailShipLegoInstances(
                                    obj,
                                    currentRoot.Root,
                                    currentRoot.AnimationBlend,
                                    retailAssets,
                                    resolver,
                                    localRslRoots,
                                    shipFixedSearchNodes,
                                    shipLegoTracker,
                                    out _);
                            }

                            if (needsFixed)
                            {
                                TryAppendCurrentRetailFixedShipVisualInstances(
                                    obj,
                                    currentRoot.Root,
                                    currentRoot.AnimationBlend,
                                    retailAssets,
                                    resolver,
                                    localRslRoots,
                                    shipFixedSearchNodes,
                                    shipLegoTracker);
                            }
                        }
                        else
                        {
                            shipLegoTracker.RslAttachmentAmbiguous++;
                            string reason =
                                $"0x{obj.GameId:X8}: RSLID {resolvedRsl.RslId} materialized {topRoots.Length} current top-level RenderObjects; R9C cannot choose one wrapper delegate target for ship attachment composition.";
                            shipLegoTracker.FirstDiagnostic ??= reason;
                        }
                    }
                }

                if(sceneOwner is not null && localRslRoots.Count==1 && effectRslObjectUpdate?.RenderStructure?.Animations.Count==0 &&
                    !HasCurrentRetailShipLegoComposition(obj) && !EnbCurrentRetailShipFixedVisualRuntime.IsEligible(obj))
                {
                    var prepared=localRslRoots[0];var model=prepared.Root;
                    if(model.AggregateAttachments.Count==0&&model.Collection is null&&model.HmodelChildren.Count==0&&
                        model.Sphere is null&&model.Ring is null&&model.RetainedEmitter is null&&prepared.AnimationBlend is null)
                    {
                        sceneOwner.PreparedRslRoot=prepared;sceneOwner.RslStructureDirty=false;
                        sceneOwner.PreparedFileInputs=prepared.Appearance;
                    }
                }
                if(sceneOwner is not null)CaptureRslCallbackTree(sceneOwner,obj,localRslRoots);
                roots.AddRange(localRslRoots);
                return;
            }

            // Null/procedural/special BaseAsset kinds belong to later renderer
            // families and are deliberately not approximated as W3D geometry.
            if (resolution.Status is not (EnbBaseAssetResolutionStatus.NullObject or EnbBaseAssetResolutionStatus.NoRenderObject))
            {
                missingOrUnsupported++;
                string branch = resolution.Definition.Kind switch
                {
                    EnbBaseAssetDefinitionKind.FileName => "FILE-RESOLVE",
                    EnbBaseAssetDefinitionKind.Rsl => "RSL-RESOLVE",
                    _ => "BASE-RESOLVE",
                };
                string resource = resolution.Definition.Kind switch
                {
                    EnbBaseAssetDefinitionKind.FileName =>
                        $"{resolution.Definition.File ?? "?"}::{resolution.Definition.Name ?? "?"}",
                    EnbBaseAssetDefinitionKind.Rsl =>
                        $"RSLID {resolution.Definition.RslId?.ToString() ?? "-"} LINK {resolution.Definition.LinkId?.ToString() ?? "-"} RS {resolution.Definition.RsId?.ToString() ?? "-"}",
                    _ => resolution.Definition.Kind.ToString(),
                };
                string resolverDiagnostic =
                    $"resolver status {resolution.Status}: {resolution.Diagnostic ?? "no diagnostic"}";
                if (resolution.Status == EnbBaseAssetResolutionStatus.InvalidW3d &&
                    resolution.Definition.Kind == EnbBaseAssetDefinitionKind.FileName &&
                    !string.IsNullOrWhiteSpace(resolution.Definition.File))
                {
                    resolverDiagnostic +=
                        " | " + DescribeUnsupportedW3dStructure(
                            retailAssets.MixResources,
                            resolution.Definition.File!,
                            resolution.Definition.Name ?? Path.GetFileNameWithoutExtension(resolution.Definition.File!));
                }

                rootRejections.Add(new LiveRootRejection(
                    obj.BaseAsset,
                    obj.ObjectType,
                    branch,
                    resource,
                    resolverDiagnostic));
                if (isInfluenceObject)
                {
                    influenceBoundaryRejected++;
                    firstInfluenceDiagnostic ??= resolverDiagnostic;
                }
            }
            else if (isInfluenceObject)
            {
                influenceBoundaryRejected++;
                firstInfluenceDiagnostic ??=
                    $"type 0x0A BaseAsset {obj.BaseAsset} resolved to a NullObject and cannot supply the proven influence boundary HLOD.";
            }
        }

        ownerSetupTiming.Dispose();
        using (EnbWorkReuseDiagnostics.Measure("roots.initial"))
        {
            if(_ownedSceneFrame)
            {
                foreach(var node in _ownedScene!.ActiveNodes)
                    BuildObjectRoots(node.State,node.State);
            }
            else foreach(IEnbRenderObject obj in SceneObjects(frame.Snapshot))BuildObjectRoots(obj);
        }
        ObserveReuseRoots("roots.initial", roots);

        HashSet<ulong> RefreshCallbackObjectRoots((ulong Identity,ulong Generation) callbackOwner, CallbackRootRefreshScratch scratch,SceneOwner? callbackSceneOwner=null)
        {
            using var reuseRefresh = EnbWorkReuseDiagnostics.Measure("callback.refresh");
            EnbWorkReuseDiagnostics.Add("callback.ownerLookupCandidates", _legoRslOwners.Count + SceneObjects(frame.Snapshot).Count);
            EnbWorkReuseDiagnostics.Add("callback.rootScanPopulation", roots.Count);
            ulong outer=callbackOwner.Identity;
            if(callbackSceneOwner is null)
            {
                EnsureLegoRslIndex();
                if(_legoRslByIdentity.TryGetValue(callbackOwner.Identity,out var wrapperEntry))
                {outer=wrapperEntry.Parent;callbackSceneOwner=_ownedSceneFrame?wrapperEntry.Owner.ParentScene:null;}
            }
            IEnbRenderObject? obj=callbackSceneOwner;
            if(obj is null && !_ownedSceneFrame)
                foreach(var candidate in SnapshotObjects(frame.Snapshot))
                    if(candidate.RenderIdentity==outer){obj=candidate;break;}
            Action? buildEffectRoots=null;
            if(callbackSceneOwner is null)effectRootBuilders.TryGetValue(outer,out buildEffectRoots);
            if(callbackSceneOwner is not null)scratch.UseOwner(callbackSceneOwner.CallbackIdentities);
            var identities=scratch.Identities;
            if(obj is null && buildEffectRoots is null)return identities;
            if(callbackSceneOwner is null)
            {foreach(var pair in LegoRslOwnersFor(outer))identities.Add(pair.Value.Object.RenderIdentity);identities.Add(outer);}
            using var callbackRootTiming=worldStages is not null?FullClientBenchmarkMeter.Enter("callback-roots-owner."+(obj?.BaseAsset.ToString()??"<effect>")):default;
            RootTaskList? evaluatedReplacement=null;
            if(callbackSceneOwner is {RslCallbackTree:{} tree,Resolution.Rsl:{} callbackRsl} &&
                tree.Frame==_rigidFrame && ReferenceEquals(callbackSceneOwner.RootTasks.Collection,roots) &&
                callbackSceneOwner.RootTasks.Count==tree.Instances.Count &&
                callbackSceneOwner.RslUpdate is {CurrentObjectStateId:not null,CurrentObjectHidden:false} callbackUpdate)
            {
                _rslCallbackMutationAttempts++;
                tree.Evaluated.Clear();
                int evaluated=AddRslRoots(obj!,callbackRsl,retailAssets,tree.Replacements,animationReceipts,soundRequests,
                    frame.CurrentClientTime,callbackUpdate,out var evaluationDiagnostic,out bool evaluationPending,
                    evaluatedRoots:tree.Evaluated);
                if(MutateRslCallbackTree(callbackSceneOwner,tree,out _))
                {
                    roots.MoveOwnedToEnd(callbackSceneOwner,scratch.Current);
                    scratch.PersistentTopology=tree.Persistent && tree.PersistentValid;
                    _rslCallbackMutations++;callbackSceneOwner.CallbackMutations++;_rslCallbackMutatedRoots+=scratch.Current.Count;
                    EnbWorkReuseDiagnostics.Add("callback.retainedMutations");
                    EnbWorkReuseDiagnostics.Add("callback.retainedMutationRoots",scratch.Current.Count);
                    return identities;
                }
                // A linked animation may select another model while evaluated.
                // Consume that evaluation once: replaying it can repeat NEXT,
                // sounds or lifetime changes. Reconcile only this real change.
                tree.Replacements.Clear();
                foreach(var selected in tree.Evaluated)
                    if(!TryAppendRootInstanceTree(obj!,selected.Root,true,tree.Replacements,selected.Blend,out var error))
                        evaluationDiagnostic??=error;
                evaluatedReplacement=tree.Replacements;callbackSceneOwner.RslCallbackTree=null;
                if(evaluated==0 || tree.Replacements.Count==0)
                {
                    missingOrUnsupported++;
                    rootRejections.Add(new(obj!.BaseAsset,obj.ObjectType,"RSL",$"RSLID {callbackRsl.RslId}",
                        evaluationDiagnostic??"RSL produced no live render root",evaluationPending));
                }
            }
            _rslCallbackRebuilds++;
            if(callbackSceneOwner is null)_rslCallbackUnownedRebuilds++;else callbackSceneOwner.CallbackRebuilds++;
            var previous=scratch.Previous;
            if(callbackSceneOwner is not null)roots.RemoveOwned(callbackSceneOwner,scratch.CollectPreviousPredicate);
            else roots.RemoveAll(scratch.CollectPreviousPredicate);
            EnbWorkReuseDiagnostics.Add("callback.wrappersSelectedForRebuild", previous.Count);
            _hiddenCombatSourceRoots.RemoveAll(scratch.BelongsPredicate);
            _hiddenRslEmitterRoots.RemoveAll(scratch.BelongsPredicate);
            if(callbackSceneOwner is not null)callbackSceneOwner.EmptyRadiusCandidate=false;
            foreach(var identity in identities)_emptyRslSourcePoses.Remove(identity);
            scratch.ClearObservations(this);
            int replacementStart=roots.Count;
            if(evaluatedReplacement is not null)roots.AddRange(evaluatedReplacement);
            else if(obj is not null)
            {
                BuildObjectRoots(obj,callbackSceneOwner);
            }
            else buildEffectRoots!();
            var replacements=scratch.Replacements;
            var callbackRoots=scratch.Current;
            for(int i=replacementStart;i<roots.Count;i++)
                if(scratch.Belongs(roots[i]))callbackRoots.Add(roots[i]);
            //965F40 prepared the old model before9661C0.460260 changes the
            //current model without running another detail-budget traversal.
            foreach(var instance in callbackRoots)
            {
                var retained=scratch.FindPrevious(instance);
                if(retained is not null)
                {
                    instance.ShouldDraw=retained.ShouldDraw;instance.HlodSelection=retained.HlodSelection;
                    // Rebuilding a frame wrapper does not discard the retained
                    // model's prepared bounds. PassEffect and camera/shadow
                    // consumers run after the scene callback traversal.
                    instance.CurrentRetailWorldOrigin=retained.CurrentRetailWorldOrigin;
                    instance.CurrentRetailWorldSphere=retained.CurrentRetailWorldSphere;
                    instance.CurrentRetailLocalBox=retained.CurrentRetailLocalBox;
                    instance.CurrentRetailBoundsWorld=retained.CurrentRetailBoundsWorld;
                    replacements[retained]=instance;
                }
                else if(instance.Root.Hlod is {} hlod)
                    instance.HlodSelection=new(hlod.Name,RememberedHlodLevel(instance,hlod));
            }
            //The scene's visibility bit belongs to the wrapper prepared before
            //the callback; replacing its model does not perform another cull.
            var priorWrapper=scratch.FindWrapper(callbackOwner.Identity);
            if(priorWrapper is {ShouldDraw:false})
                foreach(var instance in callbackRoots)
                    if(instance.Object.RenderIdentity==callbackOwner.Identity)instance.ShouldDraw=false;
            //Damage/movie children are retained attachments, not new per-draw
            //factory requests. Keep their actual parent when its model survives.
            scratch.PreserveAttachments(_bloodAttachments,roots);
            scratch.PreserveAttachments(_shipDamageAttachments,roots);
            scratch.PreserveAttachments(_movieAttachments,roots);
            //460FF0 renders the current model after461460's callback. Rebind
            //its concrete primitive consumers without a second allocation.
            //Clearing the old observations above must not erase a surviving
            //primitive or defer a newly selected one until the next frame.

            ObserveRetainedWorldSpheres(callbackRoots);
            ObserveRetainedWorldRings(callbackRoots);
            return identities;
        }

        //7BCA70 changes scene membership of an already constructed RenderObj.
        //72CC10/732750 publish controller radii before7BE3A0 reads both owners.
        //Build all objects first: snapshot iteration order is not radius authority.
        var admissionDistanceRadii=BuildRadiusAdmission(roots,retailAssets.ClientBaseAssetMounts);
        HashSet<ulong> radarDetachedIdentities;
        if(_ownedSceneFrame)
        {
            radarDetachedIdentities=_radarDetachedOwners;
            foreach(var node in _ownedScene!.ActiveNodes)
            {
                var source=node.State;
                bool attached=ShouldAttachOwnedRadar(source,radarObserver,frame.Snapshot.Hud.ScanRange,false);
                if(source.Visibility.RadarAttached==attached)continue;
                source.Visibility.RadarAttached=attached;_radarMembershipChanges++;
                if(attached)radarDetachedIdentities.Remove(node.Definition.RenderIdentity);
                else radarDetachedIdentities.Add(node.Definition.RenderIdentity);
            }
            roots.RemoveAll(IsOwnedRadarDetached);
        }
        else
        {
            radarDetachedIdentities=SceneObjects(frame.Snapshot).Where(obj=>
                !EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(
                    radarObserver,obj,frame.Snapshot.Hud.ScanRange,admissionDistanceRadii))
                .Select(obj=>obj.RenderIdentity).ToHashSet();
            roots.RemoveAll(instance=>radarDetachedIdentities.Contains(instance.Object.RenderIdentity));
        }
        radarRangeDetached=radarDetachedIdentities.Count;

        if (frame.Movie is { } movieChildren) AppendMovieChildren(movieChildren.Visuals, retailAssets, roots);
        else {_movieChildren.Clear();_movieAttachments.Clear();}
        long profileObjectRootsEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.auxiliary-roots");
        int influenceOutside = 0;
        int influenceBetween = 0;
        int influenceRoom = 0;
        int influenceEvaluationRejected = 0;
        var activeInfluenceEnvironments = new List<(uint GameId, EnbCurrentRetailInfluenceNebulaEvaluation Evaluation)>();
        foreach ((IEnbRenderObject influenceObject, LiveCpuRoot influenceRoot, EnbCurrentRetailInfluenceNebulaBoundarySource source) in influenceBoundarySources)
        {
            uint gameId = influenceObject.GameId;
            var influenceKey = (influenceObject.RenderIdentity, influenceObject.RenderObjectGeneration, influenceObject.BaseAsset);
            var influenceEvaluation = EvaluateInfluence(influenceKey, source, frame.Camera.Position, retailAssets.NebulaEnvironment);
            if (influenceEvaluation is null) continue;
            if (!TryAppendInfluenceRoom(influenceObject, influenceRoot, source, influenceEvaluation, roots, out var roomDiagnostic))
            {
                influenceEvaluationRejected++;
                firstInfluenceDiagnostic ??= roomDiagnostic;
            }
            switch (influenceEvaluation.Region)
            {
                case EnbCurrentRetailInfluenceNebulaRegion.Outside:
                    influenceOutside++;
                    break;
                case EnbCurrentRetailInfluenceNebulaRegion.Between:
                    influenceBetween++;
                    activeInfluenceEnvironments.Add((gameId, influenceEvaluation));
                    break;
                case EnbCurrentRetailInfluenceNebulaRegion.Room:
                    influenceRoom++;
                    activeInfluenceEnvironments.Add((gameId, influenceEvaluation));
                    break;
                default:
                    influenceEvaluationRejected++;
                    firstInfluenceDiagnostic ??= influenceEvaluation.Diagnostic;
                    break;
            }
        }

        bool influenceEnvironmentApplied = false;
        EnbCurrentRetailInfluenceNebulaEvaluation? appliedInfluenceEnvironment = null;
        if (influenceBoundaryRejected == 0 && influenceEvaluationRejected == 0)
        {
            if (activeInfluenceEnvironments.Count == 1 &&
                activeInfluenceEnvironments[0].Evaluation.Environment is EnbRetailNebulaEnvironmentState influenceEnvironment)
            {
                sceneFog = new EnbW3dSceneFogState(
                    true,
                    influenceEnvironment.FogColorRgb24,
                    influenceEnvironment.FogNearRange,
                    influenceEnvironment.FogFarRange);
                influenceEnvironmentApplied = true;
                appliedInfluenceEnvironment = activeInfluenceEnvironments[0].Evaluation;
            }
            else if (activeInfluenceEnvironments.Count > 1)
            {
                influenceEvaluationRejected++;
                firstInfluenceDiagnostic ??=
                    $"camera is inside {activeInfluenceEnvironments.Count} type 0x0A helpers; retail helper update ordering is not proven for overlapping influence objects";
            }
        }

        LiveInfluenceSkyPresentation influenceSkyPresentation = LiveInfluenceSkyPresentation.Inactive with
        {
            SnapshotVersion = frame.Snapshot.Version,
            SectorNum = frame.Snapshot.Environment?.SectorNum,
        };
        if (influenceEnvironmentApplied &&
            appliedInfluenceEnvironment is not null &&
            activeInfluenceEnvironments.Count == 1)
        {
            influenceSkyPresentation = new LiveInfluenceSkyPresentation(
                OverrideActive: true,
                SnapshotVersion: frame.Snapshot.Version,
                SectorNum: frame.Snapshot.Environment?.SectorNum,
                GameId: activeInfluenceEnvironments[0].GameId,
                Region: appliedInfluenceEnvironment.Region,
                SkyBoxColor: retailAssets.NebulaEnvironment?.Between.SkyBoxColor);
        }
        Volatile.Write(ref _influenceSkyPresentation, influenceSkyPresentation);

        AppendEffectRslRoots(frame,retailAssets,effectRslSchedulerUpdate?.VisualNodes??[],roots,
            animationReceipts,soundRequests,rslRuntimeUpdates,rslPassOwners,liveRslRenderIdentities,liveRslOwners,
            callbackUpdates,effectRootBuilders,effectSoundCollectors);
        _effectRslObjectRuntime.Prune(liveRslRenderIdentities);
        foreach(var key in _compatRslConfiguredRoots.Where(k=>!liveRslOwners.Contains((k.Identity,k.Generation))).ToArray()){_rslConfiguredRoots.Remove(key);_compatRslConfiguredRoots.Remove(key);}
        _rslFactoryPrepared.RemoveWhere(k=>!liveRslOwners.Contains(k));
        foreach(var key in _soundCompletionLifetime.Compatibility.Where(key=>!liveRslOwners.Contains((key.RenderIdentity,key.VisualGeneration))).ToArray())
            RemoveSoundCompletion(key);
        foreach(var key in _compatRslSoundSelections.Where(key=>!liveRslOwners.Contains(key)).ToArray())
        {_rslSoundSelections.Remove(key);_compatRslSoundSelections.Remove(key);}
        foreach (ulong renderIdentity in _compatEffectiveActionOwners
                     .Where(key => !liveRslRenderIdentities.Contains(key)).ToArray())
            {_rslEffectiveActionsByRenderIdentity.Remove(renderIdentity);_compatEffectiveActionOwners.Remove(renderIdentity);}
        // Both transition caches belong to the retained RenderObj generation.
        // Removing/replacing that owner must retire its local animation history,
        // including packet-armed NEXT entries (007362F0 destroys the object).
        foreach (var key in _automaticTransitionLifetime.Compatibility.Where(key => !liveRslOwners.Contains(key.Owner)).ToArray())
            RemoveAutomaticTransition(key);
        foreach (var key in _packetTransitionLifetime.Compatibility.Where(key => !liveRslOwners.Contains(key.Owner)).ToArray())
            RemovePacketTransition(key);
        foreach(var key in _compatRslBoneOwners.Where(key=>!liveRslOwners.Contains(key)).ToArray())
            {_rslBoneOwners.Remove(key);_compatRslBoneOwners.Remove(key);}
        foreach (var key in _compatRslPassOwners.Where(key => !liveRslOwners.Contains(key)).ToArray())
        {
            _rslPassOwners[key].Library.Dispose();
            _rslPassOwners.Remove(key);_compatRslPassOwners.Remove(key);
        }

        // Retail's mutable material mappers belong to the materialized
        // RenderObj, not to a particular MainView draw. Establish and refresh
        // that ownership before HLOD/frustum culling can mark roots invisible.
        // This lets an initially offscreen planet and a planet that leaves the
        // viewport retain the same mapper epoch and mode-0 PLT_DN phase for the
        // scanner's direct scene draw.
        ImmutableHashSet<LivePlanetGpuIdentity> ownedPlanetMapperStates =
            UpdatePlanetMapperStateOwnership(frame, retailAssets, roots);
        EnbResolvedRenderCamera? orbitalCamera = AppendOrbitalRoots(frame, retailAssets, resolver, roots);
        var orbitalOwners = roots.Where(instance => instance.OrbitalCamera.HasValue)
            .Select(instance => instance.Object.RenderIdentity).ToHashSet();
        long profileBoundsStart = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.owner-bounds");
        var retainedWorldRoots=_compatWorldSceneRoots;retainedWorldRoots.Clear();
        foreach(var instance in roots)
        {
            if(instance.Parent is not null)continue;
            var source=instance.PreparedOwner?.SceneSource;
            if(instance.IsRsl&&(source?.RslUpdate??rslRuntimeUpdates.GetValueOrDefault(instance.Object.RenderIdentity))?.CurrentObjectStateId!=instance.Root.RslFactoryStateId)continue;
            var root=instance.Root;
            if(root.RetainedEmitter is null&&root.RslBoneNode is null&&root.AggregateAttachments.Count==0&&root.HmodelChildren.Count==0&&root.CollectionChildren.Count==0)continue;
            if(_ownedSceneFrame&&source is not null)
            {
                if(source.FrameAdmissionRoot is not null)throw new InvalidOperationException("Duplicate world scene root.");
                source.FrameAdmissionRoot=instance;
            }
            else retainedWorldRoots.Add(instance.Object.RenderIdentity,instance);
        }
        foreach(var admission in pendingWorldSceneAdmissions)
        {
            var (obj,source,newCallback,updates)=admission;
            var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
            if(newCallback)
            {
                if(source is not null)RegisterOwnedRslCallback(source,key);
                else _ordinaryRslCallbacks.Register(key);
            }
            RetainWorldSceneModel(obj,source is not null?source.FrameAdmissionRoot:retainedWorldRoots.GetValueOrDefault(obj.RenderIdentity),source);
            if(source is not null?source.PhysicalOwner is not null:_legoPhysicalOwners.ContainsKey(obj.RenderIdentity))
                SynchronizeLegoScene(obj,source is not null?source.RslUpdate:updates?.GetValueOrDefault(obj.RenderIdentity),retailAssets,true,source);
        }
        pendingWorldSceneAdmissions.Clear();retainedWorldRoots.Clear();
        PrepareBlood(frame,roots,retailAssets);
        PrepareShipDamage(frame,roots,retailAssets);
        _frameEmitterResidents.BeginFrame(_ownedSceneFrame);
        _frameEmitterResidents.UnionWith(liveRslOwners.FrameExtensions);
        if(!_ownedSceneFrame)foreach(var obj in SnapshotObjects(frame.Snapshot))_frameEmitterResidents.Add((obj.RenderIdentity,obj.VisualGeneration));
        PrepareRetainedEmitters(frame.CurrentClientTime,roots,_frameEmitterResidents);
        PruneDazzleHistory(roots,liveRslOwners);
        UpdateShipExhaust(frame.CurrentClientTime, roots);
        UpdateAggregateLines(frame.CurrentClientTime, roots);
        UpdateWarpPlumes(frame, retailAssets, roots, effectRslSchedulerUpdate?.VisualNodes ?? []);
        UpdateAttachedEffects(frame, retailAssets, roots, effectRslSchedulerUpdate?.VisualNodes ?? []);
        PrepareAttachmentComposites(roots);
        PrepareDebris(frame, retailAssets);
        long profileHlodStart=Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.hlod");
        LiveHlodSelectionStats hlodStats = ApplyHlodSelections(
            frame.Camera,
            detailSettings,
            roots,
            hlodRejections,
            shipLegoTracker, effectRslSchedulerUpdate?.VisualNodes ?? [], frame.Snapshot, retailAssets, unchecked((uint)frame.CurrentClientTime));
        long profileHlodEnd=Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.effect-prepare");
        //Complete factory construction and selected-child scene attachment
        //before9661C0. Retain packet order independently of callback order.
        var preparedEffectPackets=new List<EnbW3dDrawTask>();
        var preparedEffectObjects=PrepareEffectObjects(preparedEffectPackets,effectRslSchedulerUpdate?.VisualNodes??[],
            frame.Snapshot,retailAssets,BuildCombatEffectRadii(roots,retailAssets,SceneObjects(frame.Snapshot)),lightEnvironment,
            frame.CurrentClientTime,ReadMapperClock(),frame.Camera,(spaceComposition || orbitalCamera.HasValue)?.95f:1);
        long profileEffectObjectsEnd=Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.callbacks");
        long profileRslRefreshTicks=0;
        int profileRslRefreshCount=0;
        var refreshStages=new long[7];
        void AdvanceRslSceneCallback((ulong Identity,ulong Generation) key)
        {
            if(callbackUpdates.TryGetValue(key,out var advance))AdvanceBoundRslSceneCallback(key,advance);
        }
        void AdvanceBoundRslSceneCallback((ulong Identity,ulong Generation) key,Action advance,SceneOwner? sceneOwner=null)
        {
            using var callbackOwnerScope=new SceneOwnerScope(this,sceneOwner);
            using var callbackOwnerTiming=worldStages is not null?FullClientBenchmarkMeter.Enter("callback-owner."+(sceneOwner?.BaseAsset.ToString()??"<effect>")):default;
            var reuseBefore = CaptureReuseStructure(roots,key);
            var reuseStamp = EnbWorkReuseDiagnostics.Stamp();
            long refreshStart=Stopwatch.GetTimestamp();
            using var reuseCallback = EnbWorkReuseDiagnostics.Measure("callback.complete");
            advance();
            long step=Stopwatch.GetTimestamp();refreshStages[0]+=step-refreshStart;
            if(sceneOwner is {PreparedRslRoot:not null,RslStructureDirty:false})
            {
                // A rigid selected model with no animated/attached children has
                // no topology to refresh when this callback only advances clocks.
                profileRslRefreshTicks+=step-refreshStart;profileRslRefreshCount++;
                return;
            }
            using var rootRefresh=_callbackRootRefreshScratch.Rent();
            var refreshedOwners=RefreshCallbackObjectRoots(key,rootRefresh,sceneOwner);
            long next=Stopwatch.GetTimestamp();refreshStages[1]+=next-step;step=next;
            PrepareRetainedEmittersForOwners(frame.CurrentClientTime,roots,
                _frameEmitterResidents,beginFrame:false,refreshedOwners:refreshedOwners,refresh:rootRefresh);
            next=Stopwatch.GetTimestamp();refreshStages[2]+=next-step;step=next;
            UpdateRetainedEmittersForOwners(frame.CurrentClientTime,rootRefresh.Current,refreshedOwners);
            next=Stopwatch.GetTimestamp();refreshStages[3]+=next-step;step=next;
            UpdateShipExhaust(frame.CurrentClientTime,rootRefresh.Current,refreshedOwners);
            next=Stopwatch.GetTimestamp();refreshStages[4]+=next-step;step=next;
            UpdateWarpPlumes(frame,retailAssets,rootRefresh.Current,effectRslSchedulerUpdate?.VisualNodes??[],refreshedOwners);
            UpdateAttachedEffects(frame,retailAssets,rootRefresh.Current,effectRslSchedulerUpdate?.VisualNodes??[],refreshedOwners);
            next=Stopwatch.GetTimestamp();refreshStages[5]+=next-step;step=next;
            PrepareAttachmentCompositesForOwners(rootRefresh.Current,refreshedOwners,rootsAreSelected:true);
            refreshStages[6]+=Stopwatch.GetTimestamp()-step;
            profileRslRefreshTicks+=Stopwatch.GetTimestamp()-refreshStart;
            profileRslRefreshCount++;
            bool reuseSame = ObserveCallbackMutation(reuseBefore, roots,key);
            if (reuseBefore is not null) EnbWorkReuseDiagnostics.Elapsed(reuseSame ? "callback.classifiedNoStructureChange" : "callback.classifiedStructureChange", reuseStamp);
        }
        _dispatchOwnedRslCallback=AdvanceBoundRslSceneCallback;
        _ordinaryRslCallbacks.SetCallback(AdvanceRslSceneCallback);
        _effectRslCallbacks.SetCallback(AdvanceRslSceneCallback);
        PrepareSharedParticleCallbacks(frame,roots,effectRslSchedulerUpdate?.VisualNodes??[],retailAssets);
        long profileParticleCallbacksEnd=Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.sound-visibility");
        _lastRslRefreshStages=refreshStages.Select(t=>t*1000d/Stopwatch.Frequency).ToArray();
        if(worldStages is not null)
        {
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.advance",refreshStages[0]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.roots",refreshStages[1]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.emitter-prepare",refreshStages[2]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.emitter-update",refreshStages[3]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.exhaust",refreshStages[4]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.effects",refreshStages[5]);
            FullClientBenchmarkMeter.RecordObservedWallTicks("callback-rsl.attachments",refreshStages[6]);
        }
        foreach(var collect in effectSoundCollectors)collect();
        foreach(var (obj,source) in SceneOwnerObjects(frame.Snapshot))
        {
            var update=source?.RslUpdate;
            if(source is null)rslRuntimeUpdates.TryGetValue(obj.RenderIdentity,out update);
            var resolution=source is null?ResolveBaseAsset(resolver,obj.BaseAsset):source.Resolution;
            if(update is not null && resolution?.Rsl is {} soundRsl)
            {
                using var soundOwnerScope=new SceneOwnerScope(this,source);
                AppendOrdinaryRslSounds(obj,soundRsl,update,retailAssets,soundRequests);
            }
        }
        foreach(var wrapper in _legoRslOwners.Values)
            if(wrapper.Update is {} mountedUpdate)
                AppendOrdinaryRslSounds(wrapper.Object,wrapper.Reference,mountedUpdate,retailAssets,soundRequests);
        if (frame.Movie is { } movieVisibility)
            foreach (var instance in roots)
                if (MovieAncestorHidden(instance) || movieVisibility.Visuals.TryGetValue(instance.Object.RenderIdentity, out var visible) && visible.Hidden)
                    instance.ShouldDraw = false;
        ImmutableDictionary<ulong, float> currentRetailDistanceRadii =
            MergeRadiusPublications(admissionDistanceRadii,BuildRetainedCurrentRetailDistanceRadii(roots, retailAssets.ClientBaseAssetMounts));
        InspectAndCaptureHlodPrototypeEvidence(retailAssets.MixResources, hlodRejections);
        long profileRootsEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.mesh-packets");

        ObserveReuseRoots("roots.prePackets", roots);
        PublishDockAnchors(roots,retailAssets);
        PublishStationShipGeometry(roots,frame.Camera);
        PrepareProjectedDecals(frame, roots, retailAssets, lightEnvironment);
        var traversalPackets = packetTasks.Traversal;
        AppendDebris(frame, retailAssets, traversalPackets);
        var shadowRoots = FindDynamicShadowRoots(frame, retailAssets, roots);
        var shadowAppearance = new Dictionary<LiveRootInstance, IReadOnlyList<EnbW3dDrawTask>>();
        AppendWorldSpheres(traversalPackets, frame.Camera, frame.CurrentClientTime);
        AppendWorldRings(traversalPackets,frame.Camera,frame.CurrentClientTime);
        var planetBackgroundCamera = AppendPlanetBackground(frame, sceneFog.SceneColorRgb24, traversalPackets);
        EnbResolvedRenderCamera? planetSkyCamera = AppendPlanetSkyPackets(frame, retailAssets, lightEnvironment, traversalPackets);
        var objectPresentationAudits = new Dictionary<ulong, LiveObjectPresentationAudit>();
        var usedParticleEmitterKeys = new HashSet<LiveParticleEmitterKey>();
        var planetGpuTrackers = new Dictionary<ulong, LivePlanetGpuFrameTracker>();
        int drawPacketCompileRejected = 0;
        if (_sharedMaterialOwner is null) _shipMaterials.BeginFrame();
        int particleEmitterInstances = 0;
        int particleEmitterLiveParticles = 0;
        int particleEmitterRejected = 0;
        string? firstDrawDiagnostic = frame.Snapshot.Effects.FirstOrDefault(e =>
            e.GeometryKind == EnbSceneEffectGeometryKind.ObjectToEffect) is {} unresolvedEffectTarget
            ? $"E{unresolvedEffectTarget.DefinitionId} effect 0x{unresolvedEffectTarget.EffectId:X}: packet-time position of target effect 0x{unresolvedEffectTarget.TargetGameId:X} unavailable"
            : null;
        long drawPreparationEpoch=_ownerLocalShipChildTasks?Interlocked.Increment(ref _nextDrawPreparationEpoch):0;
        foreach (LiveRootInstance instance in roots)
        {
            if(instance.Root.Sphere is not null || instance.Root.Ring is not null || instance.Root.RetainedEmitter is not null)continue; //Concrete primitive consumers own these packets.
            if(!TryPrepareRetainedRootDraw(instance,out string? admissionDiagnostic,drawPreparationEpoch))
            {missingOrUnsupported++;drawPacketCompileRejected++;firstDrawDiagnostic??=admissionDiagnostic;continue;}
            bool mainViewAdmitted = instance.ShouldDraw && CollectionAncestorsDraw(instance) && !(frame.IsFirstPerson && instance.Object.IsLocalShip);
            if (!mainViewAdmitted && !shadowRoots.Contains(instance))
                continue;

            EnbW3dHlodSelection? selection = instance.HlodSelection;
            LiveObjectPresentationAudit? objectPresentationAudit = null;
            if (!instance.OrbitalCamera.HasValue && !objectPresentationAudits.TryGetValue(
                    instance.Object.RenderIdentity,
                    out objectPresentationAudit))
            {
                objectPresentationAudit = RentPresentationAudit(instance, detailedDiagnosticsDue);
                objectPresentationAudits.Add(instance.Object.RenderIdentity, objectPresentationAudit);
            }
            objectPresentationAudit?.IncludeRoot(
                instance.InstancePath,
                instance.Root.Resource.Key,
                instance.Root.Resource.LogicalFileName,
                instance.Root.RuntimeRenderObjectName,
                selection,
                instance.CurrentRetailWorldSphere,
                ConfiguredHierarchyPostTransforms(instance) is null ? instance.RenderScale : 1f, instance.Parent is null ? ResolveScannerRootSphere(instance) : null);

            LivePlanetGpuFrameTracker? planetTracker = null;
            float? currentRetailPltDayNightPhase = instance.OrbitalPhase;
            if (IsRenderablePlanet(instance.Object))
            {
                if (!planetGpuTrackers.TryGetValue(instance.Object.RenderIdentity, out planetTracker))
                {
                    planetTracker = new LivePlanetGpuFrameTracker(instance.Object);
                    planetGpuTrackers.Add(instance.Object.RenderIdentity, planetTracker);
                }

                if (selection is EnbW3dHlodSelection planetSelection)
                    planetTracker.HlodSummary ??= $"{planetSelection.Name}:{planetSelection.LevelOrdinal}";

                // Ownership was established before MainView culling. Both the
                // ordinary draw and the isolated scanner consume that one
                // generation-qualified state instead of deriving mapper state
                // from whether this root happened to survive the frustum.
                var mapperOwnership=instance.PreparedOwner?.SceneSource?.PlanetMapper;
                if(instance.PreparedOwner?.SceneSource is null)
                    _planetMapperStateOwnership.TryGetValue(new(instance.Object.RenderIdentity,instance.Object.VisualGeneration),out mapperOwnership);
                if (mapperOwnership is not null)
                {
                    planetTracker.MapperStartMilliseconds = mapperOwnership.MapperStartMilliseconds;
                    planetTracker.RuntimeSemanticsResolved = mapperOwnership.RuntimeSemanticsResolved;
                    planetTracker.Diagnostic ??= mapperOwnership.Diagnostic;
                    if (mapperOwnership.DayNightPhase is float phase)
                    {
                        currentRetailPltDayNightPhase = phase;
                        planetTracker.DayNightPhase = phase;
                        planetTracker.DominantOrbitalIndex = mapperOwnership.DominantOrbitalIndex;
                    }
                }
                else
                {
                    planetTracker.RuntimeSemanticsResolved = false;
                    planetTracker.Diagnostic ??= "materialized planet root has no generation-qualified mapper ownership";
                }
            }
            //50BB78/804DA0 create independent orbital SceneClass owners.
            //World/movie light and fog setters do not mutate those scenes.
            var rootLights = instance.OrbitalCamera.HasValue
                ? EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f,[]) : lightEnvironment;
            bool drawReady = TryBuildRetainedRootDrawList(instance, retailAssets,
                frame.CurrentClientTime, rootLights, out EnbW3dDrawInvocation drawList,
                out string? drawDiagnostic, rootAlreadyPrepared: true);
            if (!drawReady)
            {
                missingOrUnsupported++;
                drawPacketCompileRejected++;
                firstDrawDiagnostic ??= drawDiagnostic;
                if (planetTracker is not null)
                {
                    planetTracker.RuntimeSemanticsResolved = false;
                    planetTracker.Diagnostic ??= drawDiagnostic ?? "planet draw-packet compilation rejected";
                }
                continue;
            }

            if (instance.ShipHlodDecision is LiveShipHlodDecision compiledShipDecision)
            {
                compiledShipDecision.CompiledPacketCount = drawList.Packets.Count;
                compiledShipDecision.CompiledHierarchyRecordPacketCount = drawList.Packets.Count(static packet =>
                    packet.HierarchyName is not null && packet.PivotIndex >= 0);
            }

            EnbEnvironmentMapAxis? colorizationAxis = environmentMapResolved
                ? environmentReflection.Axis
                : null;
            int colorizationRegion = EnbCurrentRetailShipColorizationRuntime.ResolveRegion(instance.InstancePath);
            bool applyColorizationMutation = EnbCurrentRetailShipColorizationRuntime.IsInstanceInCapturedRenderObjectScope(
                instance.InstancePath,
                instance.Object.ShipLegos,
                instance.Object.SubParts,
                instance.Object.ColorizationAudit);
            int instancePacketStart = traversalPackets.Count;
            int surfaceOrdinal=0;
            foreach (EnbW3dDrawTask authoredPacket in drawList.Packets)
            {
                if (instance.Root.AllowedRenderObjectNames is { Count: > 0 } allowed &&
                    !allowed.Contains(authoredPacket.RenderObjectName))
                {
                    continue;
                }

                EnbW3dDrawTask packet = authoredPacket;
                if (currentRetailPltDayNightPhase.HasValue || authoredPacket.RenderInstancePath != instance.InstancePath)
                    packet = (authoredPacket).ForkTask().SetCurrentRetailPltDayNightPhase(currentRetailPltDayNightPhase ?? authoredPacket.CurrentRetailPltDayNightPhase).SetRenderInstancePath(instance.InstancePath);

                var preparedBaseMaterial = _overbrightMaterials.Resolve(
                    packet,
                    instance.Root.OverbrightOff || _customizationPreview,
                    out EnbCurrentRetailBaseAssetOverbrightReceipt overbrightReceipt);
                if (instance.Object.IsLocalShip)
                {
                    if (overbrightReceipt.Enabled)
                        shipLegoTracker.OverbrightEligiblePackets++;
                    else
                        shipLegoTracker.OverbrightDisabledPackets++;
                    if (overbrightReceipt.Applied)
                    {
                        shipLegoTracker.OverbrightAppliedPackets++;
                        shipLegoTracker.OverbrightTargetTriangles += overbrightReceipt.TargetTriangles;
                        shipLegoTracker.OverbrightAppliedTriangles += overbrightReceipt.AppliedTriangles;
                    }
                    else if (overbrightReceipt.Failed)
                    {
                        shipLegoTracker.OverbrightUnresolvedPackets++;
                        shipLegoTracker.OverbrightFirstDiagnostic ??= overbrightReceipt.Diagnostic;
                    }
                }

                EnbShipMaterialCache.SurfaceBinding? surfaceBinding=null;
                if(_retainedOrdinaryMeshes)
                {
                    if(surfaceOrdinal==instance.SurfaceBindings.Count)instance.SurfaceBindings.Add(new());
                    surfaceBinding=instance.SurfaceBindings[surfaceOrdinal++];
                }
                long surfacePreparation=surfaceBinding?.PreparationCount??0;
                packet = _shipMaterials.ApplySurface(packet, instance.Object, instance.InstancePath,
                    colorizationRegion, colorizationAxis, applyColorizationMutation,
                    retailAssets.MetalEquivalentRgbs, retailAssets.Decals, out var materialReceipts,_swatchCatalog, preparedBaseMaterial,surfaceBinding);
                if(surfaceBinding is not null && surfaceBinding.PreparationCount!=surfacePreparation)instance.SurfaceLayoutRevision++;
                var colorizationReceipt = materialReceipts.Color;
                if (instance.Object.IsLocalShip && colorizationReceipt.IsTarget)
                {
                    shipLegoTracker.ColorizationTargetPackets++;
                    shipLegoTracker.ColorizationTargetTriangles += colorizationReceipt.TargetTriangles;
                    shipLegoTracker.ColorizationRegions.Add(colorizationReceipt.Region);
                    shipLegoTracker.ColorizationTargetInstancePaths.Add(instance.InstancePath);
                    if (string.Equals(instance.InstancePath, "root", StringComparison.Ordinal))
                        shipLegoTracker.ColorizationRootPackets++;
                    else if (instance.InstancePath.StartsWith("lego/", StringComparison.Ordinal))
                        shipLegoTracker.ColorizationLegoPackets++;
                    else if (instance.InstancePath.StartsWith("fixed/", StringComparison.Ordinal))
                        shipLegoTracker.ColorizationFixedPackets++;
                    else
                        shipLegoTracker.ColorizationOtherPackets++;
                    if (colorizationReceipt.Outcome ==
                        EnbCurrentRetailShipColorizationOutcome.OutsideCapturedRenderObjectScope)
                    {
                        shipLegoTracker.ColorizationExcludedPackets++;
                        shipLegoTracker.ColorizationExcludedTriangles += colorizationReceipt.TargetTriangles;
                        shipLegoTracker.ColorizationExcludedInstancePaths.Add(instance.InstancePath);
                    }
                    else if (colorizationReceipt.Outcome == EnbCurrentRetailShipColorizationOutcome.MissingEnvironmentMap &&
                        frame.Snapshot.Environment is null && !_customizationPreview)
                    {
                        // Color packets can precede sector environment data.
                        // Once it arrives, invalid/missing assets remain failures.
                        shipLegoTracker.ColorizationPendingPackets++;
                    }
                    else if (colorizationReceipt.Applied)
                    {
                        shipLegoTracker.ColorizationAppliedPackets++;
                        shipLegoTracker.ColorizationAppliedTriangles += colorizationReceipt.AppliedTriangles;
                        shipLegoTracker.ColorizationPrimaryTriangles += colorizationReceipt.PrimaryTriangles;
                        shipLegoTracker.ColorizationSecondaryTriangles += colorizationReceipt.SecondaryTriangles;
                        shipLegoTracker.ColorizationSkinTriangles += colorizationReceipt.SkinTriangles;
                    }
                    else
                    {
                        shipLegoTracker.ColorizationUnresolvedPackets++;
                        shipLegoTracker.ColorizationFirstDiagnostic ??= colorizationReceipt.Diagnostic;
                    }
                }

                var weaponMetalReceipt = materialReceipts.Metal;
                if (instance.Object.IsLocalShip && weaponMetalReceipt.IsTarget)
                {
                    shipLegoTracker.WeaponMetalTargetPackets++;
                    shipLegoTracker.WeaponMetalTargetTriangles += weaponMetalReceipt.TargetTriangles;
                    shipLegoTracker.WeaponMetalRegions.Add(weaponMetalReceipt.Region);
                    if (weaponMetalReceipt.Applied)
                    {
                        shipLegoTracker.WeaponMetalAppliedPackets++;
                        shipLegoTracker.WeaponMetalAppliedTriangles += weaponMetalReceipt.AppliedTriangles;
                    }
                    else if (weaponMetalReceipt.Outcome != EnbCurrentRetailWeaponMetalColorOutcome.Unchanged)
                    {
                        shipLegoTracker.WeaponMetalUnresolvedPackets++;
                        shipLegoTracker.WeaponMetalFirstDiagnostic ??= weaponMetalReceipt.Diagnostic;
                    }
                }

                var decalReceipt = materialReceipts.Decal;
                if (instance.Object.IsLocalShip && decalReceipt.IsTarget)
                {
                    shipLegoTracker.DecalTargetPackets++;
                    if (decalReceipt.Applied)
                    {
                        shipLegoTracker.DecalAppliedPackets++;
                        if (decalReceipt.Outcome == EnbCurrentRetailShipDecalOutcome.AppliedTextureAndTint)
                            shipLegoTracker.DecalTintedPackets++;
                    }
                    else if (decalReceipt.Outcome == EnbCurrentRetailShipDecalOutcome.NoSlotAssignment)
                    {
                        shipLegoTracker.DecalUnassignedPackets++;
                    }
                    else
                    {
                        shipLegoTracker.DecalUnresolvedPackets++;
                        shipLegoTracker.DecalFirstDiagnostic ??= decalReceipt.Diagnostic;
                    }
                }

                if (decalReceipt.Outcome is EnbCurrentRetailShipDecalOutcome.Hidden or EnbCurrentRetailShipDecalOutcome.OmittedDescriptorPass) continue;
                EnbCurrentRetailShipNameDecalTexture? generatedNameDecal = null;
                string? nameDecalBuildDiagnostic = null;
                if (EnbCurrentRetailShipNameDecalRuntime.IsTarget(packet.RenderObjectName) &&
                    instance.Object.NameDecal is EnbSceneNameDecalSnapshot retainedNameDecal)
                {
                    var decalOwner=_ownedSceneFrame?instance.PreparedOwner?.SceneSource:null;
                    if(decalOwner is not null && (decalOwner.Definition.RenderIdentity!=packet.RenderIdentity ||
                        decalOwner.Definition.VisualGeneration!=packet.VisualGeneration))decalOwner=null;
                    LiveNameDecalTextureKey nameDecalKey=default;
                    if(decalOwner is not null)generatedNameDecal=decalOwner.NameDecalTexture;
                    else
                    {
                        nameDecalKey=new(_assetGeneration,packet.RenderIdentity,packet.VisualGeneration,instance.Object.NameDecalApplyCount);
                        _nameDecalTextures.TryGetValue(nameDecalKey,out generatedNameDecal);
                    }
                    if(generatedNameDecal is null)
                    {
                        if(nameDecalFont?.Image is not EnbTextureImage fontImage)
                            nameDecalBuildDiagnostic=nameDecalFont?.Diagnostic??
                                $"Texture resource '{EnbCurrentRetailShipNameDecalRuntime.RetailFontTextureName}' was not resolved.";
                        else if(EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(retainedNameDecal,fontImage,
                            EnbCurrentRetailShipNameDecalRuntime.CreateLogicalTextureName(packet.RenderIdentity,packet.VisualGeneration),
                            out EnbCurrentRetailShipNameDecalTexture built,out nameDecalBuildDiagnostic))
                        {
                            generatedNameDecal=built;
                            if(decalOwner is not null)decalOwner.SetNameDecalTexture(built);
                            else _nameDecalTextures.Add(nameDecalKey,built);
                        }
                    }

                    if (generatedNameDecal is not null && decalOwner is null)
                        frameNameDecalTextures[generatedNameDecal.LogicalName] = generatedNameDecal.Image;
                }

                var nameBinding=surfaceBinding?.Name;
                long namePreparation=nameBinding?.PreparationCount??0;
                packet = _shipMaterials.ApplyName(
                    packet,
                    instance.Object,
                    generatedNameDecal,
                    colorizationRegion, colorizationAxis, applyColorizationMutation,
                    out EnbCurrentRetailShipNameDecalReceipt nameDecalReceipt,nameBinding);
                if(nameBinding is not null && nameBinding.PreparationCount!=namePreparation)instance.SurfaceLayoutRevision++;
                if (instance.Object.IsLocalShip && nameDecalReceipt.IsTarget)
                {
                    shipLegoTracker.NameDecalTargetPackets++;
                    if (nameDecalReceipt.Applied)
                    {
                        shipLegoTracker.NameDecalAppliedPackets++;
                        shipLegoTracker.NameDecalTextureWidth = generatedNameDecal!.Image.Width;
                        shipLegoTracker.NameDecalTextureHeight = generatedNameDecal.Image.Height;
                        shipLegoTracker.NameDecalTextWidth = generatedNameDecal.TextWidth;
                    }
                    else
                    {
                        shipLegoTracker.NameDecalUnresolvedPackets++;
                        shipLegoTracker.NameDecalFirstDiagnostic ??=
                            nameDecalBuildDiagnostic ?? nameDecalReceipt.Diagnostic;
                    }
                }

                if (planetTracker is not null)
                {
                    EnbCurrentRetailW3dGeometryClass geometryClass =
                        EnbCurrentRetailCameraGeometryRuntime.Classify(packet.Mesh.Attributes);
                    switch (geometryClass)
                    {
                        case EnbCurrentRetailW3dGeometryClass.Normal:
                            planetTracker.NormalGeometryPackets++;
                            break;
                        case EnbCurrentRetailW3dGeometryClass.CameraAligned:
                            planetTracker.CameraAlignedGeometryPackets++;
                            break;
                        case EnbCurrentRetailW3dGeometryClass.CameraOriented:
                            planetTracker.CameraOrientedGeometryPackets++;
                            break;
                        case EnbCurrentRetailW3dGeometryClass.Skin:
                            planetTracker.SkinGeometryPackets++;
                            break;
                        default:
                            planetTracker.UnsupportedGeometryPackets++;
                            break;
                    }
                    if (currentRetailPltDayNightPhase.HasValue && PacketContainsMutablePltDayNightBinding(packet))
                        planetTracker.DayNightPackets++;

                    // Later mesh-frustum skips are accounted separately from failed submissions.
                    planetTracker.ExpectedPackets++;
                }

                traversalPackets.Add(packet);
                if (instance.ShipHlodDecision is LiveShipHlodDecision admittedShipDecision)
                {
                    admittedShipDecision.AdmittedPacketCount++;
                    if (packet.HierarchyName is not null && packet.PivotIndex >= 0)
                        admittedShipDecision.AdmittedHierarchyRecordPacketCount++;
                }
            }

            int swatchSourcePackets = ApplyOwnedSwatchGroups(traversalPackets, instancePacketStart, instance);
            BindRslPassContext(traversalPackets, instancePacketStart, instance, rslRuntimeUpdates);
            RetainOwnedAppearance(traversalPackets, instancePacketStart, ReadMapperClock(),instance);
            if (shadowRoots.Contains(instance))
            {
                shadowAppearance.Add(instance, traversalPackets.GetRange(instancePacketStart, traversalPackets.Count - instancePacketStart));
            }
            if (!mainViewAdmitted)
            {
                traversalPackets.RemoveRange(instancePacketStart, traversalPackets.Count - instancePacketStart);
                continue;
            }
            if (instance.Object.IsLocalShip && swatchSourcePackets > 0)
            {
                shipLegoTracker.DecalUnresolvedPackets -= swatchSourcePackets;
                shipLegoTracker.DecalAppliedPackets += swatchSourcePackets;
                if (shipLegoTracker.DecalUnresolvedPackets == 0)
                    shipLegoTracker.DecalFirstDiagnostic = null;
            }
            foreach (var dazzle in drawList.Dazzles)
                _frameDazzles.Add((RetainDazzleHistory(instance,dazzle), instance.OrbitalVisibility, instance.OrbitalCamera ?? frame.Camera,
                    instance.OrbitalCamera.HasValue ? (frame.Movie is not null ? 1 : frame.Snapshot.Environment?.PresentationMode == 1 ? .96f : frame.Snapshot.Environment?.PresentationMode == 5 ? .97f : .95f) : 0,
                    frame.Movie is not null || instance.OrbitalCamera.HasValue || !orbitalCamera.HasValue ? 1 : .95f));
            AppendProjectedDecals(instance, drawList, traversalPackets, frame.Camera);
            foreach (EnbW3dParticleEmitterDrawInstance emitter in drawList.ParticleEmitters)
            {
                if (IsRetainedAggregateLine(instance, selection, emitter))
                    continue;
                if (instance.Root.AllowedRenderObjectNames is { Count: > 0 } allowed &&
                    !allowed.Contains(emitter.RenderObjectName))
                {
                    continue;
                }

                particleEmitterInstances++;
                var emitterKey = new LiveParticleEmitterKey(
                    _assetGeneration,
                    drawList.RenderIdentity,
                    drawList.VisualGeneration,
                    drawList.ResourceKey,
                    instance.InstancePath,
                    emitter.EmitterResourceName,
                    emitter.PivotIndex,
                    emitter.BindingOrdinal);
                usedParticleEmitterKeys.Add(emitterKey);
                long now = frame.CurrentClientTime;
                var emitterTimes = (_shipExhaustOwner ?? this)._particleEmitterStartTimes;
                if (!emitterTimes.TryGetValue(emitterKey, out long emitterStart) || now < emitterStart)
                {
                    emitterStart = now;
                    emitterTimes[emitterKey] = emitterStart;
                }

                IReadOnlyList<EnbCurrentRetailParticleBirth>? inheritedBirths=null;
                EnbRetailParticleMotionHistory? inheritedMotion=null;
                {
                    var birthsOwner=(_shipExhaustOwner ?? this)._ordinaryParticleBirths;
                    uint age=checked((uint)(now-emitterStart));
                    bool initial=!birthsOwner.TryGetValue(emitterKey,out var births) || age<births.Age;
                    if(initial)births=(EnbCurrentRetailAnimatedParticleRuntime.CreateEmitter(emitter.Definition),age);
                    if(initial || age!=births.Age)
                    {
                        Matrix4x4.Decompose(emitter.WorldTransform,out var emitterScale,out _,out _);
                        EnbCurrentRetailAnimatedParticleRuntime.AdvanceEmitter(births.State,emitter.Definition,age,
                            initial?0:age-births.Age,emitter.WorldTransform,true,emitterScale.X,initial);
                        birthsOwner[emitterKey]=(births.State,age);
                    }
                    inheritedBirths=births.State.WorldBirths.Select(b=>b with {Time=b.Time+emitterStart}).ToArray();
                    inheritedMotion=births.State.MotionHistory;
                }

                if (!EnbCurrentRetailParticleEmitterRuntime.TryBuildPacket(
                        drawList.ResourceKey,
                        drawList.RenderIdentity,
                        drawList.VisualGeneration,
                        emitter,
                        frame.Camera,
                        rootLights,
                        ComputeStableParticleEmitterSalt(instance.InstancePath),
                        emitterStart,
                        now,
                        out EnbW3dDrawTask? particlePacket,
                        out EnbCurrentRetailParticleEmitterReceipt particleReceipt,
                        out string? particleDiagnostic,
                        worldBirths: inheritedBirths,motionHistory:inheritedMotion,cullToCamera:FrustumCullingEnabled))
                {
                    missingOrUnsupported++;
                    drawPacketCompileRejected++;
                    particleEmitterRejected++;
                    firstDrawDiagnostic ??= particleDiagnostic;
                    continue;
                }

                particleEmitterLiveParticles += particleReceipt.LiveParticleCount;
                if (particlePacket is not null && (!FrustumCullingEnabled || instance.OrbitalForceVisible ||
                    EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(instance.OrbitalCamera ?? frame.Camera,
                        particlePacket.Bounds)))
                    traversalPackets.Add(particlePacket);
            }
        }

        long profileMeshPacketsEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.shadows");
        UpdateDynamicShadowCaptures(gl, fb, frame, retailAssets, roots, shadowAppearance,
            fogEvaluation.TerrainLight, frameNameDecalTextures, environmentAtlasSet,
            detailSettings.Texture, textureCapabilities, radarDetachedIdentities);
        long profileShadowsEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.terrain");
        var terrainPackets = packetTasks.Terrain;
        AppendTerrainPackets(frame, retailAssets, lightEnvironment, terrainPackets,
            fogEvaluation.TerrainLight, frameNameDecalTextures, roots,detailSettings.LandLod);
        traversalPackets.InsertRange(0, terrainPackets);
        long profileTerrainEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.effect-packets");
        using var effectStages=FullClientBenchmarkMeter.BeginStages(worldStages is not null,"effect-phase.retirement");

        foreach (LiveParticleEmitterKey staleEmitter in _particleEmitterStartTimes.Keys
                     .Where(key => !usedParticleEmitterKeys.Contains(key))
                     .ToArray())
        {
            _particleEmitterStartTimes.Remove(staleEmitter);
            _ordinaryParticleBirths.Remove(staleEmitter);
        }

        effectStages?.Next("effect-phase.visuals");
        ApplyEffectVisuals(traversalPackets,effectRslSchedulerUpdate?.VisualNodes ?? [],frame.Snapshot,retailAssets,roots,ReadMapperClock(),frame.CurrentClientTime);
        effectStages?.Next("effect-phase.particles");
        DrawShipExhaust(traversalPackets, frame, lightEnvironment);
        AppendRetainedEmitters(traversalPackets,frame.Camera,lightEnvironment,frame.CurrentClientTime,
            _shipExhaustOwner is null?null:liveRslOwners.Union(SceneObjects(frame.Snapshot).Select(o=>(o.RenderIdentity,o.VisualGeneration))).ToHashSet());
        effectStages?.Next("effect-phase.aggregate-lines");
        int aggregateLineRejected = 0;
        DrawAggregateLines(traversalPackets, frame, lightEnvironment, ref particleEmitterInstances,
            ref particleEmitterLiveParticles, ref aggregateLineRejected, ref firstDrawDiagnostic);
        particleEmitterRejected += aggregateLineRejected;
        drawPacketCompileRejected += aggregateLineRejected;
        missingOrUnsupported += aggregateLineRejected;
        effectStages?.Next("effect-phase.attached");
        ReportBloodDraws(traversalPackets);
        DrawWarpPlumes(traversalPackets, frame, lightEnvironment);
        DrawAttachedEffects(traversalPackets, frame, lightEnvironment);
        effectStages?.Next("effect-phase.objects");
        traversalPackets.AddRange(preparedEffectPackets);
        DrawPreparedEffectObjects(traversalPackets,effectRslSchedulerUpdate?.VisualNodes??[],frame.Snapshot,
            lightEnvironment,frame.Camera,frame.CurrentClientTime,preparedEffectObjects);
        DrawWarpTunnels(traversalPackets,effectRslSchedulerUpdate?.VisualNodes??[],frame,retailAssets,lightEnvironment);
        effectStages?.Next("effect-phase.lines-jets");
        PublishEffectPositions(effectRslSchedulerUpdate?.VisualNodes??[],roots,retailAssets);
        DrawEffectLines(traversalPackets,effectRslSchedulerUpdate?.VisualNodes??[],frame,retailAssets,roots);
        DrawParticleJets(traversalPackets,effectRslSchedulerUpdate?.VisualNodes??[],frame,retailAssets,lightEnvironment);
        effectStages?.Next("effect-phase.camera-query");
        UpdateCameraQueries(roots,frame);
        effectStages?.Next("effect-phase.occlusion-query");
        if ((!DeveloperRenderDiagnostics.Available || DeveloperRenderDiagnostics.Dazzles) && _frameDazzles.Count != 0) PrepareDazzleCollision(roots, _useMainViewEnvironment && !frame.Snapshot.IsTransitioning && frame.Snapshot.Environment is {PresentationMode:1} env && _terrain?.Sector == env.SectorNum ? _terrain : null);
        effectStages?.Next("effect-phase.order");
        if(DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Particles)
            traversalPackets.RemoveAll(packet=>packet.CurrentRetailParticleBuffer);
        if(DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Effects)
            traversalPackets.RemoveAll(packet=>packet.RenderInstancePath is {} path &&
                (path.StartsWith("effect-",StringComparison.Ordinal) || path.Contains("/effect/",StringComparison.Ordinal)));
        if (!EnbW3dFrameSubmissionOrderCompiler.TryCompile(
                EnbRenderPassKind.OrdinaryWorld,
                traversalPackets,
                out EnbW3dFrameSubmissionOrder order,
                out string? orderDiagnostic,packetTasks.Sort))
        {
            Volatile.Write(ref _status, new LiveWorldW3dStatus(
                $"R6Q live W3D: frame order rejected · {orderDiagnostic ?? "unknown"}"));
            return false;
        }
        effectStages?.Dispose();
        long profilePacketsEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.submission");

        long residencyFrame = ++_gpuResidencyFrame;
        int submittedPackets = 0;
        int drawCalls = 0;
        int preparedSubmissionBatches = 0;
        int primitives = 0;
        int vertices = 0;
        int transientPackets = 0;
        int submissionRejected = 0;
        int laneUnsupported = 0;
        int laneCompileRejected = 0;
        var laneMissEvidence = new List<string>();
        int lanePlanCacheHits = 0;
        int lanePlanCacheMisses = 0;
        int gpuCreateRejected = 0;
        string? firstPipelineDiagnostic = null;
        var laneRejections = new List<LiveLaneRejection>();
        var renderedObjects = _frameRenderedObjects;

        _indexedSubmissions.ExplicitResidencyEnabled=_ownedSceneFrame;
        _indexedSubmissions.IndexedSweepEnabled = ReadSweepExperiment();
        _indexedSubmissions.BeginFrame();
        uint frameMapperTime = ReadMapperClock();
        bool planetOrbitalsFirst = frame.Snapshot.Environment is { PresentationMode: 1, SwapBackdrop: false };
        foreach(var packet in order.Packets)
            packetTasks.Add(packet.RenderIdentity == PlanetBackgroundIdentity ? -2 :
                packet.RenderIdentity == PlanetSkyIdentity ? -1 :
                orbitalOwners.Contains(packet.RenderIdentity) ? planetOrbitalsFirst ? -3 : 0 : 1,packet);
        var meshLayers=packetTasks.Active;
        // Keep the scene boundaries explicit: swapping the entire world would put
        // the backdrop over ships. 50E090 reverses only sky110 and orbital11C.
        bool movieStarsDrawn = false; double meshBatchMilliseconds = 0;
        double laneMilliseconds = 0, submitMilliseconds = 0, pickMilliseconds = 0;
        double rigidLaneMs=0, skinLaneMs=0, particleLaneMs=0, terrainLaneMs=0, otherLaneMs=0;
        double skinPrepareMs=0, skinResourceMs=0;
        double slowestSubmit = 0, specialMilliseconds = 0;
        string? slowestSubmitName = null;
        _texturedBackend.ResetSubmissionTiming();
        bool retainedSubmissionExperiment = ReadRetainedSubmissionExperiment();
        void DrawMovieStars()
        {
            if ((DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Backdrop) || frame.Movie is null || movieStarsDrawn) return;
            _backdropRenderer.RenderSpace(gl, fb, pixelWidth, pixelHeight, frame.Backdrop, frame.Camera,
                EnbSpaceScenePass.Stars, null, null);
            movieStarsDrawn = true;
        }
        foreach (var spacePass in spaceComposition
            ? frame.Snapshot.Environment!.PresentationMode == 5 ? EnbRetailSpaceComposition.GasPasses : EnbRetailSpaceComposition.Passes(frame.Snapshot.Environment.SwapBackdrop, SpaceStarsEnabled)
            : new[] { EnbSpaceScenePass.World })
        {
        if (spaceComposition)
        {
            if(spacePass==EnbSpaceScenePass.Backdrop && DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Backdrop)continue;
            if (spacePass == EnbSpaceScenePass.Stars || spacePass == EnbSpaceScenePass.Backdrop && _influenceSkyPresentation.OverrideActive)
            {
                if((DeveloperRenderDiagnostics.Available && !DeveloperRenderDiagnostics.Backdrop))continue;
                _backdropRenderer.RenderSpace(gl, fb, pixelWidth, pixelHeight, frame.Backdrop, frame.Camera, spacePass,
                    () => SpacePassObserved?.Invoke(spacePass), SpaceGeometryObserved,
                    frame.Snapshot.Environment!.PresentationMode == 5 ? new Vector2(.95f,.97f) : null);
                continue;
            }
            ApplyOrbitalDepthRange(gl, spacePass == EnbSpaceScenePass.Orbitals, orbitalMinimum: frame.Snapshot.Environment!.PresentationMode == 5 ? .97f : .95f);
            SpacePassObserved?.Invoke(spacePass);
        }
        // 9661C0 installs scene lights after capture and before object traversal.
        // A special-only scene must not inherit capture or preceding-scene lights.
        _specialDeviceLights=lightEnvironment;
        foreach(var materialLayer in meshLayers.Where(layer => !spaceComposition || (spacePass == EnbSpaceScenePass.Backdrop ? layer.Key == -1 : spacePass == EnbSpaceScenePass.Orbitals ? layer.Key == 0 : layer.Key == 1)))
        {
        bool independentEnvironmentLayer = frame.Movie is not null && materialLayer.Key == -1 || materialLayer.Any(packet => orbitalOwners.Contains(packet.RenderIdentity));
        var layerLights = independentEnvironmentLayer ? EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f,[]) : lightEnvironment;
        _specialDeviceLights = layerLights;
        if (materialLayer.Key >= 0) DrawMovieStars();
        if (frame.Movie is not null && materialLayer.Key == 1)
            RenderDazzles(gl, fb, pixelWidth, pixelHeight, retailAssets, true, detailedDiagnosticsDue);
        long meshBatchStart = Stopwatch.GetTimestamp();
        var meshDraws = CompileMeshDraws(materialLayer,
                texture => frameNameDecalTextures.TryGetValue(texture.LogicalName, out var image)
                    ? image : retailAssets.Textures.Resolve(texture.LogicalName), frameMapperTime,
                packet=>packet.RenderIdentity==PlanetBackgroundIdentity?planetBackgroundCamera!.Value:
                    packet.RenderIdentity==PlanetSkyIdentity?planetSkyCamera!.Value:
                    orbitalOwners.Contains(packet.RenderIdentity)?orbitalCamera!.Value:frame.Camera, planetGpuTrackers);
        meshBatchMilliseconds += Stopwatch.GetElapsedTime(meshBatchStart).TotalMilliseconds;
        using var depthRangeScope = new OrbitalDepthRangeScope(this);
        using var retainedSubmissionScope = _texturedBackend.BeginRetainedSubmission(gl, retainedSubmissionExperiment);
        bool specialFlushed=false;
        void FlushSpecialLayer()
        {
            _texturedBackend.InterruptRetainedSubmission();
            specialFlushed=true;
        long specialStarted=Stopwatch.GetTimestamp();
        var specialReceipt=FlushSpecialMaterials(gl,fb,pixelWidth,pixelHeight,materialLayer,
            packet=>packet.RenderIdentity==PlanetBackgroundIdentity?planetBackgroundCamera!.Value:
                packet.RenderIdentity==PlanetSkyIdentity?planetSkyCamera!.Value:
                orbitalOwners.Contains(packet.RenderIdentity)?orbitalCamera!.Value:frame.Camera,
            layerLights,independentEnvironmentLayer ? new Vector3(.5f) : frame.Movie?.Ambient ?? mainViewLighting?.SpecialMaterialAmbient??fogEvaluation.SpecialMaterialAmbient??
                new Vector3((effectiveAmbientRgb24>>16&255)/255f,(effectiveAmbientRgb24>>8&255)/255f,(effectiveAmbientRgb24&255)/255f),
            retailAssets,detailSettings.Texture,textureCapabilities,beforeDraw:packet=>
            {
                if(!(spaceComposition || orbitalCamera.HasValue || planetSkyCamera.HasValue))return;
                bool isOrbital=orbitalOwners.Contains(packet.RenderIdentity);
                ApplyOrbitalDepthRange(gl,isOrbital,frame.Movie is not null ? 1 : .95f, frame.Snapshot.Environment?.PresentationMode == 5 ? .97f : .95f);
                if(isOrbital && frame.Snapshot.Environment?.PresentationMode==1)_setOrbitalDepthRange!(.96f,1f);
                if(packet.RenderIdentity==PlanetBackgroundIdentity)_setOrbitalDepthRange!(.96f,.96f);
                if(packet.RenderIdentity==PlanetSkyIdentity)
                {
                    bool swapped=frame.Movie is not null || frame.Snapshot.Environment!.PresentationMode==0 || frame.Snapshot.Environment.PresentationMode==1 && frame.Snapshot.Environment.SwapBackdrop;
                    _setOrbitalDepthRange!(swapped?1f:.95f,swapped?1f:frame.Snapshot.Environment!.PresentationMode==5?.975f:.96f);
                }
            });
        specialMilliseconds+=Stopwatch.GetElapsedTime(specialStarted).TotalMilliseconds;
        drawCalls+=specialReceipt.DrawCalls;primitives+=specialReceipt.Primitives;submittedPackets+=specialReceipt.DrawCalls;
        vertices+=specialReceipt.Vertices;
        if(specialReceipt.Diagnostics.Count!=0)
        {gpuCreateRejected+=specialReceipt.Diagnostics.Count;firstPipelineDiagnostic??=specialReceipt.Diagnostics[0];}
        foreach(var packet in materialLayer.Where(p=>p.SpecialMaterial is not null))renderedObjects.Add(packet.RenderIdentity);
        }
        preparedSubmissionBatches += meshDraws.Count;
        foreach (var meshDraw in meshDraws)
        {
            if(!specialFlushed && meshDraw.Packet.OrderKey.StaticSort.RuntimeLevel!=0)FlushSpecialLayer();
            var packet = meshDraw.Packet;
            bool isOrbital = orbitalOwners.Contains(packet.RenderIdentity);
            bool isPlanetSky = packet.RenderIdentity == PlanetSkyIdentity;
            bool isPlanetBackground = packet.RenderIdentity == PlanetBackgroundIdentity;
            EnbResolvedRenderCamera packetCamera = isPlanetBackground ? planetBackgroundCamera!.Value : isPlanetSky ? planetSkyCamera!.Value : isOrbital ? orbitalCamera!.Value : frame.Camera;
            if (spaceComposition || orbitalCamera.HasValue || planetSkyCamera.HasValue)
            {
                ApplyOrbitalDepthRange(gl, isOrbital, frame.Movie is not null ? 1 : .95f, frame.Snapshot.Environment?.PresentationMode == 5 ? .97f : .95f);
                if (isOrbital && frame.Snapshot.Environment?.PresentationMode == 1)
                    _setOrbitalDepthRange!(.96f, 1f);
                if (isPlanetBackground) _setOrbitalDepthRange!(.96f, .96f);
                if (isPlanetSky)
                {
                    bool swapped = frame.Movie is not null || frame.Snapshot.Environment!.PresentationMode == 0 || frame.Snapshot.Environment.PresentationMode == 1 && frame.Snapshot.Environment.SwapBackdrop;
                    _setOrbitalDepthRange!(swapped ? 1f : .95f, swapped ? 1f : frame.Snapshot.Environment!.PresentationMode == 5 ? .975f : .96f);
                }
            }
            // Planet presentation is atomic across all authored GPU packets.
            // If current-retail runtime semantics for the object are unresolved,
            // do not submit a partial planet.
            if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? preflightPlanet) &&
                !preflightPlanet.RuntimeSemanticsResolved)
            {
                continue;
            }

            if (meshDraw.Submission is not {} submission)
            {
                string? submissionDiagnostic = meshDraw.Diagnostic;
                missingOrUnsupported++;
                submissionRejected++;
                firstPipelineDiagnostic ??= submissionDiagnostic;
                if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? planetTracker))
                {
                    planetTracker.RejectedPackets++;
                    planetTracker.Diagnostic ??= submissionDiagnostic ?? "planet indexed submission rejected";
                }
                continue;
            }

            if (environmentAtlasSet is not null &&
                shipLegoTracker.LocalShipSeen &&
                packet.GameId == shipLegoTracker.LocalShipGameId &&
                submission.Runs.Any(run => run.Binding.Textures.Any(binding =>
                    binding is not null && environmentAtlasSet.ProceduralTextures.ContainsKey(binding.LogicalName))))
            {
                shipLegoTracker.EnvironmentAtlasTargetPackets++;
            }

            LiveLaneKind laneKind = SelectLane(submission);
            if (laneKind == LiveLaneKind.Unsupported)
            {
                missingOrUnsupported++;
                laneUnsupported++;
                string unsupportedDiagnostic = DescribeUnsupportedLane(submission);
                firstPipelineDiagnostic ??= unsupportedDiagnostic;
                laneRejections.Add(CreateLaneRejection(SceneObjects(frame.Snapshot), packet, laneKind, unsupportedDiagnostic));
                if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? planetTracker))
                {
                    planetTracker.RejectedPackets++;
                    planetTracker.Diagnostic ??= unsupportedDiagnostic;
                }
                continue;
            }

            var preparedBindings=packet.GetPreparedConsumerState() as PreparedGpuBindings;
            int preparedBatch=meshDraw.PreparedBatchOrdinal??-1;
            // Skin streams change every draw, but their mapper context belongs
            // to the same retained owner as their registered descriptor ranges.
            if (_retainedOrdinaryMeshes && _ownedSceneFrame && preparedBatch >= 0 &&
                (preparedBindings is null || !ReferenceEquals(preparedBindings.Context,_preparedGpuContext) ||
                    preparedBindings.Slots.Length != meshDraw.PreparedBatchCount))
            {
                preparedBindings=new(_preparedGpuContext,meshDraw.PreparedBatchCount);
                if(_ownedScene!.TryFind(packet.RenderIdentity,out var mapperNode) && mapperNode.Definition.VisualGeneration==packet.VisualGeneration)
                    preparedBindings.MapperOwner=mapperNode.State.MapperContext;
                packet.SetPreparedConsumerState(preparedBindings);
            }
            uint? mapperStartMillisecondsOverride=packet.CurrentRetailEffectMapperStartMilliseconds;
            if(mapperStartMillisecondsOverride is null)
            {
                if(_ownedSceneFrame&&preparedBindings?.MapperOwner is {} mapperSource&&ReferenceEquals(preparedBindings.Context,_preparedGpuContext))
                    mapperStartMillisecondsOverride=mapperSource.FrameMapperStartOverride??mapperSource.PlanetMapper?.MapperStartMilliseconds;
                else
                {
                    _packetMapperLookups++;
                    if (preparedBatch >= 0 && packet.RenderIdentity != ulong.MaxValue) _ordinaryPreparedMapperLookups++;
                    if(_traceSceneLookups){string site=$"{packet.RenderIdentity}/{packet.Mesh.Name}/batch{meshDraw.PreparedBatchOrdinal}/skin{packet.DeformedGeometry is not null}/binding{preparedBindings is not null}";_packetMapperLookupSites[site]=_packetMapperLookupSites.GetValueOrDefault(site)+1;}
                    var packetPlanetIdentity=new LivePlanetGpuIdentity(packet.RenderIdentity,packet.VisualGeneration);
                    mapperStartMillisecondsOverride=frame.MapperStartMillisecondsOverrides.TryGetValue(packetPlanetIdentity,out uint inherited)
                        ?inherited:_planetMapperStateOwnership.TryGetValue(packetPlanetIdentity,out var mapper)?mapper.MapperStartMilliseconds:null;
                }
            }
            PreparedGpuBinding? preparedBinding=null;
            if(_retainedOrdinaryMeshes && preparedBatch>=0 && packet.Mesh.CurrentRetailUsesMeshRenderer && packet.DeformedGeometry is null &&
                !packet.CurrentRetailParticleBuffer && !packet.CurrentRetailTerrainSea && !packet.CurrentRetailTerrainDisplacement)
            {
                if(preparedBindings is not null && preparedBatch<preparedBindings.Slots.Length &&
                    preparedBindings.Slots[preparedBatch] is {} binding && binding.Matches(_preparedGpuContext,_assetGeneration,laneKind,detailSettings.Texture.TextureReduction,
                        detailSettings.Texture.TextureFilter,environmentAtlasSet?.ReflectionTexture,mapperStartMillisecondsOverride,meshDraw.BatchIdentity))
                {preparedBinding=binding;_preparedGpuBindingHits++;}
                else _preparedGpuBindingMisses++;
            }
            var key = preparedBinding?.Key ?? new LiveGpuKey(
                _assetGeneration,
                packet.OrderKey.ResourceKey,
                packet.OrderKey.MeshOrdinal,
                packet.OrderKey.MaterialPassOrdinal,
                packet.RenderIdentity,
                packet.VisualGeneration,
                packet.OrderKey.BindingOrdinal,
                packet.RenderInstancePath,
                laneKind,
                detailSettings.Texture.TextureReduction,
                detailSettings.Texture.TextureFilter,
                environmentAtlasSet?.ReflectionTexture,
                mapperStartMillisecondsOverride,
                packet.CurrentRetailParticleBuffer ? null : meshDraw.BatchIdentity);

            bool transient = packet.DeformedGeometry is not null || packet.CurrentRetailParticleBuffer || packet.CurrentRetailTerrainSea || packet.CurrentRetailTerrainDisplacement;
            // Skins stream vertices; sea/displacement stream vertices, UVs and topology.
            // Both retain material mapper clocks with the GPU owner.
            bool streamedGeometry = transient && (packet.Mesh.BoneIndices.Count != 0 || packet.CurrentRetailTerrainSea || packet.CurrentRetailTerrainDisplacement ||
                ParticleStreamingEnabled && packet.CurrentRetailParticleBuffer) && laneKind == LiveLaneKind.Textured;
            LiveGpuResource? gpu = null;
            if (!transient || streamedGeometry)
                gpu=preparedBinding?.Resource ?? _gpuResources.GetValueOrDefault(key);
            LiveGpuResource? cachedGpu = gpu;

            EnbW3dSkinLaneOwner? activeSkinLane=null;
            try
            {
            LiveLanePlan lane;
            LiveCachedLanePlan? preparedLane=null;
            long laneStarted = Stopwatch.GetTimestamp();
            string? laneDiagnostic = null;
            if (streamedGeometry && packet.Mesh.BoneIndices.Count != 0 && gpu?.Textured is {} skinGpu &&
                skinGpu.LanePlan.Submission.Runs.SequenceEqual(submission.Runs) &&
                skinGpu.LanePlan.Submission.Packet.CurrentRetailColorizationRevision == packet.CurrentRetailColorizationRevision &&
                skinGpu.LanePlan.Submission.Packet.CurrentRetailNameDecalRevision == packet.CurrentRetailNameDecalRevision &&
                skinGpu.LanePlan.Submission.Packet.CurrentRetailDecalRevision == packet.CurrentRetailDecalRevision &&
                gpu!.TryPrepareSkinLane(submission,packetCamera,
                    !EnbW3dNPatchCompiler.IsEligible(packet,_texturedBackend.NPatchCapabilityEnabled,_texturedBackend.NPatchSegments),
                    out var skinLane,out activeSkinLane))
            {
                // As in station avatars, update the changing pose/normals while
                // retaining the immutable texture/material compilation.
                _skinNormalProofUpdates++;
                lane = LiveLanePlan.From(skinLane);
                lanePlanCacheHits++;
            }
            else if(streamedGeometry && packet.CurrentRetailParticleBuffer && gpu?.Textured is {} particleGpu &&
                EnbW3dProgrammableTextureLaneCompiler.TryUpdateParticleFrame(particleGpu.LanePlan,submission,packetCamera,
                    detailSettings.Texture,textureCapabilities,out var particleLane,out _))
            {
                lane=LiveLanePlan.From(particleLane);
                lanePlanCacheHits++;
            }
            else if (!transient &&
                (preparedLane=preparedBinding?.Lane ?? _lanePlans.GetValueOrDefault(key)) is {} cachedLane &&
                cachedLane.TryUpdateFrame(submission, packetCamera, out lane, compactTexturedFrame: true, retainedDefinition: preparedBinding is not null))
            {
                lanePlanCacheHits++;
            }
            else
            {
                lanePlanCacheMisses++;
                if (laneMissEvidence.Count < 12 && _lanePlans.TryGetValue(key, out var oldLane))
                {
                    var oldPacket = oldLane.Plan.Textured?.Submission.Packet ?? oldLane.Plan.Lit?.Submission.Packet;
                    laneMissEvidence.Add($"{packet.RenderObjectName}/b{packet.OrderKey.BindingOrdinal}: mesh={ReferenceEquals(oldLane.Mesh, packet.Mesh)} material={ReferenceEquals(oldLane.MaterialPass, packet.Material.Source)} order={oldPacket?.OrderKey == packet.OrderKey} old={oldLane.RenderObjectName}");
                }
                if (!TryCompileLane(
                        submission,
                        packetCamera,
                        detailSettings.Texture,
                        textureCapabilities,
                        retailAssets.Textures,
                        frameNameDecalTextures,
                        environmentAtlasSet,
                        laneKind,
                        out lane,
                        out laneDiagnostic))
                {
                    missingOrUnsupported++;
                    laneCompileRejected++;
                    string rejectionDiagnostic = laneDiagnostic ?? "unknown programmable-lane compile rejection";
                    rejectionDiagnostic += $" | {DescribeR6Z7LaneEvidence(submission, retailAssets.Textures)}";
                    firstPipelineDiagnostic ??= rejectionDiagnostic;
                    laneRejections.Add(CreateLaneRejection(SceneObjects(frame.Snapshot), packet, laneKind, rejectionDiagnostic));
                    if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? planetTracker))
                    {
                        planetTracker.RejectedPackets++;
                        planetTracker.Diagnostic ??= rejectionDiagnostic;
                    }
                    continue;
                }

                if (!transient)
                    _lanePlans[key] = preparedLane = LiveCachedLanePlan.Create(packet, packetCamera, lane);
            }

            double skinPrepared = _texturedBackend.MeasureSubmissionStages && packet.Mesh.BoneIndices.Count!=0 ? Stopwatch.GetElapsedTime(laneStarted).TotalMilliseconds : 0;
            if (detailedDiagnosticsDue && planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? geometryTracker) &&
                geometryTracker.CameraGeometrySampleMeshOrdinals.Add(packet.Mesh.Ordinal))
            {
                string? cameraGeometrySample = DescribePlanetCameraGeometrySample(packet, lane, frame.Camera, geometryTracker.Object);
                if (!string.IsNullOrWhiteSpace(cameraGeometrySample))
                    geometryTracker.CameraGeometrySamples.Add(cameraGeometrySample);
            }

            // Other transient geometry cannot own a mutable mapper. Skins and
            // sea use the persistent streaming path below.
            if (transient && !streamedGeometry && HasMutableTextureMapper(lane))
            {
                missingOrUnsupported++;
                if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? planetTracker))
                {
                    planetTracker.RejectedPackets++;
                    planetTracker.Diagnostic ??= "planet packet combined CPU-deformed geometry with mutable texture mapper state";
                }
                continue;
            }

            if (gpu?.Textured is {} retainedTextured &&
                (retainedTextured.LanePlan.Submission.Packet.CurrentRetailColorizationRevision != packet.CurrentRetailColorizationRevision ||
                 retainedTextured.LanePlan.Submission.Packet.CurrentRetailNameDecalRevision != packet.CurrentRetailNameDecalRevision ||
                 retainedTextured.LanePlan.Submission.Packet.CurrentRetailDecalRevision != packet.CurrentRetailDecalRevision))
            {
                // An identical material packet can replace retained owners
                // without replacing the RSL/animation VisualGeneration.
                gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
                RemoveGpuResourceBinding(key);
                gpu = null;
            }

            if (streamedGeometry && gpu is not null)
            {
                var previous = gpu.Textured!.LanePlan.Submission.Packet;
                if(packet.CurrentRetailParticleBuffer)
                {
                    if(EnbOpenGlStage0TextureBackend.CanStreamParticleGeometry(gpu.Textured,lane.Textured!))
                        gpu.UpdateGeometry(_texturedBackend.UpdateStreamedParticleGeometry(gl,gpu.Textured,lane.Textured!));
                    else
                    {
                        gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
                        RemoveGpuResourceBinding(key);
                        gpu=null;
                    }
                }
                else if(packet.CurrentRetailTerrainSea||packet.CurrentRetailTerrainDisplacement)
                {
                    if(!ReferenceEquals(previous.Mesh,packet.Mesh))
                        gpu.UpdateGeometry(_texturedBackend.UpdateStreamedTerrainGeometry(gl,gpu.Textured,lane.Textured!));
                }
                else if (ReferenceEquals(previous.Mesh, packet.Mesh) &&
                    ReferenceEquals(previous.Material.Source, packet.Material.Source))
                {
                    // The retained pose owns immutable deformed streams. An
                    // unchanged pose still submits current lighting/mapper/view
                    // inputs, but needs neither a new upload plan nor a VB write.
                    if(!ReferenceEquals(gpu.UploadedSkinGeometry,packet.DeformedGeometry))
                    {
                        gpu.UpdateGeometry(activeSkinLane is null
                            ?_texturedBackend.UpdateGeometry(gl,gpu.Textured,lane.Textured!)
                            :_texturedBackend.UpdateOwnedSkinGeometry(gl,gpu.Textured,activeSkinLane));
                        gpu.UploadedSkinGeometry=packet.DeformedGeometry;
                    }
                }
                else
                {
                    gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
                    RemoveGpuResourceBinding(key);
                    gpu = null;
                }
            }

            if (gpu is null)
            {
                long createStarted = Stopwatch.GetTimestamp();
                uint? mapperStartMilliseconds = mapperStartMillisecondsOverride;
                if (!TryCreateGpuResource(
                        gl,
                        lane,
                        laneKind,
                        mapperStartMilliseconds,
                        out LiveGpuResource created,
                        out string? gpuDiagnostic,
                        _gpuRecoveryState.GetValueOrDefault(key)))
                {
                    if (!transient)
                        _lanePlans.Remove(key);
                    missingOrUnsupported++;
                    gpuCreateRejected++;
                    firstPipelineDiagnostic ??= gpuDiagnostic;
                    if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? planetTracker))
                    {
                        planetTracker.RejectedPackets++;
                        planetTracker.Diagnostic ??= gpuDiagnostic ?? "planet GPU resource creation rejected";
                    }
                    continue;
                }
                _gpuRecoveryState.Remove(key);
                gpu = created;
                _frameGpuCreates++;
                if (!transient && _frameGpuCreateEvidence.Count < 4)
                    _frameGpuCreateEvidence.Add($"0x{packet.GameId:X8}/{packet.Mesh.Name}/pass{packet.Material.Source.Ordinal} {gpu.EstimatedUploadBytes/1048576.0:0.00}MiB {Stopwatch.GetElapsedTime(createStarted).TotalMilliseconds:0.00}ms");
            }

            if (!transient || streamedGeometry)
            {
                if (!ReferenceEquals(cachedGpu, gpu))
                {
                    _gpuResources[key] = gpu;
                    RegisterGpuResourceLifetime(key,gpu,!transient);
                }
                gpu.LastVisibleFrame = residencyFrame;
                if(_retainedOrdinaryMeshes && preparedBatch>=0 && !transient && packet.Mesh.CurrentRetailUsesMeshRenderer && preparedLane is not null &&
                    (preparedBinding is null || !ReferenceEquals(preparedBinding.Resource,gpu) || !ReferenceEquals(preparedBinding.Lane,preparedLane)))
                {
                    if(preparedBindings is null || !ReferenceEquals(preparedBindings.Context,_preparedGpuContext) || preparedBindings.Slots.Length!=meshDraw.PreparedBatchCount)
                    {
                        preparedBindings=new(_preparedGpuContext,meshDraw.PreparedBatchCount);
                        if(_ownedSceneFrame&&_ownedScene!.TryFind(packet.RenderIdentity,out var sourceNode)&&sourceNode.Definition.VisualGeneration==packet.VisualGeneration)
                            preparedBindings.MapperOwner=sourceNode.State.MapperContext;
                        packet.SetPreparedConsumerState(preparedBindings);
                    }
                    preparedBindings.Slots[preparedBatch]=new PreparedGpuBinding(_preparedGpuContext,key,gpu,preparedLane);
                    gpu.TrackPreparedBinding(preparedBindings,preparedBatch);
                }
            }
            else
            {
                transientPackets++;
            }

            if (gpu.Textured is not null && _terrainLitColor is not null)
                _texturedBackend.UpdateRuntimeTexture(gpu.Textured, _terrainTextureName, _terrainLitColor);

            double laneElapsed = Stopwatch.GetElapsedTime(laneStarted).TotalMilliseconds;
            laneMilliseconds += laneElapsed;
            if(_texturedBackend.MeasureSubmissionStages)
            {
                if(packet.CurrentRetailParticleBuffer) particleLaneMs+=laneElapsed;
                else if(packet.Mesh.BoneIndices.Count!=0) { skinLaneMs+=laneElapsed; skinPrepareMs+=skinPrepared; skinResourceMs+=laneElapsed-skinPrepared; }
                else if(packet.CurrentRetailTerrainSea||packet.CurrentRetailTerrainDisplacement) terrainLaneMs+=laneElapsed;
                else if(!transient) rigidLaneMs+=laneElapsed;
                else otherLaneMs+=laneElapsed;
            }
            long submitStarted = Stopwatch.GetTimestamp();
            EnbOpenGlSubmissionReceipt receipt = SubmitGpuResource(
                gl,
                fb,
                pixelWidth,
                pixelHeight,
                gpu,
                lane,
                (isPlanetSky && frame.Movie is not null || isOrbital) ? new EnbW3dSceneFogState(false, 0, 0, 0) :
                isPlanetSky && frame.Snapshot.Environment is { } skyEnvironment
                    ? new EnbW3dSceneFogState(skyEnvironment.FogBackdrop, sceneFog.SceneColorRgb24,
                        skyEnvironment.BackdropFogNear, skyEnvironment.BackdropFogFar)
                    : sceneFog,
                fogPipeline,
                packet.CurrentRetailPltDayNightPhase,
                mapperNowMilliseconds: frameMapperTime);
            if (receipt.GlErrorCode != EnbGlConstants.GL_NO_ERROR)
                throw new InvalidOperationException($"OpenGL submission ended with GL error 0x{receipt.GlErrorCode:X4}.");
            double submitElapsed=Stopwatch.GetElapsedTime(submitStarted).TotalMilliseconds;
            submitMilliseconds += submitElapsed;
            if(submitElapsed>slowestSubmit) {slowestSubmit=submitElapsed;slowestSubmitName=packet.RenderObjectName;}

            submittedPackets++;
            if (SubmittedMeshObserved is not null || SubmittedRangesObserved is not null)
            {
                // Readback/recording is outside the native material sequence.
                // Forget physical GL bindings without advancing shared mappers.
                _texturedBackend.InterruptRetainedSubmission(materialChanged:false);
                SubmittedMeshObserved?.Invoke(packet);
                SubmittedRangesObserved?.Invoke(lane.Submission);
                _retainedOrbitalDepthRange=null;
            }
            drawCalls += receipt.DrawCallCount;
            primitives += receipt.PrimitiveCount;
            vertices += receipt.VertexCount;
            renderedObjects.Add(packet.RenderIdentity);
            if (objectPresentationAudits.TryGetValue(
                    packet.RenderIdentity,
                    out LiveObjectPresentationAudit? objectPresentationAudit))
            {
                long pickStarted = Stopwatch.GetTimestamp();
                objectPresentationAudit.IncludeSubmittedPacket(packet, lane, frame.Camera, pixelWidth, pixelHeight);
                pickMilliseconds += Stopwatch.GetElapsedTime(pickStarted).TotalMilliseconds;
            }
            if (planetGpuTrackers.TryGetValue(packet.RenderIdentity, out LivePlanetGpuFrameTracker? submittedPlanetTracker))
                submittedPlanetTracker.SubmittedPackets++;

            if (transient && !streamedGeometry)
                gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
            }
            finally { activeSkinLane?.ReleaseFrame(); }
        }
        if(!specialFlushed)FlushSpecialLayer();
        }
        }

        DrawMovieStars();
        if (spaceComposition || orbitalCamera.HasValue || planetSkyCamera.HasValue)
            ApplyOrbitalDepthRange(gl, false, 1);
        // Aggregate existing wall timers once per frame. Backend stages are
        // inclusive inside submit; do not add them to these parent buckets.
        // No per-draw allocation, thread-time query or additional GL call.
        if(_texturedBackend.MeasureSubmissionStages && _useMainViewEnvironment && !_embeddedScanner && FullClientBenchmarkMeter.Active is not null)
        {
            // Denominators cover this exact segment and the same main-view scope
            // as the timers. A prepared batch is not a unique mesh instance.
            FullClientBenchmarkMeter.RecordObservedCount("submission.prepared-batches",preparedSubmissionBatches);
            FullClientBenchmarkMeter.RecordObservedCount("submission.input-packets",order.Packets.Length);
            FullClientBenchmarkMeter.RecordObservedCount("submission.draws",drawCalls);
            FullClientBenchmarkMeter.RecordObservedCount("submission.triangles",primitives);
            FullClientBenchmarkMeter.RecordObservedCount("submission.rendered-owners",renderedObjects.Count);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.mesh-categories",meshBatchMilliseconds);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lanes-resources",laneMilliseconds);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lane-kind.rigid",rigidLaneMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lane-kind.skin",skinLaneMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lane-kind.particle",particleLaneMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lane-kind.terrain",terrainLaneMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.lane-kind.other",otherLaneMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.skin-detail.prepare",skinPrepareMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.skin-detail.resources",skinResourceMs);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.draw",submitMilliseconds);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.pick-bounds",pickMilliseconds);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.special",specialMilliseconds);
            var stageTicks=_texturedBackend.SubmissionStageTicks;
            for(int i=0;i<stageTicks.Length;i++)
                FullClientBenchmarkMeter.RecordObservedWallTicks(SubmissionStageNames[i],stageTicks[i]);
        }
        if(DeveloperRenderDiagnostics.Available && DeveloperRenderDiagnostics.Open && _useMainViewEnvironment && !_embeddedScanner)
        {
            FullClientBenchmarkMeter.RecordObservedCount("draws",drawCalls);
            FullClientBenchmarkMeter.RecordObservedCount("triangles",primitives);
            FullClientBenchmarkMeter.RecordObservedCount("rendered-owners",renderedObjects.Count);
            FullClientBenchmarkMeter.RecordObservedCount("admitted-dazzles",_frameDazzles.Count);
            FullClientBenchmarkMeter.RecordObservedCount("ordinary-particle-evaluations",particleEmitterLiveParticles);
        }
        long dazzleStarted = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.dazzles");
        RenderDazzles(gl, fb, pixelWidth, pixelHeight, retailAssets,
            frame.Movie is not null && meshLayers.Any(layer => layer.Key == 1) ? false : null, detailedDiagnosticsDue);
        double dazzleMilliseconds = Stopwatch.GetElapsedTime(dazzleStarted).TotalMilliseconds;
        worldStages?.Next("world-phase.resource-retirement");
        if(_texturedBackend.MeasureSubmissionStages && _useMainViewEnvironment && !_embeddedScanner && FullClientBenchmarkMeter.Active is not null)
        {
            FullClientBenchmarkMeter.RecordObservedWallTicks("dazzle.admission",_dazzleVisibilityProfile.AdmissionTicks);
            FullClientBenchmarkMeter.RecordObservedWallTicks("dazzle.sphere-within-admission",_dazzleVisibilityProfile.SphereTicks);
            FullClientBenchmarkMeter.RecordObservedWallTicks("dazzle.cast",_dazzleVisibilityProfile.CastTicks);
            FullClientBenchmarkMeter.RecordObservedWallTicks("dazzle.mesh-within-cast",_dazzleVisibilityProfile.MeshTicks);
            FullClientBenchmarkMeter.RecordObservedWallTicks("dazzle.draw",_dazzleDrawTicks);
        }
        FinishParticleScene(frame.CurrentClientTime);
        _frameRetainedGpuOwners.UnionWith(liveRslOwners.FrameExtensions);
        if(planetSkyCamera.HasValue)_frameRetainedGpuOwners.Add((PlanetSkyIdentity,(ulong)_assetGeneration));
        foreach(var instance in roots)
            if(instance.OrbitalCamera.HasValue)_frameRetainedGpuOwners.Add((instance.Object.RenderIdentity,instance.Object.VisualGeneration));
        RetireUnusedGpuResources(gl, residencyFrame, frame.Snapshot,_frameRetainedGpuOwners);
        LivePlanetGpuPresentation planetGpuPresentation = BuildPlanetGpuPresentation(
            frame,
            planetGpuTrackers,
            ownedPlanetMapperStates,
            _planetMapperStateOwnership);
        Volatile.Write(ref _planetGpuPresentation, planetGpuPresentation);
        var completedQueries=BuildProjectedObjectBoundsFrame(
                frame,
                objectPresentationAudits.Values,
                currentRetailDistanceRadii,
                ResolveRetainedScannerFallbackSpheres(roots,frame.CurrentClientTime));
        completedQueries.Retain();
        var previousQueries=_projectedObjectBounds;
        Volatile.Write(ref _projectedObjectBounds,completedQueries);
        previousQueries.Release();
        long profileGpuEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.bridge-audio");
        // Bridge HLOD cues are emitted while submitting its final pose. Keep
        // that draw after the world, but before the single audio-scene update
        // so released cues enter the same frame without retiring other voices.
        _bridgeAnimationSounds.Clear();
        if(includeBridge && (drawCalls>0 || frame.Movie is not null))
            RenderBridge(gl,fb,pixelWidth,pixelHeight,frame);
        if (_audio is not null)
        {
            _audio.DialogGain = playerOptions.Options.SoundDialog / 100f;
            if (frame.Movie is { } movieAudio)
                _audio.UpdateMovieAudio(movieAudio, retailAssets, playerOptions.Options.SoundSfx / 100f);
            _audio.UpdateShipEmotesForObjects(frame.Snapshot.IsTransitioning?[]:SceneObjects(frame.Snapshot), retailAssets, frame.Camera,
                playerOptions.Options.SoundSfx / 100f, frame.CurrentClientTime / 1000.0);
            _audio.Player = MusicPlayer;
            _audio.UpdateMusic(frame.Snapshot.IsTransitioning ? null : frame.Snapshot.Music,
                retailAssets, frame.CurrentClientTime / 1000.0, playerOptions.Options.SoundMusic / 100f);
            _audio.UpdateEngines(frame.Snapshot.IsTransitioning ? [] : SceneObjects(frame.Snapshot),
                retailAssets, frame.Camera, playerOptions.Options.SoundSfx / 100f,
                // Position samples share this clock; compositor-only redraws must not change velocity.
                frame.Snapshot.ClientTime, playerOptions.Options.DopplerEnabled);
            _audio.EffectObjectIdentity = id => SceneObjects(frame.Snapshot).FirstOrDefault(o=>o.GameId==id)?.RenderIdentity;
            _audio.EffectBonePosition = (id,bone) => SceneObjects(frame.Snapshot).FirstOrDefault(o=>o.GameId==id) is { Spatial: not null } source
                ? ResolveCombatSourcePose(source,bone,roots,retailAssets).Translation : null;
            _audio.EffectRenderPosition = node => node.Definition.EffectClassKind==EnbEffectClassKind.AttachEffect
                ? node.EffectOwner?.Attachment?.SoundPosition
                : node.Definition.EffectClassKind==EnbEffectClassKind.LineEffect
                ? _lineBindings.SoundPosition(node,frame.Camera.Position)
                : node.Definition.EffectClassKind is EnbEffectClassKind.RObjLookAtEffect or EnbEffectClassKind.ParticleJetEffect
                ? ResolveBinaryEffectSoundPosition(node,frame.Camera.Position)
                : _combatEffectPoses.TryGetValue(
                (node.Root.EffectId,node.Root.InstanceGeneration,node.Sequence),out var pose) ? pose.Translation : null;
            _audio.UpdateEffects((effectRslSchedulerUpdate?.SoundNodes ?? []).Concat(_shipAnimationSounds).Concat(_bloodSounds).Concat(_bridgeAnimationSounds).ToArray(),retailAssets,frame.Camera,
                id => SceneObjects(frame.Snapshot).FirstOrDefault(o=>o.GameId==id)?.Spatial?.Transform.Position,
                playerOptions.Options.SoundSfx/100f,contextEnded:effectRslSchedulerUpdate is null,
                warpObjects:frame.Snapshot.IsTransitioning ? [] : SceneObjects(frame.Snapshot),
                sampleTime:unchecked((uint)frame.CurrentClientTime),dopplerEnabled:playerOptions.Options.DopplerEnabled);
            foreach (LiveRslSoundCompletionEvent completion in _audio.Update(
                         frame.Camera,
                         soundRequests,
                         retailAssets.RetailMixResources,
                         frame.CurrentClientTime,
                         playerOptions.Options.SoundSfx / 100f))
            {
                _rslSoundCompletions[completion.Key]=completion;
                _soundCompletionLifetime.Register(completion.Key,AuxiliarySource((completion.Key.RenderIdentity,completion.Key.VisualGeneration)));
            }
        }
        var residentOwners = _residentGpuOwners;
        if (frame.Movie is not null)
            MovieRenderStatistics = (primitives + _backdropRenderer.SubmittedWorldVertices / 3,
                vertices + _backdropRenderer.SubmittedWorldVertices);
        _meshScratch.Clear();
        _indexedSubmissions.EndFrame(residentOwners);
        CameraMeshBatches.EndFrame();
        if (_sharedMaterialOwner is null)
        {
            // Material mutations belong to live RenderObjs, including culled
            // targets and inactive LODs. GPU eviction/reset is not retirement.
            var materialOwners = _frameMaterialOwners;
            foreach(var root in roots)
            {
                // Ordinary membership was registered by the source change feed.
                // Extend it only for generated/auxiliary owner generations.
                var source=root.PreparedOwner?.SceneSource;
                if(_ownedSceneFrame&&source is not null&&source.Definition.RenderIdentity==root.Object.RenderIdentity&&
                    source.Definition.VisualGeneration==root.Object.VisualGeneration)continue;
                materialOwners.Add((root.Object.RenderIdentity,root.Object.VisualGeneration));
            }
            _shipMaterials.EndFrame(materialOwners);
        }
        EnbWorkReuseDiagnostics.Add("render.draws", drawCalls);
        EnbWorkReuseDiagnostics.Add("render.roots", roots.Count);
        EnbWorkReuseDiagnostics.Add("render.packets", order.PacketCount);
        EnbWorkReuseDiagnostics.Add("render.triangles", primitives);
        bool publishDiagnosticReceipt = detailedDiagnosticsDue || drawCalls == 0 || firstDrawDiagnostic is not null;
        long profileAudioEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.diagnostics");
        string metrics = "", r6z1Diagnostic = "", r6z3Diagnostic = "", r6z31Diagnostic = "", r6z34Diagnostic = "", r7Diagnostic = "", r7aDiagnostic = "", r8Diagnostic = "", r9Diagnostic = "", r9atDiagnostic = "";
        if (publishDiagnosticReceipt)
        {
            string audioStatus = (_audio?.StatusText ?? "OpenAL surface unavailable")+" · "+_effectVisualStatus+" · "+_effectObjectVisualStatus+" · "+_effectRslStatus+(_rslFactoryDiagnostic is null?"":" · "+_rslFactoryDiagnostic)+" · "+_shipExhaustStatus+
                $" · ship folding parts {_shipAnimatedParts} animation cues {_shipAnimationSounds.Count}";
            int resolvedExternalHierarchies = _externalHierarchies.Values.Count(static value => value.Hierarchy is not null);
            int failedExternalHierarchies = _externalHierarchies.Count - resolvedExternalHierarchies;
            string ambientReceipt = sceneEnvironmentResolved
                ? $"ambient=0x{effectiveAmbientRgb24:X6} exact" +
                  (string.IsNullOrWhiteSpace(fogDiagnostic) ? string.Empty : $" ({fogDiagnostic})")
                : $"ambient=0x{effectiveAmbientRgb24:X6} fallback ({fogDiagnostic ?? "unknown environment failure"})";
            int enabledLights = lightEnvironment.Lights.Count(static slot => slot.Enabled);
            string mainViewLightReceipt = mainViewLighting is null
                ? "R9AF MainView rig unresolved"
                : $"R9AF_CURRENT_RETAIL_MAINVIEW_LIGHT_RIG_ACTIVE orbital {mainViewLighting.EnabledDirectionalLightCount} " +
                  "R9AI_CAMERA_PLUS_Z_LIGHT_BASES_ACTIVE " +
                  $"dir K{DescribeVector3(mainViewLighting.DirectionalLights[0].Light.Direction)} " +
                  $"RK{DescribeVector3(mainViewLighting.DirectionalLights[1].Light.Direction)} " +
                  $"C{DescribeVector3(mainViewLighting.DirectionalLights[2].Light.Direction)} " +
                  $"RC{DescribeVector3(mainViewLighting.DirectionalLights[3].Light.Direction)}";
            string gammaSource = playerOptions.Options.GraphicsDefault
                ? "Graphics_Default=yes"
                : $"Graphics_Default=no Graphics_Gamma={playerOptions.Options.GraphicsGamma?.ToString("0.###", CultureInfo.InvariantCulture) ?? "retail-source-default"}";
            metrics =
                $"objects {renderedObjects.Count} · packets {submittedPackets}/{order.PacketCount} · draws {drawCalls} · tris {primitives} · " +
                $"{_orbitalDrawStatus} · {_terrainStatus} · {_planetSkyStatus} · {_dazzleStatus} visible {orbitalOwners.Count(renderedObjects.Contains)} · " +
                $"HLOD {hlodStats.Selected}/{hlodStats.Total} cull {hlodStats.Culled} fail {hlodStats.Failed} " +
                $"budget {hlodStats.FinalCost:0.###}/{hlodStats.Budget:0.###} fixed {hlodStats.FixedCost:0.###} candidates {hlodStats.CandidateCount} · RSL {rslRoots} · " +
                $"xhier {resolvedExternalHierarchies}/{failedExternalHierarchies} · transient {transientPackets} · skipped {missingOrUnsupported} · radar-range detached {radarRangeDetached} · " +
                $"particle emitters {particleEmitterInstances} live {particleEmitterLiveParticles} reject {particleEmitterRejected} · " +
                $"reject draw {drawPacketCompileRejected} submit {submissionRejected} lane {laneUnsupported}/{laneCompileRejected} gpu {gpuCreateRejected} · " +
                $"GPU cache {_gpuResources.Count} · lane cache {lanePlanCacheHits}/{lanePlanCacheMisses} hit/miss · " +
                $"index cache {_indexedSubmissions.HitCount}/{_indexedSubmissions.MissCount} hit/miss · " +
                $"HAnim registry {_hAnimRegistry.Count} hit/miss {_hAnimRegistry.HitCount}/{_hAnimRegistry.MissCount} · " +
                $"lights {enabledLights}/{EnbW3dLightEnvironment.CurrentRetailLightSlotCount} " +
                $"server {frame.Snapshot.Lighting.Lights.Length} · {mainViewLightReceipt} " +
                $"ENBNEXT_DISPLAY_GAMMA_UNAPPLIED requested-gamma={detailSettings.GammaFactor:0.###} {gammaSource} " +
                $"{(playerOptions.UsesPersistedFile ? $"player{playerOptions.Selector?.ToString() ?? "?"}" : "no persisted file")} · " +
                $"{ambientReceipt} · " +
                DescribeFogReceipt(frame.Snapshot.Environment, fogResolved, fogEvaluation, fogPipeline, fogDiagnostic);
            r6z1Diagnostic = DescribeR6Z1RootAndHlodCensus(
                fileDefinitionObjects,
                fileResolvedResources,
                fileRootedObjects,
                rslDefinitionObjects,
                rslResolvedReferences,
                rslRootedObjects,
                rslRoots,
                roots.Count,
                rootRejections,
                hlodRejections);
            r6z3Diagnostic = DescribeR6Z3PrototypeAndLaneCensus(_hlodPrototypeEvidence.Values, laneRejections);
            r6z31Diagnostic = DescribeR6Z31AnimationReceipts(animationReceipts, retailAssets.RetailMixResources, audioStatus);
            r6z34Diagnostic = DescribeR6Z34EffectRslReceipt(
                effectRslSchedulerUpdate,
                rslRuntimeUpdates.Count,
                _rslInvocationCounts.Applied,
                _rslInvocationCounts.Ignored,
                unmatchedEffectRslEvents,
                _rslInvocationCounts.Failures);
            r7Diagnostic = DescribeR7StaticSectorObjects(
                SceneObjects(frame.Snapshot),
                _rslInvocationCounts.Selections,
                _rslInvocationCounts.Rejected);
            r7aDiagnostic = DescribeR7AInfluenceNebula(
                influenceObjects,
                influenceBoundarySources.Count,
                influenceOutside,
                influenceBetween,
                influenceRoom,
                influenceBoundaryRejected + influenceEvaluationRejected,
                influenceEnvironmentApplied,
                appliedInfluenceEnvironment,
                sceneFog,
                firstInfluenceDiagnostic);
            r8Diagnostic = planetGpuPresentation.Status;
            r9Diagnostic = DescribeR9ShipLego(shipLegoTracker);
            r9atDiagnostic = DescribeR9AtObjectPresentationAudit(
                frame.Camera,
                pixelWidth,
                pixelHeight,
                objectPresentationAudits.Values);
        }
        long profileDiagnosticEnd = Stopwatch.GetTimestamp();
        worldStages?.Next("world-phase.tail");
        string renderProfile =
            $"render profile: R6_CLOSEOUT_FRAME_REUSE_ACTIVE · setup {ProfileMilliseconds(profileStart, profileSetupEnd):0.00}ms · " +
            $"RSL {ProfileMilliseconds(profileSetupEnd, profileRslEnd):0.00}ms · " +
            $"roots {ProfileMilliseconds(profileRslEnd, profileRootsEnd):0.00}ms · " +
            $"(objects {ProfileMilliseconds(profileRslEnd, profileObjectRootsEnd):0.00}ms, bounds {ProfileMilliseconds(profileBoundsStart, profileRootsEnd):0.00}ms [owners {ProfileMilliseconds(profileBoundsStart,profileHlodStart):0.00}, HLOD {ProfileMilliseconds(profileHlodStart,profileHlodEnd):0.00}, effects {ProfileMilliseconds(profileHlodEnd,profileRootsEnd):0.00}; prepare {ProfileMilliseconds(profileHlodEnd,profileEffectObjectsEnd):0.00}, callbacks {ProfileMilliseconds(profileEffectObjectsEnd,profileParticleCallbacksEnd):0.00}, RSL refresh {profileRslRefreshCount}/{profileRslRefreshTicks*1000d/Stopwatch.Frequency:0.00}]) · " +
            $"packets {ProfileMilliseconds(profileRootsEnd, profilePacketsEnd):0.00}ms " +
            $"(meshes {ProfileMilliseconds(profileRootsEnd, profileMeshPacketsEnd):0.00}, shadows {ProfileMilliseconds(profileMeshPacketsEnd, profileShadowsEnd):0.00}, terrain {ProfileMilliseconds(profileShadowsEnd, profileTerrainEnd):0.00}, effects {ProfileMilliseconds(profileTerrainEnd, profilePacketsEnd):0.00}) · ship materials {_shipMaterials.Hits}/{_shipMaterials.Misses} hit/miss · " +
            $"GPU {ProfileMilliseconds(profilePacketsEnd, profileGpuEnd):0.00}ms (CPU wall, not GPU elapsed; mesh batches {meshBatchMilliseconds:0.00}ms, lanes/resources {laneMilliseconds:0.00}ms, submit {submitMilliseconds:0.00}ms, pick bounds {pickMilliseconds:0.00}ms, dazzle {dazzleMilliseconds:0.00}ms, special {specialMilliseconds:0.00}ms, slowest {slowestSubmitName}/{slowestSubmit:0.00}ms) · dazzle {_dazzleVisibilityProfile}; draw CPU {_dazzleDrawTicks*1000d/Stopwatch.Frequency:0.00}ms · " +
            $"audio {ProfileMilliseconds(profileGpuEnd, profileAudioEnd):0.00}ms · " +
            $"diag {ProfileMilliseconds(profileAudioEnd, profileDiagnosticEnd):0.00}ms · lane miss [{string.Join("; ", laneMissEvidence)}] · " +
            $"GPU created [{string.Join("; ", _frameGpuCreateEvidence)}] · idle-budget evicted {_frameGpuEvictions}/{_frameGpuEvictedBytes/1048576.0:0.00}MiB";
        renderProfile += _texturedBackend.SubmissionTiming;
        if (_retainedSubmissionControl is not null)
            renderProfile += $" retainedMode={(retainedSubmissionExperiment ? 1 : 0)}" + _texturedBackend.RetainedSubmissionTiming;
        if (_frozenInputControl is not null) renderProfile += $" inputMode={_frozenInputMode} inputPhase={_frozenInputToken}";
        if (_diagnosticExperimentControl is not null) renderProfile += $" diagnosticMode={(_sampleDiagnosticReceipts ? 1 : 0)}";
        if (_sweepExperimentControl is not null) renderProfile += $" sweepMode={(_indexedSubmissions.IndexedSweepEnabled ? 1 : 0)} sweepRetained={_indexedSubmissions.RetainedCount} sweepCandidates={_indexedSubmissions.LastSweepCandidateCount}";
        _lastFrameProfile = renderProfile;
        _framePerformanceScene="Space";
        _framePerformanceStages=[ProfileMilliseconds(profileStart,profileSetupEnd),
            ProfileMilliseconds(profileSetupEnd,profileRslEnd),ProfileMilliseconds(profileRslEnd,profileRootsEnd),
            ProfileMilliseconds(profileRootsEnd,profilePacketsEnd),ProfileMilliseconds(profilePacketsEnd,profileGpuEnd),
            ProfileMilliseconds(profileGpuEnd,profileAudioEnd),ProfileMilliseconds(profileAudioEnd,profileDiagnosticEnd)];
        if (!publishDiagnosticReceipt) return drawCalls > 0;
        if (drawCalls > 0)
        {
            // A partially rendered frame remains usable, but its first rejected
            // draw must reach the existing issue indicator and offline receipts.
            string partialDrawDiagnostic = firstDrawDiagnostic is null ? "" : $"\nrender draw rejected: {firstDrawDiagnostic}";
            string receipt = sceneEnvironmentResolved
                ? "R6T_LIVE_WORLD_AMBIENT_ACTIVE"
                : fogResolved
                    ? "R6S_LIVE_WORLD_FOG_ACTIVE"
                    : "R6R_LIVE_WORLD_LIGHTING_ACTIVE";
            Volatile.Write(ref _status, new LiveWorldW3dStatus($"{receipt} · {metrics}{partialDrawDiagnostic}\n{renderProfile}\n{r6z1Diagnostic}\n{r6z3Diagnostic}\n{r6z31Diagnostic}\n{r6z34Diagnostic}\n{r7Diagnostic}\n{r7aDiagnostic}\n{r8Diagnostic}\n{r9Diagnostic}\n{r9atDiagnostic}"));
            return true;
        }

        string firstDiagnostic = hlodStats.FirstFailure ?? firstDrawDiagnostic ?? firstPipelineDiagnostic ??
            _externalHierarchies.Values.Select(static value => value.Diagnostic).FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ??
            "no pipeline rejection diagnostic";
        Volatile.Write(ref _status, new LiveWorldW3dStatus(
            $"R6Q live W3D: no submitted ordinary W3D draws · {metrics} · first: {firstDiagnostic}\n{renderProfile}\n{r6z1Diagnostic}\n{r6z31Diagnostic}\n{r6z34Diagnostic}\n{r7Diagnostic}\n{r7aDiagnostic}\n{r8Diagnostic}\n{r9Diagnostic}\n{r9atDiagnostic}"));
        return false;
    }

    private static string DescribeR9AtObjectPresentationAudit(
        EnbResolvedRenderCamera camera,
        int pixelWidth,
        int pixelHeight,
        IEnumerable<LiveObjectPresentationAudit> audits)
    {
        float horizontalPixelsPerProjectionUnit = pixelWidth * 0.5f * camera.Projection.Matrix.M11;
        float verticalPixelsPerProjectionUnit = pixelHeight * 0.5f * camera.Projection.Matrix.M22;
        float pixelAxisRatio = verticalPixelsPerProjectionUnit > 0f
            ? horizontalPixelsPerProjectionUnit / verticalPixelsPerProjectionUnit
            : float.NaN;
        string viewportAgreement = camera.Viewport.PixelWidth == pixelWidth && camera.Viewport.PixelHeight == pixelHeight
            ? "exact"
            : $"MISMATCH camera={camera.Viewport.PixelWidth}x{camera.Viewport.PixelHeight}";

        LiveObjectPresentationAudit[] centeredFixedObjects = audits
            .Where(static audit =>
                !audit.Object.IsLocalShip &&
                audit.Object.Spatial?.ProviderKind == EnbSceneSpatialProviderKind.Fixed &&
                audit.HasProjectedBounds &&
                audit.IntersectsViewport)
            .OrderBy(static audit => audit.ProjectedCenterDistanceSquared)
            .ThenBy(static audit => audit.Object.GameId)
            .Take(6)
            .ToArray();
        string objects = centeredFixedObjects.Length == 0
            ? "none"
            : string.Join("; ", centeredFixedObjects.Select(audit => audit.Describe(camera)));

        return
            "R9AT_ORDINARY_W3D_PRESENTATION_AUDIT_ACTIVE · R9AV_CAMERA_OBJECT_ALIGNMENT_AUDIT_ACTIVE · " +
            $"GL {pixelWidth}x{pixelHeight} vs camera viewport {viewportAgreement} · " +
            $"projection aspect {camera.Projection.AspectRatio:0.######} M11/M22 {camera.Projection.Matrix.M11:0.######}/{camera.Projection.Matrix.M22:0.######} · " +
            $"pixel-axis X/Y {pixelAxisRatio:0.######} · " +
            $"basis F{FormatPreciseVector(camera.Basis.Forward)} R{FormatPreciseVector(camera.Basis.Right)} U{FormatPreciseVector(camera.Basis.Up)} · " +
            $"centered fixed [{objects}]";
    }

    private LiveCompletedRenderQueries BuildProjectedObjectBoundsFrame(
        LiveFrame frame,
        IEnumerable<LiveObjectPresentationAudit> audits,
        ImmutableDictionary<ulong, float> distanceRadii,
        IReadOnlyDictionary<ulong,EnbW3dWorldBounds> retainedScannerSpheres)
    {
        LiveCompletedRenderQueries? result=null;
        foreach(var candidate in _completedQueryStorage)if(!candidate.IsRetained){result=candidate;break;}
        if(result is null){result=new();_completedQueryStorage.Add(result);}
        result.Clear();
        var bounds=result.BoundsByRenderIdentity;
        var renderSpheres=result.RenderSpheresByRenderIdentity;
        var localCenters=result.LocalSphereCenters;
        foreach (LiveObjectPresentationAudit audit in audits)
        {
            var resolvedSphere=retainedScannerSpheres.TryGetValue(audit.Object.RenderIdentity,out var retainedSphere)
                ?retainedSphere:audit.RenderSphere;
            if (resolvedSphere is EnbW3dWorldBounds sphere && sphere.IsValid)
            {
                renderSpheres[audit.Object.RenderIdentity] = sphere;
                if (audit.Object.Spatial?.Transform is {} pose)
                    localCenters[audit.Object.RenderIdentity] = Vector3.Transform(sphere.Center - pose.Position,
                        Quaternion.Inverse(pose.Orientation));
            }
            if (audit.TryCreateProjectedBounds(frame.Camera.Viewport, out LiveRenderedObjectQuery projected))
            {
                if(bounds.TryGetValue(projected.RenderIdentity,out var replaced))replaced.BodyQueries?.Release();
                projected.BodyQueries?.Retain();
                bounds[projected.RenderIdentity] = projected;
            }
        }

        // Retained emitter buffers may have no submitted packets in this view.
        // Their scanner fit still belongs to the queried RenderObject.
        foreach(var (identity,sphere) in retainedScannerSpheres)
            renderSpheres[identity]=sphere;

        result.SnapshotVersion=frame.Snapshot.Version;
        result.DistanceRadiiByRenderIdentity=distanceRadii;
        return result;
    }

    private static ImmutableDictionary<ulong, float> BuildCurrentRetailDistanceRadii(
        IReadOnlyCollection<LiveRootInstance> roots, EnbClientBaseAssetMountCatalog? mounts)
    {
        var radii = ImmutableDictionary.CreateBuilder<ulong, float>();
        if (mounts is null) return radii.ToImmutable();
        foreach (IGrouping<ulong, LiveRootInstance> objectRoots in roots
                     .Where(static instance => HasCurrentRetailShipLegoComposition(instance.Object))
                     .GroupBy(static instance => instance.Object.RenderIdentity))
        {
            LiveRootInstance[] controllerRoots = objectRoots
                .Where(static instance =>
                    string.Equals(instance.InstancePath, "root", StringComparison.Ordinal) ||
                    instance.IsShipLegoController)
                .ToArray();
            if (controllerRoots.Length == 0) continue;
            var controllers=new Dictionary<string,LegoRadiusController>(StringComparer.Ordinal);
            foreach(var instance in controllerRoots)
                if(TryCaptureLegoRadiusController(instance,out var controller))
                    controllers.TryAdd(controller.Path,controller);
            if(controllers.Count==controllerRoots.Length && TryResolveLegoRadius(controllers,mounts,out float radius))
                radii[objectRoots.Key]=radius;
        }
        return radii.ToImmutable();
    }

    private ImmutableHashSet<LivePlanetGpuIdentity> UpdatePlanetMapperStateOwnership(
        LiveFrame frame,
        EnbRetailAssetBundle retailAssets,
        IReadOnlyList<LiveRootInstance> roots)
    {
        if(_ownedSceneFrame)return UpdateOwnedPlanetMappers(frame,retailAssets,roots);
        var liveIdentities = SceneObjects(frame.Snapshot)
            .Where(IsRenderablePlanet)
            .Select(static obj => new LivePlanetGpuIdentity(obj.RenderIdentity, obj.VisualGeneration))
            .ToHashSet();
        foreach (LivePlanetGpuIdentity identity in _planetMapperStateOwnership.Keys
                     .Where(identity => !liveIdentities.Contains(identity)).ToArray())
        {
            _planetMapperStateOwnership.Remove(identity);
        }

        var owned = ImmutableHashSet.CreateBuilder<LivePlanetGpuIdentity>();
        IEnumerable<IEnbRenderObject> materializedPlanets = roots
            .Where(static instance => IsRenderablePlanet(instance.Object))
            .GroupBy(static instance => new LivePlanetGpuIdentity(
                instance.Object.RenderIdentity,
                instance.Object.VisualGeneration))
            .Select(static group => group.First().Object);
        foreach (IEnbRenderObject planet in materializedPlanets)
        {
            var identity = new LivePlanetGpuIdentity(planet.RenderIdentity, planet.VisualGeneration);

            if (!_planetMapperStateOwnership.TryGetValue(
                    identity,
                    out LivePlanetMapperStateOwnership? ownership))
            {
                ownership = new LivePlanetMapperStateOwnership(
                    unchecked((uint)Environment.TickCount));
                _planetMapperStateOwnership.Add(identity, ownership);
            }

            UpdatePlanetMapperInputs(frame,retailAssets,planet,identity,ownership);
            if(ownership.RuntimeSemanticsResolved)owned.Add(identity);
        }

        return owned.ToImmutable();
    }

    private void UpdatePlanetMapperInputs(LiveFrame frame,EnbRetailAssetBundle retailAssets,
        IEnbRenderObject planet,LivePlanetGpuIdentity identity,LivePlanetMapperStateOwnership ownership,SceneOwner? source=null)
    {
            // The isolated scanner renderer receives the MainView-owned epoch.
            // If a corrected generation-qualified value arrives after a local
            // resource was prepared, replacing it changes LiveGpuKey and forces
            // that resource to be rebuilt from the retained owner state.
            uint inheritedMapperStartMilliseconds=source?.FrameMapperStartOverride??0;
            if (source is not null?source.FrameMapperStartOverride.HasValue:frame.MapperStartMillisecondsOverrides.TryGetValue(identity,out inheritedMapperStartMilliseconds))
            {
                ownership.MapperStartMilliseconds = inheritedMapperStartMilliseconds;
            }

            float inheritedPhase=source?.FrameDayNightOverride??0;
            if (source is not null?source.FrameDayNightOverride.HasValue:frame.DayNightPhaseOverrides.TryGetValue(identity,out inheritedPhase))
            {
                ownership.DayNightPhase = inheritedPhase;
                ownership.DominantOrbitalIndex = null;
                ownership.RuntimeSemanticsResolved = float.IsFinite(inheritedPhase);
                ownership.Diagnostic = ownership.RuntimeSemanticsResolved
                    ? null
                    : "inherited current-retail PLT_DN phase is non-finite";
                return;
            }

            int? presentationMode = frame.Snapshot.Environment?.PresentationMode;
            if (_useMainViewEnvironment && presentationMode == 0)
            {
                if (TryResolveCurrentRetailPlanetDayNightPhase(
                        frame,
                        retailAssets,
                        planet,
                        out float phase,
                        out int dominantOrbitalIndex,
                        out string? diagnostic))
                {
                    ownership.DayNightPhase = phase;
                    ownership.DominantOrbitalIndex = dominantOrbitalIndex;
                    ownership.RuntimeSemanticsResolved = true;
                    ownership.Diagnostic = null;
                }
                else
                {
                    ownership.DayNightPhase = null;
                    ownership.DominantOrbitalIndex = null;
                    ownership.RuntimeSemanticsResolved = false;
                    ownership.Diagnostic = diagnostic ?? "current-retail PLT_DN phase is unresolved";
                }
                return;
            }

            ownership.DayNightPhase = null;
            ownership.DominantOrbitalIndex = null;
            ownership.RuntimeSemanticsResolved = presentationMode is 1 or 5;
            ownership.Diagnostic = ownership.RuntimeSemanticsResolved
                ? null
                : presentationMode is null
                    ? "retail presentation mode has not materialized"
                    : $"presentation mode {presentationMode.Value} is outside the proven MainView planet modes 0/1/5";
    }

    private bool TryResolveCurrentRetailPlanetDayNightPhase(
        LiveFrame frame,
        EnbRetailAssetBundle retailAssets,
        IEnbRenderObject obj,
        out float phase,
        out int dominantOrbitalIndex,
        out string? diagnostic)
    {
        phase = 0f;
        dominantOrbitalIndex = -1;
        diagnostic = null;

        EnbSceneEnvironmentSnapshot? environment = frame.Snapshot.Environment;
        if (environment is null || !EnbCurrentRetailMainViewPassRuntime.UsesModeZeroLightRig(environment.PresentationMode))
        {
            diagnostic = environment?.PresentationMode is null
                ? "retail presentation mode has not materialized"
                : $"presentation mode {environment.PresentationMode.Value} does not enter ordinary MainView AdjustDayNight";
            return false;
        }
        if (obj.Spatial?.Transform is not IEnbRenderTransform spatial || !IsFinite(spatial.Position))
        {
            diagnostic = $"planet object 0x{obj.GameId:X8} has no finite spatial position";
            return false;
        }

        var dominant = _currentOrbitalEvaluation.DominantLight;
        if (!_currentOrbitalEvaluationResolved)
        {
            diagnostic = "Orbital manager state is unavailable for day/night.";
            return false;
        }
        //004F45A0 resets manager+20..28 to +Z.0050F1B0/0075F690
        //also consumes that direction when no emitter was selected.
        Vector3 dominantDirection = dominant?.Direction ?? Vector3.UnitZ;
        dominantOrbitalIndex = _currentOrbitalEvaluation.DominantOrbitalIndex;

        if (!EnbRetailOrbitalFogEvaluator.TryComputeOrdinarySceneDayNightPhase(
                spatial.Position,
                frame.Camera.Position,
                dominantDirection,
                out phase))
        {
            diagnostic = $"planet object 0x{obj.GameId:X8} did not produce a finite current-retail PLT_DN phase";
            return false;
        }

        return true;
    }

    private static bool PacketContainsMutablePltDayNightBinding(EnbW3dDrawTask packet)
    {
        foreach (EnbW3dRenderTriangleBinding binding in packet.Material.Source.TriangleBindings)
        {
            if (binding.Semantics is not EnbW3dCompiledMaterialSemantics semantics)
                continue;

            for (int stage = 0; stage < binding.Textures.Count && stage < 2; stage++)
            {
                EnbW3dRenderTextureBinding? texture = binding.Textures[stage];
                if (texture is null || texture.IsUnbound ||
                    string.IsNullOrWhiteSpace(texture.LogicalName) ||
                    !texture.LogicalName.StartsWith("plt_dn", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                EnbW3dMapperRuntimeProgram program = stage == 0
                    ? semantics.Mapper0Program
                    : semantics.Mapper1Program;
                if (program.Kind == EnbW3dMapperRuntimeProgramKind.Linear)
                    return true;
            }
        }

        return false;
    }

    private static LivePlanetGpuPresentation BuildPlanetGpuPresentation(
        LiveFrame frame,
        IReadOnlyDictionary<ulong, LivePlanetGpuFrameTracker> trackers,
        ImmutableHashSet<LivePlanetGpuIdentity> ownedPlanetMapperStates,
        IReadOnlyDictionary<LivePlanetGpuIdentity, LivePlanetMapperStateOwnership> mapperStateOwnership)
    {
        var dayNightPhases = ImmutableDictionary.CreateBuilder<LivePlanetGpuIdentity, float>();
        var mapperStarts = ImmutableDictionary.CreateBuilder<LivePlanetGpuIdentity, uint>();
        foreach (LivePlanetGpuIdentity identity in ownedPlanetMapperStates)
        {
            if (!mapperStateOwnership.TryGetValue(identity, out var ownership)) continue;
            mapperStarts[identity] = ownership.MapperStartMilliseconds;
            if (ownership.DayNightPhase is float phase && float.IsFinite(phase))
                dayNightPhases[identity] = phase;
        }

        var presented = ImmutableHashSet.CreateBuilder<LivePlanetGpuIdentity>();
        int expectedPackets = 0, culledPackets = 0;
        int submittedPackets = 0;
        int rejectedPackets = 0;
        int dayNightPackets = 0;
        int normalGeometryPackets = 0;
        int alignedGeometryPackets = 0;
        int orientedGeometryPackets = 0;
        int skinGeometryPackets = 0;
        int unsupportedGeometryPackets = 0;
        LivePlanetGpuFrameTracker? firstReady = null;
        LivePlanetGpuFrameTracker? firstUnresolved = null;

        foreach (LivePlanetGpuFrameTracker tracker in trackers.Values.OrderBy(static tracker => tracker.Object.GameId))
        {
            expectedPackets += tracker.ExpectedPackets; culledPackets += tracker.CulledPackets;
            submittedPackets += tracker.SubmittedPackets;
            rejectedPackets += tracker.RejectedPackets;
            dayNightPackets += tracker.DayNightPackets;
            normalGeometryPackets += tracker.NormalGeometryPackets;
            alignedGeometryPackets += tracker.CameraAlignedGeometryPackets;
            orientedGeometryPackets += tracker.CameraOrientedGeometryPackets;
            skinGeometryPackets += tracker.SkinGeometryPackets;
            unsupportedGeometryPackets += tracker.UnsupportedGeometryPackets;

            bool ready = tracker.RuntimeSemanticsResolved &&
                         tracker.ExpectedPackets > 0 &&
                         tracker.UnsupportedGeometryPackets == 0 &&
                         tracker.RejectedPackets == 0 &&
                         tracker.SubmittedPackets + tracker.CulledPackets == tracker.ExpectedPackets;
            if (ready)
            {
                presented.Add(new LivePlanetGpuIdentity(
                    tracker.Object.RenderIdentity,
                    tracker.Object.VisualGeneration));
                firstReady ??= tracker;
            }
            else
            {
                firstUnresolved ??= tracker;
            }
        }

        if (trackers.Count == 0)
        {
            string ownershipStatus = ownedPlanetMapperStates.Count == 0
                ? "no materialized planet root in this frame"
                : $"mapper owners {ownedPlanetMapperStates.Count} · current draw 0 (outside MainView frustum)";
            return new LivePlanetGpuPresentation(
                frame.Snapshot.Version,
                ownedPlanetMapperStates,
                ImmutableHashSet<LivePlanetGpuIdentity>.Empty,
                dayNightPhases.ToImmutable(),
                mapperStarts.ToImmutable(),
                "render planet: R8A_CURRENT_RETAIL_PLANET_CAMERA_GEOMETRY_READY · objects 0 · " + ownershipStatus);
        }

        if (firstReady is { } sample)
        {
            string phase = sample.DayNightPhase is float dayNightPhase
                ? $"phase={dayNightPhase:0.000000} · dominant orbital {sample.DominantOrbitalIndex?.ToString() ?? "?"} · PLT_DN frozen"
                : "authored mapper state (no mode-0 PLT adjustment required)";
            string hlod = string.IsNullOrWhiteSpace(sample.HlodSummary) ? "HLOD <none>" : $"HLOD {sample.HlodSummary}";
            string geometrySample = sample.CameraGeometrySamples.Count == 0
                ? string.Empty
                : $" · camera samples [{string.Join("; ", sample.CameraGeometrySamples)}]";
            string unresolvedSuffix = firstUnresolved is null
                ? string.Empty
                : $" · unresolved {trackers.Count - presented.Count}: {firstUnresolved.Diagnostic ?? "not fully submitted"}";
            return new LivePlanetGpuPresentation(
                frame.Snapshot.Version,
                ownedPlanetMapperStates,
                presented.ToImmutable(),
                dayNightPhases.ToImmutable(),
                mapperStarts.ToImmutable(),
                "render planet: R8A_LIVE_CURRENT_RETAIL_PLANET_CAMERA_GEOMETRY_ACTIVE · " +
                $"objects {presented.Count}/{trackers.Count} · mapper owners {ownedPlanetMapperStates.Count} · GPU packets {submittedPackets}/{expectedPackets} reject {rejectedPackets} culled {culledPackets} · " +
                $"geometry normal/aligned/oriented/skin/unsupported {normalGeometryPackets}/{alignedGeometryPackets}/{orientedGeometryPackets}/{skinGeometryPackets}/{unsupportedGeometryPackets} · " +
                $"PLT_DN packets {dayNightPackets} · {hlod} · {phase} · authored W3D camera geometry + HLOD/materials on OpenGL · GPU body + native ATMOS Screen blend in shared R6X/R6Y framebuffer" +
                geometrySample + unresolvedSuffix);
        }

        LivePlanetGpuFrameTracker unresolved = firstUnresolved ?? trackers.Values.First();
        return new LivePlanetGpuPresentation(
            frame.Snapshot.Version,
            ownedPlanetMapperStates,
            ImmutableHashSet<LivePlanetGpuIdentity>.Empty,
            dayNightPhases.ToImmutable(),
            mapperStarts.ToImmutable(),
            "render planet: R8A_CURRENT_RETAIL_PLANET_CAMERA_GEOMETRY_UNRESOLVED · " +
            $"objects 0/{trackers.Count} · mapper owners {ownedPlanetMapperStates.Count} · GPU packets {submittedPackets}/{expectedPackets} reject {rejectedPackets} culled {culledPackets} · " +
            $"geometry normal/aligned/oriented/skin/unsupported {normalGeometryPackets}/{alignedGeometryPackets}/{orientedGeometryPackets}/{skinGeometryPackets}/{unsupportedGeometryPackets} · " +
            $"PLT_DN packets {dayNightPackets} · {unresolved.Diagnostic ?? "not fully submitted"}");
    }

    private static string? DescribePlanetCameraGeometrySample(
        EnbW3dDrawTask packet,
        LiveLanePlan lane,
        EnbResolvedRenderCamera camera,
        IEnbRenderObject obj)
    {
        EnbCurrentRetailW3dGeometryClass geometryClass = EnbCurrentRetailCameraGeometryRuntime.Classify(packet.Mesh.Attributes);
        if (geometryClass is not (EnbCurrentRetailW3dGeometryClass.CameraAligned or EnbCurrentRetailW3dGeometryClass.CameraOriented))
            return null;

        EnbW3dIndexedSubmissionPlan? submission = lane.Submission;
        Matrix4x4? presentationWorld = lane.FrameUniforms?.ModelToWorld ?? lane.Lit?.ModelToWorld ?? lane.Textured?.LightingPlan.ModelToWorld;
        if (submission is null || presentationWorld is not Matrix4x4 world)
        {
            return $"{packet.Mesh.Name}/p{packet.Material.Source.Ordinal} {geometryClass} " +
                   $"s={packet.CurrentRetailMeshModelScale:0.######} raster receipt unavailable";
        }

        Vector3 localCenter = packet.Mesh.BoundingSphereCenter;
        Vector3 presentedCenter = Vector3.Transform(localCenter, world);
        Vector3 objectPosition = obj.Spatial?.Transform?.Position ?? new Vector3(float.NaN);
        float centerDelta = IsFinite(presentedCenter) && IsFinite(objectPosition)
            ? Vector3.Distance(presentedCenter, objectPosition)
            : float.NaN;
        Vector3 viewCenter = camera.WorldToCanonicalView(presentedCenter);
        float centerDepth = -viewCenter.Z;
        string centerProjection = camera.Projection.TryProjectViewPoint(viewCenter, out Vector3 centerNdc)
            ? $"center=({centerNdc.X:+0.00;-0.00;+0.00},{centerNdc.Y:+0.00;-0.00;+0.00})/{centerDepth:0.#}u"
            : "center=behind";

        float localRadius = packet.Mesh.BoundingRadius;
        float scaledRadius = localRadius * packet.CurrentRetailMeshModelScale;
        IReadOnlyList<Vector3> positions = submission.Value.Positions;
        IReadOnlyList<uint> indices = submission.Value.Indices;
        EnbRenderClipRange clipRange = camera.Projection.ClipRange;

        int projectableVertices = 0;
        int depthRangeVertices = 0;
        int viewportVertices = 0;
        float minX = float.PositiveInfinity;
        float minY = float.PositiveInfinity;
        float minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float maxY = float.NegativeInfinity;
        float maxZ = float.NegativeInfinity;

        for (int vertexOrdinal = 0; vertexOrdinal < positions.Count; vertexOrdinal++)
        {
            Vector3 worldPosition = Vector3.Transform(positions[vertexOrdinal], world);
            Vector3 viewPosition = camera.WorldToCanonicalView(worldPosition);
            float depth = -viewPosition.Z;
            if (!camera.Projection.TryProjectViewPoint(viewPosition, out Vector3 ndc))
                continue;

            projectableVertices++;
            minX = Math.Min(minX, ndc.X);
            minY = Math.Min(minY, ndc.Y);
            minZ = Math.Min(minZ, ndc.Z);
            maxX = Math.Max(maxX, ndc.X);
            maxY = Math.Max(maxY, ndc.Y);
            maxZ = Math.Max(maxZ, ndc.Z);

            bool inDepth = depth > clipRange.NearPlane && depth < clipRange.FarPlane && ndc.Z >= 0f && ndc.Z <= 1f;
            if (!inDepth)
                continue;

            depthRangeVertices++;
            if (ndc.X >= -1f && ndc.X <= 1f && ndc.Y >= -1f && ndc.Y <= 1f)
                viewportVertices++;
        }

        int clockwiseTriangles = 0;
        int counterClockwiseTriangles = 0;
        int degenerateTriangles = 0;
        int projectableTriangles = 0;
        int viewportCandidateTriangles = 0;
        int currentGlKeptViewportTriangles = 0;
        int triangleCount = indices.Count / 3;
        for (int triangleOrdinal = 0; triangleOrdinal < triangleCount; triangleOrdinal++)
        {
            int indexOffset = triangleOrdinal * 3;
            uint ia = indices[indexOffset];
            uint ib = indices[indexOffset + 1];
            uint ic = indices[indexOffset + 2];
            if (ia >= (uint)positions.Count || ib >= (uint)positions.Count || ic >= (uint)positions.Count)
                continue;

            if (!TryProjectPlanetDiagnosticVertex(positions[(int)ia], world, camera, out Vector3 a, out float depthA) ||
                !TryProjectPlanetDiagnosticVertex(positions[(int)ib], world, camera, out Vector3 b, out float depthB) ||
                !TryProjectPlanetDiagnosticVertex(positions[(int)ic], world, camera, out Vector3 c, out float depthC))
            {
                continue;
            }

            projectableTriangles++;
            float signedArea2 = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
            const float windingEpsilon = 1e-8f;
            int winding = signedArea2 > windingEpsilon ? 1 : signedArea2 < -windingEpsilon ? -1 : 0;
            if (winding > 0)
                counterClockwiseTriangles++;
            else if (winding < 0)
                clockwiseTriangles++;
            else
                degenerateTriangles++;

            float triangleMinX = Math.Min(a.X, Math.Min(b.X, c.X));
            float triangleMaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            float triangleMinY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
            float triangleMaxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
            float triangleMinDepth = Math.Min(depthA, Math.Min(depthB, depthC));
            float triangleMaxDepth = Math.Max(depthA, Math.Max(depthB, depthC));
            bool viewportCandidate =
                triangleMaxX >= -1f && triangleMinX <= 1f &&
                triangleMaxY >= -1f && triangleMinY <= 1f &&
                triangleMaxDepth > clipRange.NearPlane && triangleMinDepth < clipRange.FarPlane &&
                Math.Max(a.Z, Math.Max(b.Z, c.Z)) >= 0f && Math.Min(a.Z, Math.Min(b.Z, c.Z)) <= 1f;
            if (!viewportCandidate)
                continue;

            viewportCandidateTriangles++;
            EnbRenderCullMode cullMode = packet.Material.Source.TriangleBindings[triangleOrdinal].Semantics?.FixedFunction.CullMode
                ?? EnbRenderCullMode.None;
            if (CurrentOpenGlRasterKeepsTriangle(cullMode, winding))
                currentGlKeptViewportTriangles++;
        }

        string bounds = projectableVertices == 0
            ? "bbox=none"
            : $"bbox=x[{minX:+0.00;-0.00;+0.00},{maxX:+0.00;-0.00;+0.00}] y[{minY:+0.00;-0.00;+0.00},{maxY:+0.00;-0.00;+0.00}] z[{minZ:0.0000},{maxZ:0.0000}]";
        string raster = DescribePlanetRasterState(packet);
        string material = DescribePlanetMaterialState(lane);

        return
            $"{packet.Mesh.Name}/p{packet.Material.Source.Ordinal} {geometryClass} s={packet.CurrentRetailMeshModelScale:0.######} " +
            $"r={localRadius:0.#}->{scaledRadius:0.#}u centerΔ={centerDelta:0.#}u {centerProjection} · " +
            $"{bounds} v proj/depth/view={projectableVertices}/{depthRangeVertices}/{viewportVertices}/{positions.Count} · " +
            $"tri proj cw/ccw/deg={projectableTriangles} {clockwiseTriangles}/{counterClockwiseTriangles}/{degenerateTriangles} " +
            $"viewport/GLkeep={viewportCandidateTriangles}/{currentGlKeptViewportTriangles} · {raster} · {material}";
    }

    private static bool TryProjectPlanetDiagnosticVertex(
        Vector3 modelPosition,
        Matrix4x4 modelToWorld,
        EnbResolvedRenderCamera camera,
        out Vector3 ndc,
        out float depth)
    {
        Vector3 worldPosition = Vector3.Transform(modelPosition, modelToWorld);
        Vector3 viewPosition = camera.WorldToCanonicalView(worldPosition);
        depth = -viewPosition.Z;
        return camera.Projection.TryProjectViewPoint(viewPosition, out ndc);
    }

    private static bool CurrentOpenGlRasterKeepsTriangle(EnbRenderCullMode cullMode, int winding)
        => cullMode switch
        {
            EnbRenderCullMode.None => winding != 0,
            // The retail selector names the winding to discard. MapCullState
            // therefore keeps CCW for D3DCULL_CW and CW for D3DCULL_CCW while
            // using GL_BACK culling.
            EnbRenderCullMode.Clockwise => winding > 0,
            EnbRenderCullMode.CounterClockwise => winding < 0,
            _ => false,
        };

    private static string DescribePlanetRasterState(EnbW3dDrawTask packet)
    {
        var states = new HashSet<string>(StringComparer.Ordinal);
        foreach (EnbW3dRenderTriangleBinding binding in packet.Material.Source.TriangleBindings)
        {
            if (binding.Semantics is not EnbW3dCompiledMaterialSemantics semantics)
                continue;

            EnbW3dFixedFunctionState fixedFunction = semantics.FixedFunction;
            string blend = fixedFunction.BlendEnabled
                ? $"{fixedFunction.SourceBlend}>{fixedFunction.DestinationBlend}"
                : "off";
            string alpha = fixedFunction.AlphaTest.Enabled
                ? $"{fixedFunction.AlphaTest.Compare}@0x{fixedFunction.AlphaTest.Reference:X2}"
                : "off";
            states.Add(
                $"D={fixedFunction.DepthCompare}/{(fixedFunction.DepthWriteEnabled ? "W" : "R")} " +
                $"C={fixedFunction.CullMode} B={blend} A={alpha}");
        }

        return states.Count switch
        {
            0 => "raster=<none>",
            1 => "raster=" + states.First(),
            _ => $"raster=mixed({states.Count})",
        };
    }

    private static string DescribePlanetMaterialState(LiveLanePlan lane)
    {
        EnbW3dMaterialUniforms? firstMaterial = null;
        int runCount = 0;
        if (lane.Textured is EnbW3dProgrammableTextureLanePlan textured)
        {
            runCount = textured.RunStates.Length;
            if (runCount != 0)
                firstMaterial = textured.RunStates[0].LightingRun.Material;
        }
        else if (lane.Lit is EnbW3dProgrammableLightingLanePlan lit)
        {
            runCount = lit.RunStates.Length;
            if (runCount != 0)
                firstMaterial = lit.RunStates[0].Material;
        }

        if (firstMaterial is not EnbW3dMaterialUniforms material)
            return $"material runs={runCount}";

        return
            $"material runs={runCount} E=({material.Emissive.X:0.00},{material.Emissive.Y:0.00},{material.Emissive.Z:0.00}) " +
            $"D=({material.Diffuse.X:0.00},{material.Diffuse.Y:0.00},{material.Diffuse.Z:0.00},{material.DiffuseAlpha:0.00})";
    }

    private static bool IsRenderablePlanet(IEnbRenderObject obj)
        => obj.ObjectType == 0x03 || obj.Spatial?.ProviderKind == EnbSceneSpatialProviderKind.Planet;

    // MainView+80 (514270) adds a created object's RenderObj to the world scene
    // unless 6F5D90 reports type 0x1C or 0x1D; nothing else admits those.
    /// <summary>Next option (Modern UI "Hide corpses in space"): the world view leaves hulks (type 19h) out; RenderFrame's
    /// BuildObjectRoots returns before building their roots.</summary>
    internal bool HideHulks { get; set; }
    internal static bool MainViewAdmitsRenderObject(byte objectType) => objectType is not (0x1C or 0x1D);

    private static string DescribeR7StaticSectorObjects(
        IReadOnlyList<IEnbRenderObject> objects,
        int stateSelectionsApplied,
        int stateSelectionsRejected)
    {
        int gates = 0;
        int starbases = 0;
        int resourceLike = 0;
        int decorations = 0;
        int harvestables = 0;
        int harvestablePercentMaterialized = 0;
        int manufacturingLabs = 0;
        int unnamed29 = 0;
        int influenceNebula = 0;
        int lights = 0;

        foreach (IEnbRenderObject obj in objects)
        {
            switch (obj.ObjectType)
            {
                case 0x08: lights++; break;
                case 0x0A: influenceNebula++; break;
                case 0x0B: gates++; break;
                case 0x0C: starbases++; break;
                case 0x22: resourceLike++; break;
                case 0x25: decorations++; break;
                case 0x26:
                    harvestables++;
                    if (obj.HarvestablePercentFull.HasValue)
                        harvestablePercentMaterialized++;
                    break;
                case 0x27: manufacturingLabs++; break;
                case 0x29: unnamed29++; break;
            }
        }

        return
            "render static: R7_LIVE_STATIC_SECTOR_OBJECT_STATE_ACTIVE · " +
            $"gate {gates} · starbase {starbases} · resource22 {resourceLike} · deco25 {decorations} · " +
            $"harvest26 {harvestables} percent {harvestablePercentMaterialized} select/reject {stateSelectionsApplied}/{stateSelectionsRejected} · " +
            $"mfg27 {manufacturingLabs} · type29 {unnamed29} · influence0A {influenceNebula} · light08 {lights}";
    }

    private static string DescribeR7AInfluenceNebula(
        int influenceObjects,
        int boundarySources,
        int outside,
        int between,
        int room,
        int rejected,
        bool environmentApplied,
        EnbCurrentRetailInfluenceNebulaEvaluation? appliedEnvironment,
        EnbW3dSceneFogState effectiveFog,
        string? firstDiagnostic)
    {
        string state = environmentApplied && appliedEnvironment is not null
            ? appliedEnvironment.Region == EnbCurrentRetailInfluenceNebulaRegion.Room
                ? $"Room ref {appliedEnvironment.RoomReferenceIndex?.ToString() ?? "?"}"
                : "Between"
            : "ordinary-sector";
        string first = string.IsNullOrWhiteSpace(firstDiagnostic) ? "[none]" : firstDiagnostic;
        return
            "render influence: R7A_LIVE_CURRENT_RETAIL_INFLUENCE_NEBULA_BOUNDARY_ACTIVE · " +
            $"objects {influenceObjects} · boundary {boundarySources} · outside/between/room {outside}/{between}/{room} · reject {rejected} · " +
            $"env {state} · fog {(effectiveFog.Enabled ? "on" : "off")} 0x{effectiveFog.SceneColorRgb24:X6} {effectiveFog.Start:0.###}/{effectiveFog.End:0.###} · " +
            $"sky BaseAsset {EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset} authored sky path R7B1 ready · first {first}";
    }

    private string DescribeR6Z34EffectRslReceipt(
        EnbCurrentRetailEffectRslSchedulerUpdate? schedulerUpdate,
        int objectRuntimeCount,
        int effectActionsApplied,
        int effectEventsIgnored,
        int unmatchedEvents,
        int failClosedEvents)
    {
        if (schedulerUpdate is null)
            return "render effects: R6Z34 current-retail duration-linked effect timing unavailable (presentation catalog missing)";

        EnbCurrentRetailEffectRslSchedulerReceipt receipt = schedulerUpdate.Receipt;
        string durationLinkedExample = string.IsNullOrWhiteSpace(receipt.DurationLinkedExample)
            ? "[none]"
            : receipt.DurationLinkedExample;
        return
            "render effects: R6Z34_LIVE_CURRENT_RETAIL_DURATION_LINKED_EFFECT_TIMING_ACTIVE · " +
            $"roots snapshot/tracked/new/reject {receipt.SnapshotRoots}/{receipt.TrackedRoots}/{receipt.CreatedRoots}/{receipt.RejectedRoots} · " +
            $"dur0E tracked/new/reject {receipt.TrackedDurationLinkedRoots}/{receipt.ResolvedDurationLinkedRoots}/{receipt.RejectedDurationLinkedRoots} · " +
            $"sample {durationLinkedExample} · " +
            $"retiring/tomb {receipt.RetiringRoots}/{receipt.CompletedTombstones} · " +
            $"nodes sched/active/ended {receipt.ScheduledNodes}/{receipt.ActiveNodes}/{receipt.EndedWaitingForChildren} · " +
            $"start/end {receipt.StartedNodes}/{receipt.CompletedNodes} · links start/next {receipt.CreatedStartLinks}/{receipt.CreatedNextLinks} · " +
            $"remove {receipt.RemovalMarks} · objects {objectRuntimeCount} · actions {effectActionsApplied} · ignored {effectEventsIgnored} · " +
            $"unmatched {unmatchedEvents} · fail {failClosedEvents}" +
            "\neffect lifecycle (last 64 edges, client clock):\n  " + _effectRslScheduler.LifecycleHistory +
            "\neffect surface changes (last 16, client clock):\n  " + _effectVisualHistoryText;
    }

    private bool TryResolveLiveSceneEnvironment(
        LiveFrame frame,
        EnbRetailBaseAssetResolver resolver,
        EnbRetailAssetBundle retailAssets,
        out EnbRetailOrbitalFogEvaluation evaluation,
        out EnbRetailMainViewLightingEvaluation? mainViewLighting,
        out bool fogResolved,
        out string? diagnostic)
    {
        evaluation = default;
        mainViewLighting = null;
        fogResolved = false;
        diagnostic = null;
        EnbSceneEnvironmentSnapshot? environment = frame.Snapshot.Environment;
        if (environment is null)
        {
            diagnostic = "Server Parameters have not materialized yet.";
            return false;
        }

        if (!EnbRetailOrbitalFogEvaluator.TryResolveWorldFogEnabled(environment, out bool enabled, out diagnostic))
            return false;
        if (!float.IsFinite(environment.FogNear) || !float.IsFinite(environment.FogFar))
        {
            diagnostic = "Server Parameters fog range contains a non-finite value.";
            return false;
        }

        if (!enabled)
        {
            // This is already exact at this point even if the local ambient assets
            // later fail. Keep that receipt independently ambient
            // realization.
            fogResolved = true;
            evaluation = new EnbRetailOrbitalFogEvaluation(
                new EnbW3dSceneFogState(false, 0u, environment.FogNear, environment.FogFar),
                Vector3.Zero,
                0u,
                0,
                0,
                0);
        }

        if (!_orbitalScenes.TryGetValue(environment.SectorNum, out EnbRetailOrbitalSceneDefinition? orbitalScene))
        {
            if (!retailAssets.Paths.TryResolveOrbitalIniPath(environment.SectorNum, out string path))
            {
                // MainViewLoadOrbitalIni @ 00513700 resets OrbitalManager through
                // 00403EA4 -> 004F3310 before asking the current retail resource
                // manager for orbital%04d.ini. When no loose candidate resolves,
                // the INI is not loaded and OrbitalManagerLoadIni is skipped.
                bool resetResolved = EnbRetailOrbitalFogEvaluator.TryEvaluateMissingOrbitalIniResetState(
                    environment,
                    out evaluation,
                    out diagnostic);
                fogResolved = resetResolved;
                return resetResolved;
            }

            try
            {
                orbitalScene = EnbRetailOrbitalSceneDefinition.Load(path);
                _orbitalScenes[environment.SectorNum] = orbitalScene;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                diagnostic = $"{path}: {ex.Message}";
                return false;
            }
        }

        PrepareOrbitalFactories(orbitalScene,frame,retailAssets);

        if (_sceneFogData is null && _sceneFogDataFailure is null)
        {
            try
            {
                _sceneFogData = EnbRetailSceneFogData.Load(retailAssets.Paths);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                _sceneFogDataFailure = $"splines.wsd: {ex.Message}";
            }
        }

        if (_sceneFogData is null)
        {
            diagnostic = _sceneFogDataFailure ?? "splines.wsd is unavailable.";
            return false;
        }

        int loadCount = environment.SplineUpdateCountAtLoad;
        if (loadCount < 0 || loadCount > frame.Snapshot.SplineUpdates.Length)
        {
            diagnostic = $"0x0042 captured spline prefix {loadCount}, but the immutable snapshot contains {frame.Snapshot.SplineUpdates.Length} updates.";
            return false;
        }

        string? baseSplineDiagnostic = _sceneFogData.BaseLoadDiagnostic;
        if (baseSplineDiagnostic is not null)
            baseSplineDiagnostic += $"; runtime prefix {loadCount}/{frame.Snapshot.SplineUpdates.Length}";

        if(!_loadSceneSplines.TryResolve(_sceneFogData.Splines,frame.Snapshot.SplineUpdates,loadCount,
            out var splines,out var beforeDiagnostic))
        {
            diagnostic=$"runtime 0x0038 spline state before 0x0042: {beforeDiagnostic}";
            return false;
        }

        if (!EnbRetailOrbitalFogEvaluator.TrySampleSkyEmissiveAtLoad(
                orbitalScene,
                splines,
                out Vector3 cachedSkyEmissiveRgb,
                out string? skyEmissiveDiagnostic))
        {
            // Missing SKYEMISSIVE does not invalidate an already-computable
            // fog result. For enabled fog, reconstruct the current database and
            // preserve before reporting the ambient gap.
            if (enabled)
            {
                if(!_currentSceneSplines.TryResolve(_sceneFogData.Splines,frame.Snapshot.SplineUpdates,
                    frame.Snapshot.SplineUpdates.Length,out splines,out var afterDiagnostic))
                {
                    diagnostic=$"runtime 0x0038 spline state after 0x0042: {afterDiagnostic}";
                    return false;
                }

                fogResolved = EnbRetailOrbitalFogEvaluator.TryEvaluateFog(
                    environment,
                    frame.Snapshot.GetOrbitalTimeMilliseconds(frame.CurrentClientTime),
                    frame.Camera.Position,
                    orbitalScene,
                    splines,
                    (EnbRetailOrbitalDefinition orbital, Matrix4x4 orbitalWorld, out EnbW3dWorldBounds sphere, out string? sphereDiagnostic) =>
                        TryResolveOrbitalWorldSphere(resolver, retailAssets, orbital, orbitalWorld, out sphere, out sphereDiagnostic),
                    out evaluation,
                    out string? fogOnlyDiagnostic);
                if (!fogResolved)
                {
                    diagnostic = fogOnlyDiagnostic;
                    return false;
                }
            }

            diagnostic = CombineEnvironmentDiagnostic(baseSplineDiagnostic, skyEmissiveDiagnostic);
            return false;
        }

        if(!_currentSceneSplines.TryResolve(_sceneFogData.Splines,frame.Snapshot.SplineUpdates,
            frame.Snapshot.SplineUpdates.Length,out splines,out var currentDiagnostic))
        {
            diagnostic=$"runtime 0x0038 spline state after 0x0042: {currentDiagnostic}";
            return false;
        }

        bool resolved = EnbRetailOrbitalFogEvaluator.TryEvaluate(
            environment,
            frame.Snapshot.GetOrbitalTimeMilliseconds(frame.CurrentClientTime),
            frame.Camera.Position,
            orbitalScene,
            splines,
            cachedSkyEmissiveRgb,
            (EnbRetailOrbitalDefinition orbital, Matrix4x4 orbitalWorld, out EnbW3dWorldBounds sphere, out string? sphereDiagnostic) =>
                TryResolveOrbitalWorldSphere(resolver, retailAssets, orbital, orbitalWorld, out sphere, out sphereDiagnostic),
            out evaluation,
            out diagnostic, evaluateModeZeroLight: environment.PresentationMode is 0 or 5);
        fogResolved = resolved;
        if (resolved)
        {
            string? lightDiagnostic = null;
            if (!TryResolveMainViewLighting(
                    frame,
                    retailAssets,
                    evaluation.DominantLight,
                    evaluation.GlobalAmbientRgb24,
                    out mainViewLighting,
                    out lightDiagnostic))
            {
                mainViewLighting = null;
            }
            diagnostic = CombineEnvironmentDiagnostic(baseSplineDiagnostic, lightDiagnostic);
        }
        return resolved;
    }

    private bool TryResolveMainViewLighting(
        LiveFrame frame,
        EnbRetailAssetBundle retailAssets,
        EnbRetailTerrainLightState? dominant,
        uint baseGlobalAmbientRgb24,
        out EnbRetailMainViewLightingEvaluation? evaluation,
        out string? diagnostic)
    {
        evaluation = null;
        diagnostic = null;
        EnbSceneEnvironmentSnapshot? environment = frame.Snapshot.Environment;
        if (environment is null || !EnbCurrentRetailMainViewPassRuntime.UsesModeZeroLightRig(environment.PresentationMode))
        {
            diagnostic = environment?.PresentationMode is null
                ? "R9AF MainView lighting awaits the retail presentation mode"
                : $"R9AF MainView lighting mode {environment.PresentationMode.Value} is outside the exact mode-0 slice";
            return false;
        }

        if (!_mainViewLightSectors.TryGetValue(
                environment.SectorNum,
                out EnbRetailMainViewLightSectorDefinition? authored))
        {
            if (!retailAssets.Paths.TryResolveMainViewLightSectorIniPath(environment.SectorNum, out string path))
            {
                // OrbitalManager retains its constructor-zero light-sector
                // table when the formatted INI resource does not resolve.
                authored = EnbRetailMainViewLightSectorDefinition.Empty;
            }
            else
            {
                try
                {
                    authored = EnbRetailMainViewLightSectorDefinition.Load(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                {
                    diagnostic = $"{path}: {ex.Message}";
                    return false;
                }
            }
            _mainViewLightSectors[environment.SectorNum] = authored;
        }

        if (!EnbRetailMainViewLightingEvaluator.TryEvaluateModeZero(
                frame.Camera,
                authored,
                baseGlobalAmbientRgb24,
                dominant,
                out EnbRetailMainViewLightingEvaluation resolved,
                out diagnostic))
        {
            return false;
        }

        evaluation = resolved;
        return true;
    }

    private static string? CombineEnvironmentDiagnostic(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return second;
        if (string.IsNullOrWhiteSpace(second))
            return first;
        return $"{first}; {second}";
    }

    private bool TryResolveOrbitalWorldSphere(
        EnbRetailBaseAssetResolver resolver,
        EnbRetailAssetBundle retailAssets,
        EnbRetailOrbitalDefinition orbital,
        Matrix4x4 orbitalWorld,
        out EnbW3dWorldBounds sphere,
        out string? diagnostic)
    {
        sphere = default;
        diagnostic = null;
        if (!float.IsFinite(orbital.Scale))
        {
            diagnostic = "authored SCALE is non-finite.";
            return false;
        }

        var factory=ResolveOrbitalFileFactory(orbital,retailAssets);
        Matrix4x4 world = Matrix4x4.CreateScale(factory?.Scale??orbital.Scale) * orbitalWorld;
        if (!IsFinite(world))
        {
            diagnostic = "authored SCALE and orbital transform produced a non-finite world matrix.";
            return false;
        }

        EnbBaseAssetResolution resolution = factory?.Factory.Resolution??ResolveBaseAsset(resolver, orbital.BaseAsset);
        if (resolution.Status == EnbBaseAssetResolutionStatus.NullObject)
        {
            //004F2960 admits the explicit NULLOBJECT factory.00941540
            //supplies radius .1; inherited009421B0 transforms its center,
            //leaving that radius unchanged even under authored scale.
            var bounds = EnbCurrentRetailRenderBoundsStorage.Calculate(world,
                new Vector4(0, 0, 0, .1f), Vector3.Zero, new Vector3(.1f));
            sphere = new(new Vector3(bounds.Sphere.X, bounds.Sphere.Y, bounds.Sphere.Z), bounds.Sphere.W);
            return sphere.IsValid;
        }
        if(factory is not null)
        {
            if(factory.Factory.Root is {} selected)return TryComputeRootWorldSphere(selected,world,out sphere,out diagnostic);
            diagnostic=factory.Factory.Diagnostic;return false;
        }
        if (resolution.Status == EnbBaseAssetResolutionStatus.W3d && resolution.W3d is EnbResolvedW3dResource w3d)
        {
            var key = new LiveCpuRootKey(orbital.BaseAsset, -1, w3d.LogicalFileName, w3d.RequestedObjectName);
            if (!TryGetCpuRoot(key, w3d, retailAssets, out LiveCpuRoot root, out string? rootDiagnostic))
            {
                diagnostic = $"W3D root '{w3d.RequestedObjectName}' could not be compiled: {rootDiagnostic ?? "unknown rejection"}.";
                return false;
            }
            return TryComputeRootWorldSphere(root, world, out sphere, out diagnostic);
        }

        if (resolution.Status == EnbBaseAssetResolutionStatus.RslReference && resolution.Rsl is EnbResolvedRslReference rsl)
        {
            if (!TryGetInitialRslRoot(orbital.BaseAsset, rsl, retailAssets, out LiveCpuRoot root, out diagnostic))
                return false;
            return TryComputeRootWorldSphere(root, world, out sphere, out diagnostic);
        }

        diagnostic = $"BaseAsset resolves as {resolution.Status}, which has no proven ordinary RenderObject +0xF0 world-sphere handoff.";
        return false;
    }

    private bool TryGetInitialRslRoot(
        int baseAsset,
        EnbResolvedRslReference rsl,
        EnbRetailAssetBundle retailAssets,
        out LiveCpuRoot root,
        out string? diagnostic)
    {
        root = default!;
        if (!TryFindInitialRslObjectState(
                rsl,
                out int activeState,
                out EnbResolvedRslStateResource state,
                out diagnostic))
        {
            return false;
        }

        return TryMaterializeRslObjectRoot(
            baseAsset,
            activeState,
            state,
            retailAssets,
            out root,
            out diagnostic);
    }

    private static bool TryFindInitialRslObjectState(
        EnbResolvedRslReference rsl,
        out int stateId,
        out EnbResolvedRslStateResource state,
        out string? diagnostic)
    {
        stateId = default;
        state = default!;
        diagnostic = null;
        EnbResolvedRslLibraryResourceGraph? graph = rsl.ResourceGraph;
        if (graph is null)
        {
            diagnostic = "RSL resource graph is unavailable.";
            return false;
        }

        var initialLinks = new Dictionary<int, int>();
        foreach (EnbRslRenderStateCommand command in rsl.InitialStateCommands)
        {
            if (command.Kind == EnbRslRenderStateCommandKind.InitializeState && command.ArgumentStateId is int target &&
                (!graph.TryFindState(command.StateId,out var initialized) || !EnbRslRenderStateSemantics.IsNoOpInitializer(initialized.Definition.Type)))
                initialLinks[command.StateId] = target;
        }

        int[] objectStates = rsl.InitialStateCommands
            .Where(static command => command.Kind == EnbRslRenderStateCommandKind.ActivateState)
            .Select(command => ResolveRslStateLink(command.StateId, initialLinks))
            .Distinct()
            .Where(candidate =>
                graph.TryFindState(candidate, out EnbResolvedRslStateResource resolved) &&
                resolved.Definition.Type == EnbRslStateType.Object)
            .ToArray();
        if (objectStates.Length != 1)
        {
            diagnostic =
                $"RSL initial state resolves {objectStates.Length} active OBJECT render roots; non-OBJECT states are not counted as wrapper geometry and EnbNext will not invent aggregation semantics.";
            return false;
        }

        stateId = objectStates[0];
        if (!graph.TryFindState(stateId, out state))
        {
            diagnostic = $"RSL initial OBJECT state {stateId} is absent from the resource graph.";
            return false;
        }
        return true;
    }

    private static bool TryComputeRootWorldSphere(
        LiveCpuRoot root,
        Matrix4x4 world,
        out EnbW3dWorldBounds sphere,
        out string? diagnostic)
    {
        sphere = default;
        diagnostic = null;
        if(root.Collection is not null || root.SpecialMaterial is not null || root.Sphere is not null || root.Ring is not null || root.RetainedEmitter is not null)
        {
            if(!TryComputeRootLocalBounds(root,out var local,out diagnostic))return false;
            var transformed=EnbCurrentRetailRenderBoundsStorage.Calculate(world,local.Sphere,local.BoxCenter,local.BoxExtent);
            sphere=new(new(transformed.Sphere.X,transformed.Sphere.Y,transformed.Sphere.Z),transformed.Sphere.W);
            return sphere.IsValid;
        }
        // 0600 attachments are already inserted into retained HLOD child
        // storage during materialization. Its base-pose bounds include them;
        // specialized emitter entries have zero local bounds (0097D4B0).
        if (root.Hlod is EnbW3dRenderHlod hlod)
        {
            if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                    root.Resource,
                    hlod,
                    world,
                    animation: null,
                    animationFrame: 0f,
                    out EnbW3dHlodBoundsOwner bounds,
                    out diagnostic))
            {
                return false;
            }
            // HLOD +0xF0 copies its already-scaled model radius. Our shared
            // compiler deliberately leaves immutable model radii unscaled;
            // ordinary culling supplies this factor separately. Orbital clip
            // limits and occlusion need it here as well (e.g. SUN02 SCALE=4).
            float modelScale = MathF.Max(new Vector3(world.M11,world.M12,world.M13).Length(),
                MathF.Max(new Vector3(world.M21,world.M22,world.M23).Length(),
                    new Vector3(world.M31,world.M32,world.M33).Length()));
            sphere = bounds.WorldSphere with { Radius = bounds.WorldSphere.Radius * modelScale };
            return sphere.IsValid;
        }

        if(root.Resource.ExternalRenderObjects.Any(value=>value.DazzleDefinition is not null &&
            string.Equals(value.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase)))
        {
            if(!TryComputeRootLocalBounds(root,out var local,out diagnostic))return false;
            var bounds=EnbCurrentRetailRenderBoundsStorage.Calculate(world,local.Sphere,local.BoxCenter,local.BoxExtent);
            sphere=new(new Vector3(bounds.Sphere.X,bounds.Sphere.Y,bounds.Sphere.Z),bounds.Sphere.W);
            return sphere.IsValid;
        }

        if(root.Resource.ExternalRenderObjects.Any(value=>value.Kind==EnbW3dExternalRenderObjectKind.NullObject &&
            string.Equals(value.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase)))
        {
            var bounds=EnbCurrentRetailRenderBoundsStorage.Calculate(world,new Vector4(0,0,0,.1f),Vector3.Zero,new Vector3(.1f));
            sphere=new(new Vector3(bounds.Sphere.X,bounds.Sphere.Y,bounds.Sphere.Z),bounds.Sphere.W);
            return sphere.IsValid;
        }

        if (root.AllowedRenderObjectNames is not { Count: > 0 } allowed)
        {
            diagnostic = "compiled root has neither an HLOD nor an explicit mesh root.";
            return false;
        }

        EnbW3dRenderMesh[] meshes = root.Resource.Meshes
            .Where(mesh => allowed.Contains(mesh.Name))
            .ToArray();
        if (meshes.Length != 1)
        {
            diagnostic = $"compiled non-HLOD root contains {meshes.Length} mesh children; current wrapper +0xF0 aggregation is not reproduced by approximation.";
            return false;
        }

        EnbW3dRenderMesh mesh = meshes[0];
        if (!float.IsFinite(mesh.BoundingRadius) || mesh.BoundingRadius < 0f || !IsFinite(mesh.BoundingSphereCenter))
        {
            diagnostic = $"mesh '{mesh.Name}' authored sphere is invalid.";
            return false;
        }

        float xScale = new Vector3(world.M11, world.M12, world.M13).Length();
        float yScale = new Vector3(world.M21, world.M22, world.M23).Length();
        float zScale = new Vector3(world.M31, world.M32, world.M33).Length();
        float maxScale = MathF.Max(xScale, MathF.Max(yScale, zScale));
        sphere = new EnbW3dWorldBounds(
            Vector3.Transform(mesh.BoundingSphereCenter, world),
            mesh.BoundingRadius * maxScale);
        if (!sphere.IsValid)
        {
            diagnostic = $"mesh '{mesh.Name}' world sphere is invalid after the authored transform.";
            return false;
        }
        return true;
    }

    private static bool TryComputeCollectionLocalBounds(LiveCpuRoot root,out EnbCurrentRetailCollectionBounds bounds,out string? diagnostic)
    {
        bounds=default;diagnostic=null;
        var childBounds=new EnbCurrentRetailCollectionBounds[root.CollectionChildren.Count];
        for(int index=0;index<childBounds.Length;index++)
            if(!TryComputeRootLocalBounds(root.CollectionChildren[index],out childBounds[index],out diagnostic))return false;
        bounds=EnbCurrentRetailCollectionBounds.Calculate(()=>childBounds.Length,index=>childBounds[index].Sphere,
            index=>(childBounds[index].BoxCenter,childBounds[index].BoxExtent));
        return true;
    }

    private static bool TryComputeRootLocalBounds(LiveCpuRoot root,out EnbCurrentRetailCollectionBounds bounds,out string? diagnostic)
    {
        if(root.NamedChildBoundsRoot is {} named)return TryComputeRootLocalBounds(named,out bounds,out diagnostic);
        if (root.InfluenceLocalBounds is {} influenceBounds) { bounds=influenceBounds; diagnostic=null; return true; }
        bounds=default;diagnostic=null;
        if(root.RetainedEmitter is not null)return true; //0097D4B0/0097D4D0: zero local sphere/box.
        if(root.SpecialMaterial is {} special)
        {
            var d=special.Definition;
            bounds=new(new(d.SphereCenter,d.SphereRadius),(d.Minimum+d.Maximum)*.5f,(d.Maximum-d.Minimum)*.5f);return true;
        }
        if(root.Sphere is {} sphere)
        {
            var header=sphere.Asset.Header;
            var local=EnbRetailSphereBounds.Local(header.Center,header.AuthoredExtent);
            bounds=new(new(local.Center,local.Radius),header.Center,header.AuthoredExtent);return true;
        }
        if(root.Ring is {} ring)
        {
            var header=ring.Asset.Header;
            var extent=new Vector3(header.SecondRadii,0);
            bounds=new(new(header.Center,extent.Length()),header.Center,extent);return true;
        }
        if(root.Collection is not null)return TryComputeCollectionLocalBounds(root,out bounds,out diagnostic);
        if(root.Hlod is {} hlod)
        {
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(root.Resource,hlod,Matrix4x4.Identity,
                animation:null,animationFrame:0,out var value,out diagnostic))return false;
            bounds=new(new(value.LocalSphere.Center,value.LocalSphere.Radius),value.LocalBox.Center,value.LocalBox.Extents);return true;
        }
        var external=root.Resource.ExternalRenderObjects.FirstOrDefault(value=>string.Equals(value.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase));
        if(external?.Kind==EnbW3dExternalRenderObjectKind.NullObject)
        { bounds=new(new(0,0,0,.1f),Vector3.Zero,new(.1f));return true; }
        //0096BEC0/0096BEE0: constructor radius at+F4, multiplied by instance+E8 (initially1).
        if(external?.DazzleDefinition is {} dazzle)
        { float radius=dazzle.Radius*external.DazzleInstanceScale; bounds=new(new(0,0,0,radius),Vector3.Zero,new(radius));return true; }
        if(external?.BoxDefinition is {} boxDefinition)
        { bounds=EnbCurrentRetailRenderBoundsStorage.BoxLocalBounds(boxDefinition.Center,boxDefinition.Extents);return true; }
        var meshes=root.Resource.Meshes.Where(mesh=>root.AllowedRenderObjectNames?.Contains(mesh.Name)==true).ToArray();
        if(meshes.Length==1)
        {
            var mesh=meshes[0];var box=EnbCurrentRetailRenderBoundsStorage.MeshLocalBox(mesh.BoundingBoxMin,mesh.BoundingBoxMax);
            bounds=new(new(mesh.BoundingSphereCenter,mesh.BoundingRadius),box.Center,box.Extent);return true;
        }
        diagnostic=$"Root '{root.RootRenderObjectName}' has no resolved concrete local bounds.";return false;
    }

    private static bool TryDescribeCompositeChild(LiveCpuRoot root,string name,
        out EnbW3dRenderCompositeChild child,out string? diagnostic)
    {
        if(root.NamedChildBoundsRoot is {} snapshot)
        {
            if(!TryDescribeCompositeChild(snapshot,name,out child,out diagnostic))return false;
            child=RefreshStoredDazzleBounds(child,root);
            return true;
        }
        child=default!;
        if(!TryComputeRootLocalBounds(root,out var bounds,out diagnostic)||!TryComputeRootIntegerCost(root,out int cost,out diagnostic))return false;
        if(root.SpecialMaterial is not null){child=new(name,0x24,bounds,cost);return true;}
        if(root.Sphere is not null){child=new(name,0x1d,bounds,cost);return true;}
        if(root.Ring is not null){child=new(name,0x1e,bounds,cost);return true;}
        if(root.RetainedEmitter is not null){child=new(name,0x0f,bounds,cost);return true;}
        if(root.Collection is not null)
        {
            var children=new List<EnbW3dRenderCompositeChild>();
            foreach(var member in root.CollectionChildren)
            {if(!TryDescribeCompositeChild(member,member.RootRenderObjectName,out var value,out diagnostic))return false;children.Add(value);}
            child=new(name,0x17,bounds,cost){CollectionChildren=children.ToArray()};return true;
        }
        if(root.Hlod is {} hlod)
        {child=new(name,0x19,bounds,cost){HlodResource=ConfiguredHierarchyResource(root),Hlod=hlod,HasConfiguredHierarchy=root.ConfiguredHierarchyTranslations is not null};return true;}
        var mesh=root.Resource.Meshes.SingleOrDefault(value=>root.AllowedRenderObjectNames?.Contains(value.Name)==true);
        var external=root.Resource.ExternalRenderObjects.FirstOrDefault(value=>string.Equals(value.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase));
        child=mesh is null?new(name,external?.DazzleDefinition is not null?0x20:external?.BoxDefinition is {} box?box.RetailClassId:0x16,bounds,cost):new(name,0,bounds,cost){MeshMinimum=mesh.BoundingBoxMin,MeshMaximum=mesh.BoundingBoxMax};
        if(external?.DazzleDefinition is {} dazzle)child=child with {DazzleDefinitionRadius=dazzle.Radius,DazzleInstanceScale=external.DazzleInstanceScale};
        return true;
    }

    //96BAB0 changes the retained dazzle scalar at each parent Scale call.
    //Named mesh/pivot snapshots still describe their pre-parent storage, but
    //a dazzle getter must observe its current scalar, not replay that scale.
    private static EnbW3dRenderCompositeChild RefreshStoredDazzleBounds(EnbW3dRenderCompositeChild child,LiveCpuRoot live)
    {
        if(child.DazzleDefinitionRadius is not null)
        {
            var dazzle=live.Resource.ExternalRenderObjects.Single(e=>e.DazzleDefinition is not null);
            return child with {DazzleInstanceScale=dazzle.DazzleInstanceScale};
        }
        if(child.CollectionChildren is {} members)
            return child with {CollectionChildren=members.Select((member,index)=>RefreshStoredDazzleBounds(member,live.CollectionChildren[index])).ToArray()};
        if(child.HlodResource is {} resource && child.Hlod is {} hlod)
        {
            var descriptions=resource.CompositeRenderChildren.ToDictionary(value=>value.Name,StringComparer.OrdinalIgnoreCase);
            var names=hlod.Levels.SelectMany(level=>level.Members).Concat(hlod.Aggregate?.Members??[]).ToArray();
            foreach(var member in live.HmodelChildren)
            {
                string name=names[member.StorageOrdinal].Name;
                if(descriptions.TryGetValue(name,out var description))descriptions[name]=RefreshStoredDazzleBounds(description,member.Root);
            }
            return child with {HlodResource=resource.Copy().SetCompositeRenderChildren(descriptions.Values.ToArray()).SetExternalRenderObjects(resource.ExternalRenderObjects.Select(value=>value.DazzleDefinition is null?value:
                    live.Resource.ExternalRenderObjects.Single(current=>current.Name.Equals(value.Name,StringComparison.OrdinalIgnoreCase))).ToArray())};
        }
        return child;
    }

    private static bool TryComputeRootIntegerCost(LiveCpuRoot root,out int cost,out string? diagnostic)
    {
        cost=0;diagnostic=null;
        if(root.RetainedEmitter is not null)return true; //Emitter+20 inherits00462950.
        if(root.SpecialMaterial is {} special){foreach(var member in special.Definition.Members)cost=unchecked(cost+(int)member.TriangleCount);return true;}
        if(root.Sphere is not null){cost=(int)EnbSphereRingDetailBudgetSemantics.SphereLevelCosts[EnbSphereRingDetailBudgetSemantics.SphereInitialLevelOrdinal];return true;}
        if(root.Ring is not null){cost=(int)EnbSphereRingDetailBudgetSemantics.RingLevelCosts[20];return true;}
        if(root.Collection is not null)
        {
            foreach(var child in root.CollectionChildren)
            { if(!TryComputeRootIntegerCost(child,out int childCost,out diagnostic))return false;cost=unchecked(cost+childCost); }
            return true;
        }
        if(root.Hlod is {} hlod)
        {
            if(!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(root.Resource,hlod,_=>true,out var levels,out diagnostic))return false;
            cost=levels[EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod)];
            if(hlod.Aggregate is {} aggregate)foreach(var member in aggregate.Members)
            { if(!EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(root.Resource,member.Name,out int childCost,out diagnostic))return false;cost=unchecked(cost+childCost); }
            return true;
        }
        if(root.Resource.ExternalRenderObjects.Any(value=>string.Equals(value.Name,root.RootRenderObjectName,StringComparison.OrdinalIgnoreCase)))
            return EnbW3dHlodBudgetCompiler.TryResolveNamedRenderObjectCost(root.Resource,root.RootRenderObjectName,out cost,out diagnostic);
        foreach(var mesh in root.Resource.Meshes.Where(mesh=>root.AllowedRenderObjectNames?.Contains(mesh.Name)==true))
            cost=unchecked(cost+mesh.TriangleMetadata.Count*mesh.CurrentRetailRenderPassCount);
        return true;
    }

    private static bool HasCollectionAncestor(LiveRootInstance instance)
    {
        for(var child=instance;child.Parent is {} parent;child=parent)
            if(child.IsOrdinaryHlodChild||parent.Root.Collection is not null)return true;
        return false;
    }
    private static bool CollectionAncestorsDraw(LiveRootInstance instance)
    {
        if(instance.Root.RenderObjectHidden||instance.EngineChildHidden||instance.MovieHidden)return false;
        for(var child=instance;child.Parent is {} parent;child=parent)
            if((child.IsOrdinaryHlodChild||child.IsAggregateHlodChild||parent.Root.Collection is not null)&&
                (!parent.ShouldDraw||parent.Root.RenderObjectHidden||parent.EngineChildHidden||parent.MovieHidden))return false;
        return true;
    }

    private static string DescribeFogReceipt(
        EnbSceneEnvironmentSnapshot? environment,
        bool resolved,
        EnbRetailOrbitalFogEvaluation evaluation,
        EnbW3dFogPipelineSemantics pipeline,
        string? diagnostic)
    {
        if (!resolved)
            return $"fog unresolved: {diagnostic ?? "unknown"}";

        EnbW3dSceneFogState fog = evaluation.SceneFog;
        string mode = environment?.PresentationMode?.ToString() ?? "?";
        string sector = environment?.SectorNum.ToString() ?? "?";
        string state = fog.Enabled ? "on" : "off";
        return $"fog {state} rgb=0x{fog.SceneColorRgb24:X6} {fog.Start:0.###}/{fog.End:0.###} mode {mode} sector {sector} " +
               $"orbitals {evaluation.EmittingOrbitalCount}/{evaluation.OrbitalCount} occ {evaluation.OccludedSampleCount} distance {pipeline.DistanceSource}";
    }

    private static EnbW3dLightEnvironment BuildLiveLightEnvironment(
        EnbSceneLightingSnapshot lighting,
        uint globalAmbientRgb24,
        EnbRetailMainViewLightingEvaluation? mainViewLighting)
    {
        ArgumentNullException.ThrowIfNull(lighting);
        IEnumerable<EnbW3dLightSlot> serverLights = lighting.Lights.Select(static light => new EnbW3dLightSlot(
            Enabled: true,
            Light: new EnbW3dCompiledLight(
                EnbW3dLightType.Point,
                light.Diffuse,
                light.Specular,
                light.Ambient,
                light.Position,
                light.Direction,
                light.Range,
                light.Falloff,
                light.Attenuation0,
                light.Attenuation1,
                light.Attenuation2,
                light.Theta,
                light.Phi)));
        IEnumerable<EnbW3dLightSlot> orbitalLights = mainViewLighting?.DirectionalLights ?? [];

        // Server-created scene lights persist while MainView removes and
        // re-adds its four owned directional objects every frame. Retail's
        // first-added four-slot snapshot therefore retains server lights first
        // and fills the remaining slots from the orbital rig in its add order.
        return EnbW3dLightEnvironment.CreateCurrentRetail(
            globalAmbientRgb24,
            serverLights.Concat(orbitalLights).Take(EnbW3dLightEnvironment.CurrentRetailLightSlotCount));
    }

    private EnbBaseAssetResolution ResolveBaseAsset(EnbRetailBaseAssetResolver resolver, int selector)
    {
        if (_baseAssets.TryGetValue(selector, out EnbBaseAssetResolution? cached) && cached is not null)
            return cached;
        EnbBaseAssetResolution resolved = TryGetColdAsset(resolver,selector,out var decoded)?decoded:resolver.Resolve(selector);
        _baseAssets[selector] = resolved;
        return resolved;
    }

    private int AddRslRoots(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        EnbRetailAssetBundle retailAssets,
        RootTaskList roots,
        List<RslAnimationDiagnostic>? animationReceipts,
        List<LiveRslSoundRequest> soundRequests,
        uint currentClientTime,
        EnbRslObjectState? effectRslObjectUpdate,
        out string? diagnostic,
        out bool auditPending,bool currentObjectQuery=false,
        List<(LiveCpuRoot Root,LiveRslAnimationBlendBinding? Blend)>? evaluatedRoots=null)
    {
        auditPending = false;
        diagnostic = null;
        EnbResolvedRslLibraryResourceGraph? graph = effectRslObjectUpdate?.Graph ?? rsl.ResourceGraph;
        if (graph is null)
        {
            diagnostic = "RSL resource graph is unavailable";
            return 0;
        }

        var sourceOwner=_buildingSceneOwner is {} current&&current.Definition.RenderIdentity==obj.RenderIdentity?current:null;
        var structure=effectRslObjectUpdate?.RenderStructure;
        if(sourceOwner is null&&structure?.Matches(effectRslObjectUpdate!,graph)!=true)structure=null;
        if(currentObjectQuery)structure=structure?.CurrentObjectQuery;
        if(structure is null)_rslRenderStructureFallbacks++;else _rslRenderStructureUses++;
        IReadOnlyDictionary<int,int> runtimeLinks=ImmutableDictionary<int,int>.Empty;
        //002F arguments belong to the ordered class initializer in the retained
        //runtime. They must not redirect packet selectors or NEXT targets.

        var stateIds = structure is null?new List<int>():null;
        //The retained RSL object runtime publishes every scene RSL object's
        //state before roots are built; there is no snapshot-only evaluation.
        if (effectRslObjectUpdate is null)
        {
            diagnostic = "retained RSL runtime publishes no object state";
            return 0;
        }
        stateIds?.AddRange(effectRslObjectUpdate.RenderStateIds);
        IReadOnlyList<EnbCurrentRetailRslEffectiveAction> effectiveActions = effectRslObjectUpdate.Actions;

        IReadOnlyList<int> distinctStateIds=structure is null?stateIds!.Distinct().ToArray():structure.StateIds;
        if (distinctStateIds.Count == 0)
        {
            diagnostic =
                $"no active RSL state resolves in graph (active={obj.ActiveRenderStates.Length}, actions={obj.RenderStateActions.Length}, initial={rsl.InitialStateCommands.Count(static command => command.Kind == EnbRslRenderStateCommandKind.ActivateState)})";
            return 0;
        }

        int added = 0;
        int animationControlStates = 0;
        var discoveredAnimations=structure is null?new List<(int StateId,EnbResolvedRslStateResource State)>():null;
        IReadOnlyList<(int StateId,EnbResolvedRslStateResource State)> activeAnimationStates=structure?.Animations??discoveredAnimations!;
        var failures = new List<string>();
        IReadOnlyList<EnbCurrentRetailRslRenderBinding> renderBindings=structure?.Bindings??distinctStateIds
            .Select(id=>new EnbCurrentRetailRslRenderBinding(id,graph.TryFindState(id,out var resolved)?resolved:null)).ToArray();
        var retainedPlan=!_isolatedLegoNotification&&sourceOwner is not null&&structure is not null
            ?currentObjectQuery?sourceOwner.RslQueryRootPlan??=new RetainedRslRootPlan(renderBindings.Count)
                :sourceOwner.RslRootPlan??=new RetainedRslRootPlan(renderBindings.Count):null;
        bool Materialize(int slot,int stateId,EnbResolvedRslStateResource state,out LiveCpuRoot root,out string? error)
        {
            error=null;
            if(retainedPlan?.Slots[slot].Root is {} retained){root=retained;return true;}
            if(!TryMaterializeRslObjectRoot(obj.BaseAsset,stateId,state,retailAssets,out root,out error))return false;
            root=ResolveEffectRslRoot(obj,stateId,root);
            if(retainedPlan is not null)retainedPlan.Slots[slot].Root=root;
            return true;
        }
        LiveCpuRoot AttachBones(int slot,int stateId,LiveCpuRoot root)
        {
            if(retainedPlan is not null&&ReferenceEquals(retainedPlan.Slots[slot].Root,root))
                return retainedPlan.Slots[slot].WithBones??=WithRslBoneAttachments(obj,stateId,root);
            return WithRslBoneAttachments(obj,stateId,root);
        }
        bool AppendSelectedRoot(LiveCpuRoot selected,LiveRslAnimationBlendBinding? blend,out string? error)
        {
            if(evaluatedRoots is not null){evaluatedRoots.Add((selected,blend));error=null;return true;}
            return TryAppendRootInstanceTree(obj,selected,true,roots,blend,out error);
        }
        for(int bindingIndex=0;bindingIndex<renderBindings.Count;bindingIndex++)
        {
            var entry=renderBindings[bindingIndex];
            int stateId=entry.StateId;
            if (entry.State is not {} state)
            {
                failures.Add($"state {stateId}: graph entry missing");
                continue;
            }

            if (state.Definition.Type == EnbRslStateType.Animation)
            {
                // Current retail's animation-state activation reads the existing
                // RenderObj from its owner/context and derives the animation asset
                // from that RenderObj name. The animation state is not a new root.
                animationControlStates++;
                discoveredAnimations?.Add((stateId, state));
                continue;
            }
            if (state.Definition.Type == EnbRslStateType.Sound)
            {
                failures.Add(
                    $"{DescribeRslStateForDiagnostic(state)}: current-retail Sound state class is distinct from OBJECT, but its activation/root ownership is not yet proven; remains fail-closed");
                continue;
            }

            if (state.Definition.Type != EnbRslStateType.Object)
            {
                failures.Add($"{DescribeRslStateForDiagnostic(state)}: unsupported RSL state class remains fail-closed");
                continue;
            }

            if (Materialize(bindingIndex,stateId,state,out LiveCpuRoot root,out string? rootDiagnostic))
            {
                root=AttachBones(bindingIndex,stateId,root);
                if (AppendSelectedRoot(root,null,out string? aggregateDiagnostic))
                {
                    added++;
                }
                else
                {
                    failures.Add(aggregateDiagnostic ?? $"{DescribeRslStateForDiagnostic(state)}: aggregate attachment materialization failed");
                }
            }
            else
            {
                failures.Add(rootDiagnostic ?? $"{DescribeRslStateForDiagnostic(state)}: unknown OBJECT materialization rejection");
            }
        }

        // Active Animation states operate on the retained current RenderObj. Current
        // retail gathers every active HAnim in active-list order and submits one
        // normalized aggregate to that RenderObj. Packet-authored active-state
        // membership may be a set in snapshots, so retain the actual tail order
        // uninterrupted activation intervals before binding the HAnims.
        if (added == 0 && failures.Count == 0 && animationControlStates > 0)
        {
            string? retainedFailure = null;
            int retainedObjectStateId = -1;
            EnbResolvedRslStateResource? retainedObjectState = null;
            if (effectRslObjectUpdate?.CurrentObjectStateId is int runtimeObjectStateId)
            {
                if (graph.TryFindState(runtimeObjectStateId, out EnbResolvedRslStateResource runtimeObjectState) &&
                    runtimeObjectState.Definition.Type == EnbRslStateType.Object)
                {
                    retainedObjectStateId = runtimeObjectStateId;
                    retainedObjectState = runtimeObjectState;
                }
                else
                {
                    retainedFailure = $"persistent R6Z33 current Object state {runtimeObjectStateId} is unavailable or not OBJECT";
                }
            }
            else if (!TryFindInitialRslObjectState(
                         rsl,
                         out retainedObjectStateId,
                         out EnbResolvedRslStateResource initialObjectState,
                         out retainedFailure))
            {
                // retainedFailure already describes the initial-state boundary.
            }
            else
            {
                retainedObjectState = initialObjectState;
            }

            if (retainedObjectState is null)
            {
                // retainedFailure already describes the current-Object boundary.
            }
            else if (!Materialize(renderBindings.Count,retainedObjectStateId,retainedObjectState,
                         out LiveCpuRoot retainedRoot,out retainedFailure))
            {
                // retainedFailure already describes the OBJECT materialization boundary.
            }
            else
            {
                IReadOnlyList<(int StateId, EnbResolvedRslStateResource State)> orderedAnimations =
                    effectRslObjectUpdate?.ActiveList.Snapshot().Count > 0
                    ? structure?.OrderedAnimations??effectRslObjectUpdate.ActiveList.Snapshot()
                        .Join(activeAnimationStates, id => id, candidate => candidate.StateId,
                            (id, candidate) => (candidate.StateId, candidate.State)).ToArray()
                    : OrderCurrentRetailActiveAnimationStates(rsl, effectiveActions, activeAnimationStates);
                var bindings = retainedPlan is null?new List<LiveRslAnimationBinding>(orderedAnimations.Count):null;
                retainedPlan?.PrepareAnimations(orderedAnimations.Count);
                LiveRslVisualOverride? soleVisualOverride = null;
                bool failed = false;

                for(int animationIndex=0;animationIndex<orderedAnimations.Count;animationIndex++)
                {
                    var (stateId,state)=orderedAnimations[animationIndex];
                    //0045AEA9..0045B015 builds PIVOTMAP entries but never
                    //publishes their controller into the copied blend record.

                    if (!TryResolveRslAnimationBinding(
                            obj,
                            runtimeLinks,
                            effectiveActions,
                            graph,
                            stateId,
                            state,
                            retainedObjectStateId,
                            retainedRoot,
                            retailAssets,
                            currentClientTime,
                            out LiveRslAnimationBinding? animationBinding,
                            out RslAnimationEvaluationOwner animationReceipt,
                            out retainedFailure,retainedPlan?.AnimationSlots[animationIndex],retainedPlan?.AnimationResults[animationIndex]))
                    {
                        animationReceipts?.Add(animationReceipt.CaptureDiagnostic());
                        auditPending = animationReceipt.AuditPending && failures.Count == 0;
                        failed = true;
                        break;
                    }

                    if (animationBinding is not null && effectRslObjectUpdate is not null)
                    {
                        float weight = effectRslObjectUpdate.TryGetAnimationWeight(animationBinding.StateId, out float bindingWeight)
                            ? bindingWeight
                            : effectRslObjectUpdate.TryGetAnimationWeight(stateId, out float sourceWeight)
                                ? sourceWeight
                                : 1f;
                        animationBinding.Weight = weight;
                    }

                    animationReceipts?.Add(animationReceipt.CaptureDiagnostic());
                    if (animationReceipt.LinkedSounds.Count != 0)
                        soundRequests.AddRange(animationReceipt.LinkedSounds);
                    if (animationReceipt.VisualOverride is LiveRslVisualOverride visualOverride)
                    {
                        if (orderedAnimations.Count != 1)
                        {
                            retainedFailure =
                                "multiple active Animation states also carry linked visual replacement/animation chains; complete compound visual-chain ownership is not represented by the current persistent RSL model, so this case remains fail-closed.";
                            failed = true;
                            break;
                        }
                        soleVisualOverride = visualOverride;
                    }
                    else if (animationBinding is not null)
                    {
                        // A certified NULL HAnim retains the root but contributes
                        // no animation sample to the blend.
                        bindings?.Add(animationBinding);
                    }
                    retainedPlan?.SetAnimation(animationIndex,animationBinding);
                }

                if (!failed)
                {
                    LiveCpuRoot finalRoot = soleVisualOverride?.Root ?? retainedRoot;
                    LiveRslAnimationBlendBinding blend = soleVisualOverride?.AnimationBlend ??
                        (retainedPlan?.ReadBlend(finalRoot)??CreateRslAnimationBlend(finalRoot, bindings!));
                    finalRoot=AttachBones(renderBindings.Count,retainedObjectStateId,finalRoot);
                    if (!AppendSelectedRoot(finalRoot,blend,out retainedFailure))
                    {
                        failed = true;
                    }
                }


                if (!failed)
                {
                    added = 1;
                    retainedFailure = null;
                }
            }

            if (added == 0)
            {
                failures.Add(
                    $"{animationControlStates} active Animation state(s) require an existing RenderObj, but the retained initial OBJECT root could not be materialized/blended: " +
                    (retainedFailure ?? "unknown retained-animation rejection"));
            }
        }

        if (added == 0)
        {
            diagnostic = failures.Count == 0
                ? $"{distinctStateIds.Count} active RSL state(s) resolved but produced no W3D root"
                : string.Join(" | ", failures.Distinct(StringComparer.Ordinal));
        }

        return added;
    }

    private static IReadOnlyList<(int StateId, EnbResolvedRslStateResource State)> OrderCurrentRetailActiveAnimationStates(
        EnbResolvedRslReference rsl,
        IReadOnlyList<EnbCurrentRetailRslEffectiveAction> effectiveActions,
        IReadOnlyList<(int StateId, EnbResolvedRslStateResource State)> activeAnimations)
    {
        var initialLinks = new Dictionary<int, int>();
        foreach (EnbRslRenderStateCommand command in rsl.InitialStateCommands)
        {
            if (command.Kind == EnbRslRenderStateCommandKind.InitializeState &&
                command.ArgumentStateId is int target &&
                (rsl.ResourceGraph is not {} initialGraph || !initialGraph.TryFindState(command.StateId,out var initialized) ||
                 !EnbRslRenderStateSemantics.IsNoOpInitializer(initialized.Definition.Type)))
            {
                initialLinks[command.StateId] = target;
            }
        }

        var initialActivationOrdinals = new Dictionary<int, int>();
        int initialOrdinal = 0;
        foreach (EnbRslRenderStateCommand command in rsl.InitialStateCommands)
        {
            if (command.Kind != EnbRslRenderStateCommandKind.ActivateState)
                continue;

            int resolved = ResolveRslStateLink(command.StateId, initialLinks);
            initialActivationOrdinals[resolved] = initialOrdinal++;
        }

        return activeAnimations
            .Select(candidate =>
            {
                bool active = false;
                int packetActivationOrdinal = -1;
                uint packetActivationClientTime = 0;
                for (int actionIndex = 0; actionIndex < effectiveActions.Count; actionIndex++)
                {
                    EnbCurrentRetailRslEffectiveAction action = effectiveActions[actionIndex];
                    if (action.StateId != candidate.StateId)
                        continue;

                    switch (action.Opcode)
                    {
                        case 0x0030:
                            if (!active)
                            {
                                active = true;
                                packetActivationOrdinal = actionIndex;
                                packetActivationClientTime = action.ClientTime;
                            }
                            break;
                        case 0x0032:
                            active = false;
                            packetActivationOrdinal = -1;
                            packetActivationClientTime = 0;
                            break;
                    }
                }

                int initial = initialActivationOrdinals.TryGetValue(candidate.StateId, out int authoredOrdinal)
                    ? authoredOrdinal
                    : -1;
                return new
                {
                    candidate.StateId,
                    candidate.State,
                    HasPacketActivation = active && packetActivationOrdinal >= 0,
                    PacketActivationOrdinal = active ? packetActivationOrdinal : -1,
                    PacketActivationClientTime = active ? packetActivationClientTime : 0,
                    InitialActivationOrdinal = initial,
                };
            })
            // A successful activation appends at the active-list tail. Packet
            // action order is stronger than client-time alone because several
            // state activations may share one current-client timestamp.
            .OrderBy(static candidate => candidate.HasPacketActivation)
            .ThenBy(static candidate => candidate.PacketActivationOrdinal)
            .ThenBy(static candidate => candidate.PacketActivationClientTime)
            .ThenBy(static candidate => candidate.InitialActivationOrdinal)
            .ThenBy(static candidate => candidate.StateId)
            .Select(static candidate => (candidate.StateId, candidate.State))
            .ToArray();
    }

    private EnbW3dRenderHierarchyAnimation? ResolveRetainedRslAnimationClip(
        IEnbRenderObject obj, EnbResolvedRslReference rsl, EnbRetailAssetBundle assets,
        int animationId, int objectId)
    {
        if (rsl.ResourceGraph is not { } graph || !graph.TryFindState(animationId, out var state) ||
            !graph.TryFindState(objectId, out var body) ||
            !TryMaterializeRslObjectRoot(obj.BaseAsset, objectId, body, assets, out var root, out _) ||
            root.RuntimeHAnimHierarchyIdentity is null) return null;
        string name = EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(root.RuntimeHAnimHierarchyIdentity,
            state.AnimationName, state.AnimationSuffix);
        return _hAnimRegistry.TryResolve(root.Resource, state.FileDependency, assets.Paths,
            assets.MixResources, assets.RetailMixResources, name, out var clip, out _)
            ? clip.Animation : null;
    }

    private bool TryResolveRslAnimationBinding(
        IEnbRenderObject obj,
        IReadOnlyDictionary<int, int> runtimeLinks,
        IReadOnlyList<EnbCurrentRetailRslEffectiveAction> effectiveActions,
        EnbResolvedRslLibraryResourceGraph graph,
        int stateId,
        EnbResolvedRslStateResource state,
        int currentObjectStateId,
        LiveCpuRoot retainedRoot,
        EnbRetailAssetBundle retailAssets,
        uint currentClientTime,
        out LiveRslAnimationBinding? binding,
        out RslAnimationEvaluationOwner receipt,
        out string? diagnostic,LiveRslAnimationBinding? retainedBinding=null, RslAnimationEvaluationOwner? result=null)
    {
        result ??= new();
        binding = null;
        diagnostic = null;

        // 00459D50 rebinds on the owning model/hierarchy change. The retained
        // root plan is invalidated by RSL membership and asset adoption, so a
        // resolved binding reads its clip directly between those mutations.
        var retainedResolution=retainedBinding is not null && ReferenceEquals(retainedBinding.LookupRoot,retainedRoot) &&
            ReferenceEquals(retainedBinding.LookupFile,state.FileDependency) &&
            retainedBinding.Resolution is {RequiresAcquisitionMaintenance:false} selected && selected.RegistryGeneration==_hAnimRegistry.Generation
            ?selected:null;
        bool retainedNull=result.HasNullBinding(retainedRoot,state,_hAnimRegistry.Generation,_hAnimRegistry.RegistrationCount);
        string? runtimeHAnimHierarchyIdentity = retainedRoot.RuntimeHAnimHierarchyIdentity;
        string hAnimName;
        try
        {
            hAnimName = (retainedNull?result.NullName:null) ?? retainedResolution?.HAnimName ?? EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(
                runtimeHAnimHierarchyIdentity!,
                state.AnimationName,
                state.AnimationSuffix);
        }
        catch (ArgumentException ex)
        {
            diagnostic =
                $"{DescribeRslStateForDiagnostic(state)}: retained RenderObj virtual +0xCC exposes no usable hierarchy identity for current-retail HAnim construction: {ex.Message}";
            receipt = UpdateRslAnimationResult(result,
                obj,
                stateId,
                state,
                hAnimName: "<unresolved>",
                source: "-",
                activationClientTime: null,
                currentClientTime,
                frameRate: null,
                numFrames: null,
                frame: null,
                sampled: false,
                diagnostic);
            return false;
        }

        //The retained RSL object runtime owns activation, NEXT and LINK
        //execution; every scene RSL object publishes its state before roots
        //are built, so its Animation frame is the only frame source.
        if (!TryGetFrameRslUpdate(obj, out var retainedUpdate) ||
            !retainedUpdate.TryGetAnimationFrame(stateId, out var retainedFrame))
        {
            diagnostic = $"{DescribeRslStateForDiagnostic(state)}: retained RSL runtime publishes no Animation frame.";
            receipt = UpdateRslAnimationResult(result,
                obj,
                stateId,
                state,
                hAnimName,
                source: "-",
                activationClientTime: null,
                currentClientTime,
                frameRate: null,
                numFrames: null,
                frame: null,
                sampled: false,
                diagnostic);
            return false;
        }
        var interval = new EnbRslAnimationActivationInterval(retainedFrame.ActivationTick, null);

        // 00459D50 stores the resolved NULL on this RenderObj; 00459B10 does
        // not rerun the loader on each frame. Activation timing remains live.
        if(retainedNull)
        {
            receipt=UpdateRslAnimationResult(result,obj,stateId,state,hAnimName,"NULL:Get_HAnim",
                interval.ActivationClientTime,currentClientTime,null,null,null,false,result.NullDiagnostic,
                interval.PacketActivateNextOnceClientTime,staticNull:true);
            return true;
        }
        EnbCurrentRetailRslHAnimResolution hAnim=retainedResolution!;
        string? hAnimDiagnostic=null;
        if (retainedResolution is null && !_hAnimRegistry.TryResolve(
                retainedRoot.Resource,
                state.FileDependency,
                retailAssets.Paths,
                retailAssets.MixResources,
                retailAssets.RetailMixResources,
                hAnimName,
                out hAnim,
                out hAnimDiagnostic))
        {
            if (_hAnimRegistry.TryProbeLazyHeader(
                    retailAssets.Paths,
                    retailAssets.MixResources,
                    retailAssets.RetailMixResources,
                    hAnimName,
                    out EnbCurrentRetailRslHAnimHeaderResolution headerResolution,
                    out string? headerDiagnostic))
            {
                EnbW3dHAnimHeaderInfo header = headerResolution.Header;
                string flavor = header.CompressionFlavor?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                diagnostic =
                    $"{DescribeRslStateForDiagnostic(state)}: {hAnimDiagnostic ?? $"current-retail HAnim '{hAnimName}' is not executable at the established classic boundary."} " +
                    $"R6Z20 hierarchy-identity correction resolves the retail lazy source to {headerResolution.Source}, which contains registry key '{header.RegistryName}' as family 0x{(uint)header.Family:X4}, frames {header.NumFrames}, rate {header.FrameRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}, flavor {flavor}. Header identity is proven, but full W3D/channel execution remains fail-closed at this evidence-only boundary.";
                receipt = UpdateRslAnimationResult(result,
                    obj,
                    stateId,
                    state,
                    hAnimName,
                    headerResolution.Source,
                    interval.ActivationClientTime,
                    currentClientTime,
                    header.FrameRate,
                    header.NumFrames,
                    frame: null,
                    sampled: false,
                    diagnostic,
                    interval.PacketActivateNextOnceClientTime);
                return false;
            }

            string familyCensus = DescribeRetainedHAnimFamilyCensus(
                retainedRoot.Resource,
                hAnimName,
                out EnbW3dHAnimHeaderInfo? uniqueHeaderCandidate);
            if (uniqueHeaderCandidate is null)
            {
                // Get_HAnim found nothing in the registry or through the lazy source:
                // retail binds the NULL HAnim at once (no corpus search).
                string nullDiagnostic =
                    $"{DescribeRslStateForDiagnostic(state)}: {hAnimDiagnostic ?? $"current-retail HAnim '{hAnimName}' could not be resolved."} {familyCensus} " +
                    $"Current retail FUN_00459D50 stores the NULL HAnim on the existing RenderObj state, and FUN_00459B10 returns without advancing frames or firing end transitions while that pointer is NULL; retain the RenderObj statically.";
                // Pending acquisition must remain live until completion.
                if(!_hAnimRegistry.HasPendingAcquisition(retainedRoot.Resource))
                    result.BindNull(retainedRoot,state,_hAnimRegistry.Generation,_hAnimRegistry.RegistrationCount,hAnimName,nullDiagnostic);
                receipt = UpdateRslAnimationResult(result,
                    obj,
                    stateId,
                    state,
                    hAnimName,
                    source: "NULL:Get_HAnim",
                    interval.ActivationClientTime,
                    currentClientTime,
                    frameRate: null,
                    numFrames: null,
                    frame: null,
                    sampled: false,
                    nullDiagnostic,
                    interval.PacketActivateNextOnceClientTime,
                    staticNull: true);
                diagnostic = null;
                return true;
            }

            string boundary = uniqueHeaderCandidate.Family == EnbW3dHAnimFamily.Classic0200
                ? " The matching classic header is not a decoded executable R4D animation, so R6Z19 does not infer channel execution from the header alone."
                : $" The matching 0x{(uint)uniqueHeaderCandidate.Family:X4} header is a current-retail HAnim candidate, but R6Z19 intentionally does not execute that family's channels from header evidence alone.";
            diagnostic =
                $"{DescribeRslStateForDiagnostic(state)}: {hAnimDiagnostic ?? $"current-retail HAnim '{hAnimName}' could not be resolved."} {familyCensus}{boundary}";
            receipt = UpdateRslAnimationResult(result,
                obj,
                stateId,
                state,
                hAnimName,
                source: $"retained:{retainedRoot.Resource.LogicalFileName}#0x{(uint)uniqueHeaderCandidate.Family:X4}-header",
                interval.ActivationClientTime,
                currentClientTime,
                frameRate: uniqueHeaderCandidate.FrameRate,
                numFrames: uniqueHeaderCandidate.NumFrames,
                frame: null,
                sampled: false,
                diagnostic,
                interval.PacketActivateNextOnceClientTime);
            return false;
        }

        if(retainedResolution is null)_effectRslObjectRuntime.RecordBoundAnimation(obj, stateId, hAnim.Animation);
        float frame = retainedFrame.Frame;

        if(retainedBinding is not null && retainedBinding.StateId==stateId &&
            retainedBinding.HAnimName==hAnimName && retainedBinding.Source==hAnim.Source &&
            retainedBinding.ActivationClientTime==interval.ActivationClientTime &&
            ReferenceEquals(retainedBinding.Animation,hAnim.Animation) && ReferenceEquals(retainedBinding.DrawResource,hAnim.DrawResource))
        {binding=retainedBinding;binding.Sample=new(hAnim.Animation.Name,frame);}
        else binding = new LiveRslAnimationBinding(stateId,hAnimName,hAnim.Source,interval.ActivationClientTime,
            hAnim.Animation,new(hAnim.Animation.Name,frame),hAnim.DrawResource) {LookupRoot=retainedRoot,LookupFile=state.FileDependency,Resolution=hAnim};
        receipt = UpdateRslAnimationResult(result,
            obj,
            stateId,
            state,
            binding,
            currentClientTime,
            sampled: true,
            diagnostic: null,
            packetActivateNextOnceClientTime: interval.PacketActivateNextOnceClientTime);
        receipt.LinkedSounds=[];receipt.VisualOverride=null;
        return true;
    }

    private static string DescribeRslLinkActivationPlan(EnbCurrentRetailRslLinkActivationPlan activation)
    {
        const int diagnosticStepLimit = 8;
        string chain = activation.Steps.Count == 0
            ? "none"
            : string.Join(
                " -> ",
                activation.Steps
                    .Take(diagnosticStepLimit)
                    .Select(static step => $"{step.StateId}:{step.StateType}"));
        if (activation.Steps.Count > diagnosticStepLimit)
            chain += $" -> ... ({activation.Steps.Count} activations)";

        string termination = activation.Termination switch
        {
            EnbCurrentRetailRslLinkActivationTermination.NoFurtherLink => "end",
            EnbCurrentRetailRslLinkActivationTermination.MissingStartingTarget =>
                $"missing start {activation.UnresolvedOrUnfollowedLinkStateId}",
            EnbCurrentRetailRslLinkActivationTermination.MissingLinkedTarget =>
                $"missing LINK {activation.UnresolvedOrUnfollowedLinkStateId}",
            EnbCurrentRetailRslLinkActivationTermination.NegativeInitialBudget => "negative initial budget",
            EnbCurrentRetailRslLinkActivationTermination.LinkBudgetExhausted =>
                $"budget exhausted before LINK {activation.UnresolvedOrUnfollowedLinkStateId}",
            _ => activation.Termination.ToString(),
        };

        string preActivation = activation.FirstTargetPreActivationSeconds is double seconds
            ? $"+0x34({seconds.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)}) first target · "
            : string.Empty;
        return
            $"start {activation.StartingStateId} · {preActivation}budget {activation.InitialLinkBudget} · " +
            $"chain [{chain}] · {termination}";
    }

    private string DescribeR6Z30SoundActivationResolution(
        EnbRetailAssetBundle retailAssets,
        EnbResolvedRslLibraryResourceGraph graph,
        IEnbRenderObject obj,
        int linkedStateId,
        EnbResolvedRslStateResource linkedState,
        uint activationClientTime,
        out LiveRslSoundRequest? request, bool describe = true)
    {
        request = null;
        string? logicalSoundName = linkedState.Definition.Sound;
        if (string.IsNullOrWhiteSpace(logicalSoundName))
            return !describe ? string.Empty : $"Sound state {linkedStateId} <unnamed>; authored SOUND token is absent; R6Z30 audio blocked";

        EnbRetailSoundCatalog? sounds = retailAssets.Sounds;
        if (sounds is null)
            return !describe ? string.Empty : $"Sound state {linkedStateId} '{logicalSoundName}'; selected install sounds.ini is unavailable; R6Z30 audio blocked";

        EnbRetailSoundDefinition definition=sounds.GetDefinitionOrFile(logicalSoundName);
        RslSoundSelection selection;
        try { selection=SelectRslSound(obj,linkedStateId,definition); }
        catch(Exception ex) when(ex is ArgumentException or OverflowException or InvalidOperationException)
        { return !describe ? string.Empty : $"Sound state {linkedStateId} '{logicalSoundName}'; audio blocked: {ex.Message}"; }
        if(selection.Definition is null)return !describe ? string.Empty : $"Sound state {linkedStateId} '{logicalSoundName}'; authored silent choice";
        definition=selection.Definition;

        // The selected handle owns its resource. Per-frame updates move/check
        // that handle (45FC50), rather than resolving its file again (45FB40).
        EnbRetailSoundResolution resolution = selection.Resolve(retailAssets);

        EnbCurrentRetailRslSoundContract soundContract =
            EnbCurrentRetailRslSoundRuntime.Describe(linkedState.Definition.Flags);
        EnbCurrentRetailRslSoundCompletionContract completionContract =
            EnbCurrentRetailRslSoundCompletionRuntime.Describe(linkedState.Definition, soundContract);
        string summary = string.Empty;
        if (describe)
        {
        string resources = resolution.Choices.Count == 0
            ? "no authored SoundFilename/\\Files entries"
            : string.Join(
                ", ",
                resolution.Choices.Select(choice => choice.Status switch
                {
                    EnbRetailSoundResourceStatus.NullChoice => $"NULL{WeightSuffix(choice.Choice.Weight)}",
                    EnbRetailSoundResourceStatus.LooseFile =>
                        $"{choice.Choice.LogicalResourceName}{WeightSuffix(choice.Choice.Weight)}@loose:{choice.LoosePath}",
                    EnbRetailSoundResourceStatus.MixResource =>
                        $"{choice.Choice.LogicalResourceName}{WeightSuffix(choice.Choice.Weight)}@MIX:{choice.MixLocation!.ArchiveName}/{choice.MixLocation.Name}",
                    _ => $"{choice.Choice.LogicalResourceName}{WeightSuffix(choice.Choice.Weight)}@missing",
                }));

        static string WeightSuffix(int? weight)
            => weight is int authoredWeight ? $"×{authoredWeight}" : string.Empty;

        string soundType = definition.UsesRetailFallbackForSoundType
            ? $"{definition.EffectiveSoundType} retail-default"
            : definition.EffectiveSoundType.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string completionShape = completionContract.FiniteOnePass
            ? completionContract.EntersGenericTransitionOnCompletion
                ? completionContract.CompletionLinkStateId is int completionLink
                    ? $"one-pass -> transition LINK {completionLink}"
                    : "one-pass -> transition/no LINK -> deactivate"
                : "one-pass -> deactivate"
            : "continuous";
        string ignoredNext = completionContract.AuthoredNextIgnoredBySoundFactory
            ? $"; authored NEXT {linkedState.Definition.NextStateId} ignored by current Sound factory"
            : string.Empty;

        summary =
            $"Sound state {linkedStateId} '{logicalSoundName}' -> sounds.ini [{definition.Name}] " +
            $"files {resources} · loop {definition.EffectiveLoopCount} type {soundType} " +
            $"volume {definition.EffectiveVolume.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"priority {definition.EffectivePriority.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"radii max/drop {definition.EffectiveMaxVolumeRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)}/" +
            $"{definition.EffectiveDropOffRadius.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"resource {(resolution.IsComplete ? "resolved" : "incomplete")} · " +
            $"Sound prototype 0x{soundContract.PrototypeFlags:X8} -> instance 0x{soundContract.InstanceFlags:X8} " +
            $"(0x8000 stripped; handle {(soundContract.InstanceHandleEligible ? "eligible" : "blocked")}; " +
            $"RSL loopctl {soundContract.HandleLoopCountOverride}; completion {completionShape}{ignoredNext}; " +
            $"cleanup {(soundContract.StopsHandleOnCleanup ? "stop" : "keep")}; " +
            $"spatial {(soundContract.FollowsLiveObjectTransform ? "follow" : "stationary")})";

        }

        if (!EnbCurrentRetailRsl3dSoundRuntime.TryCreatePlan(
                definition,
                resolution,
                soundContract,
                out EnbCurrentRetailRsl3dSoundPlan plan,
                out EnbRetailSoundResolvedChoice? selectedChoice,
                out string? planDiagnostic) ||
            selectedChoice is null)
        {
            return !describe ? string.Empty : $"{summary} · R6Z30 audio blocked: {planDiagnostic ?? "no exact current-retail 3D submission plan"}";
        }

        var key = RslSoundKey(obj,linkedStateId,activationClientTime);
        if (_rslSoundCompletions.TryGetValue(key, out LiveRslSoundCompletionEvent completed))
        {
            if (!completionContract.FiniteOnePass)
                return !describe ? string.Empty : $"{summary} · R6Z30 contradiction: continuous Sound produced a completion event at {completed.ObservedClientTime}";

            if (completionContract.CompletionLinkStateId is int completedLink)
            {
                EnbCurrentRetailRslLinkActivationPlan activation =
                    EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, completedLink, firstTargetPreActivationSeconds: 0.0);
                return !describe ? string.Empty :
                    $"{summary} · completed @{completed.ObservedClientTime}; current retail generic LINK activation " +
                    DescribeRslLinkActivationPlan(activation) +
                    "; R6Z30 records the finite-Sound completion LINK chain; its asynchronous follow-on class execution remains a later local-transition boundary.";
            }

            return !describe ? string.Empty : $"{summary} · completed @{completed.ObservedClientTime}; current Sound deactivated and is not restarted";
        }

        Vector3? worldPosition = obj.Spatial?.Transform?.Position;
        if (!worldPosition.HasValue ||
            !float.IsFinite(worldPosition.Value.X) ||
            !float.IsFinite(worldPosition.Value.Y) ||
            !float.IsFinite(worldPosition.Value.Z))
        {
            return !describe ? string.Empty : $"{summary} · R6Z30 audio blocked: live object world position is unavailable/non-finite";
        }

        request = new LiveRslSoundRequest(
            key,
            logicalSoundName,
            selectedChoice,
            plan,
            worldPosition.Value) { HandleId=selection.HandleId };

        return !describe ? string.Empty :
            $"{summary} · R6Z30 3D plan ready gain {plan.Gain.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"pitch {plan.Pitch.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"ref/max {plan.ReferenceDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}/" +
            $"{plan.MaximumDistance.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"loop {(plan.LoopContinuously ? "continuous" : "one-pass")} " +
            $"spatial {(plan.FollowsLiveObjectTransform ? "follow" : "stationary")}";
    }


    private string DescribeRetainedHAnimFamilyCensus(
        EnbW3dRenderResource resource,
        string requestedHAnimName,
        out EnbW3dHAnimHeaderInfo? uniqueHeaderCandidate)
    {
        LiveHAnimFamilyCensus census = GetHAnimFamilyCensus(resource);
        if (census.Descriptions.TryGetValue(requestedHAnimName, out var saved))
        {
            uniqueHeaderCandidate = saved.Header;
            return saved.Text;
        }
        string text = DescribeRetainedHAnimFamilyCensusCore(resource, requestedHAnimName, census, out uniqueHeaderCandidate);
        census.Descriptions.Add(requestedHAnimName, (text, uniqueHeaderCandidate));
        return text;
    }

    private static string DescribeRetainedHAnimFamilyCensusCore(
        EnbW3dRenderResource resource, string requestedHAnimName, LiveHAnimFamilyCensus census,
        out EnbW3dHAnimHeaderInfo? uniqueHeaderCandidate)
    {
        uniqueHeaderCandidate = null;
        IReadOnlyList<EnbW3dHAnimHeaderInfo>? cachedHeaders = census.Headers;
        if (cachedHeaders is null)
        {
            return $"Retained resource '{resource.LogicalFileName}' raw HAnim family census unavailable: {census.Diagnostic ?? "resource bytes could not be inspected"}.";
        }

        IReadOnlyList<EnbW3dHAnimHeaderInfo> headers = cachedHeaders;
        EnbW3dHAnimHeaderInfo[] matches = headers
            .Where(header => string.Equals(header.RegistryName, requestedHAnimName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 1)
            uniqueHeaderCandidate = matches[0];

        int classic = headers.Count(static header => header.Family == EnbW3dHAnimFamily.Classic0200);
        int compressed = headers.Count(static header => header.Family == EnbW3dHAnimFamily.Compressed0280);
        int adaptive = headers.Count(static header => header.Family == EnbW3dHAnimFamily.Adaptive02C0);
        string matchDetail = matches.Length == 0
            ? $"no raw HAnim header candidate matches '{requestedHAnimName}'"
            : matches.Length == 1
                ? DescribeHAnimHeaderCandidate(matches[0])
                : $"{matches.Length} raw HAnim header candidates match '{requestedHAnimName}' [{string.Join(", ", matches.Take(8).Select(DescribeHAnimHeaderCandidate))}]";
        string boundedHeaders = headers.Count == 0
            ? "none"
            : string.Join(
                ", ",
                headers
                    .Take(8)
                    .Select(static header => $"0x{(uint)header.Family:X4}:{header.RegistryName}"));
        string tail = headers.Count > 8 ? $", +{headers.Count - 8} more" : string.Empty;
        return
            $"Retained resource '{resource.LogicalFileName}' raw HAnim headers classic/compressed/adaptive {classic}/{compressed}/{adaptive}; {matchDetail}; census [{boundedHeaders}{tail}]. Header presence is census evidence only, not proof that current retail completed hierarchy/channel loading and registry insertion.";
    }

    private static string DescribeHAnimHeaderCandidate(EnbW3dHAnimHeaderInfo header)
    {
        string flavor = header.CompressionFlavor is ushort value
            ? $" flavor {value}"
            : string.Empty;
        return
            $"0x{(uint)header.Family:X4} '{header.RegistryName}' raw '{header.Name}' hierarchy '{header.HierarchyName}' frames {header.NumFrames} rate {header.FrameRate.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{flavor} @0x{header.TopLevelOffset:X}";
    }

    private LiveHAnimFamilyCensus GetHAnimFamilyCensus(EnbW3dRenderResource resource)
    {
        if (_hAnimFamilyCensuses.TryGetValue(resource.Key, out LiveHAnimFamilyCensus? cached))
            return cached;

        LiveHAnimFamilyCensus census;
        if (!TryReadCompiledResourceBytes(resource, out byte[] bytes, out string? readDiagnostic))
        {
            census = new LiveHAnimFamilyCensus(null, readDiagnostic ?? "resource bytes could not be read");
        }
        else if (!EnbW3dHAnimHeaderProbe.TryInspect(bytes, out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers, out string? probeDiagnostic))
        {
            census = new LiveHAnimFamilyCensus(null, probeDiagnostic ?? "invalid W3D topology");
        }
        else
        {
            census = new LiveHAnimFamilyCensus(headers, null);
        }

        _hAnimFamilyCensuses[resource.Key] = census;
        return census;
    }

    private static bool TryReadCompiledResourceBytes(
        EnbW3dRenderResource resource,
        out byte[] bytes,
        out string? diagnostic)
    {
        bytes = [];
        diagnostic = null;
        EnbW3dRenderResourceKey key = resource.Key;
        if (string.IsNullOrWhiteSpace(key.ArchivePath))
        {
            diagnostic = "compiled resource has no source path";
            return false;
        }
        if (key.DataOffset < 0 || key.DataLength <= 0 || key.DataLength > int.MaxValue)
        {
            diagnostic = $"compiled resource source range {key.DataOffset}+{key.DataLength} is invalid";
            return false;
        }

        try
        {
            using var stream = new FileStream(
                EnbNext.Installation.EnbPathCase.Resolve(key.ArchivePath),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (key.DataOffset > stream.Length - key.DataLength)
            {
                diagnostic = $"compiled resource source range {key.DataOffset}+{key.DataLength} exceeds source length {stream.Length}";
                return false;
            }

            stream.Position = key.DataOffset;
            bytes = new byte[(int)key.DataLength];
            stream.ReadExactly(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"{ex.GetType().Name}: {ex.Message}";
            bytes = [];
            return false;
        }
    }

    private static RslAnimationEvaluationOwner UpdateRslAnimationResult(
        RslAnimationEvaluationOwner result, IEnbRenderObject obj,
        int stateId,
        EnbResolvedRslStateResource state,
        LiveRslAnimationBinding binding,
        uint currentClientTime,
        bool sampled,
        string? diagnostic,
        uint? packetActivateNextOnceClientTime = null,
        LiveRslAnimationTransitionKind? transitionKind = null,
        int? transitionFromStateId = null,
        uint? transitionClientTime = null)
        => UpdateRslAnimationResult(
            result, obj,
            stateId,
            state,
            binding.HAnimName,
            binding.Source,
            binding.ActivationClientTime,
            currentClientTime,
            binding.Animation.PlaybackFrameRate,
            binding.Animation.NumFrames,
            binding.Sample.Frame,
            sampled,
            diagnostic,
            packetActivateNextOnceClientTime,
            staticNull: false,
            transitionKind: transitionKind,
            transitionFromStateId: transitionFromStateId,
            transitionClientTime: transitionClientTime);

    private static RslAnimationEvaluationOwner UpdateRslAnimationResult(
        RslAnimationEvaluationOwner result, IEnbRenderObject obj,
        int stateId,
        EnbResolvedRslStateResource state,
        string hAnimName,
        string source,
        uint? activationClientTime,
        uint currentClientTime,
        float? frameRate,
        uint? numFrames,
        float? frame,
        bool sampled,
        string? diagnostic,
        uint? packetActivateNextOnceClientTime = null,
        bool staticNull = false,
        LiveRslAnimationTransitionKind? transitionKind = null,
        int? transitionFromStateId = null,
        uint? transitionClientTime = null)
    {
        result.BaseAsset=obj.BaseAsset;
        result.GameId=obj.GameId;
        result.StateId=stateId;
        result.HAnimName=hAnimName;
        result.Source=source;
        result.ActivationClientTime=activationClientTime;
        result.CurrentClientTime=currentClientTime;
        result.AuthoredFlags=EnbCurrentRetailRslAnimationRuntime.EncodeAuthoredFlags(state.Definition.Flags);
        result.FrameRate=frameRate;
        result.NumFrames=numFrames;
        result.Frame=frame;
        result.PacketActivateNextOnceClientTime=packetActivateNextOnceClientTime;
        result.TransitionKind=transitionKind ?? (transitionFromStateId.HasValue ? LiveRslAnimationTransitionKind.PacketNextOnce : null);
        result.TransitionFromStateId=transitionFromStateId;
        result.TransitionClientTime=transitionClientTime;
        result.Sampled=sampled;
        result.StaticNull=staticNull;
        result.Diagnostic=diagnostic;
        result.AuditPending=false;result.LinkedSounds=[];result.VisualOverride=null;
        return result;
    }

    private bool TryMaterializeRslObjectRoot(
        int baseAsset,
        int stateId,
        EnbResolvedRslStateResource state,
        EnbRetailAssetBundle retailAssets,
        out LiveCpuRoot root,
        out string? diagnostic)
    {
        // This cache owns immutable prototypes, NOT live object instances or
        // animation poses. Consult it before OBJECT's lazy loader: the later
        // CPU-root cache was too late to prevent archive reads/W3D decoding.
        var key = state;
        if (!_rslObjectPrototypes.TryGetValue(key, out var cached))
        {
            bool ready = TryMaterializeRslObjectPrototype(baseAsset, stateId, state,
                retailAssets, out var prototype, out diagnostic);
            if(ready)
            {
                try
                {
                    prototype=RetainRslParticleTemplate(state,prototype);
                    RemoveRslTemplatePrototype(state);
                }
                catch(InvalidDataException error)
                {ready=false;diagnostic=$"RSL particle template: {error.Message}";}
            }
            //45EF80 uses the installed manager's +14 (48B0F0), not the
            //unchanged BASE +78 shortcut. Postprocess the shared named mesh
            //before45C990/45F280 configures the per-state clone.
            if(ready && prototype.IsDirectMeshPrototype)
                foreach(var mesh in prototype.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,prototype.RootRenderObjectName)))
                    PostprocessOrdinaryNamedMesh(mesh);
            cached = (ready ? prototype with {RslFactoryStateId=stateId} : null, diagnostic);
            _rslObjectPrototypes[key] = cached;
        }
        root = cached.Root!;
        diagnostic = cached.Diagnostic;
        if(root is not null)
        {
            try {root=RetainRslParticleTemplate(state,root);}
            catch(InvalidDataException error){root=null!;diagnostic=$"RSL particle template: {error.Message}";}
        }
        return root is not null;
    }

    private bool TryMaterializeRslObjectPrototype(
        int baseAsset,
        int stateId,
        EnbResolvedRslStateResource state,
        EnbRetailAssetBundle retailAssets,
        out LiveCpuRoot root,
        out string? diagnostic)
    {
        root = default!;
        diagnostic = null;
        string stateDescriptor = DescribeRslStateForDiagnostic(state);
        if (state.Definition.Type is not (EnbRslStateType.Object or EnbRslStateType.Bone))
        {
            diagnostic = $"{stateDescriptor}: state does not use current-retail Object prototype materialization.";
            return false;
        }

        string? prototypeName = state.ObjectPrototypeName;
        if (string.IsNullOrEmpty(prototypeName))
        {
            diagnostic = $"{stateDescriptor}: OBJECT prototype is empty.";
            return false;
        }

        //0091D4E2 handles the built-in NULL before registry or FILE lookup.
        if(string.Equals(prototypeName,"NULL",StringComparison.OrdinalIgnoreCase) &&
            EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(retailAssets.FileResources,
                retailAssets.MixResources,"NULL",out var builtin,out diagnostic))
            return TryGetCpuRoot(new(baseAsset,stateId,builtin.LogicalFileName,"NULL"),builtin,retailAssets,out root,out diagnostic);

        var attempts = new List<string>(2);

        if(TryAcquireConfiguredPrototype(baseAsset,prototypeName,stateId,retailAssets,out root))return true;

        //0091D4B0 returns a registered prototype before deriving any filename.
        //This includes prior FILE/precache/swatch loads in the same asset manager.
        if (_prototypeResources.TryFindPrototypeSource(prototypeName, out var registeredSource))
        {
            var registeredKey = new LiveCpuRootKey(baseAsset, stateId,
                registeredSource.LogicalFileName, prototypeName);
            if (TryGetCpuRoot(registeredKey, registeredSource, retailAssets, out root, out diagnostic))
                return true;
            attempts.Add($"registered OBJECT '{prototypeName}' could not materialize: {diagnostic}");
        }

        // Current client FUN_0045EF80 asks WW3DAssetManager::Create_Render_Object
        // for OBJECT before consulting FILE. FUN_0091D4B0 proves that a missing
        // simple prototype lazily loads <OBJECT>.w3d and retries the registry.
        // Keep this runtime lookup separate from the immutable RSL FILE graph.
        else if (retailAssets.W3dFiles.TryResolveLazyFile(
                prototypeName,
                out EnbResolvedW3dResource lazySource,
                out string? lazyDiagnostic))
        {
            var selectedLazySource = lazySource;
            string? rootDiagnostic = null;
            bool registered = _prototypeResources.TryLoadFile(lazySource, out _, out rootDiagnostic, ReadMapperClock()) &&
                _prototypeResources.TryFindPrototypeSource(prototypeName, out selectedLazySource);
            var lazyKey = new LiveCpuRootKey(
                baseAsset,
                stateId,
                lazySource.LogicalFileName,
                lazySource.RequestedObjectName);
            if (registered && TryGetCpuRoot(
                    lazyKey,
                    selectedLazySource,
                    retailAssets,
                    out root,
                    out rootDiagnostic))
            {
                return true;
            }

            attempts.Add(
                $"OBJECT-first lazy {lazySource.LogicalFileName}::{prototypeName}: {rootDiagnostic ?? "OBJECT is absent after the lazy FILE load"}");
        }
        else
        {
            string detail = lazyDiagnostic ?? "lazy OBJECT lookup failed";
            string logicalLazyName = prototypeName + ".w3d";
            if (detail.Contains("not decoded", StringComparison.OrdinalIgnoreCase))
            {
                detail += " | " + DescribeUnsupportedW3dStructure(
                    retailAssets.MixResources,
                    logicalLazyName,
                    prototypeName);
            }
            attempts.Add($"OBJECT-first lazy {logicalLazyName}::{prototypeName}: {detail}");
        }

        // Retail only reaches this source-load branch after OBJECT creation has
        // failed. An authored FILE is loaded, then the same OBJECT is retried.
        // EnbNext can use the already-resolved FILE model as that second source,
        // but does not turn an unsupported W3D family into the retail NULL
        // fallback merely because our decoder is incomplete.
        EnbResolvedRslFileDependency fileDependency = state.FileDependency;
        bool explicitFilePublished = false;
        if (fileDependency.IsResolved &&
            fileDependency.ResourceLocation is EnbMixResourceLocation location &&
            fileDependency.Model is EnbW3dModel model &&
            fileDependency.Textures is IReadOnlyList<EnbResolvedTextureReference> textureReferences)
        {
            var fileSource = new EnbResolvedW3dResource(
                fileDependency.LogicalFileName,
                prototypeName,
                location,
                model,
                textureReferences) { LooseFilePath = fileDependency.LooseFilePath,PrototypeFile=fileDependency.PrototypeFile };
            string? rootDiagnostic;
            explicitFilePublished = _prototypeResources.TryLoadFile(fileSource, out _, out rootDiagnostic, ReadMapperClock());
            if (explicitFilePublished &&
                _prototypeResources.TryFindPrototypeSource(prototypeName, out var retrySource))
            {
                var fileKey = new LiveCpuRootKey(baseAsset, stateId,
                    retrySource.LogicalFileName, retrySource.RequestedObjectName);
                if (TryGetCpuRoot(fileKey, retrySource, retailAssets, out root, out rootDiagnostic))
                    return true;
            }

            rootDiagnostic ??= $"OBJECT '{prototypeName}' is absent after the explicit FILE load.";

            attempts.Add(
                $"FILE fallback {fileSource.LogicalFileName}::{prototypeName}: {rootDiagnostic ?? "unknown CPU-root rejection"}");
        }
        else if (fileDependency.Status == EnbRslFileDependencyStatus.NotAuthored)
        {
            attempts.Add("FILE fallback not authored");
        }
        else
        {
            string detail =
                $"FILE fallback '{fileDependency.LogicalFileName}' unresolved ({fileDependency.Status}): {fileDependency.Diagnostic ?? "no diagnostic"}";
            if (fileDependency.Status == EnbRslFileDependencyStatus.InvalidW3d &&
                !string.IsNullOrWhiteSpace(fileDependency.LogicalFileName))
            {
                detail += " | " + DescribeUnsupportedW3dStructure(
                    retailAssets.MixResources,
                    fileDependency.LogicalFileName,
                    prototypeName);
            }
            attempts.Add(detail);
        }

        //45EF80/45F280 construct a named NULL after genuine factory failure.
        //Certify name absence against the current namespace; unsupported model
        //families/failed decoding are not evidence of a native missing object.
        //A detached FILE graph may miss a loose override, so never use its
        //MissingResource label alone to authorize the fallback.
        bool fileLookupRepresented=fileDependency.Status==EnbRslFileDependencyStatus.NotAuthored ||
            explicitFilePublished ||
            fileDependency.Status==EnbRslFileDependencyStatus.MissingResource &&
                !retailAssets.FileResources.TryRead(fileDependency.LogicalFileName,out _,out _,out _);
        if(fileLookupRepresented && !EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                retailAssets.FileResources,retailAssets.MixResources,prototypeName,out _,out _,out _,out bool nativeMissing,
                _prototypeResources.CapturePrototypeSources(out _)) && nativeMissing &&
            EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(retailAssets.FileResources,
                retailAssets.MixResources,"NULL",out var nullSource,out diagnostic))
            return TryGetCpuRoot(new(baseAsset,stateId,nullSource.LogicalFileName,"NULL"),nullSource,retailAssets,out root,out diagnostic);

        diagnostic = $"{stateDescriptor}: " + string.Join(" | ", attempts.Distinct(StringComparer.Ordinal));
        return false;
    }

    private static string DescribeUnsupportedW3dStructure(
        EnbMixResourceCatalog? resources,
        string logicalFileName,
        string asciiNeedle)
    {
        if (resources is null)
            return "R6Z10 W3D structure unavailable: MIX catalog absent";
        if (!resources.TryReadResource(logicalFileName, out byte[] bytes, out EnbMixResourceLocation location))
            return $"R6Z10 W3D structure unavailable: '{logicalFileName}' could not be read";
        if (!EnbW3dTopLevelChunkProbe.TryInspect(
                bytes,
                string.IsNullOrWhiteSpace(asciiNeedle) ? Path.GetFileNameWithoutExtension(logicalFileName) : asciiNeedle,
                out EnbW3dTopLevelChunkProbeResult probe,
                out string? probeDiagnostic))
        {
            return $"R6Z10 W3D structure probe rejected '{location.Name}': {probeDiagnostic ?? "unknown structural failure"}";
        }

        const int maximumChunks = 24;
        string top = string.Join(
            ",",
            probe.Chunks.Take(maximumChunks).Select(static chunk =>
                $"0x{chunk.Type:X4}{(chunk.IsContainer ? "C" : "L")}@0x{chunk.HeaderOffset:X}"));
        if (probe.Chunks.Length > maximumChunks)
            top += $",+{probe.Chunks.Length - maximumChunks}";

        string token = probe.Matches.Length == 0
            ? "none"
            : string.Join(
                ",",
                probe.Matches.Take(8).Select(static match =>
                    $"0x{match.ChunkType:X4}{(match.IsContainer ? "C" : "L")}@0x{match.MatchOffset:X}")) +
              (probe.Matches.Length > 8 ? $",+{probe.Matches.Length - 8}" : string.Empty);
        return
            $"R6Z10 W3D structure '{location.Name}'/{location.ArchiveName} bytes={bytes.Length} top [{top}] object-token [{token}]";
    }

    private static string DescribeRslStateForDiagnostic(EnbResolvedRslStateResource state)
    {
        EnbRslStateDefinition definition = state.Definition;
        string file = string.IsNullOrWhiteSpace(definition.File) ? "<none>" : definition.File!;
        string prototype = string.IsNullOrWhiteSpace(definition.Object) ? "<none>" : definition.Object!;
        EnbRslStateFlags flags = definition.Flags;
        return
            $"state {definition.StateId} type {definition.Type} object '{prototype}' file '{file}' " +
            $"flags precached={(flags.Precached ? 1 : 0)} instance={(flags.InstanceData ? 1 : 0)} removeProto={(flags.RemovePrototype ? 1 : 0)}";
    }

    private static int ResolveRslStateLink(int stateId, IReadOnlyDictionary<int, int> links)
    {
        // Current packet/base-asset initialization supplies one explicit link
        // indirection. Keep the runtime bounded instead of inventing recursive
        // state-machine execution for NEXT/LINK authored edges.
        return links.TryGetValue(stateId, out int target) ? target : stateId;
    }

    private bool TryGetCpuRoot(
        LiveCpuRootKey key,
        EnbResolvedW3dResource source,
        EnbRetailAssetBundle retailAssets,
        out LiveCpuRoot root,
        out string? diagnostic)
        => TryGetCpuRootCore(
            key,
            source,
            retailAssets,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            depth: 0,
            out root,
            out diagnostic);

    private EnbW3dRenderResource ConvertRegisteredHmodelOwner(EnbW3dRenderResource resource, string requestedName,
        EnbRetailAssetBundle retailAssets,LiveCpuRootKey key,HashSet<string> aggregateStack,int depth,
        out IReadOnlyList<LiveHmodelChild> retainedChildren)
    {
        retainedChildren=[];
        var model = resource.HierarchyModels.FirstOrDefault(value =>
            string.Equals(value.Name, requestedName, StringComparison.OrdinalIgnoreCase));
        if (model is null) return resource;
        string ownerKey=$"hmodel:{requestedName}";
        if(!aggregateStack.Add(ownerKey))return resource;
        try
        {
        var meshes = new List<EnbW3dRenderMesh>();
        var external = new List<EnbW3dExternalRenderObject>();
        var composites=new List<EnbW3dRenderCompositeChild>();
        var children=new List<LiveHmodelChild>();
        var created = new HashSet<int>();
        foreach (var node in model.Nodes)
        {
            //98C7A0 uses manager+14 just like98C420: a generated prototype
            //already in the shared namespace wins before the FILE retry.
            if(TryAcquireConfiguredChild(key.BaseAsset,key.RslStateId,node.RenderObjectName,retailAssets,out var configuredChild))
            {
                if(!TryDescribeCompositeChild(configuredChild,node.RenderObjectName,out var description,out _))return resource;
                children.Add(new(node.Ordinal,node.PivotIndex,configuredChild));
                if(!composites.Any(value=>string.Equals(value.Name,node.RenderObjectName,StringComparison.OrdinalIgnoreCase)))composites.Add(description);
                created.Add(node.Ordinal);
                continue;
            }
            if (!_prototypeResources.TryFindPrototypeSource(node.RenderObjectName, out var source))
            {
                if (string.Equals(node.RenderObjectName, "NULL", StringComparison.OrdinalIgnoreCase))
                {
                    external.Add(new("NULL", EnbW3dExternalRenderObjectKind.NullObject, resource.LogicalFileName, resource.Key.ArchivePath));
                    created.Add(node.Ordinal);
                    continue;
                }
                int dot = node.RenderObjectName.IndexOf('.');
                string filename = (dot < 0 ? node.RenderObjectName : node.RenderObjectName[..dot]) + ".w3d";
                bool decoded = retailAssets.W3dFiles.TryResolve(filename, node.RenderObjectName,
                    out var loaded, out _, out bool opened);
                if (!opened)
                    decoded = retailAssets.W3dFiles.TryResolve("..\\" + filename, node.RenderObjectName,
                        out loaded, out _, out opened);
                // 0091D4B0 retries its registry after both failed file opens;
                // 0098C7A0 skips the resulting null child.
                if (!opened) continue;
                if (!decoded || !_prototypeResources.TryLoadFile(loaded, out _, out _, ReadMapperClock())) return resource;
                //0091D270 ignores child loader status;0091D4B0 then repeats
                //the lookup. A fully represented opened file can legitimately
                //register other prototypes without supplying this child.
                if (!_prototypeResources.TryFindPrototypeSource(node.RenderObjectName, out source)) continue;
            }
            if(source.AggregatePrototype is not null)
            {
                //0098C8DA acquires the named RenderObj, including a0600
                //prototype's independently constructed base/attachments.
                var aggregateChildKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,source.LogicalFileName,node.RenderObjectName,source.PrototypeRegistration);
                if(!TryGetCpuRootCore(aggregateChildKey,source,retailAssets,aggregateStack,depth+1,out var aggregateChild,out _) ||
                    !TryDescribeCompositeChild(aggregateChild,node.RenderObjectName,out var aggregateDescription,out _))return resource;
                children.Add(new(node.Ordinal,node.PivotIndex,aggregateChild));
                if(!composites.Any(value=>string.Equals(value.Name,node.RenderObjectName,StringComparison.OrdinalIgnoreCase)))composites.Add(aggregateDescription);
                created.Add(node.Ordinal);
                continue;
            }
            if (!_prototypeResources.TryGetRegisteredPrototype(source, out var registered) ||
                source.RegisteredPrototype is not {} entry) return resource;
            switch (entry.Kind)
            {
                case EnbW3dPrototypeKind.Mesh:
                    var mesh = registered.Meshes[entry.Ordinal];
                    PostprocessOrdinaryNamedMesh(mesh);
                    if (!meshes.Any(value => ReferenceEquals(value, mesh))) meshes.Add(mesh);
                    break;
                case EnbW3dPrototypeKind.Dazzle:
                case EnbW3dPrototypeKind.Box:
                case EnbW3dPrototypeKind.NullObject:
                    var kind = entry.Kind == EnbW3dPrototypeKind.Dazzle ? EnbW3dExternalRenderObjectKind.Dazzle : entry.Kind == EnbW3dPrototypeKind.Box
                        ? EnbW3dExternalRenderObjectKind.Box : EnbW3dExternalRenderObjectKind.NullObject;
                    var child = registered.ExternalRenderObjects.Where(value => value.Kind == kind).ElementAt(entry.Ordinal);
                    if (!external.Any(value => ReferenceEquals(value, child))) external.Add(child);
                    break;
                case EnbW3dPrototypeKind.Collection:
                case EnbW3dPrototypeKind.HierarchyModel:
                case EnbW3dPrototypeKind.HierarchicalLod:
                case EnbW3dPrototypeKind.LegacyLod:
                case EnbW3dPrototypeKind.SpecialMaterial:
                case EnbW3dPrototypeKind.Sphere:
                case EnbW3dPrototypeKind.Ring:
                case EnbW3dPrototypeKind.ParticleEmitter:
                    var childKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,source.LogicalFileName,node.RenderObjectName,source.PrototypeRegistration);
                    if(!TryGetCpuRootCore(childKey,source,retailAssets,aggregateStack,depth+1,out var childRoot,out _) ||
                        (entry.Kind switch {
                            EnbW3dPrototypeKind.HierarchyModel=>childRoot.Hlod?.IsConvertedHierarchyModel!=true,
                            EnbW3dPrototypeKind.HierarchicalLod=>!childRoot.AuthoredGlobalOwner,
                            EnbW3dPrototypeKind.LegacyLod=>childRoot.Hlod?.IsConvertedLegacyLod!=true,
                            EnbW3dPrototypeKind.SpecialMaterial=>childRoot.SpecialMaterial is null,
                            EnbW3dPrototypeKind.Sphere=>childRoot.Sphere is null,
                            EnbW3dPrototypeKind.Ring=>childRoot.Ring is null,
                            EnbW3dPrototypeKind.ParticleEmitter=>childRoot.RetainedEmitter is null,
                            _=>childRoot.Collection is null }) ||
                        !TryComputeRootLocalBounds(childRoot,out var childBounds,out _) ||
                        !TryComputeRootIntegerCost(childRoot,out int childCost,out _))return resource;
                    children.Add(new(node.Ordinal,node.PivotIndex,childRoot));
                    if(!composites.Any(value=>string.Equals(value.Name,node.RenderObjectName,StringComparison.OrdinalIgnoreCase)))
                        {
                        if(!TryDescribeCompositeChild(childRoot,node.RenderObjectName,out var composite,out _))return resource;
                        composites.Add(composite);
                    }
                    break;
                default:
                    return resource;
            }
            created.Add(node.Ordinal);
        }
        // Each child above comes from the retained first global prototype.
        // Repeated members retain separate pivots even when geometry is shared.
        var storageOrdinals=model.Nodes.Where(node=>created.Contains(node.Ordinal)).Select((node,index)=>(node.Ordinal,index)).ToDictionary(pair=>pair.Ordinal,pair=>pair.index);
        retainedChildren=children.Select(child=>child with {StorageOrdinal=storageOrdinals[child.Ordinal]}).ToArray();
        return resource.Copy().SetMeshes(meshes.ToArray()).SetExternalRenderObjects(external.ToArray()).SetCompositeRenderChildren(composites.ToArray()).SetCollections([]).SetHierarchyModels([]).SetHierarchicalLods([EnbW3dHmodelConversion.Create(model, node => created.Contains(node.Ordinal))]);
        }
        finally { aggregateStack.Remove(ownerKey); }
    }

    // Whole0098C420 creates each authored member through0091D4B0, retaining
    // the first registered prototype rather than geometry from the latest file.
    // Composite member owners are handled separately from concrete leaf storage.
    private bool TryBindRegisteredAuthoredHlodOwner(EnbW3dRenderResource resource,string requestedName,
        EnbRetailAssetBundle retailAssets,LiveCpuRootKey key,HashSet<string> aggregateStack,int depth,
        out EnbW3dRenderResource bound,out IReadOnlyList<LiveHmodelChild> retainedChildren)
    {
        bound=resource;retainedChildren=[];
        var hlod=resource.HierarchicalLods.FirstOrDefault(value=>!value.UsesRetainedPivotIndices&&
            string.Equals(value.Name,requestedName,StringComparison.OrdinalIgnoreCase));
        if(hlod is null)return false;
        string ownerKey=$"hlod:{requestedName}";
        if(!aggregateStack.Add(ownerKey))return false;
        try
        {
        var meshes=new List<EnbW3dRenderMesh>();var external=new List<EnbW3dExternalRenderObject>();
        var composites=new List<EnbW3dRenderCompositeChild>();var compositeRoots=new Dictionary<string,LiveCpuRoot>(StringComparer.OrdinalIgnoreCase);
        var retained=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var member in hlod.Levels.SelectMany(value=>value.Members).Concat(hlod.Aggregate?.Members??[]))
        {
            if(retained.Contains(member.Name))continue;
            if(TryAcquireConfiguredChild(key.BaseAsset,key.RslStateId,member.Name,retailAssets,out var configuredChild))
            {
                if(!TryDescribeCompositeChild(configuredChild,member.Name,out var description,out _))return false;
                compositeRoots.Add(member.Name,configuredChild);composites.Add(description);retained.Add(member.Name);
                continue;
            }
            if(!_prototypeResources.TryFindPrototypeSource(member.Name,out var source))
            {
                if(string.Equals(member.Name,"NULL",StringComparison.OrdinalIgnoreCase))
                {external.Add(new("NULL",EnbW3dExternalRenderObjectKind.NullObject,resource.LogicalFileName,resource.Key.ArchivePath));retained.Add(member.Name);continue;}
                int dot=member.Name.IndexOf('.');string filename=(dot<0?member.Name:member.Name[..dot])+".w3d";
                bool decoded=retailAssets.W3dFiles.TryResolve(filename,member.Name,out var loaded,out _,out bool opened);
                if(!opened)decoded=retailAssets.W3dFiles.TryResolve("..\\"+filename,member.Name,out loaded,out _,out opened);
                if(!opened)continue;
                if(!decoded||!_prototypeResources.TryLoadFile(loaded,out _,out _,ReadMapperClock()))return false;
                if(!_prototypeResources.TryFindPrototypeSource(member.Name,out source))continue;
            }
            if(source.AggregatePrototype is not null)
            {
                var aggregateChildKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,source.LogicalFileName,member.Name,source.PrototypeRegistration);
                if(!TryGetCpuRootCore(aggregateChildKey,source,retailAssets,aggregateStack,depth+1,out var aggregateChild,out _) ||
                    !TryDescribeCompositeChild(aggregateChild,member.Name,out var aggregateDescription,out _))return false;
                compositeRoots.Add(member.Name,aggregateChild);composites.Add(aggregateDescription);
                retained.Add(member.Name);
                continue;
            }
            if(!_prototypeResources.TryGetRegisteredPrototype(source,out var registered)||source.RegisteredPrototype is not {} entry)return false;
            switch(entry.Kind)
            {
                case EnbW3dPrototypeKind.Mesh:
                    var mesh=registered.Meshes[entry.Ordinal];PostprocessOrdinaryNamedMesh(mesh);
                    if(!meshes.Any(value=>ReferenceEquals(value,mesh)))meshes.Add(mesh);break;
                case EnbW3dPrototypeKind.Dazzle:
                case EnbW3dPrototypeKind.Box:
                case EnbW3dPrototypeKind.NullObject:
                    var kind=entry.Kind==EnbW3dPrototypeKind.Dazzle?EnbW3dExternalRenderObjectKind.Dazzle:entry.Kind==EnbW3dPrototypeKind.Box?EnbW3dExternalRenderObjectKind.Box:EnbW3dExternalRenderObjectKind.NullObject;
                    var child=registered.ExternalRenderObjects.Where(value=>value.Kind==kind).ElementAt(entry.Ordinal);
                    if(!external.Any(value=>ReferenceEquals(value,child)))external.Add(child);break;
                case EnbW3dPrototypeKind.Collection:
                case EnbW3dPrototypeKind.HierarchyModel:
                case EnbW3dPrototypeKind.HierarchicalLod:
                case EnbW3dPrototypeKind.LegacyLod:
                case EnbW3dPrototypeKind.SpecialMaterial:
                case EnbW3dPrototypeKind.Sphere:
                case EnbW3dPrototypeKind.Ring:
                case EnbW3dPrototypeKind.ParticleEmitter:
                    var childKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,source.LogicalFileName,member.Name,source.PrototypeRegistration);
                    if(!TryGetCpuRootCore(childKey,source,retailAssets,aggregateStack,depth+1,out var childRoot,out _) ||
                        (entry.Kind==EnbW3dPrototypeKind.HierarchicalLod&&!childRoot.AuthoredGlobalOwner) ||
                        !TryDescribeCompositeChild(childRoot,member.Name,out var composite,out _))return false;
                    compositeRoots.Add(member.Name,childRoot);composites.Add(composite);break;
                default:return false;
            }
            retained.Add(member.Name);
        }
        var hierarchy=resource.Hierarchies.FirstOrDefault(value=>string.Equals(value.Name,hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
        if(hierarchy is null)return false;
        var selected=hlod with {
            Levels=hlod.Levels.Select(level=>level with {Members=level.Members.Where(member=>retained.Contains(member.Name)).ToArray()}).ToArray(),
            Aggregate=hlod.Aggregate is {} aggregate?aggregate with {Members=aggregate.Members.Where(member=>retained.Contains(member.Name)&&member.ReferenceIndex<(uint)hierarchy.Pivots.Count).ToArray()}:null };
        var children=new List<LiveHmodelChild>();int ordinal=0;
        foreach(var level in selected.Levels)foreach(var member in level.Members)
        {if(compositeRoots.TryGetValue(member.Name,out var childRoot))children.Add(new(ordinal,member.ReferenceIndex,childRoot,level.Ordinal));ordinal++;}
        foreach(var member in selected.Aggregate?.Members??[])
        {if(compositeRoots.TryGetValue(member.Name,out var childRoot))children.Add(new(ordinal,member.ReferenceIndex,childRoot,null,true));ordinal++;}
        retainedChildren=children.ToArray();
        bound=resource.Copy().SetMeshes(meshes.ToArray()).SetExternalRenderObjects(external.ToArray()).SetCompositeRenderChildren(composites.ToArray()).SetCollections([]).SetHierarchyModels([]).SetHierarchicalLods([selected]);
        return true;
        }
        finally {aggregateStack.Remove(ownerKey);}
    }

    private bool TryGetCpuRootCore(
        LiveCpuRootKey key,
        EnbResolvedW3dResource source,
        EnbRetailAssetBundle retailAssets,
        HashSet<string> aggregateStack,
        int depth,
        out LiveCpuRoot root,
        out string? diagnostic)
    {
        diagnostic = null;
        if(source.CurrentRetailAggregate is not null && source.AcquisitionPublications.Any(publication=>
            publication.PrototypeFile?.Entries.Any(entry=>entry.Aggregate?.Name.Equals(source.RequestedObjectName,StringComparison.OrdinalIgnoreCase)==true)==true))
        {
            //A pre-resolved authored source still represents FILE loads, not
            //a constructed global prototype. Existing NAME wins; otherwise
            //publish its retained files before constructing the selected root.
            if(!_prototypeResources.TryFindPrototypeSource(source.RequestedObjectName,out var publishedSource))
            {
                foreach(var publication in source.AcquisitionPublications)
                    if(!_prototypeResources.TryLoadFile(publication,out _,out diagnostic,ReadMapperClock()))
                    {root=default!;return false;}
                if(!_prototypeResources.TryFindPrototypeSource(source.RequestedObjectName,out publishedSource))
                {root=default!;diagnostic=$"Aggregate '{source.RequestedObjectName}' is absent after its retained FILE publications.";return false;}
            }
            source=publishedSource;
        }
        if(source.AggregatePrototype is not null)
        {
            var wrapperSource=source;
            bool created=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(retailAssets.FileResources,retailAssets.RetailMixResources,
                wrapperSource,_prototypeResources.CapturePrototypeSources(out _),out source,out var creationDiagnostic,out var publications);
            //009A3AB0's base acquisition uses the ordinary manager: every
            //loaded FILE publishes before creation. Retain real registration
            //identities in the root key after publishing those acquisitions.
            foreach(var publication in publications)
                if(!_prototypeResources.TryLoadFile(publication,out _,out diagnostic,ReadMapperClock()))
                {root=default!;return false;}
            if(!created){root=default!;diagnostic=creationDiagnostic;return false;}
            if(publications.Count!=0)
            {
                if(!EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(retailAssets.FileResources,retailAssets.RetailMixResources,
                    wrapperSource,_prototypeResources.CapturePrototypeSources(out _),out source,out diagnostic))
                {root=default!;return false;}
            }
        }
        //The published wrapper survives base removal. A later construction
        //must not reuse a root built from its earlier base registration.
        key = key with { PrototypeRegistration = source.PrototypeRegistration,
            AggregateBaseRegistration=source.CurrentRetailAggregate?.BasePrototypeRegistration??0 };
        if (_cpuRoots.TryGetValue(key, out root!))
            return true;
        EnbCurrentRetailW3dAggregateResolution? aggregate = source.CurrentRetailAggregate;
        string requestedRenderObjectName = aggregate?.BaseRequestedObjectName ?? source.RequestedObjectName;
        EnbResolvedW3dResource renderSource = aggregate is null
            ? source
            : new EnbResolvedW3dResource(
                aggregate.BaseLogicalFileName,
                aggregate.BaseRequestedObjectName,
                aggregate.BaseResourceLocation,
                source.Model,
                source.Textures){RegisteredPrototype=aggregate.BaseRegisteredPrototype,
                    PrototypeRegistration=aggregate.BasePrototypeRegistration,LooseFilePath=aggregate.BaseLooseFilePath};

        string? aggregateStackKey = null;
        if (aggregate is not null)
        {
            aggregateStackKey = $"{source.ResourceLocation.ArchiveName}|{source.ResourceLocation.Name}|{source.RequestedObjectName}";
            if (!aggregateStack.Add(aggregateStackKey))
            {
                root = default!;
                diagnostic = $"0x0600 aggregate '{source.RequestedObjectName}' forms a recursive attachment cycle.";
                return false;
            }
        }

        try
        {
            EnbW3dRenderResource resource;
            string? compileDiagnostic = null;
            //The manager's built-in NULL has no authored cache registration.
            bool compiled = renderSource.PrototypeRegistration==0 && string.Equals(requestedRenderObjectName,"NULL",StringComparison.OrdinalIgnoreCase)
                ? EnbW3dRenderResourceCompiler.TryCompile(renderSource,out resource,out compileDiagnostic)
                : renderSource.RegisteredPrototype is not null
                ? _prototypeResources.TryGetRegisteredPrototype(renderSource, out resource)
                : _prototypeResources.TryGetOrCompile(renderSource, out resource, out compileDiagnostic, ReadMapperClock());
            if (!compiled)
            {
                root = default!;
                diagnostic = $"render-resource compile rejected: {compileDiagnostic ?? "unknown reason"}";
                return false;
            }

            //9A454E constructs the aggregate base through9A3AB0's
            //ordinary named factory before its authored children are created.
            if(aggregate is not null && renderSource.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Mesh})
                foreach(var mesh in resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,requestedRenderObjectName)))
                    PostprocessOrdinaryNamedMesh(mesh);

            if(renderSource.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.SpecialMaterial} ||
                renderSource.RegisteredPrototype is null && renderSource.Model.SpecialMaterials.Any(r=>r.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)))
            {
                var definition=renderSource.RegisteredPrototype is {} specialEntry
                    ? renderSource.Model.SpecialMaterials[specialEntry.Ordinal]
                    : renderSource.Model.SpecialMaterials.Single(r=>r.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
                var discarded=aggregate is null ? Array.Empty<EnbCurrentRetailParticleEmitterDefinition>() :
                    EnbCurrentRetailRObjEffectAsset.PreparePrototype(retailAssets,source,Vector3.Zero,
                        _prototypeResources.CapturePrototypeSources(out _),CreateLiveWorldPublicationContext(ReadMapperClock())).DiscardedParticleConstructions;
                var empty=resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([]).SetSpecialMaterials([definition]);
                root=new(false,empty,null,new(StringComparer.OrdinalIgnoreCase){definition.Name},aggregate?.RenderDefinition.Name??definition.Name,
                    definition.Name,null,null,[]){SpecialMaterial=_prototypeResources.CreateSpecialMaterialInstance(definition),RenderObjectHidden=(definition.Flags&0x1000)!=0,DiscardedParticleConstructions=discarded,FactoryLookupName=source.RequestedObjectName};
                _cpuRoots[key]=root;
                return true;
            }

            //0091D4B0 calls the retained prototype's Create for every class.
            //Sphere's attachment virtuals are no-ops, including0600 wrappers.
            if(renderSource.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Sphere} ||
                renderSource.RegisteredPrototype is null && renderSource.Model.Spheres.Any(s=>s.Header.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)))
            {
                var discarded=aggregate is null ? Array.Empty<EnbCurrentRetailParticleEmitterDefinition>() :
                    EnbCurrentRetailRObjEffectAsset.PreparePrototype(retailAssets,source,Vector3.Zero,
                        _prototypeResources.CapturePrototypeSources(out _),CreateLiveWorldPublicationContext(ReadMapperClock())).DiscardedParticleConstructions;
                root=CreateRetainedSphereRoot(resource,aggregate?.RenderDefinition.Name??requestedRenderObjectName,
                    EnbRetailSphereAttachment.LoadFromSource(retailAssets,source)) with {DiscardedParticleConstructions=discarded,FactoryLookupName=source.RequestedObjectName};
                _cpuRoots[key]=root;
                return true;
            }

            if(renderSource.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.Ring} ||
                renderSource.RegisteredPrototype is null && renderSource.Model.Rings.Any(r=>r.Header.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)))
            {
                //009A3A50 constructs children before the primitive's no-op insertion.
                var discarded=aggregate is null ? Array.Empty<EnbCurrentRetailParticleEmitterDefinition>() :
                    EnbCurrentRetailRObjEffectAsset.PreparePrototype(retailAssets,source,Vector3.Zero,
                        _prototypeResources.CapturePrototypeSources(out _),CreateLiveWorldPublicationContext(ReadMapperClock())).DiscardedParticleConstructions;
                root=CreateRetainedRingRoot(resource,aggregate?.RenderDefinition.Name??requestedRenderObjectName,
                    EnbRetailRingAttachment.LoadFromSource(retailAssets,source)) with {DiscardedParticleConstructions=discarded,FactoryLookupName=source.RequestedObjectName};
                _cpuRoots[key]=root;
                return true;
            }

            if(renderSource.RegisteredPrototype is {Kind:EnbW3dPrototypeKind.ParticleEmitter} ||
                renderSource.RegisteredPrototype is null && renderSource.Model.ParticleEmitters.Any(p=>p.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)))
            {
                var discarded=aggregate is null ? Array.Empty<EnbCurrentRetailParticleEmitterDefinition>() :
                    EnbCurrentRetailRObjEffectAsset.PreparePrototype(retailAssets,source,Vector3.Zero,
                        _prototypeResources.CapturePrototypeSources(out _),CreateLiveWorldPublicationContext(ReadMapperClock())).DiscardedParticleConstructions;
                var definition=renderSource.RegisteredPrototype is {} particleEntry
                    ?renderSource.Model.ParticleEmitters[particleEntry.Ordinal]
                    :renderSource.Model.ParticleEmitters.Single(p=>p.Name.Equals(requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
                var emitter=new EnbCurrentRetailShipExhaustAsset(resource.Key,definition,null,null,null).PrepareTexture(retailAssets);
                var empty=resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetLegacyLods([]).SetCompositeRenderChildren([]).SetExternalRenderObjects([]);
                root=new(false,empty,null,new(StringComparer.OrdinalIgnoreCase),aggregate?.RenderDefinition.Name??requestedRenderObjectName,
                    requestedRenderObjectName,null,null,[]){RetainedEmitter=emitter,DiscardedParticleConstructions=discarded,FactoryLookupName=source.RequestedObjectName};
                _cpuRoots[key]=root;
                return true;
            }

            if (renderSource.RegisteredPrototype is {} registeredEntry)
            {
                resource=resource.Copy().SetLegacyLods(registeredEntry.Kind==EnbW3dPrototypeKind.LegacyLod
                    ? [resource.LegacyLods[registeredEntry.Ordinal]] : []);
                resource = registeredEntry.Kind switch
                {
                    EnbW3dPrototypeKind.Mesh => resource.Copy().SetMeshes([resource.Meshes[registeredEntry.Ordinal]]).SetHierarchyModels([]).SetHierarchicalLods([]),
                    EnbW3dPrototypeKind.HierarchyModel => resource.Copy().SetHierarchyModels([resource.HierarchyModels[registeredEntry.Ordinal]]).SetHierarchicalLods([]),
                    EnbW3dPrototypeKind.HierarchicalLod => resource.Copy().SetHierarchicalLods([resource.HierarchicalLods[registeredEntry.Ordinal]]),
                    EnbW3dPrototypeKind.Collection => resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([resource.Collections[registeredEntry.Ordinal]]),
                    EnbW3dPrototypeKind.LegacyLod => resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]),
                    EnbW3dPrototypeKind.Dazzle => resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([resource.ExternalRenderObjects.Where(value=>value.Kind==EnbW3dExternalRenderObjectKind.Dazzle).ElementAt(registeredEntry.Ordinal)]),
                    EnbW3dPrototypeKind.Box => resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([resource.ExternalRenderObjects.Where(value=>value.Kind==EnbW3dExternalRenderObjectKind.Box).ElementAt(registeredEntry.Ordinal)]),
                    EnbW3dPrototypeKind.NullObject => resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([resource.ExternalRenderObjects.Where(value => value.Kind == EnbW3dExternalRenderObjectKind.NullObject).ElementAt(registeredEntry.Ordinal)]),
                    _ => resource,
                };
            }

            var legacyLod=resource.LegacyLods.FirstOrDefault(value=>string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<LiveHmodelChild> legacyChildren=[];
            if(legacyLod is not null && !TryConvertRegisteredLegacyLodOwner(resource,legacyLod,retailAssets,key,aggregateStack,depth,
                out resource,out legacyChildren,out diagnostic))
            {root=default!;return false;}

            if (!TryBindExternalHierarchies(resource, requestedRenderObjectName, retailAssets, out resource, out string? hierarchyDiagnostic))
            {
                root = default!;
                diagnostic = $"external hierarchy bind rejected: {hierarchyDiagnostic ?? "unknown reason"}";
                return false;
            }

            resource = ConvertRegisteredHmodelOwner(resource, requestedRenderObjectName, retailAssets,key,aggregateStack,depth,
                out var hmodelChildren);
            bool authoredGlobalOwner=TryBindRegisteredAuthoredHlodOwner(resource,requestedRenderObjectName,retailAssets,key,aggregateStack,depth,out var authoredResource,out var authoredChildren);
            if(authoredGlobalOwner){resource=authoredResource;hmodelChildren=authoredChildren;}
            if(legacyLod is not null){authoredGlobalOwner=true;hmodelChildren=legacyChildren;}
            resource = BindExternalHlodRenderObjects(resource, requestedRenderObjectName, retailAssets);

            var collection=resource.Collections.FirstOrDefault(value=>string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
            List<LiveCpuRoot> collectionChildren=[];
            var unavailableAttachments=new List<string>();
            if(collection is not null)
            {
                string collectionKey=$"collection:{source.PrototypeRegistration}:{source.ResourceLocation.ArchiveName}:{source.ResourceLocation.Name}:{requestedRenderObjectName}";
                if(!aggregateStack.Add(collectionKey))
                { root=default!;diagnostic=$"Collection '{requestedRenderObjectName}' forms a recursive prototype cycle.";return false; }
                try
                {
                    foreach(string childName in collection.ChildNames)
                    {
                        //9904FC uses the manager's ordinary named factory;
                        //a generated primitive has no decoded FILE source.
                        if(TryAcquireConfiguredChild(key.BaseAsset,key.RslStateId,childName,retailAssets,out var configuredChild))
                        {collectionChildren.Add(configuredChild);continue;}
                        if(string.Equals(childName,"NULL",StringComparison.OrdinalIgnoreCase))
                        {
                            var nullResource=resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetCollections([]).SetExternalRenderObjects([new("NULL",EnbW3dExternalRenderObjectKind.NullObject,resource.LogicalFileName,source.ResourceLocation.ArchiveName)]);
                            collectionChildren.Add(new(false,nullResource,null,new(StringComparer.OrdinalIgnoreCase){"NULL"},"NULL","NULL",null,null,[]));
                            continue;
                        }
                        bool registeredChild=_prototypeResources.TryFindPrototypeSource(childName,out var childSource);
                        if(!registeredChild || childSource.AggregatePrototype is not null)
                        {
                            string? childDiagnostic;IReadOnlyList<EnbResolvedW3dResource> childPublications;bool nativeMissing;
                            var childNamespace=_prototypeResources.CapturePrototypeSources(out _);
                            bool acquired=registeredChild
                                ? EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(retailAssets.FileResources,retailAssets.RetailMixResources,
                                    childSource,childNamespace,out _,out childDiagnostic,out childPublications,out nativeMissing)
                                : EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(retailAssets.FileResources,retailAssets.RetailMixResources,
                                    childName,out _,out childDiagnostic,out childPublications,out nativeMissing,childNamespace);
                            foreach(var publication in childPublications)
                                if(!_prototypeResources.TryLoadFile(publication,out _,out diagnostic,ReadMapperClock()))
                                {root=default!;return false;}
                            if(!acquired)
                            {
                                //Retail00990539 stores a failed009904FC result,
                                //then0099054A dereferences it unconditionally.
                                //Contain that proven bug locally; preserve the
                                //later children and do not invent a substitute.
                                if(nativeMissing)
                                {
                                    unavailableAttachments.Add($"Collection '{requestedRenderObjectName}' child '{childName}' unavailable: {childDiagnostic}");
                                    continue;
                                }
                                root=default!;diagnostic=$"Collection '{requestedRenderObjectName}' child '{childName}' could not materialize: {childDiagnostic}";return false;
                            }
                            if(!_prototypeResources.TryFindPrototypeSource(childName,out childSource))
                            {root=default!;diagnostic=$"Collection '{requestedRenderObjectName}' child '{childName}' is absent after its FILE load: {diagnostic}";return false;}
                        }
                        var childKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,childSource.LogicalFileName,childName,childSource.PrototypeRegistration);
                        if(!TryGetCpuRootCore(childKey,childSource,retailAssets,aggregateStack,depth+1,out var childRoot,out diagnostic))
                        { root=default!;return false; }
                        //9904FC invokes the ordinary named constructor
                        //48B0F0, including48E9D0 shared-mesh postprocessing.
                        if(childRoot.IsDirectMeshPrototype)
                            foreach(var mesh in childRoot.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,childRoot.RootRenderObjectName)))
                                PostprocessOrdinaryNamedMesh(mesh);
                        collectionChildren.Add(childRoot);
                    }
                }
                finally { aggregateStack.Remove(collectionKey); }
                resource=resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects([]);
            }

            EnbW3dRenderHlod? hlod = resource.HierarchicalLods.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, requestedRenderObjectName, StringComparison.OrdinalIgnoreCase));
            HashSet<string>? allowed = null;
            bool directMeshPrototype = false;
            string? attachmentHierarchyName = hlod?.HierarchyName;
            if (hlod is null)
            {
                allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (EnbW3dRenderMesh mesh in resource.Meshes)
                {
                    if (EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh, requestedRenderObjectName))
                        allowed.Add(mesh.Name);
                }

                EnbW3dRenderHierarchyModel? hierarchyModel = resource.HierarchyModels.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, requestedRenderObjectName, StringComparison.OrdinalIgnoreCase));
                if (hierarchyModel is not null)
                {
                    attachmentHierarchyName = hierarchyModel.HierarchyName;
                    foreach (EnbW3dRenderHierarchyModelNode node in hierarchyModel.Nodes)
                    {
                        allowed.Add(node.RenderObjectName);
                    }
                }
                else if (allowed.Count != 0)
                {
                    directMeshPrototype = aggregate is null;
                    //0093E660 exposes one Mesh prototype; its clone and
                    //0093F390 draw use that MeshModel, not the enclosing file's
                    //HMODEL/HLOD bindings for the same mesh.
                    resource = resource.Copy().SetMeshes(resource.Meshes.Where(mesh => allowed.Contains(mesh.Name)).ToArray()).SetHierarchyModels([]).SetHierarchicalLods([]);
                }

                if(collection is not null)allowed.Add(requestedRenderObjectName);
                if (allowed.Count == 0 && resource.ExternalRenderObjects.Any(value =>
                    value.Kind is EnbW3dExternalRenderObjectKind.NullObject or EnbW3dExternalRenderObjectKind.Box or EnbW3dExternalRenderObjectKind.Dazzle &&
                    string.Equals(value.Name, requestedRenderObjectName, StringComparison.OrdinalIgnoreCase)))
                {
                    allowed.Add(requestedRenderObjectName);
                    resource = resource.Copy().SetMeshes([]).SetHierarchyModels([]).SetHierarchicalLods([]).SetExternalRenderObjects(resource.ExternalRenderObjects.Where(value=>string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)).ToArray());
                }

                if (allowed.Count == 0)
                {
                    root = default!;
                    diagnostic =
                        $"requested root '{requestedRenderObjectName}' is absent after compile/bind (HLOD={resource.HierarchicalLods.Count}, mesh={resource.Meshes.Count}, hierarchy-model={resource.HierarchyModels.Count})";
                    return false;
                }
            }

            IReadOnlyList<LiveAggregateAttachment> attachments = Array.Empty<LiveAggregateAttachment>();
            var discardedParticles=new List<EnbCurrentRetailParticleEmitterDefinition>();
            bool nullAggregateBase=aggregate is not null && resource.ExternalRenderObjects.Any(value=>
                value.Kind==EnbW3dExternalRenderObjectKind.NullObject &&
                string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
            bool dazzleRoot=resource.ExternalRenderObjects.Any(value=>value.Kind==EnbW3dExternalRenderObjectKind.Dazzle &&
                string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase));
            bool meshAggregateBase=aggregate is not null && collection is null && legacyLod is null && hlod is null &&
                resource.HierarchyModels.Count==0 && resource.Meshes.Count==1;
            bool discardsAggregateChildren=nullAggregateBase || aggregate is not null && (meshAggregateBase || collection is not null || dazzleRoot || resource.ExternalRenderObjects.Any(value=>
                value.Kind==EnbW3dExternalRenderObjectKind.Box &&
                string.Equals(value.Name,requestedRenderObjectName,StringComparison.OrdinalIgnoreCase)));
            if (aggregate is not null && aggregate.RenderDefinition.Attachments.Count != 0)
            {
                if (attachmentHierarchyName is null && !discardsAggregateChildren)
                {
                    root = default!;
                    diagnostic =
                        $"0x0600 aggregate '{aggregate.RenderDefinition.Name}' has {aggregate.RenderDefinition.Attachments.Count} attachment(s), but base RenderObj '{requestedRenderObjectName}' exposes no hierarchy for authored bone attachment.";
                    return false;
                }

                var materialized = new List<LiveAggregateAttachment>(aggregate.RenderDefinition.Attachments.Count);
                for (int index = 0; index < aggregate.RenderDefinition.Attachments.Count; index++)
                {
                    EnbCurrentRetailW3dAggregateAttachment authored = aggregate.RenderDefinition.Attachments[index];
                    if(TryAcquireConfiguredChild(key.BaseAsset,key.RslStateId,authored.SubobjectName,retailAssets,out var configuredChild))
                    {
                        //9A3AB0's first named acquisition also sees generated
                        //objects. Retained clones do not replay child constructors.
                        if(!discardsAggregateChildren)materialized.Add(new(authored.SubobjectName,authored.BoneName,configuredChild,false));
                        continue;
                    }
                    bool hasPublishedSphereName=_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out var publishedSphereSource);
                    if (!discardsAggregateChildren && retailAssets.RetailMixResources is { } sphereResources &&
                        (hasPublishedSphereName ? publishedSphereSource.RegisteredPrototype?.Kind==EnbW3dPrototypeKind.Sphere :
                        sphereResources.TryReadResource(authored.SubobjectName + ".w3d", out var sphereBytes, out _) &&
                        sphereBytes.Length >= 8 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sphereBytes) == 0x741))
                    {
                        if(!hasPublishedSphereName)
                        {
                            //009A3AB0 publishes the lazy FILE before creating
                            //its named child; later owners retain this definition.
                            if(!retailAssets.W3dFiles.TryResolveLazyFile(authored.SubobjectName,out var spherePublication,out diagnostic) ||
                                !_prototypeResources.TryLoadFile(spherePublication,out _,out diagnostic,ReadMapperClock()) ||
                                !_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out publishedSphereSource))
                            {root=default!;diagnostic??=$"Sphere '{authored.SubobjectName}' is absent after FILE publication.";return false;}
                            hasPublishedSphereName=true;
                        }
                        var tree = resource.Hierarchies.First(h => string.Equals(h.Name, attachmentHierarchyName, StringComparison.OrdinalIgnoreCase));
                        var sphere=EnbRetailSphereAttachment.Load(retailAssets,tree,authored,index,source:hasPublishedSphereName?publishedSphereSource:null);
                        materialized.Add(new(authored.SubobjectName,authored.BoneName,
                            CreateRetainedSphereRoot(resource,authored.SubobjectName,sphere),false));
                        continue;
                    }
                    if (string.Equals(authored.SubobjectName, "NULL", StringComparison.OrdinalIgnoreCase))
                    {
                        if(discardsAggregateChildren)continue;
                        materialized.Add(new LiveAggregateAttachment(
                            authored.SubobjectName,
                            authored.BoneName,
                            Root: null,
                            IsRetailNoOp: true));
                        continue;
                    }
                    // 009A3A50/009A3AB0 acquire ordinary emitter children through
                    // the same registry and retained lifetime as other RenderObjs.
                    // Only ship exhaust/fixed visual consumers have a separate owner.
                    if (!discardsAggregateChildren &&
                        (EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(authored.BoneName) ||
                         EnbCurrentRetailShipFixedVisualRuntime.Specs.Any(s => s.BaseAsset == key.BaseAsset)) &&
                        retailAssets.MixResources is EnbMixResourceCatalog mixResources &&
                        EnbInstalledW3dPrototypeTokenAudit.TryResolveDirectParticleEmitterPrototype(
                            mixResources, authored.SubobjectName, out _))
                    {
                        materialized.Add(new LiveAggregateAttachment(
                            authored.SubobjectName,
                            authored.BoneName,
                            Root: null,
                            IsRetailNoOp: true,
                            Exhaust: EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(authored.BoneName) ||
                                EnbCurrentRetailShipFixedVisualRuntime.Specs.Any(s => s.BaseAsset == key.BaseAsset)
                                ? EnbCurrentRetailShipExhaustAsset.Load(retailAssets, authored.SubobjectName,
                                    allowInheritedMotion: EnbCurrentRetailShipFixedVisualRuntime.Specs.Any(s => s.BaseAsset == key.BaseAsset)) : null));
                        continue;
                    }

                    LiveCpuRoot childRoot=default!;
                    string? sameResourceDiagnostic=null;
                    //009A3AB0 asks the global manager before loading a file.
                    //A local sibling must not shadow an existing registration.
                    bool foundRegisteredAttachment=_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out var registeredAttachment);
                    var sameResourceSource = foundRegisteredAttachment
                        ? registeredAttachment with {RequestedObjectName=authored.SubobjectName} : new EnbResolvedW3dResource(
                        renderSource.LogicalFileName,
                        authored.SubobjectName,
                        renderSource.ResourceLocation,
                        renderSource.Model,
                        renderSource.Textures);
                    var sameResourceKey = new LiveCpuRootKey(
                        key.BaseAsset,
                        key.RslStateId,
                        sameResourceSource.LogicalFileName,
                        sameResourceSource.RequestedObjectName);
                    //A removed global prototype cannot be resurrected from a
                    //registered base's old decoded siblings.009A3AB0 must run
                    //ordinary acquisition and publish its lazy FILE again.
                    if ((!foundRegisteredAttachment && aggregate.BasePrototypeRegistration>0) || !TryGetCpuRootCore(
                            sameResourceKey,
                            sameResourceSource,
                            retailAssets,
                            aggregateStack,
                            depth + 1,
                            out childRoot,
                            out sameResourceDiagnostic))
                    {
                        bool TryAttachmentFile(bool fullName,out LiveCpuRoot loadedRoot,out string? failure)
                        {
                            loadedRoot=default!;
                            EnbResolvedW3dResource publication;
                            bool decoded=fullName
                                ? retailAssets.W3dFiles.TryResolveAggregateFile(authored.SubobjectName,out publication,out failure)
                                : retailAssets.W3dFiles.TryResolveLazyFile(authored.SubobjectName,out publication,out failure);
                            if(!decoded || !_prototypeResources.TryLoadFile(publication,out _,out failure,ReadMapperClock()))return false;
                            if(!_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out var childSource))
                            {
                                //009A3AE0 performs another ordinary acquisition,
                                //including its lazy FILE when the full-name FILE
                                //did not publish the requested child.
                                if(!fullName || !retailAssets.W3dFiles.TryResolveLazyFile(authored.SubobjectName,out var retryPublication,out failure) ||
                                    !_prototypeResources.TryLoadFile(retryPublication,out _,out failure,ReadMapperClock()) ||
                                    !_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out childSource))return false;
                            }
                            var childKey=new LiveCpuRootKey(key.BaseAsset,key.RslStateId,childSource.LogicalFileName,authored.SubobjectName);
                            return TryGetCpuRootCore(childKey,childSource,retailAssets,aggregateStack,depth+1,out loadedRoot,out failure);
                        }
                        //009A3AB0 first uses ordinary acquisition;009A3AD0
                        //then retries the full authored name in the working
                        //directory after creation fails, even for dotted names.
                        string? lazyDiagnostic=null;
                        bool acquired=!_prototypeResources.TryFindPrototypeSource(authored.SubobjectName,out _) &&
                            TryAttachmentFile(false,out childRoot,out lazyDiagnostic);
                        if(!acquired && !TryAttachmentFile(true,out childRoot,out var retryDiagnostic))
                        {
                            //009A3A75 omits this child and continues later siblings.
                            unavailableAttachments.Add($"0x0600 aggregate '{aggregate.RenderDefinition.Name}' subobject {index} '{authored.SubobjectName}' on bone '{authored.BoneName}' unavailable: base={sameResourceDiagnostic}; lazy={lazyDiagnostic}; full-name={retryDiagnostic}.");
                            continue;
                        }
                    }

                    //9A3AB0 acquires through manager+14 before insertion,
                    //including shared model postprocessing for discarded children.
                    if(childRoot.IsDirectMeshPrototype)
                        foreach(var mesh in childRoot.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,childRoot.RootRenderObjectName)))
                            PostprocessOrdinaryNamedMesh(mesh);

                    //009A3A50 still creates the child before insertion. NULL,
                    //Mesh, Box, Dazzle and Collection use inherited no-op slots.
                    //The +80 wrapper calls +B0(00462BD0) and +84(00462AD0), both
                    //no-ops, then the caller releases its temporary child.
                    if(discardsAggregateChildren)
                    {
                        CollectDiscardedParticleConstructions(childRoot,discardedParticles);
                        continue;
                    }
                    materialized.Add(new LiveAggregateAttachment(
                        authored.SubobjectName,
                        authored.BoneName,
                        childRoot,
                        IsRetailNoOp: false));
                }
                attachments = materialized.ToArray();
            }

            if(hlod is not null && attachments.Any(value=>value.Root is not null ||
                string.Equals(value.SubobjectName,"NULL",StringComparison.OrdinalIgnoreCase)))
            {
                //009A3A50/+80 appends these objects to the same HLOD child
                //storage used by0098F1D0 bounds and0098DCA0 cost. Keeping them
                //only in a parallel draw list omitted them from those queries.
                var tree=resource.Hierarchies.First(value=>string.Equals(value.Name,attachmentHierarchyName,StringComparison.OrdinalIgnoreCase));
                var members=new List<EnbW3dRenderHlodMember>(hlod.Aggregate?.Members??[]);
                var retained=new List<LiveHmodelChild>(hmodelChildren);
                var descriptors=new List<EnbW3dRenderCompositeChild>(resource.CompositeRenderChildren);
                var external=new List<EnbW3dExternalRenderObject>(resource.ExternalRenderObjects);
                var specialized=new List<LiveAggregateAttachment>();
                int levelMembers=hlod.Levels.Sum(value=>value.Members.Count);
                for(int index=0;index<attachments.Count;index++)
                {
                    var attachment=attachments[index];
                    uint pivot=checked((uint)EnbCurrentRetailAggregateBoneLookup.Resolve(tree,attachment.BoneName));
                    if(attachment.Root is {} child)
                    {
                        //A unique internal key represents the native child
                        //pointer, without shadowing a same-named inline mesh.
                        string memberName=$"<0600:{aggregate!.Definition.Name}:{index}:{attachment.SubobjectName}>";
                        if(!TryDescribeCompositeChild(child,memberName,out var descriptor,out diagnostic)){root=default!;return false;}
                        retained.Add(new(levelMembers+members.Count,pivot,child,null,true));
                        descriptors.Add(descriptor);members.Add(new(pivot,memberName));
                    }
                    else if(string.Equals(attachment.SubobjectName,"NULL",StringComparison.OrdinalIgnoreCase))
                    {
                        if(!external.Any(value=>string.Equals(value.Name,"NULL",StringComparison.OrdinalIgnoreCase)))
                            external.Add(new("NULL",EnbW3dExternalRenderObjectKind.NullObject,resource.LogicalFileName,resource.Key.ArchivePath));
                        members.Add(new(pivot,"NULL"));
                    }
                    else specialized.Add(attachment);
                }
                var previous=hlod;hlod=hlod with {Aggregate=new(hlod.Aggregate?.RawValue??0,members.ToArray())};
                resource=resource.Copy().SetHierarchicalLods(resource.HierarchicalLods.Select(value=>ReferenceEquals(value,previous)?hlod:value).ToArray()).SetCompositeRenderChildren(descriptors.ToArray()).SetExternalRenderObjects(external.ToArray());
                hmodelChildren=retained.ToArray();attachments=specialized.ToArray();
            }

            // Ordinary mesh stages reach the same 009254ED -> 0094BC80 ->
            // 0094B920 loader as effects. A named failed file binds 009AF0F0's
            // executable image; it is not the explicit unbound-stage sentinel.
            if(resource.Meshes.SelectMany(mesh=>mesh.MaterialPasses).Any(pass=>
                pass.TextureStages.SelectMany(stage=>stage.Textures).Any(texture=>texture.IsMissing) ||
                pass.TriangleBindings.SelectMany(triangle=>triangle.Textures).Any(texture=>texture?.IsMissing==true)))
                resource=EnbCurrentRetailMeshTexturePreparation.Prepare(resource,retailAssets,missingResourcesOnly:true);

            //Dazzle inherits GetName004628D0 ("UNNAMED") and no-op SetName.
            //Configured NULL's SetName(+14,004628F0) also preserves its name.
            string runtimeRenderObjectName = dazzleRoot ? "UNNAMED" : nullAggregateBase ? requestedRenderObjectName : aggregate?.RenderDefinition.Name ?? requestedRenderObjectName;
            string? runtimeHAnimHierarchyIdentity = resource.Hierarchies.FirstOrDefault(tree =>
                string.Equals(tree.Name, attachmentHierarchyName, StringComparison.OrdinalIgnoreCase))?.RuntimeName;
            bool overbrightOff = retailAssets.PresentationCatalog?
                .ResolveBaseAsset(key.BaseAsset)
                .OverbrightOff == true;
            root = new LiveCpuRoot(
                overbrightOff,
                resource,
                hlod,
                allowed,
                requestedRenderObjectName,
                runtimeRenderObjectName,
                runtimeHAnimHierarchyIdentity,
                attachmentHierarchyName,
                attachments) { Collection=collection,CollectionChildren=collectionChildren.ToArray(),HmodelChildren=hmodelChildren,AuthoredGlobalOwner=authoredGlobalOwner,
                    IsDirectMeshPrototype=directMeshPrototype,
                    ForceSubObjectLod=aggregate?.RenderDefinition.ForceSubObjectLod==true,
                    UnavailableAttachments=unavailableAttachments.ToArray(),DiscardedParticleConstructions=discardedParticles,
                    FactoryLookupName=source.RequestedObjectName };
            _cpuRoots[key] = root;
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            // Optional external HLOD assets (including unsupported authored
            // dazzles) must reject this root, not abort the entire scene frame.
            // Callers already retain the diagnostic and continue other roots.
            root = default!;
            diagnostic = $"render-root '{requestedRenderObjectName}' asset rejected: {ex.Message}";
            return false;
        }
        finally
        {
            if (aggregateStackKey is not null)
                aggregateStack.Remove(aggregateStackKey);
        }
    }

    private static bool HasCurrentRetailShipLegoComposition(IEnbRenderObject obj)
        => !obj.LegoControllerMutations.IsDefaultOrEmpty || !obj.SubParts.IsDefaultOrEmpty ||
           (obj.ShipLegos is EnbSceneShipLegoSnapshot legos && !legos.Attachments.IsDefaultOrEmpty);

    private bool TryAppendCurrentRetailShipLegoInstances(
        IEnbRenderObject obj,
        LiveCpuRoot? parentRoot,
        LiveRslAnimationBlendBinding? parentAnimationBlend,
        EnbRetailAssetBundle retailAssets,
        EnbRetailBaseAssetResolver resolver,
        RootTaskList destination,
        List<ShipAttachmentOwner> fixedSearchNodes,
        LiveShipLegoFrameTracker tracker,
        out string? diagnostic)
    {
        diagnostic = null;
        tracker.ObjectsSeen++;
        var retainedRadius=_buildingSceneOwner is {} radiusSource&&radiusSource.Definition.RenderIdentity==obj.RenderIdentity?radiusSource.LegoControllers:null;
        if(!_preparingLegoRadiusQueries)
        {
            if(_buildingSceneOwner is null||_buildingSceneOwner.Definition.RenderIdentity!=obj.RenderIdentity)(_shipExhaustOwner??this)._legoControllers.TryGetValue(obj.RenderIdentity,out retainedRadius);
            if(retainedRadius?.RadiusDiagnostic is {} radiusDiagnostic)tracker.FirstDiagnostic??=radiusDiagnostic;
        }

        EnbClientBaseAssetMountCatalog? mounts = retailAssets.ClientBaseAssetMounts;
        if (mounts is null)
            return RejectCurrentRetailShipLego(obj, tracker, "installed cbasset.ini mount catalog is unavailable", out diagnostic);
        if (!EnbCurrentRetailShipLegoRuntime.TryBuildRigidObjectWorld(
                obj,
                out Matrix4x4 rootRigidWorld,
                out diagnostic))
        {
            return RejectCurrentRetailShipLego(obj, tracker, diagnostic ?? "owning ship rigid transform is unavailable", out diagnostic);
        }

        var retainedSpecs=RetainedLegoSpecs(obj);
        var physicalSpecs=PhysicalLegoSpecs(obj,retailAssets,out var physicalRoot);
        var drawSource=!_isolatedLegoNotification&&_buildingSceneOwner is {} source&&source.Definition.RenderIdentity==obj.RenderIdentity?source:null;
        var specPlan=drawSource is not null&&retainedSpecs is not null
            ?drawSource.LegoDraw??=new LegoDrawPlan(physicalSpecs??retainedSpecs,physicalSpecs is not null):null;
        var specs=specPlan?.Specs??new List<LiveShipLegoSpec>();
        if(specPlan is null)
        {
            if(physicalSpecs is not null)specs.AddRange(physicalSpecs);
            else if(retainedSpecs is not null)specs.AddRange(retainedSpecs);
        }
        if (retainedSpecs is null && obj.ShipLegos is EnbSceneShipLegoSnapshot legos)
        {
            foreach (EnbSceneShipLegoAttachmentSnapshot attachment in legos.Attachments)
            {
                // Current retail's factory returns null for unrecognized Lego
                // types. Assets <= 0 likewise produces no BaseAsset RenderObj.
                if (attachment.Type is not (1 or 2 or 3) || attachment.BaseAsset <= 0)
                {
                    tracker.NoOpAttachments++;
                    continue;
                }
                if (!TryParseShipLegoPath(attachment.BoneName, out string[] path, out diagnostic))
                {
                    //72B7F0 rejects this child's invalid split;732790 continues
                    //processing later Aux entries instead of dropping siblings.
                    RejectCurrentRetailShipLego(obj, tracker, diagnostic!, out diagnostic);
                    continue;
                }
                specs.Add(new LiveShipLegoSpec(
                    Source: "aux",
                    Ordinal: attachment.Ordinal,
                    Path: path,
                    Type: attachment.Type,
                    BaseAsset: attachment.BaseAsset,
                    Data: attachment.Data) {MaterializationRevision=attachment.MaterializationRevision});
                tracker.AuxAttachments++;
            }
        }

        for (int ordinal = 0; retainedSpecs is null && ordinal < obj.SubParts.Length; ordinal++)
        {
            EnbSceneSubPartSnapshot part = obj.SubParts[ordinal];
            if (part.PartValue == 0)
            {
                tracker.NoOpAttachments++;
                continue;
            }
            if (part.PartValue > int.MaxValue)
            {
                RejectCurrentRetailShipLego(obj, tracker, $"0x00B4 SubParts[{ordinal}] BaseAsset 0x{part.PartValue:X8} exceeds current signed BaseAsset range.", out diagnostic);
                continue;
            }
            if (!TryParseShipLegoPath(part.Name, out string[] path, out diagnostic))
            {
                RejectCurrentRetailShipLego(obj, tracker, diagnostic!, out diagnostic);
                continue;
            }

            specs.Add(new LiveShipLegoSpec(
                Source: "0x00B4",
                Ordinal: ordinal,
                Path: path,
                Type: 1,
                BaseAsset: (int)part.PartValue,
                Data: string.Empty) {MaterializationRevision=part.MaterializationRevision});
            tracker.SubPartAttachments++;
        }

        if (specs.Count == 0 && physicalRoot is null)
        {
            tracker.ComposedObjects++;
            return true;
        }

        if(specPlan is null)ResolveLegoPathReplacement(obj,specs);
        var duplicatePath=specPlan is not null?specPlan.DuplicatePath:specs
            .GroupBy(static spec=>spec.PhysicalDrawPath??spec.CanonicalPath,StringComparer.Ordinal)
            .FirstOrDefault(static group=>group.Count()>1)?.Key;
        if(duplicatePath is not null)
        {
            return RejectCurrentRetailShipLego(obj, tracker,
                $"multiple authoritative Lego sources target BoneName chain '{duplicatePath}'; source precedence is not encoded in the detached snapshot.", out diagnostic);
        }

        EnbW3dRenderHierarchy? rootHierarchy=null;
        if (parentRoot is not null && !TryResolveConfiguredAttachmentHierarchy(parentRoot, out rootHierarchy, out diagnostic))
        {
            RejectCurrentRetailShipLego(obj, tracker, diagnostic!, out diagnostic);
        }

        LiveRootInstance? rootInstance=null;
        int rootInstanceCount=0;
        foreach(var instance in destination)
            if(ReferenceEquals(instance.Root,parentRoot) && instance.InstancePath=="root")
            {rootInstance=instance;rootInstanceCount++;}
        if (rootHierarchy is not null && rootInstanceCount != 1)
        {
            return RejectCurrentRetailShipLego(
                obj,
                tracker,
                $"root BASE-{obj.BaseAsset} has {rootInstanceCount} current top-level RenderObject instances; R9 cannot bind one hierarchy state-record owner.",
                out diagnostic);
        }

        // Compatibility specs still install overrides during this walk and
        // need writable local dictionaries. Fully physical membership reads
        // the concrete model's mutation-published binding directly.
        bool sharePhysicalOverrides=physicalSpecs is not null && !specs.Any(spec=>spec.PhysicalModel is null);
        var rootOverrideBinding=sharePhysicalOverrides?physicalRoot?.GetOverrideBinding():null;
        var rootHierarchyOverrides = rootOverrideBinding?.Definitions ??
            physicalRoot?.Overrides.ToDictionary(p=>p.Key,p=>p.Value) ?? new();
        var rootDrawHierarchyOverrides = rootOverrideBinding?.Draw ?? rootHierarchyOverrides.ToDictionary(p=>p.Key,p=>p.Value.Transform);
        using var parentFrame=specPlan?.Rent();
        var parents=specPlan is null?new Dictionary<string,ShipAttachmentOwner>(StringComparer.Ordinal):null;
        void SetParent(string path,ShipAttachmentOwner value,int slot)
        {if(parentFrame is not null)parentFrame.Parents[slot]=value;else parents![path]=value;}
        ShipAttachmentOwner? GetParent(string path,int slot)
            =>parentFrame is not null?(slot<0?null:parentFrame.Parents[slot]):parents!.GetValueOrDefault(path);
        if(rootHierarchy is not null && parentRoot is not null)
        {
            rootInstance!.SetHierarchyState(
                rootHierarchy.Name,
                rootRigidWorld,
                obj.Scale,
                rootDrawHierarchyOverrides);

            SetParent(string.Empty,PrepareLegoAttachment(
                    BaseAsset: obj.BaseAsset,
                    Root: parentRoot,
                    Hierarchy: rootHierarchy,
                    RigidWorld: rootRigidWorld,
                    Scale: obj.Scale,
                    Animations: parentAnimationBlend?.ResolvedSamples,
                    HierarchyOverrides: rootHierarchyOverrides,
                    DrawHierarchyOverrides: rootDrawHierarchyOverrides,
                    Instance: rootInstance!),0);
            if (!fixedSearchNodes.Any(static node => node.Path.Length == 0))
            {
                fixedSearchNodes.Add(PrepareFixedAttachment(
                    Path: string.Empty,
                    Root: parentRoot,
                    Hierarchy: rootHierarchy,
                    RigidWorld: rootRigidWorld,
                    Scale: obj.Scale,
                    Animations: parentAnimationBlend?.ResolvedSamples,
                    HierarchyOverrides: GetParent(string.Empty,0)!.HierarchyOverrides.Values,
                    Instance: rootInstance!, DrawOverrides: rootDrawHierarchyOverrides));
            }
        }

        IEnumerable<LiveShipLegoSpec> orderedSpecs=specPlan is not null?specPlan.Ordered:physicalSpecs is not null?specs:specs
            .OrderBy(item=>item.Path.Length).ThenBy(item=>item.Source,StringComparer.Ordinal).ThenBy(item=>item.Ordinal);
        int childSlot=0;
        foreach(LiveShipLegoSpec spec in orderedSpecs)
        {
            childSlot++;
            string compositionPath=spec.PhysicalDrawPath??spec.CanonicalPath;
            bool logicalController=spec.PhysicalModel is null || retainedSpecs?.Any(s=>ReferenceEquals(s,spec.PhysicalModel.Spec))==true;
            string parentPath=spec.PhysicalParentPath??spec.LogicalParentPath;
            string mountBoneName = spec.Path[^1];
            var parent=GetParent(parentPath,specPlan?.ParentSlots[childSlot-1]??-1);
            EnbClientBaseAssetMount mount=default!;
            bool canAttach=parent is not null && mounts.TryResolveMount(parent.BaseAsset,mountBoneName,out mount);
            var childFactory=ResolveLegoBaseFactory(obj,spec,retailAssets);
            EnbBaseAssetResolution childResolution = childFactory?.Resolution ?? ResolveBaseAsset(resolver, spec.BaseAsset);
            if(childFactory is {Root:null})
            {
                RejectCurrentRetailShipLego(obj,tracker,$"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset}: {childFactory.Diagnostic??childResolution.Diagnostic??childResolution.Status.ToString()}.",out diagnostic);
                continue;
            }
            var childW3d=childResolution.W3d;
            if (childResolution.Status != EnbBaseAssetResolutionStatus.W3d ||
                (childW3d is null && childFactory?.Root is null))
            {
                (_shipExhaustOwner??this)._legoRslOwners.TryGetValue(
                    (obj.RenderIdentity,spec.Source,spec.Ordinal,spec.MaterializationRevision,spec.BaseAsset,spec.CanonicalPath),out var rslParent);
                if(rslParent is null || rslParent.Update is null)
                {
                    RejectCurrentRetailShipLego(obj,tracker,$"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} has no retained RSL factory.",out diagnostic);
                    continue;
                }
                var wrapperObject=rslParent.Invocation(_shipExhaustOwner is not null);
                if(canAttach && parent is not null)
                {
                    if(physicalSpecs is null)
                    {
                        EnbW3dRenderHierarchy? mountedHierarchy=null;
                        if(rslParent.QueryRoot is {} selected)
                            TryResolveConfiguredAttachmentHierarchy(selected.Root,out mountedHierarchy,out _);
                        if(EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(mount,mountedHierarchy,1,
                            out var captured,out _,out _))
                        {
                            parent.HierarchyOverrides[captured.PivotIndex]=captured;
                            parent.DrawHierarchyOverrides[captured.PivotIndex]=captured.Transform;
                        }
                    }
                    if(!EnbCurrentRetailShipLegoRuntime.TryResolveParentMountWorld(parent.Hierarchy,parent.RigidWorld,
                        ResolvePendingHierarchyScale(parent.Instance!,parent.Scale),mount with {BoneIndex=EnbCurrentRetailAggregateBoneLookup.Resolve(parent.Hierarchy,mount.BoneName)},
                        parent.Animations,parent.HierarchyOverrides.Values,out var wrapperWorld,out diagnostic))continue;
                    var spatial=(EnbRenderSpatialState)wrapperObject.Spatial!;
                    spatial.SetWorldTransform(spatial.Transform,wrapperWorld);
                }
                else tracker.PendingAttachments++;
                var queries=new RootTaskList();
                var model=ResolveRslSourceQueryRoot(wrapperObject,rslParent.Reference,rslParent.Update,retailAssets,
                    rslParent.Time,out var queryDiagnostic,queries);
                if(_shipExhaustOwner is null)
                {rslParent.QueryRoot=model;rslParent.QueryDiagnostic=queryDiagnostic;}
                if(model is null)continue;
                bool inScene=canAttach && parent?.InScene==true;
                bool hidden=parent?.SuppressDraw==true || rslParent.Update.CurrentObjectHidden;
                model.Parent=canAttach?parent!.Instance:null;
                model.IsExternalLegoChild=canAttach;
                model.IsExternalRslModel=true;
                if(canAttach)model.OrdinaryHlodPivot=(uint)EnbCurrentRetailAggregateBoneLookup.Resolve(parent!.Hierarchy,mount.BoneName);
                var world=model.WorldTransformOverride??(TryBuildObjectWorld(wrapperObject,out var pose)?pose:Matrix4x4.Identity);
                var retainedModel=CurrentLegoPhysicalModel(rslParent);
                var overrideBinding=sharePhysicalOverrides?retainedModel?.GetOverrideBinding():null;
                var overrides=overrideBinding?.Definitions??retainedModel?.Overrides.ToDictionary(p=>p.Key,p=>p.Value)??[];
                var drawOverrides=overrideBinding?.Draw??overrides.ToDictionary(p=>p.Key,p=>p.Value.Transform);
                if(TryResolveConfiguredAttachmentHierarchy(model.Root,out var hierarchy,out _) && hierarchy is not null)
                {
                    model.SetHierarchyState(hierarchy.Name,world,1,drawOverrides);
                    SetParent(compositionPath,PrepareLegoAttachment(spec.BaseAsset,model.Root,hierarchy,world,1,
                        model.AnimationBlend?.ResolvedSamples,overrides,drawOverrides,model)
                        .SetScene(inScene,hidden),childSlot);
                }
                if(inScene)
                {
                    if(hidden)_hiddenRslEmitterRoots.AddRange(queries);
                    else destination.AddRange(queries);
                    tracker.ChildRoots++;
                }
                continue;
            }
            LiveCpuRoot childRoot=childFactory?.Root!;
            string? childDiagnostic=childFactory?.Diagnostic;
            if (childFactory is not null ? childRoot is null : !TryGetCpuRoot(
                    new LiveCpuRootKey(spec.BaseAsset, -1, childW3d!.LogicalFileName, childW3d.RequestedObjectName),
                    childW3d,
                    retailAssets,
                    out childRoot,
                    out childDiagnostic))
            {
                if(!canAttach){tracker.PendingAttachments++;continue;}
                RejectCurrentRetailShipLego(obj, tracker,
                    $"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} could not materialize its W3D root: {childDiagnostic ?? "unknown root rejection"}.", out diagnostic);
                continue;
            }
            childRoot=RetainLegoParticleFactory(obj,spec,childRoot);
            float childFactoryScale=childRoot.ConfiguredFactoryInputs?.Scale??EnbCurrentRetailShipLegoRuntime.LegoControllerCreationScale;
            if(parentPath.Length==0 && parentRoot is not null && !HasNoOpLegoMounts(parentRoot) && rootHierarchy is null &&
                mounts.TryResolveMount(obj.BaseAsset,mountBoneName,out _))
            {
                //The factory exists, but this nonhierarchical parent's concrete
                //bone-attachment virtuals are not established by the HLOD path.
                RejectCurrentRetailShipLego(obj,tracker,$"Lego '{spec.CanonicalPath}' has a mount on a nonhierarchical root; attachment remains unresolved.",out diagnostic);
                continue;
            }
            if(childRoot.PhysicalFactoryOwner is {} queryOwner && !_legoSourceInstances.ContainsKey(queryOwner))
                _legoSourceInstances.Add(queryOwner,new LiveRootInstance(obj,childRoot,false,
                    $"lego/{spec.CanonicalPath}",Matrix4x4.Identity)
                    {IsShipLegoController=true,LegoControllerBaseAsset=spec.BaseAsset});
            if (!canAttach || parent is null)
            {
                //72B7F0 retains an orphan/null-mount child after construction.
                //A plain Mesh also retains it when cbasset has a mount:
                //B111A4 v+BC/C8 are no-ops; v+80 ->941F60 ->v+84
                //(462AD0) returns zero without parenting or moving the child.
                tracker.PendingAttachments++;
                //Its own hierarchy can still receive deeper children. Scene
                //admission follows parentage; controller membership does not.
                if(childRoot.PhysicalFactoryOwner is {} detachedOwner &&
                    _legoSourceInstances.TryGetValue(detachedOwner,out var detached) &&
                    TryResolveConfiguredAttachmentHierarchy(childRoot,out var detachedHierarchy,out _) && detachedHierarchy is not null)
                {
                    var detachedWorld=detached.HierarchyPostTransforms?.RigidWorldTransform??detached.WorldTransformOverride??Matrix4x4.Identity;
                    var overrideBinding=sharePhysicalOverrides?spec.PhysicalModel?.GetOverrideBinding():null;
                    var overrides=overrideBinding?.Definitions??spec.PhysicalModel?.Overrides.ToDictionary(p=>p.Key,p=>p.Value)??[];
                    var drawOverrides=overrideBinding?.Draw??overrides.ToDictionary(p=>p.Key,p=>p.Value.Transform);
                    detached.SetHierarchyState(detachedHierarchy.Name,detachedWorld,childFactoryScale,drawOverrides);
                    SetParent(compositionPath,PrepareLegoAttachment(spec.BaseAsset,childRoot,detachedHierarchy,detachedWorld,childFactoryScale,
                        detached.AnimationBlend?.ResolvedSamples,overrides,drawOverrides,detached).SetScene(false,false),childSlot);
                }
                continue;
            }
            if (!TryResolveConfiguredAttachmentHierarchy(childRoot, out EnbW3dRenderHierarchy? childHierarchy, out childDiagnostic))
            {
                RejectCurrentRetailShipLego(obj, tracker,
                    $"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} hierarchy resolution failed: {childDiagnostic ?? "unknown hierarchy rejection"}.", out diagnostic);
                continue;
            }

            if (!EnbCurrentRetailShipLegoRuntime.TryCreateParentHierarchyOverride(
                    mount,
                    childHierarchy,
                    childRoot.ConfiguredHierarchyTranslations is null?childFactoryScale:1,
                    out EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride hierarchyOverride,
                    out bool usedMissingAttachFallback,
                    out string? placementDiagnostic))
            {
                RejectCurrentRetailShipLego(obj, tracker,
                    $"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} placement failed: {placementDiagnostic ?? "unknown transform rejection"}.", out diagnostic);
                continue;
            }
            // FUN_0072D0E0 writes one mode-0 override into the selected parent
            // hierarchy record before attaching the child RenderObject. The
            // record is single-valued, so a later attach to the same BoneIndex
            // replaces the earlier matrix exactly as retail does.
            if(spec.PhysicalModel is null)
            parent.HierarchyOverrides[hierarchyOverride.PivotIndex] = hierarchyOverride;
            if(spec.PhysicalModel is null)
            {
                parent.DrawHierarchyOverrides[hierarchyOverride.PivotIndex] = hierarchyOverride.Transform;
                parent.AttachmentPose = null;
            }
            if (!TryResolveOwnedLegoMount(parent, mount,
                    out Matrix4x4 childRigidWorld, out placementDiagnostic))
            {
                RejectCurrentRetailShipLego(obj, tracker,
                    $"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} parent hierarchy evaluation failed: {placementDiagnostic ?? "unknown transform rejection"}.", out diagnostic);
                continue;
            }

            Matrix4x4 childDrawWorld;
            try
            {
                // 0098E4B0 copies LOD bias through v+12C, not geometry scale.
                // 00729770 creates this child at controller scale 1; attaching
                // it does not repeat the parent's earlier factory v+164 scale.
                childDrawWorld = EnbCurrentRetailShipLegoRuntime.CreateAttachedChildDrawWorld(childRigidWorld);
            }
            catch (ArgumentException ex)
            {
                RejectCurrentRetailShipLego(obj, tracker, $"Lego '{spec.CanonicalPath}' draw transform rejected: {ex.Message}", out diagnostic);
                continue;
            }

            if(spec.PhysicalModel is null)childRoot=ApplyLegoInheritedLodBias(obj,spec,childRoot,parent.Root.LodBias);
            var childOverrideBinding=sharePhysicalOverrides?spec.PhysicalModel?.GetOverrideBinding():null;
            var childHierarchyOverrides = childOverrideBinding?.Definitions ??
                spec.PhysicalModel?.Overrides.ToDictionary(p=>p.Key,p=>p.Value) ?? new();
            var childDrawHierarchyOverrides = childOverrideBinding?.Draw ?? childHierarchyOverrides.ToDictionary(p=>p.Key,p=>p.Value.Transform);

            var childAnimation = parent.InScene
                ? ResolveShipWarpAnimation(obj, childRoot, $"lego/{spec.CanonicalPath}", childRigidWorld.Translation)
                : childRoot.PhysicalFactoryOwner is {} retainedOwner && _legoSourceInstances.TryGetValue(retainedOwner,out var retainedChild)
                    ? retainedChild.AnimationBlend : null;
            var childInstance=RentRootInvocation(obj,childRoot,false,$"lego/{spec.CanonicalPath}",
                childDrawWorld,childAnimation,false,logicalController,spec.BaseAsset);
            childInstance.Parent=parent.Instance;
            childInstance.IsExternalLegoChild=true;
            childInstance.OrdinaryHlodPivot=(uint)EnbCurrentRetailAggregateBoneLookup.Resolve(parent.Hierarchy,mount.BoneName);
            if (childHierarchy is not null)
            {
                childInstance.SetHierarchyState(
                    childHierarchy.Name,
                    childRigidWorld,
                    childFactoryScale,
                    childDrawHierarchyOverrides);
            }
            if(parent.InScene)(parent.SuppressDraw?_hiddenRslEmitterRoots:destination).Add(childInstance);
            if(childRoot.PhysicalFactoryOwner is {} attachedQueryOwner)_legoSourceInstances[attachedQueryOwner]=childInstance;
            tracker.ChildRoots++;
            if (usedMissingAttachFallback)
                tracker.MissingAttachFallbacks++;

            if (!_preparingLegoRadiusQueries && parent.InScene && (childRoot.AggregateAttachments.Count != 0 || childRoot.HmodelChildren.Count != 0 || childRoot.CollectionChildren.Count != 0) &&
                !TryAppendCurrentRetailShipAggregateDescendants(
                    obj,
                    childRoot,
                    childRigidWorld,
                    childFactoryScale,
                    $"lego/{spec.CanonicalPath}",
                    parent.SuppressDraw?_hiddenRslEmitterRoots:destination,
                    fixedSearchNodes,
                    tracker,
                    childHierarchyOverrides.Values,
                    depth: 0,
                    out string? aggregateDiagnostic,
                    childInstance))
            {
                RejectCurrentRetailShipLego(obj, tracker,
                    $"Lego '{spec.CanonicalPath}' BASE-{spec.BaseAsset} aggregate composition failed: {aggregateDiagnostic ?? "unknown aggregate rejection"}.", out diagnostic);
                continue;
            }

            // Slash-delimited BoneName chains recurse into an already-created
            // Lego child. Its own cbasset BoneIndex lookup therefore uses this
            // child's BaseAsset, hierarchy, rigid placement, and its own
            // factory geometry scale.
            // A child with no hierarchy can still be drawn, but cannot serve as
            // the parent of a deeper slash segment because there is no BoneIndex
            // transform source to query.
            if (childHierarchy is not null)
            {
                SetParent(compositionPath,PrepareLegoAttachment(
                    spec.BaseAsset,
                    childRoot,
                    childHierarchy,
                    childRigidWorld,
                    childFactoryScale,
                    Animations: childAnimation?.ResolvedSamples,
                    HierarchyOverrides: childHierarchyOverrides,
                    DrawHierarchyOverrides: childDrawHierarchyOverrides,
                    Instance: childInstance).SetScene(parent.InScene,parent.SuppressDraw),childSlot);
                if(parent.InScene && logicalController)fixedSearchNodes.Add(PrepareFixedAttachment(
                    Path: spec.CanonicalPath,
                    Root: childRoot,
                    Hierarchy: childHierarchy,
                    RigidWorld: childRigidWorld,
                    Scale: childFactoryScale,
                    Animations: childAnimation?.ResolvedSamples,
                    HierarchyOverrides: GetParent(compositionPath,childSlot)!.HierarchyOverrides.Values,
                    Instance: childInstance, DrawOverrides: childDrawHierarchyOverrides));
            }
        }

        tracker.ComposedObjects++;
        return true;

    }

    private bool TryAppendCurrentRetailShipAggregateDescendants(
        IEnbRenderObject obj,
        LiveCpuRoot aggregateRoot,
        Matrix4x4 aggregateRigidWorld,
        float aggregateScale,
        string instancePath,
        RootTaskList destination,
        List<ShipAttachmentOwner> fixedSearchNodes,
        LiveShipLegoFrameTracker tracker,
        IReadOnlyCollection<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride>? hierarchyOverrides,
        int depth,
        out string? diagnostic,
        LiveRootInstance parentInstance)
    {
        const int maximumAggregateDepth = 8;
        diagnostic = null;
        if((aggregateRoot.HmodelChildren.Count!=0||aggregateRoot.CollectionChildren.Count!=0) &&
            !TryAppendShipRetainedCompositeChildren(parentInstance,Matrix4x4.CreateScale(aggregateScale)*aggregateRigidWorld,
                destination,fixedSearchNodes,tracker,out diagnostic))return false;
        if (aggregateRoot.AggregateAttachments.Count == 0)
            return true;
        if (depth > maximumAggregateDepth)
        {
            diagnostic = $"ship aggregate attachment depth exceeds {maximumAggregateDepth}.";
            tracker.AggregateRejected++;
            return false;
        }

        tracker.AggregateWrappers++;
        if (!TryResolveConfiguredAttachmentHierarchy(
                aggregateRoot,
                out EnbW3dRenderHierarchy? hierarchy,
                out diagnostic) ||
            hierarchy is null)
        {
            diagnostic ??= $"aggregate RenderObj '{aggregateRoot.RuntimeRenderObjectName}' has no attachment hierarchy.";
            tracker.AggregateRejected++;
            return false;
        }

        if (!EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
                hierarchy,
                aggregateRigidWorld,
                ResolvePendingHierarchyScale(parentInstance,aggregateScale),
                animations: null,
                hierarchyOverrides,
                out Matrix4x4[] pose,
                out diagnostic))
        {
            tracker.AggregateRejected++;
            return false;
        }

        ObservePlanetExhaust(obj, hierarchy, pose, instancePath);
        for (int attachmentIndex = 0; attachmentIndex < aggregateRoot.AggregateAttachments.Count; attachmentIndex++)
        {
            LiveAggregateAttachment attachment = aggregateRoot.AggregateAttachments[attachmentIndex];
            if(attachment.RslChild is {Parent:null})continue;
            if (UsesPlanetExhaust(obj) && EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(attachment.BoneName)) continue;
            int pivotIndex=EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,attachment.BoneName);
            if (pivotIndex >= pose.Length)
            {
                diagnostic =
                    $"aggregate RenderObj '{aggregateRoot.RuntimeRenderObjectName}' attachment '{attachment.SubobjectName}' requires a nonempty hierarchy '{hierarchy.Name}'.";
                tracker.AggregateRejected++;
                return false;
            }

            EnbCurrentRetailShipExhaustVisibility exhaustVisibility =
                EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(obj);
            bool isExhaustAttachment =
                obj.ObjectType is 0 or 1 or 25 &&
                EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(attachment.BoneName);
            if (isExhaustAttachment)
            {
                switch (exhaustVisibility.Decision)
                {
                    case EnbCurrentRetailShipExhaustVisibilityDecision.Hidden:
                        tracker.ExhaustHiddenAttachments++;
                        break;
                    case EnbCurrentRetailShipExhaustVisibilityDecision.Visible:
                        tracker.ExhaustVisibleAttachments++;
                        break;
                }
            }

            // Current retail aggregate setup creates each authored subobject
            // through the asset manager and immediately attaches it to the
            // aggregate base RenderObj by bone name. There is no Lego ATTACH
            // inverse and no child scale write on this path, so the child keeps
            // its default BaseAsset scale 1.0 and inherits the evaluated parent
            // bone world directly.
            if (attachment.Sphere is { } sphere)
            {
                ObserveWorldSphere(obj, sphere, pose[pivotIndex], $"{instancePath}/aggregate/{attachmentIndex}:{attachment.SubobjectName}",parent:parentInstance);
                continue;
            }
            if (attachment.IsRetailNoOp)
            {
                tracker.AggregateNoOps++;
                ObserveShipExhaust(obj, attachment, pose[pivotIndex], hierarchy.Name, pivotIndex,
                    $"{instancePath}/aggregate/{attachmentIndex}:{attachment.SubobjectName}",physicalFactory:PhysicalParticleFactory(parentInstance));
                continue;
            }
            if (isExhaustAttachment && !exhaustVisibility.ShouldDraw)
                continue;
            if (attachment.Root is not LiveCpuRoot childRoot)
            {
                diagnostic =
                    $"aggregate RenderObj '{aggregateRoot.RuntimeRenderObjectName}' attachment '{attachment.SubobjectName}' has no materialized child RenderObj.";
                tracker.AggregateRejected++;
                return false;
            }

            Matrix4x4 childRigidWorld = pose[pivotIndex];
            string childPath = $"{instancePath}/aggregate/{attachmentIndex}:{attachment.SubobjectName}";
            if(attachment.RslStateId is {} stateId)childPath=$"{instancePath}/rsl-bone/{stateId}";
            var childInstance=RentRootInvocation(obj,childRoot,false,childPath,childRigidWorld,null,false);
            childInstance.Parent=parentInstance;childInstance.RslChild=attachment.RslChild;childInstance.OrdinaryHlodPivot=(uint)pivotIndex;
            destination.Add(childInstance);
            tracker.ChildRoots++;
            tracker.AggregateChildRoots++;

            if (TryResolveConfiguredAttachmentHierarchy(
                    childRoot,
                    out EnbW3dRenderHierarchy? childHierarchy,
                    out string? childHierarchyDiagnostic) &&
                childHierarchy is not null)
            {
                fixedSearchNodes.Add(PrepareFixedAttachment(
                    Path: childPath,
                    Root: childRoot,
                    Hierarchy: childHierarchy,
                    RigidWorld: childRigidWorld,
                    Scale: 1f,
                    Animations: null,
                    HierarchyOverrides: Array.Empty<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride>(),
                    Instance: childInstance));
            }
            else if (!string.IsNullOrWhiteSpace(childHierarchyDiagnostic))
            {
                diagnostic =
                    $"aggregate RenderObj '{aggregateRoot.RuntimeRenderObjectName}' child '{attachment.SubobjectName}' hierarchy resolution failed: {childHierarchyDiagnostic}";
                tracker.AggregateRejected++;
                return false;
            }

            if ((childRoot.AggregateAttachments.Count != 0 || childRoot.HmodelChildren.Count != 0 || childRoot.CollectionChildren.Count != 0) &&
                !TryAppendCurrentRetailShipAggregateDescendants(
                    obj,
                    childRoot,
                    childRigidWorld,
                    1f,
                    childPath,
                    destination,
                    fixedSearchNodes,
                    tracker,
                    hierarchyOverrides: null,
                    depth: depth + 1,
                    out diagnostic,
                    childInstance))
            {
                return false;
            }
        }

        return true;
    }

    private long _fixedQueryPoseRequests,_fixedQueryPoseReuses,_fixedVisualQueryEpoch;
    private readonly RootStagingScratch _fixedVisualTaskScratch=new();
    private void TryAppendCurrentRetailFixedShipVisualInstances(
        IEnbRenderObject obj,
        LiveCpuRoot parentRoot,
        LiveRslAnimationBlendBinding? parentAnimationBlend,
        EnbRetailAssetBundle retailAssets,
        EnbRetailBaseAssetResolver resolver,
        RootTaskList destination,
        List<ShipAttachmentOwner> searchNodes,
        LiveShipLegoFrameTracker tracker)
    {
        tracker.FixedEligibleObjects++;

        if (parentRoot.AggregateAttachments.Count != 0)
            tracker.FixedRecursiveAggregateGaps++;

        if (searchNodes.Count == 0)
        {
            if (!EnbCurrentRetailShipLegoRuntime.TryBuildRigidObjectWorld(
                    obj,
                    out Matrix4x4 rootRigidWorld,
                    out string? rootWorldDiagnostic))
            {
                RejectFixedShipVisualGroup(obj, tracker, rootWorldDiagnostic ?? "owning ship rigid transform is unavailable");
                return;
            }

            if (!TryResolveConfiguredAttachmentHierarchy(parentRoot, out EnbW3dRenderHierarchy? rootHierarchy, out string? hierarchyDiagnostic))
            {
                RejectFixedShipVisualGroup(obj, tracker, hierarchyDiagnostic ?? "owning ship attachment hierarchy could not be resolved");
                return;
            }
            if (rootHierarchy is not null)
            {
                var matchingRoots = destination.Where(instance => ReferenceEquals(instance.Root,parentRoot) &&
                    instance.InstancePath == "root").ToArray();
                searchNodes.Add(PrepareFixedAttachment(
                    Path: string.Empty,
                    Root: parentRoot,
                    Hierarchy: rootHierarchy,
                    RigidWorld: rootRigidWorld,
                    Scale: obj.Scale,
                    Animations: parentAnimationBlend?.ResolvedSamples,
                    HierarchyOverrides: Array.Empty<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride>(),
                    Instance: matchingRoots.Length == 1 ? matchingRoots[0] : null));
            }
        }

        var preparation=FixedVisualPreparation(obj);
        IReadOnlyList<LiveShipFixedVisualMaterialized>? materialized=preparation?.Children;
        if(materialized is null)
        {
        var preparing = new List<LiveShipFixedVisualMaterialized>(EnbCurrentRetailShipFixedVisualRuntime.Specs.Length);
        foreach (EnbCurrentRetailShipFixedVisualSpec spec in EnbCurrentRetailShipFixedVisualRuntime.FactorySpecs)
        {
            EnbBaseAssetResolution resolution = ResolveBaseAsset(resolver, spec.BaseAsset);
            switch (resolution.Status)
            {
                case EnbBaseAssetResolutionStatus.NullObject: case EnbBaseAssetResolutionStatus.NoRenderObject:
                    preparing.Add(new LiveShipFixedVisualMaterialized(spec, Root: null, IsRsl: false, IsRetailNoOp: true));
                    break;

                case EnbBaseAssetResolutionStatus.W3d when resolution.W3d is EnbResolvedW3dResource w3d:
                    if (!TryGetCpuRoot(
                            new LiveCpuRootKey(spec.BaseAsset, -1, w3d.LogicalFileName, w3d.RequestedObjectName),
                            w3d,
                            retailAssets,
                            out LiveCpuRoot w3dRoot,
                            out string? w3dDiagnostic))
                    {
                        RejectFixedShipVisualGroup(
                            obj,
                            tracker,
                            $"fixed slot {spec.Slot} BASE-{spec.BaseAsset} could not materialize W3D root: {w3dDiagnostic ?? "unknown root rejection"}");
                        return;
                    }
                    w3dRoot=RetainFixedShipParticleFactory(obj,spec.Slot,w3d.RequestedObjectName,w3dRoot);
                    preparing.Add(new LiveShipFixedVisualMaterialized(spec, w3dRoot, IsRsl: false, IsRetailNoOp: false));
                    break;

                case EnbBaseAssetResolutionStatus.RslReference when resolution.Rsl is EnbResolvedRslReference rsl:
                    if (!TryGetInitialRslRoot(spec.BaseAsset, rsl, retailAssets, out LiveCpuRoot rslRoot, out string? rslDiagnostic))
                    {
                        RejectFixedShipVisualGroup(
                            obj,
                            tracker,
                            $"fixed slot {spec.Slot} BASE-{spec.BaseAsset} RSL initial RenderObject could not materialize: {rslDiagnostic ?? "unknown RSL rejection"}");
                        return;
                    }
                    preparing.Add(new LiveShipFixedVisualMaterialized(spec, rslRoot, IsRsl: true, IsRetailNoOp: false));
                    break;

                default:
                    RejectFixedShipVisualGroup(
                        obj,
                        tracker,
                        $"fixed slot {spec.Slot} BASE-{spec.BaseAsset} resolves as {resolution.Status}; current retail requires a non-null RenderObject before attaching any of the six fixed ship visuals");
                    return;
            }
        }

        materialized=preparing.OrderBy(child=>child.Spec.Slot).ToArray();
        if(preparation is not null){preparation.Children=materialized;_fixedVisualPreparationCount++;}
        }
        foreach(var child in materialized)
        {
            tracker.FixedFactoryCreated++;
            if(child.IsRetailNoOp)tracker.FixedFactoryNoOps++;
            if(child.IsRsl)tracker.FixedRslChildren++;
        }

        tracker.FixedFactoryGroupsReady++;
        // The six child attachment queries read one parent hierarchy state.
        // Child construction below does not modify these parent inputs.
        long queryEpoch=++_fixedVisualQueryEpoch;
        using var fixedScratch=_fixedVisualTaskScratch.Rent();

        foreach (LiveShipFixedVisualMaterialized fixedChild in materialized)
        {
            ShipAttachmentOwner? matched=null;
            Matrix4x4 matchedWorld=default;
            int matchCount=0;
            bool resolutionRejected = false;
            foreach (ShipAttachmentOwner node in searchNodes)
            {
                if(node.FixedQueryEpoch!=queryEpoch)
                {
                    float pendingScale=node.Instance is {} queryInstance?ResolvePendingHierarchyScale(queryInstance,node.Scale):
                        node.Root.ConfiguredHierarchyTranslations is null?node.Scale:1;
                    bool ready;Matrix4x4[] pose;string? poseDiagnostic;
                    if(node.Instance is {} retainedParent && (node.QueryOverrides.Count==0 || node.DrawOverrides is not null))
                    {
                        var previousPose=retainedParent.ChildPose?.Pose;
                        var previousRevision=previousPose?.MutationRevision;
                        _fixedQueryPoseRequests++;
                        ready=TryResolveRetainedHierarchyPose(retainedParent,node.Hierarchy,node.RigidWorld,pendingScale,
                            node.Animations??Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),node.DrawOverrides,out var evaluatedPose,out poseDiagnostic);
                        if(ready && ReferenceEquals(previousPose,evaluatedPose) && previousRevision==evaluatedPose.MutationRevision)_fixedQueryPoseReuses++;
                        pose=ready?evaluatedPose.WorldTransforms:[];
                    }
                    else ready=EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchy(
                        node.Hierarchy,node.RigidWorld,pendingScale,node.Animations,node.QueryOverrides,out pose,out poseDiagnostic);
                    node.FixedQuery=(ready,pose,poseDiagnostic);node.FixedQueryEpoch=queryEpoch;
                }
                var evaluated=node.FixedQuery;
                string? boneDiagnostic=evaluated.Diagnostic;
                Matrix4x4 boneWorld=default;bool found=false;
                if (!evaluated.Ready || !EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(
                        node.Hierarchy,evaluated.Pose,fixedChild.Spec.BoneName,
                        out boneWorld,out found,out boneDiagnostic))
                {
                    tracker.FixedBoneResolutionRejects++;
                    tracker.FixedFirstDiagnostic ??=
                        $"0x{obj.GameId:X8} fixed slot {fixedChild.Spec.Slot} '{fixedChild.Spec.BoneName}' in '{node.Path}': {boneDiagnostic ?? "unknown hierarchy rejection"}";
                    resolutionRejected = true;
                    break;
                }
                if (found)
                {if(matchCount==0){matched=node;matchedWorld=boneWorld;}matchCount++;}
            }

            if (resolutionRejected)
                continue;
            if (matchCount == 0)
            {
                tracker.FixedMissingBones++;
                continue;
            }
            if (matchCount > 1)
            {
                tracker.FixedAmbiguousBones++;
                tracker.FixedFirstDiagnostic ??=
                    $"0x{obj.GameId:X8} fixed slot {fixedChild.Spec.Slot} bone '{fixedChild.Spec.BoneName}' matched {matchCount} RenderObject hierarchy nodes; R9B will not invent re-parenting precedence.";
                continue;
            }

            tracker.FixedLogicalAttachments++;
            if (fixedChild.IsRetailNoOp || fixedChild.Root is null)
                continue;

            ShipAttachmentOwner target=matched!;
            Matrix4x4 targetBoneWorld=matchedWorld;
            var staged=fixedScratch.Roots;staged.Clear();
            if (!TryAppendRootInstanceTreeCore(
                    obj,
                    fixedChild.Root,
                    fixedChild.IsRsl,
                    targetBoneWorld,
                    $"fixed/{fixedChild.Spec.Slot}:{fixedChild.Spec.BoneName}@{target.Path}",
                    useWorldOverride: true,
                    animationBlend: null,
                    staged,
                    out string? childDiagnostic,
                    maneuverJetSlot: fixedChild.Spec.Slot,
                    parentInstance: target.Instance))
            {
                tracker.FixedChildRootRejects++;
                tracker.FixedFirstDiagnostic ??=
                    $"0x{obj.GameId:X8} fixed slot {fixedChild.Spec.Slot} BASE-{fixedChild.Spec.BaseAsset} attachment tree rejected: {childDiagnostic ?? "unknown child-root rejection"}";
                continue;
            }

            destination.AddRange(staged);
            tracker.FixedDrawRoots += staged.Count;
            tracker.FixedAttachedChildren++;
        }
    }

    private static void RejectFixedShipVisualGroup(
        IEnbRenderObject obj,
        LiveShipLegoFrameTracker tracker,
        string reason)
    {
        tracker.FixedRejectedGroups++;
        tracker.FixedFirstDiagnostic ??= $"0x{obj.GameId:X8}: {reason}";
    }


    private static void TrackR9RslShipRoot(
        IEnbRenderObject obj,
        EnbResolvedRslReference rsl,
        IReadOnlyList<LiveRootInstance> topRoots,
        LiveShipLegoFrameTracker tracker)
    {
        tracker.RslAttachmentCandidates += topRoots.Count;
        if (!obj.IsLocalShip)
            return;

        tracker.LocalShipRootKind = $"RSLID {rsl.RslId}";
        tracker.LocalShipAttachmentTopRoots = topRoots.Count;
        if (topRoots.Count == 1)
        {
            LiveRootInstance root = topRoots[0];
            tracker.LocalShipAttachmentRootName = root.Root.RootRenderObjectName;
            tracker.LocalShipAttachmentAnimationSamples = root.AnimationBlend?.ResolvedSamples.Count ?? 0;
        }
    }

    private static void ObserveR9ShipPresentation(
        IEnbRenderObject obj,
        LiveShipLegoFrameTracker tracker)
    {
        if (obj.ObjectType is 0 or 1)
            tracker.ShipObjectsSeen++;
        if (!obj.IsLocalShip)
            return;

        tracker.LocalShipSeen = true;
        tracker.LocalShipGameId = obj.GameId;
        tracker.LocalShipBaseAsset = obj.BaseAsset;
        tracker.LocalShipObjectType = obj.ObjectType;
        tracker.LocalShipObjectScale = obj.Scale;
        tracker.LocalShipPresentation = obj.ShipPresentation;
        tracker.LocalShipLego = obj.ShipLegos;
        tracker.LocalShipAuxDataAudit = obj.ShipAuxDataAudit;
        tracker.LocalShipColorizationAudit = obj.ColorizationAudit;
        tracker.LocalShipDecalEntries = obj.Decals.Length;
        tracker.LocalShipNameDecal = obj.NameDecal;
        tracker.LocalShipColorizationEntries = obj.ColorizationEntries.Length;
        tracker.LocalShipColorizationSideSample = DescribeR9AjColorizationInputs(obj.ColorizationEntries);
        tracker.LocalShipSubParts = obj.SubParts.Length;
        tracker.LocalShipActiveSubParts = obj.SubParts.Count(static part => part.PartValue != 0);
        tracker.LocalShipSubPartSample = string.Join(',', obj.SubParts
            .Take(6)
            .Select(static part => $"{part.Name}={part.PartValue}"));
    }

    private static string DescribeR9AjColorizationInputs(
        ImmutableArray<EnbSceneColorizationSnapshot> entries)
    {
        if (entries.IsDefaultOrEmpty)
            return "none";

        return string.Join(',', entries.Take(8).Select(static (entry, index) =>
        {
            int selector = unchecked((int)entry.Field);
            if (entry.Vector.Length != 12)
                return $"{index}:{selector}=invalid{entry.Vector.Length}";

            ReadOnlySpan<byte> vector = entry.Vector.AsSpan();
            float r = BinaryPrimitives.ReadSingleLittleEndian(vector.Slice(0, 4));
            float g = BinaryPrimitives.ReadSingleLittleEndian(vector.Slice(4, 4));
            float b = BinaryPrimitives.ReadSingleLittleEndian(vector.Slice(8, 4));
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{index}:{selector}=({r:0.###},{g:0.###},{b:0.###})");
        }));
    }

    private static string DescribeVector3(Vector3? value)
        => value is not Vector3 vector
            ? "(-)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"({vector.X:0.###},{vector.Y:0.###},{vector.Z:0.###})");

    private static string FormatPreciseVector(Vector3 value)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"({value.X:0.######},{value.Y:0.######},{value.Z:0.######})");

    // Retail 72B7F0 finds an existing child entry by its BoneName chain and
    // replaces that child, so for one path the source mutated last wins. The
    // ordered 732880/6FEF70 mutations carry that order; a duplicate whose
    // sources have no recorded Create stays unresolved.
    private static void ResolveLegoPathReplacement(IEnbRenderObject obj,List<LiveShipLegoSpec> specs)
    {
        if(obj.LegoControllerMutations.IsDefaultOrEmpty||specs.Count<2)return;
        var last=new Dictionary<(string Source,int Ordinal),ulong>();
        foreach(var change in obj.LegoControllerMutations)
            if(change.Kind!=EnbNext.Net7.World.Net7LegoControllerMutationKind.Remove)last[(change.Source,change.Ordinal)]=change.Sequence;
        foreach(var group in specs.GroupBy(static spec=>spec.CanonicalPath,StringComparer.Ordinal).Where(static g=>g.Count()>1).ToArray())
        {
            if(group.Any(spec=>!last.ContainsKey((spec.Source,spec.Ordinal))))continue;
            var winner=group.MaxBy(spec=>last[(spec.Source,spec.Ordinal)])!;
            specs.RemoveAll(spec=>!ReferenceEquals(spec,winner)&&string.Equals(spec.CanonicalPath,group.Key,StringComparison.Ordinal));
        }
    }

    private static bool RejectCurrentRetailShipLego(
        IEnbRenderObject obj,
        LiveShipLegoFrameTracker tracker,
        string reason,
        out string? diagnostic)
    {
        diagnostic = reason;
        tracker.RejectedObjects++;
        tracker.FirstDiagnostic ??= $"0x{obj.GameId:X8}: {reason}";
        return false;
    }

    private static bool TryParseShipLegoPath(
        string boneNameChain,
        out string[] path,
        out string? diagnostic)
    {
        diagnostic = null;
        //843970 -> 4FC2F0 splits literal '/' without trimming. Empty input
        //is the zero-segment root chain;72B7F0 cannot attach a child to itself.
        //Leading/interior empty tokens survive; a trailing delimiter adds none.
        if (boneNameChain.Length == 0)
        {
            path = [];
            diagnostic = "An empty Lego BoneName chain cannot attach a child to its own root.";
            return false;
        }
        path = boneNameChain.Split('/', StringSplitOptions.None);
        if (boneNameChain[^1] == '/') path = path[..^1];
        return true;
    }

    private static bool TryResolveLiveAttachmentHierarchy(
        LiveCpuRoot root,
        out EnbW3dRenderHierarchy? hierarchy,
        out string? diagnostic)
    {
        hierarchy = null;
        diagnostic = null;
        if (root.AttachmentHierarchyName is null)
            return true;

        int matches = 0;
        EnbW3dRenderHierarchy? match = null;
        foreach (var candidate in root.Resource.Hierarchies)
            if (string.Equals(candidate.Name, root.AttachmentHierarchyName, StringComparison.OrdinalIgnoreCase))
            {
                match = candidate;
                matches++;
            }
        if (matches == 1)
        {
            hierarchy = match;
            return true;
        }
        if (matches == 0)
        {
            diagnostic =
                $"RenderObj '{root.RootRenderObjectName}' requires missing hierarchy '{root.AttachmentHierarchyName}'.";
            return false;
        }

        diagnostic =
            $"RenderObj '{root.RootRenderObjectName}' hierarchy '{root.AttachmentHierarchyName}' is ambiguous ({matches} matches).";
        return false;
    }

    private static string DescribeR9ShipLego(LiveShipLegoFrameTracker tracker)
    {
        string local = "local none";
        if (tracker.LocalShipSeen)
        {
            EnbSceneShipPresentationSnapshot? presentation = tracker.LocalShipPresentation;
            string organic = presentation?.IsOrganic switch
            {
                true => "true",
                false => "false",
                null => "absent",
            };
            string inventory = presentation is null
                ? "ShipAux none"
                : presentation.InventoryMaterialized ? "inventory materialized" : "inventory dormant";
            string equipModel = string.IsNullOrWhiteSpace(presentation?.EquipMountModel)
                ? "-"
                : presentation!.EquipMountModel!;
            int mountNames = presentation?.Mounts.Count(static mount => !string.IsNullOrWhiteSpace(mount.BoneName)) ?? 0;
            int equippedSeen = presentation?.EquippedSlots.Length ?? 0;
            int equippedPopulated = presentation?.EquippedSlots.Count(static slot => slot.ItemTemplateId != 0) ?? 0;
            string mountSample = presentation is null
                ? "none"
                : string.Join(',', presentation.Mounts
                    .Where(static mount => !string.IsNullOrWhiteSpace(mount.BoneName))
                    .Take(6)
                    .Select(static mount => $"{mount.Ordinal}:{mount.BoneName}"));
            if (string.IsNullOrWhiteSpace(mountSample))
                mountSample = "none";
            string equippedSample = presentation is null
                ? "none"
                : string.Join(',', presentation.EquippedSlots
                    .Where(static slot => slot.ItemTemplateId != 0)
                    .Take(8)
                    .Select(static slot => $"{slot.Ordinal}:{slot.ItemTemplateId}"));
            if (string.IsNullOrWhiteSpace(equippedSample))
                equippedSample = "none";

            ImmutableArray<EnbSceneShipLegoAttachmentSnapshot> legoAttachments =
                tracker.LocalShipLego?.Attachments ?? ImmutableArray<EnbSceneShipLegoAttachmentSnapshot>.Empty;
            EnbSceneShipLegoAttachmentSnapshot[] activeLegos = legoAttachments
                .Where(static attachment =>
                    attachment.Type is 1 or 2 or 3 &&
                    attachment.BaseAsset > 0 &&
                    !string.IsNullOrWhiteSpace(attachment.BoneName))
                .ToArray();
            string legoSample = string.Join(',', activeLegos
                .Take(8)
                .Select(static attachment =>
                    $"{attachment.Ordinal}:{attachment.BoneName}={attachment.BaseAsset}/t{attachment.Type}"));
            if (string.IsNullOrWhiteSpace(legoSample))
                legoSample = "none";

            EnbSceneColorizationApplicationAuditSnapshot? colorAudit = tracker.LocalShipColorizationAudit;
            string colorScope = colorAudit is null
                ? "packet-time scope unavailable"
                : $"packet-time scope {colorAudit.ApplyCount} aux/subparts {colorAudit.ActiveShipLegoAttachmentsAtLastApply}/{colorAudit.ActiveSubPartsAtLastApply} after-aux {colorAudit.ObjectAuxDataAttemptsAtLastApply}; completed graph {activeLegos.Length}/{tracker.LocalShipActiveSubParts}";
            string colorPathSample = string.Join(',', tracker.ColorizationTargetInstancePaths
                .Order(StringComparer.Ordinal)
                .Take(8));
            if (string.IsNullOrWhiteSpace(colorPathSample))
                colorPathSample = "none";
            string excludedColorPathSample = string.Join(',', tracker.ColorizationExcludedInstancePaths
                .Order(StringComparer.Ordinal)
                .Take(8));
            if (string.IsNullOrWhiteSpace(excludedColorPathSample))
                excludedColorPathSample = "none";
            EnbSceneAuxDataMaterializationAuditSnapshot? auxAudit = tracker.LocalShipAuxDataAudit;
            string auxData = "auxdata none";
            if (auxAudit is not null)
            {
                string outcome = auxAudit.LargestAccepted ? "accepted" : auxAudit.LargestRetailCompatiblePartial ? "partial (retail-compatible)" : "REJECT";
                string error = string.Empty;
                if (!auxAudit.LargestAccepted && !auxAudit.LargestRetailCompatiblePartial && !string.IsNullOrWhiteSpace(auxAudit.LargestError))
                {
                    string clean = auxAudit.LargestError
                        .Replace('\r', ' ')
                        .Replace('\n', ' ');
                    if (clean.Length > 160)
                        clean = clean[..160] + "…";
                    error = $" '{clean}'";
                }
                auxData =
                    $"auxdata attempts {auxAudit.AttemptCount} max {auxAudit.LargestDeltaLength} sel 0x{auxAudit.LargestSelector:X2} {outcome}{error}";
            }

            local =
                $"local 0x{tracker.LocalShipGameId:X8} BASE {tracker.LocalShipBaseAsset} type {tracker.LocalShipObjectType} " +
                $"scale object {tracker.LocalShipObjectScale:0.######} legoNotify {(tracker.LocalShipLego?.Scale ?? 1f):0.######} · {auxData} · " +
                $"organic {organic} · {inventory} equipMount '{equipModel}' mounts {presentation?.MaterializedMountCount ?? 0}/20 names {mountNames}/20 [{mountSample}] " +
                $"equipped {equippedPopulated}/{equippedSeen} [{equippedSample}] · legos active/retained {activeLegos.Length}/{legoAttachments.Length} [{legoSample}] " +
                $"engine thrust {presentation?.EngineThrustState?.ToString() ?? "-"} trail type {presentation?.EngineTrailType?.ToString() ?? "-"} warp private/global {presentation?.PrivateWarpState?.ToString() ?? "-"}/{presentation?.GlobalWarpState?.ToString() ?? "-"} · " +
                $"decals entries {tracker.LocalShipDecalEntries} target/applied/tinted/unassigned/unresolved {tracker.DecalTargetPackets}/{tracker.DecalAppliedPackets}/{tracker.DecalTintedPackets}/{tracker.DecalUnassignedPackets}/{tracker.DecalUnresolvedPackets} · " +
                $"R9AK_CURRENT_RETAIL_NAMEDECAL_ACTIVE name '{tracker.LocalShipNameDecal?.Name ?? "-"}' rgb {DescribeVector3(tracker.LocalShipNameDecal?.Rgb)} target/applied/unresolved {tracker.NameDecalTargetPackets}/{tracker.NameDecalAppliedPackets}/{tracker.NameDecalUnresolvedPackets} texture {tracker.NameDecalTextureWidth}x{tracker.NameDecalTextureHeight} textw {tracker.NameDecalTextWidth} · " +
                $"R9AO_CURRENT_RETAIL_BASE_ASSET_OVERBRIGHT_ACTIVE packets eligible/rewritten/optout/unresolved {tracker.OverbrightEligiblePackets}/{tracker.OverbrightAppliedPackets}/{tracker.OverbrightDisabledPackets}/{tracker.OverbrightUnresolvedPackets} triangles target/rewritten {tracker.OverbrightTargetTriangles}/{tracker.OverbrightAppliedTriangles} · " +
                $"color entries {tracker.LocalShipColorizationEntries} packets target/applied/excluded/pending/unresolved {tracker.ColorizationTargetPackets}/{tracker.ColorizationAppliedPackets}/{tracker.ColorizationExcludedPackets}/{tracker.ColorizationPendingPackets}/{tracker.ColorizationUnresolvedPackets} triangles target/applied/excluded {tracker.ColorizationTargetTriangles}/{tracker.ColorizationAppliedTriangles}/{tracker.ColorizationExcludedTriangles} regions [{string.Join(',', tracker.ColorizationRegions.Order())}] {colorScope} draw-scope root/lego/fixed/other {tracker.ColorizationRootPackets}/{tracker.ColorizationLegoPackets}/{tracker.ColorizationFixedPackets}/{tracker.ColorizationOtherPackets} [{colorPathSample}] excluded [{excludedColorPathSample}] · R9AJ_CURRENT_RETAIL_SURFACE_INPUT_AUDIT_ACTIVE sides [{tracker.LocalShipColorizationSideSample ?? "none"}] applied-family-triangles S1/S2/SK {tracker.ColorizationPrimaryTriangles}/{tracker.ColorizationSecondaryTriangles}/{tracker.ColorizationSkinTriangles} · " +
                $"R9AP_CURRENT_RETAIL_WEAPON_METAL_RGB_ACTIVE packets target/applied/unresolved {tracker.WeaponMetalTargetPackets}/{tracker.WeaponMetalAppliedPackets}/{tracker.WeaponMetalUnresolvedPackets} triangles target/applied {tracker.WeaponMetalTargetTriangles}/{tracker.WeaponMetalAppliedTriangles} regions [{string.Join(',', tracker.WeaponMetalRegions.Order())}] · " +
                $"subparts {tracker.LocalShipSubParts} [{(string.IsNullOrWhiteSpace(tracker.LocalShipSubPartSample) ? "none" : tracker.LocalShipSubPartSample)}] · " +
                $"root {tracker.LocalShipRootKind ?? "unresolved"} top {tracker.LocalShipAttachmentTopRoots} anim {tracker.LocalShipAttachmentAnimationSamples} '{tracker.LocalShipAttachmentRootName ?? "-"}'";
        }

        string environmentMap = tracker.EnvironmentMapTexture is not null
            ? $"R9AG_CURRENT_RETAIL_ENVIRONMENT_MAP_SELECTOR_ACTIVE back {tracker.EnvironmentMapBaseAsset} texture {tracker.EnvironmentMapTexture} axis {tracker.EnvironmentMapAxis} {(tracker.EnvironmentMapUsesDefaultTexture ? "default" : "explicit")}"
            : $"R9AG current-retail environment-map selector unresolved back {tracker.EnvironmentMapBaseAsset?.ToString() ?? "-"}";
        string environmentAtlas = tracker.EnvironmentAtlasBuiltTextures != 0
            ? $"R9AH_CURRENT_RETAIL_ENVIRONMENT_ATLAS_ACTIVE proctex {tracker.EnvironmentAtlasBuiltTextures}/{tracker.EnvironmentAtlasExpectedTextures} reflection {tracker.EnvironmentMapTexture} reduction {tracker.EnvironmentAtlasTextureReduction} filter {(int)tracker.EnvironmentAtlasTextureFilter}/{tracker.EnvironmentAtlasTextureFilter} triangle-half x2-saturate box-mips ship-packets {tracker.EnvironmentAtlasTargetPackets}"
            : $"R9AH current-retail environment atlas unresolved proctex {tracker.EnvironmentAtlasBuiltTextures}/{tracker.EnvironmentAtlasExpectedTextures}";
        string renderSettings =
            $"ENBNEXT_GRAPHICS_SETTINGS_ACTIVE source local-defaults/player-options texture reduction {tracker.RenderTextureReduction} filter {(int)tracker.RenderTextureFilter}/{tracker.RenderTextureFilter}";
        string status =
            $"render ship: {renderSettings} · {environmentMap} · {environmentAtlas} · R9AN_CURRENT_RETAIL_PACKET_SCOPE_AUTHORED_WEAPON_MAPPER_ACTIVE · {local} · ships {tracker.ShipObjectsSeen} · " +
            $"lego objects {tracker.ComposedObjects}/{tracker.ObjectsSeen} aux {tracker.AuxAttachments} subparts {tracker.SubPartAttachments} child roots {tracker.ChildRoots} " +
            $"noop {tracker.NoOpAttachments} pending {tracker.PendingAttachments} ATTACH0 {tracker.MissingAttachFallbacks} reject {tracker.RejectedObjects} · " +
            $"aggregate wrappers {tracker.AggregateWrappers} child roots {tracker.AggregateChildRoots} noop {tracker.AggregateNoOps} reject {tracker.AggregateRejected} exhaust visible/hidden/unresolved {tracker.ExhaustVisibleAttachments}/{tracker.ExhaustHiddenAttachments}/{tracker.ExhaustUnresolvedAttachments} · " +
            $"fixed eligible {tracker.FixedEligibleObjects} groups {tracker.FixedFactoryGroupsReady} factory {tracker.FixedFactoryCreated} attached {tracker.FixedAttachedChildren} " +
            $"logical {tracker.FixedLogicalAttachments} roots {tracker.FixedDrawRoots} missing {tracker.FixedMissingBones} ambiguous {tracker.FixedAmbiguousBones} " +
            $"RSL {tracker.FixedRslChildren} noop {tracker.FixedFactoryNoOps} reject group/bone/root {tracker.FixedRejectedGroups}/{tracker.FixedBoneResolutionRejects}/{tracker.FixedChildRootRejects} aggregate-gap {tracker.FixedRecursiveAggregateGaps} · " +
            $"RSL-parent candidates/ready/ambiguous {tracker.RslAttachmentCandidates}/{tracker.RslAttachmentReady}/{tracker.RslAttachmentAmbiguous}";
        if (tracker.LocalShipSeen)
        {
            string optionSource = tracker.HlodUsesPersistedOptions
                ? $"player{tracker.HlodOptionSelector?.ToString() ?? "?"}"
                : "retail defaults";
            string decisions = string.Join(',', tracker.HlodDecisions
                .Take(24)
                .Select(static decision => decision.Describe()));
            if (string.IsNullOrWhiteSpace(decisions))
                decisions = "none";
            status +=
                $" · HLOD options {optionSource} poly {tracker.HlodPolyBudget} shared {tracker.HlodFinalCost:0.###}/{tracker.HlodPolyBudget} " +
                $"fixed {tracker.HlodFixedCost:0.###} candidates {tracker.HlodCandidateCount} · local HLOD [{decisions}]";
        }
        string? first = tracker.FirstDiagnostic ?? tracker.FixedFirstDiagnostic;
        if (!string.IsNullOrWhiteSpace(first))
            status += $" · first [{first}]";
        if (!string.IsNullOrWhiteSpace(tracker.DecalFirstDiagnostic))
            status += $" · first decal [{tracker.DecalFirstDiagnostic}]";
        if (!string.IsNullOrWhiteSpace(tracker.NameDecalFirstDiagnostic))
            status += $" · first name-decal [{tracker.NameDecalFirstDiagnostic}]";
        if (!string.IsNullOrWhiteSpace(tracker.ColorizationFirstDiagnostic))
            status += $" · first color [{tracker.ColorizationFirstDiagnostic}]";
        if (!string.IsNullOrWhiteSpace(tracker.WeaponMetalFirstDiagnostic))
            status += $" · first weapon-metal [{tracker.WeaponMetalFirstDiagnostic}]";
        if (!string.IsNullOrWhiteSpace(tracker.EnvironmentMapDiagnostic))
            status += $" · environment map [{tracker.EnvironmentMapDiagnostic}]";
        if (!string.IsNullOrWhiteSpace(tracker.EnvironmentAtlasDiagnostic))
            status += $" · environment atlas [{tracker.EnvironmentAtlasDiagnostic}]";
        return status;
    }

    private bool TryAppendRootInstanceTree(
        IEnbRenderObject obj,
        LiveCpuRoot root,
        bool isRsl,
        RootTaskList destination,
        out string? diagnostic)
        => TryAppendRootInstanceTree(
            obj,
            root,
            isRsl,
            destination,
            animationBlend: null,
            out diagnostic);

    private static LiveRslAnimationBlendBinding CreateRslAnimationBlend(LiveCpuRoot root,IReadOnlyList<LiveRslAnimationBinding> bindings)
        =>new(root,bindings);

    private bool TryAppendRootInstanceTree(
        IEnbRenderObject obj,
        LiveCpuRoot root,
        bool isRsl,
        RootTaskList destination,
        LiveRslAnimationBlendBinding? animationBlend,
        out string? diagnostic)
    {
        diagnostic = null;
        float hierarchySourceScale=1;
        var appearance=FactoryAppearance.From(obj);
        if(root.UnconfiguredFactoryObject)
            appearance=FactoryAppearance.Neutral;
        if(root.ConfiguredFactoryInputs is {} factoryInputs)
        {
            float remaining=isRsl && (_shipExhaustOwner??this)._effectRslRootOwners.TryGetValue(obj.RenderIdentity,out var effectOwner)?effectOwner.SourceRadiusScale:1;
            hierarchySourceScale=remaining;
            appearance=new(factoryInputs.Scale*remaining,factoryInputs.Hsv.X,factoryInputs.Hsv.Y,factoryInputs.Hsv.Z);
        }
        if(!isRsl && root.ConfiguredFactoryInputs is null && !root.ParticleConstructionPrepared && HasConfigurableMeshTree(root))
            root=RetainConfiguredDirectMeshRoot(root,ref appearance);
        animationBlend ??= ResolveShipWarpAnimation(obj, root, "root", obj.Spatial?.Transform.Position ?? Vector3.Zero);
        if (root.AggregateAttachments.Count == 0 && root.Collection is null && root.HmodelChildren.Count == 0 && root.Sphere is null && root.Ring is null && root.RetainedEmitter is null)
        {
            var instance = _retainedOrdinaryMeshes
                ? isRsl ? RentRootInvocation(obj,root,true,"root",null,animationBlend,false)
                    : RentSimpleRoot(obj, root, animationBlend)
                : new LiveRootInstance(obj, root, isRsl, animationBlend: animationBlend);
            instance.RetainedChildPoses=instance.PreparedOwner?.ChildPoses??RetainChildPoseOwner(obj,!instance.IsRsl);
            RetainPrimitiveFactoryTransform(instance);
            destination.Add(instance);
            return true;
        }
        if (!TryBuildObjectWorld(obj, out Matrix4x4 objectWorld,appearance.Scale))
        {
            diagnostic = $"0x0600 aggregate root '{root.RootRenderObjectName}' has a non-finite owning object transform.";
            return false;
        }

        using var staging=_rootStagingScratch.Rent();
        var staged=staging.Roots;
        if (!TryAppendRootInstanceTreePrepared(
                obj,
                root,
                isRsl,
                objectWorld,
                "root",
                useWorldOverride: false,
                animationBlend,
                staged,
                out diagnostic,initialize:RetainPrimitiveFactoryTransform,appearance:appearance))
        {
            return false;
        }

        //0048B220 configures retained data before scene transforms are set.
        //Keep this factor separate from the synthetic scale in objectWorld.
        RetainPrimitiveFactoryTransform(staged[0]);
        destination.AddRange(staged);
        return true;

        void RetainPrimitiveFactoryTransform(LiveRootInstance instance)
        {
            instance.Appearance=appearance;
            instance.HierarchySourceScale=hierarchySourceScale;
            if(TryBuildObjectWorld(obj,out var primitiveFactoryWorld,1))
                instance.PrimitiveFactoryWorld=primitiveFactoryWorld;
            instance.PrimitiveFactoryScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(appearance.Scale)?appearance.Scale:1;
            var shift=appearance.Hsv;
            instance.PrimitiveFactoryHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(shift)?shift:Vector3.Zero;
        }
    }

    private bool TryAppendRootInstanceTreeCore(IEnbRenderObject obj,LiveCpuRoot root,bool isRsl,
        Matrix4x4 objectWorld,string instancePath,bool useWorldOverride,LiveRslAnimationBlendBinding? animationBlend,
        RootTaskList staged,out string? diagnostic,int? maneuverJetSlot=null,
        LiveRootInstance? parentInstance=null,bool isOrdinaryHlodChild=false,FactoryAppearance? appearance=null)
        =>TryAppendRootInstanceTreePrepared(obj,root,isRsl,objectWorld,instancePath,useWorldOverride,
            animationBlend,staged,out diagnostic,maneuverJetSlot,parentInstance,isOrdinaryHlodChild,appearance:appearance);

    private bool TryAppendRootInstanceTreePrepared(
        IEnbRenderObject obj,
        LiveCpuRoot root,
        bool isRsl,
        Matrix4x4 objectWorld,
        string instancePath,
        bool useWorldOverride,
        LiveRslAnimationBlendBinding? animationBlend,
        RootTaskList staged,
        out string? diagnostic,
        int? maneuverJetSlot = null,
        LiveRootInstance? parentInstance = null,
        bool isOrdinaryHlodChild=false,
        uint ordinaryHlodPivot=0,bool isAggregateHlodChild=false,Action<LiveRootInstance>? initialize=null,RootInvocations? invocationSlots=null,FactoryAppearance? appearance=null)
    {
        diagnostic = null;
        var localAppearance=appearance??parentInstance?.Appearance??FactoryAppearance.From(obj);
        //45C990 configures Bone children before4609C0 attaches them. Their
        //own retained factory inputs do not become the parent's inputs.
        if(root.IndependentRslFactory && root.ConfiguredFactoryInputs is {} childInputs)
            localAppearance=new(childInputs.Scale,childInputs.Hsv.X,childInputs.Hsv.Y,childInputs.Hsv.Z);
        var instance = RentRootInvocation(obj,root,isRsl,instancePath,
            useWorldOverride?objectWorld:null,animationBlend,isOrdinaryHlodChild,
            prepared:invocationSlots,preparedOwner:invocationSlots is not null?parentInstance?.PreparedOwner:null);
        instance.Appearance=localAppearance;
        instance.Parent=parentInstance;
        instance.OrdinaryHlodPivot=ordinaryHlodPivot;
        instance.IsAggregateHlodChild=isAggregateHlodChild;
        initialize?.Invoke(instance);
        staged.Add(instance);
        if(!TryAppendRetainedCompositeChildren(instance,objectWorld,staged,out diagnostic))return false;

        if (root.AggregateAttachments.Count == 0)
            return true;

        string? hierarchyName = root.AttachmentHierarchyName;
        if (hierarchyName is null)
        {
            diagnostic =
                $"0x0600 aggregate base RenderObj '{root.RootRenderObjectName}' has attached subobjects but no resolved hierarchy.";
            return false;
        }

        var aggregateBinding = RetainAggregateAttachmentBinding(instance);
        EnbW3dRenderHierarchy? hierarchy = aggregateBinding.Hierarchy;
        if (hierarchy is null)
        {
            diagnostic =
                $"0x0600 aggregate base RenderObj '{root.RootRenderObjectName}' requires missing attachment hierarchy '{hierarchyName}'.";
            return false;
        }
        var configuredPost=ConfiguredHierarchyPostTransforms(instance);
        bool poseReady=TryResolveRetainedHierarchyPose(instance,hierarchy,
            configuredPost?.RigidWorldTransform??objectWorld,configuredPost?.HierarchyTranslationScale,
            animationBlend?.ResolvedSamples??[],configuredPost?.Transforms,
            out var evaluatedPose,out var poseDiagnostic);
        var pose=poseReady?evaluatedPose.WorldTransforms:[];
        var pivotVisibility=poseReady?evaluatedPose.Visibility:[];
        if (!poseReady)
        {
            diagnostic =
                $"0x0600 aggregate base RenderObj '{root.RootRenderObjectName}' attachment hierarchy '{hierarchyName}' could not evaluate: {poseDiagnostic ?? "unknown hierarchy rejection"}.";
            return false;
        }

        ObservePlanetExhaust(obj, hierarchy, pose, instancePath);
        for (int attachmentIndex = 0; attachmentIndex < root.AggregateAttachments.Count; attachmentIndex++)
        {
            LiveAggregateAttachment attachment = root.AggregateAttachments[attachmentIndex];
            if(attachment.RslChild is {Parent:null})continue;
            if (UsesPlanetExhaust(obj) && EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(attachment.BoneName)) continue;
            int pivotIndex = aggregateBinding.Pivots[attachmentIndex];
            if (pivotIndex >= pose.Length)
            {
                diagnostic =
                    $"0x0600 aggregate attachment '{attachment.SubobjectName}' requires missing bone '{attachment.BoneName}' in hierarchy '{hierarchyName}'.";
                return false;
            }

            var slots=PrepareAggregateInvocation(instance,attachmentIndex,attachment);
            string attachmentPath=slots?.AttachmentPath??$"{instancePath}/{attachmentIndex}:{attachment.SubobjectName}";
            ObserveShipExhaust(obj, attachment, pose[pivotIndex], hierarchyName, pivotIndex,
                attachmentPath, maneuverJetSlot,physicalFactory:PhysicalParticleFactory(instance));
            if (EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(attachment.BoneName) &&
                !EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(obj).ShouldDraw)
            {
                continue;
            }

            // Proven retail NULL and ParticleEmitter subobjects are valid attached
            // RenderObjs whose draw virtual is a no-op. Bone existence is still
            // validated above, but they intentionally contribute no draw packet.
            if (attachment.Sphere is { } sphere)
            {
                ObserveWorldSphere(obj, sphere, pose[pivotIndex], attachmentPath,
                    (pivotVisibility.Length==0) || pivotVisibility[pivotIndex],instance);
                continue;
            }
            if (attachment.IsRetailNoOp)
                continue;
            if (attachment.Root is not LiveCpuRoot childRoot)
            {
                diagnostic =
                    $"0x0600 aggregate attachment '{attachment.SubobjectName}' has no materialized RenderObj root.";
                return false;
            }

            // Attached mesh roots obey their parent's animated pivot visibility
            // just as inline HLOD meshes do. Their own hierarchy has no copy of
            // the parent's visibility track.
            //Retained Sphere/Ring children still receive their visibility
            //setters, resetting progress while preserving sampled properties.
            if (!(pivotVisibility.Length==0) && !pivotVisibility[pivotIndex] && childRoot.Sphere is null && childRoot.Ring is null && childRoot.RetainedEmitter is null)
                continue;

            string childPath=slots?.Path??(attachment.RslStateId is {} stateId?$"{instancePath}/rsl-bone/{stateId}":attachmentPath);
            int aggregateChildInstance=staged.Count;
            if (!TryAppendRootInstanceTreePrepared(
                    obj,
                    childRoot,
                    isRsl,
                    pose[pivotIndex],
                    childPath,
                    useWorldOverride: true,
                    animationBlend: null,
                    staged,
                    out diagnostic,
                    maneuverJetSlot,
                    instance,ordinaryHlodPivot:(uint)pivotIndex,isAggregateHlodChild:true,invocationSlots:slots))
            {
                return false;
            }
            staged[aggregateChildInstance].IsAggregateHlodChild=true;
            staged[aggregateChildInstance].RslChild=attachment.RslChild;
            staged[aggregateChildInstance].OrdinaryHlodPivot=(uint)pivotIndex;
        }

        return true;
    }

    private static EnbW3dRenderResource BindExternalHlodRenderObjects(
        EnbW3dRenderResource resource,
        string requestedObjectName,
        EnbRetailAssetBundle retailAssets)
    {
        EnbMixResourceCatalog? resources = retailAssets.MixResources;
        if (resources is null)
            return resource;

        if (resource.ExternalRenderObjects.Any(value => value.DazzleTypeName is not null && value.DazzleDefinition is null))
        {
            var definitions = EnbRetailIniDocument.Load(Path.Combine(retailAssets.Paths.ClientIniDirectory, "Dazzle.ini"));
            resource = resource.Copy().SetExternalRenderObjects(resource.ExternalRenderObjects.Select(value =>
                value.DazzleTypeName is {} type && value.DazzleDefinition is null
                    ? value with { DazzleDefinition = EnbDazzleDefinition.Resolve(definitions, type) } : value).ToArray());
        }
        EnbW3dRenderHlod? requestedHlod = resource.HierarchicalLods.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requestedObjectName, StringComparison.OrdinalIgnoreCase));
        if (requestedHlod is null)
            return resource;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (EnbW3dRenderHlodLevel level in requestedHlod.Levels)
        {
            foreach (EnbW3dRenderHlodMember member in level.Members)
                names.Add(member.Name);
        }
        if (requestedHlod.Aggregate is EnbW3dRenderHlodAggregate aggregate)
        {
            foreach (EnbW3dRenderHlodMember member in aggregate.Members)
                names.Add(member.Name);
        }

        var external = resource.ExternalRenderObjects.ToList();
        if (resources.TryReadResource(resource.LogicalFileName, out var modelBytes, out var modelLocation))
        {
            var dazzles = EnbW3dDazzle.Decode(modelBytes);
            if (dazzles.Count != 0)
            {
                var ini = EnbRetailIniDocument.Load(Path.Combine(retailAssets.Paths.ClientIniDirectory, "Dazzle.ini"));
                foreach (var dazzle in dazzles.Where(d => names.Contains(d.Name) && !external.Any(value => string.Equals(value.Name, d.Name, StringComparison.OrdinalIgnoreCase))))
                    external.Add(new(dazzle.Name, EnbW3dExternalRenderObjectKind.Dazzle, modelLocation.Name, modelLocation.ArchiveName)
                        { DazzleDefinition = EnbDazzleDefinition.Resolve(ini, dazzle.Type) });
            }
        }
        foreach (string name in names)
        {
            bool representedByMesh = resource.Meshes.Any(mesh =>
                string.Equals(mesh.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(mesh.ContainerName) &&
                 string.Equals($"{mesh.ContainerName}.{mesh.Name}", name, StringComparison.OrdinalIgnoreCase)));
            if (string.Equals(name, "NULL", StringComparison.OrdinalIgnoreCase) ||
                representedByMesh ||
                resource.CompositeRenderChildren.Any(child => string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                external.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Current retail Create_Render_Object first checks the prototype
            // registry and, when lazy loading is enabled and a simple name is
            // absent, loads <name>.w3d before retrying the registry. Admit only
            // a direct resource whose bytes prove the exact 0x0500/0x0501
            // ParticleEmitter asset-manager name. No friendly-name class map.
            if (!EnbInstalledW3dPrototypeTokenAudit.TryResolveDirectParticleEmitterPrototype(
                    resources,
                    name,
                    out EnbMixResourceLocation location))
            {
                continue;
            }


            if (!resources.TryReadResource(location, out byte[] emitterBytes) ||
                !EnbCurrentRetailParticleEmitterDecoder.TryDecode(
                    emitterBytes,
                    out EnbCurrentRetailParticleEmitterDefinition emitterDefinition,
                    out _, retailAssets.Textures) ||
                !string.Equals(emitterDefinition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            EnbW3dRenderTextureLocation? textureLocation = null;
            if (resources.TryLocateResource(
                    emitterDefinition.TextureFileName,
                    out EnbMixResourceLocation resolvedTexture))
            {
                textureLocation = new EnbW3dRenderTextureLocation(
                    resolvedTexture.Name,
                    resolvedTexture.ArchivePath,
                    resolvedTexture.DataOffset,
                    resolvedTexture.DataLength);
            }

            external.Add(new EnbW3dExternalRenderObject(
                name,
                EnbW3dExternalRenderObjectKind.ParticleEmitter,
                location.Name,
                location.ArchiveName)
            {
                ParticleEmitterDefinition = emitterDefinition,
                ParticleTextureLocation = textureLocation,
                ParticleRetainedTexture = string.IsNullOrEmpty(emitterDefinition.TextureFileName) ? null :
                    textureLocation is not null
                        ? EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(retailAssets.Textures, emitterDefinition.TextureFileName)
                        : retailAssets.Textures.ResolveCurrentRetailMissingTexture(emitterDefinition.TextureFileName, retailAssets.Paths.ClientExecutablePath),
            });
        }

        return external.Count == resource.ExternalRenderObjects.Count
            ? resource
            : resource.Copy().SetExternalRenderObjects(external.ToArray());
    }

    private bool TryBindExternalHierarchies(
        EnbW3dRenderResource resource,
        string requestedObjectName,
        EnbRetailAssetBundle retailAssets,
        out EnbW3dRenderResource bound,
        out string? diagnostic)
    {
        //009C5210 obtains its hierarchy from the global manager, even when
        //the just-loaded FILE contains another tree with the same name.
        resource = resource.Copy().SetHierarchies(resource.Hierarchies.Select(hierarchy =>
            _prototypeResources.TryFindHierarchy(hierarchy.Name, out var registered) ? registered : hierarchy).ToArray());
        bound = resource;
        diagnostic = null;

        var requiredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnbW3dRenderHlod? requestedHlod = resource.HierarchicalLods.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, requestedObjectName, StringComparison.OrdinalIgnoreCase));
        if (requestedHlod is not null)
        {
            requiredNames.Add(requestedHlod.HierarchyName);

            var memberNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (EnbW3dRenderHlodLevel level in requestedHlod.Levels)
            {
                foreach (EnbW3dRenderHlodMember member in level.Members)
                    memberNames.Add(member.Name);
            }
            if (requestedHlod.Aggregate is EnbW3dRenderHlodAggregate aggregate)
            {
                foreach (EnbW3dRenderHlodMember member in aggregate.Members)
                    memberNames.Add(member.Name);
            }

            foreach (EnbW3dRenderHierarchyModel model in resource.HierarchyModels)
            {
                if (model.Nodes.Any(node => memberNames.Contains(node.RenderObjectName)))
                {
                    requiredNames.Add(model.HierarchyName);
                }
            }
        }
        else
        {
            foreach (EnbW3dRenderHierarchyModel model in resource.HierarchyModels)
            {
                bool ownsRequestedRoot = string.Equals(model.Name, requestedObjectName, StringComparison.OrdinalIgnoreCase) ||
                    model.Nodes.Any(node => string.Equals(node.RenderObjectName, requestedObjectName, StringComparison.OrdinalIgnoreCase));
                if (ownsRequestedRoot)
                    requiredNames.Add(model.HierarchyName);
            }
        }

        if (requiredNames.Count == 0)
            return true;

        var hierarchies = resource.Hierarchies.ToList();
        foreach (string hierarchyName in requiredNames)
        {
            hierarchies.RemoveAll(tree => string.Equals(tree.Name,hierarchyName,StringComparison.OrdinalIgnoreCase));
            if (!TryResolveExternalHierarchy(retailAssets, hierarchyName, out EnbW3dRenderHierarchy hierarchy, out diagnostic))
                return false;
            hierarchies.Add(hierarchy);
        }

        bound = resource.Copy().SetHierarchies(hierarchies.ToArray());
        return true;
    }

    private bool TryResolveExternalHierarchy(
        EnbRetailAssetBundle retailAssets,
        string hierarchyName,
        out EnbW3dRenderHierarchy hierarchy,
        out string? diagnostic)
    {
        //Runtime owners are created after the proven loading transition
        //enables the manager's lazy gate. Global precache uses that gate
        //separately; an owner-local default never becomes a registry entry.
        bool resolved = _prototypeResources.TryResolveHierarchy(retailAssets.W3dFiles,
            hierarchyName, lazyLoadEnabled: true, out hierarchy, out diagnostic,
            ReadMapperClock());
        //Diagnostic history is not lookup authority or a missing-tree cache.
        _externalHierarchies[hierarchyName] = new ExternalHierarchyResolution(
            resolved ? hierarchy : null, null, diagnostic);
        return resolved;
    }
    // Both scene traversal and direct child capture use the same retained
    // animation/HLOD/hierarchy inputs. Visibility belongs to their respective
    // callers: capture0098DD50 does not test the outer RSL's scene visibility.
    private bool TryBuildRetainedRootDrawList(LiveRootInstance instance,
        EnbRetailAssetBundle assets, long currentClientTime, EnbW3dLightEnvironment lightEnvironment,
        out EnbW3dDrawInvocation drawList, out string? drawDiagnostic,
        bool captureProjectedDecals = false, EnbW3dHlodSelection? captureSelection = null, bool rootAlreadyPrepared = false)
    {
        drawDiagnostic=null;
        instance.CurrentAppearancePreparation=null;
        // Main traversal has just prepared admission and pose, before inspecting
        // the root. Capture callers still perform their own preparation here.
        if(!rootAlreadyPrepared&&!TryPrepareRetainedRootDraw(instance,out drawDiagnostic)){drawList=default!;return false;}
        if(instance.Root.SpecialMaterial is not null)
            return TryBuildSpecialMaterialDrawList(instance,currentClientTime,lightEnvironment,out drawList,out drawDiagnostic);
        EnbW3dHlodSelection? selection = captureProjectedDecals ? captureSelection : instance.HlodSelection;
        EnbW3dRenderResource drawResource = captureProjectedDecals ? RetainedDrawResource(instance) : VisibleRetainedDrawResource(instance);
        drawResource = HarvestColors(drawResource,instance.Object,assets);
        drawResource=ConfiguredHierarchyResource(instance.Root,drawResource);
        var configuredPostTransforms=ConfiguredHierarchyPostTransforms(instance);
        IReadOnlyList<EnbW3dWeightedAnimationSample>? animationBlend = instance.AnimationBlend?.Samples;
        IReadOnlyList<EnbW3dWeightedAnimationSample> drawAnimations =
            animationBlend ?? Array.Empty<EnbW3dWeightedAnimationSample>();
        bool drawReady;
        var drawObject=instance.Object;
        float renderScale=instance.RenderScale;
        Matrix4x4? drawWorld=instance.WorldTransformOverride;
        if(instance.InheritedRenderObjectScale is {} meshScale&&instance.HierarchyPostTransforms is null&&instance.Root.Collection is null)
        {
            renderScale=meshScale;
            if(drawWorld is {} rigid)drawWorld=Matrix4x4.CreateScale(meshScale)*rigid;
        }
        if (captureProjectedDecals)
        {
            if (drawWorld is null)
            {
                if (!TryBuildObjectWorld(drawObject, out var objectWorld,renderScale))
                { drawList = default!; drawDiagnostic = "Projected decal receiver transform is unavailable."; return false; }
                drawWorld = objectWorld;
            }
            return EnbW3dDrawPacketCompiler.TryCaptureProjectedDecalReceivers(drawObject, drawResource,
                drawWorld.Value, currentClientTime, selection, lightEnvironment, drawAnimations,
                configuredPostTransforms, out drawList, out drawDiagnostic,instance.SkinParentPose,renderScale);
        }
        if (_retainedOrdinaryMeshes && selection is null && configuredPostTransforms is null &&
            drawAnimations.Count == 0 && EnbW3dRigidOccurrence.CanPrepare(drawResource))
        {
            if (drawWorld is null)
            {
                if (!TryBuildObjectWorld(drawObject, out var ordinaryWorld,renderScale))
                { drawList = default!; drawDiagnostic = "Rigid occurrence transform is unavailable."; return false; }
                drawWorld = ordinaryWorld;
            }
            return TrySnapshotRigidOccurrence(instance, drawObject, drawResource, drawWorld.Value,
                currentClientTime, lightEnvironment, out drawList, out drawDiagnostic,renderScale);
        }
        if (_retainedOrdinaryMeshes)
        {
            var preparation=RetainMeshDefinitions(instance);
            using var geometryScope=preparation.UseGeometryFrame(_particleStreamFrame);
            long traversalBuilds=preparation.TraversalPreparationCount, traversalUses=preparation.TraversalUseCount, skinBuilds=preparation.TransientSkinBuildCount;
            drawReady = EnbW3dDrawPacketCompiler.TryBuildPrepared(drawObject, drawResource, drawWorld,
                currentClientTime, EnbRenderPassKind.OrdinaryWorld, selection, lightEnvironment, drawAnimations,
                configuredPostTransforms, preparation, out drawList, out drawDiagnostic,
                instance.SkinParentPose,renderScale:renderScale);
            _preparedTraversalBuilds+=preparation.TraversalPreparationCount-traversalBuilds;
            _preparedTraversalUses+=preparation.TraversalUseCount-traversalUses;
            _skinFrameBuilds+=preparation.TransientSkinBuildCount-skinBuilds;
            if(drawReady)instance.CurrentAppearancePreparation=preparation;
        }
        else if (drawWorld is Matrix4x4 attachedWorld)
        {
            drawReady = configuredPostTransforms is EnbW3dHierarchyState attachedOverrides
                ? EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(
                    drawObject,
                    drawResource,
                    attachedWorld,
                    currentClientTime,
                    EnbRenderPassKind.OrdinaryWorld,
                    selection,
                    lightEnvironment,
                    drawAnimations,
                    attachedOverrides,
                    out drawList,
                    out drawDiagnostic,skinParentPose:instance.SkinParentPose,renderScale:renderScale)
                : EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(
                    drawObject,
                    drawResource,
                    attachedWorld,
                    currentClientTime,
                    EnbRenderPassKind.OrdinaryWorld,
                    selection,
                    lightEnvironment,
                    drawAnimations,
                    out drawList,
                    out drawDiagnostic,skinParentPose:instance.SkinParentPose,renderScale:renderScale);
        }
        else
        {
            if(!TryBuildObjectWorld(drawObject,out var explicitWorld,renderScale))
            {drawList=default!;drawDiagnostic="Root occurrence transform is unavailable.";return false;}
            TryBuildObjectWorld(drawObject,out var rigidWorld,1);
            var special=new EnbRetailSpecialMaterialTransform(rigidWorld,renderScale);
            drawReady=configuredPostTransforms is {} rootOverrides
                ?EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(drawObject,drawResource,explicitWorld,currentClientTime,
                    EnbRenderPassKind.OrdinaryWorld,selection,lightEnvironment,drawAnimations,rootOverrides,
                    out drawList,out drawDiagnostic,special,instance.SkinParentPose,renderScale)
                :EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(drawObject,drawResource,explicitWorld,currentClientTime,
                    EnbRenderPassKind.OrdinaryWorld,selection,lightEnvironment,drawAnimations,
                    out drawList,out drawDiagnostic,special,instance.SkinParentPose,renderScale);
        }
        bool specialInvocationCopied=false;
        if(drawReady)for(int i=0;i<drawList.Packets.Count;i++)
        {
            var packet=drawList.Packets[i];
            if(packet.SpecialMaterial is not {BindRetainedInstance:true} special)continue;
            if(!specialInvocationCopied){drawList=drawList.CopyForInvocation();specialInvocationCopied=true;}
            drawList.Packets[i]=packet.ForkTask().SetSpecialMaterial(special with
            {RetainedOwnerPath=RetainedModelOwnerPath(instance)+"/"+(special.RetainedOwnerPath??packet.RenderInstancePath),
             RetainedRslGeneration=instance.IsRsl?instance.Object.RenderObjectGeneration:null,
             RetainedPhysicalFactory=PhysicalParticleFactory(instance)});
        }
        return drawReady;
    }

    //009A4540 publishes bit10000.0098DDC7..DDE5 calls aggregate child+120
    //with parent+124 immediately before ordinary rendering, after budgeting.
    //The frame tree already retains every child; selected-level masks perform
    //the corresponding draw admission when this retained level changes.
    private void ApplyAggregateRenderLevel(LiveRootInstance instance,bool parentAlreadyPrepared=false)
    {
        if(instance.Parent is not {} parent)return;
        if(!parentAlreadyPrepared)ApplyAggregateRenderLevel(parent);
        if(!(instance.IsAggregateHlodChild||instance.IsShipLegoController||instance.IsExternalLegoChild) || !parent.Root.ForceSubObjectLod ||
            parent.Root.Hlod is not {} parentHlod || instance.Root.Hlod is not {} childHlod ||
            EnbCurrentRetailAggregateLevelRuntime.ResolvePushedLevel(parent.Root.ForceSubObjectLod,
                parent.ShouldDraw && CollectionAncestorsDraw(parent),
                parent.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(parentHlod),
                childHlod.Levels.Count) is not int level)return;
        int previous=instance.HlodSelection?.LevelOrdinal??RememberedHlodLevel(instance,childHlod);
        if(previous!=level&&_retailAssetsAccessor() is {} assets)
            PrepareHlodSceneChanges([instance],assets)(instance,level);
        else
        {
            instance.HlodSelection=new(childHlod.Name,level);
            RememberHlodLevel(instance,childHlod,level);
        }
    }

    //0098F090 assigns and recursively refreshes ordinary child owners after
    //the parent pose has incorporated animation and retained record overrides.
    private static bool TryRefreshOrdinaryHlodChild(LiveRootInstance instance,out string? diagnostic)
        =>TryRefreshOrdinaryHlodChildCore(instance,true,out diagnostic);

    // Within one preparation sweep the hierarchy inputs are fixed.
    // Sibling emitters share ancestors; refresh each ancestor once in this
    // sweep, never across an animation/callback or another preparation phase.
    private static bool TryRefreshHierarchyOnce(LiveRootInstance instance,HashSet<LiveRootInstance> refreshed,out string? diagnostic)
    {
        diagnostic=null;
        if(refreshed.Contains(instance))return true;
        if(instance.Parent is {} parent &&
            (instance.IsOrdinaryHlodChild||instance.IsAggregateHlodChild||instance.IsExternalLegoChild||parent.Root.Collection is not null) &&
            !TryRefreshHierarchyOnce(parent,refreshed,out diagnostic))return false;
        if(!TryRefreshOrdinaryHlodChildCore(instance,false,out diagnostic))return false;
        refreshed.Add(instance);
        return true;
    }

    private static bool TryRefreshOrdinaryHlodChildCore(LiveRootInstance instance,bool refreshParents,out string? diagnostic)
    {
        diagnostic=null;
        instance.SkinParentPose=null;
        if(instance.Parent is not {} parent)return true;
        if(!instance.IsOrdinaryHlodChild&&!instance.IsAggregateHlodChild&&!instance.IsExternalLegoChild&&parent.Root.Collection is null)return true;
        if(refreshParents&&!TryRefreshOrdinaryHlodChildCore(parent,true,out diagnostic))return false;
        bool ApplyIndependentFactory()
        {
            if(!instance.Root.IndependentRslFactory && instance.Root.NamedChildFactoryScale is null)return true;
            //98E4B0 transfers detail bias;98F090 supplies the rigid pivot
            //matrix. Neither repeats98EF60's earlier child-data scaling.
            if(!TryResolvePrimitiveFactoryTransform(instance,out var rigid,out float scale,out _))return false;
            instance.WorldTransformOverride=rigid;
            instance.InheritedRenderObjectScale=scale;
            if(instance.Root.AttachmentHierarchyName is {} name)
                instance.SetHierarchyState(name,rigid,scale,
                    instance.HierarchyPostTransforms?.Transforms??System.Collections.Immutable.ImmutableDictionary<int,Matrix4x4>.Empty);
            return true;
        }
        if(parent.Root.Collection is not null)
        {
            if(parent.WorldTransformOverride is {} collectionWorld)instance.WorldTransformOverride=collectionWorld;
            else if(TryBuildObjectWorld(parent.Object,out collectionWorld,parent.RenderScale))instance.WorldTransformOverride=collectionWorld;
            else{diagnostic="Collection parent transform is unavailable.";return false;}
            instance.InheritedRenderObjectScale=parent.InheritedRenderObjectScale??parent.HierarchyPostTransforms?.RenderObjectScale;
            if(instance.InheritedRenderObjectScale is {} collectionScale&&instance.Root.AttachmentHierarchyName is {} collectionHierarchy)
                instance.SetHierarchyState(collectionHierarchy,instance.WorldTransformOverride.Value,collectionScale,
                    instance.HierarchyPostTransforms?.Transforms??System.Collections.Immutable.ImmutableDictionary<int,Matrix4x4>.Empty);
            return ApplyIndependentFactory();
        }
        if(!TryResolveConfiguredAttachmentHierarchy(parent.Root,out var hierarchy,out diagnostic)||hierarchy is null)return false;
        Matrix4x4 parentWorld;
        if(parent.WorldTransformOverride is {} attached)parentWorld=attached;
        else if(!TryBuildObjectWorld(parent.Object,out parentWorld,parent.RenderScale)){diagnostic="HMODEL parent world transform is unavailable.";return false;}
        var animations=parent.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
        EnbW3dHierarchyPoseOwner pose;
        var parentPost=ConfiguredHierarchyPostTransforms(parent);
        bool scaled=parentPost is {} post&&string.Equals(post.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase);
        var poseWorld=scaled?parentPost!.RigidWorldTransform:parentWorld;
        float? poseScale=scaled?parentPost!.HierarchyTranslationScale:null;
        var poseOverrides=scaled?parentPost!.Transforms:null;
        if(!TryResolveRetainedHierarchyPose(parent,hierarchy,poseWorld,poseScale,animations,poseOverrides,out pose,out diagnostic))return false;
        //0093EFE0/0093F390 obtain the direct mesh's actual parent hierarchy.
        //Keep the full evaluated pose for skin drawing and decal capture;
        //the mount matrix below still owns ordinary bounds/placement.
        if(instance.Root.IsDirectMeshPrototype && instance.Root.Resource.Meshes.Any(mesh=>mesh.BoneIndices.Count!=0))
            instance.SkinParentPose=pose;
        if(instance.OrdinaryHlodPivot>=(uint)pose.WorldTransforms.Length){diagnostic="HMODEL child pivot is unavailable.";return false;}
        instance.WorldTransformOverride=pose.WorldTransforms[(int)instance.OrdinaryHlodPivot];
        instance.PivotVisible=instance.IsExternalRslModel || pose.Visibility[(int)instance.OrdinaryHlodPivot];
        instance.ShouldDraw=instance.PivotVisible&&
            (instance.OrdinaryHlodLevelOrdinal is null||instance.OrdinaryHlodLevelOrdinal==
                (parent.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(parent.Root.Hlod!)));
        //98E4B0 attaches the independently constructed Lego model without rescaling it.
        instance.InheritedRenderObjectScale=instance.IsExternalLegoChild?instance.Root.ConfiguredFactoryInputs?.Scale??1:scaled?parentPost!.RenderObjectScale:null;
        if(scaled&&instance.Root.AttachmentHierarchyName is {} childHierarchy)
            instance.SetHierarchyState(childHierarchy,instance.WorldTransformOverride.Value,
                instance.InheritedRenderObjectScale!.Value,instance.HierarchyPostTransforms?.Transforms??System.Collections.Immutable.ImmutableDictionary<int,Matrix4x4>.Empty);
        return ApplyIndependentFactory();
    }

    private int _retainedEmitterBudgetCount;

    private LiveHlodSelectionStats ApplyHlodSelections(
        EnbResolvedRenderCamera camera,
        EnbRenderDetailRuntimeSettings detailSettings,
        RootTaskList roots,
        List<LiveHlodRejection> rejections,
        LiveShipLegoFrameTracker shipTracker,
        IReadOnlyList<EnbCurrentRetailEffectOwner> effectNodes, IEnbSceneFrame snapshot, EnbRetailAssetBundle assets, uint currentClientTime)
    {
        using var hlodStages=FullClientBenchmarkMeter.BeginStages(FullClientBenchmarkMeter.WorldStagesEnabled,"hlod-phase.setup");
        var candidates = _hlodDetailOwners;candidates.Clear();
        var candidateOwners = _hlodCandidateRoots;candidateOwners.Clear();
        var selectSceneLevel=PrepareHlodSceneChanges(roots,assets);
        var candidateShipDecisions = _hlodCandidateDecisions;candidateShipDecisions.Clear();
        float fixedCost = DebrisBudgetCost;
        _retainedEmitterBudgetCount=0;
        int total = 0;
        int selected = 0;
        int culled = 0;
        int failed = 0;
        string? firstFailure = null;

        //Finish the retained pose walk before gathering. Refreshing a parent
        //while visiting its child must not erase a culling decision already made.
        var refreshRejected=new HashSet<LiveRootInstance>();
        using var refreshScope=_emitterPreparationScratch.Rent();
        var refreshedHierarchy=refreshScope.Refreshed;
        hlodStages?.Next("hlod-phase.hierarchy-refresh");
        foreach(var instance in roots)
        {
            //810750's child flags precede scene +104 detail gathering, not
            //only draw submission. Newly staged children start with no flags.
            instance.EngineChildHidden=!instance.IsExternalRslModel && (instance.IsOrdinaryHlodChild||instance.IsAggregateHlodChild||instance.IsExternalLegoChild)&&!RetainedEngineChildVisible(instance);
            if(!TryRefreshHierarchyOnce(instance,refreshedHierarchy,out string? childRefreshDiagnostic))
            {instance.ShouldDraw=false;refreshRejected.Add(instance);failed++;firstFailure??=childRefreshDiagnostic;}
        }
        hlodStages?.Next("hlod-phase.ordinary-candidates");
        foreach (LiveRootInstance instance in roots)
        {
            if(refreshRejected.Contains(instance))continue;
            if(HasCollectionAncestor(instance))
            {
                //Class23 gathers current cost; ordinary HLOD children are
                //excluded by98E9A0's aggregate-only recursive preparation.
                //Keep their retained level, including a previous Render-time
                //forced setter; skipping +104 does not reconstruct the HLOD.
                if(instance.Root.Hlod is {} childHlod)instance.HlodSelection=new(childHlod.Name,
                    RememberedHlodLevel(instance,childHlod));
                continue;
            }
            if(instance.IsAggregateHlodChild&&!CollectionAncestorsDraw(instance))
            {
                if(instance.Root.Hlod is {} retainedHlod)
                {
                    instance.HlodSelection=new(retainedHlod.Name,RememberedHlodLevel(instance,retainedHlod));
                }
                continue;
            }
            if(instance.Root.Collection is not null)
            {
                Matrix4x4 collectionWorld;
                bool collectionWorldReady=instance.WorldTransformOverride is {} supplied ? IsFinite(collectionWorld=supplied) : TryBuildObjectWorld(instance.Object,out collectionWorld,instance.RenderScale);
                string? collectionDiagnostic=null;
                if(!collectionWorldReady || !TryComputeRootWorldSphere(instance.RetainedQueryRoot??instance.Root,collectionWorld,out var collectionSphere,out collectionDiagnostic) ||
                    !TryComputeRootIntegerCost(instance.Root,out int collectionCost,out collectionDiagnostic))
                { instance.ShouldDraw=false;failed++;firstFailure??=collectionWorldReady?collectionDiagnostic:"Collection world transform is unavailable.";continue; }
                instance.CurrentRetailWorldSphere=collectionSphere;
                if(FrustumCullingEnabled&&!instance.IsAggregateHlodChild&&!instance.ForcesSceneVisibility&&!EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(instance.OrbitalCamera??camera,collectionSphere))
                { instance.ShouldDraw=false;culled++;continue; }
                fixedCost=EnbCurrentRetailFixedRenderCost.AddDefaultIntegerCost(fixedCost,collectionCost);
                continue;
            }
            EnbW3dRenderHlod? hlod = instance.Root.Hlod;
            if (hlod is null)
            {
                //98E9A0 recursively calls permanent-child +104. A retained
                //default-cost leaf inherits942020/942040. Mesh, Box, NULL,
                //dazzle, SpecialMaterial and emitter use their own +20 cost;
                //zero contributes1e-6. Sphere/ring have separate allocators.
                //These described children are excluded from the parent's
                //compiled aggregate cost and must be gathered here. Removed
                //children are absent from roots; ordinary/collection children
                //were already excluded above from recursive preparation.
                bool sceneLeaf=instance.Parent is null;
                bool stagedPermanent=instance.IsAggregateHlodChild && instance.Parent is {} leafParent &&
                    (instance.ParentStorageOrdinal is null||leafParent.Root.AuthoredGlobalOwner||leafParent.Root.HmodelChildren.Any(c=>c.IsAggregate));
                if(instance.Root.Sphere is null && instance.Root.Ring is null && (sceneLeaf||stagedPermanent))
                {
                    if(sceneLeaf)
                    {
                        //965F40 admits every scene root through +F0/+170,
                        //not just HLOD/Collection owners. Read the concrete
                        //configured storage and use its rigid object pose.
                        if(instance.Root.RenderObjectHidden||instance.EngineChildHidden)continue;
                        string? boundsError=null;
                        if(!TryResolvePrimitiveFactoryTransform(instance,out var leafWorld,out _,out _) ||
                            !TryComputeRootLocalBounds(CreateConfiguredBoundsRoot(instance.Root,1),out var local,out boundsError))
                        {instance.ShouldDraw=false;failed++;firstFailure??=boundsError??"Leaf world transform is unavailable.";continue;}
                        var stored=EnbCurrentRetailRenderBoundsStorage.Calculate(leafWorld,local.Sphere,local.BoxCenter,local.BoxExtent);
                        var sphere=new EnbW3dWorldBounds(new(stored.Sphere.X,stored.Sphere.Y,stored.Sphere.Z),stored.Sphere.W);
                        instance.CurrentRetailWorldSphere=sphere;
                        if(FrustumCullingEnabled&&!instance.ForcesSceneVisibility&&!EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(instance.OrbitalCamera??camera,sphere))
                        {instance.ShouldDraw=false;culled++;continue;}
                    }
                    if(!TryComputeRootIntegerCost(instance.Root,out int leafCost,out var leafDiagnostic))
                    {instance.ShouldDraw=false;failed++;firstFailure??=leafDiagnostic;continue;}
                    fixedCost=EnbCurrentRetailFixedRenderCost.AddDefaultIntegerCost(fixedCost,leafCost);
                    if(instance.Root.RetainedEmitter is not null)_retainedEmitterBudgetCount++;
                }
                continue;
            }

            total++;
            int initialLevel=RememberedHlodLevel(instance,hlod);
            // Direct capture0098DD50 uses retained +E8 even when scene detail
            // gathering rejects the outer object. Culling must not erase it.
            instance.HlodSelection = new(hlod.Name, initialLevel);
            LiveShipHlodDecision? shipDecision = instance.Object.IsLocalShip
                ? new LiveShipHlodDecision(
                    instance.InstancePath,
                    instance.Root.Resource.LogicalFileName,
                    hlod.Name,
                    hlod.Levels.Count,
                    initialLevel)
                : null;
            if (shipDecision is not null)
            {
                shipDecision.HierarchyOverrideCount = instance.HierarchyPostTransforms?.Transforms.Count ?? 0;
                instance.ShipHlodDecision = shipDecision;
                shipTracker.HlodDecisions.Add(shipDecision);
            }

            Matrix4x4 world;
            bool worldReady;
            if (instance.WorldTransformOverride is Matrix4x4 attachedWorld)
            {
                world = attachedWorld;
                worldReady = IsFinite(world);
            }
            else
            {
                worldReady = TryBuildObjectWorld(instance.Object, out world,instance.RenderScale);
            }
            if (!worldReady)
            {
                instance.ShouldDraw = false;
                failed++;
                string reason = $"HLOD '{hlod.Name}' object transform is not finite.";
                if (shipDecision is not null)
                    shipDecision.Outcome = "world-reject";
                firstFailure ??= reason;
                rejections.Add(CreateHlodRejection(instance, hlod.Name, reason));
                continue;
            }
            instance.CurrentRetailWorldOrigin = new Vector3(world.M41, world.M42, world.M43);

            IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> boundsAnimations =
                instance.AnimationBlend?.ResolvedSamples ?? Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
            bool boundsReady = TryResolveHlodCompositeBounds(
                instance,
                hlod,
                world,
                boundsAnimations,
                out EnbW3dHlodBoundsOwner bounds,
                out string? boundsDiagnostic);
            if (!boundsReady)
            {
                instance.ShouldDraw = false;
                failed++;
                string reason = boundsDiagnostic ?? $"HLOD '{hlod.Name}' bounds compilation failed.";
                if (shipDecision is not null)
                    shipDecision.Outcome = "bounds-reject";
                firstFailure ??= reason;
                rejections.Add(CreateHlodRejection(instance, hlod.Name, reason));
                continue;
            }

            if (shipDecision is not null)
            {
                shipDecision.WorldDistance = Vector3.Distance(camera.Position, bounds.WorldSphere.Center);
                shipDecision.WorldRadius = bounds.WorldSphere.Radius;
            }
            instance.CurrentRetailWorldSphere = bounds.WorldSphere;
            instance.CurrentRetailLocalBox = bounds.LocalBox;
            instance.CurrentRetailBoundsWorld = instance.HierarchyPostTransforms?.RigidWorldTransform ?? world;

            //965F40 culls scene roots.98E9A0 recursively gathers permanent
            //children through +104/+174 without a second frustum test.
            //Independent particle buffers retain their own scene admission.
            if (FrustumCullingEnabled && !instance.IsAggregateHlodChild && !instance.ForcesSceneVisibility && !EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(instance.OrbitalCamera ?? camera,
                    bounds.WorldSphere, ConfiguredHierarchyPostTransforms(instance) is null ? instance.RenderScale : 1f))
            {
                instance.ShouldDraw = false;
                culled++;
                if (shipDecision is not null)
                    shipDecision.Outcome = "culled";
                continue;
            }

            if (!TryResolveRetainedHlodCompiledCosts(
                    instance,
                    out ImmutableArray<int> levelCosts,
                    out float aggregateCost,
                    out string? costDiagnostic))
            {
                instance.ShouldDraw = false;
                failed++;
                string reason = costDiagnostic ?? $"HLOD '{hlod.Name}' render-cost compilation failed.";
                if (shipDecision is not null)
                    shipDecision.Outcome = "cost-reject";
                firstFailure ??= reason;
                rejections.Add(CreateHlodRejection(instance, hlod.Name, reason));
                continue;
            }

            if (shipDecision is not null)
            {
                shipDecision.LevelCosts = levelCosts;
                shipDecision.AggregateCost = aggregateCost;
            }

            fixedCost += aggregateCost;
            if (_mapModelDetail)
            {
                // 00497700 RObj2Texture forces count-1 for the map's RenderObj image.
                int level=hlod.Levels.Count-1;
                selectSceneLevel(instance,level);
                fixedCost+=EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(levelCosts[level]);
                selected++; continue;
            }
            if (hlod.Levels.Count == 1)
            {
                float levelCost = EnbW3dHlodBudgetCompiler.CurrentRetailLevelCost(levelCosts[0]);
                fixedCost += levelCost;
                selectSceneLevel(instance,0);
                if (shipDecision is not null)
                {
                    shipDecision.Outcome = "fixed";
                    shipDecision.MinimumLevel = 0;
                    shipDecision.SelectedLevel = 0;
                }
                selected++;
                continue;
            }

            if (!TryCreateOwnedHlodBudgetCandidate(
                    instance, hlod,
                    initialLevel,
                    instance.OrbitalCamera ?? camera,
                    bounds.WorldSphere,
                    levelCosts,
                    lodScale: instance.Root.LodBias,
                    out EnbHlodDetailOwner candidate,
                    out string? candidateDiagnostic))
            {
                instance.ShouldDraw = false;
                failed++;
                string reason = candidateDiagnostic ?? $"HLOD '{hlod.Name}' budget candidate creation failed.";
                if (shipDecision is not null)
                    shipDecision.Outcome = "candidate-reject";
                firstFailure ??= reason;
                rejections.Add(CreateHlodRejection(instance, hlod.Name, reason));
                continue;
            }

            if (shipDecision is not null)
            {
                shipDecision.Outcome = "candidate";
                shipDecision.CurrentLevel = candidate.CurrentLevelOrdinal;
                shipDecision.MinimumLevel = candidate.MinimumLevelOrdinal;
                shipDecision.ProjectedDetail = candidate.ProjectedDetail;
            }
            candidates.Add(candidate);
            selectSceneLevel(instance,candidate.CurrentLevelOrdinal);
            candidateOwners.Add(instance);
            candidateShipDecisions.Add(shipDecision);
        }

        hlodStages?.Next("hlod-phase.generated-candidates");
        if(!effectNodes.Any(n=>n.TryGetRObjDefinition(out var d,out _) &&
            assets.PresentationCatalog!.ResolveBaseAsset(d.BaseAssetId).Kind==EnbBaseAssetDefinitionKind.Rsl))
            UpdateCombatEffectPoses(effectNodes,snapshot,assets,roots,currentClientTime);
        var effectRadii = BuildCombatEffectRadii(roots,assets,SceneObjects(snapshot));
        var effectCandidates = PrepareEffectHlods(effectNodes, snapshot, assets, effectRadii, camera, currentClientTime, out float fixedEffectCost);
        var ringCandidates = PrepareEffectRings(effectNodes,snapshot,assets,effectRadii,camera,currentClientTime);
        var sphereCandidates = PrepareEffectSpheres(effectNodes,snapshot,assets,effectRadii,camera,currentClientTime);
        ObserveRetainedWorldSpheres(roots);
        var worldSphereCandidates = PrepareWorldSpheres(camera);
        ObserveRetainedWorldRings(roots);
        var worldRingCandidates=PrepareWorldRings(camera);
        var particleCandidates=PrepareRetainedEmitterCandidates(camera,currentClientTime);
        var compositeParticleCandidates=PrepareEffectCompositeParticleCandidates(camera,currentClientTime);
        var jetCandidates=PrepareParticleJetCandidates(effectNodes,camera,currentClientTime);
        var exhaustCandidates=PrepareExhaustCandidates(camera,currentClientTime,snapshot);
        fixedCost+=AttachmentCompositeCost();
        var attachmentParticleCandidates=PrepareAttachmentParticleCandidates(camera,currentClientTime,snapshot);
        var tunnelCandidates=PrepareWarpTunnelCandidates(effectNodes,snapshot,assets,camera,currentClientTime,out float tunnelFixedCost);
        fixedCost+=tunnelFixedCost;
        var lineCandidates = PrepareEffectLines(effectNodes,assets,effectRadii,camera,currentClientTime,out float fixedLineCost);
        int ordinaryCandidateCount = candidates.Count;
        candidates.AddRange(effectCandidates.Select(c => c.Candidate));
        fixedCost += fixedEffectCost + effectCandidates.Sum(c => c.FixedCost) + fixedLineCost;
        float budget = _renderingMovie is not null ? 50000 : detailSettings.RemainingSpacePolyBudget(0f);
        hlodStages?.Next("hlod-phase.shared-inputs");
        var sharedCandidates = _sharedDetailOwners;sharedCandidates.Clear();
        foreach(var candidate in candidates)sharedCandidates.Add(EnbRenderDetailBudgetAllocator.AdaptHlodCandidate(candidate));
        foreach(var item in ringCandidates)sharedCandidates.Add(item.Candidate);
        foreach(var item in sphereCandidates)sharedCandidates.Add(item.Candidate);
        foreach(var item in worldSphereCandidates)sharedCandidates.Add(item.Candidate);
        foreach(var item in worldRingCandidates)sharedCandidates.Add(item.Candidate);
        foreach(var item in lineCandidates)sharedCandidates.Add(item.Candidate);
        foreach(var item in _effectObjectLineCandidates)sharedCandidates.Add(item.Candidate);
        int particleCandidateOffset=sharedCandidates.Count;
        foreach(var item in particleCandidates)sharedCandidates.Add(item.Candidate);
        int compositeParticleCandidateOffset=sharedCandidates.Count;
        foreach(var item in compositeParticleCandidates)sharedCandidates.Add(item.Candidate);
        int jetCandidateOffset=sharedCandidates.Count;
        foreach(var item in jetCandidates)sharedCandidates.Add(item.Candidate);
        int exhaustCandidateOffset=sharedCandidates.Count;
        foreach(var item in exhaustCandidates)sharedCandidates.Add(item.Candidate);
        int attachmentParticleOffset=sharedCandidates.Count;
        foreach(var item in attachmentParticleCandidates)sharedCandidates.Add(item.Candidate);
        int tunnelCandidateOffset=sharedCandidates.Count;
        foreach(var item in tunnelCandidates)sharedCandidates.Add(item.Candidate);
        hlodStages?.Next("hlod-phase.allocate");
        float finalRenderCost = EnbRenderDetailBudgetAllocator.AllocateInPlace(
            budget,
            sharedCandidates,
            _detailWorkspace,
            fixedCost,
            levelChanged:(index,level)=>
            {
                if(index<ordinaryCandidateCount)selectSceneLevel(candidateOwners[index],level);
                else if(index<ordinaryCandidateCount+effectCandidates.Count)
                    SetEffectHlodSceneLevel(effectCandidates[index-ordinaryCandidateCount].Key,level);
            });
        hlodStages?.Next("hlod-phase.publish");
        for (int index = 0; index < ordinaryCandidateCount; index++)
        {
            LiveRootInstance owner = candidateOwners[index];
            var chosen = sharedCandidates[index];
            int selectedLevel=chosen.CurrentLevelOrdinal;
            selectSceneLevel(owner,selectedLevel);
            if (candidateShipDecisions[index] is LiveShipHlodDecision decision)
                decision.SelectedLevel = selectedLevel;
            selected++;
        }

        for (int i = 0; i < effectCandidates.Count; i++)
        {
            var chosen = sharedCandidates[ordinaryCandidateCount + i];
            SetEffectHlodSceneLevel(effectCandidates[i].Key,chosen.CurrentLevelOrdinal);
        }

        for(int i=0;i<ringCandidates.Count;i++)
            ringCandidates[i].Owner.Levels[ringCandidates[i].Ordinal]=sharedCandidates[candidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<sphereCandidates.Count;i++)
            sphereCandidates[i].Owner.Levels[sphereCandidates[i].Ordinal]=sharedCandidates[candidates.Count+ringCandidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<worldSphereCandidates.Count;i++)
            worldSphereCandidates[i].Owner.Level=sharedCandidates[candidates.Count+ringCandidates.Count+sphereCandidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<worldRingCandidates.Count;i++)
            worldRingCandidates[i].Owner.Level=sharedCandidates[candidates.Count+ringCandidates.Count+sphereCandidates.Count+worldSphereCandidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<lineCandidates.Count;i++)
            _effectLineLevels[lineCandidates[i].Key]=sharedCandidates[candidates.Count+ringCandidates.Count+sphereCandidates.Count+worldSphereCandidates.Count+worldRingCandidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<_effectObjectLineCandidates.Count;i++)
            _effectObjectLineLevels[_effectObjectLineCandidates[i].Key]=sharedCandidates[candidates.Count+ringCandidates.Count+sphereCandidates.Count+worldSphereCandidates.Count+worldRingCandidates.Count+lineCandidates.Count+i].CurrentLevelOrdinal;
        for(int i=0;i<particleCandidates.Count;i++)
            particleCandidates[i].Owner.Level=sharedCandidates[particleCandidateOffset+i].CurrentLevelOrdinal;
        for(int i=0;i<compositeParticleCandidates.Count;i++)
            compositeParticleCandidates[i].Owner.Level=sharedCandidates[compositeParticleCandidateOffset+i].CurrentLevelOrdinal;

        for(int i=0;i<jetCandidates.Count;i++)jetCandidates[i].Owner.Level=sharedCandidates[jetCandidateOffset+i].CurrentLevelOrdinal;
        for(int i=0;i<exhaustCandidates.Count;i++)exhaustCandidates[i].Owner.Level=sharedCandidates[exhaustCandidateOffset+i].CurrentLevelOrdinal;
        for(int i=0;i<attachmentParticleCandidates.Count;i++)attachmentParticleCandidates[i].Owner.Level=sharedCandidates[attachmentParticleOffset+i].CurrentLevelOrdinal;
        for(int i=0;i<tunnelCandidates.Count;i++)tunnelCandidates[i].Owner.Level=sharedCandidates[tunnelCandidateOffset+i].CurrentLevelOrdinal;

        shipTracker.HlodFixedCost = fixedCost;
        shipTracker.HlodFinalCost = finalRenderCost;
        shipTracker.HlodCandidateCount = sharedCandidates.Count;

        return new LiveHlodSelectionStats(
            total,
            selected,
            culled,
            failed,
            budget,
            fixedCost,
            finalRenderCost,
            sharedCandidates.Count,
            firstFailure);
    }

    private bool TryResolveHlodCompositeBounds(
        LiveRootInstance instance,
        EnbW3dRenderHlod hlod,
        Matrix4x4 world,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,
        out EnbW3dHlodBoundsOwner bounds,
        out string? diagnostic)
    {
        var hierarchyResource=ConfiguredHierarchyResource(instance.Root);
        var hierarchyPostTransforms=ConfiguredHierarchyPostTransforms(instance);
        if(_retainedOrdinaryMeshes)
        {
            var source=instance.RetainedQueryRoot??instance.Root;
            var resource=ReferenceEquals(source,instance.Root)?hierarchyResource:ConfiguredHierarchyResource(source);
            var selected=instance.RetainedQueryRoot is not null?source.Hlod!:hlod;
            var prepared=PrepareOwnedHlodBounds(instance,source,resource,selected);
            if(!prepared.HasBoundingBoxOverride)
                return TryResolveRetainedQueryBounds(prepared.State,resource,selected,world,hierarchyPostTransforms,out bounds,out diagnostic,prepared.Bounds);
            // BOUNDINGBOX is the proven live-pose exception (0098CF90).
            // Its animation is still evaluated on every invocation.
            return hierarchyPostTransforms is {} livePost
                ?EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,selected,world,animations,livePost,out bounds,out diagnostic,prepared.Bounds)
                :EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,selected,world,animations,out bounds,out diagnostic,prepared.Bounds);
        }
        if(instance.RetainedQueryRoot is {} retained)
        {
            //Membership changes invalidate this occurrence's native bounds,
            //not the shared prototype cache or another occurrence's children.
            var retainedHlod=retained.Hlod!;
            var retainedResource=ConfiguredHierarchyResource(retained);
            var samples=EnbW3dHlodCompositeBoundsCompiler.HasBoundingBoxOverride(retainedResource,retainedHlod)
                ?animations:Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
            return hierarchyPostTransforms is {} post
                ?EnbW3dHlodCompositeBoundsCompiler.TryCompute(retainedResource,retainedHlod,world,samples,post,out bounds,out diagnostic)
                :EnbW3dHlodCompositeBoundsCompiler.TryCompute(retainedResource,retainedHlod,world,samples,out bounds,out diagnostic);
        }
        // Ordinary aggregate bounds are base-pose data (0098F1D0), even
        // for an animated RenderObj. Keep them on the local-bounds cache;
        // feeding live poses here makes the scanner camera orbit and zoom.
        if (EnbW3dHlodCompositeBoundsCompiler.HasBoundingBoxOverride(hierarchyResource,hlod))
            return hierarchyPostTransforms is {} specialOverrides
                ? EnbW3dHlodCompositeBoundsCompiler.TryCompute(hierarchyResource,hlod,world,animations,specialOverrides,out bounds,out diagnostic)
                : EnbW3dHlodCompositeBoundsCompiler.TryCompute(hierarchyResource,hlod,world,animations,out bounds,out diagnostic);
        animations = Array.Empty<EnbW3dResolvedWeightedAnimationSample>();

        if (animations.Count == 0 && hierarchyPostTransforms is null)
        {
            var key = new LiveStaticHlodKey(_assetGeneration, instance.Root.Resource.Key, hlod.Name);
            if (!_staticHlodBounds.TryGetValue(key, out EnbW3dHlodBoundsOwner? localBounds))
            {
                if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                        instance.Root.Resource,
                        hlod,
                        Matrix4x4.Identity,
                        animations,
                        out localBounds,
                        out diagnostic))
                {
                    bounds = default!;
                    return false;
                }
                _staticHlodBounds[key] = localBounds;
            }

            Vector4 sphere=EnbCurrentRetailRenderBoundsStorage.Calculate(world,new(localBounds.LocalSphere.Center,localBounds.LocalSphere.Radius),localBounds.LocalBox.Center,localBounds.LocalBox.Extents).Sphere;
            var worldSphere = new EnbW3dWorldBounds(new(sphere.X,sphere.Y,sphere.Z),sphere.W);
            bounds = new(localBounds.LocalBox,localBounds.LocalSphere,worldSphere);
            diagnostic = worldSphere.IsValid ? null : $"W3D HLOD '{hlod.Name}' produced invalid cached world bounds.";
            return diagnostic is null;
        }

        if (animations.Count == 0 &&
            hierarchyPostTransforms is EnbW3dHierarchyState staticPostTransforms)
        {
            if (!IsFinite(world))
            {
                bounds = default!;
                diagnostic = $"W3D HLOD '{hlod.Name}' received a non-finite scene transform.";
                return false;
            }

            var key = new LivePostTransformHlodKey(
                _assetGeneration,
                instance.Object.RenderIdentity,
                instance.Object.VisualGeneration,
                instance.Root.Resource.Key,
                hlod.Name,
                RetainedModelOwnerPath(instance));
            if (!_postTransformHlodBounds.TryGetValue(key, out LivePostTransformHlodBounds? cached) ||
                !cached.Matches(staticPostTransforms))
            {
                // HLOD +0x1BC owns a local-bounds cache. Rigid placement only
                // participates in +0xF0's world-sphere refresh, so compile the
                // immutable local state once and keep per-frame movement out of
                // the cache key.
                var localPostTransforms=staticPostTransforms;
                if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                        hierarchyResource,
                        hlod,
                        Matrix4x4.Identity,
                        animations,
                        localPostTransforms,
                        out EnbW3dHlodBoundsOwner localBounds,
                        out diagnostic,localBoundsQuery:true))
                {
                    bounds = default!;
                    return false;
                }

                cached = LivePostTransformHlodBounds.Create(staticPostTransforms, localBounds);
                _postTransformHlodBounds[key] = cached;
            }

            return cached.TryResolveWorldSphere(
                hlod.Name,
                staticPostTransforms.RigidWorldTransform,
                out bounds,
                out diagnostic);
        }

        return hierarchyPostTransforms is EnbW3dHierarchyState boundsOverrides
            ? EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                hierarchyResource,
                hlod,
                world,
                animations,
                boundsOverrides,
                out bounds,
                out diagnostic)
            : EnbW3dHlodCompositeBoundsCompiler.TryCompute(
                hierarchyResource,
                hlod,
                world,
                animations,
                out bounds,
                out diagnostic);
    }

    private bool TryResolveHlodCompiledCosts(
        EnbW3dRenderResource resource,
        EnbW3dRenderHlod hlod,
        bool recursiveAggregateOwners,
        out ImmutableArray<int> levelCosts,
        out float aggregateCost,
        out string? diagnostic)
    {
        aggregateCost = 0f;
        var key = new LiveStaticHlodKey(_assetGeneration, resource.Key, hlod.Name,recursiveAggregateOwners);
        if (_hlodCompiledCosts.TryGetValue(key, out LiveHlodCompiledCosts cached))
        {
            levelCosts = cached.LevelCosts;
            aggregateCost = cached.AggregateCost;
            diagnostic = null;
            return true;
        }

        if (!EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
                resource,
                hlod,
                static _ => true,
                out levelCosts,
                out diagnostic) ||
            !EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(
                resource,
                hlod,
                member=>!recursiveAggregateOwners||!resource.CompositeRenderChildren.Any(child=>string.Equals(child.Name,member.Name,StringComparison.OrdinalIgnoreCase)),
                out aggregateCost,
                out diagnostic))
        {
            return false;
        }

        _hlodCompiledCosts[key] = new LiveHlodCompiledCosts(levelCosts, aggregateCost);
        return true;
    }

    private static LiveHlodRejection CreateHlodRejection(
        LiveRootInstance instance,
        string hlodName,
        string diagnostic)
        => new(
            instance.Object.BaseAsset,
            instance.Object.ObjectType,
            instance.IsRsl,
            instance.Root.Resource.Key.ResourceName,
            hlodName,
            TryExtractQuotedHlodMemberName(diagnostic),
            diagnostic);

    private static string? TryExtractQuotedHlodMemberName(string diagnostic)
    {
        const string marker = " member '";
        int start = diagnostic.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += marker.Length;
        int end = diagnostic.IndexOf('\'', start);
        return end > start ? diagnostic[start..end] : null;
    }

    private void InspectAndCaptureHlodPrototypeEvidence(
        EnbMixResourceCatalog? resources,
        IReadOnlyList<LiveHlodRejection> rejections)
    {
        if (resources is null)
            return;

        foreach (LiveHlodRejection rejection in rejections)
        {
            if (string.IsNullOrWhiteSpace(rejection.MemberName) || string.IsNullOrWhiteSpace(rejection.ResourceName))
                continue;

            string memberName = rejection.MemberName!;
            string key = $"{rejection.ResourceName}|{memberName}";
            if (_hlodPrototypeEvidence.ContainsKey(key))
                continue;

            if (!resources.TryReadResource(rejection.ResourceName, out byte[] bytes, out EnbMixResourceLocation location))
            {
                _hlodPrototypeEvidence[key] = new LiveHlodPrototypeEvidence(
                    rejection.ResourceName,
                    memberName,
                    "resource could not be read from the effective MIX catalog",
                    CapturePath: null);
                continue;
            }

            string probeSummary;
            if (!EnbW3dTopLevelChunkProbe.TryInspect(
                    bytes,
                    memberName,
                    out EnbW3dTopLevelChunkProbeResult probe,
                    out string? probeDiagnostic))
            {
                probeSummary = $"top-level structural probe failed: {probeDiagnostic ?? "unknown"}";
            }
            else
            {
                string topLevel = probe.Chunks.Length == 0
                    ? "none"
                    : string.Join(
                        ",",
                        probe.Chunks.Take(24).Select(static chunk =>
                            $"0x{chunk.Type:X4}{(chunk.IsContainer ? "C" : "L")}@0x{chunk.HeaderOffset:X}")) +
                      (probe.Chunks.Length > 24 ? $",+{probe.Chunks.Length - 24}" : string.Empty);
                string matches = probe.Matches.Length == 0
                    ? "none"
                    : string.Join(
                        ",",
                        probe.Matches.Take(24).Select(static match =>
                            $"0x{match.ChunkType:X4}{(match.IsContainer ? "C" : "L")}@0x{match.MatchOffset:X}")) +
                      (probe.Matches.Length > 24 ? $",+{probe.Matches.Length - 24}" : string.Empty);
                probeSummary = $"top [{topLevel}] member-token chunks [{matches}]";
            }

            string? capturePath = TryCaptureHlodResearchResource(location, bytes, memberName, probeSummary, out string? captureDiagnostic);
            if (capturePath is null && !string.IsNullOrWhiteSpace(captureDiagnostic))
                probeSummary += $" capture failed: {captureDiagnostic}";

            _hlodPrototypeEvidence[key] = new LiveHlodPrototypeEvidence(
                location.Name,
                memberName,
                probeSummary,
                capturePath);
        }
    }

    private string? TryCaptureHlodResearchResource(
        EnbMixResourceLocation location,
        byte[] bytes,
        string memberName,
        string probeSummary,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!EnbDiagnosticFiles.Enabled) return null;
        string captureKey = location.Name;
        if (_capturedHlodResearchResources.Contains(captureKey))
        {
            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            string safeStem = System.IO.Path.GetFileNameWithoutExtension(location.Name);
            return System.IO.Path.Combine(outputDirectory, $"hlod-{safeStem}-research-bundle.zip");
        }

        try
        {
            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            Directory.CreateDirectory(outputDirectory);
            string safeFileName = System.IO.Path.GetFileName(location.Name);
            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = "hlod-research.w3d";
            string safeStem = System.IO.Path.GetFileNameWithoutExtension(safeFileName);
            if (string.IsNullOrWhiteSpace(safeStem))
                safeStem = "hlod-research";
            string zipPath = System.IO.Path.Combine(outputDirectory, $"hlod-{safeStem}-research-bundle.zip");
            if (EnbNext.Installation.EnbPathCase.FileExists(zipPath))
                File.Delete(zipPath);

            string sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                ZipArchiveEntry w3dEntry = archive.CreateEntry(safeFileName, CompressionLevel.Optimal);
                using (Stream stream = w3dEntry.Open())
                    stream.Write(bytes);

                ZipArchiveEntry hashEntry = archive.CreateEntry(safeFileName + ".sha256.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(hashEntry.Open()))
                    writer.WriteLine($"{sha256}  {safeFileName}");

                ZipArchiveEntry manifestEntry = archive.CreateEntry("manifest.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(manifestEntry.Open()))
                {
                    writer.WriteLine("research=R6Z3_LIVE_HLOD_PROTOTYPE_AND_LANE_CENSUS");
                    writer.WriteLine($"resource={location.Name}");
                    writer.WriteLine($"archive={location.ArchiveName}");
                    writer.WriteLine($"dataOffset={location.DataOffset}");
                    writer.WriteLine($"dataLength={location.DataLength}");
                    writer.WriteLine($"sha256={sha256}");
                    writer.WriteLine($"hlodMember={memberName}");
                    writer.WriteLine($"probe={probeSummary}");
                    writer.WriteLine("currentRetailStaticEmitterLoaderChunk=0x0500");
                }
            }

            _capturedHlodResearchResources.Add(captureKey);
            return zipPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            diagnostic = $"{ex.GetType().Name}: {ex.Message}";
            return null;
        }
    }

    private static bool TryBuildObjectWorld(IEnbRenderObject obj, out Matrix4x4 world, float? scaleOverride=null)
    {
        float scale=scaleOverride??obj.Scale;
        world = default;
        if (obj.Spatial?.Transform is not IEnbRenderTransform transform ||
            !float.IsFinite(scale) ||
            !IsFinite(transform.Position) ||
            !IsFinite(transform.Orientation))
        {
            return false;
        }

        if (!EnbCurrentRetailSpatialTransformRuntime.TryBuildRigidWorld(transform,
                out Matrix4x4 rigidWorld,
                out _))
        {
            return false;
        }

        world = Matrix4x4.CreateScale(scale) * rigidWorld;
        return IsFinite(world);
    }

    private LiveLaneKind SelectLane(EnbW3dIndexedSubmissionPlan submission)
    {
        if (submission.Runs.IsDefaultOrEmpty)
            return LiveLaneKind.Unsupported;
        EnbW3dCompiledMaterialSemantics semantics = submission.Runs[0].Binding.Semantics;
        byte stages = semantics.PermutationKey.TextureStageCount;
        // The unified GPU lane also admits zero-stage lit/unlit materials.
        // It owns optional subdivision for every ordinary mesh material.
        if (stages <= 2 && EnbW3dNPatchCompiler.IsEligible(submission.Packet,
            _texturedBackend.NPatchCapabilityEnabled, _texturedBackend.NPatchSegments))
            return LiveLaneKind.Textured;
        if (stages == 0)
        {
            if(submission.Runs.Any(run=>run.Binding.Semantics.Mapper0Program.Kind!=EnbW3dMapperRuntimeProgramKind.Disabled ||
                run.Binding.Semantics.Mapper1Program.Kind!=EnbW3dMapperRuntimeProgramKind.Disabled))return LiveLaneKind.Textured;
            return semantics.VertexLighting.LightingEnabled ? LiveLaneKind.Lit : LiveLaneKind.Unlit;
        }
        if (stages is 1 or 2)
            return LiveLaneKind.Textured;
        return LiveLaneKind.Unsupported;
    }

    private static uint ComputeStableParticleEmitterSalt(string instancePath)
    {
        uint hash = 2166136261u;
        foreach (char value in instancePath)
        {
            hash ^= char.ToUpperInvariant(value);
            hash *= 16777619u;
        }
        return hash;
    }

    private static string DescribeUnsupportedLane(EnbW3dIndexedSubmissionPlan submission)
    {
        if (submission.Runs.IsDefaultOrEmpty)
            return "Indexed W3D submission has no material runs.";

        EnbW3dCompiledMaterialSemantics semantics = submission.Runs[0].Binding.Semantics;
        return $"No R6Q programmable lane for textureStages={semantics.PermutationKey.TextureStageCount}, lighting={semantics.VertexLighting.LightingEnabled}.";
    }

    private bool TryCompileLane(
        EnbW3dIndexedSubmissionPlan submission,
        EnbResolvedRenderCamera camera,
        EnbRenderTextureRuntimeSettings textureSettings,
        EnbRenderTextureBackendCapabilities textureCapabilities,
        EnbRetailTextureResolver textures,
        IReadOnlyDictionary<string, EnbTextureImage> nameDecalTextures,
        EnbCurrentRetailEnvironmentAtlasSet? environmentAtlasSet,
        LiveLaneKind kind,
        out LiveLanePlan lane,
        out string? diagnostic)
    {
        lane = default!;
        diagnostic = null;
        switch (kind)
        {
            case LiveLaneKind.Unlit:
                if (!EnbW3dProgrammableShaderLaneCompiler.TryCompileFirstLane(submission, camera, out EnbW3dProgrammableShaderLanePlan unlit, out diagnostic))
                    return false;
                lane = LiveLanePlan.From(unlit);
                return true;

            case LiveLaneKind.Lit:
                if (!EnbW3dProgrammableLightingLaneCompiler.TryCompile(submission, camera, out EnbW3dProgrammableLightingLanePlan lit, out diagnostic))
                    return false;
                lane = LiveLanePlan.From(lit);
                return true;

            case LiveLaneKind.Textured:
                if (!EnbW3dProgrammableTextureLaneCompiler.TryCompile(
                        submission,
                        camera,
                        textureSettings,
                        textureCapabilities,
                        binding => ResolveTextureImage(binding, textures, nameDecalTextures, environmentAtlasSet),
                        out EnbW3dProgrammableTextureLanePlan textured,
                        out diagnostic))
                {
                    return false;
                }
                lane = LiveLanePlan.From(textured);
                return true;

            default:
                diagnostic = "Unsupported live W3D lane.";
                return false;
        }
    }

    private EnbTextureImage? ResolveTextureImage(
        EnbW3dRenderTextureBinding binding,
        EnbRetailTextureResolver textures,
        IReadOnlyDictionary<string, EnbTextureImage> nameDecalTextures,
        EnbCurrentRetailEnvironmentAtlasSet? environmentAtlasSet)
    {
        if (binding.CurrentRetailResolvedTexture is {} retained) return retained.Image;
        // GPU render-target sampling borrows the capture texture; the image carries the upload.
        if (_shadowTargetsByName.ContainsKey(binding.LogicalName))
            return textures.Resolve("shadow.tga").Image;
        if (nameDecalTextures.TryGetValue(binding.LogicalName, out EnbTextureImage? nameDecalTexture)) return nameDecalTexture;
        if (environmentAtlasSet is not null &&
            environmentAtlasSet.TryResolve(binding.LogicalName, out EnbTextureImage runtimeAtlas))
        {
            return runtimeAtlas;
        }
        var sampling=binding.Sampling;
        using var acquired=binding.CurrentRetailTextureState is null && sampling.AuthoredInfoPresent && _retailAssetsAccessor() is {} assets
            ? EnbW3dRenderResourceCache.ForAssetNamespace(assets).AcquireW3dTexture(new(binding.LogicalName,
                new(sampling.AuthoredAttributes,sampling.AuthoredWord1,sampling.AuthoredWord2))) : null;
        return EnbCurrentRetailEffectMeshHsvRuntime.ResolveSourceTexture(textures,
            binding.LogicalName,binding.CurrentRetailTextureState??acquired?.State).Image;
    }

    private EnbCurrentRetailEnvironmentAtlasSet? ResolveEnvironmentAtlasSet(
        EnbRetailAssetBundle retailAssets,
        EnbEnvironmentMapCatalog catalog,
        EnbEnvironmentReflectionDefinition reflection,
        int textureReduction,
        out string? diagnostic)
    {
        if (_environmentAtlasAttempted &&
            string.Equals(_environmentAtlasReflectionTexture, reflection.Texture, StringComparison.OrdinalIgnoreCase) &&
            _environmentAtlasTextureReduction == textureReduction)
        {
            diagnostic = _environmentAtlasDiagnostic;
            return _environmentAtlasSet;
        }

        _environmentAtlasAttempted = true;
        _environmentAtlasReflectionTexture = reflection.Texture;
        _environmentAtlasTextureReduction = textureReduction;
        bool built = EnbCurrentRetailEnvironmentAtlasRuntime.TryBuild(
            catalog,
            reflection,
            textureReduction,
            logicalName => retailAssets.Textures.Resolve(logicalName).Image,
            out EnbCurrentRetailEnvironmentAtlasSet atlasSet,
            out diagnostic);
        _environmentAtlasSet = built ? atlasSet : null;
        _environmentAtlasDiagnostic = diagnostic;
        return _environmentAtlasSet;
    }


    private static string DescribeR6Z7LaneEvidence(
        EnbW3dIndexedSubmissionPlan submission,
        EnbRetailTextureResolver textures)
    {
        string[] runs = submission.Runs
            .Take(4)
            .Select(run =>
            {
                EnbW3dCompiledMaterialSemantics semantics = run.Binding.Semantics;
                EnbW3dTextureCombinerSemantics combiners = semantics.FixedFunction.TextureCombinerSemantics;
                string bindings = string.Join(
                    ",",
                    run.Binding.Textures.Select((binding, stage) =>
                    {
                        if (binding is null)
                            return $"s{stage}=<none>";
                        if (binding.IsUnbound)
                            return $"s{stage}=<unbound:0xFFFFFFFF>";
                        if (string.IsNullOrWhiteSpace(binding.LogicalName))
                            return $"s{stage}=<blank>";

                        EnbResolvedTextureResource resolved = textures.Resolve(binding.LogicalName);
                        string actual = resolved.ResourceLocation?.Name ?? "<none>";
                        string format = resolved.Image is EnbTextureImage image
                            ? $"{image.Format}/{image.Width}x{image.Height}/mips{image.MipLevels.Count}"
                            : resolved.Diagnostic ?? resolved.Status.ToString();
                        return $"s{stage}={binding.LogicalName}->{actual}:{resolved.Status}:{format}";
                    }));
                return
                    $"tri{run.FirstTriangleOrdinal} stages={semantics.PermutationKey.TextureStageCount} " +
                    $"stage0={DescribeCombinerStage(combiners.Stage0)} stage1={DescribeCombinerStage(combiners.Stage1)} tex[{bindings}]";
            })
            .ToArray();

        return $"R6Z7 evidence [{string.Join("; ", runs)}{(submission.Runs.Length > runs.Length ? $"; +{submission.Runs.Length - runs.Length} runs" : string.Empty)}]";
    }

    private static string DescribeCombinerStage(EnbW3dTextureStageSemantics stage)
        => $"C={stage.Color.Operation}/{stage.Color.Argument1}/{stage.Color.Argument2},A={stage.Alpha.Operation}/{stage.Alpha.Argument1}/{stage.Alpha.Argument2}";


    private static bool HasMutableTextureMapper(LiveLanePlan lane)
    {
        EnbW3dProgrammableTextureLanePlan? textured = lane.Textured;
        if (textured is null)
            return false;

        return textured.RunStates.Any(static run =>
            run.Mapper0Program.RequiresMutableState ||
            run.Mapper1Program.RequiresMutableState);
    }

    private bool TryCreateGpuResource(
        EnbGlApi gl,
        LiveLanePlan lane,
        LiveLaneKind kind,
        uint? mapperStartMilliseconds,
        out LiveGpuResource resource,
        out string? diagnostic,
        EnbOpenGlTextureMeshRecoveryState? recoveryState = null)
    {
        lane=lane.MaterializeFrame();
        lane.Submission.Packet.RetainAfterDrain();
        _texturedBackend.InterruptRetainedSubmission(materialChanged:false);
        resource = default!;
        diagnostic = null;
        switch (kind)
        {
            case LiveLaneKind.Unlit:
                if (!_unlitBackend.TryCreateResource(gl, lane.Unlit!, out EnbOpenGlFirstShaderMeshResource unlit, out diagnostic))
                    return false;
                resource = LiveGpuResource.From(unlit);
                return true;
            case LiveLaneKind.Lit:
                if (!_litBackend.TryCreateResource(gl, lane.Lit!, out EnbOpenGlLitShaderMeshResource lit, out diagnostic))
                    return false;
                resource = LiveGpuResource.From(lit);
                return true;
            case LiveLaneKind.Textured:
                EnbOpenGlStage0TextureMeshResource textured;
                if (mapperStartMilliseconds is uint inheritedMapperStartMilliseconds)
                {
                    if (!_texturedBackend.TryCreateResource(
                            gl,
                            lane.Textured!,
                            inheritedMapperStartMilliseconds,
                            out textured,
                            out diagnostic, recoveryState))
                    {
                        return false;
                    }
                }
                else if (!_texturedBackend.TryCreateResource(
                             gl,
                             lane.Textured!,
                             ReadMapperClock(),
                             out textured,
                             out diagnostic, recoveryState))
                {
                    return false;
                }
                resource = LiveGpuResource.From(textured);
                return true;
            default:
                diagnostic = "Unsupported live W3D lane.";
                return false;
        }
    }

    private EnbOpenGlSubmissionReceipt SubmitGpuResource(
        EnbGlApi gl,
        int fb,
        int pixelWidth,
        int pixelHeight,
        LiveGpuResource gpu,
        LiveLanePlan lane,
        EnbW3dSceneFogState sceneFog,
        EnbW3dFogPipelineSemantics fogPipeline,
        float? currentRetailPltDayNightPhase,
        uint? mapperNowMilliseconds = null,
        bool compositeOpaqueCoverage = false)
    {
        if((_stationInterior?.Fog ?? _stationShipSceneFog) is {} stationFog)
        {
            sceneFog=stationFog;
            fogPipeline=EnbW3dFogPipelineSemantics.FromCurrentRetailRangeCapability(true);
        }
        if(lane.Textured?.RunStates.LastOrDefault(r=>r.Image0 is not null) is {} samplerRun)_specialMaterialBackend.Stage0Sampler=samplerRun.Sampler;
        _specialDeviceLights=lane.Submission.Packet.LightEnvironment;
        if (gpu.Kind != LiveLaneKind.Textured) _texturedBackend.InterruptRetainedSubmission();
        switch (gpu.Kind)
        {
            case LiveLaneKind.Unlit:
            {
                EnbOpenGlFirstShaderMeshResource source = gpu.Unlit!;
                EnbW3dProgrammableShaderLanePlan currentLane = lane.Unlit!;
                EnbOpenGlFirstShaderMeshResource current = source with
                {
                    UploadPlan = source.UploadPlan with
                    {
                        LanePlan = currentLane,
                        IndexedPlan = RebindCachedDrawCommands(source.UploadPlan.IndexedPlan, lane.Submission),
                    },
                };
                return _unlitBackend.Submit(gl, fb, pixelWidth, pixelHeight, current, sceneFog, fogPipeline, rendererSupportsFog: true, clearColorAndDepth: false);
            }
            case LiveLaneKind.Lit:
            {
                EnbOpenGlLitShaderMeshResource source = gpu.Lit!;
                EnbW3dProgrammableLightingLanePlan currentLane = lane.Lit!;
                EnbOpenGlLitShaderMeshResource current = source with
                {
                    UploadPlan = source.UploadPlan with
                    {
                        LanePlan = currentLane,
                        IndexedPlan = RebindCachedDrawCommands(source.UploadPlan.IndexedPlan, lane.Submission),
                    },
                };
                return _litBackend.Submit(gl, fb, pixelWidth, pixelHeight, current, sceneFog, fogPipeline, rendererSupportsFog: true, clearColorAndDepth: false);
            }
            case LiveLaneKind.Textured:
            {
                EnbOpenGlStage0TextureMeshResource source = gpu.Textured!;
                EnbW3dProgrammableTextureLanePlan currentLane = lane.Textured!;
                EnbOpenGlIndexedUploadPlan indices = source.NPatchGeometry is null
                        ? RebindCachedDrawCommands(source.GeometryUploadPlan.IndexedPlan, lane.Submission)
                        : EnbOpenGlStage0TextureBackend.RebindNPatchCommands(source, lane.Submission);
                return _texturedBackend.Submit(
                    gl,
                    fb,
                    pixelWidth,
                    pixelHeight,
                    source,
                    mapperNowMilliseconds ?? unchecked((uint)Environment.TickCount),
                    sceneFog,
                    fogPipeline,
                    rendererSupportsFog: true,
                    clearColorAndDepth: false,
                    currentRetailPltDayNightPhase: currentRetailPltDayNightPhase,
                    compositeOpaqueCoverage: compositeOpaqueCoverage,
                    borrowedStage0Texture: lane.Submission.Packet.RenderInstancePath == "scene-dynamic-shadow" &&
                        currentLane.RunStates.Length > 0 && _shadowTargetsByName.TryGetValue(currentLane.RunStates[0].Texture0.LogicalName, out var shadowTarget)
                            ? shadowTarget : null,
                    frameLane: currentLane,
                    frameIndices: indices,
                    framePacket: lane.FrameSubmission?.Packet,
                    frameUniforms: lane.FrameUniforms, frameGeneratedView: lane.FrameGeneratedView,
                    frameProjection: lane.FrameProjection);
            }
            default:
                throw new InvalidOperationException("Unsupported cached live W3D lane.");
        }
    }

    private static EnbOpenGlIndexedUploadPlan RebindCachedDrawCommands(
        EnbOpenGlIndexedUploadPlan cached,
        EnbW3dIndexedSubmissionPlan currentSubmission)
    {
        if (cached.DrawCommands.Length != currentSubmission.Runs.Length)
        {
            throw new InvalidOperationException(
                $"R6Q cached indexed topology has {cached.DrawCommands.Length} draw commands but the current frame has {currentSubmission.Runs.Length} runs.");
        }

        bool unchanged = true;
        for(int i=0;i<cached.DrawCommands.Length;i++)
            if(!ReferenceEquals(cached.DrawCommands[i].SourceRun,currentSubmission.Runs[i])) { unchanged=false;break; }
        if(unchanged)return cached;
        var rebound = ImmutableArray.CreateBuilder<EnbOpenGlIndexedDrawCommand>(cached.DrawCommands.Length);
        for (int i = 0; i < cached.DrawCommands.Length; i++)
        {
            EnbOpenGlIndexedDrawCommand command = cached.DrawCommands[i];
            EnbW3dIndexedSubmissionRun cachedRun = command.SourceRun;
            EnbW3dIndexedSubmissionRun currentRun = currentSubmission.Runs[i];
            if (!HasSameIndexedRange(cachedRun, currentRun))
            {
                throw new InvalidOperationException(
                    $"R6Q cached indexed topology changed at run {i}; the GPU resource must not be reused for a different triangle/index range.");
            }

            rebound.Add(new EnbOpenGlIndexedDrawCommand(currentRun, command.IndexByteOffset));
        }

        return cached with { DrawCommands = rebound.MoveToImmutable() };
    }

    private static bool HasSameIndexedRange(
        EnbW3dIndexedSubmissionRun left,
        EnbW3dIndexedSubmissionRun right)
        => left.FirstTriangleOrdinal == right.FirstTriangleOrdinal &&
           left.TriangleCount == right.TriangleCount &&
           left.StartIndex == right.StartIndex &&
           left.IndexCount == right.IndexCount &&
           left.MinVertexIndex == right.MinVertexIndex &&
           left.MaxVertexIndex == right.MaxVertexIndex &&
           left.VertexCount == right.VertexCount;


    private static LiveLaneRejection CreateLaneRejection(
        IReadOnlyList<IEnbRenderObject> objects,
        EnbW3dDrawTask packet,
        LiveLaneKind laneKind,
        string diagnostic)
    {
        IEnbRenderObject? obj = objects.FirstOrDefault(candidate => candidate.GameId == packet.GameId);
        return new LiveLaneRejection(
            obj?.BaseAsset ?? -1,
            obj?.ObjectType ?? byte.MaxValue,
            packet.OrderKey.ResourceKey.ResourceName,
            packet.RenderObjectName,
            packet.Mesh.Name,
            packet.OrderKey.MaterialPassOrdinal,
            laneKind,
            diagnostic);
    }

    private static string DescribeR6Z3PrototypeAndLaneCensus(
        IEnumerable<LiveHlodPrototypeEvidence> prototypeEvidence,
        IReadOnlyList<LiveLaneRejection> laneRejections)
    {
        LiveHlodPrototypeEvidence[] evidence = prototypeEvidence
            .OrderBy(static item => item.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.MemberName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string hlodDetail = evidence.Length == 0
            ? "none"
            : string.Join(
                "; ",
                evidence.Select(static item =>
                    $"{item.ResourceName}::{item.MemberName}: {CompactDiagnostic(item.Summary)}" +
                    (string.IsNullOrWhiteSpace(item.CapturePath) ? string.Empty : $" capture {item.CapturePath}")));

        var laneGroups = laneRejections
            .GroupBy(static item => new
            {
                item.BaseAsset,
                item.ObjectType,
                item.ResourceName,
                item.RenderObjectName,
                item.MeshName,
                item.MaterialPassOrdinal,
                item.LaneKind,
                item.Diagnostic,
            })
            .OrderBy(static group => group.Key.BaseAsset)
            .ThenBy(static group => group.Key.ResourceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Key.RenderObjectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static group => group.Key.MaterialPassOrdinal)
            .ToArray();
        string laneDetail = laneGroups.Length == 0
            ? "none"
            : string.Join(
                "; ",
                laneGroups.Take(24).Select(static group =>
                    $"BASE {group.Key.BaseAsset} type 0x{group.Key.ObjectType:X2} {group.Key.ResourceName} " +
                    $"obj {group.Key.RenderObjectName} mesh {group.Key.MeshName} pass {group.Key.MaterialPassOrdinal} " +
                    $"lane {group.Key.LaneKind} ×{group.Count()}: {CompactDiagnostic(group.Key.Diagnostic)}")) +
              (laneGroups.Length > 24 ? $"; +{laneGroups.Length - 24} groups" : string.Empty);

        return
            "render research: R6Z3_LIVE_HLOD_PROTOTYPE_AND_LANE_CENSUS · " +
            "current-retail particle-emitter loader chunk 0x0500 (static) · " +
            $"HLOD evidence [{hlodDetail}] · lane reject {laneRejections.Count} [{laneDetail}]";
    }

    private static string DescribeR6Z1RootAndHlodCensus(
        int fileDefinitionObjects,
        int fileResolvedResources,
        int fileRootedObjects,
        int rslDefinitionObjects,
        int rslResolvedReferences,
        int rslRootedObjects,
        int rslRoots,
        int rootCount,
        IReadOnlyList<LiveRootRejection> rootRejections,
        IReadOnlyList<LiveHlodRejection> hlodRejections)
    {
        var pendingRoots = rootRejections.Where(static item => item.AuditPending).ToArray();
        rootRejections = rootRejections.Where(static item => !item.AuditPending).ToArray();
        string rootDetail = rootRejections.Count == 0
            ? "none"
            : string.Join(
                "; ",
                rootRejections
                    .GroupBy(static item => new { item.BaseAsset, item.Branch, item.Resource, item.Diagnostic })
                    .OrderBy(static group => group.Key.BaseAsset)
                    .ThenBy(static group => group.Key.Branch, StringComparer.Ordinal)
                    .Select(static group =>
                    {
                        string types = string.Join(
                            ",",
                            group.Select(static item => item.ObjectType).Distinct().Order().Select(static type => $"0x{type:X2}"));
                        return $"BASE {group.Key.BaseAsset} {group.Key.Branch} {group.Key.Resource} ×{group.Count()} types {types}: {CompactDiagnostic(group.Key.Diagnostic)}";
                    }));

        string hlodDetail = hlodRejections.Count == 0
            ? "none"
            : string.Join(
                "; ",
                hlodRejections
                    .GroupBy(static item => new { item.BaseAsset, item.IsRsl, item.HlodName, item.Diagnostic })
                    .OrderBy(static group => group.Key.BaseAsset)
                    .ThenBy(static group => group.Key.HlodName, StringComparer.OrdinalIgnoreCase)
                    .Select(static group =>
                    {
                        string types = string.Join(
                            ",",
                            group.Select(static item => item.ObjectType).Distinct().Order().Select(static type => $"0x{type:X2}"));
                        string branch = group.Key.IsRsl ? "RSL" : "FILE";
                        return $"BASE {group.Key.BaseAsset} {branch} HLOD {group.Key.HlodName} ×{group.Count()} types {types}: {CompactDiagnostic(group.Key.Diagnostic)}";
                    }));

        return
            $"render roots: R6Z1_LIVE_RENDER_ROOT_REJECTION_CENSUS · R6Z2_LIVE_HLOD_NULL_RENDER_OBJECT_ACTIVE · R6Z5_LIVE_PARTICLE_EMITTER_RENDER_OBJECT_ACTIVE · R6Z6_LIVE_PER_RUN_RASTER_STATE_ACTIVE · R6Z7_LIVE_STAGE0_DIFFUSE_PASSTHROUGH_ACTIVE · R6Z8_LIVE_DXT5_AND_DISABLED_UNBOUND_STAGE_ACTIVE · R6Z9_LIVE_D3D8_NULL_TEXTURE_CASCADE_ACTIVE · R6Z10_LIVE_RSL_OBJECT_FIRST_MATERIALIZATION_ACTIVE · R6Z11_LIVE_CURRENT_RETAIL_AGGREGATE_ACTIVE · R6Z12_LIVE_CURRENT_RETAIL_0603_IGNORE_ACTIVE · " +
            $"FILE def/resolve/root {fileDefinitionObjects}/{fileResolvedResources}/{fileRootedObjects} · " +
            $"RSL def/resolve/rooted {rslDefinitionObjects}/{rslResolvedReferences}/{rslRootedObjects} roots {rslRoots} · " +
            $"compiled roots {rootCount} · preorder fail {rootRejections.Count} · HLOD fail {hlodRejections.Count} · HAnim audit pending {pendingRoots.Length} [{string.Join(", ", pendingRoots.Select(static item => $"BASE {item.BaseAsset}"))}] · " +
            $"root [{rootDetail}] · hlod [{hlodDetail}]";
    }

    private string DescribeR6Z31AnimationReceipts(
        IReadOnlyList<RslAnimationDiagnostic> receipts,
        EnbMixResourceCatalog? retailPackages,
        string audioStatus)
    {
        string packageCensus = retailPackages is null
            ? "MIX packages ?/?"
            : $"MIX packages {retailPackages.ArchiveCount}/{retailPackages.DiscoveredArchiveCount}";
        if (receipts.Count == 0)
            return $"render animation: R6Z31_LIVE_CURRENT_RETAIL_RSL_ANIMATION_LIFETIME_BLEND_ACTIVE · {packageCensus} · audio {audioStatus} · {_bloodStatus} · none active";

        string detail = string.Join(
            "; ",
            receipts
                .OrderBy(static receipt => receipt.BaseAsset)
                .ThenBy(static receipt => receipt.GameId)
                .ThenBy(static receipt => receipt.StateId)
                .Select(static receipt =>
                {
                    string activation = receipt.ActivationClientTime?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    string rate = receipt.FrameRate?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    string count = receipt.NumFrames?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?";
                    string frame = receipt.Frame?.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
                    string arm = receipt.PacketActivateNextOnceClientTime?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                    string disposition;
                    if (receipt.AuditPending)
                    {
                        disposition = "Pending/HAnimAudit";
                    }
                    else if (receipt.TransitionFromStateId is int transitionFrom && receipt.TransitionClientTime is uint transitionTime)
                    {
                        string transitionKind = receipt.TransitionKind == LiveRslAnimationTransitionKind.AuthoredActivateNext
                            ? "AutoNext"
                            : "NextOnce";
                        string transition = $"{transitionKind} {transitionFrom}->{receipt.StateId}@{transitionTime}";
                        string armSuffix = receipt.PacketActivateNextOnceClientTime.HasValue ? $" arm@{arm}" : string.Empty;
                        disposition = receipt.Sampled
                            ? $"Sampled/{transition}{armSuffix}"
                            : $"Blocked/{transition}{armSuffix}";
                    }
                    else
                    {
                        disposition = receipt.StaticNull
                            ? receipt.PacketActivateNextOnceClientTime.HasValue ? $"Static/NULL/Armed@{arm}" : "Static/NULL"
                            : receipt.Sampled
                                ? receipt.PacketActivateNextOnceClientTime.HasValue ? $"Sampled/Armed@{arm}" : "Sampled"
                                : receipt.PacketActivateNextOnceClientTime.HasValue ? $"Blocked/Armed@{arm}" : "Blocked";
                    }
                    string diagnostic = string.IsNullOrWhiteSpace(receipt.Diagnostic)
                        ? string.Empty
                        : $" ({CompactDiagnostic(receipt.Diagnostic!)})";
                    return
                        $"BASE {receipt.BaseAsset} obj 0x{receipt.GameId:X8} RS {receipt.StateId} HAnim {receipt.HAnimName} src {receipt.Source} frame {frame}/{count} rate {rate} act {activation} now {receipt.CurrentClientTime} flags 0x{receipt.AuthoredFlags:X8} {disposition}{diagnostic}";
                }));

        return $"render animation: R6Z31_LIVE_CURRENT_RETAIL_RSL_ANIMATION_LIFETIME_BLEND_ACTIVE · {packageCensus} · audio {audioStatus} · {_bloodStatus} · {detail}";
    }

    private static string CompactDiagnostic(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ').Trim();


    private static double ProfileMilliseconds(long startTimestamp, long endTimestamp)
        => Math.Max(0.0, (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency);

    private void DestroyAllGpuResources(EnbGlApi? gl,bool preserveScene=false)
    {
        DestroyDynamicShadows(gl);
        DrainRetiredOrdinaryGpuResources(gl);
        if(preserveScene)
        {
            foreach(var (key,gpu) in _gpuResources)
                if(gpu.Textured is not null)_gpuRecoveryState[key]=RetainGpuState(gpu);
        }
        else { _gpuRecoveryState.Clear(); _specialMaterialBackend.ResetScene(gl); }
        foreach (LiveGpuResource resource in _gpuResources.Values)
            resource.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        _gpuResources.Clear();_compatGpuResources.Clear();ClearGpuResidentOwners();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)node.State.GpuResources.Clear();
        _lanePlans.Clear();
    }

    private readonly List<(LiveGpuKey Key,LiveGpuResource Resource,bool Local,int Order)> _idleGpuResources=[];
    private readonly EnbRetainedMembershipSet<(ulong Identity,ulong Generation)> _gpuLiveVisuals=new(),_gpuLiveRenderObjects=new(),_gpuLocalVisuals=new();
    private void RetireUnusedGpuResources(EnbGlApi gl, long ordinal, IEnbSceneFrame snapshot,
        IReadOnlySet<(ulong RenderIdentity, ulong VisualGeneration)> retainedPresentationOwners)
    {
        DrainRetiredOrdinaryGpuResources(gl);
        // Ordinary resources are registered with their source generation and
        // released by explicit invalidation. The bounded idle cache below is
        // for generated/auxiliary resources without that lifetime owner.
        // Visibility is not asset lifetime. Keep a bounded LRU of offscreen/LOD
        // resources so banking back does not compile shaders and upload textures.
        // These are client cache budgets, not retail protocol/render semantics.
        const long maximumIdleBytes = 128L * 1024 * 1024;
        const int maximumIdleResources = 256;
        var liveVisualIdentities=_gpuLiveVisuals;liveVisualIdentities.BeginFrame(_ownedSceneFrame);
        if(!_ownedSceneFrame)foreach(var obj in SceneObjects(snapshot))liveVisualIdentities.Add((obj.RenderIdentity,obj.VisualGeneration));
        // RSL effects and orbitals own retained RenderObjs too. Their synthetic
        // identities never appear in the snapshot; culling is not destruction.
        liveVisualIdentities.UnionWith(_ownedSceneFrame?_frameRetainedGpuOwners.FrameExtensions:retainedPresentationOwners);
        if (!snapshot.IsTransitioning && snapshot.Environment is { PresentationMode: 1 } environment &&
            _terrainKey == (environment.SectorNum, _assetGeneration))
        {
            int meshCount = _terrainMeshes.Count + _terrainDecorationMeshes.Count + (_terrainWater is null ? 0 : 1);
            for (int i = 0; i < meshCount; i++)
                liveVisualIdentities.Add((ulong.MaxValue - 10000 - (ulong)i, (ulong)_assetGeneration));
            liveVisualIdentities.Add((PlanetSkyIdentity, (ulong)_assetGeneration));
            if(_terrainDisplacement is not null)
                liveVisualIdentities.Add((PlanetDisplacementIdentity,(ulong)_assetGeneration));
        }
        var liveRenderObjects=_gpuLiveRenderObjects;liveRenderObjects.BeginFrame(_ownedSceneFrame);
        if(!_ownedSceneFrame)foreach(var obj in SceneObjects(snapshot))liveRenderObjects.Add((obj.RenderIdentity,obj.RenderObjectGeneration));
        liveRenderObjects.UnionWith(_ownedSceneFrame?_frameRetainedGpuOwners.FrameExtensions:retainedPresentationOwners);
        foreach(var key in _specialCompositeInstances.Keys.Where(key=>(key.Factory is {} factory
            ? !(_shipExhaustOwner??this)._livePhysicalParticleFactories.Contains(factory)
            : !(key.IsRsl?liveRenderObjects:liveVisualIdentities).Contains((key.Identity,key.Generation))) ||
            ((key.Path.StartsWith("effect-object/",StringComparison.Ordinal)||key.Path.Contains("/effect/",StringComparison.Ordinal)) && !_specialCompositeUsed.Contains(_specialCompositeInstances[key]))).ToArray())
        {_specialCompositeInstances[key].Dispose();_specialCompositeInstances.Remove(key);}
        _specialCompositeUsed.Clear();
        foreach(var key in _gpuRecoveryState.Keys.ToArray())
            if(!liveVisualIdentities.Contains((key.RenderIdentity,key.VisualGeneration)) ||
                key.RenderInstancePath.StartsWith("effect-object/",StringComparison.Ordinal) ||
                key.RenderInstancePath.Contains("/effect/",StringComparison.Ordinal))
                _gpuRecoveryState.Remove(key);
        // Pending reuploads share the existing offscreen resource-count budget.
        // Once their visual dies they are removed above, even if never redrawn.
        foreach(var key in _gpuRecoveryState.Keys.Skip(maximumIdleResources).ToArray())
            _gpuRecoveryState.Remove(key);
        var localVisualIdentities=_gpuLocalVisuals;localVisualIdentities.BeginFrame(_ownedSceneFrame);
        if(!_ownedSceneFrame)foreach(var obj in SceneObjects(snapshot))if(obj.IsLocalShip)localVisualIdentities.Add((obj.RenderIdentity,obj.VisualGeneration));
        long idleBytes = 0;
        int idleCount = 0;
        _idleGpuResources.Clear();
        foreach(var pair in _ownedSceneFrame?_compatGpuResources:_gpuResources)
            if(pair.Value.LastVisibleFrame!=ordinal)_idleGpuResources.Add((pair.Key,pair.Value,
                localVisualIdentities.Contains((pair.Key.RenderIdentity,pair.Key.VisualGeneration)),_idleGpuResources.Count));
        // Preserve the original stable local-ship/LRU ordering, including ties.
        _idleGpuResources.Sort(static (a,b)=>
        {
            int order=b.Local.CompareTo(a.Local);
            if(order==0)order=b.Resource.LastVisibleFrame.CompareTo(a.Resource.LastVisibleFrame);
            return order!=0?order:a.Order.CompareTo(b.Order);
        });
        foreach(var candidate in _idleGpuResources)
        {
            var key=candidate.Key;var resource=candidate.Resource;
            // An ended effect is not an invisible LOD of its still-live
            // source ship. Retire its instance materials/mappers immediately.
            if (!key.RenderInstancePath.StartsWith("effect-object/", StringComparison.Ordinal) &&
                !key.RenderInstancePath.Contains("/effect/", StringComparison.Ordinal) &&
                liveVisualIdentities.Contains((key.RenderIdentity, key.VisualGeneration)) &&
                idleCount < maximumIdleResources && resource.EstimatedUploadBytes <= maximumIdleBytes - idleBytes)
            {
                idleCount++;
                idleBytes += resource.EstimatedUploadBytes;
                continue;
            }
            RemoveGpuResourceBinding(key);
            _lanePlans.Remove(key);
            if (liveVisualIdentities.Contains((key.RenderIdentity, key.VisualGeneration)))
            {
                _frameGpuEvictions++;
                _frameGpuEvictedBytes += resource.EstimatedUploadBytes;
            }
            resource.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
        }

        _idleGpuResources.Clear();

        foreach (LiveHlodKey key in _hlodLevels.Keys.Where(key => key.PhysicalFactory is {} factory
            ? !(_shipExhaustOwner??this)._livePhysicalParticleFactories.Contains(factory)
            : !(key.IsRsl?liveRenderObjects:liveVisualIdentities).Contains((key.RenderIdentity,key.VisualGeneration))).ToArray())
            _hlodLevels.Remove(key);
        foreach (LivePostTransformHlodKey key in _postTransformHlodBounds.Keys
                     .Where(key => !liveVisualIdentities.Contains((key.RenderIdentity, key.VisualGeneration)))
                     .ToArray())
        {
            _postTransformHlodBounds.Remove(key);
        }
        // Ordinary decals are owned directly by their source generation.
        // Only the diagnostic/secondary snapshot path uses this cache.
        if(!_ownedSceneFrame)
        {
            var liveNameDecalKeys = SceneObjects(snapshot)
                .Where(static obj => obj.NameDecal is not null)
                .Select(obj => new LiveNameDecalTextureKey(_assetGeneration, obj.RenderIdentity, obj.VisualGeneration, obj.NameDecalApplyCount))
                .ToHashSet();
            foreach (LiveNameDecalTextureKey key in _nameDecalTextures.Keys
                         .Where(key => !liveNameDecalKeys.Contains(key)).ToArray())
            {
                _nameDecalTextures.Remove(key);
            }
        }
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Matrix4x4 value)
        => float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14) &&
           float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24) &&
           float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34) &&
           float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

    private sealed record ExternalHierarchyResolution(
        EnbW3dRenderHierarchy? Hierarchy,
        string? ResourceName,
        string? Diagnostic);

    private readonly record struct LiveRootRejection(
        int BaseAsset,
        byte ObjectType,
        string Branch,
        string Resource,
        string Diagnostic,
        bool AuditPending = false);

    private readonly record struct LiveHlodRejection(
        int BaseAsset,
        byte ObjectType,
        bool IsRsl,
        string ResourceName,
        string HlodName,
        string? MemberName,
        string Diagnostic);

    private sealed record LiveHAnimFamilyCensus(
        IReadOnlyList<EnbW3dHAnimHeaderInfo>? Headers,
        string? Diagnostic)
    {
        public readonly Dictionary<string, (string Text, EnbW3dHAnimHeaderInfo? Header)> Descriptions = new(StringComparer.Ordinal);
    }

    private sealed record LiveHlodPrototypeEvidence(
        string ResourceName,
        string MemberName,
        string Summary,
        string? CapturePath);

    private readonly record struct LiveLaneRejection(
        int BaseAsset,
        byte ObjectType,
        string ResourceName,
        string RenderObjectName,
        string MeshName,
        int MaterialPassOrdinal,
        LiveLaneKind LaneKind,
        string Diagnostic);

    private readonly record struct LiveHlodSelectionStats(
        int Total,
        int Selected,
        int Culled,
        int Failed,
        float Budget,
        float FixedCost,
        float FinalCost,
        int CandidateCount,
        string? FirstFailure);

    private sealed class LiveObjectPresentationAudit
    {
        private readonly HashSet<string> _roots = new(StringComparer.Ordinal);
        private readonly Dictionary<EnbW3dRenderResourceKey, HashSet<string>> _rootsByResource = [];
        private readonly Dictionary<EnbW3dRenderResourceKey, LiveProjectedPixelBounds> _boundsByResource = [];
        private readonly Dictionary<string, LiveProjectedPixelBounds> _boundsByMesh = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshes = new(StringComparer.OrdinalIgnoreCase);
        private EnbW3dWorldBounds? _pickSphere;
        private EnbW3dWorldBounds? _scannerSphere;
        private EnbW3dWorldBounds? _scannerFallbackSphere;
        private float _minimumPixelX = float.PositiveInfinity;
        private float _minimumPixelY = float.PositiveInfinity;
        private float _maximumPixelX = float.NegativeInfinity;
        private float _maximumPixelY = float.NegativeInfinity;
        private int _pixelWidth;
        private int _pixelHeight;

        private bool _collectDetails;
        public LiveObjectPresentationAudit(IEnbRenderObject obj, bool details=true)
        { Object = obj ?? throw new ArgumentNullException(nameof(obj)); _collectDetails=details; }

        public IEnbRenderObject Object { get; private set; }

        public void Reset(IEnbRenderObject obj,bool details)
        {
            Object=obj;_collectDetails=details;
            _roots.Clear();_rootsByResource.Clear();_boundsByResource.Clear();_boundsByMesh.Clear();_meshes.Clear();
            _pickSphere=_scannerSphere=_scannerFallbackSphere=null;
            _minimumPixelX=_minimumPixelY=float.PositiveInfinity;
            _maximumPixelX=_maximumPixelY=float.NegativeInfinity;
            _pixelWidth=_pixelHeight=SubmittedPacketCount=ProjectedVertexCount=0;
            _bodyQueries=null;
            foreach(var candidate in _bodyQueryStorage)if(!candidate.BodyQueries.IsRetained){_bodyQueries=candidate;break;}
            if(_bodyQueries is null){_bodyQueries=new();_bodyQueryStorage.Add(_bodyQueries);}
            _bodyQueries.BodyQueries.Clear();
            // Keep only the last invocation's immutable projection inputs.
            // Removed or deformed geometry expires after the next reset.
            _previousProjectedGeometry.Clear();
            (_projectedGeometry,_previousProjectedGeometry)=(_previousProjectedGeometry,_projectedGeometry);
        }

        public int SubmittedPacketCount { get; private set; }

        public int ProjectedVertexCount { get; private set; }

        public EnbW3dWorldBounds? RenderSphere => _scannerSphere is {Radius:>0} ? _scannerSphere : _scannerFallbackSphere ?? _scannerSphere;

        public bool HasProjectedBounds =>
            ProjectedVertexCount > 0 &&
            float.IsFinite(_minimumPixelX) &&
            float.IsFinite(_minimumPixelY) &&
            float.IsFinite(_maximumPixelX) &&
            float.IsFinite(_maximumPixelY);

        public bool IntersectsViewport =>
            HasProjectedBounds &&
            _maximumPixelX >= 0f && _minimumPixelX <= _pixelWidth &&
            _maximumPixelY >= 0f && _minimumPixelY <= _pixelHeight;

        public float ProjectedCenterDistanceSquared
        {
            get
            {
                if (!HasProjectedBounds || _pixelWidth <= 0 || _pixelHeight <= 0)
                    return float.PositiveInfinity;
                float centerX = ((_minimumPixelX + _maximumPixelX) * 0.5f) / _pixelWidth;
                float centerY = ((_minimumPixelY + _maximumPixelY) * 0.5f) / _pixelHeight;
                float dx = centerX - 0.5f;
                float dy = centerY - 0.5f;
                return (dx * dx) + (dy * dy);
            }
        }

        public void IncludeRoot(
            string instancePath,
            EnbW3dRenderResourceKey resourceKey,
            string logicalFileName,
            string renderObjectName,
            EnbW3dHlodSelection? selection,
            EnbW3dWorldBounds? worldSphere,
            float unappliedModelScale, EnbW3dWorldBounds? scannerSphere)
        {
            if(_collectDetails)
            {
            string hlod = selection is EnbW3dHlodSelection selected
                ? $"{selected.Name}:L{selected.LevelOrdinal}"
                : "direct";
            string root = $"{instancePath}={logicalFileName}::{renderObjectName}/{hlod}";
            _roots.Add(root);
            if (!_rootsByResource.TryGetValue(resourceKey, out HashSet<string>? resourceRoots))
            {
                resourceRoots = new HashSet<string>(StringComparer.Ordinal);
                _rootsByResource.Add(resourceKey, resourceRoots);
            }
            resourceRoots.Add(root);
            }
            if (worldSphere is EnbW3dWorldBounds sphere && sphere.IsValid) IncludeSphere(ref _pickSphere, sphere);
            if (scannerSphere is EnbW3dWorldBounds scanner &&
                EnbCurrentRetailScannerCameraRuntime.TryResolveFitSphere(scanner, unappliedModelScale, out var fitSphere))
                IncludeSphere(ref _scannerSphere, fitSphere);
        }

        private readonly List<LiveRenderedObjectQuery> _bodyQueryStorage=[];
        private LiveRenderedObjectQuery? _bodyQueries;
        public int BodyQueryCount=>_bodyQueries?.BodyQueries.Count??0;
        private Dictionary<(object Positions,Matrix4x4 Clip,int Width,int Height), (float MinX,float MinY,float MaxX,float MaxY,int Count)> _projectedGeometry=[];
        private Dictionary<(object Positions,Matrix4x4 Clip,int Width,int Height), (float MinX,float MinY,float MaxX,float MaxY,int Count)> _previousProjectedGeometry=[];

        public void IncludeSubmittedPacket(
            EnbW3dDrawTask packet,
            LiveLanePlan lane,
            EnbResolvedRenderCamera camera,
            int pixelWidth,
            int pixelHeight)
        {
            Matrix4x4 clipFromModel;
            IReadOnlyList<Vector3> positions;
            EnbW3dIndexedSubmissionPlan submission;
            if (lane.Unlit is EnbW3dProgrammableShaderLanePlan unlit)
            {
                clipFromModel = unlit.CanonicalClipFromModel;
                positions = unlit.Submission.Positions;
                submission = unlit.Submission;
            }
            else if (lane.Lit is EnbW3dProgrammableLightingLanePlan lit)
            {
                clipFromModel = lit.CanonicalClipFromModel;
                positions = lit.Submission.Positions;
                submission = lit.Submission;
            }
            else if (lane.Textured is EnbW3dProgrammableTextureLanePlan textured)
            {
                clipFromModel = lane.FrameUniforms?.CanonicalClipFromModel ?? textured.LightingPlan.CanonicalClipFromModel;
                positions = textured.Submission.Positions;
                submission = textured.Submission;
            }
            else
            {
                return;
            }

            submission=lane.Submission;
            positions=submission.Positions;
            Matrix4x4 presentationWorld=default;
            var retainedWorld=packet.CurrentRetailViewPositions is null
                ? lane.FrameUniforms?.ModelToWorld ?? lane.Textured?.LightingPlan.ModelToWorld ?? lane.Lit?.ModelToWorld : null;
            bool hasPresentationWorld;
            if(retainedWorld is {} world){presentationWorld=world;hasPresentationWorld=true;}
            else hasPresentationWorld=EnbCurrentRetailCameraGeometryRuntime.TryResolvePresentationWorldTransform(
                submission,camera,out presentationWorld,out _,out _);
            if (hasPresentationWorld &&
                TryComputePresentedMeshSphere(packet.Mesh, presentationWorld, out EnbW3dWorldBounds packetSphere))
            {
                IncludeSphere(ref _pickSphere, packetSphere);
                IncludeSphere(ref _scannerFallbackSphere, packetSphere);
            }

            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            // Retain immutable geometry references and the exact presented transform.
            // Triangle intersection is deferred until this object's hover prefilter passes.
            // 007B7880 ->009671B0 uses RenderObj +E4 for body selection.
            // Class10 buffer vtable00B11E64 delegates to009422E0, whose
            // +D0 call is00462CD0 (false). Generated buffer triangles cannot
            // select a body; do not publish/copy their temporary vertices.
            if (!packet.CurrentRetailParticleBuffer)
            {
                if(_bodyQueries is null){_bodyQueries=new();_bodyQueryStorage.Add(_bodyQueries);}
                _bodyQueries.BodyQueries.Add(submission,clipFromModel,pixelWidth,pixelHeight,CaptureBodyQueries);
            }
            SubmittedPacketCount++;
            if(!_collectDetails)return; // Hover projection belongs to the UI query, not every draw.
            LiveProjectedPixelBounds? resourceBounds=null,meshBounds=null;
            if(_collectDetails)
            {
            _meshes.Add(packet.Mesh.Name);
            if (!_boundsByResource.TryGetValue(packet.OrderKey.ResourceKey, out resourceBounds))
            {
                resourceBounds = new LiveProjectedPixelBounds();
                _boundsByResource.Add(packet.OrderKey.ResourceKey, resourceBounds);
            }
            if (!_boundsByMesh.TryGetValue(packet.Mesh.Name, out meshBounds))
            {
                meshBounds = new LiveProjectedPixelBounds();
                _boundsByMesh.Add(packet.Mesh.Name, meshBounds);
            }

            }

            var projectionKey=((object)positions,clipFromModel,pixelWidth,pixelHeight);
            if(!_projectedGeometry.TryGetValue(projectionKey,out var projected))
            {
                if(!_previousProjectedGeometry.TryGetValue(projectionKey,out projected))
                {
                var exact=EnbSubmittedMeshProjection.Project(positions,clipFromModel,pixelWidth,pixelHeight);
                projected=(exact.MinX,exact.MinY,exact.MaxX,exact.MaxY,exact.Count);
                }
                _projectedGeometry.Add(projectionKey,projected);
            }
            // Material passes share immutable positions and the same clip matrix.
            // Keep every pick submission and diagnostic count; only reuse the
            // exact vertex projection within this object's current frame audit.
            if(projected.Count!=0)
            {
                _minimumPixelX=MathF.Min(_minimumPixelX,projected.MinX);_minimumPixelY=MathF.Min(_minimumPixelY,projected.MinY);
                _maximumPixelX=MathF.Max(_maximumPixelX,projected.MaxX);_maximumPixelY=MathF.Max(_maximumPixelY,projected.MaxY);
                resourceBounds?.Include(projected.MinX,projected.MinY);resourceBounds?.Include(projected.MaxX,projected.MaxY);
                meshBounds?.Include(projected.MinX,projected.MinY);meshBounds?.Include(projected.MaxX,projected.MaxY);
                ProjectedVertexCount+=projected.Count;
            }

        }

        private static bool TryComputePresentedMeshSphere(
            EnbW3dRenderMesh mesh,
            Matrix4x4 world,
            out EnbW3dWorldBounds sphere)
        {
            sphere = default;
            if (!float.IsFinite(mesh.BoundingRadius) || mesh.BoundingRadius < 0f || !IsFinite(mesh.BoundingSphereCenter))
                return false;

            float xScale = new Vector3(world.M11, world.M12, world.M13).Length();
            float yScale = new Vector3(world.M21, world.M22, world.M23).Length();
            float zScale = new Vector3(world.M31, world.M32, world.M33).Length();
            float maxScale = MathF.Max(xScale, MathF.Max(yScale, zScale));
            sphere = new EnbW3dWorldBounds(
                Vector3.Transform(mesh.BoundingSphereCenter, world),
                mesh.BoundingRadius * maxScale);
            return sphere.IsValid;
        }

        private static void IncludeSphere(ref EnbW3dWorldBounds? accumulator, EnbW3dWorldBounds incoming)
        {
            if (accumulator is not EnbW3dWorldBounds current) { accumulator = incoming; return; }

            Vector3 delta = incoming.Center - current.Center;
            float separation = delta.Length();
            if (!float.IsFinite(separation))
                return;
            if (separation + incoming.Radius <= current.Radius)
                return;
            if (separation + current.Radius <= incoming.Radius)
            {
                accumulator = incoming;
                return;
            }

            float enclosingRadius = (separation + current.Radius + incoming.Radius) * 0.5f;
            Vector3 enclosingCenter = separation <= 1e-6f
                ? current.Center
                : current.Center + delta * ((enclosingRadius - current.Radius) / separation);
            var enclosing = new EnbW3dWorldBounds(enclosingCenter, enclosingRadius);
            if (enclosing.IsValid)
                accumulator = enclosing;
        }

        public bool TryCreateProjectedBounds(
            EnbRenderViewport viewport,
            out LiveRenderedObjectQuery bounds)
        {
            bounds = null!;
            // Diagnostic projection includes all rendered effects. Body-selection
            // bounds must always come from the separately admitted pick geometry.
            if (_bodyQueries is not {BodyQueries.Count:>0}) return false;
            bounds=_bodyQueries;
            bounds.RenderIdentity=Object.RenderIdentity;
            bounds.MinimumX=bounds.MinimumY=bounds.MaximumX=bounds.MaximumY=0;
            bounds.PickSphereCenter=_pickSphere?.Center;bounds.PickSphereRadius=_pickSphere?.Radius??0;
            bounds.ProjectionViewport=viewport;
            return true;
        }

        public string Describe(EnbResolvedRenderCamera camera)
        {
            string name = string.IsNullOrWhiteSpace(Object.Name) ? "<unnamed>" : Object.Name;
            float width = _maximumPixelX - _minimumPixelX;
            float height = _maximumPixelY - _minimumPixelY;
            float aspect = height > 0f ? width / height : float.NaN;
            string roots = string.Join(",", _roots.OrderBy(static value => value, StringComparer.Ordinal));
            string resourceBounds = string.Join(
                ";",
                _rootsByResource
                    .OrderBy(
                        static pair => pair.Value.OrderBy(static value => value, StringComparer.Ordinal).First(),
                        StringComparer.Ordinal)
                    .Select(pair =>
                    {
                        string labels = string.Join(",", pair.Value.OrderBy(static value => value, StringComparer.Ordinal));
                        return _boundsByResource.TryGetValue(pair.Key, out LiveProjectedPixelBounds? bounds) && bounds.HasBounds
                            ? $"{labels}=>{bounds.Width:0.#}x{bounds.Height:0.#}/ar{bounds.AspectRatio:0.###}"
                            : $"{labels}=>no-submitted-bounds";
                    }));
            string meshes = string.Join(",", _meshes.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).Take(8));
            string meshTail = _meshes.Count > 8 ? $",+{_meshes.Count - 8}" : string.Empty;
            string meshBounds = string.Join(
                ",",
                _boundsByMesh
                    .Where(static pair => pair.Value.HasBounds)
                    .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .Select(static pair =>
                        $"{pair.Key}={pair.Value.Width:0.#}x{pair.Value.Height:0.#}/ar{pair.Value.AspectRatio:0.###}"));
            string transform = "transform unavailable";
            string pivotProjection = "pivot unavailable";
            if (Object.Spatial is IEnbRenderSpatial spatial)
            {
                IEnbRenderTransform value = spatial.Transform;
                Quaternion q = value.Orientation;
                transform =
                    $"{spatial.ProviderKind} p({value.Position.X:0.###},{value.Position.Y:0.###},{value.Position.Z:0.###}) " +
                    $"q({q.X:0.######},{q.Y:0.######},{q.Z:0.######},{q.W:0.######}) " +
                    $"r({value.Rotation.X:0.######},{value.Rotation.Y:0.######},{value.Rotation.Z:0.######})";
                Vector3 canonicalView = camera.WorldToCanonicalView(value.Position);
                pivotProjection = camera.TryProjectWorldPoint(
                        value.Position,
                        viewportMargin: 0,
                        out EnbRenderProjectionResult pivot)
                    ? $"pivot v({canonicalView.X:0.###},{canonicalView.Y:0.###},{canonicalView.Z:0.###}) px({pivot.PixelX:0.#},{pivot.PixelY:0.#})"
                    : $"pivot v({canonicalView.X:0.###},{canonicalView.Y:0.###},{canonicalView.Z:0.###}) offscreen";
            }
            return
                $"{name}#0x{Object.GameId:X8}/B{Object.BaseAsset}/x{Object.Scale:0.###} {transform} {pivotProjection} roots {_roots.Count} [{roots}] " +
                $"packets/meshes/vertices {SubmittedPacketCount}/{_meshes.Count}/{ProjectedVertexCount} [{meshes}{meshTail}] " +
                $"bbox px x[{_minimumPixelX:0.#},{_maximumPixelX:0.#}] y[{_minimumPixelY:0.#},{_maximumPixelY:0.#}] " +
                $"size {width:0.#}x{height:0.#} ar {aspect:0.###} resource-bounds [{resourceBounds}] mesh-bounds [{meshBounds}]";
        }

    }

    private sealed class LiveProjectedPixelBounds
    {
        private float _minimumX = float.PositiveInfinity;
        private float _minimumY = float.PositiveInfinity;
        private float _maximumX = float.NegativeInfinity;
        private float _maximumY = float.NegativeInfinity;

        public bool HasBounds =>
            float.IsFinite(_minimumX) &&
            float.IsFinite(_minimumY) &&
            float.IsFinite(_maximumX) &&
            float.IsFinite(_maximumY);

        public float Width => _maximumX - _minimumX;

        public float Height => _maximumY - _minimumY;

        public float AspectRatio => Height > 0f ? Width / Height : float.NaN;

        public void Include(float x, float y)
        {
            _minimumX = MathF.Min(_minimumX, x);
            _minimumY = MathF.Min(_minimumY, y);
            _maximumX = MathF.Max(_maximumX, x);
            _maximumY = MathF.Max(_maximumY, y);
        }
    }

    private sealed record LiveFrame(
        IEnbSceneFrame Snapshot,
        EnbResolvedRenderCamera Camera,
        bool IsFirstPerson,
        uint CurrentClientTime,
        long ClientTimeAnchorTimestamp,
        EnbBackdropFrame? Backdrop,
        long PresentationToken,
        ImmutableDictionary<LivePlanetGpuIdentity, float> DayNightPhaseOverrides,
        ImmutableDictionary<LivePlanetGpuIdentity, uint> MapperStartMillisecondsOverrides)
    {
        public IReadOnlyDictionary<ulong, EnbRslObjectState>? RslObjectUpdateOverrides { get; init; }
        public IReadOnlyDictionary<ulong, EnbCurrentRetailRslEffectPassLibrary>? RslPassOwnerOverrides { get; init; }
        public EnbBridgeRenderFrame? Bridge { get; init; }
        public EnbRetailMovieFrame? Movie { get; init; }

        public LiveFrame ForRenderTimestamp(long renderTimestamp)
            => Movie is not null ? this : this with
            {
                CurrentClientTime = EnbRenderFrameClock.AdvanceClientTime(
                    CurrentClientTime,
                    ClientTimeAnchorTimestamp,
                    renderTimestamp,
                    Stopwatch.Frequency),
            };
    }

    private sealed record LiveFrameCadenceStatus(string Text)
    {
        public static LiveFrameCadenceStatus Waiting { get; } = new(
            "frame loop: R6 closeout waiting for continuous OpenGL presentation");
    }

    private sealed record LiveWorldW3dStatus(string Text)
    {
        public static LiveWorldW3dStatus Waiting { get; } = new("R6Q live W3D: waiting for projectable world frame");
    }

    private readonly record struct LiveCpuRootKey(
        int BaseAsset,
        int RslStateId,
        string LogicalFileName,
        string RequestedObjectName,
        long PrototypeRegistration = 0)
    {
        public long AggregateBaseRegistration {get;init;}
    }

    private sealed record LiveCpuRoot(
        bool OverbrightOff,
        EnbW3dRenderResource Resource,
        EnbW3dRenderHlod? Hlod,
        HashSet<string>? AllowedRenderObjectNames,
        string RootRenderObjectName,
        string RuntimeRenderObjectName,
        string? RuntimeHAnimHierarchyIdentity,
        string? AttachmentHierarchyName,
        IReadOnlyList<LiveAggregateAttachment> AggregateAttachments)
    {
        public readonly EnbW3dAnimationResourceOwner AnimationResources=new();
        public DockDefinitionState? DockDefinition;
        public LiveRslBoneNode? RslBoneNode {get;init;}
        public EnbCurrentRetailW3dCollectionDefinition? Collection { get; init; }
        public IReadOnlyList<LiveCpuRoot> CollectionChildren { get; init; }=[];
        public IReadOnlyList<LiveHmodelChild> HmodelChildren { get; init; }=[];
        public bool AuthoredGlobalOwner { get; init; }
        public bool IsDirectMeshPrototype { get; init; }
        public bool RenderObjectHidden { get; init; }
        public bool IndependentRslFactory { get; init; }
        public float? NamedChildFactoryScale { get; init; }
        public LiveCpuRoot? NamedChildBoundsRoot { get; init; }
        public bool UnconfiguredFactoryObject { get; init; }
        //48B220 keys by the requested prototype token, not an aggregate's
        //underlying mesh/HLOD name or a class-specific GetName result.
        public string? FactoryLookupName { get; init; }
        public object PrimitiveFactoryIdentity { get; init; } = new();
        public int RslFactoryStateId { get; init; } = -1;
        public uint? DazzleFactoryCreationClock { get; init; }
        public (float Scale,Vector3 Hsv)? ConfiguredFactoryInputs { get; init; }
        public object? PrimitiveConfigurationOwner { get; init; }
        public object? HierarchyConfigurationOwner { get; init; }
        public IReadOnlyList<EnbW3dRenderHierarchy>? ConfiguredHierarchyTranslations { get; init; }
        public IReadOnlyList<string>? AuthoredHlodMemberNames { get; init; }
        public Vector3? InfluenceFactoryHsv { get; init; }
        public EnbRetailParticleRandomOwner? FactoryParticleRandom { get; init; }
        public object? PhysicalFactoryOwner { get; init; }
        public float LodBias { get; init; } = 1;
        public bool ParticleConstructionPrepared { get; init; }
        public bool ForceSubObjectLod { get; init; }
        public EnbRetailSpecialMaterialInstance? SpecialMaterial { get; init; }
        public EnbRetailSphereAttachment? Sphere { get; init; }
        public EnbRetailRingAttachment? Ring { get; init; }
        public EnbCurrentRetailShipExhaustAsset? RetainedEmitter { get; init; }
        public IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> DiscardedParticleConstructions {get;init;}=[];
        public IReadOnlyList<string> UnavailableAttachments { get; init; }=[];
        public EnbCurrentRetailCollectionBounds? InfluenceLocalBounds { get; init; }
    }

    private sealed record LiveHmodelChild(int Ordinal,uint Pivot,LiveCpuRoot Root,int? LevelOrdinal=null,bool IsAggregate=false)
    {
        public int StorageOrdinal { get; init; } = Ordinal;
    }

    private sealed record LiveAggregateAttachment(
        string SubobjectName,
        string BoneName,
        LiveCpuRoot? Root,
        bool IsRetailNoOp,
        EnbCurrentRetailShipExhaustAsset? Exhaust = null,
        EnbRetailSphereAttachment? Sphere = null)
    {
        public EnbCurrentRetailRslBoneRenderObjectRuntime? RslChild {get;init;}
        public int? RslStateId {get;init;}
        public float FactoryParticleScale {get;init;}=1;
    }

    private enum LiveRslAnimationTransitionKind : byte
    {
        PacketNextOnce = 0,
        AuthoredActivateNext = 1,
    }

    // Protocol GameIDs can be reused after sector entry. These histories belong
    // to a specific RenderObj lifetime, including same-object visual replacement.
    private readonly record struct LiveRslAutomaticNextTransitionKey(
        (ulong RenderIdentity, ulong VisualGeneration) Owner,
        int SourceStateId,
        uint SourceActivationClientTime);

    private sealed record LiveRslAutomaticNextTransition(
        int TargetStateId,
        uint TransitionClientTime,
        float InitialFrame,
        double LeftoverSeconds);

    private readonly record struct LiveRslPacketNextOnceTransitionKey(
        (ulong RenderIdentity, ulong VisualGeneration) Owner,
        int SourceStateId,
        uint ActivationClientTime,
        uint PacketActivateNextOnceClientTime);

    private readonly record struct LiveRslLinkedStateActivationKey(
        (ulong RenderIdentity, ulong VisualGeneration) Owner,
        int StateId,
        uint ActivationClientTime);

    private sealed record LiveRslPacketNextOnceTransition(
        int TargetStateId,
        uint TransitionClientTime,
        float InitialFrame,
        double LeftoverSeconds);

    private sealed record LiveRslAnimationBinding(
        int StateId,
        string HAnimName,
        string Source,
        uint ActivationClientTime,
        EnbW3dRenderHierarchyAnimation Animation,
        EnbW3dAnimationSample Sample,
        EnbW3dRenderResource DrawResource,
        float Weight = 1f)
    {
        public LiveCpuRoot? LookupRoot {get;init;}
        public EnbResolvedRslFileDependency? LookupFile {get;init;}
        public EnbCurrentRetailRslHAnimResolution? Resolution {get;init;}
        public EnbW3dAnimationSample Sample {get;set;}=Sample;
        public float Weight {get;set;}=Weight;
    }

    // The root's retained clip bindings own frame and weight. These indexed
    // views read those fields directly; no sample arrays cross a frame boundary.
    private sealed class LiveRslAnimationBlendBinding
    {
        public IReadOnlyList<LiveRslAnimationBinding> Bindings {get;}
        public EnbW3dRenderResource DrawResource {get;}
        public IReadOnlyList<EnbW3dWeightedAnimationSample> Samples {get;}
        public IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> ResolvedSamples {get;}
        private readonly Dictionary<string,IReadOnlyList<EnbW3dResolvedWeightedAnimationSample>> hierarchies=new(StringComparer.OrdinalIgnoreCase);
        public LiveRslAnimationBlendBinding(LiveCpuRoot root,IReadOnlyList<LiveRslAnimationBinding> bindings)
        {
            Bindings=bindings;
            Samples=new SampleView<EnbW3dWeightedAnimationSample>(bindings,static b=>new(b.Animation.Name,b.Sample.Frame,b.Weight,b.Animation.HierarchyName));
            ResolvedSamples=new SampleView<EnbW3dResolvedWeightedAnimationSample>(bindings,static b=>new(b.Animation,b.Sample.Frame,b.Weight));
            DrawResource=root.AnimationResources.Resolve(root.Resource,ResolvedSamples);
        }
        public IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> ForHierarchy(string name)
        {
            if(hierarchies.TryGetValue(name,out var selected))return selected;
            var indices=new List<int>();
            for(int i=0;i<Bindings.Count;i++)if(string.Equals(Bindings[i].Animation.HierarchyName,name,StringComparison.OrdinalIgnoreCase))indices.Add(i);
            selected=indices.Count==Bindings.Count?ResolvedSamples:indices.Count==0?Array.Empty<EnbW3dResolvedWeightedAnimationSample>():
                new SampleView<EnbW3dResolvedWeightedAnimationSample>(Bindings,static b=>new(b.Animation,b.Sample.Frame,b.Weight),indices.ToArray());
            hierarchies.Add(name,selected);return selected;
        }
        private sealed class SampleView<T>(IReadOnlyList<LiveRslAnimationBinding> bindings,Func<LiveRslAnimationBinding,T> read,int[]? indices=null):IReadOnlyList<T>
        {
            public int Count=>indices?.Length??bindings.Count;
            public T this[int index]=>read(bindings[indices is null?index:indices[index]]);
            public IEnumerator<T> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
        }
    }

    private sealed record LiveRslVisualOverride(
        int ObjectStateId,
        LiveCpuRoot Root,
        LiveRslAnimationBlendBinding? AnimationBlend);

    private sealed class RslAnimationEvaluationOwner
    {
        public int BaseAsset;
        public uint GameId;
        public int StateId;
        public string HAnimName=string.Empty;
        public string Source=string.Empty;
        public uint? ActivationClientTime;
        public uint CurrentClientTime;
        public uint AuthoredFlags;
        public float? FrameRate;
        public uint? NumFrames;
        public float? Frame;
        public uint? PacketActivateNextOnceClientTime;
        public LiveRslAnimationTransitionKind? TransitionKind;
        public int? TransitionFromStateId;
        public uint? TransitionClientTime;
        public bool Sampled;
        public bool StaticNull;
        public string? Diagnostic;
        private LiveCpuRoot? nullRoot;
        private EnbResolvedRslStateResource? nullState;
        private ulong nullGeneration;
        private int nullRegistrationCount;
        public string? NullName {get;private set;}
        public string? NullDiagnostic {get;private set;}
        public bool HasNullBinding(LiveCpuRoot root,EnbResolvedRslStateResource state,ulong generation,int count)
            => NullName is not null && ReferenceEquals(nullRoot,root) && ReferenceEquals(nullState,state)
                && nullGeneration==generation && nullRegistrationCount==count;
        public void BindNull(LiveCpuRoot root,EnbResolvedRslStateResource state,ulong generation,int count,string name,string diagnostic)
        {nullRoot=root;nullState=state;nullGeneration=generation;nullRegistrationCount=count;NullName=name;NullDiagnostic=diagnostic;}
        public bool AuditPending;
        public IReadOnlyList<LiveRslSoundRequest> LinkedSounds=[];
        public LiveRslVisualOverride? VisualOverride;
        public RslAnimationDiagnostic CaptureDiagnostic() => new(BaseAsset,GameId,StateId,HAnimName,Source,ActivationClientTime,CurrentClientTime,AuthoredFlags,FrameRate,NumFrames,Frame,PacketActivateNextOnceClientTime,TransitionKind,TransitionFromStateId,TransitionClientTime,Sampled,StaticNull,Diagnostic) {AuditPending=AuditPending};
    }

    // Detached diagnostic history; never animation control state.
    private readonly record struct RslAnimationDiagnostic(

        int BaseAsset,
        uint GameId,
        int StateId,
        string HAnimName,
        string Source,
        uint? ActivationClientTime,
        uint CurrentClientTime,
        uint AuthoredFlags,
        float? FrameRate,
        uint? NumFrames,
        float? Frame,
        uint? PacketActivateNextOnceClientTime,
        LiveRslAnimationTransitionKind? TransitionKind,
        int? TransitionFromStateId,
        uint? TransitionClientTime,
        bool Sampled,
        bool StaticNull,
        string? Diagnostic)
    {public bool AuditPending {get;init;}}

    private sealed class LivePlanetGpuFrameTracker(IEnbRenderObject obj)
    {
        public IEnbRenderObject Object { get; } = obj;
        public bool RuntimeSemanticsResolved { get; set; } = true;
        public float? DayNightPhase { get; set; }
        public uint? MapperStartMilliseconds { get; set; }
        public int? DominantOrbitalIndex { get; set; }
        public string? HlodSummary { get; set; }
        public string? Diagnostic { get; set; }
        public HashSet<int> CameraGeometrySampleMeshOrdinals { get; } = [];
        public List<string> CameraGeometrySamples { get; } = [];
        public int ExpectedPackets { get; set; }
        public int CulledPackets { get; set; }
        public int SubmittedPackets { get; set; }
        public int RejectedPackets { get; set; }
        public int DayNightPackets { get; set; }
        public int NormalGeometryPackets { get; set; }
        public int CameraAlignedGeometryPackets { get; set; }
        public int CameraOrientedGeometryPackets { get; set; }
        public int SkinGeometryPackets { get; set; }
        public int UnsupportedGeometryPackets { get; set; }
    }

    private sealed class LivePlanetMapperStateOwnership
    {
        public LivePlanetMapperStateOwnership(uint mapperStartMilliseconds)
        {
            MapperStartMilliseconds = mapperStartMilliseconds;
        }

        public uint MapperStartMilliseconds { get; set; }
        public float? DayNightPhase { get; set; }
        public int? DominantOrbitalIndex { get; set; }
        public bool RuntimeSemanticsResolved { get; set; }
        public string? Diagnostic { get; set; }
    }

    private sealed class LiveShipLegoFrameTracker
    {
        public int ShipObjectsSeen { get; set; }
        public bool LocalShipSeen { get; set; }
        public uint LocalShipGameId { get; set; }
        public int LocalShipBaseAsset { get; set; }
        public byte LocalShipObjectType { get; set; }
        public float LocalShipObjectScale { get; set; }
        public EnbSceneShipPresentationSnapshot? LocalShipPresentation { get; set; }
        public EnbSceneShipLegoSnapshot? LocalShipLego { get; set; }
        public EnbSceneAuxDataMaterializationAuditSnapshot? LocalShipAuxDataAudit { get; set; }
        public EnbSceneColorizationApplicationAuditSnapshot? LocalShipColorizationAudit { get; set; }
        public int LocalShipDecalEntries { get; set; }
        public EnbSceneNameDecalSnapshot? LocalShipNameDecal { get; set; }
        public int LocalShipColorizationEntries { get; set; }
        public string? LocalShipColorizationSideSample { get; set; }
        public int LocalShipSubParts { get; set; }
        public int LocalShipActiveSubParts { get; set; }
        public string? LocalShipSubPartSample { get; set; }
        public string? LocalShipRootKind { get; set; }
        public int LocalShipAttachmentTopRoots { get; set; }
        public int LocalShipAttachmentAnimationSamples { get; set; }
        public string? LocalShipAttachmentRootName { get; set; }
        public int RslAttachmentCandidates { get; set; }
        public int RslAttachmentReady { get; set; }
        public int RslAttachmentAmbiguous { get; set; }

        public int DecalTargetPackets { get; set; }
        public int DecalAppliedPackets { get; set; }
        public int DecalTintedPackets { get; set; }
        public int DecalUnassignedPackets { get; set; }
        public int DecalUnresolvedPackets { get; set; }
        public string? DecalFirstDiagnostic { get; set; }

        public int NameDecalTargetPackets { get; set; }
        public int NameDecalAppliedPackets { get; set; }
        public int NameDecalUnresolvedPackets { get; set; }
        public int NameDecalTextureWidth { get; set; }
        public int NameDecalTextureHeight { get; set; }
        public int NameDecalTextWidth { get; set; }
        public string? NameDecalFirstDiagnostic { get; set; }

        public int OverbrightEligiblePackets { get; set; }
        public int OverbrightAppliedPackets { get; set; }
        public int OverbrightDisabledPackets { get; set; }
        public int OverbrightUnresolvedPackets { get; set; }
        public int OverbrightTargetTriangles { get; set; }
        public int OverbrightAppliedTriangles { get; set; }
        public string? OverbrightFirstDiagnostic { get; set; }

        public int ColorizationTargetPackets { get; set; }
        public int ColorizationAppliedPackets { get; set; }
        public int ColorizationExcludedPackets { get; set; }
        public int ColorizationUnresolvedPackets { get; set; }
        public int ColorizationPendingPackets { get; set; }
        public int ColorizationTargetTriangles { get; set; }
        public int ColorizationAppliedTriangles { get; set; }
        public int ColorizationExcludedTriangles { get; set; }
        public int ColorizationPrimaryTriangles { get; set; }
        public int ColorizationSecondaryTriangles { get; set; }
        public int ColorizationSkinTriangles { get; set; }
        public int ColorizationRootPackets { get; set; }
        public int ColorizationLegoPackets { get; set; }
        public int ColorizationFixedPackets { get; set; }
        public int ColorizationOtherPackets { get; set; }
        public HashSet<int> ColorizationRegions { get; } = [];
        public HashSet<string> ColorizationTargetInstancePaths { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ColorizationExcludedInstancePaths { get; } = new(StringComparer.Ordinal);
        public string? ColorizationFirstDiagnostic { get; set; }

        public int WeaponMetalTargetPackets { get; set; }
        public int WeaponMetalAppliedPackets { get; set; }
        public int WeaponMetalUnresolvedPackets { get; set; }
        public int WeaponMetalTargetTriangles { get; set; }
        public int WeaponMetalAppliedTriangles { get; set; }
        public HashSet<int> WeaponMetalRegions { get; } = [];
        public string? WeaponMetalFirstDiagnostic { get; set; }

        public int RenderTextureReduction { get; set; }
        public EnbRenderTextureFilterMode RenderTextureFilter { get; set; }

        public short? EnvironmentMapBaseAsset { get; set; }
        public string? EnvironmentMapTexture { get; set; }
        public EnbEnvironmentMapAxis? EnvironmentMapAxis { get; set; }
        public bool EnvironmentMapUsesDefaultTexture { get; set; }
        public string? EnvironmentMapDiagnostic { get; set; }
        public int EnvironmentAtlasExpectedTextures { get; set; }
        public int EnvironmentAtlasBuiltTextures { get; set; }
        public int EnvironmentAtlasTargetPackets { get; set; }
        public int EnvironmentAtlasTextureReduction { get; set; }
        public EnbRenderTextureFilterMode EnvironmentAtlasTextureFilter { get; set; }
        public string? EnvironmentAtlasDiagnostic { get; set; }

        public int? HlodOptionSelector { get; set; }
        public bool HlodUsesPersistedOptions { get; set; }
        public int HlodPolyBudget { get; set; }
        public float HlodFixedCost { get; set; }
        public float HlodFinalCost { get; set; }
        public int HlodCandidateCount { get; set; }
        public List<LiveShipHlodDecision> HlodDecisions { get; } = [];

        public int ObjectsSeen { get; set; }
        public int ComposedObjects { get; set; }
        public int AuxAttachments { get; set; }
        public int SubPartAttachments { get; set; }
        public int ChildRoots { get; set; }
        public int NoOpAttachments { get; set; }
        public int PendingAttachments { get; set; }
        public int MissingAttachFallbacks { get; set; }
        public int RejectedObjects { get; set; }
        public string? FirstDiagnostic { get; set; }

        public int AggregateWrappers { get; set; }
        public int AggregateChildRoots { get; set; }
        public int AggregateNoOps { get; set; }
        public int AggregateRejected { get; set; }
        public int ExhaustVisibleAttachments { get; set; }
        public int ExhaustHiddenAttachments { get; set; }
        public int ExhaustUnresolvedAttachments { get; set; }

        public int FixedEligibleObjects { get; set; }
        public int FixedFactoryGroupsReady { get; set; }
        public int FixedFactoryCreated { get; set; }
        public int FixedFactoryNoOps { get; set; }
        public int FixedRslChildren { get; set; }
        public int FixedLogicalAttachments { get; set; }
        public int FixedAttachedChildren { get; set; }
        public int FixedDrawRoots { get; set; }
        public int FixedMissingBones { get; set; }
        public int FixedAmbiguousBones { get; set; }
        public int FixedRejectedGroups { get; set; }
        public int FixedBoneResolutionRejects { get; set; }
        public int FixedChildRootRejects { get; set; }
        public int FixedRecursiveAggregateGaps { get; set; }
        public string? FixedFirstDiagnostic { get; set; }
    }

    private sealed class LiveShipHlodDecision
    {
        public LiveShipHlodDecision(
            string instancePath,
            string resourceName,
            string hlodName,
            int levelCount,
            int currentLevel)
        {
            InstancePath = instancePath;
            ResourceName = resourceName;
            HlodName = hlodName;
            LevelCount = levelCount;
            CurrentLevel = currentLevel;
        }

        public string InstancePath { get; }
        public string ResourceName { get; }
        public string HlodName { get; }
        public int LevelCount { get; }
        public string Outcome { get; set; } = "pending";
        public int CurrentLevel { get; set; }
        public int MinimumLevel { get; set; } = -1;
        public int SelectedLevel { get; set; } = -1;
        public float? ProjectedDetail { get; set; }
        public float? WorldDistance { get; set; }
        public float? WorldRadius { get; set; }
        public float AggregateCost { get; set; }
        public int HierarchyOverrideCount { get; set; }
        public int CompiledPacketCount { get; set; }
        public int AdmittedPacketCount { get; set; }
        public int CompiledHierarchyRecordPacketCount { get; set; }
        public int AdmittedHierarchyRecordPacketCount { get; set; }
        public ImmutableArray<int> LevelCosts { get; set; } = ImmutableArray<int>.Empty;

        public string Describe()
        {
            string resource = string.IsNullOrWhiteSpace(ResourceName) ? "?" : ResourceName;
            string levels = SelectedLevel >= 0
                ? $"{CurrentLevel}>{SelectedLevel}/min{MinimumLevel}"
                : $"{CurrentLevel}>-";
            string costs = LevelCosts.IsDefaultOrEmpty
                ? "-"
                : string.Join('/', LevelCosts);
            string projection = ProjectedDetail.HasValue
                ? $" p{ProjectedDetail.Value:0.###}"
                : string.Empty;
            string bounds = WorldDistance.HasValue && WorldRadius.HasValue
                ? $" d/r{WorldDistance.Value:0.###}/{WorldRadius.Value:0.###}"
                : string.Empty;
            return
                $"{InstancePath}:{resource}/{HlodName} {Outcome} L{levels}/{LevelCount} c{costs}+{AggregateCost:0.###} o{HierarchyOverrideCount} pkt{AdmittedPacketCount}/{CompiledPacketCount} rec{AdmittedHierarchyRecordPacketCount}/{CompiledHierarchyRecordPacketCount}{projection}{bounds}";
        }
    }

    private sealed record LiveShipLegoSpec(
        string Source,
        int Ordinal,
        string[] Path,
        int Type,
        int BaseAsset,
        string Data)
    {
        public string CanonicalPath { get; } = string.Join('/', Path);
        public string LogicalParentPath { get; } = Path.Length<=1?string.Empty:string.Join('/',Path,0,Path.Length-1);
        public ulong MaterializationRevision { get; init; }
        public string? PhysicalDrawPath { get; init; }
        public string? PhysicalParentPath { get; init; }
        public LegoPhysicalModel? PhysicalModel { get; init; }
        public LegoFactoryBinding? FactoryBinding;
    }

    // All mounts read the same parent's current hierarchy state. Physical
    // controller overrides are installed before this walk. The compatibility
    // attachment path explicitly clears AttachmentPose after each state write.
    private static bool TryResolveOwnedLegoMount(ShipAttachmentOwner parent,
        EnbClientBaseAssetMount mount,out Matrix4x4 world,out string? diagnostic)
    {
        world=default;diagnostic=null;
        var pose=parent.AttachmentPose;
        if(pose is null)
        {
            if(!TryResolveRetainedHierarchyPose(parent.Instance!,parent.Hierarchy,parent.RigidWorld,
                ResolvePendingHierarchyScale(parent.Instance!,parent.Scale),
                parent.Animations??Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),
                parent.DrawHierarchyOverrides,out pose,out diagnostic))return false;
            parent.AttachmentPose=pose;
        }
        int pivot=EnbCurrentRetailAggregateBoneLookup.Resolve(parent.Hierarchy,mount.BoneName);
        if(pivot<0 || pivot>=pose.WorldTransforms.Length)
        {
            diagnostic=$"BASE-{mount.BaseAssetId} mount '{mount.BoneName}' BoneIndex {pivot} is outside hierarchy '{parent.Hierarchy.Name}' pivot range 0..{pose.WorldTransforms.Length-1}.";
            return false;
        }
        world=pose.WorldTransforms[pivot];return true;
    }

    private sealed record LiveShipFixedVisualMaterialized(
        EnbCurrentRetailShipFixedVisualSpec Spec,
        LiveCpuRoot? Root,
        bool IsRsl,
        bool IsRetailNoOp);

    // The retained occurrence owns its hierarchy fields. Queries and draw
    // preparation read this state; no alternate immutable pose is published.
    private static bool TryResolveRetainedHierarchyPose(LiveRootInstance parent,EnbW3dRenderHierarchy hierarchy,
        Matrix4x4 world,float? scale,IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,IReadOnlyDictionary<int,Matrix4x4>? overrides,
        out EnbW3dHierarchyPoseOwner pose,out string? diagnostic)
    {
        if(parent.ChildPose is null)
        {
            var owners=parent.RetainedChildPoses;
            if(owners is null||!owners.TryGetValue(parent.InstancePath,out var owner))
            {owner=new();if(owners is not null)owners[parent.InstancePath]=owner;}
            parent.ChildPose=owner;
        }
        return parent.ChildPose.Update(hierarchy,world,scale,animations,overrides,out pose,out diagnostic);
    }

    private sealed class LiveRootInstance
    {
        public ShipAttachmentOwner? AttachmentOwner;
        public readonly List<OrdinaryCameraQuery> CameraQueries=[];
        public RootTaskList.Cell? QueuedTaskHead;
        public RootInvocations? InvocationStorage;
        public int InvocationSlot;
        public RetainedEmitterBinding? PreparedEmitter;
        public long DrawPreparationEpoch;
        public SceneVisibility? PreparedSceneVisibility;
        public RslWrapperOwner? RslAttachment;
        public CompositeOccurrence? PreparedCompositeOccurrence;
        public LegoPhysicalModel? PreparedSceneModel;
        public bool SceneModelBound;
        public AggregateLinePreparation? AggregateLines;
        public AggregateAttachmentBinding? AttachmentBinding;
        public readonly List<QueryChildTaskSlot> QueryChildTasks=[];
        public int QueryChildTaskDepth;
        public OwnedDazzleRoot? PreparedDazzleRoot;
        public RigidOwner? PreparedOwner;
        public EnbW3dMeshOccurrencePreparation? PreparedDefinitions;
        public EnbW3dRigidOccurrence? PreparedOccurrence;
        public Dictionary<int,EnbCameraCollisionGeometrySlot>? PreparedQueries;
        public int CachedModelOwnerState=-1;
        public string? CachedModelOwnerPrefix,CachedModelOwnerPath;
        public LiveRootInstance(
            IEnbRenderObject obj,
            LiveCpuRoot root,
            bool IsRsl,
            string instancePath = "root",
            Matrix4x4? worldTransformOverride = null,
            LiveRslAnimationBlendBinding? animationBlend = null)
        {
            Object = obj; Appearance=FactoryAppearance.From(obj);
            Root = root;
            this.IsRsl = IsRsl;
            InstancePath = instancePath;
            WorldTransformOverride = worldTransformOverride;
            AnimationBlend = animationBlend;
        }

        public IEnbRenderObject Object { get; private set; }
        public ProjectedDecalOwner? ProjectedDecals;
        public object? ProjectedDecalFactory;
        public long ProjectedDecalEpoch=-1;
        public FactoryAppearance Appearance;
        public float RenderScale=>Appearance.Scale;
        public LiveCpuRoot Root { get; }
        // Identity of the actual attachment owner, recorded during materialization.
        // Diagnostic paths and shared prototypes do not establish parentage.
        public List<ChildTaskChain>? QueuedChildChains;
        private LiveRootInstance? parent;
        public LiveRootInstance? Parent
        {
            get=>parent;
            set {if(ReferenceEquals(parent,value))return;parent=value;RootTaskList.Reparent(this);}
        }
        public int? ParentStorageOrdinal { get; set; }
        public EnbCurrentRetailRslBoneRenderObjectRuntime? RslChild {get;set;}
        public LiveCpuRoot? RetainedQueryRoot { get; set; }
        public RootInvocations?[] CompositeInvocations=[];
        public readonly List<EnbShipMaterialCache.SurfaceBinding> SurfaceBindings=[];
        public long SurfaceLayoutRevision;
        public PreparedSwatchLayout? SwatchLayout;
        public OwnedAppearanceLayout? AppearanceLayout;
        public object? CurrentAppearancePreparation;
        public OwnedHlodCosts? PreparedHlodCosts;
        public OwnedHlodBudget? PreparedHlodBudget;
        public OwnedHlodBounds? PreparedHlodBounds;
        public HlodLevelState? PreparedHlodLevel;
        public object? PreparedHlodFactory;
        public EnginePivotLayout? PreparedEnginePivots;
        public EnbW3dRenderResource? EngineVisibilitySource,EngineHiddenResource;
        //0072CC10 traverses attached controllers, not their internal render children.
        public bool IsShipLegoController { get; init; }
        public bool IsExternalLegoChild { get; set; }
        public bool IsExternalRslModel { get; set; }
        public int? LegoControllerBaseAsset { get; init; }
        public bool IsOrdinaryHlodChild { get; init; }
        public bool PivotVisible { get; set; } = true;
        public bool IsAggregateHlodChild { get; set; }
        public int? OrdinaryHlodLevelOrdinal { get; set; }
        public uint OrdinaryHlodPivot { get; set; }
        public bool EngineChildHidden { get; set; }
        public bool IsRsl { get; }
        public string InstancePath { get; }
        public Matrix4x4? WorldTransformOverride { get; set; }
        public EnbW3dHierarchyPoseOwner? SkinParentPose { get; set; }
        public EnbW3dRetainedHierarchyPose? ChildPose { get; set; }
        public Dictionary<string,EnbW3dRetainedHierarchyPose>? RetainedChildPoses { get; set; }
        public LiveRslAnimationBlendBinding? AnimationBlend { get; private set; }
        public EnbW3dHierarchyState? HierarchyPostTransforms { get; set; }
        private EnbW3dHierarchyState? ownedHierarchyState;
        public void SetHierarchyState(string name,Matrix4x4 world,float scale,IReadOnlyDictionary<int,Matrix4x4> transforms)
        {
            ownedHierarchyState??=new(name,world,scale,transforms);
            ownedHierarchyState.Update(name,world,scale,transforms);
            HierarchyPostTransforms=ownedHierarchyState;
        }

        public EnbW3dHierarchyState? ConfiguredPost { get; set; }
        public float? InheritedRenderObjectScale { get; set; }
        public Matrix4x4? PrimitiveFactoryWorld { get; set; }
        public float PrimitiveFactoryScale { get; set; } = 1;
        public float HierarchySourceScale { get; set; } = 1;
        public Vector3 PrimitiveFactoryHsv { get; set; }
        public LiveShipHlodDecision? ShipHlodDecision { get; set; }
        public Vector3? CurrentRetailWorldOrigin { get; set; }
        public EnbW3dWorldBounds? CurrentRetailWorldSphere { get; set; }
        public EnbW3dAxisAlignedBounds? CurrentRetailLocalBox { get; set; }
        public Matrix4x4? CurrentRetailBoundsWorld { get; set; }
        public bool ShouldDraw { get; set; } = true;
        public bool MovieHidden { get; set; }
        public EnbResolvedRenderCamera? OrbitalCamera { get; set; }
        public float? OrbitalPhase { get; set; }
        public bool OrbitalForceVisible { get; set; }
        public bool MovieForceVisible { get; set; }
        public bool ForcesSceneVisibility => OrbitalForceVisible || MovieForceVisible;
        public float OrbitalVisibility { get; set; } = 1;
        public EnbW3dHlodSelection? HlodSelection { get; set; }
        // Frame invocations rent this object only after its previous consumers
        // finish. Reset every mutable field formerly initialized by construction.
        public void UpdateCallbackAnimation(LiveRslAnimationBlendBinding? animation)=>AnimationBlend=animation;
        public void RefreshRetainedSceneFrame(IEnbRenderObject obj)
        {
            Object=obj;AnimationBlend=null;
            RetainedQueryRoot=PreparedCompositeOccurrence?.Membership?.QueryRoot;
            PivotVisible=true;EngineChildHidden=false;SkinParentPose=null;
            HierarchyPostTransforms=null;InheritedRenderObjectScale=null;
            PrimitiveFactoryWorld=null;PrimitiveFactoryScale=1;PrimitiveFactoryHsv=default;
            ShipHlodDecision=null;CurrentRetailWorldOrigin=null;CurrentRetailWorldSphere=null;
            CurrentRetailLocalBox=null;CurrentRetailBoundsWorld=null;
            ShouldDraw=true;MovieHidden=false;OrbitalCamera=null;OrbitalPhase=null;
            OrbitalForceVisible=false;MovieForceVisible=false;OrbitalVisibility=1;HlodSelection=null;
            if(Parent is null)WorldTransformOverride=null;
        }
        public void RefreshSimple(IEnbRenderObject obj, LiveRslAnimationBlendBinding? animation)
        {
            Object = obj; AnimationBlend = animation;
            Parent = default;
            ParentStorageOrdinal = default;
            RslChild = default;
            RetainedQueryRoot = default;
            IsExternalLegoChild = default;
            IsExternalRslModel = default;
            PivotVisible = true;
            IsAggregateHlodChild = default;
            OrdinaryHlodLevelOrdinal = default;
            OrdinaryHlodPivot = default;
            EngineChildHidden = default;
            WorldTransformOverride = default;
            SkinParentPose = default;
            ChildPose = default;
            HierarchyPostTransforms = default;
            InheritedRenderObjectScale = default;
            PrimitiveFactoryWorld = default;
            PrimitiveFactoryScale = 1;
            HierarchySourceScale = 1;
            PrimitiveFactoryHsv = default;
            ShipHlodDecision = default;
            CurrentRetailWorldOrigin = default;
            CurrentRetailWorldSphere = default;
            CurrentRetailLocalBox = default;
            CurrentRetailBoundsWorld = default;
            ShouldDraw = true;
            MovieHidden = default;
            OrbitalCamera = default;
            OrbitalPhase = default;
            OrbitalForceVisible = default;
            MovieForceVisible = default;
            OrbitalVisibility = 1;
            HlodSelection = default;
        }

    }

    private static LiveHlodKey RetainedHlodKey(LiveRootInstance instance,string name)
    {
        var factory=PhysicalParticleFactory(instance);
        return new(instance.Object.RenderIdentity,factory is not null?0:instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration,
            instance.Root.Resource.Key,name,RetainedModelOwnerPath(instance),instance.IsRsl) {PhysicalFactory=factory};
    }

    private readonly record struct LiveHlodKey(
        ulong RenderIdentity,
        ulong VisualGeneration,
        EnbW3dRenderResourceKey ResourceKey,
        string HlodName,
        string InstancePath,
        bool IsRsl)
    { public object? PhysicalFactory {get;init;} }

    private readonly record struct LiveStaticHlodKey(
        long AssetGeneration,
        EnbW3dRenderResourceKey ResourceKey,
        string HlodName,bool RecursiveAggregateOwners=false);

    private readonly record struct LivePostTransformHlodKey(
        long AssetGeneration,
        ulong RenderIdentity,
        ulong VisualGeneration,
        EnbW3dRenderResourceKey ResourceKey,
        string HlodName,
        string InstancePath);

    private sealed class LivePostTransformHlodBounds
    {
        private readonly string _hierarchyName;
        private readonly int _renderObjectScaleBits;
        private readonly int _hierarchyScaleBits;
        private readonly ImmutableArray<LiveHierarchyPostTransformEntry> _transforms;
        private readonly EnbW3dAxisAlignedBounds _localBox;
        private readonly EnbW3dWorldBounds _localSphere;

        private LivePostTransformHlodBounds(
            string hierarchyName,
            int renderObjectScaleBits,
            int hierarchyScaleBits,
            ImmutableArray<LiveHierarchyPostTransformEntry> transforms,
            EnbW3dAxisAlignedBounds localBox,
            EnbW3dWorldBounds localSphere)
        {
            _hierarchyName = hierarchyName;
            _renderObjectScaleBits = renderObjectScaleBits;
            _hierarchyScaleBits = hierarchyScaleBits;
            _transforms = transforms;
            _localBox = localBox;
            _localSphere = localSphere;
        }

        public static LivePostTransformHlodBounds Create(
            EnbW3dHierarchyState postTransforms,
            EnbW3dHlodBoundsOwner localBounds)
            => new(
                postTransforms.HierarchyName,
                BitConverter.SingleToInt32Bits(postTransforms.RenderObjectScale),
                BitConverter.SingleToInt32Bits(postTransforms.HierarchyTranslationScale),
                postTransforms.Transforms
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => new LiveHierarchyPostTransformEntry(pair.Key, pair.Value))
                    .ToImmutableArray(),
                localBounds.LocalBox,
                localBounds.LocalSphere);

        public bool Matches(EnbW3dHierarchyState postTransforms)
        {
            if (!string.Equals(_hierarchyName, postTransforms.HierarchyName, StringComparison.OrdinalIgnoreCase) ||
                _renderObjectScaleBits != BitConverter.SingleToInt32Bits(postTransforms.RenderObjectScale) ||
                _hierarchyScaleBits != BitConverter.SingleToInt32Bits(postTransforms.HierarchyTranslationScale) ||
                _transforms.Length != postTransforms.Transforms.Count)
            {
                return false;
            }

            foreach (LiveHierarchyPostTransformEntry entry in _transforms)
            {
                if (!postTransforms.Transforms.TryGetValue(entry.PivotIndex, out Matrix4x4 transform) ||
                    !entry.Transform.Equals(transform))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryResolveWorldSphere(
            string hlodName,
            Matrix4x4 rigidWorldTransform,
            out EnbW3dHlodBoundsOwner bounds,
            out string? diagnostic, EnbW3dHlodBoundsOwner? destination=null)
        {
            Vector4 nativeSphere=EnbCurrentRetailRenderBoundsStorage.Calculate(rigidWorldTransform,new(_localSphere.Center,_localSphere.Radius),_localBox.Center,_localBox.Extents).Sphere;
            Vector3 worldCenter = new(nativeSphere.X,nativeSphere.Y,nativeSphere.Z);
            if (!IsFinite(worldCenter) || !float.IsFinite(_localSphere.Radius))
            {
                bounds = default!;
                diagnostic = $"W3D HLOD '{hlodName}' produced non-finite current-retail composite bounds.";
                return false;
            }

            var worldSphere = new EnbW3dWorldBounds(worldCenter, _localSphere.Radius);
            bounds = (destination??new()).Set(_localBox, _localSphere, worldSphere);
            diagnostic = worldSphere.IsValid
                ? null
                : $"W3D HLOD '{hlodName}' produced invalid current-retail composite bounds.";
            return diagnostic is null;
        }
    }

    private readonly record struct LiveHierarchyPostTransformEntry(int PivotIndex, Matrix4x4 Transform);

    private readonly record struct LiveHlodCompiledCosts(
        ImmutableArray<int> LevelCosts,
        float AggregateCost);

    private enum LiveLaneKind : byte
    {
        Unsupported,
        Unlit,
        Lit,
        Textured,
    }

    private readonly record struct LiveGpuKey(
        long AssetGeneration,
        EnbW3dRenderResourceKey ResourceKey,
        int MeshOrdinal,
        int MaterialPassOrdinal,
        ulong RenderIdentity,
        ulong VisualGeneration,
        int BindingOrdinal,
        string RenderInstancePath,
        LiveLaneKind Lane,
        int TextureReduction,
        EnbRenderTextureFilterMode TextureFilter,
        string? EnvironmentReflectionTexture,
        uint? MapperStartMillisecondsOverride,
        object? BatchIdentity);

    private readonly record struct LiveNameDecalTextureKey(
        long AssetGeneration,
        ulong RenderIdentity,
        ulong VisualGeneration,
        int ApplyCount);

    private readonly record struct LiveParticleEmitterKey(
        long AssetGeneration,
        ulong RenderIdentity,
        ulong VisualGeneration,
        EnbW3dRenderResourceKey ResourceKey,
        string InstancePath,
        string EmitterResourceName,
        int PivotIndex,
        int BindingOrdinal);

    private readonly record struct LiveLanePlan(
        EnbW3dProgrammableShaderLanePlan? Unlit,
        EnbW3dProgrammableLightingLanePlan? Lit,
        EnbW3dProgrammableTextureLanePlan? Textured)
    {
        public EnbW3dIndexedSubmissionPlan? FrameSubmission { get; init; }
        public EnbW3dLightingFrameUniforms? FrameUniforms { get; init; }
        public Matrix4x4? FrameGeneratedView { get; init; }
        public Matrix4x4? FrameProjection { get; init; }
        public EnbW3dIndexedSubmissionPlan Submission => FrameSubmission ?? Textured?.Submission ?? Lit?.Submission ?? Unlit!.Submission;
        public LiveLanePlan MaterializeFrame() => FrameSubmission is {} current && Textured is {} texture
            ? From((texture).Copy().SetLightingPlan((FrameUniforms ?? EnbW3dLightingFrameUniforms.From(texture.LightingPlan))
                .Materialize(texture.LightingPlan,current)).SetRetailGeneratedCoordinateViewFromWorld(FrameGeneratedView ?? texture.RetailGeneratedCoordinateViewFromWorld).SetCanonicalCameraProjection(FrameProjection ?? texture.CanonicalCameraProjection)) : this;
        public static LiveLanePlan From(EnbW3dProgrammableShaderLanePlan lane) => new(lane, null, null);
        public static LiveLanePlan From(EnbW3dProgrammableLightingLanePlan lane) => new(null, lane, null);
        public static LiveLanePlan From(EnbW3dProgrammableTextureLanePlan lane) => new(null, null, lane);
    }

    private sealed class LiveCachedLanePlan(
        EnbResolvedRenderCamera Camera,
        Matrix4x4 WorldTransform,
        float CurrentRetailMeshModelScale,
        EnbW3dRenderMesh Mesh,
        EnbW3dRenderMaterialPass MaterialPass,
        string RenderObjectName,
        LiveLightingSignature Lighting,
        LiveLanePlan Plan)
    {
        public EnbResolvedRenderCamera Camera { get; private set; } = Camera;
        public Matrix4x4 WorldTransform { get; private set; } = WorldTransform;
        public float CurrentRetailMeshModelScale { get; private set; } = CurrentRetailMeshModelScale;
        public EnbW3dRenderMesh Mesh { get; private set; } = Mesh;
        public EnbW3dRenderMaterialPass MaterialPass { get; private set; } = MaterialPass;
        public string RenderObjectName { get; private set; } = RenderObjectName;
        public LiveLightingSignature Lighting { get; private set; } = Lighting;
        public LiveLanePlan Plan { get; private set; } = RetainLane(Plan);
        private bool previousHasViewPositions=Plan.Submission.Packet.CurrentRetailViewPositions is not null;
        private Matrix4x4? previousUnscaledTransform=Plan.Submission.Packet.CurrentRetailUnscaledWorldTransform;
        private int previousLightCount=Plan.Submission.Packet.LightEnvironment.Lights.Length;
        private static LiveLanePlan RetainLane(LiveLanePlan plan)
        {plan.Submission.Packet.RetainAfterDrain();plan.Textured?.Submission.Packet.RetainAfterDrain();return plan;}
        public static LiveCachedLanePlan Create(
            EnbW3dDrawTask packet,
            EnbResolvedRenderCamera camera,
            LiveLanePlan plan)
            => new(
                camera,
                packet.WorldTransform,
                packet.CurrentRetailMeshModelScale,
                packet.Mesh,
                packet.Material.Source,
                packet.RenderObjectName,
                LiveLightingSignature.Create(packet.LightEnvironment),
                plan);

        public bool Matches(EnbW3dDrawTask packet, EnbResolvedRenderCamera camera, bool retainedDefinition = false)
            => Camera.Equals(camera) &&
               WorldTransform.Equals(packet.WorldTransform) &&
               CurrentRetailMeshModelScale.Equals(packet.CurrentRetailMeshModelScale) &&
               (retainedDefinition || ReferenceEquals(Mesh, packet.Mesh) &&
               ReferenceEquals(MaterialPass, packet.Material.Source) &&
               string.Equals(RenderObjectName, packet.RenderObjectName, StringComparison.Ordinal)) &&
               Lighting.Equals(LiveLightingSignature.Create(packet.LightEnvironment));

        public bool TryUpdateFrame(EnbW3dIndexedSubmissionPlan submission,
            EnbResolvedRenderCamera camera, out LiveLanePlan lane, bool compactTexturedFrame = false, bool retainedDefinition = false)
        {
            lane = Plan;
            using var reuseLane = EnbWorkReuseDiagnostics.Measure("lane.update");
            bool reuseMatches = Matches(submission.Packet, camera, retainedDefinition);
            EnbWorkReuseDiagnostics.Add(reuseMatches ? "lane.inputsMatch" : "lane.inputsDiffer");
            var previous=Plan.Submission;
            if (reuseMatches && (retainedDefinition || EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(previous,submission.Packet)) &&
                !previousHasViewPositions && submission.Packet.CurrentRetailViewPositions is null &&
                previousUnscaledTransform==submission.Packet.CurrentRetailUnscaledWorldTransform &&
                previousLightCount==submission.Packet.LightEnvironment.Lights.Length &&
                (retainedDefinition || previous.Runs.AsSpan().SequenceEqual(submission.Runs.AsSpan())))
            {
                // Rebind the current packet without repeating unchanged camera,
                // inverse-normal and lighting preparation. Mapper evaluation is
                // still performed for every actual draw by the backend.
                if(compactTexturedFrame && Plan.Textured is not null)
                    lane=Plan with {FrameSubmission=submission,
                        FrameUniforms=(Plan.FrameUniforms ?? EnbW3dLightingFrameUniforms.From(Plan.Textured.LightingPlan))
                            with {LightEnvironment=submission.Packet.LightEnvironment}};
                else if(Plan.Textured is {} texture)
                    lane=LiveLanePlan.From((texture).Copy().SetLightingPlan((texture.LightingPlan).Copy().SetSubmission(submission).SetLightEnvironment(submission.Packet.LightEnvironment)));
                else if(Plan.Lit is {} light)
                    lane=LiveLanePlan.From((light).Copy().SetSubmission(submission).SetLightEnvironment(submission.Packet.LightEnvironment));
                else lane=LiveLanePlan.From(Plan.Unlit! with {Submission=submission});
                PublishFrame(submission.Packet,camera,lane);
                return true;
            }
            // The prepared owner proves geometry/material identity. Keep its lane;
            // only this invocation's value-owned uniforms change. No aliasing of queued views.
            if(compactTexturedFrame && retainedDefinition && Plan.Textured is {} retained &&
                EnbW3dProgrammableLightingLaneCompiler.TryResolveRetainedFrame(submission,camera,out var uniforms,out _))
            {
                lane=Plan with {FrameSubmission=submission,FrameUniforms=uniforms,
                    FrameGeneratedView=EnbW3dGeneratedTextureCoordinates.CreateRetailCameraViewFromWorld(camera),
                    FrameProjection=camera.Projection.Matrix};
                PublishFrame(submission.Packet,camera,lane);
                return true;
            }
            if (Plan.Textured is { } textured &&
                EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(textured, submission, camera, out var updatedTexture, out _, retainedDefinition: retainedDefinition))
            {
                lane = LiveLanePlan.From(updatedTexture);
                PublishFrame(submission.Packet,camera,lane);
                return true;
            }
            if (Plan.Lit is { } lit &&
                EnbW3dProgrammableLightingLaneCompiler.TryUpdateFrame(lit, submission, camera, out var updatedLight, out _, retainedDefinition: retainedDefinition))
            {
                lane = LiveLanePlan.From(updatedLight);
                PublishFrame(submission.Packet,camera,lane);
                return true;
            }
            return false;
        }
        private void PublishFrame(EnbW3dDrawTask packet,EnbResolvedRenderCamera camera,LiveLanePlan plan)
        {
            Camera=camera;WorldTransform=packet.WorldTransform;CurrentRetailMeshModelScale=packet.CurrentRetailMeshModelScale;
            Lighting=LiveLightingSignature.Create(packet.LightEnvironment);
            previousHasViewPositions=packet.CurrentRetailViewPositions is not null;
            previousUnscaledTransform=packet.CurrentRetailUnscaledWorldTransform;
            previousLightCount=packet.LightEnvironment.Lights.Length;
            // Keep definition and current value uniforms, never a drained visible
            // task. The returned lane still owns its invocation-local submission.
            Plan=RetainLane(plan with {FrameSubmission=null});
        }
    }

    private readonly record struct LiveLightingSignature(uint AmbientRgb24,ImmutableArray<EnbW3dLightSlot> Lights)
    {
        public static LiveLightingSignature Create(EnbW3dLightEnvironment environment)
            =>new(environment.GlobalAmbientRgb24,environment.Lights);
        private EnbW3dLightSlot Slot(int index)=>!Lights.IsDefault && index<Lights.Length?Lights[index]:default;
        public bool Equals(LiveLightingSignature other)
        {
            if(AmbientRgb24!=other.AmbientRgb24)return false;
            if(Lights.Equals(other.Lights))return true;
            for(int i=0;i<EnbW3dLightEnvironment.CurrentRetailLightSlotCount;i++)
                if(Slot(i)!=other.Slot(i))return false;
            return true;
        }
        public override int GetHashCode()=>HashCode.Combine(AmbientRgb24,Slot(0),Slot(1),Slot(2),Slot(3));
    }

    private sealed class LiveGpuResource(
        LiveLaneKind Kind,
        EnbOpenGlFirstShaderMeshResource? Unlit,
        EnbOpenGlLitShaderMeshResource? Lit,
        EnbOpenGlStage0TextureMeshResource? Textured)
    {
        public LiveLaneKind Kind { get; } = Kind;
        public EnbOpenGlFirstShaderMeshResource? Unlit { get; } = Unlit;
        public EnbOpenGlLitShaderMeshResource? Lit { get; } = Lit;
        public EnbOpenGlStage0TextureMeshResource? Textured { get; private set; } = Textured;
        public SceneOwner? LifetimeOwner;
        private EnbW3dSkinLaneOwner? _skinLane;
        public EnbW3dDeformedGeometry? UploadedSkinGeometry {get;set;}=Textured?.LanePlan.Submission.Packet.DeformedGeometry;
        public bool TryPrepareSkinLane(EnbW3dIndexedSubmissionPlan submission,EnbResolvedRenderCamera camera,
            bool owned,out EnbW3dProgrammableTextureLanePlan lane,out EnbW3dSkinLaneOwner? active)
        {
            active=null;
            if(!owned)return EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(Textured!.LanePlan,submission,camera,
                out lane,out _,geometryWillBeUploaded:true,reuseValidatedNormals:true);
            if(_skinLane is null || !ReferenceEquals(_skinLane.Definition,Textured!.LanePlan))_skinLane=new(Textured!.LanePlan);
            if(!_skinLane.TryBind(submission,camera,out _)){lane=null!;return false;}
            active=_skinLane;lane=active.Current;return true;
        }
        public long LastVisibleFrame { get; set; }
        public bool IsDestroyed { get; private set; }
        // Registration is a resource-lifetime component, not a frame sweep.
        // A collected prepared definition must also retire its registration;
        // a list of WeakReference objects would keep accumulating dead entries.
        private System.Runtime.CompilerServices.ConditionalWeakTable<PreparedGpuBindings,HashSet<int>>? _preparedBindings;
        public void TrackPreparedBinding(PreparedGpuBindings owner,int slot)
            =>(_preparedBindings??=new()).GetValue(owner,static _=>new()).Add(slot);
        private void InvalidatePreparedBindings()
        {
            if(_preparedBindings is null)return;
            foreach(var (owner,slots) in _preparedBindings)
                foreach(int slot in slots)
                    if(ReferenceEquals(owner.Slots[slot]?.Resource,this))owner.Slots[slot]=null;
            _preparedBindings.Clear();
        }
        public long EstimatedUploadBytes { get; private set; } = Estimate(Unlit, Lit, Textured);

        // Streaming mutates the existing allocation after earlier draws have
        // consumed it. Texture/mapper ownership is unchanged by these backend
        // operations; account only for the changed geometry, not every mip.
        public void UpdateGeometry(EnbOpenGlStage0TextureMeshResource updated)
        {
            EstimatedUploadBytes+=GeometryBytes(updated)-GeometryBytes(Textured!);
            Textured=updated;
        }
        private static long GeometryBytes(EnbOpenGlStage0TextureMeshResource resource)
        {
            var geometry=resource.GeometryUploadPlan;
            return geometry.VertexCount*(long)(EnbOpenGlLitShaderUploadPlan.StrideBytes+8)+
                geometry.IndexedPlan.IndexCount*(long)geometry.IndexedPlan.IndexElementSizeBytes;
        }

        private static long Estimate(EnbOpenGlFirstShaderMeshResource? unlit,
            EnbOpenGlLitShaderMeshResource? lit, EnbOpenGlStage0TextureMeshResource? textured)
        {
            if (unlit is not null)
                return unlit.UploadPlan.VertexElements.Length * 4L +
                    unlit.UploadPlan.IndexedPlan.IndexCount * (long)unlit.UploadPlan.IndexedPlan.IndexElementSizeBytes;
            var geometry = textured?.GeometryUploadPlan ?? lit!.UploadPlan;
            long bytes = geometry.VertexCount * (long)EnbOpenGlLitShaderUploadPlan.StrideBytes +
                geometry.IndexedPlan.IndexCount * (long)geometry.IndexedPlan.IndexElementSizeBytes;
            if (textured is not null)
            {
                bytes += geometry.VertexCount * 8L; // stage-1 UV buffer
                var counted = new HashSet<int>();
                for (int i = 0; i < textured.TextureIdsByRun.Length; i++)
                    if (textured.TextureIdsByRun[i] != 0 && counted.Add(textured.TextureIdsByRun[i]))
                        foreach (var mip in textured.TextureUploadPlans[i].Mips) bytes += mip.RgbaByteLength;
                int stage1Plan = 0;
                foreach (int id in textured.Texture1IdsByRun)
                {
                    if (id == 0) continue;
                    var texture = textured.Texture1UploadPlans[stage1Plan++];
                    if (counted.Add(id)) foreach (var mip in texture.Mips) bytes += mip.RgbaByteLength;
                }
            }
            return bytes;
        }

        public static LiveGpuResource From(EnbOpenGlFirstShaderMeshResource resource) => new(LiveLaneKind.Unlit, resource, null, null);
        public static LiveGpuResource From(EnbOpenGlLitShaderMeshResource resource) => new(LiveLaneKind.Lit, null, resource, null);
        public static LiveGpuResource From(EnbOpenGlStage0TextureMeshResource resource) => new(LiveLaneKind.Textured, null, null, resource);

        public void Destroy(
            EnbGlApi? gl,
            EnbOpenGlFirstW3dShaderBackend unlitBackend,
            EnbOpenGlFixedFunctionLightingBackend litBackend,
            EnbOpenGlStage0TextureBackend texturedBackend)
        {
            IsDestroyed=true;
            InvalidatePreparedBindings();
            if (Unlit is not null)
                unlitBackend.DestroyResource(gl, Unlit);
            if (Lit is not null)
                litBackend.DestroyResource(gl, Lit);
            if (Textured is not null)
                texturedBackend.DestroyResource(gl, Textured);
        }
    }
}
