using Point = EnbNext.Presentation.EnbUiPoint;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.RetailMath;

namespace EnbNext.Engine;

/// <summary>Retail TSI08A lower-right scanner, controls and target readouts.</summary>
internal sealed class EnbScannerUi
{
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private readonly EnbScannerInterruption _interruption = new();
    private uint? _assessmentTargetId;
    private ulong? _assessmentOwner;
    private string? _assessmentText;
    private EnbAttachedTextStyle? _assessmentStyle;
    private ulong? _assessmentColorIdentity;
    private ulong _assessmentColorRevision;
    private EnbRgb _assessmentColor;
    private readonly EnbScannerText _interruptText = new();
    private readonly EnbAttachedLabelScroll _interruptScroll = new();
    private EnbAttachedTextStyle? _interruptStyle;
    private EnbAuthoredUiAsset? _interruptAsset;
    private Matrix4x4 _interruptTransform;
    private ulong _interruptTextRevision;
    private uint _presentationClientTime;
    internal readonly ScannerHit[] _interruptHit = [new(),new()];
    private EnbAttachedTextStyle? _factionStyle;
    private readonly EnbAttachedLabelScroll _factionScroll=new();
    private ulong? _factionIdentity;
    private ulong _factionReputationRevision,_factionTechRevision;
    private bool _factionWasInterrupted;
    private string? _factionText;
    private EnbRgb _factionColor=FactionNeutral;
    private const double DesignWidth = 640;
    private const double DesignHeight = 480;
    private const double PanelLeft = 504;
    private const double PanelTop = 333;
    private const double PanelWidth = 128;
    private const double PanelHeight = 138;
    private const double TopInformationAnchorX = 17.0;
    private const double TopInformationAnchorY = 15.600002;
    private const double BottomInformationAnchorX = 7.0;
    private const double BottomInformationAnchorY = 129.399998;
    private const double ScannerCenterX = 64.0;
    private const double ScannerCenterY = 69.0;
    private const double FactionAnchorY = ScannerCenterY - (-0.104 * DesignHeight);
    private static readonly Rect HullBarBounds = new(28.43, 3.2, 26.23, 3.9);
    private static readonly Rect ShieldBarBounds = new(61.96, 3.2, 57.54, 3.9);
    private static readonly EnbRgb White = new(1f, 1f, 1f);
    private static readonly EnbRgb Green = new(0.1f, 1f, 0.1f);
    private static readonly EnbRgb Red = new(1f, 0.1f, 0.1f);
    private static readonly EnbRgb FactionNeutral = new(0.7f, 0.7f, 0.7f);
    private static readonly EnbRgb Yellow = new(1f, 1f, 0.1f);

    private readonly Func<uint, CancellationToken, Task<bool>> _targetRequester;
    private readonly Func<uint, uint, CancellationToken, Task<bool>> _verbExecutor;
    public string? VerbExecutionError { get; private set; }

    /// <summary>Message on a player: open the chat editor to that player (00744E70 → chat virtual AC → 0065E7C0).</summary>
    internal event Action<string>? PlayerMessageRequested;

    internal async Task ExecuteVerbSafelyAsync(uint targetId, uint verb)
    {
        VerbExecutionError = null;
        // 00744E70: Message on a player target sends nothing; it selects the player as the private recipient and opens
        // the chat editor (an editor already open keeps its recipient and draft), then plays message_1.wav (SCAN-D10).
        if ((verb & 0xFFFF0000u) == 0x00050000 && (verb & 0xFFFF) == 0 && _target is { ObjectType: 1 } player && player.GameId == targetId)
        {
            string name = DisplayName(player).Trim();
            if (name.Length > 0) { PlayerMessageRequested?.Invoke(name); InteractionSoundRequested?.Invoke("message_1.wav"); }
            return;
        }
        try
        {
            if (!await _verbExecutor(targetId, verb, CancellationToken.None))
                VerbExecutionError = "Target action was not sent (target or session is no longer ready).";
            else if (VerbSound(verb, targetId) is { } sound) InteractionSoundRequested?.Invoke(sound);
        }
        catch (Exception ex)
        {
            // This is an async UI event boundary: failed actions must not escape
            // through the native async event dispatcher and terminate the process.
            VerbExecutionError = $"Target action failed: {ex.Message}";
            System.Diagnostics.Trace.TraceError("Target verb 0x{0:X8} for 0x{1:X8}: {2}", verb, targetId, ex);
        }
    }
    private readonly EnbScannerText _name = new();
    private readonly EnbScannerText _subtitle = new();
    private readonly EnbScannerText _faction = new();
    private readonly EnbScannerText _info0 = new();
    private readonly EnbScannerText _info1 = new();
    private readonly EnbAttachedLabelScroll _topReadoutScroll = new(), _bottomReadoutScroll = new();
    private EnbAttachedTextStyle? _topReadoutStyle, _bottomReadoutStyle;
    private int _renderedTopField=-1, _renderedBottomField=-1;
    private readonly EnbScannerText[] _resourceLines =
        Enumerable.Range(0, 7).Select(_ => new EnbScannerText()).ToArray();
    private readonly EnbAttachedLabelWipe[] _harvestOpening = Enumerable.Range(0,7).Select(_=>new EnbAttachedLabelWipe()).ToArray();
    private readonly float[] _harvestWipeSpeeds = new float[7];
    private readonly (string Name,float Volume,float Duration)?[] _harvestSounds = new (string,float,float)?[7];
    private readonly EnbHarvestResourceList _harvestResources=new();
    private readonly bool[] _harvestRowSeen = new bool[7], _harvestSoundStarted = new bool[7];
    private ulong? _harvestContextIdentity;
    private bool _harvestContextMaterialized;
    private EnbInterfaceLayoutCatalog? _harvestLayout;
    private EnbRetailIniDocument? _fontArt;
    private EnbRetailFontFace? _harvestFont;
    private EnbUiTooltipDraw? _scannerTooltip;
    private readonly EnbRetailText _scannerTooltipTitle = new();
    private readonly EnbRetailText _scannerTooltipDetail = new();
    private readonly List<EnbRetailText> _scannerTooltipExtra = [];
    private readonly Dictionary<uint, EnbAuthoredUiAsset> _verbAssets = [];
    private uint? _hoveredVerb;
    public event Action<ImmutableList<EnbUiModelDraw>>? VerbPresentationChanged;
    private EnbRetailAssetBundle? _assets;
    private EnbAuthoredTargetPanel? _panelAsset;
    private EnbRetailFontFace? _font;
    private EnbRetailFontFace? _tooltipFont;
    private EnbRetailScannerTooltipDefinition? _tooltipDefinition;
    private EnbAuthoredTooltipFrame? _tooltipFrame;
    public event Action<EnbUiTooltipDraw?>? TooltipPresentationChanged;
    private int _tooltipKind;
    private Point _tooltipPointer;
    private readonly EnbCurrentRetailTooltipDelay _tooltipDwell = new();
    private int _tooltipDelayMilliseconds = EnbCurrentRetailTooltipDelay.Milliseconds(50f);
    public Func<uint?> FixedSessionIdAccessor { private get; set; } = () => null;
    private IEnbSceneFrame? _snapshot;
    private EnbRadarFrame? _radar;
    private IEnbRenderObject? _target;
    private int _topInformationField = 1;
    private int _bottomInformationField = 3;
    private string? _topRetainedReadout;
    private string? _bottomRetainedReadout;
    private bool _showHarvestContents;
    // Modern UI renders an avatar target's face itself; any other target keeps the scanner model in its portrait square.
    public bool ShowTargetModel => !_showHarvestContents && !(Replaced && ReplacementModelBounds is null);
    public event Action<string,float,float>? HarvestSoundRequested;
    public event Action<string>? InteractionSoundRequested;
    public event Action<string>? AssessmentSoundRequested;
    private bool _hasPendingLocalSelection;
    private uint _pendingLocalTargetId;
    private long _pendingLocalSelectionToken;
    private long _nextLocalSelectionToken;
    private bool _targetModelReady;
    private ImmutableArray<uint> _renderedVerbValues = ImmutableArray<uint>.Empty;
    private uint? _renderedVerbTargetId;
    private long _renderedVerbWidthBits;
    private long _renderedVerbHeightBits;
    private bool _verbControlsMaterialized;
    private double _logicalWidth = DesignWidth;
    private double _logicalHeight = DesignHeight;
    private double _renderScaling = 1.0;
    private double _panelLeft;
    private double _panelTop;
    private double _panelPixelWidth;
    private double _panelPixelHeight;

    public EnbScannerUi(Func<uint,CancellationToken,Task<bool>> targetRequester,
        Func<uint,uint,CancellationToken,Task<bool>> verbExecutor)
    {
        _targetRequester=targetRequester??throw new ArgumentNullException(nameof(targetRequester));
        _verbExecutor=verbExecutor??throw new ArgumentNullException(nameof(verbExecutor));
        Texts=[_name,_subtitle,_faction,_info0,_info1,.._resourceLines,_interruptText];
    }
    internal EnbScannerText[] Texts {get;}
    internal sealed class ScannerHit { public Rect Bounds; public bool IsVisible; }
    internal sealed class VerbButton(uint value,uint targetId,bool executable,EnbAuthoredUiAsset asset)
    {
        public uint Value=value,TargetId=targetId;
        public bool Executable=executable;
        public EnbAuthoredUiAsset Asset=asset;
        public Rect Bounds;
    }
    internal readonly List<VerbButton> Verbs=[];
    public Rect VerbBounds {get;private set;}
    private bool _verbsVisible;
    public bool VerbsVisible=>IsVisible&&_verbsVisible;
    private bool _visible=true;
    public bool IsVisible { get=>_visible; set { if(_visible==value)return;_visible=value;if(!value)HideScannerTooltip();PublishVerbs(); } }
    public event Action? TextChanged,LayoutChanged,VerbLayoutChanged;
    public event Action<bool>? TooltipTimerChanged;
    public double RenderScaling {get;set;}=1;
    public void TickTooltip()=>ShowRetailTooltip(_tooltipKind,_tooltipPointer);


    public IEnbRenderObject? SelectedTarget => _target;

    private Rect _targetModelBounds;
    public Rect TargetModelBounds { get=>Replaced && ReplacementModelBounds is {} modern?modern:UiLayout?.Apply(EnbUiGroup.Scanner,_targetModelBounds)??_targetModelBounds; private set=>_targetModelBounds=value; }
    private bool _replaced;
    /// <summary>Modern UI owns the target presentation: the scanner keeps its target, model and verb state but draws
    /// no chrome, text or verb buttons and takes no pointer input.</summary>
    internal bool Replaced { get=>_replaced; set { if(_replaced==value)return;_replaced=value;HideScannerTooltip();PublishVerbs();LayoutChanged?.Invoke(); } }
    /// <summary>Modern UI target portrait square for the scanner model (screen space).</summary>
    internal Rect? ReplacementModelBounds {get;set;}
    internal string? TargetDisplayName=>_target is {} target?DisplayName(target):null;
    internal EnbRgb TargetNameColor=>_assessmentColor;
    /// <summary>The server's assessment of the current target ("Pathetic" .. "Impossible"), as the scanner shows it under the name.</summary>
    internal string? TargetAssessment=>_target is not null&&_assessmentTargetId==_target.GameId?_assessmentText:null;
    /// <summary>Modern UI: the target's skill activation (the interruption gauges' state), while the gauges would show.</summary>
    internal EnbModernCast? ModernCast=>_interruption.Visible?new(_interruption.Text,_interruption.Progress,_interruption.ActivationProgress,_interruption.State):null;
    internal string? TargetReadout(int field)=>_target is null?null:ResolveInformationField(field);
    internal bool TargetVerbsVisible=>IsVisible&&_verbsVisible;
    /// <summary>A targeted asteroid's contents as the harvest page lists them ("name (count)"), for a prospecting pilot; otherwise null.
    /// Retail shows them after a click on the scanner (005BF790); Modern UI shows them in the target frame's tooltip.</summary>
    internal System.Collections.Immutable.ImmutableArray<string> HarvestContents
    {
        get
        {
            if(_target is not {ObjectType:0x26} target || (_snapshot?.Hud.ProspectSkillLevel??0)==0)return default;
            if(target.HarvestableResourcesState==0)return ["Contents not yet scanned."];
            var lines=System.Collections.Immutable.ImmutableArray.CreateBuilder<string>();
            foreach(var item in target.HarvestableResources)
            {
                string label=item.Name??"Null Item";int end=label.IndexOf('\0');if(end>=0)label=label[..end];
                lines.Add($"{label} ({item.StackCount})");
            }
            return lines.Count==0?["Empty"]:lines.ToImmutable();
        }
    }
    internal void ClearSelectedTarget(){InteractionSoundRequested?.Invoke("TextClick01.wav");ClearTarget();}

    internal Rect LayoutBounds=>new(_panelLeft,_panelTop,_panelPixelWidth,_panelPixelHeight);
    public Rect PanelBounds => UiLayout?.Apply(EnbUiGroup.Scanner,new(_panelLeft,_panelTop,_panelPixelWidth,_panelPixelHeight))??new(_panelLeft,_panelTop,_panelPixelWidth,_panelPixelHeight);
    public EnbGaugeDraw? GaugePresentation { get; private set; }
    public double ScannerAuthoredAspectRatio => _panelAsset is { } asset
        ? asset.ScannerBounds.Width / asset.ScannerBounds.Height : double.NaN;

    public void Update(
        IEnbSceneFrame snapshot,
        EnbRadarFrame radar,
        EnbRetailAssetBundle? assets,
        double logicalWidth,
        double logicalHeight)
        => UpdateAtTime(snapshot,radar,assets,logicalWidth,logicalHeight,snapshot.ClientTime);

    public void UpdateAtTime(IEnbSceneFrame snapshot,EnbRadarFrame radar,EnbRetailAssetBundle? assets,
        double logicalWidth,double logicalHeight,uint currentClientTime)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(radar);
        _snapshot = snapshot;
        _presentationClientTime=currentClientTime;
        _radar = radar;
        if (!ReferenceEquals(_assets, assets))
            LoadAssets(assets);

        _logicalWidth = Math.Max(1, logicalWidth);
        _logicalHeight = Math.Max(1, logicalHeight);
        _renderScaling = Math.Max(0.01, RenderScaling);
        EnsureFont(assets, _logicalWidth * _renderScaling, _logicalHeight * _renderScaling);
        _tooltipDelayMilliseconds = EnbCurrentRetailTooltipDelay.Milliseconds(
            assets?.ResolvePlayerOptions(FixedSessionIdAccessor()).Options.InterfaceTooltipDelay ?? 50f);

        double left = PanelLeft * logicalWidth / DesignWidth;
        double top = PanelTop * logicalHeight / DesignHeight;
        double width = PanelWidth * logicalWidth / DesignWidth;
        double height = PanelHeight * logicalHeight / DesignHeight;
        _panelLeft = left;
        _panelTop = top;
        _panelPixelWidth = width;
        _panelPixelHeight = height;
        LayoutChanged?.Invoke();
        Rect authoredScanner = _panelAsset?.ScannerBounds ?? default;
        TargetModelBounds = new Rect(
            (PanelLeft + authoredScanner.X) * logicalWidth / DesignWidth,
            (PanelTop + authoredScanner.Y) * logicalHeight / DesignHeight,
            authoredScanner.Width * logicalWidth / DesignWidth,
            authoredScanner.Height * logicalHeight / DesignHeight);

        uint? snapshotSelectedId = snapshot.Hud.HasLocalTargetSelection
            ? snapshot.Hud.LocalSelectedTargetId
            : snapshot.Hud.CurrentTargetId;
        if (_hasPendingLocalSelection && snapshotSelectedId.GetValueOrDefault() == _pendingLocalTargetId)
            _hasPendingLocalSelection = false;
        uint? selectedId = _hasPendingLocalSelection
            ? (_pendingLocalTargetId == 0 ? null : _pendingLocalTargetId)
            : snapshotSelectedId;
        IEnbRenderObject? prior = _target;
        _target = selectedId is uint id
            ? snapshot.Objects.FirstOrDefault(obj => obj.GameId == id)
            : null;
        if (prior?.RenderIdentity != _target?.RenderIdentity)
        {
            _showHarvestContents = false;
            ResetInformationPages();
            _targetModelReady = _target is null;
        }

        UpdateAssessment();
        UpdateVitals();
        UpdateText();
        UpdateVerbs(left, top, width, height);
        if (_scannerTooltip is not null)
            ShowRetailTooltip(_tooltipKind, _tooltipPointer);
    }

    public long ApplyLocalTargetSelection(uint targetId)
    {
        _nextLocalSelectionToken = _nextLocalSelectionToken == long.MaxValue
            ? 1
            : _nextLocalSelectionToken + 1;
        _hasPendingLocalSelection = true;
        _pendingLocalTargetId = targetId;
        _pendingLocalSelectionToken = _nextLocalSelectionToken;
        IEnbRenderObject? prior = _target;
        _target = targetId == 0
            ? null
            : _snapshot?.Objects.FirstOrDefault(obj => obj.GameId == targetId);
        // 5BC9E0 compares the physical owner before resetting pages/readouts.
        if (prior?.RenderIdentity != _target?.RenderIdentity)
        {
            _showHarvestContents = false;
            ResetInformationPages();
            _targetModelReady = _target is null;
        }
        UpdateVitals();
        UpdateText();
        UpdateVerbs(_panelLeft, _panelTop, _panelPixelWidth, _panelPixelHeight);
        return _pendingLocalSelectionToken;
    }

    public void RejectLocalTargetSelection(long selectionToken)
    {
        // A completed older request must never undo a newer local click. Once
        // this exact optimistic selection is rejected, the next immutable
        // snapshot tick resumes ownership of the displayed target.
        if (!_hasPendingLocalSelection || _pendingLocalSelectionToken != selectionToken)
            return;
        _hasPendingLocalSelection = false;
        _verbsVisible = false;VerbLayoutChanged?.Invoke();
    }

    public void SetTargetModelReady(bool ready)
    {
        bool effective = _target is null || ready;
        if (_targetModelReady == effective)
            return;

        _targetModelReady = effective;
    }

    public void ReleaseAssets()
    {
        _interruption.Reset();_interruptText.Clear();_interruptScroll.Clear();
        _interruptStyle=null;_interruptAsset=null;
        _factionStyle=null;_factionIdentity=null;_factionText=null;_factionScroll.Clear();
        _assessmentStyle=null;_assessmentColorIdentity=null;
        foreach(var hit in _interruptHit)hit.IsVisible=false;
        _topReadoutStyle=_bottomReadoutStyle=null;
        _topReadoutScroll.Clear();_bottomReadoutScroll.Clear();
        InvalidateVerbControls();
        GaugePresentation = null;
        _panelAsset = null;
        _verbAssets.Clear();
        _font = null;
        _tooltipFont = null;
        _tooltipDefinition = null;
        _tooltipFrame = null;
        HideScannerTooltip();
        _scannerTooltipTitle.Clear();
        _scannerTooltipDetail.Clear();
        foreach(var line in _scannerTooltipExtra)line.Clear();
        _assets = null;
        foreach (EnbScannerText line in new[] { _name, _subtitle, _faction, _info0, _info1 }.Concat(_resourceLines))
            line.Clear();
        TextChanged?.Invoke();LayoutChanged?.Invoke();
    }



    private void LoadAssets(EnbRetailAssetBundle? assets)
    {
        ReleaseAssets();
        _assets = assets;
        if (assets is null)
            return;
        _tooltipDefinition = EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
        _harvestLayout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_harvesting.ini"));
        _fontArt = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        Array.Clear(_harvestWipeSpeeds);
        Array.Clear(_harvestSounds);
        for(int row=0;row<_harvestSounds.Length;row++)
            if(_harvestLayout.TryResolve($"TEXT{row:00}_HARV",out var soundText) &&
                soundText.Properties.TryGetValue("SoundOnID",out var soundName) && !string.IsNullOrWhiteSpace(soundName) &&
                float.TryParse(soundText.Properties.GetValueOrDefault("SoundOnVol"),NumberStyles.Float,CultureInfo.InvariantCulture,out var soundVolume) &&
                float.TryParse(soundText.Properties.GetValueOrDefault("SoundOnDur"),NumberStyles.Float,CultureInfo.InvariantCulture,out var soundDuration))
                _harvestSounds[row]=(soundName,soundVolume,soundDuration);
        for(int row=0;row<_harvestWipeSpeeds.Length;row++)
            if(_harvestLayout.TryResolve($"TEXT{row:00}_HARV",out var text))
                foreach(var effect in _fontArt.EnumerateSections().Select(s=>s.Value))
                    if(text.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(id=>id.Trim()==effect.GetValueOrDefault("EffectID")) &&
                        effect.GetValueOrDefault("EffectType")=="WIPE" && effect.GetValueOrDefault("Direction")=="RIGHT" &&
                        float.Parse(effect.GetValueOrDefault("Intensity","0"),CultureInfo.InvariantCulture)>0)
                        _harvestWipeSpeeds[row]=float.Parse(effect.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
        _tooltipFrame = _tooltipDefinition is { } definition ? EnbAuthoredTooltipFrame.Load(assets,definition) : null;
        if (EnbAuthoredTargetPanelLoader.TryCreate(assets, out EnbAuthoredTargetPanel panel))
        {
            _panelAsset = panel;
            // Scanner chrome and navigation glyphs are submitted as GPU materials.
        }
        if(EnbAuthoredUiAsset.Load(assets,"tsi08a.w3d") is {} scannerAsset)
        {
            var parent=Matrix4x4.CreateTranslation((float)(PanelLeft+ScannerCenterX-320),(float)(240-PanelTop-ScannerCenterY),0);
            if(assets.MfdLayout?.TryResolve("MFD_INFO_VALUE_00",out var top)==true)
                _topReadoutStyle=EnbAttachedTextStyle.Load(assets,scannerAsset,top,parent);
            if(assets.MfdLayout?.TryResolve("MFD_INFO_VALUE_01",out var bottom)==true)
                _bottomReadoutStyle=EnbAttachedTextStyle.Load(assets,scannerAsset,bottom,parent);
            if(assets.MfdLayout?.TryResolve("MFD_INTERRUPT",out var interrupt)==true)
                _interruptStyle=EnbAttachedTextStyle.Load(assets,scannerAsset,interrupt,parent);
            if(assets.MfdLayout?.TryResolve("MFD_FACTION",out var faction)==true)
                _factionStyle=EnbAttachedTextStyle.Load(assets,scannerAsset,faction,parent);
            if(assets.MfdLayout?.TryResolve("MFD_CON",out var assessment)==true)
                _assessmentStyle=EnbAttachedTextStyle.Load(assets,scannerAsset,assessment,parent);
            _interruptAsset=EnbAuthoredUiAsset.Load(assets,"tsi08c.w3d");
            var bone=scannerAsset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name=="BONE_INTER");
            if(bone is not null)_interruptTransform=EnbCockpitUi.ResolveTransforms(scannerAsset)[bone.Ordinal];
            else _interruptAsset=null;
            if(_interruptAsset is {} gauge)
            {
                var transforms=EnbCockpitUi.ResolveTransforms(gauge);
                for(int i=0;i<2;i++)
                {
                    string name=i==0?"INTER_BAR0":"INTER2_BAR0";
                    var mesh=gauge.Resource.Meshes.FirstOrDefault(m=>m.Name==name);
                    var pivot=gauge.Hierarchy.Pivots.FirstOrDefault(p=>p.Name==name);
                    if(mesh is null || pivot is null){_interruptAsset=null;break;}
                    var transform=transforms[pivot.Ordinal]*_interruptTransform;
                    var min=new Vector3(float.PositiveInfinity);var max=new Vector3(float.NegativeInfinity);
                    for(int j=0;j<8;j++)
                    {
                        var p=Vector3.Transform(new((j&1)==0?mesh.BoundingBoxMin.X:mesh.BoundingBoxMax.X,
                            (j&2)==0?mesh.BoundingBoxMin.Y:mesh.BoundingBoxMax.Y,(j&4)==0?mesh.BoundingBoxMin.Z:mesh.BoundingBoxMax.Z),transform);
                        min=Vector3.Min(min,p);max=Vector3.Max(max,p);
                    }
                    _interruptHit[i].Bounds=new(ScannerCenterX+min.X,ScannerCenterY-max.Y,max.X-min.X,max.Y-min.Y);
                }
            }
        }
        _scannerTooltipTitle.Clear();
        _scannerTooltipDetail.Clear();
        foreach(var line in _scannerTooltipExtra)line.Clear();
    }

    private void EnsureFont(EnbRetailAssetBundle? assets, double logicalWidth, double logicalHeight)
    {
        if (assets is null)
        {
            _font = null;
            _tooltipFont = null;
            return;
        }

        if (_tooltipDefinition is { } tooltip &&
            EnbRetailFontFactory.TryResolveDefinition(assets, tooltip.FontId,
                (int)Math.Round(logicalWidth), (int)Math.Round(logicalHeight), out var tooltipFont) &&
            (_tooltipFont is null || !string.Equals(_tooltipFont.ResourceName, tooltipFont.Resource, StringComparison.OrdinalIgnoreCase)) &&
            EnbRetailFontFactory.TryCreate(assets, tooltipFont, out var face))
            _tooltipFont = face;

        string fontId = "FONT_SMALL";
        if (_harvestLayout?.TryResolve("TEXT00_HARV",out var harvest) == true &&
            EnbRetailFontFactory.TryResolveDefinition(assets,harvest.Properties["FontType"],
                (int)Math.Round(logicalWidth),(int)Math.Round(logicalHeight),out var harvestFont) &&
            (_harvestFont is null || _harvestFont.ResourceName != harvestFont.Resource) &&
            EnbRetailFontFactory.TryCreate(assets,harvestFont,out var harvestFace))
            _harvestFont=harvestFace;
        if (assets.MfdLayout?.TryResolve("MFD_INFO_VALUE_00", out EnbInterfaceTextDefinition definition) == true &&
            definition.Properties.TryGetValue("FontType", out string? authoredFont) &&
            !string.IsNullOrWhiteSpace(authoredFont))
        {
            fontId = authoredFont.Trim();
        }

        if (EnbRetailFontFactory.TryResolveDefinition(
                assets, fontId, (int)Math.Round(logicalWidth), (int)Math.Round(logicalHeight), out EnbFontDefinition fontDefinition) &&
            (_font is null || !string.Equals(_font.ResourceName, fontDefinition.Resource, StringComparison.OrdinalIgnoreCase)) &&
            EnbRetailFontFactory.TryCreate(assets, fontDefinition, out EnbRetailFontFace font))
        {
            _font = font;
        }
    }

    private void UpdateVitals()
    {
        _interruption.Update(_target?.RenderIdentity,_target?.Interruption,_presentationClientTime);
        foreach(var hit in _interruptHit)hit.IsVisible=_interruption.Visible&&_interruptAsset is not null&&!Replaced;
        LayoutChanged?.Invoke();
        if(!_interruption.Visible && _tooltipKind is 5 or 6)HideScannerTooltip();
        EnbSceneTargetVitalsSnapshot? vitals = _target?.TargetVitals;
        // 005DC390 resets unavailable gauges to zero; 005DC4A0 fills only
        // materialized ship vitals. Missing data is not evidence of full health.
        double hull = Ratio(vitals?.HullPoints, vitals?.MaximumHullPoints);
        double shield = vitals?.ShieldPercent is float shieldPercent && float.IsFinite(shieldPercent)
            ? Math.Clamp(shieldPercent, 0f, 1f) : 0;
        GaugePresentation = _panelAsset is { } asset
            ? new(asset,(float)hull,(float)shield) { TargetId = _target?.GameId,
                Interruption=_interruption.Visible && _interruptAsset is {} interruptAsset
                    ? new(interruptAsset,_interruptTransform,_interruption.Progress,_interruption.ActivationProgress,_interruption.Generation):null } : null;
    }

    private void UpdateText()
    {
        try {
        UpdateAssessmentColor();
        long harvestNow=Environment.TickCount64;
        // 5BF790 creates the row context only for materialized resources;
        // 5C0D30 destroys/recreates its child when the active pending page changes state.
        bool materialized=(_target?.HarvestableResourcesState??0)!=0;
        if(_harvestContextIdentity!=_target?.RenderIdentity ||
            (_showHarvestContents && _harvestContextMaterialized!=materialized))
        {
            _harvestContextIdentity=_target?.RenderIdentity;
            _harvestContextMaterialized=materialized;
            for(int row=0;row<_harvestOpening.Length;row++)_harvestOpening[row]=new();
            Array.Clear(_harvestRowSeen);Array.Clear(_harvestSoundStarted);
            _harvestResources.Reset(materialized && _target is {} harvestTarget?harvestTarget.HarvestableResources:[]);
        }
        if(!_showHarvestContents)
            foreach(var opening in _harvestOpening)opening.Pause(harvestNow);
        if(_renderedTopField!=_topInformationField){_topReadoutScroll.Clear();_topRetainedReadout=null;_renderedTopField=_topInformationField;}
        if(_renderedBottomField!=_bottomInformationField){_bottomReadoutScroll.Clear();_bottomRetainedReadout=null;_renderedBottomField=_bottomInformationField;}
        if (_target is null || _font is null)
        {
            ClearText();
            return;
        }

        UpdateFactionText();
        if(_interruption.Visible && _interruptAsset is not null && _interruptStyle is {} interruptStyle)
        {
            _faction.Clear();
            if(_interruptTextRevision!=_interruption.TextRevision)
            {_interruptScroll.Clear();_interruptTextRevision=_interruption.TextRevision;}
            _interruptText.SetText(_interruption.Text,_font,White);
            ScaleTextToNativeDisplayPixels(_interruptText);
            float offset=_interruptScroll.Update(_interruption.Text,Environment.TickCount64,
                (float)_interruptText.Width,interruptStyle.HalfWidth,interruptStyle.ScrollSpeed);
            double x=interruptStyle.Anchor.X-PanelLeft+offset-_interruptText.Width/2;
            double y=interruptStyle.Anchor.Y-PanelTop-_interruptText.Height/2;
            _interruptText.Clip=interruptStyle.Clip.Translate(-PanelLeft-x,-PanelTop-y);
            _interruptText.Left=x;_interruptText.Top=y;
        }
        else _interruptText.Clear();

        if (_showHarvestContents)
        {
            if(_harvestContextMaterialized)
                _harvestResources.Update(_target.HarvestableResourcesState,_target.HarvestableResourceSlotStates,_target.HarvestableResources);
            SetAttachedLeft(_name, DisplayName(_target), White, TopInformationAnchorX, TopInformationAnchorY);
            _info1.Clear();
            _subtitle.IsVisible=false;
            SetAttachedLeft(_info0, ResolveInformationField(3), White, BottomInformationAnchorX, BottomInformationAnchorY);
            // Retail 005C07E0 pads the authored row list on either side,
            // using integer division when the number of spare rows is odd.
            int rowCount = 0;
            while (rowCount < _resourceLines.Length &&
                _harvestLayout?.TryResolve($"TEXT{rowCount:00}_HARV", out _) == true) rowCount++;
            int itemCount = _harvestContextMaterialized?Math.Min(_harvestResources.Items.Count, rowCount):0;
            int firstRow = (rowCount - itemCount) / 2;
            int line = 0;
            foreach (EnbSceneHarvestableResourceSnapshot item in _harvestResources.Items.Take(itemCount))
            {
                EnbScannerText control = _resourceLines[line++];
                // 5C0350 uses the resolved template's C string verbatim,
                // including empty/whitespace; 68AAA0 falls back to Null Item.
                string label = item.Name ?? "Null Item";
                int terminator = label.IndexOf('\0');
                if (terminator >= 0) label = label[..terminator];
                if (_harvestLayout?.TryResolve($"TEXT{firstRow+line-1:00}_HARV",out var style) != true) continue;
                var color=White;
                foreach(var section in _fontArt?.SectionNames ?? [])
                    if(_fontArt!.TryGetSection(section,out var values) && values.GetValueOrDefault("ColorID")==style.Properties["Color"] &&
                        values.TryGetValue("RGB",out var rgb))
                    {
                        var components=rgb.Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)/255f).ToArray();
                        if(components.Length==3)color=new(components[0],components[1],components[2]);
                    }
                control.SetText($"{label} ({item.StackCount})", _harvestFont, color);
                ScaleTextToNativeDisplayPixels(control);
                int authoredRow=firstRow+line-1;
                // 6D60C0 skips the first frame; the next active opening frame
                // starts this font's cue. Completed empty rows never replay it.
                if(_harvestRowSeen[authoredRow] && !_harvestSoundStarted[authoredRow] && !_harvestOpening[authoredRow].Complete)
                {
                    _harvestSoundStarted[authoredRow]=true;
                    if(_harvestSounds[authoredRow] is {} cue)HarvestSoundRequested?.Invoke(cue.Name,cue.Volume,cue.Duration);
                }
                _harvestRowSeen[authoredRow]=true;
                int rasterWidth=control.Raster?.Width??0;
                int extent=_harvestWipeSpeeds[authoredRow]>0
                    ? _harvestOpening[authoredRow].Update(harvestNow,rasterWidth,_harvestWipeSpeeds[authoredRow]):rasterWidth;
                control.Clip=new Rect(0,0,rasterWidth==0?0:control.Width*extent/rasterWidth,control.Height);
                var offset=style.Properties["Offset"].Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
                // 5C17D0 uses the first readout's transform as the row origin.
                // 6D528F scales X by 640 and Y by 480, independently.
                double anchorX=(_topReadoutStyle?.Anchor.X ?? PanelLeft+TopInformationAnchorX)-PanelLeft;
                double anchorY=(_topReadoutStyle?.Anchor.Y ?? PanelTop+TopInformationAnchorY)-PanelTop;
                control.Left= anchorX + offset[0]*(float)DesignWidth;
                control.Top= anchorY-offset[1]*(float)DesignHeight-control.Height*.5;
            }
            // Empty authored rows still have their own effect state. Replacement
            // text uses setter flag0 (5C1970), so it does not restart a completed wipe.
            if(_harvestContextMaterialized)
                for(int row=0;row<rowCount;row++)
                    if(row<firstRow || row>=firstRow+itemCount)
                        _harvestOpening[row].Update(harvestNow,0,_harvestWipeSpeeds[row]);
            while (line < _resourceLines.Length)
                _resourceLines[line++].Clear();
            return;
        }

        string? threat = _assessmentTargetId == _target.GameId ? _assessmentText : null;
        SetAssessmentText(threat);
        foreach (EnbScannerText resourceLine in _resourceLines)
            resourceLine.Clear();

        if (_topInformationField == 1)
        {
            SetAttachedLeft(_name, RetainReadout(_topInformationField,ref _topRetainedReadout), White, TopInformationAnchorX, TopInformationAnchorY);
            _info1.Clear();
        }
        else
        {
            _name.Clear();
            SetAttachedLeft(
                _info1,
                RetainReadout(_topInformationField,ref _topRetainedReadout),
                White,
                TopInformationAnchorX,
                TopInformationAnchorY);
        }
        SetAttachedLeft(
            _info0,
            RetainReadout(_bottomInformationField,ref _bottomRetainedReadout),
            White,
            BottomInformationAnchorX,
            BottomInformationAnchorY);
        } finally { TextChanged?.Invoke(); }
    }

    private string? RetainReadout(int field,ref string? retained)
    {
        // 5BCE50 returns without touching the widget when the evaluator has no
        // value. Field/target changes explicitly clear it before forced refresh.
        string? value=ResolveInformationField(field);
        if(!string.IsNullOrEmpty(value))retained=value;
        return retained;
    }

    private void UpdateAssessment()
    {
        // 00512400 refreshes only when the resolved authoritative target differs
        // from the scanner target. Optimistic local selection is not a server
        // assessment; repeated snapshots must not replay or rewrite this cue.
        ulong? owner = _snapshot?.Objects.FirstOrDefault(o => o.IsLocalShip)?.RenderIdentity;
        if (_assessmentOwner != owner)
        {
            _assessmentOwner = owner;
            _assessmentTargetId = null;
            _assessmentText = null;
        }
        uint? id = _snapshot?.Hud.CurrentTargetId is uint authoritativeId &&
            _snapshot.Objects.Any(o => o.GameId == authoritativeId) ? authoritativeId : null;
        if (id == _assessmentTargetId) return;
        _assessmentTargetId = id;
        _assessmentText = null;
        if (id is null || _snapshot is null) return;
        var assessment = EnbRetailTargetAssessment.Resolve(_snapshot.Hud.TargetThreat,
            _snapshot.Hud.TargetThreatLevel, _snapshot.Hud.TargetThreatSound);
        _assessmentText = assessment.Text;
        if (assessment.Sound is { } sound) AssessmentSoundRequested?.Invoke(sound);
    }

    private void UpdateFactionText()
    {
        if(_target is null || _snapshot is null)return;
        bool interrupted=_interruption.Visible&&_interruptAsset is not null;
        bool changedTarget=_factionIdentity!=_target.RenderIdentity;
        bool force=changedTarget || (_factionWasInterrupted&&!interrupted);
        _factionIdentity=_target.RenderIdentity;
        if(force){_factionText=null;_factionScroll.Clear();}
        _factionWasInterrupted=interrupted;
        if(interrupted)
        {_faction.Clear();_factionScroll.Pause(Environment.TickCount64);return;}
        // 0060F1A0 refreshes on target selection, completion of interruption,
        // or a new materialized reputation/TechLevel update. Frame refreshes
        // alone do not call SetText (which resets scroll even for equal text).
        var hud=_snapshot.Hud;
        if(_target.ScannerFactionState!=0 && hud.ReputationState>=(force?1:2) &&
            (force || hud.ReputationRevision!=_factionReputationRevision))
        {
            _factionText=ResolveFactionText();
            _factionColor=ResolveFactionColor(_target.PlayerPresentation?.FactionReaction);
            _factionScroll.Clear();
        }
        if(_target.ScannerTechLevelState>=(force?1:2) &&
            (force || _target.ScannerTechLevelRevision!=_factionTechRevision) &&
            _target.HarvestableTechLevel is int level && level!=0)
        {
            _factionText=$"Tech Level: {level}";_factionColor=FactionNeutral;_factionScroll.Clear();
        }
        _factionReputationRevision=hud.ReputationRevision;
        _factionTechRevision=_target.ScannerTechLevelRevision;
        if(_factionStyle is not {} style){_faction.Clear();return;}
        _faction.SetText(_factionText,_font,_factionColor);ScaleTextToNativeDisplayPixels(_faction);
        float offset=_factionScroll.Update(_factionText??"",Environment.TickCount64,
            (float)_faction.Width,style.HalfWidth,style.ScrollSpeed);
        double x=style.Anchor.X-PanelLeft+offset-_faction.Width/2;
        double y=style.Anchor.Y-PanelTop-_faction.Height/2;
        _faction.Clip=style.Clip.Translate(-PanelLeft-x,-PanelTop-y);
        _faction.Left=x;_faction.Top=y;
    }

    private string? ResolveFactionText()
    {
        if (_target is null)
            return null;
        string? key = _target.PlayerPresentation?.FactionIdentifier;
        return !string.IsNullOrEmpty(key) && _assets?.Factions?.TryResolve(key, out EnbFactionDefinition definition) == true
            ? definition.Name
            : null;
    }

    private EnbRgb ResolveFactionColor(float? reaction)
    {
        // 0060F714 calls __ftol before comparing the low signed 32 bits with
        // +/-2000. Fractional negative reactions must truncate toward zero.
        int? value=reaction is float number && float.IsFinite(number)?unchecked((int)(long)number):null;
        return value switch
        {
            < -2000 => Red,
            < 2000 => Yellow,
            >= 2000 => Green,
            _ => FactionNeutral,
        };
    }

    private string? ResolveInformationField(int field)
    {
        if (_snapshot is null || _target?.Spatial?.Transform is not { } targetTransform)
            return null;
        IEnbRenderObject? local = _snapshot.Objects.FirstOrDefault(static obj => obj.IsLocalShip);
        if (local?.Spatial?.Transform is not { } localTransform)
            return null;

        var radii = ResolveReadoutRadii(local);
        EnbSceneTargetVitalsSnapshot? vitals = _target.TargetVitals;
        string? shieldValue = field is 10 or 11 or 12
            ? EnbNext.RetailMath.EnbRetailShieldReadout.FormatValue(field,vitals?.MaximumShieldPower,vitals?.ShieldPercent) : null;
        return field switch
        {
            1 => DisplayName(_target),
            2 when _target.GameId != local.GameId => $"Turn To: {EnbRetailNavigationReadout.FormatDirection(EnbRetailNavigationReadout.TurnTo(localTransform.OrientationColumn0, localTransform.Position, targetTransform.Position))}",
            3 when _target.GameId != local.GameId => "Dist: " + EnbRetailNavigationReadout.FormatDistance(EnbRetailNavigationReadout.Distance(localTransform.Position,targetTransform.Position,radii.Local,radii.Target)),
            4 when _target.GameId != local.GameId => "ETA: " + EnbRetailNavigationReadout.FormatEta(EnbRetailNavigationReadout.Eta(localTransform.Position,targetTransform.Position,radii.Local,radii.Target,localTransform.Velocity)),
            5 => "Dir: " + EnbRetailNavigationReadout.Heading(targetTransform.OrientationColumn0).ToString(CultureInfo.InvariantCulture),
            6 when _target.GameId != local.GameId => $"Same Dir: {EnbRetailNavigationReadout.FormatDirection(EnbRetailNavigationReadout.SameDirection(localTransform.OrientationColumn0, targetTransform.OrientationColumn0))}",
            10 when shieldValue is not null => "SP Cur: " + shieldValue,
            11 when shieldValue is not null => "SP Max: " + shieldValue,
            12 when shieldValue is not null => "SP %: " + shieldValue,
            13 => "Vel: " + EnbNext.RetailMath.EnbRetailNavigationReadout.Velocity(targetTransform.Velocity).ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private void UpdateVerbs(double panelLeft,double panelTop,double panelWidth,double panelHeight)
    {
        if(_snapshot is null || _target is null || _hasPendingLocalSelection)
        { _verbsVisible=false;ClearVerbHover();VerbPresentationChanged?.Invoke([]);VerbLayoutChanged?.Invoke();return; }
        uint[] orderedVerbs=EnbCurrentRetailTargetingRuntime.OrderVerbButtonsLeftToRight(_snapshot.Hud.CurrentTargetVerbs);
        long widthBits=BitConverter.DoubleToInt64Bits(panelWidth),heightBits=BitConverter.DoubleToInt64Bits(panelHeight);
        bool rebuild=!_verbControlsMaterialized || _renderedVerbTargetId!=_target.GameId ||
            _renderedVerbWidthBits!=widthBits || _renderedVerbHeightBits!=heightBits || !_renderedVerbValues.SequenceEqual(orderedVerbs);
        if(rebuild)
        {
            ClearVerbHover();Verbs.Clear();
            foreach(uint verb in orderedVerbs)
                if(ResolveVerbAsset(verb) is {} asset)
                    Verbs.Add(new(verb,_target.GameId,(verb&0xffff)==0&&IsVerbExecutable(verb),asset));
            _renderedVerbValues=orderedVerbs.ToImmutableArray();_renderedVerbTargetId=_target.GameId;
            _renderedVerbWidthBits=widthBits;_renderedVerbHeightBits=heightBits;_verbControlsMaterialized=true;
        }
        _verbsVisible=Verbs.Count>0;
        double w=24*panelWidth/PanelWidth,h=24*panelHeight/PanelHeight;
        var rowSize=new EnbUiSize(Verbs.Count*w+Math.Max(0,Verbs.Count-1)*3,_verbsVisible?h:0);
        VerbBounds=new(Math.Max(0,panelLeft+panelWidth-rowSize.Width),Math.Max(0,panelTop-rowSize.Height-3),rowSize.Width,rowSize.Height);
        for(int i=0;i<Verbs.Count;i++)Verbs[i].Bounds=new(VerbBounds.X+i*(w+3),VerbBounds.Y,w,h);
        VerbLayoutChanged?.Invoke();PublishVerbs();
    }

    internal void HoverVerb(uint verb, Point pointer)
    {
        _hoveredVerb = verb;
        PublishVerbs();
        ShowRetailTooltip(100 + (int)(verb >> 16), pointer);
    }

    /// <summary>
    /// 00658430: every verb that queues its request ends at 00658A70 with SR_scifielectronic40.wav (Gate plays the same
    /// cue on its own branch); Message plays message_1.wav. Dock and Land return without it, and Follow on the own ship
    /// only reports "Cannot autofollow self.".
    /// </summary>
    private string? VerbSound(uint verb, uint targetId) => (verb & 0xFFFF0000u) switch
    {
        0x00020000 or 0x00080000 => null,
        0x00050000 => "message_1.wav",
        0x000D0000 when _snapshot?.Objects.FirstOrDefault(o => o.IsLocalShip)?.GameId == targetId => null,
        _ => "SR_scifielectronic40.wav",
    };

    private uint? _pressedVerb;
    private bool _pressedInside;

    /// <summary>
    /// 0066B910: a left press holds a verb button down (0066C2D0 gives its model 0066C860's .35 colour) and the verb runs
    /// on release over it; while held, leaving the button lets it up and coming back pushes it down again (0066B840).
    /// </summary>
    internal void PressVerb(uint verb) { _pressedVerb = verb; _pressedInside = true; PublishVerbs(); }
    internal void TrackPressedVerb(uint? over)
    {
        if (_pressedVerb is null || (over == _pressedVerb) == _pressedInside) return;
        _pressedInside = over == _pressedVerb; PublishVerbs();
    }
    /// <summary>Lets the held verb button up; returns the verb when the release was over it.</summary>
    internal uint? ReleaseVerb()
    {
        if (_pressedVerb is not { } verb) return null;
        bool inside = _pressedInside; _pressedVerb = null; _pressedInside = false; PublishVerbs();
        return inside ? verb : null;
    }

    internal void ClearVerbHover()
    {
        _hoveredVerb = null;
        if (_tooltipKind >= 100) HideScannerTooltip();
    }

    internal void PublishVerbs()
    {
        var buttons=ImmutableList.CreateBuilder<EnbUiModelDraw>();
        if(IsVisible&&_verbsVisible&&!Replaced)
            foreach(var button in Verbs)
                buttons.Add(new(button.Asset,button.Value,button.Bounds,_hoveredVerb==button.Value){LayoutGroup=EnbUiGroup.Scanner,GadgetOwner=button,
                    EmissiveOverride=_pressedInside&&_pressedVerb==button.Value?new System.Numerics.Vector3(.35f):null});
        VerbPresentationChanged?.Invoke(buttons.ToImmutable());
    }

    public bool ContainsFlightBlockingControl(Point point)
        => !Replaced&&(PanelBounds.Contains(point)||(_verbsVisible&&(UiLayout?.Apply(EnbUiGroup.Scanner,VerbBounds)??VerbBounds).Contains(point)));

    private void InvalidateVerbControls()
    {
        ClearVerbHover();
        VerbPresentationChanged?.Invoke([]);
        Verbs.Clear();VerbLayoutChanged?.Invoke();
        _verbsVisible = false;
        _renderedVerbValues = ImmutableArray<uint>.Empty;
        _renderedVerbTargetId = null;
        _renderedVerbWidthBits = 0;
        _renderedVerbHeightBits = 0;
        _verbControlsMaterialized = false;
    }

    /// <summary>A verb's button art (Next multibox: a follower's Harvest button in the leader's target frame).</summary>
    internal EnbAuthoredUiAsset? VerbAsset(uint verb) => ResolveVerbAsset(verb);
    private EnbAuthoredUiAsset? ResolveVerbAsset(uint verb)
    {
        uint kind = verb & 0xFFFF0000u;
        if (_verbAssets.TryGetValue(kind, out var cached))
            return cached;
        string? resource = kind switch
        {
            0x00010000 => "vb_scan.w3d",
            0x00020000 => "vb_land.w3d",
            0x00030000 => "vb_loot.w3d",
            0x00040000 => "vb_group.w3d",
            0x00050000 => "vb_message.w3d",
            0x00060000 => "vb_trade.w3d",
            0x00070000 => "vb_tractor.w3d",
            0x00080000 => "vb_dock.w3d",
            0x00090000 => "vb_prospect.w3d",
            0x000A0000 => "vb_gate.w3d",
            0x000B0000 => "vb_register.w3d",
            0x000C0000 => "vb_jumpstart.w3d",
            0x000D0000 => "vb_follow.w3d",
            _ => "vb_def.w3d",
        };
        if (_assets is null || resource is null ||
            EnbAuthoredUiAsset.Load(_assets, resource) is not { } asset)
        {
            return null;
        }
        _verbAssets.Add(kind, asset);
        return asset;
    }

    private async void SelectAdjacent(bool previous, bool preserveCurrentCategory)
    {
        if (_radar is null || _snapshot is null)
            return;
        // ApplyLocalTargetSelection updates _target synchronously. Use that
        // local cursor so repeated arrow clicks can continue through the list
        // without waiting for snapshot/network reconciliation of the previous
        // click (fixed navigation previews can take more than one UI tick).
        uint? current = _target?.GameId ?? (_snapshot.Hud.HasLocalTargetSelection
            ? _snapshot.Hud.LocalSelectedTargetId
            : _snapshot.Hud.CurrentTargetId);
        uint? adjacent = EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            _radar, current, previous, preserveCurrentCategory);
        if (adjacent is uint id)
        {
            try
            {
                await _targetRequester(id, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // A sector transition can retire gameplay authority between
                // the visible scanner frame and this click. The keyboard and
                // gutter target paths treat the same transition as stale input.
            }
        }
    }

    /// <summary>0060E300 commands 6/7: the arrows pick the previous/next target of the current context, or any target
    /// while the right button is held (input mask 70); ButtonClick02.wav (SCAN-D07).</summary>
    public void PressArrow(bool previous, bool unfiltered = false)
    { HideScannerTooltip();InteractionSoundRequested?.Invoke("ButtonClick02.wav");SelectAdjacent(previous, !unfiltered); }

    /// <summary>0060E300 wheel codes 101 (up: next) / 102 (down: previous) over the scanner; Alt (mask 800) picks any target.</summary>
    public void WheelArrow(bool up, bool alt)
    { HideScannerTooltip();InteractionSoundRequested?.Invoke("ButtonClick02.wav");SelectAdjacent(!up, !alt); }

    internal void ShowScannerTooltip(bool previous, Point pointer)
        => ShowRetailTooltip(previous ? 1 : 2, pointer);

    private bool _tooltipHoverActive;
    public void SetImmediateTooltips(bool value)
    {
        _tooltipDwell.Immediate = value;
        if (_tooltipHoverActive) ShowRetailTooltip(_tooltipKind, _tooltipPointer);
    }
    internal void ShowRetailTooltip(int kind, Point pointer)
    {
        if (_tooltipFont is null || _tooltipDefinition is not { } definition || _radar is null || _snapshot is null)
            return;
        _tooltipKind = kind;
        _tooltipHoverActive = true;
        _tooltipPointer = pointer;
        if (!_tooltipDwell.IsReady(kind, Environment.TickCount64, _tooltipDelayMilliseconds))
        {
            _scannerTooltip = null;
            TooltipPresentationChanged?.Invoke(null);
            TooltipTimerChanged?.Invoke(true);
            return;
        }
        TooltipTimerChanged?.Invoke(false);
        uint? current = _target?.GameId ?? (_snapshot.Hud.HasLocalTargetSelection
            ? _snapshot.Hud.LocalSelectedTargetId
            : _snapshot.Hud.CurrentTargetId);
        int count = EnbCurrentRetailTargetingRuntime.CountCandidates(
            _radar, current, preserveCurrentCategory: false);
        string text;
        bool disabledVerb = false;
        if (kind >= 100)
        {
            if (_hoveredVerb is not uint verb) { HideScannerTooltip(); return; }
            text = definition.VerbText.Replace("%s",EnbCurrentRetailVerbPresentation.Format(verb));
            disabledVerb = !EnbCurrentRetailVerbPresentation.IsEnabled(verb);
        }
        else if (kind is 3 or 4)
        {
            var vitals = _target?.TargetVitals;
            float? maximum = kind == 3 ? vitals?.MaximumHullPoints : vitals?.MaximumShieldPower;
            float? value = kind == 3 ? vitals?.HullPoints : vitals?.ShieldPercent is float percent && maximum is float max
                ? percent*max : null;
            text = definition.FormatGauge(kind == 3,value,maximum);
        }
        else if(kind is 5 or 6)
        {
            if(!_interruption.Visible){HideScannerTooltip();return;}
            float value=kind==5?(_target?.Interruption?.Progress??0):_interruption.ActivationProgress;
            text=(kind==5?definition.InterruptProgressText:definition.ActivationProgressText)
                .Replace("%d",((int)(value*100)).ToString(CultureInfo.InvariantCulture)).Replace("%%","%");
        }
        else text = (kind == 1 ? definition.PreviousText : definition.NextText)
            .Replace("%d", count.ToString(CultureInfo.InvariantCulture));
        if(_tooltipFrame is not {} frame)return;
        var presentation=EnbTooltipContent.Create(frame,_tooltipFont,text,new(_logicalWidth,_logicalHeight),
            _renderScaling,disabledVerb?Red:Green,pointer,i=>
            {
                if(i==0)return _scannerTooltipTitle;
                if(i==1)return _scannerTooltipDetail;
                while(_scannerTooltipExtra.Count<=i-2)_scannerTooltipExtra.Add(new());
                return _scannerTooltipExtra[i-2];
            });
        foreach(var part in new[]{_scannerTooltipTitle,_scannerTooltipDetail}.Concat(_scannerTooltipExtra))
            if(!presentation.Lines.Any(line=>ReferenceEquals(line.Raster,part.Raster)))part.Clear();
        _scannerTooltip=presentation;
        TooltipPresentationChanged?.Invoke(presentation);
    }

    internal void HideScannerTooltip()
    {
        _tooltipHoverActive = false;
        TooltipTimerChanged?.Invoke(false);
        _tooltipDwell.Reset();
        _scannerTooltip = null;
        TooltipPresentationChanged?.Invoke(null);
    }

    public bool PressPanel(Point position,int button)
    {
        if(_target is null)return false;
        bool topInformation=position.X>=11&&position.X<=127&&position.Y>=10&&position.Y<=23;
        bool bottomInformation=position.X>=3&&position.X<=96&&position.Y>=123&&position.Y<=136;
        bool scanner=position.X>=3.048&&position.X<=125.952&&position.Y>=7.048&&position.Y<=130.952;
        if(button==2&&topInformation){CycleInformationField(true,true);return true;}
        if(button==2&&bottomInformation){CycleInformationField(false,true);return true;}
        if(button==2&&scanner){InteractionSoundRequested?.Invoke("TextClick01.wav");ClearTarget();return true;}
        //005BC5E0 commands 1..3 take both buttons in the same zones; bit 0x10
        //(right) only selects clear-target/previous instead of next.
        if(button!=1)return false;
        if(topInformation)CycleInformationField(true,false);
        else if(bottomInformation)CycleInformationField(false,false);
        else if(scanner)
        {
            InteractionSoundRequested?.Invoke("TextClick01.wav");
            if(_target.ObjectType==0x26&&(_snapshot?.Hud.ProspectSkillLevel??0)!=0)ToggleHarvestContents();
        }
        UpdateText();return true;
    }
    private async void ClearTarget()=>await _targetRequester(0,CancellationToken.None);


    internal void ToggleHarvestContents()
    {
        // 005BF790 (page 005C3650, available while its contents panel exists): type '&' (006F5E20)
        // and a non-zero RPGInfo.Skills Prospect level.
        if (_target?.ObjectType != 0x26 || (_snapshot?.Hud.ProspectSkillLevel ?? 0) == 0)return;
        {
            _showHarvestContents = !_showHarvestContents;
        }
        UpdateText();
    }

    private void CycleInformationField(bool top, bool previous)
    {
        if (_target is null) return;
        // 5BC5E0 commands 2/3: every admitted click cues, invalidates the
        // selected cache, clears its text, and forces the replacement value.
        InteractionSoundRequested?.Invoke("TextClick01.wav");
        int current = top ? _topInformationField : _bottomInformationField;
        int selected = previous ? PreviousAvailableInformationField(current) : NextAvailableInformationField(current);
        if (top)
        {
            _topInformationField = selected;
            _topRetainedReadout = null;
            _topReadoutScroll.Clear();
        }
        else
        {
            _bottomInformationField = selected;
            _bottomRetainedReadout = null;
            _bottomReadoutScroll.Clear();
        }
        UpdateText();
    }

    private int NextAvailableInformationField(int current)
    {
        for (int offset = 1; offset <= 16; offset++)
        {
            int candidate = (current + offset) & 0x0F;
            if (!string.IsNullOrEmpty(ResolveInformationField(candidate)))
                return candidate;
        }
        return current;
    }

    private int PreviousAvailableInformationField(int current)
    {
        for (int offset = 1; offset <= 16; offset++)
        {
            int candidate = (current - offset) & 0x0F;
            if (!string.IsNullOrEmpty(ResolveInformationField(candidate)))
                return candidate;
        }
        return current;
    }

    private void ResetInformationPages()
    {
        _topRetainedReadout=null;_bottomRetainedReadout=null;
        _topReadoutScroll.Clear();_bottomReadoutScroll.Clear();
        // Scanner subtype 5BECF0, installed by 5BE980 -> 60D800.
        // The player-MFD subtype uses a separate velocity/heading default.
        bool supported = _target?.ObjectType is 0 or 1 or 2 or 3 or 4 or 9 or 10 or 11 or 12 or 25 or 37 or 38;
        _topInformationField = supported ? 1 : 0;
        _bottomInformationField = supported ? 3 : 0;
    }

    private void ClearText()
    {
        _subtitle.IsVisible=false;
        _topRetainedReadout=null;_bottomRetainedReadout=null;
        _factionIdentity=null;_factionText=null;_factionScroll.Clear();
        _interruptText.Clear();_interruptScroll.Clear();
        _topReadoutScroll.Clear();_bottomReadoutScroll.Clear();
        foreach (EnbScannerText line in new[] { _name, _faction, _info0, _info1 }.Concat(_resourceLines))
            line.Clear();
    }

    private void SetCentered(EnbScannerText control, string? text, EnbRgb color, double top)
    {
        control.SetText(text, _font, color);
        ScaleTextToNativeDisplayPixels(control);
        control.Left= (PanelWidth - control.Width) * 0.5;
        control.Top= top;
    }

    private void SetLeft(EnbScannerText control, string? text, EnbRgb color, double left, double top)
    {
        control.SetText(text, _font, color);
        ScaleTextToNativeDisplayPixels(control);
        control.Left= left;
        control.Top= top;
    }

    private void SetAttachedLeft(
        EnbScannerText control,
        string? text,
        EnbRgb color,
        double anchorX,
        double anchorY)
    {
        var scroll=ReferenceEquals(control,_info0)?_bottomReadoutScroll:_topReadoutScroll;
        bool nameField=ReferenceEquals(control,_info0)?_bottomInformationField==1:_topInformationField==1;
        // 005BCE50's string branch caches/renders at most 63 characters. An
        // overlength source differs from that cache on every update.
        if(nameField && text is {Length:>63}){text=text[..63];scroll.Clear();}
        control.SetText(text, _font, color);
        ScaleTextToNativeDisplayPixels(control);
        var style=ReferenceEquals(control,_info0)?_bottomReadoutStyle:_topReadoutStyle;
        if(style is not null)
        {
            control.FitTo(style.Clip.Size,_renderScaling);
            float offset=scroll.Update(text??"",Environment.TickCount64,(float)control.Width,style.HalfWidth,style.ScrollSpeed);
            anchorX=style.Anchor.X-PanelLeft+offset;
            anchorY=style.Anchor.Y-PanelTop;
            double top=anchorY-control.Height*.5;
            control.Clip=style.Clip.Translate(-PanelLeft-anchorX,-PanelTop-top);
        }
        control.Left= anchorX;
        control.Top= anchorY - control.Height * 0.5;
    }

    private void UpdateAssessmentColor()
    {
        if(_target is null){_assessmentColorIdentity=null;return;}
        // 60E580 selects; 6100C0 subscribes only to B88C00 relationship
        // notifications (60ED10). Group/PvP snapshots alone do not refresh it.
        if(_assessmentColorIdentity==_target.RenderIdentity &&
            _assessmentColorRevision==_target.RelationshipNotificationRevision)return;
        _assessmentColorIdentity=_target.RenderIdentity;
        _assessmentColorRevision=_target.RelationshipNotificationRevision;
        bool group=_target.ObjectType==1 && _snapshot?.Hud.GroupMemberIds.Contains(_target.GameId)==true;
        bool pvp=_target.ObjectType==1 && !_target.IsLocalShip && !group && _target.PlayerPresentation?.IsInPvp==true &&
            _snapshot?.Objects.FirstOrDefault(o=>o.IsLocalShip)?.PlayerPresentation?.IsInPvp==true;
        string category=EnbCurrentRetailGutterColorRuntime.ResolveCategory(_target.ObjectType,
            _target.RelationshipRaw,_target.IsAttacking,group,pvp);
        // 60EFE0 copies the first RGB, not the animated target sample.
        // 70A010/70A2E0 bind that slot to Target1; missing entries use .66.
        _assessmentColor=_assets?.GutterColors?.TryResolve(category,"Target1",out var color)==true
            ?color:new EnbRgb(.66f,.66f,.66f);
    }

    private void SetAssessmentText(string? text)
    {
        if(_assessmentStyle is not {} style || string.IsNullOrWhiteSpace(text) || _font is null)
        {_subtitle.IsVisible=false;return;}
        // 0060EFE0 changes both color endpoints without invalidating geometry.
        // 006CDBC0/006D64C0 retain the rendered color until the text/font rebuilds.
        // Hiding or selecting another object with the same assessment retains it.
        if(_subtitle.Raster is null || _subtitle.Text!=text || !ReferenceEquals(_subtitle.Font,_font))
            _subtitle.SetText(text,_font,_assessmentColor);
        _subtitle.IsVisible=true;
        ScaleTextToNativeDisplayPixels(_subtitle);
        _subtitle.FitTo(style.Clip.Size,_renderScaling);
        double x=style.Anchor.X-PanelLeft-_subtitle.Width*.5;
        double y=style.Anchor.Y-PanelTop-_subtitle.Height*.5;
        _subtitle.Clip=style.Clip.Translate(-PanelLeft-x,-PanelTop-y);
        _subtitle.Left=x;_subtitle.Top=y;
    }

    private static string DisplayName(IEnbRenderObject target)
        => EnbRetailObjectDisplayName.Resolve(target);



    private void ScaleTextToNativeDisplayPixels(EnbScannerText control)
        => control.SetDisplayScale(
            DesignWidth / _logicalWidth,
            DesignHeight / _logicalHeight);



    private static double Ratio(float? value, float? maximum)
        => value is float current && maximum is float max && float.IsFinite(current) && float.IsFinite(max) && max >= 1f
            ? Math.Clamp(current / max, 0f, 1f)
            : 0;

    private (float Local,float Target) ResolveReadoutRadii(IEnbRenderObject local)
    {
        float localRadius = _radar?.ObserverRenderIdentity == local.RenderIdentity
            ? _radar.ObserverWorldRadius : EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(local);
        float targetRadius = _radar?.Contacts.FirstOrDefault(item => item.RenderIdentity == _target!.RenderIdentity)?.WorldRadius
            ?? EnbCurrentRetailRadarRenderRangeRuntime.ResolveWorldRadius(_target!);
        return (localRadius,targetRadius);
    }

    internal static string VerbName(uint verb) => (verb & 0xFFFF0000u) switch
    {
        0x00010000 => "Scan", 0x00020000 => "Land", 0x00030000 => "Loot",
        0x00040000 => "Group", 0x00050000 => "Message", 0x00060000 => "Trade",
        0x00070000 => "Tractor", 0x00080000 => "Dock", 0x00090000 => "Prospect",
        0x000A0000 => "Gate", 0x000B0000 => "Register", 0x000C0000 => "Jumpstart",
        0x000D0000 => "Follow", _ => "Action",
    };

    private bool IsVerbExecutable(uint verb)
        => (verb & 0xFFFF0000u) switch
        {
            0 => false,
            // 00744E70: NPC Message sends Action 2; a player target opens private chat to them.
            0x00050000 => _target is not null,
            _ => true,
        };
}

internal sealed class EnbScannerText : EnbRetailText
{
    public double Left,Top;
    public EnbUiRect? Clip;
}
