using System.Globalization;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Net7.Social;
using EnbNext.Presentation;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private bool _showFrameTitle;
    private readonly FrameTitleAverage _frameTitleAverage=new();
    private CefBrowserSurface CreateBrowser(Uri address,string profile,string query)
    {
        // Why a page did not load goes to the session log (the address's query carries the session handoff, so only the host).
        long started=System.Diagnostics.Stopwatch.GetTimestamp();
        var browser=browserService.Create(address,profile,query);string host=address.Host;
        // Opening and closing are logged with their times, so a slow frame can be matched to a browser.
        AppendTranscript(FormattableString.Invariant($"Browser {host}: created in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms"));
        browser.Failed+=message=>AppendTranscript("Browser "+host+": "+message);
        browser.PageLoaded+=()=>AppendTranscript(FormattableString.Invariant($"Browser {host}: page loaded {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms after creation"));
        browser.Closed+=()=>AppendTranscript("Browser "+host+": closed");
        return browser;
    }
    private void ShowWorldWindowWithAudio(LiveWorldOpenAlAudio? entryAudio, EnbNext.Client.Models.CharacterSummary character)
    {
        if(_client is null){entryAudio?.Dispose();return;}
        if(worldScreen is not null){entryAudio?.Dispose();window.Raise();return;}
        if(_benchmark?.Mode=="record")_client.RecordedViewBootstrap=System.Text.Json.JsonSerializer.Serialize(new{CrtState=EnbNext.Rendering.EnbRetailCrtRandomSource.Current.State});
        cursor??=new(_client.RetailAssets!);
        var screen=new SdlWorldScreen(window,cursor,()=>_client,character,
            (uri,profile,query)=>new SdlOptionsBrowserSurface(()=>CreateBrowser(uri,profile,query)),
            TakeScreenshot,ToggleFullScreen,AppendDiagnostic);
        screen.Host.IssuePanelOpen=diagnostics.IssuesOpen||diagnostics.DeveloperOpen;
        screen.Host.IssueHitTest=point=>diagnostics.Hit(point)!=-1;
        screen.Host.PerformanceChanged=diagnostics.UpdatePerformance;screen.DiagnosticCaptureLost=diagnostics.Cancel;
        var owner=screen.Owner;var options=owner.Options;
        _client.ConfigureQualityOfLife(_preferences.QualityOfLife??new());
        ApplyNextOptions(owner,_preferences.QualityOfLife??new());
        options.MultiboxRequested=()=>multiboxOptions;options.SaveMultibox=v=>SaveMultiboxOptions(multiboxOptions with {Enabled=v.Enabled,EnableDamageMeter=v.EnableDamageMeter,EncounterPauseSeconds=v.EncounterPauseSeconds});
        options.QualityOfLifeRequested=()=>_preferences.QualityOfLife??new();
        options.BackgroundCapRequested=()=>(_preferences.BackgroundFpsCap,_preferences.BackgroundGameplayFps>0?_preferences.BackgroundGameplayFps:60);
        options.SaveBackgroundCap=(enabled,fps)=>{_preferences=_preferences with {BackgroundFpsCap=enabled,BackgroundGameplayFps=fps};SavePreferences();};
        // Live preview: only the Next map/arrow presentation follows the draft; OK saves, Cancel previews the original back.
        options.PreviewQualityOfLife=value=>{previewQualityOfLife=value==(_preferences.QualityOfLife??new())?null:value;ApplyNextOptions(owner,value);};
        options.SaveQualityOfLife=value=>
        {
            previewQualityOfLife=null;
            _preferences=_preferences with {QualityOfLife=value};
            SavePreferences();_client?.ConfigureQualityOfLife(value);ApplyNextOptions(owner,value);
            LiveWorldOpenAlAudio.SetFocusMuted(value.MuteBackgroundAudio&&!window.Focused);
        };
        options.SettingsScope=SettingsScopeOf;
        options.SettingsScopeRequested=SetSettingsScope;
        options.DiscordPresenceRequested=()=>_preferences.DiscordPresence??new();
        options.SaveDiscordPresence=value=>
        {
            _preferences=_preferences with {DiscordPresence=value};SavePreferences();
            UpdateDiscordPresence();
        };
        options.TexturePacksRequested=()=>_client?.RetailAssets?.GetTexturePackNames()??[""];
        owner.World.EnhancedGraphics=_preferences.EnhancedGraphics??EnbNext.Rendering.EnbEnhancedGraphicsOptions.Default;
        options.EnhancedGraphicsRequested=()=>owner.World.EnhancedGraphics;
        options.PreviewEnhancedGraphics=value=>owner.World.EnhancedGraphics=value;
        options.SaveEnhancedGraphics=value=>{_preferences=_preferences with {EnhancedGraphics=value};SavePreferences();};
        options.TextureOverridesRequested=()=>_client?.RetailAssets is {} assets?(assets.TextureOverridesEnabled?assets.SelectedTexturePack:""):_preferences.TexturePack;
        options.PreviewTextureOverrides=name=>_client?.RetailAssets?.SelectTexturePack(name);
        options.SaveTextureOverrides=name=>
        {
            bool changed=(_preferences.TexturePack??(_preferences.TextureOverrides?null:""))!=name;
            _preferences=_preferences with {TexturePack=name,TextureOverrides=name!=""};SavePreferences();
            if(changed && _client?.SelectedFixedSessionId is uint character)
                _client.ReportLocalChatMessage(character,19,"Texture pack applied: "+EnbGraphicsSettingsState.TextureLabel(name)+".");
        };
        options.UiScaleRequested=()=>ActiveDisplayPreferences.UiScale;
        options.SystemScaleRequested=()=>window.SystemScale;
        options.PreviewUiScaleRequested=PreviewUiScale;
        options.DisplayChoicesRequested=AvailableDisplayChoices;options.CurrentDisplayRequested=CurrentDisplayChoice;
        options.CurrentDisplayScopeRequested=CurrentDisplayScope;options.SelectDisplayScopeRequested=SelectDisplayScope;
        options.CharacterDisplayNameRequested=()=>_selectedCharacter?.FullName;
        options.BeginDisplayPreview=BeginDisplayPreview;options.PreviewDisplayRequested=choice=>ApplyDisplayChoice(choice,false);
        options.CommitDisplayPreview=CommitDisplayPreview;options.CancelDisplayPreview=CancelDisplayPreview;
        owner.Inventory.BrowserFactory=CreateBrowser;
        owner.ChatRequested=SubmitWorldChatAsync;owner.Issues=_runtimeIssues;
        if(entryAudio is not null)owner.AdoptClientAudio(entryAudio);
        worldScreen=screen;ReleaseFrontend();screen.Start();window.Title(WindowTitle);
    }
    private void CloseWorldWindow()
    {
        ClearDiscordPresence();SavePresentationDiagnostics();_frameTitleAverage.Reset();window.Title(DeveloperRenderDiagnostics.TitlePrefix);
        diagnostics.Cancel();diagnostics.UpdatePerformance(false,null);
        var old=worldScreen;worldScreen=null;
        if(old is not null){surface.DetachWorld();old.Dispose();}
        DeactivateCharacterDisplay();
    }
    // Next options that live on world-owned UI.
    private void ApplyNextOptions(EnbNext.Engine.EnbWorldController owner,EnbQualityOfLifeOptions value)
    {
        owner.Inventory.LootAllEnabled=value.LootAll;
        owner.Ui.FixedTooltip.Enabled=value.FixedTooltips;
        owner.Ui.Gutter.AlwaysShowLabels=value.AlwaysShowLabels;owner.Ui.Gutter.LabelKinds=value.LabelKinds;
        // Modern UI only: the gutter rail and the icons of labelled categories can be turned off.
        owner.Ui.Gutter.RailHidden=value.ModernUi&&value.HideGutterRail;EnbNext.Engine.EnbModernHud.EdgeRailHidden=owner.Ui.Gutter.RailHidden;owner.Ui.Gutter.HiddenIconKinds=value.ModernUi?value.HiddenIconKinds:0;owner.Ui.Gutter.SteadyAttackerLabels=value.ModernUi;
        owner.NavigationMap.ShadeOutsideSector=value.MapSectorShading;
        LoadWaypoints();
        owner.NavigationMap.Waypoints=waypoints;
        owner.NavigationMap.WaypointArrowEnabled=value.WaypointArrow;
        owner.NavigationMap.MarkersEnabled=value.MapMarkers;
        owner.Hud.MapNoteEditor=owner.NavigationMap.NoteEditor;
        ApplyMissionTrackerOptions(owner,value);
    }
    /// <summary>The window title in game names the character ("EnbNext: <character>"), so clients are told apart.</summary>
    private string WindowTitle=>worldScreen is not null&&_selectedCharacter is {} self?DeveloperRenderDiagnostics.TitlePrefix+": "+self.FullName:DeveloperRenderDiagnostics.TitlePrefix;
    private Task<bool> SubmitWorldChatAsync(Net7ChatInputRequest request)
    {
        if(TryWaypointCommand(request.Text))return Task.FromResult(true);
        if(TryReconnectCommand(request.Text))return Task.FromResult(true);
        if(!string.Equals(request.Text.Trim(),"/fps",StringComparison.OrdinalIgnoreCase))return _client?.SubmitChatAsync(request)??Task.FromResult(false);
        _showFrameTitle=!_showFrameTitle;_frameTitleAverage.Reset();
        if(_showFrameTitle)FrameIntervalClock.FrameStarted+=UpdateFrameTitle;
        else{FrameIntervalClock.FrameStarted-=UpdateFrameTitle;window.Title(WindowTitle);}
        return Task.FromResult(true);
    }
    private void UpdateFrameTitle(long timestamp)
    {
        if(_frameTitleAverage.Add(timestamp,out double ms))window.Title(string.Create(CultureInfo.InvariantCulture,$"{WindowTitle} — {1000/ms:F1} FPS | {ms:F2} ms"));
    }
}
