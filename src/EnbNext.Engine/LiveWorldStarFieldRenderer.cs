using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>
/// Current-retail point and quad star renderer. The selected stars.ini layers are
/// generated from their authored five-dimensional QMC sequence, clipped by the
/// already-closed StarBudget policy, expanded through PointGroup's rotated
/// triangle/quad atlas tables, and transformed with camera translation removed.
/// EnbWorldRenderer draws these static triangles in the shared OpenGL framebuffer
/// with ONE/ONE blending, matching current retail's proven PointGroup state.
/// </summary>
internal sealed class LiveWorldStarFieldRenderer : IDisposable
{
    private readonly EnbRetailStarLayerSelection _selection;
    private readonly EnbStarBudgetSelection _budget;
    private readonly LayerRuntime[] _layers;
    private LiveWorldStarFieldRenderer(
        EnbRetailStarLayerSelection selection,
        EnbStarBudgetSelection budget,
        LayerRuntime[] layers)
    {
        _selection = selection;
        _budget = budget;
        _layers = layers;
        Geometry = layers.Select(BuildGeometry).ToImmutableArray();
    }

    public ImmutableArray<EnbBackdropGeometry> Geometry { get; }

    public string Summary
    {
        get
        {
            string selected = _selection.SelectedStarSetId is int selectedStarSetId
                ? selectedStarSetId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "Default";
            string visible = string.Join(
                "+",
                _budget.LayerAllocations.Select(static allocation => allocation.VisibleStarCount));
            string textures = string.Join(
                ",",
                _layers.Select(static layer => layer.ResolvedTextureName).Distinct(StringComparer.OrdinalIgnoreCase));
            string offsets = string.Join(
                "/",
                _budget.LayerAllocations.Select(static allocation => allocation.Layer.QmcOffset));

            return
                $"R6Y_LIVE_STAR_FIELD_ACTIVE · set {_selection.RequestedStarSetId} → {_selection.Family} {selected} · " +
                $"layers {_budget.VisitedLayerCount} · visible {visible}={_budget.VisibleStarCount}/{_budget.TotalConfiguredCost} cost · " +
                $"{textures} · QMC offsets {offsets} · GPU point/quad triangles · additive ONE/ONE · camera-translation invariant";
        }
    }

    public static bool TryCreate(
        EnbRetailAssetBundle assets,
        EnbRetailStarLayerSelection selection,
        int starBudget,
        out LiveWorldStarFieldRenderer renderer,
        out string? diagnostic)
        => TryCreateCore(assets,selection,starBudget,null,out renderer,out diagnostic);

    public static bool TryCreateForMovie(EnbRetailAssetBundle assets, EnbRetailMovieAssets movie,
        out LiveWorldStarFieldRenderer renderer, out string? diagnostic)
        => TryCreateCore(assets,movie.Stars!,movie.StarConstructorArgument,movie.StarTextures,out renderer,out diagnostic);

    private static bool TryCreateCore(EnbRetailAssetBundle assets,EnbRetailStarLayerSelection selection,
        int starBudget,IReadOnlyList<EnbCurrentRetailTextureRegistry.Lease?>? retainedTextures,
        out LiveWorldStarFieldRenderer renderer,out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(selection);
        renderer = default!;
        diagnostic = null;

        if (selection.Layers.Count == 0)
        {
            diagnostic = $"star-set {selection.RequestedStarSetId} resolves no current-retail stars.ini layers.";
            return false;
        }

        EnbStarBudgetSelection budget = EnbStarBudgetSemantics.Allocate(selection.Layers, starBudget);
        // 004829E0 constructs every layer before 00481B70 applies a draw
        // budget. Even an invisible layer retains its shared TextureClass.
        var layers = new List<LayerRuntime>(selection.Layers.Count);
        foreach (EnbRetailStarLayerDefinition layer in selection.Layers)
        {
            int visible = budget.LayerAllocations.FirstOrDefault(allocation =>
                ReferenceEquals(allocation.Layer, layer))?.VisibleStarCount ?? 0;
            IReadOnlyList<EnbRetailGeneratedPointStar> generated;
            try
            {
                generated = visible == 0 ? [] : EnbRetailStarPointSemantics.GenerateVisiblePrefix(layer, visible);
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException or NotSupportedException)
            {
                diagnostic = $"{layer.SectionName} point-star generation rejected the authored data: {ex.Message}";
                foreach (var retained in layers) retained.Texture?.Dispose();
                return false;
            }

            layers.Add(new LayerRuntime(
                layer,
                generated.ToArray(),
                string.IsNullOrEmpty(layer.Texture) ? null : new EnbStarTexture(assets, layer.Texture, retainedTextures?[layers.Count]),
                layer.Texture ?? "<untextured>"));
        }

        if (layers.Count == 0)
        {
            diagnostic = $"star-set {selection.RequestedStarSetId} has no drawable current-retail star layers.";
            return false;
        }

        renderer = new LiveWorldStarFieldRenderer(selection, budget, layers.ToArray());
        return true;
    }

    public void Dispose() { foreach (var layer in _layers) layer.Texture?.Dispose(); }

    private static EnbBackdropGeometry BuildGeometry(LayerRuntime layer)
    {
        var vertices = ImmutableArray.CreateBuilder<EnbBackdropVertex>(layer.Stars.Length * (layer.Definition.Quads ? 6 : 3));
        foreach (EnbRetailGeneratedPointStar star in layer.Stars)
        {
            if (layer.Definition.Quads)
            {
                foreach (var corner in EnbRetailStarPointSemantics.BuildQuad(
                    star.Size, star.OrientationIndex, star.AtlasCell, layer.Definition.TextureLog2Width))
                    vertices.Add(new(star.Position, corner.Position, corner.TexCoord, Vector2.Zero, 1));
                continue;
            }
            EnbRetailPointTriangle triangle = EnbRetailStarPointSemantics.BuildTriangle(
                star.Size, star.OrientationIndex, star.AtlasCell, layer.Definition.TextureLog2Width);
            vertices.Add(new(star.Position, triangle.Vertex0, triangle.TexCoord0, Vector2.Zero, 1));
            vertices.Add(new(star.Position, triangle.Vertex1, triangle.TexCoord1, Vector2.Zero, 1));
            vertices.Add(new(star.Position, triangle.Vertex2, triangle.TexCoord2, Vector2.Zero, 1));
        }
        return new(vertices.MoveToImmutable(), null, null) { StarTexture = layer.Texture, IsUntextured = layer.Texture is null };
    }

    private sealed record LayerRuntime(
        EnbRetailStarLayerDefinition Definition,
        EnbRetailGeneratedPointStar[] Stars,
        EnbStarTexture? Texture,
        string ResolvedTextureName);

}
