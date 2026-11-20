using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbModernHud
{
    public const double Slot = 42, SlotGap = 4, Round = 22, Xp = 6, XpRow = 12, XpGap = 8, XpLevel = 20;
    private enum Overlay_ { Active, Range, Timer, Dark, NoAmmo, NoSkill, Powerup, Count }
    private static readonly string[] OverlayNames = ["itemon", "if_targ", "item_timer", "item_dark", "item_noammo", "item_noskill", "itemon01"];
    private readonly (EnbAuthoredUiAsset Asset, EnbUiRect Design)?[] overlays = new (EnbAuthoredUiAsset, EnbUiRect)?[(int)Overlay_.Count];
    private readonly (EnbModernShortcut View, bool Hovered)[] bar = new (EnbModernShortcut, bool)[12];
    /// <summary>The cockpit's shortcut owner: slot views (icon, highlight, key, combat feedback) for the twelve slots.</summary>
    internal Func<int, EnbModernShortcut>? Shortcut { get; set; }
    internal Func<EnbModernPowerup?>? Powerup { get; set; }
    private EnbModernPowerup? powerup;
    internal Func<int, bool>? ShortcutHovered { get; set; }
    internal Func<(EnbAuthoredUiAsset Button, EnbUiRect ButtonDesign, EnbUiRect IconDesign)?>? SlotArt { get; set; }
    internal Func<ImmutableArray<EnbUiTextDraw>>? DragImages { get; set; }

    /// <summary>Panel layout options for the action bar (one row of twelve, or two rows of six); with neither chosen,
    /// two rows on screens narrower than 16:9, where one row leaves the chat and the micro menu no room.</summary>
    internal const string BarOneRowOption = "ActionBar/Rows=1", BarTwoRowsOption = "ActionBar/Rows=2";
    /// <summary>Screens narrower than this (in modern units: 16:9 is about 1351) take the compact layout by default.</summary>
    private const double NarrowWidth = 1300;
    private bool Narrow => size.Width < NarrowWidth;
    private int BarRows => Layout?.Option(BarTwoRowsOption) == true ? 2 : Layout?.Option(BarOneRowOption) == true ? 1 : Narrow ? 2 : 1;
    private double BarWidth { get { int columns = 12 / BarRows; return columns * Slot + (columns - 1) * SlotGap; } }
    /// <summary>Twelve slots centred at the bottom, above the experience row: bank A (keys 1-6) then bank B (Alt+1-6),
    /// side by side in one row, or bank A above bank B in two.</summary>
    private EnbUiRect SlotRect(int i)
    {
        int rows = BarRows, columns = 12 / rows, row = i / columns, column = i % columns;
        return new((size.Width - BarWidth) / 2 + column * (Slot + SlotGap), XpRect.Y - 4 - Slot - (rows - 1 - row) * (Slot + SlotGap), Slot, Slot);
    }
    /// <summary>The experience row under the action bar, inside the gutter: combat, exploration and trade side by side, each with its level.</summary>
    private EnbUiRect XpRect { get { double width = BarWidth; return new((size.Width - width) / 2, size.Height - GutterY - XpRow, width, XpRow); } }
    /// <summary>A slot's screen rectangle (layout applied); the cockpit hit-tests its slots with it.</summary>
    public EnbUiRect? SlotBounds(int i) => size.Width > 0 && i is >= 0 and < 12 ? Apply(EnbUiGroup.ActionBar, SlotRect(i)) : null;
    /// <summary>The action bar's own extent (its twelve slots) before placement, for edit mode.</summary>
    public EnbUiRect ActionBarBounds => size.Width > 0 ? Up(SlotRect(0).Union(SlotRect(11))) : default;
    private static readonly EnbUiGroup[] XpGroups = [EnbUiGroup.CombatXp, EnbUiGroup.ExploreXp, EnbUiGroup.TradeXp];
    /// <summary>One experience part (level and bar): combat, exploration or trade, each placed on its own.</summary>
    private EnbUiRect XpPart(int i) { var full = XpRect; double third = (full.Width - 2 * XpGap) / 3; return new(full.X + i * (third + XpGap), full.Y, third, full.Height); }
    public EnbUiRect XpBounds(int i) => size.Width > 0 ? Up(XpPart(i)) : default;
    /// <summary>Autopilot (top) and damage meter (bottom) round buttons between the engine hub and the bar, before placement.</summary>
    private (EnbUiRect Autopilot, EnbUiRect Damage) RoundRects
    {
        get
        {
            var first = SlotRect(0); double x = first.X - 8 - Round, height = XpRect.Bottom - first.Y, top = first.Y + (height - 2 * Round - 4) / 2;
            return (new(x, top, Round, Round), new(x, top + Round + 4, Round, Round));
        }
    }
    /// <summary>Autopilot and damage meter buttons in screen space (Engine placement applied); the cockpit draws them.</summary>
    public (EnbUiRect Left, EnbUiRect Right)? RoundButtonBounds
    {
        get
        {
            if (size.Width <= 0) return null;
            var (autopilot, damage) = RoundRects;
            return (Apply(EnbUiGroup.Engine, autopilot), Apply(EnbUiGroup.Engine, damage));
        }
    }

    private bool overlaysLoaded;
    /// <summary>Samples the slot views; true when the bar must be rebuilt.</summary>
    private bool RefreshBar()
    {
        if (Shortcut is null) return false;
        if (!overlaysLoaded)
        {
            overlaysLoaded = true;
            for (int i = 0; i < OverlayNames.Length; i++)
            {
                try
                {
                    if (EnbAuthoredUiAsset.Load(assets, OverlayNames[i] + ".w3d", allowStandalone: true) is { } asset && asset.Resource.Meshes.Count > 0)
                        overlays[i] = (asset, asset.Resource.Meshes.Select(m => EnbCockpitUi.MeshBounds(asset, m.Name)).Aggregate((a, b) => a.Union(b)).Translate(new(-320, -240)));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { } // That overlay stays absent.
            }
        }
        bool changed = false;
        for (int i = 0; i < 12; i++)
        {
            var next = (Shortcut(i), ShortcutHovered?.Invoke(i) == true);
            if (next != bar[i]) { bar[i] = next; changed = true; }
        }
        var nextPowerup = Powerup?.Invoke();
        if (nextPowerup != powerup) { powerup = nextPowerup; changed = true; }
        return changed || DragImages?.Invoke() is { IsDefaultOrEmpty: false };
    }
}
