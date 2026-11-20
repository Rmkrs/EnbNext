using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public sealed record EnbLoadingDraw(EnbAuthoredUiAsset Asset,
    ImmutableArray<EnbUiTextDraw> Text, bool ShowLocations,
    ImmutableArray<EnbLoadingMarker> Markers, EnbUiSize LogicalSize);
public sealed record EnbLoadingMarker(EnbAuthoredUiAsset Asset, Vector3 Position);

/// <summary>Installed LoadingView artwork and font attachments; no scene readback or fabricated map.</summary>
public sealed class EnbLoadingPanel : IDisposable
{
    private EnbRetailAssetBundle? _assets;
    private EnbAuthoredUiAsset? _asset;
    private EnbAuthoredUiAsset? _select;
    private EnbAuthoredUiAsset? _from;
    private EnbInterfaceLayoutCatalog? _layout;
    private readonly EnbRetailText[] _lines = [new(), new(), new()];
    private readonly Dictionary<string, EnbRetailFontFace> _fonts = new();
    public string? Error { get; private set; }

    public EnbLoadingDraw? Build(EnbRetailAssetBundle? assets, EnbUiSize size, double scaling,
        EnbSceneLocationTransition? transition, long elapsedMilliseconds)
    {
        if (assets is null || size.Width < 1 || size.Height < 1) return null;
        if (!ReferenceEquals(_assets, assets))
        {
            Dispose(); _assets = assets; Error = null;
            try
            {
                _layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_map_load00.ini"));
                _asset = EnbAuthoredUiAsset.Load(assets,"map_load00.w3d");
                _select = EnbAuthoredUiAsset.Load(assets,"map_select.w3d");
                _from = EnbAuthoredUiAsset.Load(assets,"map_from.w3d");
                if (_asset is null) Error = "Installed map_load00.w3d is unavailable.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { Error = ex.Message; }
        }
        if (_asset is null || _layout is null) return null;
        var text = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        string[] ids = ["Source_Text_map_load00", "Dest_Text_map_load00", "Loading_Text_map_load00"];
        for(int i=0;i<ids.Length;i++)
        {
            if (!_layout.TryResolve(ids[i],out var definition)) continue;
            var props = definition.Properties;
            string value = i == 0 ? transition?.SourceText ?? "" : i == 1 ? transition?.DestinationText ?? ""
                : props.GetValueOrDefault("String0", "");
            // Retail's update (00692420) cycles one through five dots once per second.
            if (i==2 && elapsedMilliseconds>1000)
                value = value.TrimEnd('.') + new string('.', 1+(int)((elapsedMilliseconds-1001)/1001%5));
            if (!EnbRetailFontFactory.TryResolveDefinition(assets,props["FontType"],
                    (int)(size.Width*scaling),(int)(size.Height*scaling),out var fontDefinition) ||
                fontDefinition.Resource is null) continue;
            if (!_fonts.TryGetValue(fontDefinition.Resource,out var font))
            {
                if (!EnbRetailFontFactory.TryCreate(assets,fontDefinition,out font)) continue;
                _fonts.Add(fontDefinition.Resource,font);
            }
            var line = _lines[i];
            line.SetText(value,font,new EnbRgb(1,1,1));
            line.SetDisplayScale(1,1);
            if (line.Raster is not { } raster) continue;
            var pivot = _asset.Hierarchy.Pivots.First(p=>string.Equals(p.Name,props["Attach"],StringComparison.OrdinalIgnoreCase));
            EnbAuthoredTargetPanelLoader.TryResolveTranslation(_asset.Hierarchy,pivot.Ordinal,out var anchor);
            text.Add(new(raster,(new EnbUiRect((anchor.X+320)*size.Width/640,
                (240-anchor.Y)*size.Height/480-line.Height/2,line.Width,line.Height))));
        }
        var markers=ImmutableArray.CreateBuilder<EnbLoadingMarker>();
        // 00691B77..00691CBD: destination marker first; suppress the origin
        // marker when both names resolve to the same authored system bone.
        var destination=SystemPosition(transition?.DestinationSystemName);
        var source=SystemPosition(transition?.SourceSystemName);
        if(destination is { } dest && _select is not null) markers.Add(new(_select,dest));
        if(source is { } origin && source!=destination && _from is not null) markers.Add(new(_from,origin));
        return new(_asset,text.ToImmutable(), transition is not null &&
            (transition.SourceSectorName.Length+transition.SourceSystemName.Length+
             transition.DestinationSectorName.Length+transition.DestinationSystemName.Length)>0,markers.ToImmutable(),size);

        Vector3? SystemPosition(string? name)
        {
            if(string.IsNullOrEmpty(name)) return null;
            var bone=_asset.Hierarchy.Pivots.FirstOrDefault(p=>string.Equals(p.Name,name.Replace(' ','_'),StringComparison.OrdinalIgnoreCase));
            return bone is not null && EnbAuthoredTargetPanelLoader.TryResolveTranslation(_asset.Hierarchy,bone.Ordinal,out var position)
                ? position : null;
        }
    }

    public void Dispose()
    {
        foreach(var line in _lines) line.Clear();
        _fonts.Clear(); _assets=null; _asset=null; _select=null; _from=null; _layout=null;
    }
}
