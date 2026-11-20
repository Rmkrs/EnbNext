using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Retained utility gadgets and authored GPU presentation, independent of the window host.</summary>
internal sealed partial class EnbUtilityHudPanel(EnbUtilityHudResources resources)
{
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private double pixelScale=1;
    /// <summary>Next-only fifth button (multiboxing). It reuses the authored round button and
    /// the character icon twice, slightly offset; the bars and their backing widen by one
    /// button spacing.</summary>
    public bool MultiboxButton {get;set;}
    public bool MultiboxSelected {get;set;}
    public bool MultiboxHovered {get;set;}
    /// <summary>Screen rectangle of the multibox button before the Utility placement.</summary>
    public EnbUiRect MultiboxBounds {get;private set;}
    private double Spacing()
    {
        var buttons=resources.Pieces.Where(p=>IsButton(p.Key)).Select(p=>p.Bounds.X).OrderBy(x=>x).ToArray();
        return buttons.Length<2?0:(buttons[^1]-buttons[0])/(buttons.Length-1);
    }
    private readonly object?[] gadgets = resources.Pieces.Select(p => IsButton(p.Key) || p.Key.EndsWith("_BAR") ? new object() : null).ToArray();
    public static bool IsButton(string key) => key is "hud/SHIP_BUT" or "hud/CHARACTER_BUT" or "hud/MAP_BUT" or "hud/OPTIONS_BUT";
    public EnbUiRect Design(string key)
    {
        foreach (var piece in resources.Pieces) if (piece.Key == key) return piece.Bounds;
        throw new KeyNotFoundException(key);
    }

    public void Draw(ImmutableList<EnbUiModelDraw>.Builder list, EnbUiSize size, double scaling,
        EnbSceneExperienceSnapshot experience, string? pressed, string? hovered)
        => DrawCore(list, size, scaling, experience, pressed, hovered, false);

    public void DrawFrame(ImmutableList<EnbUiModelDraw>.Builder list, EnbUiSize size, double scaling,
        EnbSceneExperienceSnapshot experience) => DrawCore(list, size, scaling, experience, null, null, true);

    private void DrawCore(ImmutableList<EnbUiModelDraw>.Builder list, EnbUiSize size, double scaling,
        EnbSceneExperienceSnapshot experience, string? pressed, string? hovered, bool native)
    {
        pixelScale=scaling;
        double extra=MultiboxButton?Spacing():0;
        EnbUiRect Raster(EnbUiRect screen)=>new(Math.Floor(screen.X * scaling) / scaling, Math.Floor(screen.Y * scaling) / scaling,
            (Math.Ceiling(screen.Right * scaling) - Math.Floor(screen.X * scaling)) / scaling,
            (Math.Ceiling(screen.Bottom * scaling) - Math.Floor(screen.Y * scaling)) / scaling);
        EnbUiRect Screen(EnbUiRect r)=>new(r.X * size.Width / 640, r.Y * size.Height / 480, r.Width * size.Width / 640, r.Height * size.Height / 480);
        EnbUiRect Design(EnbUiRect raster)=>new(raster.X * 640 / size.Width - 320, raster.Y * 480 / size.Height - 240,
            raster.Width * 640 / size.Width, raster.Height * 480 / size.Height);
        for (int i = 0; i < resources.Pieces.Length; i++)
        {
            var piece = resources.Pieces[i];
            bool button = IsButton(piece.Key); int index = InputIndex(piece.Key);
            bool selected = button && (native ? Pointer.Selected(index) : piece.Key == pressed);
            var r = resources.Pieces[piece.RasterIndex].Bounds;
            var screen = new EnbUiRect(r.X * size.Width / 640, r.Y * size.Height / 480,
                r.Width * size.Width / 640, r.Height * size.Height / 480);
            var raster = new EnbUiRect(Math.Floor(screen.X * scaling) / scaling, Math.Floor(screen.Y * scaling) / scaling,
                (Math.Ceiling(screen.Right * scaling) - Math.Floor(screen.X * scaling)) / scaling,
                (Math.Ceiling(screen.Bottom * scaling) - Math.Floor(screen.Y * scaling)) / scaling);
            var design = new EnbUiRect(raster.X * 640 / size.Width - 320, raster.Y * 480 / size.Height - 240,
                raster.Width * 640 / size.Width, raster.Height * 480 / size.Height);
            // The bars and their backing stretch to span the fifth button.
            if(extra>0 && (piece.Key=="hud/BARS_BOTTOM" || piece.Key.EndsWith("_BAR")))
                raster=Raster(Screen(r with {Width=r.Width+extra}));
            float? progress = piece.Key switch
            { "hud/COMBAT_BAR" => experience.Combat, "hud/EXPLORE_BAR" => experience.Explore, "hud/TRADE_BAR" => experience.Trade, _ => null };
            bool flash = button && (native ? Pointer.Flash(index) : piece.Key == hovered && !selected);
            list.Add(new(piece.Asset, !native && selected ? 1u : 0u, raster, flash)
            { LayoutGroup=EnbUiGroup.Utility, GadgetOwner = gadgets[i], DesignBounds = design, Stage0UvOffset = progress is float f ? new(0, .46f * f) : null,
              EmissiveOverride = native && selected ? new System.Numerics.Vector3(.35f) : null,
              FlashHighlight = native && flash && !Pointer.FlashInside(index) });
        }
        MultiboxBounds=default;
        if(extra<=0)return;
        var last=resources.Pieces.Where(p=>IsButton(p.Key)).MaxBy(p=>p.Bounds.X)!;
        var icon=resources.Pieces.FirstOrDefault(p=>p.Key=="hud/ICON05");
        var buttonRaster=Raster(Screen(last.Bounds with {X=last.Bounds.X+extra}));
        MultiboxBounds=buttonRaster;
        list.Add(new(last.Asset,MultiboxSelected?1u:0u,buttonRaster,MultiboxHovered&&!MultiboxSelected)
            {LayoutGroup=EnbUiGroup.Utility,GadgetOwner=multiboxGadget,DesignBounds=Design(Raster(Screen(last.Bounds))),
             EmissiveOverride=native&&MultiboxSelected?new System.Numerics.Vector3(.35f):null});
        if(icon is null)return;
        // Two character silhouettes, one behind and up-left of the other.
        var iconBounds=resources.Pieces[icon.RasterIndex].Bounds;
        double shift=iconBounds.Width*.14;
        int silhouette=0;
        foreach(var (dx,dy) in new[]{(-shift,-shift*.6),(shift*.5,shift*.4)})
        {
            const double k=.84; // both silhouettes slightly smaller so the pair fits the round
            var moved=new EnbUiRect(last.Bounds.X+extra+iconBounds.Width*(1-k)/2+dx,iconBounds.Y+iconBounds.Height*(1-k)/2+dy,iconBounds.Width*k,iconBounds.Height*k);
            list.Add(new(icon.Asset,0,Raster(Screen(moved)),false)
                {LayoutGroup=EnbUiGroup.Utility,GadgetOwner=multiboxIcons[silhouette++],DesignBounds=Design(Raster(Screen(iconBounds)))});
        }
    }
    private readonly object multiboxGadget=new();
    private readonly object[] multiboxIcons=[new(),new()];
}
