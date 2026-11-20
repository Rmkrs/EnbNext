using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    public bool HandleText(string? text)
    {
        if(!InformationEditing||text is null||_informationUi?.FocusedEditor is null)return false;
        _nativeCollection.QueueText(text,_uiX,_uiY);_informationFollowCaret=true;Publish();return true;
    }

    private EnbNext.Engine.EnbConfirmationPanel? _confirmationPanel;
    private EnbNext.Engine.EnbControlsPanel? _controlsPanel;
    public bool NativeControlsOpen => _page == 3;

    private void LoadControls(EnbRetailAssetBundle assets)
    {
        var resources = EnbRenderSession.Of(assets).Controls;
        _controlsPanel = new(resources, _panelResources!, _quitPalette, _confirmation);
        _controlsPanel.SoundRequested += sound => InteractionSoundRequested?.Invoke(sound);
        _controlsPanel.SaveControls = draft =>
        {
            if (_owner is uint owner && owner != 0) _assets!.SaveControlOptions(owner, draft);
            else throw new InvalidOperationException("A character is required to save controls.");
        };
        _confirmationPanel = new(assets, _quitPalette);
        _controlsPanel.BindDialog(_confirmationPanel);
    }

    private void OpenControls()
    {
        BeginControls();
        _page = 3;
    }
    /// <summary>The key bindings draft (the classic page and the Modern UI Options window).</summary>
    private void BeginControls()
    {
        var panel = _controlsPanel ?? throw new InvalidOperationException("Controls are unavailable.");
        panel.Modern = ModernUtility;
        panel.Begin(
            new(EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory, "control_keys.ini")), _assets.ResolveKeyBindings(_owner)));
    }
    private void ResetControlCapture() { _controlsPanel?.Capture.Reset(); _confirmation.Clear(); }
    private EnbUiRect ControlDesign(string key)
    {
        if (key.StartsWith("control/dialog/"))
        {
            int index = key switch { "control/dialog/outer" => 0, "control/dialog/info" => 1, "control/dialog/yes" => 2, _ => 3 };
            return _confirmationPanel?.Bounds[index] ?? default;
        }
        return _controlsPanel?.Design(key) ?? default;
    }
    internal void ActivateControl(string key)
    {
        if (_controlsPanel is null) return;
        ApplyControlResult(_controlsPanel.Activate(key));
    }
    private void ApplyControlResult(EnbNext.Engine.EnbControlsCommandResult result)
    {
        if (result.UpdateError) Error = result.Error;
        if (result.Navigation == EnbNext.Engine.EnbControlsNavigation.Close) Close();
        else if (result.Navigation == EnbNext.Engine.EnbControlsNavigation.Main) ReturnFromCategory();
    }

    public bool HandleControlKey(ushort code,ushort modifiers)
    {
        if(ModernControlKey(code,modifiers))return true;
        if(NativeInformationOpen)
        {
            if(InformationEditing&&_nativeCollection.PrepareTextKey(code,modifiers))return false;
            if(code!=0)QueuePanelKey(code,modifiers);return true;
        }
        if(!NativeControlsOpen)return false;
        if(code!=0)QueuePanelKey(code,modifiers);return true;
    }
    public void HandleControlKeyUp(ushort code,ushort modifiers)
    {if(ModernControlKey(code,(ushort)(modifiers|0x1000)))return;if((NativeControlsOpen||NativeInformationOpen)&&code!=0)QueuePanelKey(code,(ushort)(modifiers|0x1000));}

    private void PublishControls(ImmutableList<EnbUiModelDraw>.Builder list, long now)
    {
        if (_controlsPanel?.Draft is null) return;
        list.AddRange(_controlsPanel.DrawFrame(_size, _scaling, now, Error));
    }

    private void PublishControlDialog(ImmutableList<EnbUiModelDraw>.Builder list)
    {
        if (_confirmationPanel is null) return;
        list.AddRange(_confirmationPanel.DrawFrame(_confirmation,new(_size.Width,_size.Height),_scaling));
    }
}
