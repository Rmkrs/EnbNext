using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using Size = EnbNext.Presentation.EnbUiSize;
using System.Collections.Immutable;
using System.Numerics;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.IO.Compression;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Live-world composition host. The authored environment and star
/// geometry is handed to the production OpenGL surface before ordinary W3D.
/// Projects retail-authored object icons into the cockpit gutter and keeps
/// targeting on the immutable presentation boundary. Every layer consumes only
/// immutable presentation state; mutable Net7 objects never cross this UI boundary.
/// </summary>
internal sealed partial class EnbWorldController
{
    private const int DiagnosticsRefreshMilliseconds = 250;

    private readonly Func<IEnbSceneFrame?> _snapshotAccessor;
    private EnbSceneFrame? _lastSampledPhysicalScene;
    private readonly EnbPresentationFrameImport _diagnosticSceneImport=new();
    private readonly Func<EnbMixResourceCatalog?> _resourceAccessor;
    private readonly Func<EnbRetailAssetBundle?> _retailAssetsAccessor;
    private readonly Func<uint?> _currentClientTimeAccessor;
    private readonly Func<uint?> _fixedSessionIdAccessor;
    private readonly Func<uint, CancellationToken, Task<bool>> _targetRequester;
    internal void ApplyGraphicsOptionChange(Action change) => _w3dWorld.ApplyGraphicsOptionChange(change);

    private readonly EnbWorldUi _ui;
    private readonly EnbWorldRenderer _w3dWorld;
    private readonly EnbWorldRenderer _targetModelView;
    private TargetPreviewKey? _targetPreviewKey;
    private readonly EnbRetailScannerCameraState _scannerCameraState=new();
    private long _targetPresentationToken;
    private long _nextTargetPresentationToken;
    private bool _resizeRefreshQueued;
    private bool _stopped;
    private readonly EnbWorldRenderer _hudGpu;
    private readonly EnbLoadingPanel _loadingScreen = new();
    private bool _awaitingWorld = true;
    private long _worldPresentationToken = 1;
    private long _loadingStarted = Stopwatch.GetTimestamp();
    private EnbRenderViewport? _presentationViewport;
    private EnbSceneLocationTransition? _locationTransition;
    private ulong? _localPresentationIdentity;
    private EnbNormalCameraController _cameraController = new();
    private CameraStartupKey? _cameraStartupKey;
    private long _lastCameraFrameTimestamp;
    private readonly EnbShipVisualBankRuntime _shipVisualBank = new();
    private readonly EnbShipWarpCacheRuntime _shipWarpCache = new();
    private readonly EnbSpaceSpeakerRuntime _spaceSpeakers = new();
    private readonly EnbCockpitWarpCallbacks _cockpitWarpCallbacks = new();
    private int _framePumpGeneration;
    private long _lastDiagnosticsRefreshTimestamp;
    private long _uiCadenceWindowStartTimestamp;
    private int _uiCadenceWindowFrames;
    private double _uiCadenceWindowCpuMilliseconds;
    private double _uiCadenceWindowWorstCpuMilliseconds;
    private string _uiCadenceStatus = "ui scene pump: waiting for presentation cadence";
    private string _diagnosticsText = "LIVE WORLD VIEW · R6Q\nwaiting for materialized world spatial state";
    private string? _capturedEnvironmentResource;
    private string _environmentCaptureStatus = "asset capture: waiting for resolved environment W3D";
    private bool _influenceSkyCaptureAttempted;
    private string _influenceSkyCaptureStatus =
        "influence sky capture: R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE · waiting for installed BaseAsset 311";
    private bool _influenceSkyPresentationPreflightAttempted;
    private EnbRetailAssetBundle? _influenceSkyPresentationPreflightAssets;
    private string _influenceSkyPresentationStatus =
        "influence sky renderer: R7B1_CURRENT_RETAIL_INFLUENCE_SKY_PRESENTATION_READY · waiting for installed BaseAsset 311";
    private string? _environmentRendererResource;
    private LiveWorldEnvironmentRenderer? _environmentRenderer;
    private bool _environmentRendererIsInfluence;
    private EnbRetailAssetBundle? _environmentRendererAssetBundle;
    private Vector3? _environmentRendererInfluenceColor;
    private string _environmentRenderStatus = "sky renderer: waiting for resolved environment W3D";
    private int? _capturedStarSetId;
    private string _starCaptureStatus = "star capture: waiting for current sector/star-set";
    private LiveWorldStarFieldRenderer? _starFieldRenderer;
    private EnbMixResourceCatalog? _starFieldResources;
    private EnbRetailAssetBundle? _starFieldAssets;
    private int? _starFieldSetId;
    private string _starRenderStatus = "star renderer: waiting for current sector/star-set";
    private readonly HashSet<string> _capturedPlanetResources = new(StringComparer.OrdinalIgnoreCase);
    private string _planetCaptureStatus = "planet capture: waiting for materialized planet object";
    private string _planetRenderStatus = "planet: waiting for materialized planet object";
    private string _renderResidualStatus = "render census: waiting for materialized world objects";

    private readonly IEnbWorldHost _host;
    internal EnbWorldUi Ui=>_ui;
    internal EnbWorldRenderer World=>_w3dWorld;
    internal EnbWorldRenderer Hud=>_hudGpu;
    internal EnbWorldRenderer ScannerRenderer=>_targetModelView;
    internal bool LoadingVisible=true,HudVisible=true,GutterVisible=true,EmptyVisible;
    internal int LoadingZIndex;
    internal string LoadingText="Loading…",EmptyText="Awaiting local spatial state…";
    internal bool IsStationLayout=>_stationActive;
    /// <summary>
    /// Next "Hide user interface": only space and its objects are drawn; no HUD, chat, panels, gutter icons,
    /// labels, reticles or damage numbers, and the hidden interface takes no pointer input. Space only; opening a
    /// window, editing chat or docking brings the interface back.
    /// </summary>
    internal bool InterfaceHidden {get;private set;}
    internal void ToggleInterface()=>SetInterfaceHidden(!InterfaceHidden&&!_stationActive);
    private void SetInterfaceHidden(bool hidden)
    {
        if(InterfaceHidden==hidden)return;
        InterfaceHidden=hidden;_w3dWorld.InterfaceHidden=hidden;_ui.Inventory.WorldInterfaceHidden=hidden;
        if(hidden){_ui.Gutter.ClearPointer();_interfaceBlockers=InterfaceBlockers();}
    }
    // Windows, chat editing and docking bring a hidden interface back when they open (an edge, so state that was
    // already set when the interface was hidden does not undo the hide on the next frame).
    private int _interfaceBlockers;
    private int InterfaceBlockers()=>(_stationActive?1:0)|(_ui.Options.IsOpen?2:0)|(_ui.Inventory.IsOpen?4:0)|(_ui.Inventory.CoversScreen?8:0)|(_ui.Map.IsOpen||_ui.GalaxyMap.IsOpen?16:0)|(_ui.Chat.Editing?32:0);
    private void TrackInterfaceBlockers()
    {
        if(!InterfaceHidden)return;
        int now=InterfaceBlockers(),opened=now&~_interfaceBlockers;_interfaceBlockers=now;
        if(opened!=0){InterfaceRestoredBy=opened;SetInterfaceHidden(false);}
    }
    /// <summary>Diagnostics: the blocker bits that last brought the hidden interface back.</summary>
    internal int InterfaceRestoredBy {get;private set;}
    internal bool AwaitingWorld=>_awaitingWorld;
    private EnbUiRect Bounds=>new(0,0,_host.Size.Width,_host.Size.Height);
    internal EnbWorldController(IEnbWorldHost host,
        Func<IEnbSceneFrame?> snapshotAccessor,Func<EnbMixResourceCatalog?> resourceAccessor,
        Func<EnbRetailBaseAssetResolver?> baseAssetResolverAccessor,Func<EnbRetailAssetBundle?> retailAssetsAccessor,
        Func<uint?> fixedSessionIdAccessor,Func<uint?> currentClientTimeAccessor,
        Func<uint,CancellationToken,Task<bool>> targetRequester,Func<uint,uint,CancellationToken,Task<bool>> verbExecutor,
        Func<EnbRenderSceneSubscription>? renderSceneSubscriptionFactory=null)
    {
        _host=host;_snapshotAccessor=snapshotAccessor;_resourceAccessor=resourceAccessor;
        _retailAssetsAccessor=retailAssetsAccessor;_fixedSessionIdAccessor=fixedSessionIdAccessor;
        _currentClientTimeAccessor=currentClientTimeAccessor;_targetRequester=targetRequester;
        _w3dWorld=new(baseAssetResolverAccessor,retailAssetsAccessor,fixedSessionIdAccessor,continuousPresentation:false);
        _targetModelView=new(baseAssetResolverAccessor,retailAssetsAccessor,fixedSessionIdAccessor,enableAudio:false,useMainViewEnvironment:false,continuousPresentation:false);
        _w3dWorld.SetRenderSceneSubscription(renderSceneSubscriptionFactory?.Invoke());_w3dWorld.AttachScanner(_targetModelView);
        _hudGpu=EnbWorldRenderer.CreateTooltipLayer(retailAssetsAccessor,fixedSessionIdAccessor);
        _hudGpu.ShareGraphicsOptions(_w3dWorld);_hudGpu.ShareSpeechScope(_w3dWorld);
        _ui=new(fixedSessionIdAccessor,RequestTargetLocallyAsync,verbExecutor,host.CreateOptionsBrowser,_w3dWorld,_hudGpu,TryRequestBridgePanel);
        _ui.Cockpit.CameraState=()=>_retailCamera.State;
        _ui.Chat.SubmitRequested=SubmitSceneChat;
        _ui.Chat.SceneAccessor=()=>_lastSampledPhysicalScene??_snapshotAccessor();
        _ui.Chat.StationPositionAccessor=()=>_stationActive&&_stationArrivalPublished&&!_stationRoomChanging&&!_awaitingWorld&&!_stopped?_w3dWorld.StationAvatarPose?.Position:null;
        _ui.Chat.InputOpened+=()=>{StopStationMovement();FlightInputSuspended?.Invoke();};
        _ui.Inventory.ModernLocalWalkRequested=WalkToStation;
        _ui.Inventory.ModernMusic=_w3dWorld.MusicPlayer;
        _hudGpu.EnableClientAudio=true;_hudGpu.ClientAudioListenerAccessor=()=>_w3dWorld.CurrentAudioListener;
        _hudGpu.MapWorldRenderer=_w3dWorld;_w3dWorld.AttachInterfaceShakeConsumer(_hudGpu);
        _ui.Cockpit.MoveShortcutRequested=request=>MoveShortcutRequested?.Invoke(request)==true;
        _ui.Inventory.TerminalCameraRequested=MoveToStationTerminal;_ui.Inventory.ConversationCameraRequested=MoveToStationNpc;
        _ui.Inventory.StationPlayerActionCompleted+=StopStationMovement;_ui.Options.ManualOpened+=StopStationMovement;
        _ui.Inventory.Changed+=_=>_host.PresentationChanged(this);
        _ui.Inventory.SkillDropped=(character,ship,ability,point)=>_ui.Cockpit.CaptureSkillDrop(character,ship,ability,point) is {} request&&AssignSkillRequested?.Invoke(request)==true;
        _ui.Inventory.ItemShortcutDropped=(item,point)=>_ui.Cockpit.CaptureItemDrop(item,point) is {} request&&AssignItemShortcutRequested?.Invoke(request)==true;
        _ui.Inventory.Opened+=()=>{_ui.Cockpit.CloseSkillMenu();_ui.Cockpit.CancelPointerInput();FlightInputSuspended?.Invoke();};
        _ui.Inventory.WorldDropAllowed=host.WorldDropAllowed;
        _ui.Options.Opened+=()=>{_ui.Cockpit.CancelPointerInputForOverlay(closeSkillMenu:!_ui.Options.QuitOpen);if(_ui.Options.SuspendsGameplayMovement)FlightInputSuspended?.Invoke();};
    }
    internal void NotifyGameplayInputSuspended()=>FlightInputSuspended?.Invoke();

    public string DiagnosticsText => Volatile.Read(ref _diagnosticsText)
        + " · world" + _w3dWorld.ClientAudioStatus
        + " · hud" + _hudGpu.ClientAudioStatus
        + Volatile.Read(ref _utilityInputDiagnostics)
        + _ui.Gutter.LabelDiagnostics
        + _ui.GalaxyMap.GalaxyOpenDiagnostics
        + $" · interface {(InterfaceHidden ? "hidden" : "shown")} blockers {InterfaceBlockers()} restored-by {InterfaceRestoredBy}";
    public Func<EnbSceneFrame?>? FrameSnapshotAccessor { get; set; }
    /// <summary>The local ship as this frame presents it (sector, position, forward): per-frame, unlike packet-time snapshots.</summary>
    public EnbPresentedShip? PresentedLocalShip { get; private set; }
    private readonly SpacePerformanceState _performanceState=new(null!,default,0,"");
    public SpacePerformanceState? PerformanceScene { get; private set; }
    internal void SetComparisonDetailSettings(EnbRenderDetailRuntimeSettings? settings)
    {
        _w3dWorld.RecordedDetailSettingsOverride = settings;
        _targetModelView.RecordedDetailSettingsOverride = settings;
        _hudGpu.RecordedDetailSettingsOverride = settings;
    }
    internal bool BenchmarkWorldReady => !_awaitingWorld && PerformanceScene is {} scene &&
        scene.Scene.Objects.Any(o=>o.IsLocalShip && o.Spatial is not null);
    internal object BenchmarkGraphicsAdapters=>new {World=_w3dWorld.GraphicsDeviceStatus,Hud=_hudGpu.GraphicsDeviceStatus,Scanner=_targetModelView.GraphicsDeviceStatus};
    internal string? BenchmarkRendererFailure=>_w3dWorld.LastRenderFailure ?? _hudGpu.LastRenderFailure ?? _targetModelView.LastRenderFailure;
    public Action<System.Collections.Immutable.ImmutableDictionary<EnbNext.Net7.Gameplay.Net7EffectPoseKey,EnbNext.Net7.Gameplay.Net7RetainedEffectPosition>>? EffectPositionsPublished
    { get=>_w3dWorld.EffectPositionsPublished; set=>_w3dWorld.EffectPositionsPublished=value; }
    public event Action? FlightInputSuspended;
    // Incapacitation is not a flight/target gate in retail: 005806E0 and
    // 006B58A0 consult only LockSpeed/LockOrient (0057D6C0), 0059DB10
    // targeting/autofollow has no gate, and 005821C0 enables the cockpit
    // buttons without it. Skill, shortcut and
    // equipment activation keep their own incapacitation checks.
    public bool CanControlFlight => CanUseCockpit && !_ui.Options.SuspendsGameplayMovement;
    public void HandleUtilityPanelAction(string action)
    {
        RecordUtilityInput(action, "dispatch");
        if (_stopped || _awaitingWorld || _host.IssuePanelOpen || _ui.Chat.Editing || _ui.Options.IsOpen ||
            _snapshotAccessor() is not { IsTransitioning: false })
        { RecordUtilityInput(action, "blocked by view state"); return; }
        // Next Modern UI: every panel key opens its Modern window, as the micro menu does (character: the skills).
        if (_ui.ModernUi)
        {
            var inventory = _ui.Inventory;
            switch (action)
            {
                case "Cargo": inventory.ToggleModernInventory(); break;
                case "Equipment": inventory.ToggleModernEquipment(); break;
                case "Vault": inventory.ToggleModernVault(); break;
                case "Character": inventory.ToggleModernSkills(); break;
                case "Missions": inventory.ToggleModernMissions(); break;
                case EnbNextControls.Factions: inventory.ToggleModernFactions(); break;
                case EnbNextControls.Local: inventory.ToggleModernLocal(); break;
                case EnbNextControls.Items: inventory.ToggleModernItems(); break;
                case EnbNextControls.Music: inventory.ToggleModernMusic(); break;
                case EnbNextControls.CaptainLog: _ui.ToggleCaptainLog(); break;
            }
            RecordUtilityInput(action, "result");
            return;
        }
        if (EnbNextControls.ModernWindows.Contains(action)) { RecordUtilityInput(action, "Modern UI only"); return; }
        if (action is "Cargo" or "Equipment" or "Vault")
        {
            if (_ui.Inventory.ToggleInventoryPanel(action))
            {
                if (_ui.Inventory.InventoryOpen) { _ui.Map.CloseForPopup(); _ui.GalaxyMap.CloseForPopup(); }
                _hudGpu.QueueUtilitySound(action == "Cargo" && _ui.Inventory.InventoryOpen
                    ? "SR_scifielectronic40.wav" : "SR_scifielectronic42.wav");
            }
            RecordUtilityInput(action, "result");
            return;
        }
        int tab = action == "Character" ? 0 : action == "Missions" ? 1 : -1;
        if (_ui.Inventory.ToggleCharacterTab(tab))
        {
            if (_ui.Inventory.CharacterOpen) { _ui.Map.CloseForPopup(); _ui.GalaxyMap.CloseForPopup(); }
            // Retail 00693E70 commands 10/11 share this cue, including opening/switching.
            _hudGpu.QueueUtilitySound("SR_scifielectronic42.wav");
        }
        RecordUtilityInput(action, "result");
    }
    public Task HandleVendorKey(bool buy)
    {
        if (_stopped || _awaitingWorld || _host.IssuePanelOpen || _ui.Chat.Editing || _ui.Options.IsOpen ||
            _snapshotAccessor() is not { IsTransitioning: false }) return Task.CompletedTask;
        return _ui.Inventory.VendorKey(buy);
    }
    public bool CanUseGeneralControls => !_ui.Layout.Editing && !_stopped && !_awaitingWorld && !_ui.Chat.Editing && !_host.IssuePanelOpen;
    public void SetImmediateTooltips(bool value)
    {
        _ui.Cockpit.SetImmediateTooltips(value);
        _ui.Scanner.SetImmediateTooltips(value);
        _ui.Inventory.SetImmediateTooltips(value);
        _ui.Map.SetImmediateTooltips(value);
        _ui.GalaxyMap.SetImmediateTooltips(value);
        _ui.Options.SetImmediateTooltips(value);
        _ui.Chat.SetImmediateTooltips(value);
    }
    public bool CanUseCockpit => CanUseGeneralControls && !_stationActive;
    public bool IsFlightSpeedLocked => _snapshotAccessor()?.Hud.LocalShipLockSpeedActive == true;
    public bool CanStartMouseFlight(EnbUiPoint point)
        => CanControlFlight && !_host.IssueContains(point) && !_ui.Chat.Contains(point) && !_ui.Inventory.BlocksFlight(point) && !_ui.Options.BlocksFlight(point) && !_ui.Scanner.ContainsFlightBlockingControl(point) && !_ui.Cockpit.BlocksFlight(point) && _ui.MapAt(point) is null;
    public EnbOptionsUi Options => _ui.Options;
    public EnbInventoryUi Inventory => _ui.Inventory;
    public EnbChatUi Chat => _ui.Chat;
    public Func<EnbShortcutAssignmentRequest,bool>? AssignSkillRequested {get;set;}
    public Func<EnbShortcutItemAssignmentRequest,bool>? AssignItemShortcutRequested {get;set;}
    public Func<EnbShortcutMoveRequest,bool>? MoveShortcutRequested {get;set;}
    public EnbNavigationMapUi NavigationMap => _ui.Map;
    public EnbNavigationMapUi GalaxyMap => _ui.GalaxyMap;
    internal EnbNavigationMapUi TopMap => _ui.TopMap;
    public EnbCockpitUi Cockpit => _ui.Cockpit;
    public Task<bool> TargetSelfAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUseCockpit) return Task.FromResult(false);
        uint? localShipId = _snapshotAccessor()?.LocalShipObjectId;
        return localShipId is uint id
            ? RequestTargetLocallyAsync(id, cancellationToken)
            : Task.FromResult(false);
    }

    public Task<bool> SelectNearestPlayerAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshotAccessor();
        if (!CanControlFlight || snapshot?.Objects.FirstOrDefault(o => o.IsLocalShip) is not { } observer)
            return Task.FromResult(false);
        var radar = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(
            EnbCurrentRetailRadarCompatibilityAdapter.Build(snapshot, _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity),
            _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity);
        return RequestTargetLocallyAsync(EnbCurrentRetailTargetingRuntime.SelectNearestPlayer(radar, observer), cancellationToken);
    }

    public Task<bool> SelectGroupMemberAsync(int index, CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshotAccessor();
        if (!CanControlFlight || snapshot is null) return Task.FromResult(false);
        return EnbCurrentRetailTargetingRuntime.SelectGroupMember(snapshot.Hud.Group, snapshot.Objects, index) is uint id
            ? RequestTargetLocallyAsync(id, cancellationToken) : Task.FromResult(false);
    }

    public Task<bool> SelectNearestEnemyAsync(CancellationToken cancellationToken = default)
        => SelectNearestTargetAsync(EnbRetailTargetCategory.Attackable, cancellationToken);

    public Task<bool> SelectNearestTargetAsync(EnbRetailTargetCategory category, CancellationToken cancellationToken = default)
    {
        var snapshot = _snapshotAccessor();
        if (!CanControlFlight || snapshot is null) return Task.FromResult(false);
        var radar = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(
            EnbCurrentRetailRadarCompatibilityAdapter.Build(snapshot, _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity),
            _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity);
        return RequestTargetLocallyAsync(EnbCurrentRetailTargetingRuntime.SelectNearest(radar, category), cancellationToken);
    }

    public Task<bool> SelectAdjacentTargetAsync(
        bool previous,
        bool preserveCurrentCategory,
        CancellationToken cancellationToken = default)
    {
        IEnbSceneFrame? snapshot = _snapshotAccessor();
        if (!CanControlFlight || snapshot is null)
            return Task.FromResult(false);
        EnbRadarFrame radar = EnbCurrentRetailRadarCompatibilityAdapter.Build(snapshot, _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity);
        radar = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(
            radar,
            _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity);
        // Local selection is visible before the asynchronously published
        // presentation snapshot catches up. Use that immediate retail scanner
        // state so two quick next/previous inputs advance twice instead of
        // selecting the same snapshot-era contact again.
        uint? current = _ui.Scanner.SelectedTarget?.GameId ??
            (snapshot.Hud.HasLocalTargetSelection
                ? snapshot.Hud.LocalSelectedTargetId
                : snapshot.Hud.CurrentTargetId);
        uint? adjacent = EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            radar,
            current,
            previous,
            preserveCurrentCategory);
        return adjacent is uint id
            ? RequestTargetLocallyAsync(id, cancellationToken)
            : Task.FromResult(false);
    }

    private async Task<bool> RequestTargetLocallyAsync(uint targetId, CancellationToken cancellationToken)
    {
        // Retail updates the local target pointer and scanner before doing the
        // network request. Invoke the request directly as well: its synchronous
        // prefix publishes Gameplay.LocalSelectedTarget before its first await.
        // Scheduling that prefix on independent thread-pool workers allowed a
        // rapid A -> B selection to execute B -> A and restore the old target.
        long selectionToken = _ui.Scanner.ApplyLocalTargetSelection(targetId);
        _ui.Gutter.SetSelectedTarget(targetId == 0 ? null : targetId);
        _host.PresentationChanged(this);
        if (targetId == 0)
        {
            _targetModelView.ClearFrame();
            _targetPreviewKey = null;
            _targetPresentationToken = 0;
        }
        try
        {
            bool requested = await _targetRequester(targetId, cancellationToken);
            if (!requested)
                _ui.Scanner.RejectLocalTargetSelection(selectionToken);
            return requested;
        }
        catch (InvalidOperationException)
        {
            // Sector handoff can retire gameplay authority between a visible
            // marker/input and dispatch. Reject only this optimistic selection;
            // a newer click is protected by its distinct sequence token.
            _ui.Scanner.RejectLocalTargetSelection(selectionToken);
            return false;
        }
        catch
        {
            _ui.Scanner.RejectLocalTargetSelection(selectionToken);
            throw;
        }
    }

    public void Start()
    {
        _stopped = false;
        _lastCameraFrameTimestamp = 0;
        _lastDiagnosticsRefreshTimestamp = 0;
        _uiCadenceWindowStartTimestamp = 0;
        _uiCadenceWindowFrames = 0;
        _uiCadenceWindowCpuMilliseconds = 0.0;
        _uiCadenceWindowWorstCpuMilliseconds = 0.0;
        _uiCadenceStatus = "ui scene pump: waiting for presentation cadence";
        RefreshWorld();
        QueueAnimationFrame(++_framePumpGeneration);
    }

    private void QueueAnimationFrame(int generation)
    {
        _host.RequestAnimationFrame(() =>
        {
            if (_stopped || generation != _framePumpGeneration) return;
            RefreshWorld();
            QueueAnimationFrame(generation);
        });
    }

    public void Stop()
    {
        Autopilot?.Cancel("World screen closed");
        _w3dWorld.DisposeRenderSceneSubscription();
        ResetRetailCamera();
        ClearStationPreloads();
        _w3dWorld.SetStationInterior(null);
        _warpCamera.Reset();
        _cameraController.ResetMotion();
        _shipVisualBank.Clear();
        FlightInputSuspended?.Invoke();
        _framePumpGeneration++;
        _lastSampledPhysicalScene?.Release();_lastSampledPhysicalScene=null;
        _stopped = true;
        _awaitingWorld = true;
        _worldPresentationToken++;
        _w3dWorld.FinishColdAssets();
        LoadingVisible = true;
        _loadingScreen.Dispose();
        _stationShipHint.Dispose();
        _host.ReleaseUi();
        _hudGpu.SetLoadingPresentation(null);
        _lastCameraFrameTimestamp = 0;
        _w3dWorld.ClearFrame();
        _targetModelView.ClearFrame();
        _targetPreviewKey = null;
        _targetPresentationToken = 0;
        _environmentRenderer?.Dispose();
        _environmentRenderer = null;
        _environmentRendererResource = null;
        _environmentRendererIsInfluence = false;
        _environmentRendererAssetBundle = null;
        _environmentRendererInfluenceColor = null;
        _influenceSkyPresentationPreflightAttempted = false;
        _influenceSkyPresentationPreflightAssets = null;
        _influenceSkyPresentationStatus =
            "influence sky renderer: R7B1_CURRENT_RETAIL_INFLUENCE_SKY_PRESENTATION_READY · waiting for installed BaseAsset 311";
        _starFieldRenderer?.Dispose();
        _starFieldRenderer = null;
        _ui.Gutter.ReleaseAssets();
        _ui.Scanner.ReleaseAssets();
    }

    internal void AdoptClientAudio(LiveWorldOpenAlAudio audio)
    {
        _w3dWorld.UseSpeechScope(audio.Speech);
        _hudGpu.UseSpeechScope(audio.Speech);
        _hudGpu.AdoptClientAudio(audio);
    }

    internal void RefreshWorld()
    {
        try { RefreshWorldCore(); } finally { _host.PresentationChanged(this); }
    }
    private void RefreshWorldCore()
    {
        using var benchmarkScope = FullClientBenchmarkMeter.Enter("scene-pump");
        UpdateIssues();
        if (_stopped) return;
        long refreshStarted = Stopwatch.GetTimestamp();
        _host.UpdatePerformance(_ui.Chat.PerformanceVisible && !_awaitingWorld,_w3dWorld.Performance);
        double cameraFrameElapsedMilliseconds = CaptureCameraFrameElapsedMilliseconds();
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width < 40 || height < 40)
        { _ui.Inventory.PauseGroupFrame(); _ui.Cockpit.PauseBuffFrame(); return; }

        // Station hover/departure restores this cursor before any space frame.
        _host.EnsureCursor(_retailAssetsAccessor());
        EnbSceneFrame? snapshot = FrameSnapshotAccessor is {} acquire?acquire():_diagnosticSceneImport.Acquire(_snapshotAccessor());
        PresentedLocalShip = null;
        var previousPhysical=_lastSampledPhysicalScene;
        _lastSampledPhysicalScene=snapshot;
        previousPhysical?.Release();
        if (snapshot is not null) snapshot = _shipWarpCache.ApplyFrame(snapshot);
        if (snapshot is { IsTransitioning: true } or { ActiveStarbaseId: not null })
            snapshot = _cockpitWarpCallbacks.ApplyFrame(snapshot, EnbRetailCameraState.Wait, active: false);
        if (snapshot is null || snapshot.IsTransitioning || snapshot.ActiveStarbaseId is not null)
        { ResetRetailCamera(); }
        // UI/voice audio outlives the space renderer during loading and docking.
        _hudGpu.SetClientAudioScene(snapshot);
        long sampleEnded = Stopwatch.GetTimestamp();
        if (snapshot?.LocationTransition != _locationTransition)
        {
            _locationTransition = snapshot?.LocationTransition;
            BeginPresentationWait();
        }
        UpdateLoadingScreen();
        if(_awaitingWorld && snapshot is not null)_w3dWorld.PrepareColdAssets(snapshot);
        // 0047/003E can materialize the ship before 004F identifies a station.
        // Start installs LocalPlayerId; until then a drawable ship
        // is not a completed initial scene. Keep the existing GPU receipt gate
        // after Start, without changing packet processing or failure recovery.
        if (snapshot is { LocalPlayerId: null })
        {
            ShowEmpty("Awaiting sector Startâ€¦");
            return;
        }
        if (snapshot is { IsTransitioning: false, ActiveStarbaseId: not null })
        {
            ShowStation(snapshot);
            return;
        }
        _stationActive = false; _ui.Options.SuppressUtilityHud=false;
        if(_stationPreloadCancellation is not null)ClearStationPreloads();
        HudVisible = true;
        _ui.Scanner.IsVisible = true;
        _w3dWorld.SetStationInterior(null);
        _stationDefinitionKey = null;
        _stationKey = null;
        if (snapshot?.IsTransitioning == true || snapshot?.StationAvatars.Length > 0)
        {
            ShowEmpty(snapshot.IsTransitioning ? "Sector handoff in progressâ€¦" : "Station presentation is not implemented yet.");
            return;
        }
        if (snapshot?.LocalShipObjectId is not uint localShipObjectId)
        {
            HideStarField();
            ShowEmpty("Awaiting local ship identityâ€¦");
            return;
        }

        uint currentClientTime = FrameSnapshotAccessor is not null
            ? snapshot.ClientTime : _currentClientTimeAccessor() ?? snapshot.ClientTime;

        IEnbRenderObject? localObject = snapshot.Objects.FirstOrDefault(static obj => obj.IsLocalShip);
        if (localObject?.Spatial?.Transform is not { } localTransform || !IsFinite(localTransform.Position))
        {
            HideStarField();
            ShowEmpty("Awaiting local spatial stateâ€¦");
            return;
        }

        if (snapshot.Environment?.SectorNum is uint presentedSector)
            PresentedLocalShip = new(presentedSector, localTransform.Position, localTransform.OrientationColumn0);
        double renderScale = _host.Scale;
        if (!EnbRenderViewport.TryFromLogicalSize(width, height, renderScale, out EnbRenderViewport viewport))
        {
            HideStarField();
            ShowEmpty("Render viewport is not projectableâ€¦");
            return;
        }

        EnsureCameraController(snapshot.Camera);
        SampleCameraInput?.Invoke(cameraFrameElapsedMilliseconds);
        if (!_manualCameraInput) _cameraController.EndManualCameraInput();
        if (_localPresentationIdentity != localObject.RenderIdentity) _cameraController.ResetMotion();
        var temporaryCamera = UpdateRetailCamera(snapshot, localObject, currentClientTime, out var emoteSnapshot);
        emoteSnapshot = _cockpitWarpCallbacks.ApplyFrame(emoteSnapshot, _retailCamera.State);
        if (!_cameraController.TryResolveFrame(
                snapshot.Camera,
                localTransform,
                localObject.ObjectType,
                cameraFrameElapsedMilliseconds,
                viewport,
                out EnbNormalCameraFrame cameraFrame,
                temporaryCamera))
        {
            HideStarField();
            ShowEmpty($"Retail camera mode {_cameraController.ActiveModeIndex} is not projectableâ€¦");
            return;
        }

        EnbResolvedRenderCamera camera;
        try
        {
            camera = UpdateBridgeCamera(localObject, cameraFrame.Camera, temporaryCamera,
                cameraFrameElapsedMilliseconds, width, height);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            ShowEmpty("Bridge camera unavailable: " + ex.Message);
            return;
        }
        if (!_cameraController.IsFirstPerson && temporaryCamera?.Position is null && temporaryCamera?.Orientation is null)
        {
            camera = _w3dWorld.ResolveShipCameraCollision(camera, localTransform.Position,
                _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity.GetValueOrDefault(localObject.RenderIdentity,
                    EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(localObject)),
                localObject.RenderIdentity, snapshot.Camera.KeepShipInView);
            _cameraController.ApplyCollisionPosition(camera.Position);
        }
        _previousCameraPosition = camera.Position;
        _previousCameraOrientation = camera.WorldOrientation;
        EnbSceneFrame visualSnapshot = _shipVisualBank.ApplyFrame(emoteSnapshot, cameraFrameElapsedMilliseconds);
        visualSnapshot = ApplyLandCamera(visualSnapshot,localObject.RenderIdentity,localObject.RenderPoseSlot);
        visualSnapshot = ApplyTakeoffCamera(visualSnapshot,localObject.RenderIdentity,localObject.RenderPoseSlot);
        if(_gateCamera.ShipTransform is {} gateTransform)visualSnapshot.ApplyTransform(localObject.RenderIdentity,gateTransform);
        if(_dockCamera.ShipTransform is {} dockTransform)visualSnapshot.ApplyTransform(localObject.RenderIdentity,dockTransform);
        // Next Modern UI "Hide corpses in space": hulks (type 19h) are not drawn and cannot be clicked in space; the
        // Local window lists them from the radar contacts, which keep them.
        if(HideCorpses && HasCorpse(visualSnapshot.Objects))visualSnapshot.Objects=WithoutCorpses(visualSnapshot.Objects);

        if (_presentationViewport != viewport || _localPresentationIdentity != localObject.RenderIdentity)
        {
            bool identityChanged = _localPresentationIdentity != localObject.RenderIdentity;
            _presentationViewport = viewport;
            _localPresentationIdentity = localObject.RenderIdentity;
            if (identityChanged || !_ui.Options.KeepsOpenThroughResize) BeginPresentationWait();
        }

        EmptyVisible = false;
        EnbMixResourceCatalog? resources = _resourceAccessor();
        EnbRetailAssetBundle? retailAssets = _retailAssetsAccessor();
        bool nativeEnvironment = TryDrawNativeEnvironment(snapshot, resources, retailAssets);
        bool nativeStars = TryDrawRetailStarField(snapshot, resources, retailAssets);
        EnbBackdropFrame? backdrop = nativeEnvironment || nativeStars
            ? new EnbBackdropFrame(
                nativeEnvironment ? _environmentRenderer!.Geometry : EnbBackdropGeometry.Empty,
                nativeStars ? _starFieldRenderer!.Geometry : [])
            : null;
        _ui.Chat.Update(snapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld);
        snapshot = _spaceSpeakers.ApplyFrame(snapshot, unchecked((uint)Math.Max(0, cameraFrameElapsedMilliseconds)), _ui.Chat.SpeakerColors);
        EnbRadarFrame radarFrame = EnbCurrentRetailRadarCompatibilityAdapter.Build(snapshot, _w3dWorld.ProjectedObjectBounds.DistanceRadiiByRenderIdentity);
        long backdropEnded = Stopwatch.GetTimestamp();
        LiveCompletedRenderQueries renderedObjectBounds = _w3dWorld.ProjectedObjectBounds;
        LivePlanetGpuPresentation planetGpuPresentation = _w3dWorld.PlanetGpuPresentation;
        radarFrame = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(
            radarFrame,
            renderedObjectBounds.DistanceRadiiByRenderIdentity);
        uint? radarSelectedId=snapshot.Hud.HasLocalTargetSelection?snapshot.Hud.LocalSelectedTargetId:snapshot.Hud.CurrentTargetId;
        // 7BCA70 independently admits the current target even outside range,
        // plus self when Show_Self_Name is enabled. Ordinary contact consumers
        // retain their range-limited population.
        var gutterFrame=EnbCurrentRetailRadarCompatibilityAdapter.Build(snapshot,
            renderedObjectBounds.DistanceRadiiByRenderIdentity,includeLocal:_ui.Chat.ShowSelfName,retainSelected:true);
        if(HideCorpses && gutterFrame.Contacts.Any(c=>c.ObjectType==0x19))gutterFrame=gutterFrame with {Contacts=gutterFrame.Contacts.RemoveAll(c=>c.ObjectType==0x19)};
        _ui.Gutter.RenderScaling=renderScale;
        _ui.Gutter.Bounds=new(0,0,width,height);
        if(DeveloperRenderDiagnostics.Gutter) _ui.Gutter.Update(
            gutterFrame,
            camera,
            retailAssets,
            width,
            height,
            renderedObjectBounds, currentClientTime);
        GutterVisible = DeveloperRenderDiagnostics.Gutter && DeveloperRenderDiagnostics.Hud;
        _ui.Scanner.RenderScaling=renderScale;
        _ui.Scanner.UpdateAtTime(snapshot, radarFrame, retailAssets, width, height,currentClientTime);
        // Local lists the radar's contacts: navigation and huge objects at any range, the rest within it. The gutter also
        // keeps the current target beyond that range (7BCA70); Local lists it too, so with the Modern rail off it is not lost.
        var localFrame = radarFrame;
        if (gutterFrame.Contacts.FirstOrDefault(c => c.IsCurrentTarget && !c.IsLocalShip) is { } farTarget && !radarFrame.Contacts.Any(c => c.GameId == farTarget.GameId))
            localFrame = radarFrame with { Contacts = radarFrame.Contacts.Add(farTarget) };
        _ui.Inventory.ModernLocalFrame = localFrame; _ui.Inventory.ModernLocalStation = null;
        _ui.UpdateModern(snapshot, retailAssets);
        _ui.Gutter.SetSelectedTarget(radarSelectedId);
        TrackInterfaceBlockers();
        _w3dWorld.SetScannerPresentation(_ui.Scanner.TargetModelBounds,
            _ui.Scanner.PanelBounds);
        _w3dWorld.SetGaugePresentation(_ui.Scanner.GaugePresentation);
        _ui.Cockpit.SetCombatTarget(radarFrame.Contacts.FirstOrDefault(c=>c.RenderIdentity==_ui.Scanner.SelectedTarget?.RenderIdentity));
        _ui.Cockpit.UpdateAtTime(emoteSnapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld, currentClientTime);

        _ui.Options.Update(snapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld);
        _ui.Inventory.Update(snapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld);
        // 742F50 -> UI/group update precedes 742F85 -> radar/map refresh.
        _ui.Map.SetRadarLabelContacts(radarFrame);
        _ui.Map.Update(snapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld);
        if (_ui.GalaxyMap.Modern) _ui.GalaxyMap.Update(snapshot, retailAssets, new(width, height), renderScale, !_awaitingWorld);
        UpdateAutopilot(snapshot);
        _hudGpu.SetMapPreparation(_ui.Map.Preparation);
        _w3dWorld.SetCockpitPresentation(_ui.Cockpit.Presentation);
        UpdateTargetModel(
            visualSnapshot,
            currentClientTime,
            renderScale,
            renderedObjectBounds,
            planetGpuPresentation);
        _ui.Scanner.SetTargetModelReady(
            _ui.Scanner.SelectedTarget is null ||
            (_targetPresentationToken != 0 &&
             _targetModelView.PresentedFrameToken == _targetPresentationToken));
        // Publish the main draw only after its scanner inputs have been staged.
        // Asset-specific sky rejection must not hold an active sector behind
        // loading. Release only after this sector's world frame is presented;
        // unsupported artwork remains reported in the rendering diagnostics.
        if (_awaitingWorld && _w3dWorld.PresentedFrameToken == _worldPresentationToken)
        {
            _awaitingWorld = false;
            _w3dWorld.FinishColdAssets();
            LoadingVisible = false;
            _hudGpu.SetLoadingPresentation(null);
        }
        _w3dWorld.UpdateFrame(visualSnapshot, camera, cameraFrame.IsFirstPerson && !_shipEmoteCamera.Active, currentClientTime, backdrop,
            presentationToken: _worldPresentationToken);
        _performanceState.Scene=visualSnapshot;_performanceState.Camera=camera;_performanceState.ClientTime=currentClientTime;
        _performanceState.RendererStatus=_w3dWorld.StatusText;_performanceState.CapturedUtc=DateTimeOffset.UtcNow;
        _performanceState.FirstPerson=cameraFrame.IsFirstPerson && !_shipEmoteCamera.Active;
        _performanceState.DetailSettings=_w3dWorld.PerformanceDetailSettings;_performanceState.FixedSessionId=_fixedSessionIdAccessor();
        PerformanceScene=visualSnapshot.IsTransitioning || visualSnapshot.ActiveStarbaseId is not null?null:_performanceState;
        UpdateLoadingScreen();

        var projected = new List<ProjectedObject>(snapshot.Objects.Count);
        long hudEnded = Stopwatch.GetTimestamp();
        int sampledCount = 0;
        int radarRangeDetached = 0;

        foreach (IEnbRenderObject obj in snapshot.Objects)
        {
            if (obj.IsLocalShip)
                continue;

            if (obj.Spatial?.Transform is not { } transform || !IsFinite(transform.Position))
                continue;

            if (!EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(localObject, obj, snapshot.Hud.ScanRange, renderedObjectBounds.DistanceRadiiByRenderIdentity))
            {
                radarRangeDetached++;
                continue;
            }

            sampledCount++;
            if (IsRenderablePlanet(obj))
            {
                if (obj.ClientBaseAssetReference is { } geometry &&
                    TryScaleClientBaseAsset(geometry, obj.Scale, out float capturedWorldRadius))
                {
                    CapturePlanetAssetOnce(resources, obj, capturedWorldRadius, "ClientBaseAsset geometry");
                }

                if (!planetGpuPresentation.Contains(obj))
                {
                    _planetRenderStatus =
                        "planet: R8A GPU presentation pending/unresolved · production software planet fallback disabled";
                    continue;
                }

                string gpuPlanetStatus = planetGpuPresentation.Status.Replace("render planet:", "planet:", StringComparison.Ordinal);
                _planetRenderStatus = gpuPlanetStatus + " · coherent GPU-only planet presentation";
                continue;
            }

            if (!TryProject(camera, transform.Position, viewportMargin: 10, out LiveWorldLogicalProjection projection))
                continue;

            projected.Add(new ProjectedObject(obj, projection));
        }

        CaptureEnvironmentAssetOnce(snapshot, resources);
        long projectionEnded = Stopwatch.GetTimestamp();
        CaptureStarFieldOnce(snapshot, resources, retailAssets);
        CaptureInfluenceSkyEvidenceOnce(retailAssets);
        PreflightInfluenceSkyPresentation(retailAssets);
        long refreshEnded = Stopwatch.GetTimestamp();
        RecordUiCadence(refreshStarted, refreshEnded);
        RecordFormationMotion(snapshot, localShipObjectId);
        if (ShouldRefreshDiagnostics(refreshEnded))
        {
            // Diagnostic snapshot walks and formatting share the publication cadence.
            string localName = string.IsNullOrWhiteSpace(localObject.Name) ? $"0x{localShipObjectId:X8}" : localObject.Name!;
            string gameplayPlayer = snapshot.LocalPlayerId is uint playerId ? $"0x{playerId:X8}" : "<pending>";
            EnvironmentAssetStatus environment = DescribeEnvironment(snapshot, resources);
            _renderResidualStatus = DescribeLiveRenderResiduals(snapshot);
            string uiStageStatus = $" · stages sample/backdrop/HUD/project/tail {Stopwatch.GetElapsedTime(refreshStarted,sampleEnded).TotalMilliseconds:0.00}/{Stopwatch.GetElapsedTime(sampleEnded,backdropEnded).TotalMilliseconds:0.00}/{Stopwatch.GetElapsedTime(backdropEnded,hudEnded).TotalMilliseconds:0.00}/{Stopwatch.GetElapsedTime(hudEnded,projectionEnded).TotalMilliseconds:0.00}/{Stopwatch.GetElapsedTime(projectionEnded,refreshEnded).TotalMilliseconds:0.00}ms";
            string visibleSpatialAudit = DescribeVisibleSpatialAudit(projected);
            string radarPlayerMetadata = DescribeRadarPlayerMetadata(snapshot);
            Volatile.Write(ref _diagnosticsText,
                "LIVE WORLD VIEW · R6Q\n" +
                $"snapshot v{snapshot.Version} @ {snapshot.ClientTime}ms · live {currentClientTime}ms · camera {localName} · ship 0x{localShipObjectId:X8} · player {gameplayPlayer}\n" +
                $"mode {cameraFrame.ActiveModeIndex} ({(cameraFrame.IsFirstPerson ? "first-person" : "third-person")}) · remembered 3P {cameraFrame.RememberedThirdPersonModeIndex} · " +
                $"bank {RadiansToDegrees(cameraFrame.BankRadians):0.##}Â° â†’ {RadiansToDegrees(cameraFrame.BankTargetRadians):0.##}Â° · {(snapshot.Camera.GateTransition is null ? _warpCamera.Phase : _gateCamera.Phase)} · {_cameraControlStatus} · " +
                $"anchor q {FormatQuaternion(localTransform.Orientation)} |q|Â² {localTransform.Orientation.LengthSquared():0.######} · " +
                $"pos {FormatVector(camera.Position)} · fwd {FormatPreciseVector(camera.Basis.Forward)}\n" +
                $"HFOV {RadiansToDegrees(camera.Projection.HorizontalFieldOfViewRadians):0.#}Â° · VFOV {RadiansToDegrees(camera.Projection.VerticalFieldOfViewRadians):0.#}Â° · " +
                $"clip {camera.Projection.ClipRange.NearPlane:0.##}..{camera.Projection.ClipRange.FarPlane / 1_000_000f:0.#}M · dpi {camera.Viewport.RenderScale:0.##}x\n" +
                visibleSpatialAudit + "\n" +
                DescribeFormationSpatialAudit(snapshot, localShipObjectId) + "\n" +
                DescribeFormationMotion() + "\n" +
                $"radar adapter: scan {snapshot.Hud.ScanRange?.ToString(CultureInfo.InvariantCulture) ?? "<pending>"} · " +
                $"observer radius {radarFrame.ObserverWorldRadius:0.###} · " +
                $"targetable {radarFrame.Contacts.Length} · gutter {_ui.Gutter.VisibleMarkerCount} " +
                $"({_ui.Gutter.ClampedMarkerCount} edge-clamped)\n" +
                radarPlayerMetadata + "\n" +
                $"{_uiCadenceStatus}{uiStageStatus} · objects {snapshot.Objects.Count} · spatial {sampledCount + 1} · visible {projected.Count} · radar-range detached {radarRangeDetached} · {environment.Summary}\n" +
                environment.Resource + "\n" +
                _environmentRenderStatus + "\n" +
                _starRenderStatus + "\n" +
                _planetRenderStatus + "\n" +
                _w3dWorld.StatusText + "\n" +
                _hudGpu.OverlayRenderDiagnostic + "\n" +
                $"cockpit: {_ui.Cockpit.Error ?? (_ui.Cockpit.Presentation is null ? "pending" : "TSI01A GPU")} · input {(_ui.Cockpit.CanThrust && CanControlFlight ? "ready" : "suspended")}\n" +
                $"utility/options: {_ui.Options.Error ?? "TSI14A/OPT01A/OPT03A GPU"} · {(_ui.Options.IsOpen ? "open" : "closed")} · experience {snapshot.Hud.Experience}\n" +
                $"inventory/character: {_ui.Inventory.Error ?? "GPU"} · {(_ui.Inventory.IsOpen ? "open" : "closed")} · cargo {snapshot.Hud.Inventory?.Cargo.Length ?? 0} · equipped {snapshot.Hud.Inventory?.Equipped.Length ?? 0} · skills {snapshot.Hud.Inventory?.Skills.Length ?? 0}\n" +
                $"buffs: {_ui.Cockpit.VisibleBuffCount} visible / {snapshot.Hud.Buffs.Length} server entries · {_ui.Cockpit.BuffError ?? "TSI02A GPU / buffdef.ini"}\n" +
                $"navigation map: {_ui.Map.Status}\n" +
                (_ui.Scanner.VerbExecutionError is {} verbError ? $"target action: {verbError}\n" : "") +
                (snapshot.Hud.Shortcuts is { } shortcuts
                    ? $"shortcuts: {shortcuts.ResolvedCount}/{shortcuts.AssignedCount} resolved · {shortcuts.Saved.Sections.Count(s => s.Exists)}/3 saved sections · {shortcuts.Saved.Diagnostics.Length} file diagnostics · six visible slots / independent banks · skill/equipment clicks + configured keys / held Alt banks / level picker (session only); cargo use and readiness UI pending\n"
                    : "shortcuts: identity data unavailable\n") +
                (_ui.Scanner.SelectedTarget is null
                    ? "target renderer: inactive\n"
                    : "target renderer: " + _targetModelView.StatusText.Replace("\n", " | ") + "\n") +
                _renderResidualStatus + "\n" +
                _influenceSkyPresentationStatus + "\n" +
                _influenceSkyCaptureStatus + "\n" +
                _environmentCaptureStatus + "\n" +
                _starCaptureStatus + "\n" +
                _planetCaptureStatus);
        }
    }

    private static string DescribeRadarPlayerMetadata(IEnbSceneFrame snapshot)
    {
        IEnbRenderObject[] players = snapshot.Objects
            .Where(static obj => obj.ObjectType == 0x01 && !obj.IsLocalShip)
            .ToArray();
        if (players.Length == 0)
            return "radar player metadata: no remote player objects";

        int auxData = players.Count(static obj => obj.ShipAuxDataAudit is not null);
        int owners = players.Count(static obj => !string.IsNullOrWhiteSpace(obj.PlayerPresentation?.Owner));
        int titles = players.Count(static obj => !string.IsNullOrWhiteSpace(obj.PlayerPresentation?.Title));
        int guilds = players.Count(static obj => !string.IsNullOrWhiteSpace(obj.PlayerPresentation?.GuildName));
        int shields = players.Count(static obj => obj.PlayerPresentation?.ShieldPercent is not null);
        string sample = string.Join(", ", players.Take(4).Select(DescribeRadarPlayerMetadataSample));
        return
            $"radar player metadata: remote {players.Length} · object AuxData {auxData}/{players.Length} · " +
            $"owner/title/guild/shield {owners}/{titles}/{guilds}/{shields} · [{sample}]";
    }

    private static string DescribeRadarPlayerMetadataSample(IEnbRenderObject obj)
    {
        EnbSceneAuxDataMaterializationAuditSnapshot? audit = obj.ShipAuxDataAudit;
        if (audit is null)
            return $"0x{obj.GameId:X8}/aux0";

        EnbScenePlayerPresentationSnapshot? player = obj.PlayerPresentation;
        string faction = string.IsNullOrWhiteSpace(player?.FactionIdentifier)
            ? "faction-pending"
            : player.FactionReaction is float reaction
                ? $"faction={player.FactionIdentifier}:{reaction.ToString("0.###", CultureInfo.InvariantCulture)}"
                : $"faction={player.FactionIdentifier}:standing-pending";
        string outcome = audit.LargestAccepted ? "accepted" : audit.LargestRetailCompatiblePartial ? "partial (retail-compatible)" : "REJECT";
        string error = audit.LargestRetailCompatiblePartial || string.IsNullOrWhiteSpace(audit.LargestError)
            ? string.Empty
            : $"/{audit.LargestError.Replace('\r', ' ').Replace('\n', ' ')}";
        return
            $"0x{obj.GameId:X8}/aux{audit.AttemptCount}:max{audit.LargestDeltaLength}" +
            $"/sel0x{audit.LargestSelector:X2}/{outcome}/{faction}{error}";
    }

    private bool ShouldRefreshDiagnostics(long nowTimestamp)
    {
        if (_lastDiagnosticsRefreshTimestamp == 0 ||
            (nowTimestamp - _lastDiagnosticsRefreshTimestamp) * 1000.0 / Stopwatch.Frequency >= DiagnosticsRefreshMilliseconds)
        {
            _lastDiagnosticsRefreshTimestamp = nowTimestamp;
            return true;
        }

        return false;
    }

    private void RecordUiCadence(long refreshStarted, long refreshEnded)
    {
        if (_uiCadenceWindowStartTimestamp == 0)
            _uiCadenceWindowStartTimestamp = refreshStarted;

        double cpuMilliseconds = Math.Max(0.0, (refreshEnded - refreshStarted) * 1000.0 / Stopwatch.Frequency);
        _uiCadenceWindowFrames++;
        _uiCadenceWindowCpuMilliseconds += cpuMilliseconds;
        _uiCadenceWindowWorstCpuMilliseconds = Math.Max(_uiCadenceWindowWorstCpuMilliseconds, cpuMilliseconds);

        long windowTicks = refreshEnded - _uiCadenceWindowStartTimestamp;
        if (windowTicks < Stopwatch.Frequency)
            return;

        double seconds = windowTicks / (double)Stopwatch.Frequency;
        double hertz = _uiCadenceWindowFrames / seconds;
        double averageCpuMilliseconds = _uiCadenceWindowFrames == 0
            ? 0.0
            : _uiCadenceWindowCpuMilliseconds / _uiCadenceWindowFrames;
        _uiCadenceStatus =
            $"ui scene pump {hertz:0.0}Hz · cpu {averageCpuMilliseconds:0.00}ms avg/{_uiCadenceWindowWorstCpuMilliseconds:0.00}ms worst · R11 animation-frame sampling";

        _uiCadenceWindowStartTimestamp = refreshEnded;
        _uiCadenceWindowFrames = 0;
        _uiCadenceWindowCpuMilliseconds = 0.0;
        _uiCadenceWindowWorstCpuMilliseconds = 0.0;
    }

    private static string DescribeLiveRenderResiduals(IEnbSceneFrame snapshot)
    {
        int spatial = 0;
        int fileName = 0;
        int rsl = 0;
        int nullObject = 0;
        int proceduralWidth = 0;
        int missing = 0;
        int unsupported = 0;
        int pointLights = 0;
        var residuals = new Dictionary<int, LiveRenderResidualEntry>();

        foreach (IEnbRenderObject obj in snapshot.Objects)
        {
            if (obj.Spatial?.Transform is null)
                continue;

            spatial++;
            if (obj.ObjectType == 0x08)
            {
                pointLights++;
                continue;
            }

            EnbPresentationBaseAssetReference? reference = obj.BaseAssetReference;
            EnbPresentationBaseAssetKind kind = reference?.Kind ?? EnbPresentationBaseAssetKind.Missing;
            switch (kind)
            {
                case EnbPresentationBaseAssetKind.FileName:
                    fileName++;
                    break;
                case EnbPresentationBaseAssetKind.Rsl:
                    rsl++;
                    break;
                case EnbPresentationBaseAssetKind.NullObject:
                    nullObject++;
                    break;
                case EnbPresentationBaseAssetKind.ProceduralWidth:
                    proceduralWidth++;
                    AddResidual(obj, reference, kind, residuals);
                    break;
                case EnbPresentationBaseAssetKind.Missing:
                    missing++;
                    AddResidual(obj, reference, kind, residuals);
                    break;
                default:
                    unsupported++;
                    AddResidual(obj, reference, kind, residuals);
                    break;
            }
        }

        int residualCount = proceduralWidth + missing + unsupported;
        string detail = residuals.Count == 0
            ? "none"
            : string.Join(
                "; ",
                residuals
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => pair.Value.Describe()));

        return
            $"render census: R6Z0_LIVE_RENDER_RESIDUAL_CENSUS · spatial {spatial} · " +
            $"FILE/NAME {fileName} · RSL {rsl} · null {nullObject} · lights {pointLights} · " +
            $"residual {residualCount} (width {proceduralWidth}, missing {missing}, unsupported {unsupported}) · {detail}";
    }

    private static void AddResidual(
        IEnbRenderObject obj,
        EnbPresentationBaseAssetReference? reference,
        EnbPresentationBaseAssetKind kind,
        Dictionary<int, LiveRenderResidualEntry> residuals)
    {
        if (!residuals.TryGetValue(obj.BaseAsset, out LiveRenderResidualEntry? entry))
        {
            entry = new LiveRenderResidualEntry(
                obj.BaseAsset,
                kind,
                reference?.Width,
                []);
            residuals.Add(obj.BaseAsset, entry);
        }

        entry.Count++;
        entry.ObjectTypes.Add(obj.ObjectType);
    }

    private sealed class LiveRenderResidualEntry(
        int baseAsset,
        EnbPresentationBaseAssetKind kind,
        float? width,
        HashSet<byte> objectTypes)
    {
        public int Count { get; set; }

        public HashSet<byte> ObjectTypes { get; } = objectTypes;

        public string Describe()
        {
            string widthSuffix = width is float value ? $" width={value:0.###}" : string.Empty;
            string types = string.Join(",", ObjectTypes.Order().Select(static type => $"0x{type:X2}"));
            return $"BASE-{baseAsset}Ã—{Count} {kind}{widthSuffix} types={types}";
        }
    }

    private double CaptureCameraFrameElapsedMilliseconds()
    {
        long now = Stopwatch.GetTimestamp();
        long previous = _lastCameraFrameTimestamp;
        _lastCameraFrameTimestamp = now;
        if (previous == 0 || now <= previous)
            return 0.0;

        return (now - previous) * 1000.0 / Stopwatch.Frequency;
    }

    private void EnsureCameraController(EnbSceneCameraStateSnapshot cameraState)
    {
        var key = new CameraStartupKey(
            cameraState.SessionOverrideSelector,
            cameraState.PlayerOptionsSelector,
            cameraState.ModeSourceProvenance?.Path,
            cameraState.PlayerOptionsSourceProvenance?.Path,
            cameraState.StartInFirstPerson);

        if (_cameraStartupKey == key)
            return;

        // A snapshot reflecting our own retail Bridge_Mode write must not discard
        // the remembered third-person slot or the live controller's saved modes.
        if (_savedCameraStartupKey == key)
        { _cameraStartupKey = key; _savedCameraStartupKey = null; return; }
        _savedCameraStartupKey = null;

        if (_cameraStartupKey is {} previous && key.ModeSourcePath == _savedCameraPath &&
            previous with { ModeSourcePath = key.ModeSourcePath } == key)
        { _cameraStartupKey = key; return; }

        _cameraController = EnbNormalCameraController.FromRetailStartup(cameraState);
        _cameraStartupKey = key;
    }

    private void ShowEmpty(string message)
    {
        _ui.Inventory.PauseGroupFrame();
        _ui.Cockpit.PauseBuffFrame();
        _scannerCameraState.Clear();
        _bridgeBound = false;
        GutterVisible = false;
        _cameraController.ResetMotion();
        _shipVisualBank.Clear();
        if (!_awaitingWorld) BeginPresentationWait();
        _w3dWorld.ClearFrame();
        _targetModelView.ClearFrame();
        _targetPreviewKey = null;
        _targetPresentationToken = 0;
        _ui.Gutter.Clear();
        EmptyText = message;
        EmptyVisible = _presentationAssetFailed;
        UpdateLoadingScreen();
        Volatile.Write(ref _diagnosticsText, "LIVE WORLD VIEW · R6Q\n" + message);
    }

    private readonly EnbSceneFramePool _targetPreviewScenes=new();

    private void UpdateTargetModel(
        IEnbSceneFrame snapshot,
        uint currentClientTime,
        double renderScale,
        LiveCompletedRenderQueries renderedObjectBounds,
        LivePlanetGpuPresentation planetGpuPresentation)
    {
        IEnbRenderObject? target = _ui.Scanner.SelectedTarget;
        if(target is null || !snapshot.Objects.Any(obj=>obj.RenderIdentity==target.RenderIdentity))
            _scannerCameraState.Clear();
        if (!_ui.Scanner.ShowTargetModel)
        {
            ClearTargetPreview();
            return;
        }
        if (target is not null)
            target = snapshot.Objects.FirstOrDefault(obj => obj.RenderIdentity == target.RenderIdentity);
        Rect bounds = _ui.Scanner.TargetModelBounds;
        if (target is not null && IsRenderablePlanet(target))
        {
            var targetPlanetIdentity = new LivePlanetGpuIdentity(
                target.RenderIdentity,
                target.VisualGeneration);
            bool matchingMainOwnership = planetGpuPresentation.Owns(target);
            bool mapperEpochReady =
                planetGpuPresentation.MapperStartMillisecondsByRenderIdentity.ContainsKey(targetPlanetIdentity);
            bool runtimeSemanticsReady = snapshot.Environment?.PresentationMode switch
            {
                0 => planetGpuPresentation.DayNightPhasesByRenderIdentity.ContainsKey(targetPlanetIdentity),
                1 or 5 => true,
                _ => false,
            };
            if (!matchingMainOwnership || !mapperEpochReady || !runtimeSemanticsReady)
            {
                // Retail's scanner renders the same RenderObj as MainView. Do
                // not seed a new VisualGeneration from maps published for an
                // older generation or make mapper lifetime depend on MainView
                // frustum survival. Modes 1/5 do not use PLT_DN; mode 0 also
                // waits for the independently refreshed day/night phase.
                _targetModelView.ClearFrame();
                return;
            }
        }
        ImmutableDictionary<LivePlanetGpuIdentity, float> dayNightPhaseOverrides =
            planetGpuPresentation.DayNightPhasesByRenderIdentity;
        ImmutableDictionary<LivePlanetGpuIdentity, uint> mapperStartMillisecondsOverrides =
            planetGpuPresentation.MapperStartMillisecondsByRenderIdentity;
        if (target?.Spatial?.Transform is not { } transform ||
            !IsFinite(transform.Position) ||
            !IsFinite(transform.Orientation) ||
            bounds.Width < 2 || bounds.Height < 2 ||
            !EnbRenderViewport.TryFromLogicalSize(
                bounds.Width,
                bounds.Height,
                renderScale,
                out EnbRenderViewport viewport))
        {
            _targetModelView.ClearFrame();
            _targetPreviewKey = null;
            _targetPresentationToken = 0;
            return;
        }

        // Active retail scanner: 005BEA00 constructs 005C2CD0, whose proxy
        // receives the target's current world transform at 005C2F00. No root
        // replacement, model-axis special case, or forced scale belongs here.
        Vector3 center = transform.Position;
        float radius = EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(target);
        if (renderedObjectBounds.RenderSpheresByRenderIdentity.TryGetValue(
                target.RenderIdentity, out EnbW3dWorldBounds renderSphere) ||
            _targetModelView.ProjectedObjectBounds.RenderSpheresByRenderIdentity.TryGetValue(
                target.RenderIdentity, out renderSphere))
        {
            center = renderSphere.Center;
            radius = renderSphere.Radius;
        }
        Vector3 observer = snapshot.Objects.FirstOrDefault(obj => obj.IsLocalShip)
            ?.Spatial?.Transform?.Position ?? transform.Position;
        // Modern UI's small square portrait frames the model a little tighter than the retail scanner panel.
        if (_ui.ModernUi) radius *= .85f;
        if (!EnbCurrentRetailScannerCameraRuntime.TryCreate(observer, transform.Position,
                center, radius, viewport, _ui.Scanner.ScannerAuthoredAspectRatio,
                out EnbResolvedRenderCamera camera,_scannerCameraState))
        {
            ClearTargetPreview();
            return;
        }
        // The scanner owns a model proxy, not a second world effect scene.
        // RenderScanner supplies the world's already-applied RSL object state.
        ImmutableArray<EnbSceneEffectSnapshot> targetEffects = [];
        // 005C2000 initializes a .2-ambient scene and diffuse-only point light;
        // 005C2920 puts the light at the scanner camera (no half-radius offset).
        var scannerLight = new EnbSceneLightSnapshot(
            0, 0, Vector3.One, Vector3.Zero, Vector3.Zero, camera.Position,
            camera.Basis.Forward, 1_000_000f, 1f, 1f, 0f, 0f,
            MathF.PI * 0.25f, MathF.PI * 0.25f);
        var _targetPreviewScene=_targetPreviewScenes.Prepare(snapshot);
        _targetPreviewScene.SetSingleObject(target);
        _targetPreviewScene.Effects=targetEffects;
        _targetPreviewScene.Lighting=new(0x00333333u,ImmutableArray.Create(scannerLight));

        _targetPreviewScene.StationAvatars=[];
        var preview=_targetPreviewScene;
        var key = new TargetPreviewKey(
            target.RenderIdentity,
            target.VisualGeneration,
            ComputeTargetPreviewStateFingerprint(target, targetEffects),
            BitConverter.SingleToInt32Bits(radius),
            BitConverter.SingleToInt32Bits(center.X),
            BitConverter.SingleToInt32Bits(center.Y),
            BitConverter.SingleToInt32Bits(center.Z),
            viewport.PixelWidth,
            viewport.PixelHeight);
        bool stagePresentation = _targetPreviewKey is not TargetPreviewKey previousKey ||
            previousKey.RenderIdentity != key.RenderIdentity ||
            previousKey.VisualGeneration != key.VisualGeneration ||
            previousKey.RadiusBits != key.RadiusBits ||
            previousKey.CenterXBits != key.CenterXBits ||
            previousKey.CenterYBits != key.CenterYBits ||
            previousKey.CenterZBits != key.CenterZBits ||
            previousKey.PixelWidth != key.PixelWidth ||
            previousKey.PixelHeight != key.PixelHeight;
        _targetPreviewKey = key;
        if (stagePresentation)
        {
            _targetPresentationToken = NextTargetPresentationToken();
            _ui.Scanner.SetTargetModelReady(false);
        }
        _targetModelView.UpdateFrame(
            preview,
            camera,
            isFirstPerson: false,
            currentClientTime,
            backdrop: null,
            presentationToken: _targetPresentationToken,
            dayNightPhaseOverrides: dayNightPhaseOverrides,
            mapperStartMillisecondsOverrides: mapperStartMillisecondsOverrides);
    }

    private long NextTargetPresentationToken()
    {
        _nextTargetPresentationToken = _nextTargetPresentationToken == long.MaxValue
            ? 1
            : _nextTargetPresentationToken + 1;
        return _nextTargetPresentationToken;
    }

    private void ClearTargetPreview()
    {
        _targetModelView.ClearFrame();
        _targetPreviewKey = null;
        _targetPresentationToken = 0;
    }

    internal void QueueResizeRefresh()
    {
        if (_stopped) return;
        // The exact-viewport GL guard already rejects a stale scene frame.
        // Do not close Options for a resize: Graphics previews and rolls back display
        // changes, and every options page re-lays out at the new size.
        if (!_ui.Options.KeepsOpenThroughResize) BeginPresentationWait();
        if (_resizeRefreshQueued)
            return;
        _resizeRefreshQueued = true;
        _host.PostRefresh(
            () =>
            {
                _resizeRefreshQueued = false;
                RefreshWorld();
            });
    }

    private void BeginPresentationWait()
    {
        HudVisible = true;
        _warpCamera.Reset();
        _ui.Cockpit.Suspend();
        _ui.Options.Suspend();
        _ui.Inventory.Suspend();
        _ui.Map.Suspend();
        _ui.GalaxyMap.Suspend();
        FlightInputSuspended?.Invoke();
        _worldPresentationToken++;
        _loadingStarted = Stopwatch.GetTimestamp();
        _awaitingWorld = true;
        LoadingVisible = true;
        _ui.Gutter.ClearPointer();
        UpdateLoadingScreen();
    }

    private void UpdateLoadingScreen()
    {
        if (!_awaitingWorld) return;
        _host.UpdatePerformance(false,_w3dWorld.Performance);
        if(_presentationAssetFailed){LoadingVisible=false;_hudGpu.SetLoadingPresentation(null);return;}
        var presentation = _loadingScreen.Build(_retailAssetsAccessor(),Bounds.Size,
            _host.Scale, _locationTransition,
            (long)Stopwatch.GetElapsedTime(_loadingStarted).TotalMilliseconds);
        LoadingText = _loadingScreen.Error is { } error ? "Loading artwork unavailable: " + error : "Loadingâ€¦";
        // A missing asset must not expose old verb/tooltip pixels from the HUD GPU layer.
        LoadingZIndex = presentation is null ? 1 : 0;
        _hudGpu.SetLoadingPresentation(presentation);
    }

    private static int ComputeTargetPreviewStateFingerprint(
        IEnbRenderObject target,
        ImmutableArray<EnbSceneEffectSnapshot> effects)
    {
        var hash = new HashCode();
        foreach (uint state in target.ActiveRenderStates)
            hash.Add(state);
        foreach (EnbSceneRenderStateActionSnapshot action in target.RenderStateActions)
            hash.Add(action);
        foreach (EnbSceneEffectSnapshot effect in effects)
        {
            hash.Add(effect.EffectId);
            hash.Add(effect.InstanceGeneration);
            hash.Add(effect.DefinitionId);
            hash.Add(effect.Timestamp);
        }
        return hash.ToHashCode();
    }

    private static EnvironmentAssetStatus DescribeEnvironment(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources)
    {
        if (snapshot.Environment is not { } environment)
            return new EnvironmentAssetStatus("env <pending>", "sky: waiting for server environment selector");

        short selector = environment.BaseAsset;
        EnbPresentationBaseAssetReference? presentation = environment.BaseAssetReference;
        if (presentation is null)
        {
            return new EnvironmentAssetStatus(
                $"env BaseAsset {selector} (unresolved)",
                "sky: immutable presentation snapshot has no resolved retail BaseAsset reference");
        }

        string summary = $"env BaseAsset {selector} ({presentation.Kind})";
        if (presentation.Kind != EnbPresentationBaseAssetKind.FileName ||
            string.IsNullOrWhiteSpace(presentation.File))
        {
            return new EnvironmentAssetStatus(
                summary,
                $"sky: BaseAsset {selector} is not a FILE/NAME W3D definition");
        }

        string resourceName = presentation.File.Trim();
        summary += $" · {resourceName}";
        if (resources is null)
        {
            return new EnvironmentAssetStatus(
                summary,
                $"sky asset: {resourceName} · MIX1 art catalog unavailable");
        }

        if (!resources.TryLocateResource(resourceName, out EnbMixResourceLocation location))
        {
            return new EnvironmentAssetStatus(
                summary,
                $"sky asset: {resourceName} · not found in {resources.ArchiveCount} indexed art archive(s)");
        }

        return new EnvironmentAssetStatus(
            summary,
            $"sky asset: {location.Name} · {location.ArchiveName} · {location.DataLength:N0} bytes · raw W3D located, decode next");
    }


    private bool TryDrawNativeEnvironment(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources,
        EnbRetailAssetBundle? retailAssets)
    {
        LiveInfluenceSkyPresentation influence = _w3dWorld.InfluenceSkyPresentation;
        bool influenceCurrent = IsCurrentInfluencePresentation(snapshot, influence);
        string? influenceFailure = null;
        if (influenceCurrent)
        {
            if (TryDrawInfluenceEnvironment(
                    retailAssets,
                    influence))
            {
                return true;
            }

            influenceFailure = _environmentRenderStatus;
        }

        bool ordinary = TryDrawOrdinaryEnvironment(snapshot, resources);
        if (influenceFailure is not null && ordinary)
        {
            _environmentRenderStatus =
                $"{influenceFailure} · ordinary sector backdrop retained fail-closed · {_environmentRenderer?.Summary ?? "renderer unavailable"}";
        }

        return ordinary;
    }

    private bool TryDrawInfluenceEnvironment(
        EnbRetailAssetBundle? retailAssets,
        LiveInfluenceSkyPresentation influence)
    {
        if (_environmentRenderer is not null &&
            _environmentRendererIsInfluence &&
            ReferenceEquals(_environmentRendererAssetBundle, retailAssets) &&
            Nullable.Equals(_environmentRendererInfluenceColor, influence.SkyBoxColor))
        {
            _environmentRenderStatus = DescribeInfluenceRenderer(influence);
            return true;
        }

        if (!TryCreateInfluenceEnvironmentRenderer(
                retailAssets,
                influence.SkyBoxColor,
                out LiveWorldEnvironmentRenderer renderer,
                out string resourceName,
                out string? diagnostic))
        {
            _environmentRenderStatus =
                $"sky renderer: R7B1 influence override rejected · {diagnostic ?? "unknown authored-environment rejection"}";
            return false;
        }

        ReplaceEnvironmentRenderer(
            renderer,
            resourceName,
            influenceSky: true,
            retailAssets,
            influence.SkyBoxColor);
        _environmentRenderStatus = DescribeInfluenceRenderer(influence);
        return true;
    }

    private bool TryDrawOrdinaryEnvironment(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources)
    {
        _environmentRenderer?.Dispose();
        _environmentRenderer = null;
        _environmentRenderStatus = "sky renderer: shared scene uses authored GPU W3D materials";
        return false;
    }

    private void ReplaceEnvironmentRenderer(
        LiveWorldEnvironmentRenderer renderer,
        string resourceName,
        bool influenceSky,
        EnbRetailAssetBundle? assetBundle,
        Vector3? influenceSkyColor)
    {
        _environmentRenderer?.Dispose();
        _environmentRenderer = renderer;
        _environmentRendererResource = resourceName;
        _environmentRendererIsInfluence = influenceSky;
        _environmentRendererAssetBundle = assetBundle;
        _environmentRendererInfluenceColor = influenceSkyColor;
    }

    private string DescribeInfluenceRenderer(LiveInfluenceSkyPresentation influence)
    {
        string objectId = influence.GameId is uint gameId ? $"0x{gameId:X8}" : "<none>";
        return
            $"sky renderer: {_environmentRenderer!.Summary} · BaseAsset {EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset} via installed basset.ini · " +
            $"region {influence.Region} · owner {objectId}";
    }

    private static bool IsCurrentInfluencePresentation(
        IEnbSceneFrame snapshot,
        LiveInfluenceSkyPresentation influence)
        => influence.OverrideActive &&
           influence.SnapshotVersion == snapshot.Version &&
           influence.SectorNum == snapshot.Environment?.SectorNum &&
           influence.Region is EnbCurrentRetailInfluenceNebulaRegion.Between or EnbCurrentRetailInfluenceNebulaRegion.Room;

    private void PreflightInfluenceSkyPresentation(EnbRetailAssetBundle? retailAssets)
    {
        if (retailAssets is null)
            return;

        if (_influenceSkyPresentationPreflightAttempted &&
            ReferenceEquals(_influenceSkyPresentationPreflightAssets, retailAssets))
        {
            return;
        }

        _influenceSkyPresentationPreflightAttempted = true;
        _influenceSkyPresentationPreflightAssets = retailAssets;
        const int selector = EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset;
        Vector3? skyBoxColor = retailAssets.NebulaEnvironment?.Between.SkyBoxColor;

        if (!TryCreateInfluenceEnvironmentRenderer(
                retailAssets,
                skyBoxColor,
                out LiveWorldEnvironmentRenderer renderer,
                out string resourceName,
                out string? diagnostic))
        {
            _influenceSkyPresentationStatus =
                $"influence sky renderer: R7B1_CURRENT_RETAIL_INFLUENCE_SKY_PRESENTATION_READY · reject · {diagnostic ?? "unknown authored-environment rejection"}";
            return;
        }

        renderer.Dispose();
        _influenceSkyPresentationStatus =
            $"influence sky renderer: R7B1_CURRENT_RETAIL_INFLUENCE_SKY_PRESENTATION_READY · " +
            $"BaseAsset {selector} via installed basset.ini -> {resourceName} · " +
            "authored W3D + exact resolved two-stage DXT1 + material/shader preflight green · passive ready";
    }

    private static bool TryCreateInfluenceEnvironmentRenderer(
        EnbRetailAssetBundle? retailAssets,
        Vector3? skyBoxColor,
        out LiveWorldEnvironmentRenderer renderer,
        out string resourceName,
        out string? diagnostic)
    {
        renderer = default!;
        resourceName = string.Empty;
        diagnostic = null;
        const int selector = EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset;

        if (retailAssets?.BaseAssetResolver is not EnbRetailBaseAssetResolver resolver ||
            retailAssets.MixResources is not EnbMixResourceCatalog resources)
        {
            diagnostic =
                $"BaseAsset {selector} cannot be materialized because installed basset.ini/MIX resources are unavailable";
            return false;
        }

        EnbBaseAssetResolution resolution = resolver.Resolve(selector);
        if (resolution.Status != EnbBaseAssetResolutionStatus.W3d ||
            resolution.W3d is not EnbResolvedW3dResource w3d)
        {
            diagnostic =
                $"BaseAsset {selector} did not resolve through installed basset.ini as FILE/W3D ({resolution.Status}) · {resolution.Diagnostic ?? "[none]"}";
            return false;
        }

        if (!resources.TryReadResource(w3d.ResourceLocation, out byte[] w3dBytes) ||
            !EnbW3dEnvironmentBackdrop.TryDecode(w3dBytes, out EnbW3dEnvironmentBackdrop backdrop))
        {
            diagnostic =
                $"BaseAsset {selector} resource {w3d.ResourceLocation.Name} is not the bounded authored environment-backdrop form";
            return false;
        }

        if (!TryReadResolvedInfluenceTexture(
                resources,
                w3d,
                backdrop.Stage0TextureName,
                out EnbDdsTexture stage0Texture,
                out string stage0ResolvedName,
                out string? stage0Diagnostic))
        {
            diagnostic =
                $"BaseAsset {selector} authored stage-0 texture resolution failed · {stage0Diagnostic ?? "unknown texture rejection"}";
            return false;
        }

        if (!TryReadResolvedInfluenceTexture(
                resources,
                w3d,
                backdrop.Stage1TextureName,
                out EnbDdsTexture stage1Texture,
                out string stage1ResolvedName,
                out string? stage1Diagnostic))
        {
            diagnostic =
                $"BaseAsset {selector} authored stage-1 texture resolution failed · {stage1Diagnostic ?? "unknown texture rejection"}";
            return false;
        }

        if (!LiveWorldEnvironmentRenderer.TryCreate(
                backdrop,
                stage0Texture,
                stage1Texture,
                stage0ResolvedName,
                stage1ResolvedName,
                influenceSky: true,
                skyBoxColor,
                out renderer,
                out diagnostic))
        {
            diagnostic = $"authored BaseAsset {selector} renderer rejected ({diagnostic})";
            return false;
        }

        resourceName = w3d.ResourceLocation.Name;
        return true;
    }

    private static bool TryReadResolvedInfluenceTexture(
        EnbMixResourceCatalog resources,
        EnbResolvedW3dResource w3d,
        string authoredTextureName,
        out EnbDdsTexture texture,
        out string resolvedName,
        out string? diagnostic)
    {
        texture = default!;
        resolvedName = string.Empty;
        diagnostic = null;

        EnbResolvedTextureReference[] matches = w3d.Textures
            .Where(reference => string.Equals(
                reference.LogicalName,
                authoredTextureName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length != 1 || matches[0].Location is not EnbMixResourceLocation location)
        {
            diagnostic = matches.Length == 0
                ? $"authored texture '{authoredTextureName}' was not resolved with BaseAsset {w3d.LogicalFileName}"
                : $"authored texture '{authoredTextureName}' has {matches.Length} resolver entries instead of one";
            return false;
        }

        if (!resources.TryReadResource(location, out byte[] textureBytes) ||
            !EnbDdsTexture.TryDecodeDxt1(textureBytes, out texture))
        {
            diagnostic =
                $"authored texture '{authoredTextureName}' resolved to {location.ArchiveName}/{location.Name} but is unavailable or not DXT1";
            return false;
        }

        resolvedName = location.Name;
        return true;
    }


    private bool TryDrawRetailStarField(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources,
        EnbRetailAssetBundle? retailAssets)
    {
        if (resources is null || retailAssets?.Stars is null || snapshot.Environment is not { } environment)
        {
            _starRenderStatus = "star renderer: waiting for current retail stars.ini and sector state";
            return false;
        }

        int starSetId = unchecked((int)environment.SectorNum);
        if (_starFieldRenderer is null ||
            _starFieldSetId != starSetId ||
            !ReferenceEquals(_starFieldResources, resources) ||
            !ReferenceEquals(_starFieldAssets, retailAssets))
        {
            _starFieldRenderer?.Dispose();
            _starFieldRenderer = null;
            _starFieldSetId = starSetId;
            _starFieldResources = resources;
            _starFieldAssets = retailAssets;

            EnbRetailStarLayerSelection selection = retailAssets.Stars.ResolveLayers(starSetId);
            int starBudget = EnbNextRenderSettings.Current
                .ClampLikeRetail()
                .StarBudget;
            if (!LiveWorldStarFieldRenderer.TryCreate(
                    retailAssets,
                    selection,
                    starBudget,
                    out LiveWorldStarFieldRenderer renderer,
                    out string? diagnostic))
            {
                _starRenderStatus = $"star renderer: set {starSetId} unresolved ({diagnostic})";
                return false;
            }

            _starFieldRenderer = renderer;
            _starRenderStatus = $"star renderer: {renderer.Summary}";
        }

        return _starFieldRenderer is not null;
    }

    private void HideStarField()
    {
        // The star layer no longer owns a native overlay. Absence is represented by
        // omitting star geometry from the shared OpenGL backdrop frame.
    }


    private void CaptureStarFieldOnce(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources,
        EnbRetailAssetBundle? retailAssets)
    {
        if (!EnbDiagnosticFiles.Enabled ||
            resources is null ||
            retailAssets?.Stars is null ||
            snapshot.Environment is not { } environment)
        {
            return;
        }

        int starSetId = unchecked((int)environment.SectorNum);
        if (_capturedStarSetId == starSetId)
            return;

        try
        {
            if (!retailAssets.TryGetSource(EnbRetailAssetSourceKind.StarsIni, out EnbRetailAssetSource starsSource) ||
                !starsSource.Exists)
            {
                _starCaptureStatus = $"star capture: stars.ini unavailable for star-set {starSetId}";
                return;
            }

            EnbRetailStarLayerSelection selection = retailAssets.Stars.ResolveLayers(starSetId);
            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            Directory.CreateDirectory(outputDirectory);

            string prefix = $"stars-{starSetId:D4}-";
            var capturedFiles = new List<string>();
            string starsIniCopy = System.IO.Path.Combine(outputDirectory, prefix + "stars.ini");
            File.Copy(starsSource.Path, starsIniCopy, overwrite: true);
            capturedFiles.Add(starsIniCopy);

            byte[] starsIniBytes = File.ReadAllBytes(EnbNext.Installation.EnbPathCase.Resolve(starsSource.Path));
            string starsIniHash = Convert.ToHexString(SHA256.HashData(starsIniBytes)).ToLowerInvariant();
            string starsIniHashPath = starsIniCopy + ".sha256.txt";
            File.WriteAllText(
                starsIniHashPath,
                $"{starsIniHash}  {System.IO.Path.GetFileName(starsIniCopy)}{Environment.NewLine}");
            capturedFiles.Add(starsIniHashPath);

            var manifestLines = new List<string>
            {
                $"sector={environment.SectorNum}",
                $"starSetId={starSetId}",
                $"family={selection.Family}",
                $"selectedStarSetId={(selection.SelectedStarSetId?.ToString() ?? "<default>")}",
                $"layerCount={selection.Layers.Count}",
                $"totalConfiguredRenderCost={selection.TotalConfiguredRenderCost}",
                $"starsIni={starsSource.Path}",
                $"starsIniSha256={starsIniHash}",
            };

            int capturedTextures = 0;
            for (int index = 0; index < selection.Layers.Count; index++)
            {
                EnbRetailStarLayerDefinition layer = selection.Layers[index];
                string layerPrefix = $"layer{index:D2}";
                manifestLines.Add(
                    $"{layerPrefix}=section:{layer.SectionName};texture:{layer.Texture ?? "<none>"};" +
                    $"textureLog2Width:{layer.TextureLog2Width};quads:{layer.Quads};numStars:{layer.NumStars};" +
                    $"minSize:{layer.MinSize:R};maxSize:{layer.MaxSize:R};qmcOffset:{layer.QmcOffset};" +
                    $"pickRandomly:{layer.PickRandomly};hemisphere:{layer.Hemisphere};renderCost:{layer.RetailRenderCost}");

                if (string.IsNullOrWhiteSpace(layer.Texture) ||
                    !resources.TryReadResource(layer.Texture, out byte[] textureBytes, out EnbMixResourceLocation textureLocation))
                {
                    manifestLines.Add($"{layerPrefix}.textureResolved=<unavailable>");
                    continue;
                }

                string textureFileName = System.IO.Path.GetFileName(textureLocation.Name);
                if (string.IsNullOrWhiteSpace(textureFileName))
                {
                    manifestLines.Add($"{layerPrefix}.textureResolved=<unnamed>");
                    continue;
                }

                string capturedTextureName = $"{layerPrefix}-{textureFileName}";
                string texturePath = System.IO.Path.Combine(outputDirectory, prefix + capturedTextureName);
                File.WriteAllBytes(texturePath, textureBytes);
                capturedFiles.Add(texturePath);

                string textureHash = Convert.ToHexString(SHA256.HashData(textureBytes)).ToLowerInvariant();
                string textureHashPath = texturePath + ".sha256.txt";
                File.WriteAllText(
                    textureHashPath,
                    $"{textureHash}  {System.IO.Path.GetFileName(texturePath)}{Environment.NewLine}");
                capturedFiles.Add(textureHashPath);
                capturedTextures++;

                manifestLines.Add($"{layerPrefix}.textureResolved={textureLocation.Name}");
                manifestLines.Add($"{layerPrefix}.textureArchive={textureLocation.ArchiveName}");
                manifestLines.Add($"{layerPrefix}.textureSha256={textureHash}");
            }

            string manifestPath = System.IO.Path.Combine(outputDirectory, prefix + "manifest.txt");
            File.WriteAllText(manifestPath, string.Join(Environment.NewLine, manifestLines) + Environment.NewLine);
            capturedFiles.Add(manifestPath);

            string zipPath = System.IO.Path.Combine(outputDirectory, prefix + "research-bundle.zip");
            if (EnbNext.Installation.EnbPathCase.FileExists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string capturedFile in capturedFiles)
                    archive.CreateEntryFromFile(capturedFile, System.IO.Path.GetFileName(capturedFile), CompressionLevel.Optimal);
            }

            _capturedStarSetId = starSetId;
            string selectedSuffix = selection.SelectedStarSetId is int selected
                ? $" {selected}"
                : string.Empty;
            _starCaptureStatus =
                $"star capture: {zipPath} · set {starSetId} â†’ {selection.Family}{selectedSuffix} · " +
                $"layers {selection.Layers.Count} · textures {capturedTextures}/{selection.Layers.Count}";
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            _starCaptureStatus =
                $"star capture: failed for star-set {starSetId} · {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void CaptureEnvironmentAssetOnce(
        IEnbSceneFrame snapshot,
        EnbMixResourceCatalog? resources)
    {
        if (!EnbDiagnosticFiles.Enabled ||
            resources is null ||
            snapshot.Environment?.BaseAssetReference is not { Kind: EnbPresentationBaseAssetKind.FileName } presentation ||
            string.IsNullOrWhiteSpace(presentation.File))
        {
            return;
        }

        string resourceName = presentation.File.Trim();
        if (string.Equals(_capturedEnvironmentResource, resourceName, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            if (!resources.TryReadResource(resourceName, out byte[] bytes, out EnbMixResourceLocation location))
            {
                _environmentCaptureStatus = $"environment capture: {resourceName} could not be read from MIX1";
                return;
            }

            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            Directory.CreateDirectory(outputDirectory);

            string fileName = System.IO.Path.GetFileName(location.Name);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "environment.w3d";

            string safeStem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            if (string.IsNullOrWhiteSpace(safeStem))
                safeStem = "environment";
            string prefix = $"environment-{safeStem}-";
            var capturedFiles = new List<string>();

            string outputPath = System.IO.Path.Combine(outputDirectory, prefix + fileName);
            File.WriteAllBytes(outputPath, bytes);
            capturedFiles.Add(outputPath);

            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string hashPath = outputPath + ".sha256.txt";
            File.WriteAllText(hashPath, $"{hash}  {System.IO.Path.GetFileName(outputPath)}{Environment.NewLine}");
            capturedFiles.Add(hashPath);

            int capturedTextures = 0;
            string probeSummary = "W3D structure unavailable";
            string textureList = "";
            int vertexCount = 0;
            int triangleCount = 0;
            float boundingRadius = 0f;
            if (EnbW3dProbe.TryInspect(bytes, out EnbW3dProbeResult probe))
            {
                foreach (string textureName in probe.TextureNames)
                {
                    if (!resources.TryReadResource(textureName, out byte[] textureBytes, out EnbMixResourceLocation textureLocation))
                        continue;

                    string textureFileName = System.IO.Path.GetFileName(textureLocation.Name);
                    if (string.IsNullOrWhiteSpace(textureFileName))
                        continue;

                    string texturePath = System.IO.Path.Combine(outputDirectory, prefix + textureFileName);
                    File.WriteAllBytes(texturePath, textureBytes);
                    capturedFiles.Add(texturePath);
                    string textureHash = Convert.ToHexString(SHA256.HashData(textureBytes)).ToLowerInvariant();
                    string textureHashPath = texturePath + ".sha256.txt";
                    File.WriteAllText(textureHashPath, $"{textureHash}  {System.IO.Path.GetFileName(texturePath)}{Environment.NewLine}");
                    capturedFiles.Add(textureHashPath);
                    capturedTextures++;
                }

                textureList = probe.TextureNames.Count == 0
                    ? "no texture refs"
                    : string.Join(" + ", probe.TextureNames);
                vertexCount = probe.VertexCount;
                triangleCount = probe.TriangleCount;
                boundingRadius = probe.BoundingRadius;
                probeSummary =
                    $"{probe.MeshName} · {probe.VertexCount}v/{probe.TriangleCount}t · {textureList} · captured {capturedTextures}/{probe.TextureNames.Count}";
            }

            string manifestPath = System.IO.Path.Combine(outputDirectory, prefix + "manifest.txt");
            File.WriteAllText(
                manifestPath,
                $"baseAsset={snapshot.Environment.BaseAsset}{Environment.NewLine}" +
                $"resource={resourceName}{Environment.NewLine}" +
                $"resolved={location.Name}{Environment.NewLine}" +
                $"archive={location.ArchiveName}{Environment.NewLine}" +
                $"w3dSha256={hash}{Environment.NewLine}" +
                $"mesh={probeSummary}{Environment.NewLine}" +
                $"vertexCount={vertexCount}{Environment.NewLine}" +
                $"triangleCount={triangleCount}{Environment.NewLine}" +
                $"boundingRadius={boundingRadius:R}{Environment.NewLine}" +
                $"textureRefs={textureList}{Environment.NewLine}" +
                $"texturesCaptured={capturedTextures}{Environment.NewLine}");
            capturedFiles.Add(manifestPath);

            string zipPath = System.IO.Path.Combine(outputDirectory, prefix + "research-bundle.zip");
            if (EnbNext.Installation.EnbPathCase.FileExists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string capturedFile in capturedFiles)
                    archive.CreateEntryFromFile(capturedFile, System.IO.Path.GetFileName(capturedFile), CompressionLevel.Optimal);
            }

            _capturedEnvironmentResource = resourceName;
            _environmentCaptureStatus =
                $"environment capture: {zipPath} · {resourceName} · W3D sha256 {hash} · {probeSummary}";
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            _environmentCaptureStatus =
                $"environment capture: failed for {resourceName} · {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void CaptureInfluenceSkyEvidenceOnce(EnbRetailAssetBundle? retailAssets)
    {
        if (!EnbDiagnosticFiles.Enabled ||
            _influenceSkyCaptureAttempted ||
            retailAssets?.BaseAssetResolver is not EnbRetailBaseAssetResolver resolver ||
            retailAssets.MixResources is not EnbMixResourceCatalog resources)
        {
            return;
        }

        _influenceSkyCaptureAttempted = true;
        const int baseAsset = EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset;

        try
        {
            EnbBaseAssetResolution resolution = resolver.Resolve(baseAsset);
            if (resolution.Status != EnbBaseAssetResolutionStatus.W3d || resolution.W3d is not EnbResolvedW3dResource w3d)
            {
                _influenceSkyCaptureStatus =
                    $"influence sky capture: R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE · " +
                    $"BaseAsset {baseAsset} did not resolve as FILE/W3D ({resolution.Status}) · {resolution.Diagnostic ?? "[none]"}";
                return;
            }

            if (!resources.TryReadResource(w3d.ResourceLocation, out byte[] w3dBytes))
            {
                _influenceSkyCaptureStatus =
                    $"influence sky capture: R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE · " +
                    $"BaseAsset {baseAsset} resource {w3d.ResourceLocation.Name} could not be read from {w3d.ResourceLocation.ArchiveName}";
                return;
            }

            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            Directory.CreateDirectory(outputDirectory);
            const string prefix = "influence-sky-base311-";
            var capturedFiles = new List<string>();

            string w3dFileName = System.IO.Path.GetFileName(w3d.ResourceLocation.Name);
            if (string.IsNullOrWhiteSpace(w3dFileName))
                w3dFileName = "BACK03.W3D";
            string w3dPath = System.IO.Path.Combine(outputDirectory, prefix + w3dFileName);
            File.WriteAllBytes(w3dPath, w3dBytes);
            capturedFiles.Add(w3dPath);

            string w3dHash = Convert.ToHexString(SHA256.HashData(w3dBytes)).ToLowerInvariant();
            string w3dHashPath = w3dPath + ".sha256.txt";
            File.WriteAllText(w3dHashPath, $"{w3dHash}  {System.IO.Path.GetFileName(w3dPath)}{Environment.NewLine}");
            capturedFiles.Add(w3dHashPath);

            var manifest = new System.Text.StringBuilder();
            manifest.AppendLine("marker=R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE");
            manifest.AppendLine($"baseAsset={baseAsset}");
            manifest.AppendLine($"definitionKind={resolution.Definition.Kind}");
            manifest.AppendLine($"definitionFile={resolution.Definition.File ?? ""}");
            manifest.AppendLine($"definitionName={resolution.Definition.Name ?? ""}");
            manifest.AppendLine($"logicalFile={w3d.LogicalFileName}");
            manifest.AppendLine($"requestedObject={w3d.RequestedObjectName}");
            manifest.AppendLine($"resolved={w3d.ResourceLocation.Name}");
            manifest.AppendLine($"archive={w3d.ResourceLocation.ArchiveName}");
            manifest.AppendLine($"w3dBytes={w3dBytes.Length}");
            manifest.AppendLine($"w3dSha256={w3dHash}");
            manifest.AppendLine($"meshes={w3d.Model.Meshes.Count}");
            manifest.AppendLine($"hierarchies={w3d.Model.Hierarchies.Count}");
            manifest.AppendLine($"animations={w3d.Model.Animations.Count}");
            manifest.AppendLine($"hierarchyModels={w3d.Model.HierarchyModels.Count}");
            manifest.AppendLine($"hlods={w3d.Model.HierarchicalLods.Count}");

            for (int index = 0; index < w3d.Model.Meshes.Count; index++)
            {
                EnbW3dMesh mesh = w3d.Model.Meshes[index];
                manifest.AppendLine(
                    $"mesh[{index}]={mesh.Name}|container={mesh.ContainerName}|vertices={mesh.Vertices.Count}|triangles={mesh.Triangles.Count}|" +
                    $"vertexMaterials={mesh.VertexMaterials.Count}|passes={mesh.MaterialPasses.Count}|textures={string.Join(",", mesh.TextureNames)}");
            }

            for (int index = 0; index < w3d.Model.HierarchicalLods.Count; index++)
            {
                EnbW3dHierarchicalLod hlod = w3d.Model.HierarchicalLods[index];
                manifest.AppendLine($"hlod[{index}]={hlod.Name}|hierarchy={hlod.HierarchyName}|levels={hlod.Levels.Count}");
                for (int levelIndex = 0; levelIndex < hlod.Levels.Count; levelIndex++)
                {
                    EnbW3dLodLevel level = hlod.Levels[levelIndex];
                    manifest.AppendLine(
                        $"hlod[{index}].level[{levelIndex}]=threshold={level.Threshold:R}|raw=0x{level.RawThreshold:X8}|" +
                        $"members={string.Join(",", level.Members.Select(static member => $"{member.ReferenceIndex}:{member.Name}"))}");
                }
            }

            int capturedTextures = 0;
            int missingTextures = 0;
            for (int index = 0; index < w3d.Textures.Count; index++)
            {
                EnbResolvedTextureReference texture = w3d.Textures[index];
                if (texture.Location is not EnbMixResourceLocation textureLocation ||
                    !resources.TryReadResource(textureLocation, out byte[] textureBytes))
                {
                    missingTextures++;
                    manifest.AppendLine($"texture[{index}]={texture.LogicalName}|missing");
                    continue;
                }

                string textureFileName = System.IO.Path.GetFileName(textureLocation.Name);
                if (string.IsNullOrWhiteSpace(textureFileName))
                    textureFileName = $"texture-{index:D2}.bin";
                string capturedTextureName = $"{prefix}texture-{index:D2}-{textureFileName}";
                string texturePath = System.IO.Path.Combine(outputDirectory, capturedTextureName);
                File.WriteAllBytes(texturePath, textureBytes);
                capturedFiles.Add(texturePath);

                string textureHash = Convert.ToHexString(SHA256.HashData(textureBytes)).ToLowerInvariant();
                string textureHashPath = texturePath + ".sha256.txt";
                File.WriteAllText(textureHashPath, $"{textureHash}  {System.IO.Path.GetFileName(texturePath)}{Environment.NewLine}");
                capturedFiles.Add(textureHashPath);
                capturedTextures++;
                manifest.AppendLine(
                    $"texture[{index}]={texture.LogicalName}|resolved={textureLocation.Name}|archive={textureLocation.ArchiveName}|" +
                    $"bytes={textureBytes.Length}|sha256={textureHash}|capture={capturedTextureName}");
            }

            manifest.AppendLine($"texturesCaptured={capturedTextures}");
            manifest.AppendLine($"texturesMissing={missingTextures}");
            string manifestPath = System.IO.Path.Combine(outputDirectory, prefix + "manifest.txt");
            File.WriteAllText(manifestPath, manifest.ToString());
            capturedFiles.Add(manifestPath);

            string zipPath = System.IO.Path.Combine(outputDirectory, "influence-sky-base311-research-bundle.zip");
            if (EnbNext.Installation.EnbPathCase.FileExists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string capturedFile in capturedFiles)
                    archive.CreateEntryFromFile(capturedFile, System.IO.Path.GetFileName(capturedFile), CompressionLevel.Optimal);
            }

            _influenceSkyCaptureStatus =
                $"influence sky capture: R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE · {zipPath} · " +
                $"BaseAsset {baseAsset} · {w3d.ResourceLocation.Name} · W3D sha256 {w3dHash} · " +
                $"mesh/hlod {w3d.Model.Meshes.Count}/{w3d.Model.HierarchicalLods.Count} · textures {capturedTextures}/{w3d.Textures.Count}";
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            System.Security.SecurityException)
        {
            _influenceSkyCaptureStatus =
                $"influence sky capture: R7B0_CURRENT_RETAIL_INFLUENCE_SKY_EVIDENCE_CAPTURE · " +
                $"failed · {ex.GetType().Name}: {ex.Message}";
        }
    }

    private void CapturePlanetAssetOnce(
        EnbMixResourceCatalog? resources,
        IEnbRenderObject obj,
        float worldRadius,
        string radiusSource)
    {
        if (!EnbDiagnosticFiles.Enabled ||
            resources is null ||
            obj.BaseAssetReference is not { Kind: EnbPresentationBaseAssetKind.FileName } presentation ||
            string.IsNullOrWhiteSpace(presentation.File))
        {
            return;
        }

        string resourceName = presentation.File.Trim();
        string captureKey = $"{obj.GameId:X8}:{resourceName}";
        if (_capturedPlanetResources.Contains(captureKey))
            return;

        try
        {
            if (!resources.TryReadResource(resourceName, out byte[] w3dBytes, out EnbMixResourceLocation w3dLocation) ||
                !EnbW3dProbe.TryInspect(w3dBytes, out EnbW3dProbeResult probe))
            {
                _planetCaptureStatus =
                    $"planet capture: {DescribeObject(obj)} · BaseAsset {obj.BaseAsset} · {resourceName} unavailable/unprobeable";
                return;
            }

            string outputDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EnbNext", "asset-captures");
            Directory.CreateDirectory(outputDirectory);
            string safeResourceName = System.IO.Path.GetFileName(w3dLocation.Name);
            string prefix = $"planet-0x{obj.GameId:X8}-";
            string w3dPath = System.IO.Path.Combine(outputDirectory, prefix + safeResourceName);
            var capturedFiles = new List<string>();
            File.WriteAllBytes(w3dPath, w3dBytes);
            capturedFiles.Add(w3dPath);
            string w3dHash = Convert.ToHexString(SHA256.HashData(w3dBytes)).ToLowerInvariant();
            string w3dHashPath = w3dPath + ".sha256.txt";
            File.WriteAllText(w3dHashPath, $"{w3dHash}  {System.IO.Path.GetFileName(w3dPath)}{Environment.NewLine}");
            capturedFiles.Add(w3dHashPath);

            int capturedTextures = 0;
            foreach (string textureName in probe.TextureNames)
            {
                if (!resources.TryReadResource(textureName, out byte[] textureBytes, out EnbMixResourceLocation textureLocation))
                    continue;

                string textureFileName = System.IO.Path.GetFileName(textureLocation.Name);
                if (string.IsNullOrWhiteSpace(textureFileName))
                    continue;

                string texturePath = System.IO.Path.Combine(outputDirectory, prefix + textureFileName);
                File.WriteAllBytes(texturePath, textureBytes);
                capturedFiles.Add(texturePath);
                string textureHash = Convert.ToHexString(SHA256.HashData(textureBytes)).ToLowerInvariant();
                string textureHashPath = texturePath + ".sha256.txt";
                File.WriteAllText(textureHashPath, $"{textureHash}  {System.IO.Path.GetFileName(texturePath)}{Environment.NewLine}");
                capturedFiles.Add(textureHashPath);
                capturedTextures++;
            }

            string manifestPath = System.IO.Path.Combine(outputDirectory, prefix + "manifest.txt");
            File.WriteAllText(
                manifestPath,
                $"resource={resourceName}{Environment.NewLine}" +
                $"resolved={w3dLocation.Name}{Environment.NewLine}" +
                $"archive={w3dLocation.ArchiveName}{Environment.NewLine}" +
                $"gameId=0x{obj.GameId:X8}{Environment.NewLine}" +
                $"baseAsset={obj.BaseAsset}{Environment.NewLine}" +
                $"scale={obj.Scale:R}{Environment.NewLine}" +
                $"meshRadius={probe.BoundingRadius:R}{Environment.NewLine}" +
                $"worldRadius={worldRadius:R}{Environment.NewLine}" +
                $"radiusSource={radiusSource}{Environment.NewLine}" +
                $"w3dSha256={w3dHash}{Environment.NewLine}" +
                $"textures={capturedTextures}/{probe.TextureNames.Count}{Environment.NewLine}");
            capturedFiles.Add(manifestPath);

            string zipPath = System.IO.Path.Combine(outputDirectory, prefix + "research-bundle.zip");
            if (EnbNext.Installation.EnbPathCase.FileExists(zipPath))
                File.Delete(zipPath);
            using (ZipArchive archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                foreach (string capturedFile in capturedFiles)
                    archive.CreateEntryFromFile(capturedFile, System.IO.Path.GetFileName(capturedFile), CompressionLevel.Optimal);
            }

            _capturedPlanetResources.Add(captureKey);
            _planetCaptureStatus =
                $"planet capture: {zipPath} · {resourceName} · meshR {probe.BoundingRadius:0.###} · " +
                $"worldR {worldRadius:0.#} ({radiusSource}) · textures {capturedTextures}/{probe.TextureNames.Count}";
        }
        catch (Exception ex) when (ex is
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            _planetCaptureStatus =
                $"planet capture: {DescribeObject(obj)} · {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string DescribeObject(IEnbRenderObject obj)
        => string.IsNullOrWhiteSpace(obj.Name) ? $"0x{obj.GameId:X8}" : obj.Name!;

    private static bool IsRenderablePlanet(IEnbRenderObject obj)
        => obj.ObjectType == 0x03 || obj.Spatial?.ProviderKind == EnbSceneSpatialProviderKind.Planet;

    private static bool TryProject(
        EnbResolvedRenderCamera camera,
        Vector3 worldPosition,
        double viewportMargin,
        out LiveWorldLogicalProjection projection)
    {
        projection = default;
        double physicalMargin = viewportMargin * camera.Viewport.RenderScale;
        if (!camera.TryProjectWorldPoint(
                worldPosition,
                physicalMargin,
                out EnbRenderProjectionResult rendered))
        {
            return false;
        }

        projection = new LiveWorldLogicalProjection(
            camera.Viewport.ToLogicalX(rendered.PixelX),
            camera.Viewport.ToLogicalY(rendered.PixelY),
            rendered.Depth,
            rendered.Distance);
        return true;
    }

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

    private static string FormatVector(Vector3 value)
        => $"({value.X:0},{value.Y:0},{value.Z:0})";

    private static string FormatPreciseVector(Vector3 value)
        => $"({value.X:0.######},{value.Y:0.######},{value.Z:0.######})";

    private static string FormatQuaternion(Quaternion value)
        => $"({value.X:0.######},{value.Y:0.######},{value.Z:0.######},{value.W:0.######})";

    private readonly Queue<(uint Time,Vector3 Local,Vector3 Leader,uint? Packet,uint? Receipt,string Model)> _formationMotion = new();
    private uint? _formationMotionParent;
    private void RecordFormationMotion(IEnbSceneFrame scene,uint localId)
    {
        var local=scene.Objects.FirstOrDefault(o=>o.GameId==localId)?.Spatial;
        uint? parent=local?.FormationLeaderId;
        if(parent!=_formationMotionParent || _formationMotion.Count>0 && scene.ClientTime<_formationMotion.Last().Time)
            _formationMotion.Clear();
        _formationMotionParent=parent;
        if(parent is null || local is null)return;
        var leader=scene.Objects.FirstOrDefault(o=>o.GameId==parent)?.Spatial;
        if(leader is null)return;
        if(_formationMotion.Count>0 && _formationMotion.Last().Time==scene.ClientTime)return;
        _formationMotion.Enqueue((scene.ClientTime,local.Transform.Position,leader.Transform.Position,leader.PacketTime,leader.ReceiptTime,leader.ModelAudit));
        while(_formationMotion.Count>96)_formationMotion.Dequeue();
    }
    private string DescribeFormationMotion()
    {
        var result=new System.Text.StringBuilder("formation motion (last 96 rendered samples; distances in world units):");
        uint? previousTime=null,previousReceipt=null;
        Vector3 previousLocal=default,previousLeader=default;
        foreach(var sample in _formationMotion)
        {
            if(sample.Receipt!=previousReceipt || previousTime is null)
                result.Append($"\n  packet={sample.Packet} receipt={sample.Receipt} {sample.Model}");
            if(previousTime is uint time)
                result.Append($"\n  t={sample.Time} dt={sample.Time-time} local-d={Vector3.Distance(sample.Local,previousLocal):0.###} leader-d={Vector3.Distance(sample.Leader,previousLeader):0.###}");
            previousTime=sample.Time;previousReceipt=sample.Receipt;previousLocal=sample.Local;previousLeader=sample.Leader;
        }
        return result.ToString();
    }

    private static string DescribeFormationSpatialAudit(IEnbSceneFrame scene, uint localId)
    {
        var local = scene.Objects.FirstOrDefault(o => o.GameId == localId);
        var parent = scene.Objects.FirstOrDefault(o => o.GameId == local?.Spatial?.FormationLeaderId);
        string Describe(IEnbRenderObject? obj)
        {
            if (obj?.Spatial is not {} s) return "unavailable";
            var a=s.Advanced;
            return $"0x{obj.GameId:X8}/{s.ProviderKind}/{s.ModelAudit} parent={s.FormationLeaderId:X8} packet={s.PacketTime} transition={s.TransitionMilliseconds} pos={FormatPreciseVector(s.Transform.Position)} velocity={FormatPreciseVector(s.Transform.Velocity)} speed={s.Transform.Speed:0.######} advanced={a?.MovementId}/{a?.CurrentSpeed}>{a?.SetSpeed}/a{a?.Acceleration}/p{a?.UpdatePeriod}";
        }
        return "formation spatial: local " + Describe(local) + " · leader " + Describe(parent);
    }

    private static string DescribeVisibleSpatialAudit(IReadOnlyList<ProjectedObject> projected)
    {
        if (projected.Count == 0)
            return "spatial audit: R9AS_VISIBLE_OBJECT_ORIENTATION_AUDIT_ACTIVE · none";

        IEnumerable<string> entries = projected
            .OrderBy(static entry => entry.Projection.Distance)
            .Take(12)
            .Select(static entry =>
            {
                IEnbRenderObject obj = entry.Object;
                IEnbRenderSpatial spatial = obj.Spatial!;
                IEnbRenderTransform transform = spatial.Transform;
                Quaternion q = transform.Orientation;
                string advanced = spatial.Advanced is not { } raw
                    ? "adv=-"
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"adv=m0x{raw.OptionalMask:X3}/mv{raw.MovementId}/s{raw.CurrentSpeed:0.####}>{raw.SetSpeed:0.####}/a{raw.Acceleration:0.########}/" +
                        $"ry{raw.RotY:0.######}>{raw.DesiredY:0.###}/rz{raw.RotZ:0.######}>{raw.DesiredZ:0.###}/" +
                        $"spin{raw.ImpartedSpin:0.######}/roll{raw.ImpartedRoll:0.######}/pitch{raw.ImpartedPitch:0.######}/p{raw.UpdatePeriod}");

                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{DescribeObject(obj)}#0x{obj.GameId:X8}/B{obj.BaseAsset}/x{obj.Scale:0.####}/{spatial.ProviderKind}/" +
                    $"d{entry.Projection.Distance:0}/q({q.X:0.######},{q.Y:0.######},{q.Z:0.######},{q.W:0.######})/" +
                    $"r({transform.Rotation.X:0.######},{transform.Rotation.Y:0.######},{transform.Rotation.Z:0.######})/{advanced}");
            });

        return "spatial audit: R9AS_VISIBLE_OBJECT_ORIENTATION_AUDIT_ACTIVE · " + string.Join("; ", entries);
    }

    private static bool TryScaleClientBaseAsset(
        EnbPresentationClientBaseAssetReference geometry,
        float objectScale,
        out float worldRadius)
    {
        worldRadius = 0f;
        if (!float.IsFinite(objectScale) || objectScale <= 0f)
            return false;

        float adjusted = geometry.Radius + geometry.RadiusAdjust;
        if (!float.IsFinite(adjusted) || adjusted <= 0f)
            return false;

        worldRadius = adjusted * objectScale;
        return float.IsFinite(worldRadius) && worldRadius > 0f;
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) &&
           float.IsFinite(value.Z) && float.IsFinite(value.W);

    private sealed record EnvironmentAssetStatus(string Summary, string Resource);

    private sealed record ProjectedObject(
        IEnbRenderObject Object,
        LiveWorldLogicalProjection Projection);

    private readonly record struct CameraStartupKey(
        int? SessionOverrideSelector,
        int? PlayerOptionsSelector,
        string? ModeSourcePath,
        string? PlayerOptionsSourcePath,
        bool StartInFirstPerson);

    private readonly record struct TargetPreviewKey(
        ulong RenderIdentity,
        ulong VisualGeneration,
        int StateFingerprint,
        int RadiusBits,
        int CenterXBits,
        int CenterYBits,
        int CenterZBits,
        int PixelWidth,
        int PixelHeight);

    private readonly record struct LiveWorldLogicalProjection(
        double X,
        double Y,
        float Depth,
        float Distance);
}
