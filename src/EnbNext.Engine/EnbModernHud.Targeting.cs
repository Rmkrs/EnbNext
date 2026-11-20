using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbModernHud
{
    // The target buttons panel right of the action bar: previous and next target, then Follow.
    private const double TargetCell = 24, TargetCellGap = 2, TargetPanelPad = 4;
    /// <summary>The scanner's Follow verb kind (vb_follow); a set low word draws a verb disabled.</summary>
    private const uint FollowKind = 0x000D0000;
    private EnbAuthoredUiAsset? followAsset;
    private bool followLoaded;

    private EnbUiRect TargetButtonsRect
    {
        get
        {
            var last = SlotRect(11);
            double width = 3 * TargetCell + 2 * TargetCellGap + 2 * TargetPanelPad, height = TargetCell + 2 * TargetPanelPad;
            return new(last.Right + 8, last.Center.Y - height / 2, width, height);
        }
    }
    private EnbUiRect TargetButtonCell(int i) { var panel = TargetButtonsRect; return new(panel.X + TargetPanelPad + i * (TargetCell + TargetCellGap), panel.Y + TargetPanelPad, TargetCell, TargetCell); }
    /// <summary>The target buttons panel's extent before its placement (layout editing).</summary>
    public EnbUiRect TargetButtonsBounds => size.Width > 0 ? Up(TargetButtonsRect) : default;

    /// <summary>The Follow verb's index in the target's verbs, or -1 when the target offers none.</summary>
    private int FollowIndex()
    {
        for (int i = 0; i < verbs.Length; i++) if ((verbs[i].Value & 0xFFFF0000u) == FollowKind) return i;
        return -1;
    }

    /// <summary>vb_follow, drawn disabled while there is nothing to follow.</summary>
    private EnbAuthoredUiAsset? FollowAsset()
    {
        if (followLoaded) return followAsset;
        followLoaded = true;
        try { followAsset = EnbAuthoredUiAsset.Load(assets, "vb_follow.w3d"); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { followAsset = null; } // The panel shows only the arrows.
        return followAsset;
    }

    /// <summary>Edit mode's stand-in target: full hull and shields, and a skill activation, so its frame and cast bars can be placed.</summary>
    private static readonly EnbSceneTargetVitalsSnapshot PreviewVitals = new(1, 100, 100, 100);
    private static readonly EnbModernCast PreviewCast = new("Hacking", .35f, .65f, 1);
}
