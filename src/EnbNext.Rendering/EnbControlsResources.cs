using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>005CA330 authored TSI31A resources and stable label/piece bindings.</summary>
public sealed class EnbControlsResources
{
    public sealed record Piece(string Key, EnbAuthoredUiAsset Asset, EnbUiRect Bounds)
    { public EnbRetailMeshPick Query { get; init; } public int GadgetIndex { get; init; } }
    public readonly record struct Alpha(float Initial, float Target, float Fade, float Blink, float Revert);
    public sealed record Label(EnbInterfaceTextDefinition Definition, string Attach, EnbUiPoint Anchor, Alpha Effect)
    { public EnbAttachedTextStyle Style { get; init; } = null!; public int GadgetIndex { get; init; } }
    public EnbAuthoredUiAsset Asset { get; }
    public Piece[] Pieces { get; }
    public int[] DrawOrder { get; }
    public string[] PointerOrder { get; }
    public EnbRetailMeshPick[] BackgroundQueries { get; }
    public Label[] Labels { get; }
    public EnbUiRect? WheelBounds { get; }
    public EnbUiRect Track { get; }
    public EnbUiRect ScrollBounds { get; }
    public EnbUiRect Up { get; }
    public EnbUiRect Down { get; }
    public EnbVerticalScrollbarResources Scrollbar { get; }

    internal EnbControlsResources(EnbRetailAssetBundle assets)
    {
        Asset = EnbAuthoredUiAsset.Load(assets, "tsi31a.w3d") ?? throw new InvalidDataException("TSI31A unavailable.");
        string[] commands = ["EXIT", "BUTTON03", "BUTTON00", "BUTTON01", "BUTTON02",
            "TOP_BUTTON00", "TOP_BUTTON01", "TOP_BUTTON02", "TOP_BUTTON03", "TOP_BUTTON04", "TOP_BUTTON05"];
        var pointerOrder = new List<string> { "control/KEYBOARD", "control/SCROLL" };
        for (int row = 10; row >= 0; row--) { pointerOrder.Add($"control/ALT_KEY{row:00}"); pointerOrder.Add($"control/PRIME_KEY{row:00}"); }
        for (int i = commands.Length - 1; i >= 0; i--) pointerOrder.Add("control/" + commands[i]);
        pointerOrder.Add("control/BACKGROUND"); PointerOrder = pointerOrder.ToArray();
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(Asset);
        EnbW3dWorldBounds? sphere = null;
        if (Asset.Resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(Asset.Resource, hlod,
                System.Numerics.Matrix4x4.Identity, null, 0, out var bounds, out var error))
                throw new InvalidDataException(error);
            sphere = bounds.WorldSphere;
            var box = bounds.LocalBox;
            WheelBounds = new(box.Center.X - box.Extents.X + 320, 240 - box.Center.Y - box.Extents.Y,
                box.Extents.X * 2, box.Extents.Y * 2);
        }
        EnbRetailMeshPick Query(EnbW3dRenderMesh mesh) => EnbUiModelQuery.BindMainView(mesh,
            transforms[Asset.Hierarchy.Pivots.First(p => p.Name.Equals(mesh.Name, StringComparison.OrdinalIgnoreCase)).Ordinal]);
        BackgroundQueries = Asset.Resource.Meshes.Select(mesh => Query(mesh) with { ParentSphere = sphere }).ToArray();
        Pieces = Asset.Resource.Meshes.Where(m => !m.Name.StartsWith("LABEL_")).Select(mesh =>
            new Piece("control/" + mesh.Name, Asset with { Resource = Asset.Resource.Copy().SetMeshes([mesh]) },
                EnbAuthoredUiAsset.MeshBounds(Asset, mesh.Name))
            { Query = Query(mesh), GadgetIndex = Array.IndexOf(PointerOrder, "control/" + mesh.Name) }).ToArray();
        DrawOrder = Enumerable.Range(0, Pieces.Length).OrderBy(i => Pieces[i].Key.Contains("FRAME") ? 0 : 1)
            .ThenByDescending(i => Pieces[i].Key.Contains("FRAME") ? Pieces[i].Bounds.Width * Pieces[i].Bounds.Height : 0).ToArray();
        var document = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tsi31a.ini"));
        var layout = EnbInterfaceLayoutCatalog.Parse(document);
        var effects = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini"));
        EnbUiPoint Pivot(string name)
        {
            var p = transforms[Asset.Hierarchy.Pivots.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Ordinal].Translation;
            return new(p.X + 320, 240 - p.Y);
        }
        var labels = new List<Label>();
        foreach (var section in document.EnumerateSections())
        {
            if (!section.Value.TryGetValue("StringID", out var id) || !layout.TryResolve(id, out var label)) continue;
            float initial = 0, target = 1, fade = 0, blink = 0, revert = 0;
            foreach (var effect in effects.EnumerateSections().Select(s => s.Value))
            {
                string? effectId = effect.GetValueOrDefault("EffectID"); if (effectId is null) continue;
                bool Uses(string property) => label.Properties.GetValueOrDefault(property, "").Split(',').Any(v => v.Trim() == effectId);
                float Number(string key) => float.Parse(effect.GetValueOrDefault(key, "0"), System.Globalization.CultureInfo.InvariantCulture);
                if (Uses("EffectOn") && effect.GetValueOrDefault("EffectType") == "FADE")
                { initial = Number("Initial"); target = Number("Intensity"); fade = Number("Speed"); }
                if (Uses("MouseOver") && effect.GetValueOrDefault("EffectType") == "BLINK") blink = Number("Speed");
                if (Uses("EffectConst") && effect.GetValueOrDefault("EffectType") == "REVERT") revert = Number("Speed");
            }
            string attach = label.Properties.GetValueOrDefault("Attach", "").ToUpperInvariant();
            labels.Add(new(label, attach, Pivot(attach), new(initial, target, fade, blink, revert))
            {
                Style = EnbAttachedTextStyle.Load(assets, Asset, label, System.Numerics.Matrix4x4.Identity, transforms),
                GadgetIndex = Array.IndexOf(PointerOrder, "control/" + attach)
            });
        }
        Labels = labels.ToArray();
        Scrollbar = EnbRenderSession.Of(assets).VerticalScrollbar;
        var top = Pivot("SLIDER_00_MIN"); var bottom = Pivot("SLIDER_00_MAX");
        Up = Scrollbar.Up.Bounds.Translate(top.X, top.Y); Down = Scrollbar.Down.Bounds.Translate(bottom.X, bottom.Y);
        double arrow = Scrollbar.Up.Bounds.Height - 2;
        Track = new(top.X - arrow / 2, top.Y + arrow, arrow, bottom.Y - top.Y - 2 * arrow);
        ScrollBounds = new(top.X - arrow / 2, top.Y, arrow, bottom.Y - top.Y);
    }
}
