using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbMapConnection(Vector2 From,Vector2 To,EnbUiRaster Texture,float Width,float Repeat,float Offset);

public sealed record EnbMapDraw(EnbAuthoredMapFrame Frame, float AnimationFrame, bool Maximized,
    EnbUiSize Size, string? Hovered, ImmutableArray<EnbUiTextDraw> Images)
{
    public ImmutableArray<EnbW3dDrawTask> Packets { get; init; } = Frame.Packets;
    public ImmutableArray<EnbMapMarker> Markers { get; init; } = [];
    public ImmutableArray<EnbUiTextDraw> ContentImages { get; init; } = [];
    /// <summary>Next-only shading drawn first inside the content clip.</summary>
    public ImmutableArray<EnbUiTextDraw> UnderlayImages { get; init; } = [];
    public EnbUiRect ContentBounds { get; init; }
    public EnbUiTooltipDraw? Help { get; init; }
    public ImmutableArray<Vector2> Route { get; init; } = [];
    public ImmutableArray<Vector2> WarpRoute { get; init; } = [];
    public IEnbSceneFrame? ModelScene { get; init; }
    public ImmutableDictionary<uint,Vector2> ModelPositions { get; init; } = ImmutableDictionary<uint,Vector2>.Empty;
    public Vector2 WorldCenter { get; init; }
    public float WorldScale { get; init; }
    public (uint? Session,ulong? Ship,uint? Sector) ModelOwner { get; init; }
    public ImmutableArray<EnbUiTextDraw> BackgroundImages { get; init; } = [];
    public ImmutableArray<EnbMapConnection> Connections { get; init; } = [];
    public int BackgroundMarkerCount { get; init; }
    public EnbResolvedRenderCamera? GalaxyCamera { get; init; }
    public ImmutableArray<EnbMapQuad> GalaxyBackgrounds { get; init; } = [];
    public ImmutableArray<EnbMapQuad> GalaxyLinks { get; init; } = [];
    public ImmutableArray<EnbMapQuad> GalaxyLabels { get; init; } = [];
    /// <summary>Next Modern UI map: its window (frame, title, legend), drawn with the Next panels just under the map.</summary>
    public ImmutableArray<EnbUiModelDraw> Chrome { get; init; } = [];
    /// <summary>Next Modern UI map: the legend's marker symbols, drawn over the window outside the map area.</summary>
    public ImmutableArray<EnbMapMarker> LegendMarkers { get; init; } = [];
    /// <summary>Next Modern UI: the sector and galaxy map windows draw in this order (the one used last on top).</summary>
    public long Order { get; init; }
}
