using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed class EnbQuitUi(EnbRetailAssetBundle assets, EnbFontEffectPalette palette,
    EnbQuitMenuState state, EnbUiInputQueue input, Action? edit=null) : IDisposable
{
    public EnbQuitPanel Panel { get; } = new(assets, palette,edit is not null);
    public EnbUiInputQueue Input => input;
    private EnbPanelTooltip? tooltip;
    private EnbPanelTooltip Tooltip => tooltip ??= new(assets, Panel.ResolveFont);
    public bool ImmediateTooltips { get => Tooltip.Immediate; set => Tooltip.Immediate = value; }

    public EnbUiTooltipDraw? DrawTooltip(EnbUiSize size, double scaling, long milliseconds)
    {
        if (!state.IsOpen) { tooltip?.Reset(); return null; }
        return Tooltip.Draw(Panel.Pointer, Panel.CommandKey(Panel.Pointer.TooltipGadget), "UI_OPTION_QUIT",
            Input, size, scaling, milliseconds, state.Character,Panel.CommandKey(Panel.Pointer.TooltipGadget)=="quit/EDIT"?"Move and resize interface panels. Create or choose a layout profile.":null);
    }

    public Task? Advance(int physicalWidth, int physicalHeight, long milliseconds, bool interactive)
    {
        if (!state.IsOpen || !interactive) return null;
        var input = Input.TakeFrame();
        string? command = Panel.AdvanceInput(input, Input.LeftDown, Input.RightDown, Input.X, Input.Y,
            physicalWidth, physicalHeight, milliseconds);
        command ??= EnbQuitMenuState.InputAction(input.Code, input.Modifiers);
        if(command=="quit/EDIT"){edit?.Invoke();return Task.CompletedTask;}
        return command is null ? null : state.ActivateAsync(command, interactive);
    }

    public void CancelInput() { Input.Clear(); Panel.RetireInput(); tooltip?.Reset(); }
    public void Dispose() { CancelInput(); Panel.Dispose(); tooltip?.Dispose(); }
}
