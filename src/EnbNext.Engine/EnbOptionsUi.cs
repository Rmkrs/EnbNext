using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>TSI14A utility HUD and OPT01A/OPT03A input. All visible art is submitted to the GPU.</summary>
internal sealed partial class EnbOptionsUi : IDisposable
{
    private readonly Func<uint?> session;
    private readonly Func<bool?> toggleMap;
    private readonly Func<Uri,string,string,IEnbOptionsBrowser> createBrowser;
    public event Action? FocusRequested;
    public event Action<bool>? VisibilityChanged;
    public event Action<bool,bool>? CaptureRequested;
    private readonly EnbNext.Client.EnbConfirmationState _confirmation = new();
    public EnbOptionsUi(Func<uint?> session, Func<bool?> toggleMap, Func<Uri,string,string,IEnbOptionsBrowser> createBrowser)
    {
        this.session = session; this.toggleMap = toggleMap; this.createBrowser=createBrowser;
        _quitState = new(session);
        _nativeCollection.Command+=ApplyNativeCommand;
        _nativeCollection.Changed+=ClearHover;
    }

    private EnbNext.Engine.EnbUtilityHudPanel? _utilityHud;
    private EnbOptionsPanelResources? _panelResources;
    private EnbOptionsPanel? _mainPanel, _cameraPanel;
    private bool _immediateTooltips, _tooltipVisible;
    private readonly EnbNext.Client.EnbCameraOptionsState _cameraState = new();
    private EnbRetailAssetBundle? _assets;
    private EnbSliderPanelResources? _sliderPanels;
    private EnbHorizontalSliderResources? _horizontalSlider;
    private EnbUiSize _size;
    private double _scaling = 1;
    private uint? _owner;
    private EnbSceneExperienceSnapshot _experience = new(0, 0, 0);
    private int _page;
    private bool _interactive;
    public bool IsOpen => _page != 0;
    public bool SuspendsGameplayMovement => QuitOpen || ManualOpen || InformationOpen || _controlsPanel?.Capture.Action is not null || _confirmation.Text is not null;
    public Action<EnbRetailCameraOptions>? SaveCinematicOptions { get; set; }
    public Func<EnbEnhancedGraphicsOptions>? EnhancedGraphicsRequested {get;set;}
    public Action<EnbEnhancedGraphicsOptions>? PreviewEnhancedGraphics {get;set;}
    public Action<EnbEnhancedGraphicsOptions>? SaveEnhancedGraphics {get;set;}
    public Func<string?[]>? TexturePacksRequested {get;set;}
    public Func<string?>? TextureOverridesRequested {get;set;}
    public Action<string?>? PreviewTextureOverrides {get;set;}
    public Action<string?>? SaveTextureOverrides {get;set;}
    /// <summary>The graphics cards to present on, "Automatic" (the card Windows picks) first; empty where the card cannot be chosen.</summary>
    public Func<(string? Key,string Label)[]>? GraphicsAdaptersRequested {get;set;}
    /// <summary>The card in use (null: Automatic).</summary>
    public Func<string?>? GraphicsAdapterRequested {get;set;}
    /// <summary>Moves presentation to a card between frames, as a preview: OK keeps it, Cancel moves back.</summary>
    public Action<string?>? PreviewGraphicsAdapter {get;set;}
    public Action<string?>? SaveGraphicsAdapter {get;set;}
    /// <summary>True where choosing this card takes effect only when EnbNext starts again (Linux).</summary>
    public Func<string?,bool>? GraphicsAdapterAtRestart {get;set;}
    public Action<EnbRetailGraphicsOptions>? SaveGraphicsOptions { get; set; }
    public Action<EnbRetailGraphicsOptions?>? PreviewGraphicsOptions { get; set; }
    public Func<double?>? UiScaleRequested {get;set;}
    public Func<double>? SystemScaleRequested {get;set;}
    public Action<double?>? PreviewUiScaleRequested {get;set;}
    public Func<IReadOnlyList<EnbDisplayChoice>>? DisplayChoicesRequested { get; set; }
    public Func<EnbDisplayChoice>? CurrentDisplayRequested { get; set; }
    public Func<EnbDisplayScope>? CurrentDisplayScopeRequested { get; set; }
    public Action<EnbDisplayScope>? SelectDisplayScopeRequested { get; set; }
    public Func<string?>? CharacterDisplayNameRequested { get; set; }
    public Action? BeginDisplayPreview { get; set; }
    public Action<EnbDisplayChoice>? PreviewDisplayRequested { get; set; }
    public Action? CommitDisplayPreview { get; set; }
    public Action? CancelDisplayPreview { get; set; }
    public event Action<ImmutableList<EnbUiModelDraw>>? Changed;
    public event Action<EnbUiTooltipDraw?>? TooltipChanged;
    public event Action? Opened;
    public event Action? InventoryRequested;
    public event Action? CharacterRequested;
    public event Action<string>? InteractionSoundRequested;
    public string? Error { get; private set; }
    public ImmutableList<EnbUiModelDraw> Presentation { get; private set; } = [];
    public bool SuppressUtilityHud {get;set;}

    public void Update(IEnbSceneFrame scene, EnbRetailAssetBundle? assets, EnbUiSize size, double scaling, bool interactive)
    {
        if (_owner != session() || !ReferenceEquals(_assets, assets))
        {
            CloseAll(); _owner = session();
            if (!ReferenceEquals(_assets, assets))
            {
                DisposeAssets(); _assets = assets;
                if (assets is not null)
                {
                    try { Load(assets); Error = null; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                    { Error = ex.Message; _utilityHud = null; }
                }
            }
        }
        _informationScene = scene;
        if (InformationOpen && _handoff != (scene.LocationTransition?.Version ?? 0)) CloseAll();
        _handoff = scene.LocationTransition?.Version ?? 0;
        if (ManualOpen && _manualHandoff != _handoff) CloseAll();
        if (QuitOpen && _quitState.Handoff != _handoff) CloseAll();
        _station = scene.ActiveStarbaseId is not null;
        _size = size; _scaling = scaling; _experience = scene.Hud.Experience;
        _interactive = interactive && _utilityHud is not null;
        VisibilityChanged?.Invoke(_interactive);
        if (!_interactive) CloseAll();
        if (_manualBrowser is { } pendingBrowser && _browserAdmissionUpdates > 0 && --_browserAdmissionUpdates == 0) pendingBrowser.Attach();
        AdvanceInformationPortraits(Environment.TickCount64*.001);
        int width = Math.Max(1, (int)Math.Round(_size.Width * _scaling));
        int height = Math.Max(1, (int)Math.Round(_size.Height * _scaling));
        if (QuitOpen) _ = _quitUi!.Advance(width, height, Environment.TickCount64, _interactive);
        else AdvanceNativeOptions(width, height, Environment.TickCount64);
        var nativePointer = QuitOpen ? _quitUi?.Panel.Pointer : NativeInformationOpen ? NativeInformationPanel?.Pointer : NativeControlsOpen ? _controlsPanel?.Pointer : SettingsOpen ? _settingsPanel?.Pointer : GraphicsOpen ? _graphicsPanel?.Pointer : NativePanel?.Pointer;
        if (nativePointer is not null || NativeUtilityOpen || NativeBrowserOpen)
        {
            bool capture = nativePointer is { LeftCapture: >= 0 } or { RightCapture: >= 0 } || HasNativeCapture;
            if (capture) CaptureRequested?.Invoke(true,false);
            else ReleaseUiHostCapture(!NativeInput.LeftDown && !NativeInput.RightDown && NativeInput.Count == 0);
        }
        Publish();
    }

    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private void Load(EnbRetailAssetBundle assets)
    {
        _utilityHud = new(EnbRenderSession.Of(assets).UtilityHud){UiLayout=UiLayout};
        LoadQuitFontAttributes();
        _panelResources = EnbRenderSession.Of(assets).OptionsPanels;
        _mainPanel = new(_panelResources, _panelResources.Main, _quitPalette);
        _cameraPanel = new(_panelResources, _panelResources.Camera, _quitPalette);
        _panelTooltip = new(assets, _panelResources.Font) { Immediate = _immediateTooltips };
        _settings = new(assets);
        _settings.SoundRequested += sound => InteractionSoundRequested?.Invoke(sound);
        _sliderPanels = EnbRenderSession.Of(assets).SliderPanels;
        _horizontalSlider = EnbRenderSession.Of(assets).HorizontalSlider;
        _settingsPanel = new(_sliderPanels, _horizontalSlider, _panelResources, _quitPalette, _settings);
        _settingsPanel.SoundRequested += sound => InteractionSoundRequested?.Invoke(sound);
        _graphicsState = new(_sliderPanels);
        _graphicsPanel = new(_sliderPanels,_horizontalSlider,_panelResources,_quitPalette,_graphicsState,
            EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_opt04a-graphics.ini")));
        _graphicsPanel.SoundRequested += sound => InteractionSoundRequested?.Invoke(sound);
        LoadControls(assets);
    }

    private void ReleaseUiHostCapture(bool forget=true)=>CaptureRequested?.Invoke(false,forget);
    internal void CaptureLost()=>CancelNativeInput();
    internal void TrackPanelWheel(double delta,ushort modifiers,bool externalCapture)
    {
        if((QuitOpen || (NativeOptionsOpen || NativeInformationOpen) && !externalCapture) && delta!=0)
            NativeInput.Push(new(delta>0?(ushort)0x101:(ushort)0x102,modifiers,_uiX,_uiY));
    }
    internal bool TrackPanelPointer(EnbUiPoint point,ushort code,ushort modifiers,bool motion,bool externalCapture,bool hostCaptured)
    {
        int width=Math.Max(1,(int)Math.Round(_size.Width*_scaling)),height=Math.Max(1,(int)Math.Round(_size.Height*_scaling));
        _uiX=width>1?Math.Clamp((float)(point.X*_scaling/(width-1)),0,1):0;
        _uiY=height>1?Math.Clamp((float)(point.Y*_scaling/(height-1)),0,1):0;
        // The Modern UI Quit menu is clicked in its Modern UI window; its keys still reach the quit panel.
        if(ModernQuitOpen)return false;
        if(!QuitOpen&&!NativeInputOpen)return false;
        var nativePointer=QuitOpen?_quitUi?.Panel.Pointer:ActivePanelPointer;
        if(!externalCapture||QuitOpen)NativeInput.SetPosition(_uiX,_uiY);
        bool consumes=QuitOpen||!externalCapture&&(NativeContains(point)||nativePointer is {LeftCapture:>=0} or {RightCapture:>=0}||HasNativeCapture||hostCaptured);
        bool release=(modifiers&0x1000)!=0;
        if(consumes&&!motion&&!release&&code is 1 or 2)CaptureRequested?.Invoke(true,false);
        bool ownedRelease=release&&(code==1?NativeInput.LeftDown:code==2&&NativeInput.RightDown);
        if(code!=0&&(consumes||ownedRelease))NativeInput.Push(new(code,modifiers,_uiX,_uiY));
        return consumes;
    }

    internal void Activate(string key)
    {
        if (!_interactive || _owner != session()) return;
        if (QuitOpen) { _ = ActivateQuitAsync(key); return; }
        if (!ManualOpen && !InformationOpen)
        {
            if (key == "hud/SHIP_BUT") { InventoryRequested?.Invoke(); return; }
            if (key == "hud/CHARACTER_BUT") { CharacterRequested?.Invoke(); return; }
            if (key == "hud/MAP_BUT") { if(toggleMap() is bool open)InteractionSoundRequested?.Invoke(open?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav"); Publish(); return; }
            if (key == "hud/OPTIONS_BUT")
            {
                if (ModernUtility && !IsOpen)
                {
                    // Next Modern UI: the Options window; the button closes it again (as Cancel).
                    if (ModernOptionsOpen) ModernOptionsCancel(); else OpenModernOptions();
                    InteractionSoundRequested?.Invoke(ModernOptionsOpen?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav");
                    ClearHover(); Publish(); return;
                }
                if (IsOpen) Close(); else { _page = 1; _returnToMain = true; Error = null; Opened?.Invoke(); }
                InteractionSoundRequested?.Invoke(IsOpen?"SR_scifielectronic40.wav":"SR_scifielectronic42.wav");
                ClearHover(); Publish(); return;
            }
        }
        if (ManualOpen) { if (key == "manual/EXIT_BUTTON") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); Close(); } return; }
        if (InformationOpen) { ActivateInformation(key); ClearHover(); Publish(); return; }
        if (SettingsOpen && key.StartsWith("settings/")) { ActivateSettings(key); ClearHover(); Publish(); return; }
        if (_page == 3) { ActivateControl(key); ClearHover(); Publish(); return; }
        if (_page == 1 && key == "main/BTTN00")
        {
            var o = _assets!.ResolvePlayerOptions(_owner).Options;
            _cameraState.Begin(o); _page = 2;
        }
        else if (_page == 1 && key == "main/BTTN01") OpenControls();
        else if (_page == 1 && key == "main/BTTN03") OpenGraphics();
        else if (_page == 1 && key == "main/BTTN02") OpenSettings(7);
        else if (_page == 1 && key == "main/BTTN05") OpenInformationBrowser(5, "Credits");
        else if (_page == 1 && key == "main/BTTN04") OpenSettings(8);
        else if (_page == 1 && key == "main/BTTN06") OpenSettings(9);
        else if (_page == 1 && key == "main/NEXT00") OpenGraphics(advanced: true);
        else if (_page == 1 && key == "main/NEXT01") OpenSettings(7, EnbSettingsState.SocialSection);
        else if (_page == 1 && key == "main/NEXT02") OpenSettings(7, EnbSettingsState.NavigationSection);
        else if (_page == 1 && key == "main/NEXT03") OpenSettings(7, EnbSettingsState.QualitySection);
        else if (_page == 1 && key == "main/NEXT04") OpenSettings(7, EnbSettingsState.ModernSection);
        else if (_page == 1 && key == "main/NEXT05") OpenSettings(7, EnbSettingsState.LabelsSection);
        else if (_page == 6) ActivateGraphics(key);
        else if (_page == 2 && key == "camera/OK")
        {
            if (_cameraState.TrySave(SaveCinematicOptions, out var error)) ReturnFromCategory();
            Error = error;
        }
        else if (_page == 2 && key == "camera/CANCEL") { ReturnFromCategory(); Error = null; }
        else if (key is "main/EXIT" or "camera/EXIT" or "graphics/EXIT") Close();
        ClearHover(); Publish();
    }

    // The utility button's Game Options returns to its button grid; a category opened from the
    // chat menu bar closes Options on Ok/Cancel, as retail does.
    private bool _returnToMain = true;
    public void OpenCategory(string key)
    {
        Close(); Activate("hud/OPTIONS_BUT"); Activate(key);
        if (_page > 1) _returnToMain = false;
    }
    private void ReturnFromCategory() { if (_returnToMain) _page = 1; else CloseAll(); }
    public bool Close()
    {
        if (ModernOptionsOpen) { ModernOptionsCancel(); return true; }
        if (InformationOpen) { LeavePetition(false); return true; }
        if (_graphicsState?.Dropdown == true) { _graphicsState.UiScaleDropdown=false;_graphicsState.TextureDropdown=false;_graphicsState.AdapterDropdown=false;_graphicsState.ResolutionDropdown = false; Publish(); return true; }
        if (QuitOpen && ManualOpen)
        {
            _page = 4; _quitState.Retire();
            CancelPointer(); ClearHover(); Publish(); return true;
        }
        if (ManualOpen) { FinishInformationBrowser(); return true; }
        return CloseAll();
    }
    private bool CloseAll()=>CloseAllCore(false);
    private bool CloseAllCore(bool preserveHelp)
    {
        // The Modern UI Options window closes as Cancel (a session or character change, another Options page).
        if (ModernOptionsOpen) { RetireModernCategory(); _modernCategory = ""; ModernOptionsError = null; ModernOptionsChanged?.Invoke(); }
        EndInformation(preserveHelp);
        EndSettings();
        RollbackGraphicsPreview();
        bool wasOpen = IsOpen; _page = 0; if (_graphicsState is not null) { _graphicsState.ResolutionDropdown = false;_graphicsState.UiScaleDropdown=false;_graphicsState.TextureDropdown=false;_graphicsState.AdapterDropdown=false; } _quitState.Retire(); CloseManualBrowser(); _controlsPanel?.Retire(); ResetControlCapture(); CancelPointer(); ClearHover(); Publish(); return wasOpen;
    }
    public void Suspend() { _interactive = false; VisibilityChanged?.Invoke(false); CloseAll(); }
    public void CancelPointer() { ReleaseUiHostCapture(); CancelNativeInput(); }
    public bool BlocksFlight(EnbUiPoint point) => _interactive && (SuspendsGameplayMovement || NativeContains(point));
    private bool NativeContains(EnbUiPoint point)
    {
        int width = Math.Max(1, (int)Math.Round(_size.Width * _scaling));
        int height = Math.Max(1, (int)Math.Round(_size.Height * _scaling));
        if (NativeUtilityOpen && _utilityHud is { } utility && utility.HitMask(
            width > 1 ? (float)(point.X * _scaling / (width - 1)) : 0,
            height > 1 ? (float)(point.Y * _scaling / (height - 1)) : 0) != 0) return true;
        if(NativeOptionsOpen)point=UiLayout?.Inverse(EnbUiGroup.Options,point)??point;
        if (NativeBrowserOpen && _manualPanel is {} browser)return browser.HitMask(width>1?point.X*_scaling/(width-1):0,height>1?point.Y*_scaling/(height-1):0)!=0;
        if (_nativeCollection.Petition is {} petition)return petition.Contains(width > 1 ? (float)(point.X*_scaling/(width-1)):0,height > 1 ? (float)(point.Y*_scaling/(height-1)):0);
        if (NativeInformationPanel is {} information)
            return information.Contains(width > 1 ? (float)(point.X * _scaling / (width - 1)) : 0,
                height > 1 ? (float)(point.Y * _scaling / (height - 1)) : 0);
        if (NativeControlsOpen && _controlsPanel is { } controls)
            return controls.Contains(width > 1 ? (float)(point.X * _scaling / (width - 1)) : 0,
                height > 1 ? (float)(point.Y * _scaling / (height - 1)) : 0);
        if (SettingsOpen && _settingsPanel is { } settings)
            return settings.Contains(width > 1 ? (float)(point.X * _scaling / (width - 1)) : 0,
                height > 1 ? (float)(point.Y * _scaling / (height - 1)) : 0);
        if (GraphicsOpen && _graphicsPanel is { } graphics)
            return graphics.Contains(width > 1 ? (float)(point.X * _scaling / (width - 1)) : 0,
                height > 1 ? (float)(point.Y * _scaling / (height - 1)) : 0,_graphicsState.Dropdown,_graphicsState.ChoiceCount);
        return NativePanel is { } panel && panel.PointerHits(width > 1 ? point.X * _scaling / (width - 1) : 0,
            height > 1 ? point.Y * _scaling / (height - 1) : 0) != 0;
    }
    private EnbUiRect SettingsScreen(EnbUiRect r) => Screen(r.Translate(_settingsPanel.Offset.X, _settingsPanel.Offset.Y));
    private EnbUiRect KeyScreen(string key) => InformationVisible(key) ? InformationScreen(key) : key.StartsWith("settings/") ? SettingsScreen(HitDesign(key)) : Screen(HitDesign(key));
    private EnbUiRect Screen(EnbUiRect r) => new(r.X * _size.Width / 640, r.Y * _size.Height / 480, r.Width * _size.Width / 640, r.Height * _size.Height / 480);
    private EnbUiRect HitDesign(string key)
    {
        EnbUiRect r = key.StartsWith("hud/") ? _utilityHud!.Design(key) : key.StartsWith("quit/") ? _quitUi!.Panel.Design(key)
            : key.StartsWith("main/") ? _mainPanel!.Design(key)
            : key.StartsWith("camera/") ? _cameraPanel!.Design(key)
            : EnbNext.Engine.EnbInformationPanel.IsKey(key) ? InformationDesign(key) : key.StartsWith("graphics/") ? _graphicsPanel.Design(key) : key.StartsWith("control/") ? ControlDesign(key) : key.StartsWith("settings/") ? _settingsPanel.Design(key) : _manualPanel!.Design(key);
        return (key.StartsWith("graphics/TRACK", StringComparison.Ordinal) || key.StartsWith("settings/TRACK", StringComparison.Ordinal))
            ? new(r.X, r.Center.Y - 8, r.Width, 16)
            : r;
    }
    private void Publish()
    {
        SynchronizeNativePanel();
        var list = ImmutableList.CreateBuilder<EnbUiModelDraw>();
        if (_interactive && _size.Width > 0 && _size.Height > 0)
        {
            // Frame first, then gadgets, then icon overlays; keep modal art above the utility HUD.
            foreach (var prefix in QuitOpen && ManualOpen ? new[] { "hud/", "manual/", "quit/" }
                : _page is 1 or 2 or 4 or 5 or 6 ? new[] { "hud/", _page == 1 ? "main/" : _page == 2 ? "camera/" : _page == 5 ? "quit/" : _page == 6 ? "graphics/" : "manual/" } : new[] { "hud/" })
            {
            if(prefix=="hud/")
            {
                if(!SuppressUtilityHud && !ModernUtility)
                {
                    _utilityHud!.DrawFrame(list,_size,_scaling,_experience);
                }
                continue;
            }
            if(prefix=="quit/")
            {
                if(!ModernUtility)list.AddRange(_quitUi!.Panel.DrawFrame(_size,_scaling,_station,Error));
                continue;
            }
            if (prefix is "main/" or "camera/")
            {
                list.AddRange((prefix == "main/" ? _mainPanel! : _cameraPanel!).DrawFrame(_size, _scaling,
                    _cameraState, Error));
                continue;
            }
            if(prefix=="graphics/"){PublishGraphics(list);continue;}
            // Next Modern UI: the page in a Modern UI window.
            _manualPanel!.ModernFrames=ModernUtility&&_assets is {} modernAssets?EnbRenderSession.Of(modernAssets).Confirmations:null;
            _manualPanel.DrawFrame(list,_size,_scaling,_browserTitle,_browserFailure??Error);
            }
            if (InformationOpen) PublishInformation(list);
            if (SettingsOpen) PublishSettings(list);
            if (_page == 3) PublishControls(list, Environment.TickCount64);
        }
        if(NativeOptionsOpen)for(int i=0;i<list.Count;i++)if(list[i].LayoutGroup==EnbUiGroup.None)list[i]=list[i] with {LayoutGroup=EnbUiGroup.Options};
        PublishLayoutEditor(list);
        Presentation = list.ToImmutable(); Changed?.Invoke(Presentation);
        if ((ModernQuitOpen, _quitState.IsSending, Error) != _modernQuitShown && (ModernQuitOpen || _modernQuitShown.Open))
        { _modernQuitShown = (ModernQuitOpen, _quitState.IsSending, Error); ModernOptionsChanged?.Invoke(); }
        PositionManualBrowser();
        ShowTooltip();
    }

    private EnbRetailFontFace? Font(string id)
        => _panelResources?.Font(id, _size, _scaling);
    private void ClearHover() { _panelTooltip?.Reset(); PublishTooltip(null); }
    private void PublishTooltip(EnbUiTooltipDraw? tooltip)
    {
        // An inactive panel must not erase another gadget owner's presentation.
        if (tooltip is not null || _tooltipVisible) TooltipChanged?.Invoke(tooltip);
        _tooltipVisible = tooltip is not null;
    }
    public void SetImmediateTooltips(bool value) { _immediateTooltips = value; if (_quitUi is not null) _quitUi.ImmediateTooltips = value; if (_panelTooltip is not null) _panelTooltip.Immediate = value; ShowTooltip(); }
    private void ShowTooltip()
    {
        if(LayoutEditor?.Layout.Editing==true){ShowLayoutTooltip();return;}
        _layoutTooltip?.Reset();
        if (ModernQuitOpen) { PublishTooltip(null); return; }
        if (QuitOpen && _quitUi is not null)
        {
            PublishTooltip(_quitUi.DrawTooltip(_size, _scaling, Environment.TickCount64));
            return;
        }
        if (NativeInputOpen && _panelTooltip is not null)
        {
            PublishTooltip(_panelTooltip.DrawOptions(_nativeCollection,_page,NativeInformationPanel,_controlsPanel,
                _experience,_size,_scaling,_owner,CharacterDisplayNameRequested));
            return;
        }
        PublishTooltip(null);
    }
    private void DisposeAssets()
    {
        _manualPanel?.Dispose(); _manualPanel=null; _panelResources?.ClearFonts();
        _mainPanel?.Dispose(); _cameraPanel?.Dispose(); _mainPanel = _cameraPanel = null; _nativeCollection.ForgetDisposedPanel(); _panelResources = null;
        _panelTooltip?.Dispose(); _panelTooltip = null; NativeInput.Clear();
        _graphicsPanel?.Dispose(); _graphicsPanel = null!; _sliderPanels = null; _horizontalSlider = null;
        _settings?.Retire(); _settings = null!;
        _settingsPanel?.Dispose(); _settingsPanel = null!;
        _utilityHud = null;
        _layoutDeletePanel?.Dispose();_layoutDeletePanel=null;_layoutDeleteState.Clear();_layoutTooltip?.Dispose();_layoutTooltip=null;_layoutArt?.Dispose();_layoutArt=null;
        _quitUi?.Dispose(); _quitUi = null;
        _informationUi?.Dispose(); _informationUi = null; _informationBlinkPeriod = 0;
        _controlsPanel?.Dispose(); _controlsPanel = null; _confirmationPanel?.Dispose(); _confirmationPanel = null;
    }
    public void Dispose() { Suspend(); DisposeAssets(); _assets = null; }
}
