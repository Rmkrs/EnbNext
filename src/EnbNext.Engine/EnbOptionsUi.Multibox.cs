using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    /// <summary>Shows the Next-only fifth utility button while local multiboxing is enabled.</summary>
    public bool MultiboxButton
    {
        get => _utilityHud?.MultiboxButton == true;
        set { if (_utilityHud is { } hud && hud.MultiboxButton != value) { hud.MultiboxButton = value; Publish(); } }
    }
    public void SetMultiboxButtonState(bool selected, bool hovered)
    {
        if (_utilityHud is not { } hud || hud.MultiboxSelected == selected && hud.MultiboxHovered == hovered) return;
        hud.MultiboxSelected = selected; hud.MultiboxHovered = hovered; Publish();
    }
    /// <summary>The button's screen rectangle before the Utility placement; empty while not drawn.</summary>
    /// <summary>Next Modern UI draws its own micro menu (with the multibox button) instead of the utility HUD.</summary>
    private bool _modernUtility;
    internal bool ModernUtility { get => _modernUtility; set { if (_modernUtility == value) return; _modernUtility = value; _utilityHud?.CancelInput(); Publish(); } }
    internal Func<EnbUiRect>? ModernMultiboxButton { get; set; }
    public EnbUiRect MultiboxButtonBounds => !_interactive || SuppressUtilityHud ? default
        : ModernUtility ? ModernMultiboxButton?.Invoke() ?? default : _utilityHud is { MultiboxButton: true } hud ? hud.MultiboxBounds : default;
}
