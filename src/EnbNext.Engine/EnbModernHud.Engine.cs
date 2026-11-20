using EnbNext.Presentation;

namespace EnbNext.Engine;

/// <summary>The cockpit's engine state for the Modern UI engine panel.</summary>
internal readonly record struct EnbModernEngine(float WarpFill, bool WarpEngaged, bool WarpEnabled, int Thrust, bool CanThrust, string? Hovered, string? Pressed, bool Distress = false);

internal sealed partial class EnbModernHud
{
    // Engine controls left of the action bar: the retail hub's forward thrust, warp drive and reverse thrust
    // buttons in their authored arrangement, with the speed beside them. The buttons are the cockpit's own
    // gadgets (THROTTLE, WARP, REVERSE) at these rectangles.
    internal Func<EnbModernEngine>? Engine { get; set; }
    private EnbModernEngine engine;

    /// <summary>The three buttons' authored extent (design space).</summary>
    private EnbUiRect EngineDesign => pieces[(int)Piece.Throttle].Design.Union(pieces[(int)Piece.Reverse].Design).Union(pieces[(int)Piece.Warp].Design);
    /// <summary>Extra opening between the thrust arcs (modern units), and how much wider than authored the arcs are drawn,
    /// so the speed and the warp label fit in the opening either side of the warp button.</summary>
    private const double EngineOpening = 8, EngineStretch = 1.5;
    /// <summary>The hub (arcs and warp button) before the ActionBar placement.</summary>
    private EnbUiRect EngineRect
    {
        get
        {
            // The hub keeps its one-row size; beside a two-row bar it is centred on the rows.
            var first = SlotRect(0); double band = XpRect.Bottom - first.Y, height = Slot + 4 + XpRow, right = RoundRects.Autopilot.X - 6;
            var design = EngineDesign; double width = EngineStretch * (height - EngineOpening) * design.Width / Math.Max(1e-6, design.Height);
            return new(right - width, first.Y + (band - height) / 2, width, height);
        }
    }
    /// <summary>The buttons before the ActionBar placement: the authored arrangement with the arcs widened and moved apart,
    /// the warp button keeping its round shape in the middle; null until loaded.</summary>
    private (EnbUiRect Warp, EnbUiRect Throttle, EnbUiRect Reverse)? EngineButtons
    {
        get
        {
            if (!loaded || size.Width <= 0) return null;
            var box = EngineRect; var design = EngineDesign;
            var squeezed = box with { Height = box.Height - EngineOpening };
            var throttle = Map(pieces[(int)Piece.Throttle].Design, design, squeezed);
            var reverse = Map(pieces[(int)Piece.Reverse].Design, design, squeezed);
            var warp = Map(pieces[(int)Piece.Warp].Design, design, squeezed);
            warp = new(box.Center.X - warp.Height / 2, warp.Y + EngineOpening / 2, warp.Height, warp.Height);
            return (warp, throttle, reverse with { Y = reverse.Y + EngineOpening });
        }
    }
    /// <summary>A cockpit engine gadget's screen rectangle (Engine placement applied); the cockpit hit-tests it.</summary>
    public EnbUiRect? EngineBounds(string name) => EngineButtons is { } b
        ? Apply(EnbUiGroup.Engine, name switch { "WARP" => b.Warp, "THROTTLE" => b.Throttle, _ => b.Reverse }) : null;
    /// <summary>The engine block (hub, autopilot and damage meter buttons) before placement, for edit mode.</summary>
    public EnbUiRect EngineLayoutBounds => size.Width > 0 && loaded ? Up(EngineRect.Union(RoundRects.Autopilot).Union(RoundRects.Damage)) : default;

    private bool RefreshEngine()
    {
        var next = Engine?.Invoke() ?? default;
        if (next == engine) return false;
        engine = next; return true;
    }
}
