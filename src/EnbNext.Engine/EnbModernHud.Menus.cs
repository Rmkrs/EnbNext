using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbModernHud
{
    // Bottom-right micro menu: small square buttons with Modern UI symbols drawn in code (EnbModernSymbols).
    public const double Micro = 20, MicroGap = 0;
    private static readonly (string Key, string Tip, string Symbol)[] MicroBase =
    [
        ("next/Inventory", "Inventory", "inventory"), ("hud/SHIP_BUT", "Equipment", "equipment"), ("next/Skills", "Skills", "skills"), ("next/Missions", "Missions", "missions"),
        ("next/Factions", "Factions", "factions"), ("hud/MAP_BUT", "Sector Map", "map"), ("next/Galaxy", "Galaxy Map", "galaxy"), ("next/Local", "Local", "local"), ("next/Music", "Music", "music"), ("next/Items", "Item catalogue", "items"),
        ("next/Social", "Social", "social"), ("next/Log", "Captain's Log", "log"), ("next/Help", "Help", "help"), ("hud/OPTIONS_BUT", "Options", "options"),
    ];
    private static readonly (string Key, string Tip, string Symbol)[] MicroWithMultibox =
        [.. MicroBase[..MultiboxIndex], ("multibox", "Multibox", "multibox"), .. MicroBase[MultiboxIndex..]];
    private const int MultiboxIndex = 12;
    // Docked, the vault button follows the inventory button (the vault belongs to the station).
    private static readonly (string Key, string Tip, string Symbol)[] MicroStation = [MicroBase[0], ("next/Vault", "Vault", "vault"), .. MicroBase[1..]];
    private static readonly (string Key, string Tip, string Symbol)[] MicroStationWithMultibox =
        [.. MicroStation[..(MultiboxIndex + 1)], ("multibox", "Multibox", "multibox"), .. MicroStation[(MultiboxIndex + 1)..]];
    // Next: the connection indicator, when shown, leads the strip (the other buttons keep their places).
    private static readonly (string Key, string Tip, string Symbol)[]?[] microSets = new (string, string, string)[]?[8];
    private (string Key, string Tip, string Symbol)[] MicroItems()
    {
        int set = (Station ? 1 : 0) | (multiboxShown ? 2 : 0) | (connectionShown ? 4 : 0);
        if (microSets[set] is { } items) return items;
        var basis = Station ? (multiboxShown ? MicroStationWithMultibox : MicroStation) : multiboxShown ? MicroWithMultibox : MicroBase;
        return microSets[set] = connectionShown ? [("next/Status", "Connection", "signal"), .. basis] : basis;
    }
    /// <summary>Utility (Map/Options) buttons: the retail utility actions by key.</summary>
    public event Action<string>? UtilityRequested;
    /// <summary>Next: the help button (its screen rectangle): its menu opens above it.</summary>
    public event Action<EnbUiRect>? HelpRequested;
    /// <summary>Next: the Social button (its screen rectangle): friends, guild and group finder open from its menu.</summary>
    public event Action<EnbUiRect>? SocialRequested;
    /// <summary>Next: the Captain's Log button.</summary>
    public event Action? CaptainLogRequested;
    /// <summary>Next: a right click on the target frame (the screen point): the target's menu opens there.</summary>
    public event Action<EnbUiPoint>? TargetMenuRequested;
    /// <summary>Next: the Modern UI inventory button.</summary>
    public event Action? InventoryRequested;
    /// <summary>Next: the Ship button opens the Modern UI equipment window (the retail cargo/equipment keep their keys).</summary>
    public event Action? EquipmentRequested;
    /// <summary>Next: the Modern UI skills window button.</summary>
    public event Action? SkillsRequested;
    /// <summary>Next: the Modern UI missions window button.</summary>
    public event Action? MissionsRequested;
    /// <summary>Next: the Modern UI factions window button.</summary>
    public event Action? FactionsRequested;
    /// <summary>Next: the Modern UI sector map (false) and galaxy map (true) buttons.</summary>
    public event Action<bool>? MapRequested;
    /// <summary>Next: the Modern UI vault button (docked only).</summary>
    public event Action? VaultRequested;
    /// <summary>Next: the Modern UI Local window button.</summary>
    public event Action? LocalRequested;
    /// <summary>Next: the Modern UI music player button.</summary>
    public event Action? MusicRequested;
    public event Action? ItemsRequested;
    /// <summary>The micro menu on screen (Utility placement applied): the Modern UI inventory opens above it.</summary>
    public EnbUiRect? MicroScreenBounds { get { int n = MicroItems().Length; return size.Width > 0 ? Apply(EnbUiGroup.Utility, MicroExtent(n)) : null; } }
    internal Func<bool>? MultiboxShown { get; set; }
    internal Func<bool>? MultiboxHovered { get; set; }
    private bool multiboxShown;

    /// <summary>Panel layout options for the micro menu: how many lines (rows, or columns when upright) it takes, and
    /// upright; with no line count chosen, two lines on screens narrower than 16:9.</summary>
    internal const string MicroLinesOption = "Utility/Lines=", MicroVerticalOption = "Utility/Vertical";
    private int MicroLines
    {
        get { for (int lines = 1; lines <= 4; lines++) if (Layout?.Option(MicroLinesOption + lines) == true) return lines; return Narrow ? 2 : 1; }
    }
    private bool MicroVertical => Layout?.Option(MicroVerticalOption) == true;
    /// <summary>The micro menu's grid (columns, rows), anchored in the bottom right corner.</summary>
    private (int Columns, int Rows) MicroGrid(int count)
    {
        int lines = Math.Clamp(MicroLines, 1, Math.Max(1, count)), along = (count + lines - 1) / lines;
        return MicroVertical ? (lines, along) : (along, lines);
    }
    /// <summary>A button of the micro menu: in reading order (down the columns when upright); the cells left empty
    /// when the count does not fill the grid are the first ones, so the block keeps to the corner.</summary>
    private EnbUiRect MicroRect(int i, int count)
    {
        var (columns, rows) = MicroGrid(count); int cell = i + columns * rows - count;
        int column = MicroVertical ? cell / rows : cell % columns, row = MicroVertical ? cell % rows : cell / columns;
        return new(size.Width - GutterX - (columns - column) * Micro - (columns - 1 - column) * MicroGap,
            size.Height - GutterY - (rows - row) * Micro - (rows - 1 - row) * MicroGap, Micro, Micro);
    }
    /// <summary>The whole micro menu block before the Utility placement.</summary>
    private EnbUiRect MicroExtent(int count)
    {
        var (columns, rows) = MicroGrid(count);
        double width = columns * Micro + (columns - 1) * MicroGap, height = rows * Micro + (rows - 1) * MicroGap;
        return new(size.Width - GutterX - width, size.Height - GutterY - height, width, height);
    }
    /// <summary>The micro menu's extent before the Utility placement (layout editing).</summary>
    public EnbUiRect MicroBounds { get { int n = MicroItems().Length; return size.Width > 0 ? Up(MicroExtent(n)) : default; } }
    /// <summary>The multibox button before the Utility placement; the multibox menu applies it.</summary>
    public EnbUiRect MultiboxButton
    {
        get { var items = MicroItems(); int i = Array.FindIndex(items, m => m.Key == "multibox"); return multiboxShown && i >= 0 && size.Width > 0 ? Up(MicroRect(i, items.Length)) : default; }
    }
    /// <summary>A micro menu button's screen rectangle (Utility placement applied).</summary>
    public EnbUiRect? MicroButtonBounds(string key)
    {
        var items = MicroItems(); int i = Array.FindIndex(items, m => m.Key == key);
        return i >= 0 && size.Width > 0 ? Apply(EnbUiGroup.Utility, MicroRect(i, items.Length)) : null;
    }
    private string MicroTip(string key) => key == "hud/MAP_BUT" && Station ? "Station Map" : Array.Find(MicroStationWithMultibox, m => m.Key == key).Tip ?? key;

    private readonly (EnbAuthoredUiAsset Asset, EnbUiRect Design)?[] utilityArt = new (EnbAuthoredUiAsset, EnbUiRect)?[8];
    private static readonly string[] UtilityKeys = ["hud/ICON03", "hud/ICON05", "hud/ICON02", "hud/ICON06", "hud/COMBAT_BAR", "hud/EXPLORE_BAR", "hud/TRADE_BAR", "hud/BARS_BOTTOM"];
    /// <summary>A TSI14A utility piece (icon or experience bar) in centred design space.</summary>
    private (EnbAuthoredUiAsset Asset, EnbUiRect Design)? UtilityIcon(string key)
    {
        int i = Array.IndexOf(UtilityKeys, key); if (i < 0) return null;
        if (utilityArt[i] is { } cachedArt) return cachedArt;
        foreach (var piece in EnbRenderSession.Of(assets).UtilityHud.Pieces)
            if (piece.Key == key) return utilityArt[i] = (piece.Asset, piece.Bounds.Translate(new(-320, -240)));
        return null;
    }

    /// <summary>What the open Modern UI menu belongs to (its button stays lit while it is open).</summary>
    internal Func<string?>? MenuOwner { get; set; }
    private string? openMenuButton;
    internal string? PlayerName => player?.Name;
    private bool RefreshMenus()
    {
        bool shown = MultiboxShown?.Invoke() == true; string? owner = MenuOwner?.Invoke();
        if (shown == multiboxShown && owner == openMenuButton) return false;
        multiboxShown = shown; openMenuButton = owner; return true;
    }
}
