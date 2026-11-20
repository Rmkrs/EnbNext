using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// Engine-owned authored panel: asset, layout, text and draw data only.
public sealed partial class EnbQuitPanel : IDisposable
{
    private sealed record Gadget(string Key, EnbAuthoredUiAsset Asset, EnbUiRect Design)
    { public EnbRetailMeshPick Query { get; init; } }
    private readonly EnbRetailAssetBundle assets;
    private readonly EnbAuthoredUiAsset asset;
    private readonly EnbInterfaceLayoutCatalog layout;
    private readonly EnbFontEffectPalette palette;
    private readonly Gadget[] gadgets;
    private readonly EnbW3dWorldBounds? backgroundSphere;
    private readonly Dictionary<string, EnbRetailFontFace> fonts = new();
    private readonly Dictionary<string, EnbRetailText> labels = new();

    private sealed class LabelInput(EnbInterfaceTextDefinition definition, EnbAttachedTextStyle style, int gadget)
    {
        public readonly EnbInterfaceTextDefinition Definition = definition;
        public readonly EnbAttachedTextStyle Style = style;
        public readonly int Gadget = gadget;
        public EnbAttachedFontColor Clock;
        public EnbRgb Ink;
    }
    private static readonly string[] LabelIds = ["GO_opt02a", "Quit_opt02a", "Character_opt02a", "Login_opt02a", "cancel_opt02a", "Note_opt02a"];
    private readonly LabelInput?[] labelInput = new LabelInput?[6];
    public EnbUiGadgetPointer Pointer { get; }
    private readonly bool layoutEditing;

    public string? AdvanceInput(EnbUiInputEvent input, bool left, bool right, float cursorX, float cursorY,
        int width, int height, long milliseconds)
    {
        int command = Pointer.Dispatch(new(input.Code, input.Modifiers, left, right),
            PointerHits(Math.Clamp(input.X, 0, 1), Math.Clamp(input.Y, 0, 1)));
        foreach (var label in labelInput)
            if (label is not null)
                label.Ink = label.Clock.Update(palette, label.Definition.Properties,
                    EnbAttachedFontColor.Contains(label.Style.Clip, width, height, cursorX, (float)LayoutY(label.Gadget>=0?PointerOrder[label.Gadget]:"",cursorY)),
                    label.Gadget >= 0 && Pointer.Selected(label.Gadget), milliseconds);
        return CommandKey(command);
    }

    public void RetireInput()
    {
        Pointer.Retire();
        foreach (var label in labelInput) if (label is not null)
        { label.Clock = default; label.Ink = palette.ResolveSelected(label.Definition.Properties, false, false, 0); }
    }

    public EnbQuitPanel(EnbRetailAssetBundle assets, EnbFontEffectPalette palette, bool layoutEditing=false)
    {
        this.layoutEditing=layoutEditing;Pointer=new(layoutEditing?6:5);
        this.assets = assets; this.palette = palette;
        asset = EnbAuthoredUiAsset.Load(assets, "opt02a.w3d") ?? throw new InvalidDataException("OPT02A unavailable.");
        layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fa_opt02a.ini"));
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(asset);
        for (int i = 0; i < LabelIds.Length; i++)
            if (layout.TryResolve(LabelIds[i], out var definition))
            {
                string key = "quit/" + definition.Properties["Attach"].ToUpperInvariant();
                labelInput[i] = new(definition, EnbAttachedTextStyle.Load(assets, asset, definition,
                    System.Numerics.Matrix4x4.Identity), Array.IndexOf(PointerOrder, key));
                labelInput[i]!.Ink = palette.ResolveSelected(definition.Properties, false, false, 0);
            }

        if (asset.Resource.HierarchicalLods.SingleOrDefault() is { } hlod)
        {
            if (!EnbW3dHlodCompositeBoundsCompiler.TryCompute(asset.Resource, hlod,
                System.Numerics.Matrix4x4.Identity, null, 0, out var bounds, out var error))
                throw new InvalidDataException(error);
            backgroundSphere = bounds.WorldSphere;
        }
        gadgets = asset.Resource.Meshes.Select(mesh => new Gadget("quit/" + mesh.Name,
            asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { mesh }) },
            EnbAuthoredUiAsset.MeshBounds(asset, mesh.Name))
            { Query = EnbUiModelQuery.BindMainView(mesh, transforms[asset.Hierarchy.Pivots.First(p => p.Name == mesh.Name).Ordinal]) })
            .OrderBy(g => g.Key.EndsWith("FRAME") ? 0 : g.Key.Contains("ICON") ? 2 : 1).ToArray();
    }

    public static string? Key(int index) => index switch
    {
        0 => "quit/BTTN02", 1 => "quit/BTTN01", 2 => "quit/BTTN00",
        3 => "quit/BTTN03", 4 => "quit/EXIT", 5 => "quit/FRAME", _ => null,
    };

    private static readonly string[] PointerOrder = ["quit/BTTN02", "quit/BTTN01", "quit/BTTN00", "quit/BTTN03", "quit/EXIT"];

    /// <summary>Normalized client coordinates from the host input adapter.
    /// 005CE830/00687D30 prepend the five buttons; 0056D970 assigns background
    /// priority 1000. Decorations are part of that background model query.</summary>
    public string? HitTest(double x, double y)
    {
        if(layoutEditing&&EditHit(x,y))return "quit/EDIT";
        foreach (string key in PointerOrder)
            foreach (var gadget in gadgets)
                if (gadget.Key == key && gadget.Query.Contains(x, LayoutY(key,y))) return key;
        foreach (var gadget in gadgets)
            if ((gadget.Query with { ParentSphere = backgroundSphere }).Contains(x, y)) return "quit/FRAME";
        return null;
    }

    // Full hit set is needed by the frame dispatcher: lower-priority gadgets
    // still update their geometric hit state after an earlier gadget consumes input.
    public uint PointerHits(double x, double y)
    {
        uint mask = 0;
        for (int i = 0; i < PointerOrder.Length; i++)
            foreach (var gadget in gadgets)
                if (gadget.Key == PointerOrder[i] && gadget.Query.Contains(x, LayoutY(gadget.Key,y))) mask |= 1u << i;
        foreach (var gadget in gadgets)
            if ((gadget.Query with { ParentSphere = backgroundSphere }).Contains(x, y))
            { mask |= 1u << (layoutEditing?6:5); break; }
        if(layoutEditing){if(EditHit(x,y))mask|=1u<<5;if(Bounds.Contains(new(x*640,y*480)))mask|=1u<<6;}
        return mask;
    }

    public IEnumerable<string> Keys => gadgets.Select(g => g.Key);
    public EnbUiRect Design(string key) => key=="quit/EDIT"?EditBounds:LayoutBounds(key,gadgets.First(g => g.Key == key).Design);
    public EnbUiRect Bounds {get{var r=gadgets.Select(g=>g.Design).Aggregate((a,b)=>a.Union(b));return layoutEditing?r with {Height=r.Height+EditStep}:r;}}
    internal EnbRetailText Label(string id) => labels[id];
    private static bool IsButton(string key) => key == "quit/EXIT" || key.StartsWith("quit/BTTN");

    // Explicit historical fixed-state diagnostic adapter. Production uses DrawFrame;
    // both feed the same GPU draw construction, with no host controls or CPU renderer.
    public ImmutableList<EnbUiModelDraw> DrawDiagnostic(EnbUiSize size, double scaling, bool station,
        string? hovered, string? pressed, long hoverElapsed, string? error = null)
        => DrawCore(size, scaling, station, hovered, pressed, hoverElapsed, error, false);

    public ImmutableList<EnbUiModelDraw> DrawFrame(EnbUiSize size, double scaling, bool station, string? error = null)
        => DrawCore(size, scaling, station, null, null, 0, error, true);

    private ImmutableList<EnbUiModelDraw> DrawCore(EnbUiSize size, double scaling, bool station,
        string? hovered, string? pressed, long hoverElapsed, string? error, bool native)
    {
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        foreach (var gadget in gadgets)
        {
            int index = Array.IndexOf(PointerOrder, gadget.Key);
            bool selected = native ? index >= 0 && Pointer.Selected(index) : IsButton(gadget.Key) && pressed == gadget.Key;
            var r = LayoutBounds(gadget.Key,gadget.Design);
            draws.Add(new(gadget.Asset, !native && selected ? 1u : 0u,
                new(r.X * size.Width / 640, r.Y * size.Height / 480, r.Width * size.Width / 640, r.Height * size.Height / 480),
                native ? index >= 0 && Pointer.Flash(index) : IsButton(gadget.Key) && hovered == gadget.Key && !selected)
                { GadgetOwner = gadget, DesignBounds = gadget.Design.Translate(-320, -240),
                  EmissiveOverride = native && selected ? new System.Numerics.Vector3(.35f) : null,
                  FlashHighlight = native && index >= 0 && Pointer.Flash(index) && !Pointer.FlashInside(index) });
        }
        var images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
        var transforms = EnbAuthoredUiAsset.ResolveTransforms(asset);
        foreach (string id in LabelIds)
        {
            if (station && id == "Note_opt02a") continue;
            if (!layout.TryResolve(id, out var definition) ||
                ResolveFont(definition.Properties.GetValueOrDefault("FontType", ""), size, scaling) is not { } font) continue;
            var pivot = asset.Hierarchy.Pivots.First(p => p.Name.Equals(definition.Properties["Attach"], StringComparison.OrdinalIgnoreCase));
            var point = transforms[pivot.Ordinal].Translation;
            if (!labels.TryGetValue(id, out var label)) labels[id] = label = new();
            label.SetText(definition.Properties.GetValueOrDefault("String0", ""), font,
                native ? labelInput[Array.IndexOf(LabelIds, id)]!.Ink :
                palette.Resolve(definition.Properties, hovered == "quit/" + pivot.Name.ToUpperInvariant(), hoverElapsed));
            label.SetDisplayScale(1,1);
            if(labelInput[Array.IndexOf(LabelIds,id)] is {} input)
                label.FitTo(new(input.Style.Clip.Width*size.Width/640,input.Style.Clip.Height*size.Height/480),scaling);
            if (label.Raster is not { } raster) continue;
            string justify = definition.Properties.GetValueOrDefault("Justify", "");
            double offset = justify.EndsWith("LEFT", StringComparison.OrdinalIgnoreCase) ? 0
                : justify.EndsWith("RIGHT", StringComparison.OrdinalIgnoreCase) ? label.Width : label.Width / 2;
            images.Add(new(raster, new((point.X + 320) * size.Width / 640 - offset,
                (240 - point.Y+(layoutEditing&&id is "cancel_opt02a" or "Note_opt02a"?EditStep:0)) * size.Height / 480 - label.Height / 2, label.Width, label.Height)));
        }
        if (error is not null && ResolveFont("FONT_TINY", size, scaling) is { } errorFont)
        {
            if (!labels.TryGetValue("error", out var label)) labels["error"] = label = new();
            label.SetText(error, errorFont, new(1, .3f, .3f)); label.SetDisplayScale(1,1);
            if (label.Raster is { } raster) images.Add(new(raster, new((size.Width - label.Width) / 2, size.Height * .47, label.Width, label.Height)));
        }
        if (draws.Count > 0) draws[^1] = draws[^1] with { Images = images.ToImmutable() };
        if(layoutEditing)draws.Add(Control("edit-entry","quit/BTTN02",ScreenEdit(size),"Edit mode",size,scaling,Pointer.Flash(5),Pointer.Selected(5)));
        return draws.ToImmutable();
    }

    public EnbRetailFontFace? ResolveFont(string id, EnbUiSize size, double scaling)
    {
        if (!EnbRetailFontFactory.TryResolveDefinition(assets, id, (int)(size.Width * scaling), (int)(size.Height * scaling), out var definition)
            || definition.Resource is null) return null;
        if (fonts.TryGetValue(definition.Resource, out var font)) return font;
        if (!EnbRetailFontFactory.TryCreate(assets, definition, out font)) return null;
        return fonts[definition.Resource] = font;
    }

    public void Dispose() { foreach (var label in labels.Values) label.Dispose(); labels.Clear(); fonts.Clear(); }
}
