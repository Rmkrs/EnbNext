using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>One unit frame's values (player or target) as the Modern UI shows them.</summary>
internal readonly record struct EnbModernUnit(string Name, int? Level, EnbRgb NameColor,
    IEnbVitalsFrame? Vitals, float? EnergyPercent, float? EnergyMaximum,
    EnbSceneAvatarSnapshot? Avatar, EnbAvatarPortraitAsset? Pose, ulong Identity, string? Assessment = null);

/// <summary>Player status the Modern UI shows: speed, warp, experience progress and levels, and a targeted asteroid's contents.</summary>
internal readonly record struct EnbModernStatus(string? Speed, bool Warp, float CombatXp, float ExploreXp, float TradeXp,
    int CombatLevel, int ExploreLevel, int TradeLevel, ImmutableArray<string> Harvest)
{
    public bool Equals(EnbModernStatus other) => Speed == other.Speed && Warp == other.Warp && CombatXp == other.CombatXp && ExploreXp == other.ExploreXp &&
        TradeXp == other.TradeXp && CombatLevel == other.CombatLevel && ExploreLevel == other.ExploreLevel && TradeLevel == other.TradeLevel && SameLines(Harvest, other.Harvest);
    public static bool SameLines(ImmutableArray<string> a, ImmutableArray<string> b) => a.IsDefaultOrEmpty ? b.IsDefaultOrEmpty : !b.IsDefaultOrEmpty && a.SequenceEqual(b);
    public override int GetHashCode() => HashCode.Combine(Speed, Warp, CombatXp, ExploreXp, TradeXp);
}

/// <summary>The target's skill activation as the retail scanner's interruption gauges show it: label, interrupt and activation progress.</summary>
internal readonly record struct EnbModernCast(string Text, float Progress, float Activation, int State);

/// <summary>A target action the Modern UI target frame offers (the retail scanner's verb buttons).</summary>
/// <summary>A target verb button; Pilot: a follower's button (Next multibox shared harvesting), its name in the tooltip.</summary>
internal readonly record struct EnbModernVerb(uint Value, uint TargetId, bool Executable, EnbAuthoredUiAsset Asset, string? Pilot = null);

/// <summary>
/// Next "Modern UI" unit frames (opt-in): a WoW-style player frame and target frame built from the
/// game's own art. The frame is the dialog window frame, the portrait border is the target-portrait
/// frame (TSI05B), hull, shield and energy use the cockpit gauges (TSI01A), with their animated textures and damage trails. Portraits show the avatar's face;
/// a non-avatar target shows the scanner's live 3D model in the portrait square.
/// </summary>
internal sealed partial class EnbModernHud(EnbRetailAssetBundle assets) : IDisposable
{
    // Modern units: logical pixels at 760 lines, scaled up with the screen height (the retail HUD scales with the
    // resolution too). Name plates sit above the frames; the frame box holds the bars and one text row.
    public const double Width = 260, Plate = 18, Box = 48, TargetBox = Box / 2, Portrait = 56, Pad = 6, Bar = 13, Verb = 24, VerbGap = 3, Cycle = 22, Row = 12, TextSize = 12, PlateText = 13.5;
    private const double ReferenceHeight = 760;
    /// <summary>Name line and portrait (the box is centred on the portrait, which stands out above and below it); buffs below.</summary>
    private const double Height = Plate + Portrait + 4;
    // Hull, shield and reactor bars are the cockpit's (TSI01A BA/BA/BA and their gauges), so all three read alike.
    // The cockpit has no hull damage trail; the scanner's (TSI08A HULL_BA) fills that in.
    private enum Piece { HullHousing, Hull0, Hull1, ShieldHousing, Shield0, Shield1, Prev, PrevArrow, Next, NextArrow, EnergyHousing, Energy0, Energy1, Warp, WarpOn, Throttle, ThrottleOn, Reverse, ReverseOn, Count }
    private static readonly (string File, string Mesh)[] Sources =
    [
        ("tsi01a.w3d", "BAR01"), ("tsi01a.w3d", "HULL_BAR0"), ("tsi08a.w3d", "HULL_BAR1"), ("tsi01a.w3d", "BAR02"), ("tsi01a.w3d", "SHIELD_BAR0"), ("tsi01a.w3d", "SHIELD_BAR1"),
        ("tsi08a.w3d", "PREV_TARGET"), ("tsi08a.w3d", "LEFT"), ("tsi08a.w3d", "NEXT_TARGET"), ("tsi08a.w3d", "RIGHT"),
        ("tsi01a.w3d", "BAR00"), ("tsi01a.w3d", "REACTOR_BAR0"), ("tsi01a.w3d", "REACTOR_BAR1"),
        ("tsi01a.w3d", "WARP"), ("tsi01a.w3d", "WARP_ON"),
        ("tsi01a.w3d", "THROTTLE"), ("tsi01a.w3d", "THROTTLE_ON"), ("tsi01a.w3d", "REVERSE"), ("tsi01a.w3d", "REVERSE_ON"),
    ];
    private readonly (EnbAuthoredUiAsset Asset, EnbUiRect Design)[] pieces = new (EnbAuthoredUiAsset, EnbUiRect)[(int)Piece.Count];
    private readonly EnbConfirmationResources resources = EnbRenderSession.Of(assets).Confirmations;
    private readonly EnbOptionsPanelResources fonts = EnbRenderSession.Of(assets).OptionsPanels;
    private readonly List<(int Index, double Width, double Height, EnbAuthoredUiAsset Asset)> frames = [];
    private readonly List<EnbRetailText> labels = [];
    private readonly EnbRetailText[] tipLabels = Enumerable.Range(0, 20).Select(_ => new EnbRetailText()).ToArray();
    private readonly List<(string Key, EnbUiRect Rect, EnbUiGroup Group)> hits = [];
    private readonly EnbRetailGaugeTrail[] gauges = Enumerable.Range(0, MemberGauge + 2 * MaxMembers).Select(_ => new EnbRetailGaugeTrail()).ToArray();
    private readonly EnbCurrentRetailTooltipDelay delay = new();
    private EnbRetailIniDocument? strings;
    private EnbRetailScannerTooltipDefinition? gaugeTooltip;
    private bool loaded;
    private EnbModernUnit? player, target;
    private EnbModernStatus status;
    private ImmutableArray<EnbModernVerb> verbs = [];
    /// <summary>Target frame panel option: its action buttons enlarged ("TargetFrame/Verbs=125" ... "=200", percent).</summary>
    internal const string VerbSizeOption = "TargetFrame/Verbs=";
    internal static readonly int[] VerbSizes = [125, 150, 175, 200];
    private static readonly string[] VerbSizeKeys = Array.ConvertAll(VerbSizes, p => VerbSizeOption + p.ToString(CultureInfo.InvariantCulture));
    private double verbScale = 1;
    private string? readout;
    private EnbSceneEnvironmentSnapshot? environment;
    private ulong playerIdentity, targetIdentity;
    private EnbModernCast? cast;
    private long gaugeTime = Environment.TickCount64;
    private EnbUiPoint pointer = new(-100000, -100000);
    private string? hovered;
    private EnbUiTooltipDraw? cachedTooltip;
    /// <summary>The text the cached tooltip shows: a gauge tooltip follows its live value while it stays hovered.</summary>
    private string? cachedTooltipText;
    private ImmutableList<EnbUiModelDraw>? cached;
    private double cachedScale; private bool cachedEditing;
    internal EnbUiLayoutProfiles? Layout { get; set; }
    /// <summary>Next Modern UI options: percentages on the hull, shield and energy bars; percentage and/or values on the XP bars.</summary>
    internal (bool Vitals, bool XpPercent, bool XpValues) BarText
    {
        get => barText;
        set { if (barText != value) { barText = value; cached = null; } }
    }
    private (bool Vitals, bool XpPercent, bool XpValues) barText;
    /// <summary>Docked: only the micro menu, experience and group (the retail station keeps just its utility HUD).</summary>
    internal bool Station
    {
        get => station;
        set { if (station != value) { station = value; cached = null; cachedTooltip = null; } }
    }
    private bool station;
    /// <summary>Target portrait square in screen space (layout applied); the scanner renders a non-avatar target's model here.</summary>
    public EnbUiRect? TargetModelBounds { get; private set; }
    private int barArrangement = -1;
    /// <summary>What the Modern chat's built-in place keeps clear of (before placement, screen units): the player frame
    /// with its buffs, and the left edge of the action bar's engine hub. Null until the HUD has been drawn.</summary>
    public (EnbUiRect PlayerFrame, double ClusterLeft)? ChatClearance => size.Width > 0 && loaded ? (PlayerBounds, Up(EngineRect).X) : null;
    public EnbUiRect PlayerBounds { get; private set; }
    public EnbUiRect TargetBounds { get; private set; }
    /// <summary>The pointer is over a scanner-owned control: a verb ("verb", value), the cycle arrows ("prev"/"next"), or none (null).
    /// The scanner shows its own retail tooltips for these.</summary>
    public event Action<string?, uint, EnbUiPoint>? ScannerHovered;
    public event Action<EnbModernVerb>? VerbPressed;
    public event Action<bool>? CycleRequested;
    public event Action? ClearTargetRequested, TargetSelfRequested;

    public void Update(EnbModernUnit? self, EnbModernUnit? selected, ImmutableArray<EnbModernVerb> targetVerbs, string? targetReadout, EnbSceneEnvironmentSnapshot? scene, EnbModernStatus state,
        ImmutableArray<EnbSceneBuffSnapshot> selfBuffs, ImmutableArray<EnbSceneBuffSnapshot> selectedBuffs, long time, EnbModernCast? targetCast)
    {
        UpdateBuffs(selfBuffs, selectedBuffs, time);
        if (targetCast != cast) { cast = targetCast; cached = null; cachedTooltip = null; }
        if (self != player || selected != target || !targetVerbs.SequenceEqual(verbs) || targetReadout != readout || !Equals(scene, environment) || !state.Equals(status)) cached = null;
        if (!EnbModernStatus.SameLines(state.Harvest, status.Harvest) || targetReadout != readout) cachedTooltip = null;
        player = self; target = selected; verbs = targetVerbs; readout = targetReadout; environment = scene; status = state;
        if (self?.Identity != playerIdentity) { playerIdentity = self?.Identity ?? 0; Reset(0, self); }
        if (selected?.Identity != targetIdentity) { targetIdentity = selected?.Identity ?? 0; Reset(3, selected); cachedTooltip = null; }
        TargetModelBounds = selected is { Avatar: null } ? Apply(EnbUiGroup.TargetFrame, TargetPortrait) : null;
    }

    private void Reset(int first, EnbModernUnit? unit)
    {
        gauges[first].Reset(Hull(unit)); gauges[first + 1].Reset(Shield(unit));
        if (first == 0) gauges[2].Reset(unit?.EnergyPercent ?? 0);
    }
    private static float Hull(EnbModernUnit? unit) => unit?.Vitals is { MaximumHullPoints: >= 1 } v ? Math.Clamp((v.HullPoints ?? 0) / v.MaximumHullPoints.Value, 0, 1) : 0;
    private static float Shield(EnbModernUnit? unit) => unit?.Vitals?.ShieldPercent is float s && float.IsFinite(s) ? Math.Clamp(s, 0, 1) : 0;

    private EnbUiSize size, screen;
    private double zoom = 1;
    /// <summary>Modern units to logical screen units (before the layout placement).</summary>
    private EnbUiRect Up(EnbUiRect r) => zoom == 1 ? r : new(r.X * zoom, r.Y * zoom, r.Width * zoom, r.Height * zoom);
    /// <summary>The Modern UI option that turns the gutter rail off: the built-in places then keep only the small margin
    /// from the screen edge, as there is no band to stay clear of.</summary>
    internal static bool EdgeRailHidden { get; set; }
    /// <summary>The screen-edge gutter band (none while the rail is off) plus a small margin, in modern units; Modern UI
    /// elements stay inside it.</summary>
    internal static double GutterDesign => (EdgeRailHidden ? 0 : EnbGutterUi.Band) + 3;
    private double GutterX => GutterDesign * size.Width / 640;
    private double GutterY => GutterDesign * size.Height / 480;
    /// <summary>The unit frames sit a little above the action bar, either side of the centre.</summary>
    private double FramesY => SlotRect(0).Y - 4 - BuffArea - Height - FramesLift;
    private EnbUiRect PlayerRect => new(size.Width / 2 - Width - 130, FramesY, Width, Height);
    private EnbUiRect TargetRect => new(size.Width / 2 + 130, FramesY, Width, Height);
    /// <summary>Enlarged target action buttons: both frames stand higher (and stay level) by what two rows of them need beyond the buff area.</summary>
    private double FramesLift => Math.Max(0, 2 * (Verb * verbScale + VerbGap) - BuffArea);
    private static EnbUiRect PortraitOf(EnbUiRect r) => new(r.X + 6, r.Y + Plate + 2, Portrait, Portrait);
    private EnbUiRect TargetPortrait => PortraitOf(TargetRect);
    /// <summary>Where incoming damage numbers start (the retail cockpit anchors them on its shield or reactor bar):
    /// the player frame's bar, as a fraction of the screen.</summary>
    private static bool HasVitals(EnbModernUnit unit) => unit.Vitals is { } v && (v.MaximumHullPoints >= 1 || v.MaximumShieldPower > 0 || v.ShieldPercent > 0 || v.HullPoints > 0);
    private EnbUiRect Apply(EnbUiGroup group, EnbUiRect r) => Layout?.Apply(group, Up(r)) ?? Up(r);

    public ImmutableList<EnbUiModelDraw> Draw(EnbUiSize viewport, double scale)
    {
        bool editing = Layout?.Editing == true;
        if (!editing) previewBuffs = [];
        bool barChanged = RefreshBar() | RefreshMenus() | RefreshEngine();
        bool nameBeside = Layout?.Option(NameBesideOption) == true;
        if (nameBeside != memberNameBeside) { memberNameBeside = nameBeside; cached = null; }
        // The bar and micro menu arrangement (panel options, and the screen's shape when none is chosen).
        int arrangement = (Layout?.Option(BarOneRowOption) == true ? 1 : 0) | (Layout?.Option(BarTwoRowsOption) == true ? 2 : 0) | (MicroVertical ? 4 : 0);
        for (int lines = 1; lines <= 4; lines++) if (Layout?.Option(MicroLinesOption + lines) == true) arrangement |= 8 << lines;
        // The target's action buttons can be enlarged (panel option); 1x keeps the arrangement unchanged.
        verbScale = 1;
        for (int i = 0; i < VerbSizes.Length; i++) if (Layout?.Option(VerbSizeKeys[i]) == true) verbScale = VerbSizes[i] / 100.0;
        arrangement |= (int)Math.Round(verbScale * 100) << 10;
        if (arrangement != barArrangement) { barArrangement = arrangement; cached = null; }
        if (cached is not null && !barChanged && viewport == screen && scale == cachedScale && editing == cachedEditing && !memberAnimating && !Array.Exists(gauges, static g => g.Active)) return cached;
        screen = viewport; cachedScale = scale; cachedEditing = editing;
        zoom = Math.Max(1, viewport.Height / ReferenceHeight);
        size = new(viewport.Width / zoom, viewport.Height / zoom);
        hits.Clear();
        if (!loaded) Load();
        // Sizes come from the screen's font profile; the glyphs from the profile of the scaled-up size, so enlarged text stays sharp.
        var small = fonts.Font("FONT_SMALL", viewport, scale); var tiny = fonts.Font("FONT_TINY", viewport, scale) ?? small;
        if (small is null || tiny is null) return [];
        var sharpSize = new EnbUiSize(viewport.Width * zoom, viewport.Height * zoom);
        var smallSharp = fonts.Font("FONT_SMALL", sharpSize, scale) ?? small; var tinySharp = fonts.Font("FONT_TINY", sharpSize, scale) ?? tiny;
        double smallRatio = Ratio(small, smallSharp), tinyRatio = Ratio(tiny, tinySharp);
        var modern = EnbModernText.Renderer;
        long now = Environment.TickCount64; uint elapsed = (uint)Math.Clamp(now - gaugeTime, 0, 1000); gaugeTime = now;
        var draws = ImmutableList.CreateBuilder<EnbUiModelDraw>(); int labelIndex = 0;
        PlayerBounds = Up(PlayerRect with { Height = Height + BuffArea + FramesLift }); TargetBounds = Up(TargetRect with { Height = Height + BuffArea + FramesLift });
        if (!station && player is { } self) Unit(self, PlayerRect, false, EnbUiGroup.PlayerFrame, 0);
        var shown = station ? null : target ?? (editing ? new EnbModernUnit("Target", 34, new(1, .3f, .3f), PreviewVitals, null, null, null, null, 0, "Even") : null);
        if (shown is { } selected)
        {
            Unit(selected, TargetRect, true, EnbUiGroup.TargetFrame, 3);
        }
        Members();
        if (!station) { ActionBar(); PowerupBar(); TargetButtons(); }
        Experience();
        if (!station) EnginePanel();
        MicroMenu();
        if (zoom != 1)
            for (int i = 0; i < draws.Count; i++)
            {
                var d = draws[i];
                draws[i] = d with { Bounds = Up(d.Bounds), PortraitBounds = Up(d.PortraitBounds),
                    Images = d.Images.IsDefaultOrEmpty ? d.Images : ImmutableArray.CreateRange(d.Images, image => image with { Bounds = Up(image.Bounds) }) };
            }
        // Dragged shortcut icons follow the pointer in screen space.
        if (DragImages?.Invoke() is { IsDefaultOrEmpty: false } drag)
            draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, new(0, 0, screen.Width, screen.Height), false) { ImagesOnly = true, Images = drag });
        return cached = draws.ToImmutable();

        void ActionBar()
        {
            const EnbUiGroup group = EnbUiGroup.ActionBar;
            if (SlotArt?.Invoke() is not { } art) return;
            for (int i = 0; i < 12; i++)
            {
                var r = SlotRect(i); var slot = bar[i];
                draws.Add(new(art.Button, 0, r, slot.Hovered) { LayoutGroup = group, DesignBounds = art.ButtonDesign });
                var icon = Map(art.IconDesign, art.ButtonDesign, r);
                if (slot.View.Icon is { } raster) draws[^1] = draws[^1] with { Images = [new(raster, icon)] };
                if (slot.View.Highlight is { } lit) draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, r, false) { ImagesOnly = true, LayoutGroup = group, Images = [new(lit, icon)] });
                if (slot.View.Feedback is { } feedback) Feedback(feedback, r);
                Text(slot.View.Label, new(r.X + 3, r.Bottom - 13, r.Width - 6, 11), new(1, .93f, .55f), group, small, true);
            }
            void Feedback(EnbCombatIcon icon, EnbUiRect r)
            {
                if (icon.CannotUse) Overlay(Overlay_.NoSkill);
                if (icon.Powerup.HasValue) Overlay(Overlay_.Powerup);
                if (icon.NoAmmo) Overlay(Overlay_.NoAmmo);
                if (icon.Invalid) Overlay(Overlay_.Dark);
                if (icon.WrongTarget) Overlay(Overlay_.NoSkill);
                if (icon.State.Active) Overlay(Overlay_.Active);
                if (icon.State.Frame >= 0) { if (Overlay(Overlay_.Range)) draws[^1] = draws[^1] with { ShortcutRangeFrame = icon.State.Frame, ShortcutRangeAngle = icon.State.Angle }; }
                if (icon.State.Cooling) Overlay(Overlay_.Timer);
                bool Overlay(Overlay_ which)
                {
                    // Authored size and offset relative to the ring (itemon), as the group's remote shortcuts place them.
                    if (overlays[(int)which] is not { } overlay || overlays[(int)Overlay_.Active] is not { } ring) return false;
                    double sx = r.Width / ring.Design.Width, sy = r.Height / ring.Design.Height;
                    var bounds = new EnbUiRect(r.X + (overlay.Design.X - ring.Design.X) * sx, r.Y + (overlay.Design.Y - ring.Design.Y) * sy, overlay.Design.Width * sx, overlay.Design.Height * sy);
                    draws.Add(new(overlay.Asset, 0, bounds, false) { LayoutGroup = EnbUiGroup.ActionBar, DesignBounds = overlay.Design, AnimateMaterials = true });
                    return true;
                }
            }
        }

        // Next cast bar: centred above the action bar while an ability powers up; it moves with the bar.
        void PowerupBar()
        {
            const EnbUiGroup group = EnbUiGroup.ActionBar;
            if (powerup is not { } c) return;
            var first = SlotRect(0);
            const double width = 220, height = 18;
            var r = new EnbUiRect(size.Width / 2 - width / 2, first.Y - 6 - height, width, height);
            Frame(1, r, group);
            var fill = new EnbUiRect(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8);
            Fill(Piece.Energy0, fill, c.Progress, group);
            var label = new EnbUiRect(r.X + 6, r.Y, r.Width - 12, r.Height);
            Text(c.Name, label, new(1, 1, 1), group, small, false, textSize: 10, capCentered: true);
            Text((c.TenthsLeft / 10.0).ToString("0.0", CultureInfo.InvariantCulture), label, new(1, 1, 1), group, small, true, textSize: 10, capCentered: true);
        }

        void EnginePanel()
        {
            const EnbUiGroup group = EnbUiGroup.Engine;
            if (Engine is null || EngineButtons is not { } b) return;
            // The buttons belong to the cockpit's gadgets: the cockpit takes their input and tooltips.
            Piece_(engine.Thrust > 0 || engine.Pressed == "THROTTLE" ? Piece.ThrottleOn : Piece.Throttle, b.Throttle, group, engine.Hovered == "THROTTLE" && engine.CanThrust);
            hits.Add(("engine/THROTTLE", b.Throttle, group));
            Piece_(engine.Thrust < 0 || engine.Pressed == "REVERSE" ? Piece.ReverseOn : Piece.Reverse, b.Reverse, group, engine.Hovered == "REVERSE" && engine.CanThrust);
            hits.Add(("engine/REVERSE", b.Reverse, group));
            Piece_(engine.WarpEngaged ? Piece.WarpOn : Piece.Warp, b.Warp, group, engine.Hovered == "WARP" && engine.WarpEnabled);
            hits.Add(("engine/WARP", b.Warp, group));
            // As on the retail hub: the speed in the opening left of the warp button, "Warp" to its right,
            // lit when the drive can engage and blue while warping.
            var hub = EngineRect;
            Text(status.Speed ?? "-", new(hub.X, b.Warp.Center.Y - Row / 2, b.Warp.X - 2 - hub.X, Row), new(.88f, .92f, 1), group, small, false, center: true, textSize: 11);
            // Incapacitated, the button calls for help: retail 00584110 shows DISTRESS_CPIT instead of WARP_CPIT.
            EnbRgb warpColor = engine.Distress ? new(1, .62f, .2f) : engine.WarpEngaged ? new(.45f, .8f, 1) : engine.WarpEnabled ? new(.45f, 1, .45f) : new(.5f, .55f, .6f);
            Text(engine.Distress ? "Distress" : "Warp", new(b.Warp.Right + 2, b.Warp.Center.Y - Row / 2, hub.Right - b.Warp.Right - 2, Row), warpColor, group, small, false, center: true, textSize: 11);
        }

        void MicroMenu()
        {
            const EnbUiGroup group = EnbUiGroup.Utility;
            var items = MicroItems();
            // One strip: a single box behind all buttons; the hovered button lights up inside it.
            Frame(1, MicroExtent(items.Length), group);
            for (int i = 0; i < items.Length; i++)
            {
                var (key, _, symbol) = items[i]; var r = MicroRect(i, items.Length);
                bool hot = hovered == key || key == "multibox" && MultiboxHovered?.Invoke() == true || key == openMenuButton;
                if (hot) Frame(2, Shrink(r, .06), group);
                // The symbol at its exact pixel size, centred in the button.
                var inner = Shrink(r, .14); int pixels = Math.Max(8, (int)Math.Round(inner.Width * zoom * scale));
                if ((key == "next/Status" ? EnbModernSymbols.Signal(pixels, connectionLevel) : EnbModernSymbols.Get(symbol, pixels, hot)) is { } image)
                {
                    double side = pixels / (zoom * scale);
                    draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, inner, false) { ImagesOnly = true, LayoutGroup = group,
                        Images = [new(image, new(inner.Center.X - side / 2, inner.Center.Y - side / 2, side, side))] });
                }
                if (key != "multibox") hits.Add((key, r, group)); // The multibox menu owns its own button input.
            }
        }

        void Experience()
        {
            string[] keys = ["hud/COMBAT_BAR", "hud/EXPLORE_BAR", "hud/TRADE_BAR"];
            float[] values = [status.CombatXp, status.ExploreXp, status.TradeXp];
            int[] levels = [status.CombatLevel, status.ExploreLevel, status.TradeLevel];
            EnbRgb[] tints = [new(.35f, .9f, .9f), new(.45f, .6f, 1), new(.8f, .5f, 1)]; // the bars' own colours
            for (int i = 0; i < 3; i++)
            {
                // Each part, placed on its own: its level number on the left, then its bar.
                var group = XpGroups[i]; var part = XpPart(i);
                var number = part with { Width = XpLevel };
                Text(levels[i].ToString(CultureInfo.InvariantCulture), number, tints[i], group, small, true);
                var r = new EnbUiRect(number.Right + 3, part.Center.Y - Xp / 2, part.Right - number.Right - 3, Xp);
                hits.Add(("xp/" + i, part, group));
                if (UtilityIcon(keys[i]) is not { } art) continue;
                draws.Add(new(art.Asset, 0, r, false) { LayoutGroup = group, DesignBounds = art.Design, Stage0UvOffset = new(0, .46f * Math.Clamp(values[i], 0, 1)) });
                if (barText.XpPercent || barText.XpValues)
                {
                    string percent = Percent(values[i]);
                    string? amounts = EnbNet7Progression.Step(levels[i], values[i]) is { } step
                        ? step.Earned.ToString("N0", CultureInfo.InvariantCulture) + " / " + step.Required.ToString("N0", CultureInfo.InvariantCulture) : null;
                    string text = barText.XpValues && amounts is not null ? (barText.XpPercent ? amounts + " (" + percent + ")" : amounts) : percent;
                    BarLabel(text, new EnbUiRect(r.X, part.Center.Y - XpRow / 2, r.Width, XpRow), group, 9);
                }
            }
        }

        void Unit(EnbModernUnit unit, EnbUiRect r, bool isTarget, EnbUiGroup group, int gauge)
        {
            // One box per unit; the square portrait sits on its left, the bars beside it (no mirroring).
            // The player's box holds hull | shield over energy; the target's only its bars, so it is half as tall.
            // Objects without hull or shields (navs, stations) have no box: their name and distance stand beside the portrait.
            var portrait = PortraitOf(r);
            bool vitals = !isTarget || HasVitals(unit);
            double boxHeight = isTarget ? TargetBox : Box;
            var box = new EnbUiRect(r.X, portrait.Center.Y - boxHeight / 2, r.Width, boxHeight);
            double left = portrait.Right + Pad, right = box.Right - Pad - 2, barWidth = right - left;
            var lining = new EnbUiRect(portrait.X - 2, portrait.Y - 2, portrait.Width + 4, portrait.Height + 4);
            if (vitals) Frame(0, box, group);
            hits.Add((isTarget ? "target" : "player", (vitals ? box : new EnbUiRect(left, portrait.Y, right - left, portrait.Height)).Union(lining), group));
            // The level is known for the player and for NPCs; the server sends 0 for other players, so no number then.
            string? levelText = unit.Level is int known && known > 0 ? known.ToString(CultureInfo.InvariantCulture) : null;
            string[] info = isTarget && readout is { } text ? text.Split('\n') : [];
            double verbsRight = 0;
            string? distance = info.Length > 0 && info[0].Length > 0 ? info[0] : null;
            // The ETA follows the distance while it is known (the ship is moving); retail's "???" (not moving) is left out.
            string? eta = distance is not null && info.Length > 1 && info[1].Length > 0 && !info[1].EndsWith("???", StringComparison.Ordinal) ? info[1] : null;
            EnbRgb readoutColor = new(.8f, .85f, .9f);
            if (vitals)
            {
                // Name line above the box with the whole width for level and name; the target's distance and ETA
                // on their own line between it and the bars, beside the portrait.
                NameLine(new EnbUiRect(portrait.X, r.Y, right - portrait.X, Plate));
                if (isTarget) Readout(new EnbUiRect(left, r.Y + Plate, right - left, box.Y - r.Y - Plate));
            }
            else
            {
                // Two lines beside the portrait: level and name, then the distance.
                NameLine(new EnbUiRect(left, portrait.Center.Y - Plate - 1, right - left, Plate));
                Readout(new EnbUiRect(left, portrait.Center.Y + 1, right - left, Plate));
            }
            var hullShield = new EnbUiRect(left, box.Y + (isTarget ? (TargetBox - Bar) / 2 : (Box - 2 * Bar - 4) / 2), barWidth, Bar);
            if (vitals)
            {
                gauges[gauge].Update(Hull(unit), elapsed); gauges[gauge + 1].Update(Shield(unit), elapsed);
                if (isTarget && !(unit.Vitals?.MaximumHullPoints >= 1))
                {
                    // Shield-only targets (organics): one full-width shield bar instead of an empty hull bar.
                    var fill = new EnbUiRect(hullShield.X + Stop, hullShield.Y, hullShield.Width - 2 * Stop, hullShield.Height);
                    Fill(Piece.Shield1, fill, gauges[gauge + 1].Trail, group); Fill(Piece.Shield0, fill, Shield(unit), group);
                    Stops(fill, group);
                    if (barText.Vitals) BarLabel(Percent(Shield(unit)), fill, group, 10);
                    hits.Add(("target/shield", hullShield, group));
                }
                else
                {
                    // Hull and shield side by side (the cockpit's BA | BA split): stop, hull, stop | stop, shield, stop.
                    double inner = hullShield.Width - 4 * Stop, hullShare = pieces[(int)Piece.Hull0].Design.Width / (pieces[(int)Piece.Hull0].Design.Width + pieces[(int)Piece.Shield0].Design.Width);
                    var hullFill = new EnbUiRect(hullShield.X + Stop, hullShield.Y, inner * hullShare, hullShield.Height);
                    var shieldFill = new EnbUiRect(hullFill.Right + 2 * Stop, hullShield.Y, inner - hullFill.Width, hullShield.Height);
                    Fill(Piece.Hull1, hullFill, gauges[gauge].Trail, group); Fill(Piece.Hull0, hullFill, Hull(unit), group);
                    Fill(Piece.Shield1, shieldFill, gauges[gauge + 1].Trail, group); Fill(Piece.Shield0, shieldFill, Shield(unit), group);
                    Stops(hullFill, group); Stops(shieldFill, group);
                    if (barText.Vitals) { BarLabel(Percent(Hull(unit)), hullFill, group, 10); BarLabel(Percent(Shield(unit)), shieldFill, group, 10); }
                    var hullHousing = new EnbUiRect(hullShield.X, hullShield.Y, hullFill.Right + Stop - hullShield.X, hullShield.Height);
                    var shieldHousing = new EnbUiRect(hullHousing.Right, hullShield.Y, hullShield.Right - hullHousing.Right, hullShield.Height);
                    hits.Add((isTarget ? "target/hull" : "player/hull", hullHousing, group));
                    hits.Add((isTarget ? "target/shield" : "player/shield", shieldHousing, group));
                }
            }
            if (!isTarget)
            {
                var energy = new EnbUiRect(left, hullShield.Bottom + 4, barWidth, Bar);
                gauges[2].Update(unit.EnergyPercent ?? 0, elapsed);
                var energyFill = new EnbUiRect(energy.X + Stop, energy.Y, energy.Width - 2 * Stop, energy.Height);
                Fill(Piece.Energy1, energyFill, gauges[2].Trail, group); Fill(Piece.Energy0, energyFill, Math.Clamp(unit.EnergyPercent ?? 0, 0, 1), group);
                Stops(energyFill, group);
                if (barText.Vitals) BarLabel(Percent(Math.Clamp(unit.EnergyPercent ?? 0, 0, 1)), energyFill, group, 10);
                hits.Add(("player/energy", energy, group));
            }
            else
            {
                // The server's strength assessment of the target (the retail scanner's line under the name), under its bars.
                // Only when the frame shows no level (other players): a known level already stands before the name.
                if (vitals && levelText is null && unit.Assessment is { Length: > 0 } assessment)
                    Text(assessment, new EnbUiRect(left + 2, box.Bottom + 2, barWidth - 2, Row), unit.NameColor, group, small, false, textSize: 11);
                verbsRight = TargetVerbs(lining);
            }
            double below = Math.Max(vitals ? box.Bottom : 0, lining.Bottom);
            // Enlarged action buttons can reach past the portrait: what stands under the bars then starts right of them.
            double under = isTarget ? Math.Max(left, verbsRight + Pad) : left;
            // The target's skill activation (the retail scanner's interruption gauges) shows under its frame, above its buffs.
            // Edit mode previews it (target buffs are not sent by the server, so the target previews none).
            var shownCast = isTarget && editing ? PreviewCast : isTarget ? cast : null;
            if (shownCast is { } c) below = Cast(c, new EnbUiRect(under, below + 1, right - under, 11), group) + 2;
            Buffs(editing ? (isTarget ? [] : PreviewBuffs()) : isTarget ? targetBuffs : playerBuffs, under, below, group, isTarget ? 't' : 'p', right);
            if (isTarget && !editing && target is not null && !status.Harvest.IsDefaultOrEmpty)
                HarvestLines(under, below, right - under, r.Bottom + BuffArea);
            // Portrait: a square inset box (the dialog info box) with the avatar's face, or the scanner's live model,
            // which the HUD pass draws over the box.
            Frame(1, lining, group);
            if (isTarget) hits.Add(("target/portrait", portrait, group));
            if (unit.Avatar is { } avatar)
                draws[^1] = draws[^1] with { Portrait = avatar, PortraitEnvironment = environment, PortraitPose = unit.Pose, PortraitSharedActor = true,
                    PortraitCacheKey = isTarget ? "modern/target" : "modern/player", PortraitBounds = portrait, PortraitAspect = 1 };
            else if (isTarget && target is not null) draws[^1] = draws[^1] with { ScannerModel = true, PortraitBounds = portrait };

            // Distance, then the ETA at a fixed place after the widest distance, so neither moves as digits change.
            void Readout(EnbUiRect line)
            {
                if (distance is not null) Text(distance, line, readoutColor, group, small, false, large: true);
                double at = Math.Max(Measure("Dist: 000.00k", small, true), distance is null ? 0 : Measure(distance, small, true)) + 8;
                if (eta is not null) Text(eta, line with { X = line.X + at, Width = Math.Max(0, line.Width - at) }, readoutColor, group, small, false, large: true);
            }
            void NameLine(EnbUiRect line)
            {
                double lw = levelText is null ? 0 : Measure(levelText, small, true) + 7;
                var nameRect = line with { X = line.X + lw, Width = line.Width - lw };
                Text(Fit(unit.Name, nameRect.Width, small, true), nameRect, unit.NameColor, group, small, false, large: true);
                if (levelText is not null) Text(levelText, line with { Width = lw - 7 }, new(1, .93f, .55f), group, small, false, large: true);
            }
        }

        void Buffs(ImmutableArray<EnbSceneBuffSnapshot> list, double x, double y, EnbUiGroup group, char owner, double limit)
        {
            if (list.IsDefaultOrEmpty || buffCatalog is null) return;
            int n = 0, perRow = Math.Clamp((int)((limit + 2 + BuffGap - x) / (BuffIcon + BuffGap)), 1, BuffsPerRow);
            foreach (var buff in list.OrderBy(b => b.Slot))
            {
                if (n >= 2 * perRow || !buff.IsVisible(clientTime) || !buffCatalog.Definitions.TryGetValue(buff.Type, out var definition) || BuffArt(definition.Asset) is not { } art) continue;
                var r = new EnbUiRect(x + n % perRow * (BuffIcon + BuffGap), y + n / perRow * BuffRow, BuffIcon, BuffIcon); n++;
                string key = "buff/" + owner + "/" + buff.Slot.ToString(CultureInfo.InvariantCulture);
                draws.Add(new(art.Asset, 0, r, hovered == key) { LayoutGroup = group, DesignBounds = art.Design });
                hits.Add((key, r, group));
                if (!buff.IsPermanent && Remaining(buff.RemovalTime - clientTime) is var (value, unit))
                    Text(value.ToString(CultureInfo.InvariantCulture) + unit, new(r.X - BuffGap, r.Bottom, r.Width + 2 * BuffGap, BuffLabel), new(.9f, .92f, .95f), group, tiny, false, center: true, textSize: 9.5);
                if (buff.Elements.Length > 1)
                    Text(buff.Elements.Length.ToString(CultureInfo.InvariantCulture), new(r.Right - 9, r.Bottom - 9, 10, 9), new(1, .93f, .55f), group, tiny, true, textSize: 8.5);
            }
        }

        // The skill bar (activation progress, the ability's name over it) and the interrupt progress under it; bottom returned.
        double Cast(EnbModernCast c, EnbUiRect skill, EnbUiGroup group)
        {
            var skillFill = new EnbUiRect(skill.X + Stop, skill.Y, skill.Width - 2 * Stop, skill.Height);
            Fill(Piece.Energy0, skillFill, Math.Clamp(c.Activation, 0, 1), group); Stops(skillFill, group);
            Text(Fit(c.Text, skillFill.Width - 4, small, false), skillFill, new(1, .95f, .75f), group, small, false, center: true, textSize: 10);
            hits.Add(("cast/skill", skill, group));
            var interrupt = new EnbUiRect(skill.X, skill.Bottom + 2, skill.Width, 5);
            var interruptFill = new EnbUiRect(interrupt.X + Stop, interrupt.Y, interrupt.Width - 2 * Stop, interrupt.Height);
            Fill(Piece.Hull0, interruptFill, Math.Clamp(c.Progress, 0, 1), group); Stops(interruptFill, group);
            hits.Add(("cast/interrupt", interrupt, group));
            return interrupt.Bottom;
        }

        // The target's verb buttons (the scanner's, in its order) under its portrait, two per row, centred on it.
        // Follow lives in the target buttons panel instead.
        // An asteroid's contents for a prospecting pilot (retail's harvest page, 005BF790 / 005C0350 "name (count)"),
        // always shown under the target frame where a ship's buffs would be (asteroids have none): in the name and distance column (clear of
        // the verb buttons under the portrait), one column, two when they do not fit, the last line telling how many
        // more there are when even those are full.
        void HarvestLines(double x, double y, double width, double bottom)
        {
            const double line = 13;
            int perColumn = Math.Max(1, (int)((bottom - y) / line)), columns = status.Harvest.Length > perColumn ? 2 : 1;
            int capacity = perColumn * columns, count = status.Harvest.Length;
            double columnWidth = width / columns;
            for (int i = 0; i < Math.Min(count, capacity); i++)
            {
                string text = i == capacity - 1 && count > capacity
                    ? "and " + (count - capacity + 1).ToString(CultureInfo.InvariantCulture) + " more"
                    : status.Harvest[i];
                var rect = new EnbUiRect(x + i / perColumn * columnWidth, y + 1 + i % perColumn * line, columnWidth - 4, line);
                Text(text, rect, new(.85f, .9f, 1), EnbUiGroup.TargetFrame, small, false, textSize: 11.5);
            }
        }
        // Two buttons a row under the portrait. Enlarged buttons wider than the portrait align to its left edge
        // instead of centring, so they stay inside the frame; the right edge of the widest row is returned.
        double TargetVerbs(EnbUiRect lining)
        {
            const EnbUiGroup group = EnbUiGroup.TargetFrame;
            int follow = FollowIndex(), count = verbs.Length - (follow >= 0 ? 1 : 0), n = 0;
            double size = Verb * verbScale, edge = lining.X;
            for (int i = 0; i < verbs.Length; i++)
            {
                if (i == follow) continue;
                int inRow = Math.Min(2, count - n / 2 * 2);
                double rowWidth = inRow * size + (inRow - 1) * VerbGap, x0 = rowWidth > lining.Width ? lining.X : lining.Center.X - rowWidth / 2;
                var v = verbs[i]; var rect = new EnbUiRect(x0 + n % 2 * (size + VerbGap), lining.Bottom + 3 + n / 2 * (size + VerbGap), size, size); n++;
                edge = Math.Max(edge, rect.Right);
                draws.Add(new(v.Asset, v.Value, rect, hovered == "verb/" + i) { LayoutGroup = group });
                hits.Add(("verb/" + i, rect, group));
            }
            return edge;
        }

        // Next to the action bar: target cycling and Follow. Always shown; Follow is disabled until the target offers it.
        void TargetButtons()
        {
            const EnbUiGroup group = EnbUiGroup.TargetButtons;
            // Loose buttons: the panel itself draws nothing.
            Arrow(Piece.Prev, Piece.PrevArrow, TargetButtonCell(0), "cycle/prev"); Arrow(Piece.Next, Piece.NextArrow, TargetButtonCell(1), "cycle/next");
            var cell = TargetButtonCell(2); int follow = FollowIndex();
            if (follow >= 0)
            {
                var v = verbs[follow];
                draws.Add(new(v.Asset, v.Value, cell, hovered == "verb/" + follow) { LayoutGroup = group });
                hits.Add(("verb/" + follow, cell, group));
            }
            else if (FollowAsset() is { } idle) { draws.Add(new(idle, FollowKind | 1, cell, false) { LayoutGroup = group }); hits.Add(("follow", cell, group)); }
            // Only the arrows: the scanner's button plates carry its panel's line work, which shows as stripes on the frame.
            void Arrow(Piece frame, Piece arrow, EnbUiRect cell_, string key)
            {
                var rect = new EnbUiRect(cell_.Center.X - Cycle / 2, cell_.Center.Y - Cycle / 2, Cycle, Cycle);
                var a = pieces[(int)arrow].Design; var f = pieces[(int)frame].Design;
                Piece_(arrow, Map(a, f, rect), group, hovered == key); hits.Add((key, cell_, group));
            }
        }

        // The group frame: one compact row per other member (the name in the frame, then hull | shield), stacked upward
        // from the retail group panel's first row as the retail rows are; edit mode previews a full group.
        void Members()
        {
            const EnbUiGroup group = EnbUiGroup.ModernGroup;
            bool preview = editing;
            int count = preview ? 5 : Math.Min(members.Length, MaxMembers);
            memberAnimating = false; memberRowCount = 0; groupDesign = default;
            if (count == 0) return;
            // Two layouts (an edit mode panel option): the name on its own line in the frame over the bars (default), or
            // beside them in one row.
            bool beside = memberNameBeside;
            // Docked there are no ships to measure: the rows show names only, one line each.
            bool namesOnly = station && !preview;
            double width = beside ? MemberWidth : MemberWidthTall, boxHeight = beside || namesOnly ? MemberBox : MemberTall, step = boxHeight + MemberGap, nameRoom = beside ? MemberName : width - 2 * Pad;
            var (right, bottom) = GroupAnchor();
            double x = right - width, top = bottom - count * step + MemberGap;
            if (formationReady && !preview)
            {
                // The retail formation button flashes for this; here a line over the rows says so (its tooltip says how).
                var line = new EnbUiRect(x, top - Plate - 2, width, Plate);
                Text("Formation ready", line, new(1, .85f, .3f), group, small, false, large: true);
                hits.Add(("group/formation", line, group));
            }
            bool blink = Environment.TickCount64 / 400 % 2 == 0;
            for (int i = 0; i < count; i++)
            {
                var m = preview ? new EnbModernMember("Member " + (i + 1).ToString(CultureInfo.InvariantCulture), 0, 0, false) : members[i];
                bool present = preview || memberPresent[i];
                IEnbVitalsFrame? v = preview ? PreviewVitals : memberVitals[i];
                string index = i.ToString(CultureInfo.InvariantCulture);
                var box = new EnbUiRect(x, bottom - (i + 1) * step + MemberGap, width, boxHeight);
                memberRows[i] = box; memberRowCount = i + 1;
                bool hot = hovered is { } h && h.StartsWith("group/", StringComparison.Ordinal) && h != "group/formation" && MemberIndex(h) == i;
                Frame(0, box, group);
                if (m.Attention) memberAnimating = true;
                // Hover lights the frame up; a member asking for help pulses gold. The frame itself stays as it is.
                if (hot || m.Attention && blink)
                    draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, box, false) { ImagesOnly = true, LayoutGroup = group,
                        Images = [new(m.Attention && blink ? EnbModernIcons.Fill(new(1, .8f, .3f), 46) : EnbModernIcons.Fill(new(.6f, .75f, 1), 26), new EnbUiRect(box.X + 3, box.Y + 3, box.Width - 6, box.Height - 6))] });
                hits.Add(("group/member/" + index, box, group));
                if (namesOnly)
                {
                    Text(Fit(m.Name, box.Width - 2 * Pad - 2, small, true), new EnbUiRect(box.X + Pad + 1, box.Y, box.Width - 2 * Pad - 2, box.Height), new(.92f, .94f, 1), group, small, false, large: true);
                    continue;
                }
                // The name in the frame; a member out of range is dimmed. A symbol the member sent ends the name area.
                double symbol = m.Symbol is null ? 0 : (beside ? MemberBox : Plate) - 4;
                EnbRgb name = present ? new(.92f, .94f, 1) : new(.58f, .62f, .7f);
                var nameArea = beside ? new EnbUiRect(box.X + Pad + 1, box.Y, nameRoom - symbol - 4, box.Height) : new EnbUiRect(box.X + Pad + 1, box.Y + 2, nameRoom - symbol - 2, Plate);
                Text(Fit(m.Name, nameArea.Width, small, true), nameArea, name, group, small, false, large: true);
                if (m.Symbol is { } art)
                {
                    var r = new EnbUiRect(box.X + Pad + nameRoom - symbol - (beside ? 2 : 0), nameArea.Y + (nameArea.Height - symbol) / 2, symbol, symbol);
                    draws.Add(new(art, 0, r, false) { LayoutGroup = group, DesignBounds = m.SymbolDesign, AnimateMaterials = true, MaterialTime = unchecked((uint)(now - m.SymbolSince)) });
                    memberAnimating = true;
                    if (m.SymbolContext) hits.Add(("group/symbol/" + index, r, group));
                }
                // Hull | shield, as on the target frame, beside the name.
                var bars = beside ? new EnbUiRect(box.X + Pad + nameRoom, box.Y + (MemberBox - Bar) / 2, box.Width - 2 * Pad - nameRoom, Bar)
                    : new EnbUiRect(box.X + Pad, box.Bottom - Pad - Bar, box.Width - 2 * Pad, Bar);
                float hull = present ? MemberHull(v) : 0, shield = present ? MemberShield(v) : 0;
                var hullGauge = gauges[MemberGauge + 2 * i]; var shieldGauge = gauges[MemberGauge + 2 * i + 1];
                hullGauge.Update(hull, elapsed); shieldGauge.Update(shield, elapsed);
                double inner = bars.Width - 4 * Stop, hullShare = pieces[(int)Piece.Hull0].Design.Width / (pieces[(int)Piece.Hull0].Design.Width + pieces[(int)Piece.Shield0].Design.Width);
                // Multibox: one of the player's own characters that shares its reactor has a green segment first, left of
                // the hull; the hull keeps its length and the shield gives up the room. The name and other rows are unchanged.
                double energyWidth = m.Reactor is null ? 0 : Math.Round(inner * (1 - hullShare) * .45);
                var energyFill = new EnbUiRect(bars.X + Stop, bars.Y, energyWidth, bars.Height);
                if (energyWidth > 0) inner -= energyWidth + 2 * Stop;
                var hullFill = new EnbUiRect(energyWidth > 0 ? energyFill.Right + 2 * Stop : bars.X + Stop, bars.Y, (bars.Width - 4 * Stop) * hullShare, bars.Height);
                var shieldFill = new EnbUiRect(hullFill.Right + 2 * Stop, bars.Y, inner - hullFill.Width, bars.Height);
                Fill(Piece.Hull1, hullFill, hullGauge.Trail, group); Fill(Piece.Hull0, hullFill, hull, group);
                Fill(Piece.Shield1, shieldFill, shieldGauge.Trail, group); Fill(Piece.Shield0, shieldFill, shield, group);
                Stops(hullFill, group); Stops(shieldFill, group);
                if (barText.Vitals && present) { BarLabel(Percent(hull), hullFill, group, 10); BarLabel(Percent(shield), shieldFill, group, 10); }
                double hullLeft = energyWidth > 0 ? energyFill.Right + Stop : bars.X;
                var hullHousing = new EnbUiRect(hullLeft, bars.Y, hullFill.Right + Stop - hullLeft, bars.Height);
                hits.Add(("group/hull/" + index, hullHousing, group));
                hits.Add(("group/shield/" + index, new EnbUiRect(hullHousing.Right, bars.Y, bars.Right - hullHousing.Right, bars.Height), group));
                if (m.Reactor is int reactor)
                {
                    float energy = reactor / 100f;
                    Fill(Piece.Energy1, energyFill, energy, group); Fill(Piece.Energy0, energyFill, energy, group);
                    Stops(energyFill, group);
                    if (barText.Vitals) BarLabel(Percent(energy), energyFill, group, 10);
                    hits.Add(("group/reactor/" + index, new EnbUiRect(bars.X, bars.Y, hullLeft - bars.X, bars.Height), group));
                }
            }
            groupDesign = new(x, top, width, bottom - top);
        }

        // Text: the host's clear font when it provides one (sized in modern units), else the game's font.
        float Size(EnbRetailFontFace face, bool large = false, double? points = null) => (float)((points ?? (large ? PlateText : face == tiny ? TextSize - 2 : TextSize)) * zoom * scale);
        double Measure(string value, EnbRetailFontFace face, bool large = false) => modern is { } m ? m.Measure(value, Size(face, large)) / (zoom * scale) : face.MeasureWidth(value) * face.Scale;
        // Shortens text with "..." until it fits the room.
        string Fit(string value, double room, EnbRetailFontFace face, bool large = false)
        {
            if (Measure(value, face, large) <= room) return value;
            while (value.Length > 1 && Measure(value + "...", face, large) > room) value = value[..^1];
            return value.TrimEnd() + "...";
        }

        // The bar's end stops either side of a gauge, over its full height.
        void Stops(EnbUiRect fill, EnbUiGroup group)
        {
            if (leftStop is null || rightStop is null) return;
            draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, fill, false) { ImagesOnly = true, LayoutGroup = group,
                Images = [new(leftStop, new(fill.X - Stop, fill.Y, Stop, fill.Height)), new(rightStop, new(fill.Right, fill.Y, Stop, fill.Height))] });
        }
        void Fill(Piece bar, EnbUiRect rect, float value, EnbUiGroup group)
        {
            var (asset, design) = pieces[(int)bar];
            draws.Add(new(asset, 0, rect, false)
            { LayoutGroup = group, DesignBounds = design, Stage0UvOffset = new(0, EnbCurrentRetailGaugeRuntime.TextureOffset(value)), AnimateMaterials = true });
        }
        void Piece_(Piece piece, EnbUiRect rect, EnbUiGroup group, bool hover = false)
        {
            var (asset, design) = pieces[(int)piece];
            draws.Add(new(asset, 0, rect, hover) { LayoutGroup = group, DesignBounds = design });
        }
        void Frame(int index, EnbUiRect rect, EnbUiGroup group)
        {
            var art = frames.FirstOrDefault(f => f.Index == index && f.Width == rect.Width && f.Height == rect.Height).Asset;
            if (art is null) { var source = resources.Frames[index]; art = source.Asset with { Geometry = source.Resize.Geometry((float)rect.Width, (float)rect.Height) }; frames.Add((index, rect.Width, rect.Height, art)); }
            var min = art.Geometry!.Positions.Aggregate(Vector3.Min); var max = art.Geometry.Positions.Aggregate(Vector3.Max);
            draws.Add(new(art, 0, rect, false) { LayoutGroup = group, DesignBounds = new(min.X, -max.Y, max.X - min.X, max.Y - min.Y) });
        }
        // Bar text (Next option): white, outlined like the speed readout, centred on the bar.
        void BarLabel(string text, EnbUiRect rect, EnbUiGroup group, double size) => Text(text, rect, new(1, 1, 1), group, small, false, center: true, textSize: size, capCentered: true);
        static string Percent(float fraction) => Math.Floor(Math.Clamp(fraction, 0, 1) * 100).ToString(CultureInfo.InvariantCulture) + "%";
        void Text(string text, EnbUiRect rect, EnbRgb color, EnbUiGroup group, EnbRetailFontFace face, bool right, bool center = false, bool large = false, double? textSize = null, bool capCentered = false)
        {
            if (modern is { } m)
            {
                float px = Size(face, large, textSize);
                if (m.Render(text, px, color) is not { } image) return;
                double unitPx = zoom * scale, pad = m.Padding(px) / unitPx, iw = image.Width / unitPx, ih = image.Height / unitPx;
                double fitWidth = Math.Min(1, rect.Width / Math.Max(1e-6, iw - 2 * pad));
                iw *= fitWidth; ih *= fitWidth; pad *= fitWidth;
                double textWidth = iw - 2 * pad;
                double tx = center ? rect.Center.X - textWidth / 2 : right ? rect.Right - textWidth : rect.X;
                // Bar text centres its capitals and digits, not the line box (whose descender room sits below them).
                double ty = rect.Center.Y - ih / 2;
                // Whole pixels keep the small outlined digits sharp and their centring exact.
                if (capCentered) ty = Math.Round((ty - m.InkCenterOffset(text, px) / unitPx * fitWidth) * unitPx) / unitPx;
                draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, rect, false) { ImagesOnly = true, LayoutGroup = group,
                    Images = [new(image, new(tx - pad, ty, iw, ih))] });
                return;
            }
            if (labelIndex == labels.Count) labels.Add(new()); var label = labels[labelIndex++];
            var (sharp, ratio) = face == tiny ? (tinySharp, tinyRatio) : (smallSharp, smallRatio);
            label.SetText(text, sharp, color); label.SetDisplayScale(1, 1);
            if (label.Raster is not { } raster) return;
            // Never larger than its box: shrink uniformly when the font is taller or wider than the slot.
            double fit = Math.Min(ratio, Math.Min(rect.Width / label.Width, rect.Height / label.Height));
            double w = label.Width * fit, h = label.Height * fit;
            double lx = center ? rect.Center.X - w / 2 : right ? rect.Right - w : rect.X;
            draws.Add(new EnbUiModelDraw(fonts.Main.Asset, 0, rect, false) { ImagesOnly = true, LayoutGroup = group,
                Images = [new(raster, new(lx, rect.Center.Y - h / 2, w, h))] });
        }
    }

    /// <summary>Size of <paramref name="face"/> relative to <paramref name="sharp"/> (the larger profile's glyphs drawn at the face's size).</summary>
    private static double Ratio(EnbRetailFontFace face, EnbRetailFontFace sharp)
    {
        const string sample = "0123456789 Speed";
        double a = face.MeasureWidth(sample) * face.Scale, b = sharp.MeasureWidth(sample) * sharp.Scale;
        return a > 0 && b > 0 ? a / b : 1;
    }
    private static EnbUiRect Shrink(EnbUiRect r, double by) => new(r.X + r.Width * by, r.Y + r.Height * by, r.Width * (1 - 2 * by), r.Height * (1 - 2 * by));

    /// <summary>Places an authored piece inside <paramref name="target"/> as it sits inside its authored housing.</summary>
    private static EnbUiRect Map(EnbUiRect design, EnbUiRect housing, EnbUiRect target)
    {
        double sx = target.Width / Math.Max(1e-6, housing.Width), sy = target.Height / Math.Max(1e-6, housing.Height);
        return new(target.X + (design.X - housing.X) * sx, target.Y + (design.Y - housing.Y) * sy, design.Width * sx, design.Height * sy);
    }

    private void Load()
    {
        loaded = true;
        foreach (var file in Sources.Select(s => s.File).Distinct())
        {
            var asset = EnbAuthoredUiAsset.Load(assets, file) ?? throw new InvalidDataException(file + " unavailable.");
            for (int i = 0; i < Sources.Length; i++)
            {
                if (Sources[i].File != file) continue;
                var mesh = asset.Resource.Meshes.First(m => m.Name == Sources[i].Mesh);
                pieces[i] = (asset with { Resource = asset.Resource.Copy().SetMeshes([mesh]) }, EnbCockpitUi.MeshBounds(asset, mesh.Name).Translate(new(-320, -240)));
            }
        }
        LoadStops();
        try { buffCatalog = EnbRetailBuffCatalog.Load(assets.Paths.ClientIniDirectory); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { buffCatalog = null; } // No buff rows.
        strings = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini"));
        gaugeTooltip = EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
    }

    private string? HitAt(EnbUiPoint screen)
    {
        for (int i = hits.Count - 1; i >= 0; i--)
        {
            var (key, rect, group) = hits[i];
            var p = Layout?.Inverse(group, screen) ?? screen;
            if (rect.Contains(new(p.X / zoom, p.Y / zoom))) return key;
        }
        return null;
    }

    public bool Contains(EnbUiPoint screen) => HitAt(screen) is { } key && !key.StartsWith("engine/", StringComparison.Ordinal);
    /// <summary>A control's screen rectangle (layout applied), for tests and pointer warps.</summary>
    internal EnbUiRect? HitRect(string key) { foreach (var (k, r, g) in hits) if (k == key) return Apply(g, r); return null; }

    public void Pointer(EnbUiPoint screen)
    {
        pointer = screen; string? now = HitAt(screen);
        if (now == hovered) return;
        hovered = now; cachedTooltip = null; cached = null;
        if (now?.StartsWith("verb/", StringComparison.Ordinal) == true && int.Parse(now.AsSpan(5), CultureInfo.InvariantCulture) is int i && i < verbs.Length)
            ScannerHovered?.Invoke(verbs[i].Pilot is null ? "verb" : null, verbs[i].Value, screen);
        else if (now is "cycle/prev" or "cycle/next") ScannerHovered?.Invoke(now[6..], 0, screen);
        else if (now == "follow") ScannerHovered?.Invoke("verb", FollowKind, screen); // its retail tooltip, though disabled
        else ScannerHovered?.Invoke(null, 0, screen);
    }

    /// <summary>Next "Right-click on target to cancel targeting" (Quality of Life, off by default).</summary>
    internal bool RightClickCancelsTarget { get; set; }

    public bool Press(EnbUiPoint screen, bool right)
    {
        string? key = HitAt(screen);
        // Engine buttons are the cockpit's gadgets; leave their input to it.
        if (key is null || key.StartsWith("engine/", StringComparison.Ordinal)) return false;
        // A member row: a click targets the member (its symbol, when it asks for something, answers it); a right-click opens its menu.
        if (key.StartsWith("group/", StringComparison.Ordinal))
        {
            // The formation choices are in the member rows' menu (with the group's other commands).
            if (key == "group/formation") { if (members.Length > 0) MemberMenuRequested?.Invoke(0, screen); }
            else if (MemberIndex(key) is int member && member < members.Length)
            {
                if (right) MemberMenuRequested?.Invoke(member, screen);
                else if (key.StartsWith("group/symbol/", StringComparison.Ordinal)) MemberSymbolPressed?.Invoke(member);
                else MemberPressed?.Invoke(member);
            }
            return true;
        }
        if (right)
        {
            // Next: a right-click cancels the target only when the player chose it (Quality of Life); otherwise it is free for a menu.
            if (key.StartsWith("target", StringComparison.Ordinal) && target is not null) { if (RightClickCancelsTarget) ClearTargetRequested?.Invoke(); else TargetMenuRequested?.Invoke(screen); }
            return true;
        }
        if (key == "next/Help") { if (MicroButtonBounds(key) is { } help) HelpRequested?.Invoke(help); }
        else if (key == "next/Social") { if (MicroButtonBounds(key) is { } social) SocialRequested?.Invoke(social); }
        else if (key == "next/Log") CaptainLogRequested?.Invoke();
        else if (key == "next/Inventory") InventoryRequested?.Invoke();
        else if (key == "next/Vault") VaultRequested?.Invoke();
        else if (key == "next/Skills") SkillsRequested?.Invoke();
        else if (key == "next/Missions") MissionsRequested?.Invoke();
        else if (key == "next/Factions") FactionsRequested?.Invoke();
        else if (key == "hud/MAP_BUT" && MapRequested is { } map) map(false);
        else if (key == "next/Galaxy") MapRequested?.Invoke(true);
        else if (key == "next/Local") LocalRequested?.Invoke();
        else if (key == "next/Music") MusicRequested?.Invoke();
        else if (key == "next/Items") ItemsRequested?.Invoke();
        else if (key == "hud/SHIP_BUT" && EquipmentRequested is { } equipment) equipment();
        else if (key.StartsWith("hud/", StringComparison.Ordinal)) UtilityRequested?.Invoke(key);
        else if (key == "cycle/prev" || key == "cycle/next") CycleRequested?.Invoke(key == "cycle/prev");
        else if (key.StartsWith("verb/", StringComparison.Ordinal) && int.Parse(key.AsSpan(5), CultureInfo.InvariantCulture) is int i && i < verbs.Length && verbs[i].Executable) VerbPressed?.Invoke(verbs[i]);
        else if (key.StartsWith("player", StringComparison.Ordinal)) TargetSelfRequested?.Invoke();
        return true;
    }

    public bool OwnsTooltip => hovered is "player/hull" or "player/shield" or "player/energy" or "target/hull" or "target/shield"
        || hovered is "target/portrait" && !status.Harvest.IsDefaultOrEmpty
        || hovered?.StartsWith("hud/", StringComparison.Ordinal) == true || hovered?.StartsWith("xp/", StringComparison.Ordinal) == true || hovered?.StartsWith("next/", StringComparison.Ordinal) == true
        || hovered?.StartsWith("buff/", StringComparison.Ordinal) == true || hovered is "cast/interrupt" or "cast/skill"
        || hovered is "group/formation" || PilotVerb(hovered) is not null || hovered?.StartsWith("group/hull/", StringComparison.Ordinal) == true || hovered?.StartsWith("group/shield/", StringComparison.Ordinal) == true
        || hovered?.StartsWith("group/reactor/", StringComparison.Ordinal) == true;

    /// <summary>The game's gauge tooltips (tooltip.ini UI_POWER for the player, the scanner gauge text for the target).</summary>
    public EnbUiTooltipDraw? Tooltip(EnbUiSize viewport, double scale)
    {
        if (!OwnsTooltip) { delay.Reset(); return null; }
        if (!delay.IsReady(hovered!.GetHashCode(), Environment.TickCount64, EnbCurrentRetailTooltipDelay.Milliseconds(assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay))) return null;
        var resources_ = EnbRenderSession.Of(assets).TooltipResources;
        if (resources_.Frame is not { } frame || resources_.Definition is not { } definition || fonts.Font(definition.FontId, viewport, scale) is not { } font) return null;
        // Next: a buff is shown as a card (its words are the card's, so a changed card is drawn again).
        var card = hovered is ['b', 'u', 'f', 'f', '/', ..] ? BuffCard(hovered) : null;
        string? text = card is not null ? card.Key() : hovered switch
        {
            "target/hull" => gaugeTooltip?.FormatGauge(true, target?.Vitals?.HullPoints, target?.Vitals?.MaximumHullPoints),
            "target/shield" => gaugeTooltip?.FormatGauge(false, target?.Vitals?.ShieldPercent * target?.Vitals?.MaximumShieldPower, target?.Vitals?.MaximumShieldPower),
            "next/Status" => connectionTip,
            ['h', 'u', 'd', '/', ..] or ['n', 'e', 'x', 't', '/', ..] => MicroTip(hovered),
            ['x', 'p', '/', ..] => Experience(hovered[3] - '0'),
            "target/portrait" => TargetTip(),
            "cast/interrupt" => gaugeTooltip?.InterruptProgressText.Replace("%d", ((int)((cast?.Progress ?? 0) * 100)).ToString(CultureInfo.InvariantCulture)).Replace("%%", "%"),
            "cast/skill" => gaugeTooltip?.ActivationProgressText.Replace("%d", ((int)((cast?.Activation ?? 0) * 100)).ToString(CultureInfo.InvariantCulture)).Replace("%%", "%"),
            ['b', 'u', 'f', 'f', '/', ..] => BuffTip(hovered),
            ['g', 'r', 'o', 'u', 'p', '/', ..] => MemberTip(hovered),
            ['v', 'e', 'r', 'b', '/', ..] => PilotVerb(hovered) is { } pilot ? "Harvest (" + pilot.Pilot + ")" + (pilot.Executable ? "" : "\nOut of range") : null,
            _ => Power(),
        };
        if (string.IsNullOrEmpty(text)) return null;
        // The text is read again each time, so a hovered gauge's numbers follow the hits; the drawing is rebuilt only when
        // they change.
        if (cachedTooltip is not null && text == cachedTooltipText) return cachedTooltip;
        cachedTooltipText = text;
        var draw = EnbTooltipContent.Create(frame, font, card?.Title ?? text, viewport, scale, new(.1f, 1, .1f), pointer, i => tipLabels[i]);
        return cachedTooltip = card is not null && draw is not null ? draw with { Card = card } : draw;
    }

    /// <summary>The hovered verb button when it is a follower's (Next multibox shared harvesting).</summary>
    private EnbModernVerb? PilotVerb(string? key) => key is ['v', 'e', 'r', 'b', '/', ..] && int.TryParse(key.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int i) && i < verbs.Length && verbs[i].Pilot is not null ? verbs[i] : null;

    /// <summary>Target portrait tooltip: a prospected asteroid's contents (the ETA stands beside the distance).</summary>
    private string? TargetTip() => status.Harvest.IsDefaultOrEmpty ? null : string.Join("\n", status.Harvest);

    private string Experience(int kind)
    {
        var (label, level, value) = kind switch { 0 => ("Combat", status.CombatLevel, status.CombatXp), 1 => ("Exploration", status.ExploreLevel, status.ExploreXp), _ => ("Trade", status.TradeLevel, status.TradeXp) };
        // The server sends the step fraction; the Net-7 step sizes give the experience values.
        if (EnbNet7Progression.Step(level, value) is { } step)
            return $"{label} level {level}\n{step.Earned.ToString("N0", CultureInfo.InvariantCulture)} / {step.Required.ToString("N0", CultureInfo.InvariantCulture)} XP ({Math.Floor(Math.Clamp(value, 0, 1) * 100)}%)\n{(step.Required - step.Earned).ToString("N0", CultureInfo.InvariantCulture)} XP to level {level + 1}";
        return $"{label} level {level}\n{Math.Clamp(value, 0, 1) * 100:0}% to the next level";
    }

    /// <summary>Cockpit gauge tooltip (EnbCockpitUi, tooltip.ini UI_POWER) for the player's bars.</summary>
    private string? Power()
    {
        var v = player?.Vitals;
        string key = hovered switch { "player/hull" => "HULL", "player/shield" => "SHIELDS", _ => "ENERGY" };
        float? max = key == "ENERGY" ? player?.EnergyMaximum : key == "HULL" ? v?.MaximumHullPoints : v?.MaximumShieldPower;
        float? value = key == "ENERGY" ? player?.EnergyPercent * max : key == "HULL" ? v?.HullPoints : v?.ShieldPercent * max;
        max ??= 0; value ??= 0;
        if (key == "SHIELDS" && !(max > 0)) key = "NO_SHIELDS";
        else if (key == "HULL") { if (max >= 1) value = Math.Clamp(value.Value, 0, max.Value); else { value = 0; max = 0; } }
        else value = value < 0 ? 0 : value > max ? max : value;
        if (strings?.TryGetSection("UI_POWER", out var section) != true) return null;
        string message = section.GetValueOrDefault(key, "").Replace("\\n", "\n");
        foreach (float number in new[] { value ?? 0, max ?? 0 })
        {
            int pos = message.IndexOf("%d", StringComparison.Ordinal);
            if (pos >= 0) message = message[..pos] + ((int)number).ToString(CultureInfo.InvariantCulture) + message[(pos + 2)..];
        }
        return message;
    }

    public void Dispose() { foreach (var label in labels) label.Dispose(); labels.Clear(); foreach (var label in tipLabels) label.Dispose(); }
}
