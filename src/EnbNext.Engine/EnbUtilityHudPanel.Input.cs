using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbUtilityHudPanel : IEnbUiGadgetHandler
{
    private EnbUiGadgetPointer? pointer;
    public EnbUiGadgetPointer Pointer => pointer ??= new(7, custom: this);
    public static string? InputKey(int index) => index switch
    {
        0 => "hud/EXPLORE_BAR", 1 => "hud/TRADE_BAR", 2 => "hud/COMBAT_BAR",
        3 => "hud/OPTIONS_BUT", 4 => "hud/SHIP_BUT", 5 => "hud/CHARACTER_BUT", 6 => "hud/MAP_BUT", _ => null
    };
    private static int InputIndex(string key) => key switch
    {
        "hud/EXPLORE_BAR" => 0, "hud/TRADE_BAR" => 1, "hud/COMBAT_BAR" => 2,
        "hud/OPTIONS_BUT" => 3, "hud/SHIP_BUT" => 4, "hud/CHARACTER_BUT" => 5, "hud/MAP_BUT" => 6, _ => -1
    };
    public uint HitMask(float x, float y)
    {
        if(UiLayout is {} layout && !layout.Placement(EnbUiGroup.Utility).IsIdentity)
        {
            double w=Math.Max(1,Math.Round(layout.Size.Width*pixelScale)-1),h=Math.Max(1,Math.Round(layout.Size.Height*pixelScale)-1);
            var p=layout.Inverse(EnbUiGroup.Utility,new(x*w/pixelScale,y*h/pixelScale));
            x=(float)(p.X*pixelScale/w);y=(float)(p.Y*pixelScale/h);
        }
        uint mask = 0;
        // With the fifth (multibox) button the bars and their backing are drawn
        // one button spacing wider; their hits cover the same stretched extent.
        double extra = MultiboxButton ? Spacing() / 640 : 0;
        float Compressed(EnbUiRect design, float px)
        {
            double left = design.X / 640, width = design.Width / 640;
            return extra > 0 && px > left + width && px <= left + width + extra ? (float)(left + (px - left) * width / (width + extra)) : px;
        }
        EnbUiRect backing = default;
        foreach (var piece in resources.Pieces)
        {
            if (piece.Key == "hud/BARS_BOTTOM") backing = piece.Bounds;
            int index = InputIndex(piece.Key);
            if (index >= 0 && piece.Query.Contains(index < 3 ? Compressed(piece.Bounds, x) : x, y)) mask |= 1u << index;
        }
        float bx = backing.Width > 0 ? Compressed(backing, x) : x;
        foreach (var query in resources.BackgroundQueries) if (query.Contains(x, y) || bx != x && query.Contains(bx, y)) { mask |= 128; break; }
        return mask;
    }
    public string? AdvanceInput(EnbUiInputQueue queue)
    {
        var input = queue.TakeFrame();
        return AdvanceInput(input, queue.LeftDown, queue.RightDown);
    }
    public string? AdvanceInput(EnbUiInputEvent input, bool left, bool right)
    {
        int command = Pointer.Dispatch(new(input.Code, input.Modifiers, left, right), HitMask(input.X, input.Y));
        return command is >= 3 and <= 6 ? InputKey(command) : null;
    }
    public void CancelInput() => pointer?.Retire();
    bool IEnbUiGadgetHandler.HasFlash(int index) => index is >= 3 and <= 6;
    bool IEnbUiGadgetHandler.TryHandle(int index, ref EnbUiPointerFrame frame, uint flags, out EnbUiGadgetResponse response)
    {
        response = default;
        return index < 3; // 0058BFE0: event mask zero; bars provide tooltip hits only.
    }
}
