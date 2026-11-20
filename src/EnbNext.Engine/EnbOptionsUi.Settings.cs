using EnbNext.Presentation;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbOptionsUi
{
    private EnbNext.Engine.EnbSettingsPanel _settingsPanel = null!;
    private EnbNext.Engine.EnbSettingsState _settings = null!;
    public Func<EnbMultiboxOptions>? MultiboxRequested {get;set;}
    public Action<EnbMultiboxOptions>? SaveMultibox {get;set;}
    public Func<EnbDiscordPresenceOptions>? DiscordPresenceRequested { get; set; }
    public Action<EnbDiscordPresenceOptions>? SaveDiscordPresence { get; set; }
    public Func<bool>? SplitChatRequested { get; set; }
    public Func<EnbQualityOfLifeOptions>? QualityOfLifeRequested { get; set; }
    public Action<EnbQualityOfLifeOptions>? PreviewQualityOfLife { get; set; }
    /// <summary>Navigation Options is open: its waypoint arrow preview draws above the options window.</summary>
    public bool NavigationOptionsOpen => (_page == 7 || _modernCategory == "navigation") && _settings?.Section == EnbSettingsState.NavigationSection;
    public Func<(bool Enabled,int Fps)>? BackgroundCapRequested { get; set; }
    public Action<bool,int>? SaveBackgroundCap { get; set; }
    public Action<EnbQualityOfLifeOptions>? SaveQualityOfLife { get; set; }
    // Probe seams use the same transaction boundaries without touching installed files.
    public Action<float>? SaveInterfaceSettings { get; set; }
    public Action<EnbRetailSoundOptions>? SaveSoundSettings { get; set; }
    public Action<EnbFontProfile, IReadOnlyList<EnbFontDefinition>>? SaveFontSettings { get; set; }
    private bool SettingsOpen => _page is 7 or 8 or 9;

    private void OpenSettings(int page, int section = EnbSettingsState.RetailSection)
    {
        try
        {
            if (_assets is null || _owner is not > 0) throw new InvalidOperationException("A character is required for settings.");
            var layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory,
                page == 7 ? "fa_opt04a-interface.ini" : page == 8 ? "fa_opt04a-sound.ini" : "fc_option_font.ini"));
            LoadQuitFontAttributes(); // Shared authored palette and FA_CYCLE evaluator.
            CancelNativeInput(); NativeInput.SetPosition(_uiX,_uiY);
            _settings.Begin(_owner.Value, page, (int)(_size.Width * _scaling), (int)(_size.Height * _scaling), SplitChatRequested?.Invoke() != false, section);
            _settings.Multibox=MultiboxRequested?.Invoke()??new();_settings.SaveMultibox=SaveMultibox;
            _settings.QualityOfLife=QualityOfLifeRequested?.Invoke()??new();
            _settings.SaveQualityOfLife=SaveQualityOfLife;_settings.PreviewQualityOfLife=PreviewQualityOfLife;_settings.BeginQualityPreview();
            _settings.DiscordPresence=DiscordPresenceRequested?.Invoke()??new();
            _settings.SaveDiscordPresence=SaveDiscordPresence;
            _settingsPanel.Open(layout); _page = page; Error = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Error = "Could not open settings: " + ex.Message; }
    }

    private bool SettingsVisible(string key) => SettingsOpen && _settingsPanel.Visible(key);
    private void ActivateSettings(string key)
    {
        if (!SettingsVisible(key)) return;
        if (_page != 9 && key is "settings/EXIT" or "settings/OK" or "settings/CANCEL") InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
        if (_page == 8 && key == "settings/CHECK00") InteractionSoundRequested?.Invoke("fontclick.wav");
        if (key == "settings/EXIT") { Close(); return; }
        if (key == "settings/CANCEL") { EndSettings(); if (_page == 9) _page = 0; else ReturnFromCategory(); Error = null; return; }
        if (key == "settings/OK")
        {
            bool fonts = _page == 9;
            if (_settings.TrySave(SaveInterfaceSettings, SaveSoundSettings, SaveFontSettings))
            { EndSettings(); if (fonts) _page = 0; else ReturnFromCategory(); }
            Error = _settings.Error;
            return;
        }
        if (key == "settings/ROUTE" && _page == 7) { _settings.ToggleRoutePlanner(); }
        else if (key == "settings/CHECK00") { _settings.ToggleCheck(); Error = _settings.Error; }
        else if (key == "settings/RESOLUTION") { _settings.DefaultFont(); Error = _settings.Error; }
    }
    private void EndSettings()
    {
        _settingsPanel?.CancelInput();
        _settings?.Retire();
    }
    private void PublishSettings(ImmutableList<EnbUiModelDraw>.Builder list)
    {
        if (_settingsPanel is null) return;
        // A page that cannot be drawn is contained here: the grid comes back with the reason, instead of every
        // later frame of the Options window failing (17 Nov 2026: an unsized row index locked the window).
        try { list.AddRange(_settingsPanel.DrawFrame(_size,_scaling,Environment.TickCount64,Error)); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { EndSettings(); _page = 1; Error = "This options page could not be shown: " + ex.Message; }
    }
}
