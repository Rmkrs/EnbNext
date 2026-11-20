using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;
using System.Globalization;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private EnbQuitUi? _quitUi;
    private readonly EnbOptionsGadgetCollection _nativeCollection=new();
    internal EnbUiInputQueue NativeInput=>_nativeCollection.Input;
    public bool NativeOptionsOpen => _page is 1 or 2 or 3 or 6 or 7 or 8 or 9;
    private EnbOptionsPanel? NativePanel => _page == 1 ? _mainPanel : _page == 2 ? _cameraPanel : null;
    private bool NativeUtilityOpen => _interactive && !SuppressUtilityHud && !ModernUtility && !QuitOpen;
    private bool NativeBrowserOpen => _page==4&&ManualOpen;
    private bool NativeInputOpen => NativeOptionsOpen || NativeInformationOpen || NativeUtilityOpen || NativeBrowserOpen;
    private EnbUiGadgetPointer? ActivePanelPointer => NativeInformationOpen ? NativeInformationPanel?.Pointer : NativeControlsOpen ? _controlsPanel?.Pointer : SettingsOpen ? _settingsPanel?.Pointer : GraphicsOpen ? _graphicsPanel?.Pointer : NativePanel?.Pointer;
    private bool HasNativeCapture=>_nativeCollection.HasCapture;
    private EnbPanelTooltip? _panelTooltip;

    private void CancelNativeInput()
    {
        _informationUi?.CancelInput(); _manualPanel?.CancelInput();
        NativeInput.Clear(); _utilityHud?.CancelInput(); _quitUi?.CancelInput(); _controlsPanel?.CancelInput(); _settingsPanel?.CancelInput(); _graphicsPanel?.CancelInput();
        _mainPanel?.RetireInput(); _cameraPanel?.RetireInput(); ClearHover();
    }

    private void SynchronizeNativePanel()
    {
        if(_nativeCollection.Synchronize(NativePanel,ActivePanelPointer,NativeUtilityOpen?_utilityHud?.Pointer:null,
            _page==10?_informationUi?.LogInput:null,PetitionOpen?_informationUi?.PetitionInput:null,NativeInputOpen,_uiX,_uiY,NativeBrowserOpen?_manualPanel:null))
            ReleaseUiHostCapture(!NativeInput.LeftDown&&!NativeInput.RightDown&&NativeInput.Count==0);
    }

    private void AdvanceNativeOptions(int width, int height, long now)
    {
        SynchronizeNativePanel();
        if (!_interactive || !NativeInputOpen) return;
        _nativeCollection.Advance(_page,_size,_scaling,width,height,now,_utilityHud,NativeInformationPanel,
            NativeControlsOpen?_controlsPanel:null,GraphicsOpen?_graphicsPanel:null,SettingsOpen?_settingsPanel:null,_cameraState,NativeOptionsOpen?UiLayout:null);
    }

    private void ApplyNativeCommand(EnbOptionsInputCommand command)
    {
        switch(command.Kind)
        {
            case EnbOptionsInputCommandKind.AnswerPrompt: AnswerInformationPrompt(command.Answer); break;
            case EnbOptionsInputCommandKind.CommitLogPage: CommitLogPage(); break;
            case EnbOptionsInputCommandKind.FollowCaret: _informationFollowCaret=true; break;
            case EnbOptionsInputCommandKind.Information: ActivateInformation(command.Key!); break;
            case EnbOptionsInputCommandKind.Controls: ApplyControlResult(command.Controls); break;
            case EnbOptionsInputCommandKind.Graphics: ApplyNativeGraphics(command.Key); break;
            case EnbOptionsInputCommandKind.Settings:
                Error=_settings.Error;
                if(command.Key is not null)ActivateSettings(command.Key);
                break;
            case EnbOptionsInputCommandKind.Sound: InteractionSoundRequested?.Invoke(command.Key!); break;
            case EnbOptionsInputCommandKind.Options: Activate(command.Key!); break;
        }
    }

    private float _uiX = float.NaN, _uiY = float.NaN;
    private bool _station;
    private readonly EnbNext.Client.EnbQuitMenuState _quitState;
    private readonly EnbFontEffectPalette _quitPalette = new();
    public bool QuitOpen => _quitState.IsOpen;
    public Func<uint, ulong, Net7LogoffDestination, Task<bool>>? LogoffRequested
    { get => _quitState.LogoffRequested; set => _quitState.LogoffRequested = value; }

    public bool OpenQuit()
    {
        if (!_interactive || _assets is null || (IsOpen && _page != 4) || _owner is not uint character ||
            character == 0 || session() != character) return false;
        try
        {
            if (_quitUi is null)
            {
                LoadQuitFontAttributes();
                _quitUi = new EnbQuitUi(_assets, _quitPalette, _quitState, NativeInput, LayoutEditor is null?null:BeginLayoutEditor);

            }
            _quitState.CloseRequested = all => { if (all) CloseAll(); else Close(); };
            _quitState.SoundRequested = sound => InteractionSoundRequested?.Invoke(sound);
            _quitState.Changed = Publish;
            _quitUi.ImmediateTooltips = _immediateTooltips;
            _quitUi.CancelInput(); _quitUi.Input.SetPosition(_uiX, _uiY);
            ClearHover();
            _quitState.Open(character, _handoff);
            _page = 5; Error = null; Opened?.Invoke(); Publish();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = ex.Message; Publish(); return false; }
    }

    internal void QueuePanelKey(ushort code, ushort modifiers)
    {
        if (QuitOpen || NativeOptionsOpen || NativeInformationOpen)
            NativeInput.Push(new(code, modifiers, _uiX, _uiY));
    }

    private void LoadQuitFontAttributes() => _quitPalette.Reload(_assets!.Paths.ClientIniDirectory);

    internal EnbRgb CycleQuitColor(string primaryId, string alternateId, long milliseconds)
        => _quitPalette.Cycle(primaryId, alternateId, milliseconds);

    internal Task ActivateQuitAsync(string key)
        => _quitState.ActivateAsync(key, _interactive);

    // Next Modern UI Quit menu: the same quit state, keys and logoff requests; the Modern UI window draws and clicks it.
    internal bool ModernQuitOpen => QuitOpen && ModernUtility;
    internal bool ModernQuitBusy => _quitState.IsSending;
    internal bool ModernQuitInStation => _station;
    internal bool ModernQuitCanEdit => LayoutEditor is not null;
    internal string? ModernQuitError => Error;
    internal string ModernQuitLabel(string attach, string fallback) => OptionLabel("fa_opt02a.ini", attach, fallback);
    /// <summary>The retail Quit menu tooltip of a button (tooltip.ini UI_OPTION_QUIT).</summary>
    internal string ModernQuitTip(string key)
        => _assets is { } assets && EnbRenderSession.Of(assets).TooltipResources.Strings.TryGetSection("UI_OPTION_QUIT", out var strings)
            ? strings.GetValueOrDefault(key, "") : "";
    internal void ModernQuitEdit() { if (ModernQuitOpen && LayoutEditor is not null) BeginLayoutEditor(); }
    /// <summary>Next: the Quit menu's Options button closes the menu and opens the Modern UI Options window.</summary>
    internal void ModernQuitOptions()
    {
        if (!ModernQuitOpen) return;
        Close();
        if (OpenModernOptions()) InteractionSoundRequested?.Invoke("SR_scifielectronic40.wav");
        Publish();
    }
    private (bool Open, bool Busy, string? Error) _modernQuitShown;
}
