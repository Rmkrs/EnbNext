using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Authored OPT01A/OPT03A resources in the asset session's prepared owner.</summary>
public sealed class EnbOptionsPanelResources
{
    public sealed record Piece(string Key, EnbAuthoredUiAsset Asset, EnbAuthoredUiAsset? Checked, EnbUiRect Bounds)
    {
        public EnbRetailMeshPick Query { get; init; } public int GadgetIndex { get; init; }
        /// <summary>Authored mesh bounds when the piece is placed or stretched away from them.</summary>
        public EnbUiRect? Source { get; init; }
    }
    /// <summary>Next category button caption, drawn with an authored button label moved by Shift.</summary>
    public readonly record struct NextLabel(string Text, int SourceLabel, EnbUiPoint Shift, int GadgetIndex);
    public readonly record struct LabelBinding(EnbInterfaceTextDefinition? Definition, EnbAttachedTextStyle? Style, int GadgetIndex);
    public sealed record Panel(EnbAuthoredUiAsset Asset, EnbInterfaceLayoutCatalog Layout, Piece[] Pieces, string[] LabelIds)
    {
        public EnbW3dWorldBounds? BackgroundSphere { get; init; }
        public LabelBinding[] LabelBindings { get; init; } = [];
        public string[] PointerOrder { get; init; } = [];
        public NextLabel[] NextLabels { get; init; } = [];
        /// <summary>Design-space move of each authored label whose button the Next grid places elsewhere.</summary>
        public EnbUiPoint[] LabelShifts { get; init; } = [];
    }
    // Next categories continue the authored two-column grid below Fonts.
    public static readonly string[] NextCategories = ["Advanced Graphics", "Social", "Navigation", "Quality of Life", "Modern UI", "Labels"];
    private readonly EnbRetailAssetBundle assets;
    private readonly Dictionary<string, EnbRetailFontFace> fonts = new();
    public Panel Main { get; }
    public Panel Camera { get; }

    internal EnbOptionsPanelResources(EnbRetailAssetBundle assets)
    {
        this.assets = assets;
        Main = Load("main/", "opt01a.w3d", "fa_opt01a.ini",
            ["GO_opt01a", "Camera_opt01a", "Control_opt01a", "Interface_opt01a", "Graphics_opt01a", "Sound_opt01a", "Credits_opt01a", "Fonts_opt01a"]);
        Camera = Load("camera/", "opt03a.w3d", "fa_opt03a-camera.ini",
            ["title_opt03a", "ok_opt03a", "cancel_opt03a", "text00_opt03a", "text01_opt03a", "text02_opt03a", "text03_opt03a", "text04_opt03a", "text05_opt03a", "text06_opt03a"]);
    }

    private Panel Load(string prefix, string model, string ini, string[] labels)
    {
        var asset = EnbAuthoredUiAsset.Load(assets, model) ?? throw new InvalidDataException(Path.GetFileNameWithoutExtension(model).ToUpperInvariant() + " unavailable.");
        string[] pointerOrder = prefix == "camera/"
            ? ["camera/CHECK06", "camera/CHECK05", "camera/CHECK04", "camera/CHECK03", "camera/CHECK02", "camera/CHECK01", "camera/CHECK00", "camera/CANCEL", "camera/OK", "camera/EXIT"]
            : ["main/BTTN06", "main/BTTN05", "main/BTTN04", "main/BTTN03", "main/BTTN02", "main/BTTN01", "main/BTTN00", "main/EXIT",
                "main/NEXT00", "main/NEXT01", "main/NEXT02", "main/NEXT03", "main/NEXT04", "main/NEXT05"];
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(asset);
        EnbW3dWorldBounds? sphere = null;
        if (asset.Resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(asset.Resource, hlod,
                System.Numerics.Matrix4x4.Identity, null, 0, out var bounds, out var error))
                throw new InvalidDataException(error);
            sphere = bounds.WorldSphere;
        }
        var pieces = asset.Resource.Meshes.Where(m => prefix != "camera/" || !m.Name.StartsWith("CHECK") || int.Parse(m.Name.AsSpan(5)) < 7)
            .Select(mesh =>
            {
                var piece = asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { mesh }) };
                EnbAuthoredUiAsset? on = null;
                if (prefix == "camera/" && mesh.Name.StartsWith("CHECK"))
                {
                    EnbW3dRenderTextureBinding Checked(EnbW3dRenderTextureBinding t) => t with
                        { LogicalName = "if_check_on.tga", Location = null, ResolveLogicalNameAtRuntime = true };
                    on = piece with { Resource = piece.Resource.Copy().SetMeshes(piece.Resource.Meshes.Select(m => m.Copy().SetMaterialPasses(m.MaterialPasses.Select(p => p.Copy()
                        .SetTextureStages(p.TextureStages.Select(s => s.Copy().SetTextures(s.Textures.Select(Checked).ToArray())).ToArray())
                        .SetTriangleBindings(p.TriangleBindings.Select(t => t with { Textures = t.Textures.Select(b => b is null ? null : Checked(b)).ToArray() }).ToArray())).ToArray())).ToArray()) };
                }
                return new Piece(prefix + mesh.Name, piece, on, EnbAuthoredUiAsset.MeshBounds(asset, mesh.Name))
                    { Query = EnbUiModelQuery.BindMainView(mesh, transforms[asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name).Ordinal]),
                      GadgetIndex = Array.IndexOf(pointerOrder, prefix + mesh.Name) };
            }).OrderBy(p => p.Key.EndsWith("FRAME") || p.Key.EndsWith("BARS_BOTTOM") ? 0 : p.Key.Contains("ICON") ? 2 : 1).ToArray();
        var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, ini));
        NextLabel[] nextLabels = [];var labelShifts = new EnbUiPoint[labels.Length];
        if (prefix == "main/")
        {
            // Owner layout: Camera|Control, Interface|Fonts, Sound|Credits, Graphics|Advanced Graphics,
            // Social|Navigation, Quality of Life|Modern UI, then Labels.
            Piece Find(string key) => pieces.First(p => p.Key == key);
            var authored = pieces.ToDictionary(p => p.Key, p => p.Bounds);
            double left = authored["main/BTTN00"].X, right = authored["main/BTTN01"].X;
            double pitch = authored["main/BTTN02"].Y - authored["main/BTTN00"].Y, single = authored["main/BTTN06"].Y;
            double Row(int row) => row < 3 ? authored["main/BTTN00"].Y + row * pitch : single + (row - 3) * pitch;
            EnbUiPoint Shift(string source, double x, int row) => new(x - authored[source].X, Row(row) - authored[source].Y);
            Piece Place(Piece piece, string key, EnbUiPoint shift) => piece with { Key = key, Bounds = piece.Bounds.Translate(shift.X, shift.Y), Source = piece.Bounds,
                Query = piece.Query with { World = piece.Query.World * System.Numerics.Matrix4x4.CreateTranslation((float)shift.X, -(float)shift.Y, 0) },
                GadgetIndex = Array.IndexOf(pointerOrder, key) };
            var moved = new Dictionary<string, EnbUiPoint> { ["main/BTTN03"] = Shift("main/BTTN03", left, 3), ["main/BTTN06"] = Shift("main/BTTN06", right, 1) };
            labelShifts[Array.IndexOf(labels, "Graphics_opt01a")] = moved["main/BTTN03"];
            labelShifts[Array.IndexOf(labels, "Fonts_opt01a")] = moved["main/BTTN06"];
            (string Source, string Label, double X, int Row)[] next = [("main/BTTN05", "Credits_opt01a", right, 3), ("main/BTTN04", "Sound_opt01a", left, 4),
                ("main/BTTN05", "Credits_opt01a", right, 4), ("main/BTTN04", "Sound_opt01a", left, 5), ("main/BTTN05", "Credits_opt01a", right, 5),
                ("main/BTTN04", "Sound_opt01a", left, 6)];
            var added = new List<Piece>(); nextLabels = new NextLabel[NextCategories.Length];
            for (int i = 0; i < NextCategories.Length; i++)
            {
                string key = $"main/NEXT{i:00}"; var shift = Shift(next[i].Source, next[i].X, next[i].Row);
                added.Add(Place(Find(next[i].Source), key, shift));
                nextLabels[i] = new(NextCategories[i], Array.IndexOf(labels, next[i].Label), shift, Array.IndexOf(pointerOrder, key));
            }
            double extra = Row(next.Max(n => n.Row)) - single;
            pieces = pieces.Select(p => p.Key == "main/FRAME" ? p with { Bounds = p.Bounds with { Height = p.Bounds.Height + extra }, Source = p.Bounds }
                : moved.TryGetValue(p.Key, out var shift) ? Place(p, p.Key, shift) : p).Concat(added).ToArray();
        }
        return new(asset, layout, pieces, labels)
        {
            NextLabels = nextLabels,
            LabelShifts = labelShifts,
            BackgroundSphere = sphere,
            PointerOrder = pointerOrder,
            LabelBindings = labels.Select(id => layout.TryResolve(id, out var d)
                ? new LabelBinding(d, EnbAttachedTextStyle.Load(assets, asset, d, System.Numerics.Matrix4x4.Identity, transforms),
                    Array.FindIndex(pointerOrder, key => key.AsSpan(prefix.Length).Equals(d.Properties["Attach"], StringComparison.OrdinalIgnoreCase)))
                : new LabelBinding(null, null, -1)).ToArray(),
        };
    }

    public EnbRetailFontFace? Font(string id, EnbUiSize size, double scaling)
    {
        if (!EnbRetailFontFactory.TryResolveDefinition(assets, id, (int)(size.Width * scaling), (int)(size.Height * scaling), out var d) || d.Resource is null) return null;
        if (fonts.TryGetValue(d.Resource, out var font)) return font;
        if (!EnbRetailFontFactory.TryCreate(assets, d, out font)) return null;
        return fonts[d.Resource] = font;
    }

    public void ClearFonts() => fonts.Clear();
}
