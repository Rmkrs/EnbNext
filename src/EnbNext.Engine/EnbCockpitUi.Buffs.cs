using Point = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbCockpitUi
{
    private static readonly string[] BuffHitNames = Enumerable.Range(0,15).Select(i => $"BUFF{i:00}").ToArray();
    private EnbRetailBuffCatalog? _buffDefinitions;
    private EnbAuthoredUiAsset? _buffFrame;
    private Matrix4x4[] _buffAnchors=[];
    private readonly Dictionary<string, EnbAuthoredUiAsset?> _buffAssets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _buffTooltips = [];
    private readonly Dictionary<string,string> _buffDescriptions=[];
    private string? _expandedBuff;
    private readonly EnbRetailText[] _buffCounts = Enumerable.Range(0,15).Select(_ => new EnbRetailText()).ToArray();
    private string? _buffCountFont;
    private EnbRgb? _buffCountColor;
    private readonly Dictionary<(EnbAuthoredUiAsset Asset,int Slot), (Matrix4x4 Attachment,Rect Bounds,Vector2[] Points,IReadOnlyList<uint> Indices)> _buffGeometry = [];
    public string? BuffError { get; private set; }
    public int VisibleBuffCount { get; private set; }
    public event Action<string>? BuffSoundRequested;
    private readonly Dictionary<int,(string Type,EnbRetailBuffVisual Visual)> _buffVisuals=[];
    internal Func<uint> BuffFrameTime {get;set;}=()=>unchecked((uint)Environment.TickCount64);
    private uint? _buffPreviousFrame;
    private float _buffHoverOpacity=1;
    internal void PauseBuffFrame()=>_buffPreviousFrame=BuffFrameTime();
    private void CaptureBuffHover(string name)
    {
        if(name.StartsWith("BUFF",StringComparison.Ordinal) && int.TryParse(name.AsSpan(4),out int slot) &&
            _buffVisuals.TryGetValue(slot,out var entry))_buffHoverOpacity=entry.Visual.Opacity;
    }

    private void LoadBuffs(EnbRetailAssetBundle assets)
    {
        try
        {
            _buffDefinitions = EnbRetailBuffCatalog.Load(assets.Paths.ClientIniDirectory);
            _buffFrame = EnbAuthoredUiAsset.Load(assets, "tsi02a.w3d")
                ?? throw new InvalidDataException("Installed TSI02A buff frame unavailable.");
            _buffAnchors = ResolveTransforms(_buffFrame);
            var labels = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fa_buff.ini"));
            if (labels.TryResolve("BuffCount", out var count))
            {
                _buffCountFont = count.Properties.GetValueOrDefault("FontType");
                var colors = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
                foreach (string section in colors.SectionNames)
                    if (section.StartsWith("COLOR-",StringComparison.OrdinalIgnoreCase) && colors.TryGetSection(section,out var color) &&
                        color.GetValueOrDefault("ColorID") == count.Properties.GetValueOrDefault("Color") &&
                        color.GetValueOrDefault("RGB")?.Split(',') is { Length:3 } rgb &&
                        byte.TryParse(rgb[0],out byte red) && byte.TryParse(rgb[1],out byte green) && byte.TryParse(rgb[2],out byte blue))
                        _buffCountColor = new(red/255f,green/255f,blue/255f);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { BuffError = ex.Message; _buffFrame = null; }
    }

    private EnbBuffDraw? BuildBuffs(ImmutableArray<EnbSceneBuffSnapshot> states, long time)
    {
        uint frame=BuffFrameTime();
        uint delta=_interactive && _buffPreviousFrame is uint previous?unchecked(frame-previous):0;
        _buffPreviousFrame=frame;
        if(!_interactive)return null;
        var admitted=states.Where(s=>s.Slot is >=0 and <15 && s.IsVisible(time)).Select(s=>s.Slot).ToHashSet();
        foreach(int slot in _buffVisuals.Keys.Where(s=>!admitted.Contains(s)).ToArray())_buffVisuals.Remove(slot);
        foreach (string name in BuffHitNames) { _hitBounds.Remove(name); _hitGeometry.Remove(name); }
        _buffTooltips.Clear();_buffDescriptions.Clear(); VisibleBuffCount = 0;
        if (_buffFrame is null || _buffDefinitions is null || _assets is null) return null;
        var icons = ImmutableArray.CreateBuilder<EnbBuffIcon>();
        var counts = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        foreach (var state in states)
        {
            if (!state.IsVisible(time) || state.Slot is < 0 or >= 15) continue;
            if (!_buffDefinitions.Definitions.TryGetValue(state.Type, out var definition))
            { BuffError = "Unknown installed buff definition: " + state.Type; continue; }
            if (!_buffAssets.TryGetValue(definition.Asset, out var asset))
            {
                try { asset = EnbAuthoredUiAsset.Load(_assets, definition.Asset + ".w3d"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                { BuffError = ex.Message; }
                _buffAssets[definition.Asset] = asset;
            }
            if (asset is null) { BuffError = "Installed buff asset unavailable: " + definition.Asset; continue; }
            bool renewed;
            bool replaced=false;
            if(!_buffVisuals.TryGetValue(state.Slot,out var retained) || retained.Type!=state.Type)
            {
                if(_expandedBuff==BuffHitNames[state.Slot])_expandedBuff=null;
                retained=(state.Type,new(state.IsPermanent,state.RemovalTime,state.Elements.Length));
                _buffVisuals[state.Slot]=retained;
                renewed=true;
                replaced=true;
            }
            else renewed=retained.Visual.Observe(state.IsPermanent,state.RemovalTime,state.Elements.Length,unchecked((uint)time));
            if(renewed)BuffSoundRequested?.Invoke(definition.IsGood?"click1c.wav":"cost.wav");
            if(_interactive)retained.Visual.Advance(unchecked((uint)time),delta);
            string name = BuffHitNames[state.Slot];
            //561030 destroys/reconstructs on type replacement, even for a shared
            //BuffAsset. 6603A0 clones the replacement's current material alpha.
            if(replaced && _hovered==name)_buffHoverOpacity=retained.Visual.Opacity;
            if (!_buffGeometry.TryGetValue((asset,state.Slot),out var geometry))
            {
                var anchor = _buffFrame.Hierarchy.Pivots.FirstOrDefault(p => p.Name == name);
                var dock = asset.Resource.Meshes.FirstOrDefault(m => m.Name == "DOCK");
                var pivot = asset.Hierarchy.Pivots.FirstOrDefault(p => p.Name == "DOCK");
                if (anchor is null || dock is null || pivot is null) { BuffError = "Buff anchor/hit geometry unavailable: " + name; continue; }
                Matrix4x4 attachment = _buffAnchors[anchor.Ordinal];
                var transform = ResolveTransforms(asset)[pivot.Ordinal] * attachment;
                var points = dock.Positions.Select(p => { var v = Vector3.Transform(p,transform); return new Vector2(v.X+320,240-v.Y); }).ToArray();
                var min = points.Aggregate(Vector2.Min); var max = points.Aggregate(Vector2.Max);
                geometry = (attachment,new(min.X,min.Y,max.X-min.X,max.Y-min.Y),points,dock.Indices);
                _buffGeometry[(asset,state.Slot)] = geometry;
            }
            _hitBounds[name] = geometry.Bounds;
            _hitGeometry[name] = (geometry.Points,geometry.Indices);
            _buffTooltips[name] = state.Tooltip(definition);
            _buffDescriptions[name]=definition.Description;
            icons.Add(new(state.Slot,asset,geometry.Attachment,_interactive && _hovered == name)
                {Opacity=retained.Visual.Opacity,HighlightOpacity=_buffHoverOpacity,GadgetOwner=retained.Visual});
            // 006661F0 counts all occupied source elements, not only active contributors.
            var line = _buffCounts[state.Slot];
            if (state.Elements.Length > 1 && _buffCountColor is { } countColor && _buffCountFont is { } fontId && Font(fontId) is { } font)
            {
                line.SetText(state.Elements.Length.ToString(CultureInfo.InvariantCulture),font,countColor);
                line.SetDisplayScale(1,1);
                if (line.Raster is { } raster) counts.Add(new(raster,new(
                    (geometry.Attachment.Translation.X+320)*_size.Width/640-line.Width/2,
                    (240-geometry.Attachment.Translation.Y)*_size.Height/480-line.Height/2,line.Width,line.Height)));
            }
            else line.Clear();
        }
        if (_hovered is not null && _hovered.StartsWith("BUFF",StringComparison.Ordinal) && !_buffTooltips.ContainsKey(_hovered)) ClearHover();
        VisibleBuffCount = icons.Count;
        return icons.Count == 0 ? null : new(_buffFrame,icons.ToImmutable(),counts.ToImmutable());
    }

    //561030 observes the left-press latch, forces the current tooltip, then
    //6664E0 selects BuffAltToolTip until the pointer leaves this widget.
    private void OpenBuffDescription(string name,Point pointer)
    {
        if(!_interactive || !_buffDescriptions.ContainsKey(name) || !Contains(name,pointer))return;
        Hover(name,pointer);_expandedBuff=name;ShowTooltip();
    }
    private string? BuffTooltip(string name) => _expandedBuff==name
        ?_buffDescriptions.GetValueOrDefault(name):_buffTooltips.GetValueOrDefault(name);

    private void DisposeBuffs()
    {
        _layoutBuffPreview=null;_buffDefinitions = null; _buffFrame = null; _buffAnchors = []; _buffAssets.Clear(); _buffTooltips.Clear();
        _buffCountFont = null; _buffCountColor = null; _buffGeometry.Clear(); BuffError = null; VisibleBuffCount = 0;
        _buffVisuals.Clear();_buffPreviousFrame=null;
        _buffDescriptions.Clear();_expandedBuff=null;
        foreach (var line in _buffCounts) line.Clear();
    }
}
