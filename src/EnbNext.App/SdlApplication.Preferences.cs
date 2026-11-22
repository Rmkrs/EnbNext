using System.Text.Json;
using EnbNext.Client.Models;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;
using SDL;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private enum DisplayState {Normal,Maximized,Minimized,FullScreen}
    private DisplayState CurrentWindowState=>window.Fullscreen?DisplayState.FullScreen:window.Minimized?DisplayState.Minimized:window.Maximized?DisplayState.Maximized:DisplayState.Normal;
    private EnbStartupPreferences _preferences = new();
    private EnbStartupPreferences? _characterPreferences;
    // Retained even while System is selected, so re-enabling Character does
    // not turn a temporary inheritance choice into a destructive reset.
    private EnbStartupPreferences? _storedCharacterPreferences;
    private EnbStartupPreferences? _characterPreviewDraft;
    private string? _characterDisplayPath;
    private EnbDisplayScope _displayScope;
    private sealed record DisplayPreviewSnapshot(EnbStartupPreferences System, EnbStartupPreferences? Character,
        EnbStartupPreferences? StoredCharacter,
        EnbDisplayScope Scope, EnbStartupPreferences Visible, DisplayState State);
    private DisplayPreviewSnapshot? _displayPreview;
    private EnbStartupPreferences ActiveDisplayPreferences =>
        _displayScope == EnbDisplayScope.Character && _characterPreferences is not null
            ? _characterPreferences : _preferences;
    private void SetActiveDisplayPreferences(EnbStartupPreferences value)
    {
        if(_displayScope == EnbDisplayScope.Character && _characterDisplayPath is not null)
            _characterPreferences=value;
        else _preferences=value;
    }
    private EnbDisplayScope CurrentDisplayScope() => _displayScope;
    private bool _restoreNormalPlacementAfterPreview;
    private bool _preferencesOpened;
    private SDL_DisplayID? _graphicsDisplay;
    private bool _dpiResizePending;
    private bool _applyingDisplayChoice;
    private long? placementDue;
    private string? _savedPreferences;
    private readonly string _preferencesPath = Environment.GetEnvironmentVariable("ENBNEXT_STARTUP_PREFERENCES")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EnbNext", "startup.json");


    private void ReadPreferences()
    {
        try{if(File.Exists(_preferencesPath))_preferences=JsonSerializer.Deserialize<EnbStartupPreferences>(File.ReadAllText(_preferencesPath))??new();}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){AppendDiagnostic("Startup preferences could not be read: "+ex.Message);}
        _savedPreferences=JsonSerializer.Serialize(_preferences);
    }
    private void InitializePreferences()
    {
        EnbNext.Assets.EnbNextControls.Directory=Path.GetDirectoryName(Path.GetFullPath(_preferencesPath));
        var initial=ActiveDisplayPreferences;
        window.UiScaleOverride=initial.UiScale;
        if(hidden){window.RefreshSize();_preferencesOpened=true;return;}
        if(initial.X is {} x&&initial.Y is {} y)
        {
            var screen=SdlDisplays.All().FirstOrDefault(s=>s.WorkingArea.Contains(x,y))??SdlDisplays.Primary();
            var area=screen.WorkingArea;window.Position(Math.Clamp(x,area.X,Math.Max(area.X,area.Right-100)),Math.Clamp(y,area.Y,Math.Max(area.Y,area.Bottom-80)));
        }
        window.SetPixelSize(initial.DisplayWidth,initial.DisplayHeight);
        if(initial.FullScreen)window.SetFullscreen(true);
        else if(initial.Maximized)window.Maximize();
        window.RefreshSize();_preferencesOpened=true;
    }
    private void HandleDisplayEvent(SDL_EventType type)
    {
        // A moved or resized window holds the loop for a moment: no lag is judged meanwhile.
        if(type is SDL_EventType.SDL_EVENT_WINDOW_MOVED or SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED)
            lagMonitor.Hold(ForegroundSettle);
        if(type is SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED)
        {window.RefreshSize();cursor?.SetScale(window.Scale);frontendScreen?.Resize();worldScreen?.Resize();}
        // Focus mode places windows temporarily; their saved placement stays as it was.
        if(focusActive)return;
        if(worldScreen?.Owner.Options is {GraphicsPreviewActive:true} options &&
            (type is SDL_EventType.SDL_EVENT_WINDOW_MOVED or SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED) &&
            (type is SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED || _graphicsDisplay!=SdlDisplays.Current(window).Id))
            options.RefreshGraphicsDisplayChoice();
        if(!_preferencesOpened||_applyingDisplayChoice)return;
        if(type==SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED&&CurrentWindowState==DisplayState.Normal)_dpiResizePending=true;
        if(type==SDL_EventType.SDL_EVENT_WINDOW_MAXIMIZED){placementDue=null;SetActiveDisplayPreferences(ActiveDisplayPreferences with{Maximized=true});}
        if(type==SDL_EventType.SDL_EVENT_WINDOW_RESTORED&&CurrentWindowState==DisplayState.Normal)SetActiveDisplayPreferences(ActiveDisplayPreferences with{Maximized=false});
        if(type is SDL_EventType.SDL_EVENT_WINDOW_MOVED or SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED or SDL_EventType.SDL_EVENT_WINDOW_RESTORED)ScheduleNormalPlacementCapture();
    }
    private void ScheduleNormalPlacementCapture()
    {
        if(_preferencesOpened&&!_applyingDisplayChoice&&CurrentWindowState==DisplayState.Normal)placementDue=Environment.TickCount64+200;
    }
    private void TickPreferences()
    {
        if(placementDue is not {} due||Environment.TickCount64<due)return;placementDue=null;
        if(focusActive)return; // Focus mode owns the window's place and size until it ends.
        if(CurrentWindowState!=DisplayState.Normal)return;
        if(_dpiResizePending||_restoreNormalPlacementAfterPreview)
        {
            bool restore=_restoreNormalPlacementAfterPreview;_dpiResizePending=false;_restoreNormalPlacementAfterPreview=false;
            _applyingDisplayChoice=true;
            try
            {
                var active=ActiveDisplayPreferences;window.SetPixelSize(active.DisplayWidth,active.DisplayHeight);
                if(restore&&active.X is {} x&&active.Y is {} y)window.Position(x,y);
            }
            finally{_applyingDisplayChoice=false;}
            ScheduleNormalPlacementCapture();
        }
        else CaptureNormalPlacement();
    }
    private void CaptureNormalPlacement()
    {
        if(hidden||!_preferencesOpened||CurrentWindowState!=DisplayState.Normal||_restoreNormalPlacementAfterPreview||_dpiResizePending)return;
        // Focus mode places the window only for a while: quitting, logging out or changing the display scope
        // during it keeps the place saved before it (the shutdown capture used to save the Focus mode place).
        if(focusActive)return;
        window.RefreshSize();var position=window.Position();
        SetActiveDisplayPreferences(ActiveDisplayPreferences.WithNormalPlacement(position.X,position.Y,window.Size.Width,window.Size.Height,window.PixelWidth,window.PixelHeight));
    }
    private EnbDisplayChoice DesktopDisplayChoice(){var screen=SdlDisplays.Current(window);return new(screen.PixelWidth,screen.PixelHeight,EnbDisplayMode.BorderlessFullscreen);}
    private EnbDisplayChoice CurrentDisplayChoice(){var desktop=DesktopDisplayChoice();return ActiveDisplayPreferences.SelectedDisplay(desktop.Width,desktop.Height);}
    private IReadOnlyList<EnbDisplayChoice> AvailableDisplayChoices()
    {
        _graphicsDisplay=SdlDisplays.Current(window).Id;
        return SdlDisplays.Choices(window,CurrentDisplayChoice()).Where(c=>ActiveDisplayPreferences.UiScale is not {} scale||c.Width/scale>=640&&c.Height/scale>=480).ToArray();
    }
    private void PreviewUiScale(double? scale)
    {
        if(scale is {} value && (!double.IsFinite(value)||value<1||value>2||window.PixelWidth/value<640||window.PixelHeight/value<480))
            throw new InvalidOperationException("UI scale does not fit this window.");
        SetActiveDisplayPreferences(ActiveDisplayPreferences with {UiScale=scale});
        window.UiScaleOverride=scale;
        HandleDisplayEvent(SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_SCALE_CHANGED);
    }
    private void BeginDisplayPreview()
    {
        if(_displayPreview is not null)return;placementDue=null;CaptureNormalPlacement();_characterPreviewDraft=_characterPreferences;
        _displayPreview=new(_preferences,_characterPreferences,_storedCharacterPreferences,_displayScope,ActiveDisplayPreferences,CurrentWindowState);
    }
    private void CommitDisplayPreview()
    {
        double? scale=ActiveDisplayPreferences.UiScale;
        bool scaleChanged=_displayPreview is {} original && original.Visible.UiScale!=scale;
        _displayPreview=null;if(_characterPreferences is not null)_storedCharacterPreferences=_characterPreferences;_characterPreviewDraft=null;SavePreferences();
        if(scaleChanged && _client?.SelectedFixedSessionId is uint character)
            _client.ReportLocalChatMessage(character,19,"UI scale applied: "+
                (scale is {} value?$"{value*100:0}%":$"System ({window.SystemScale*100:0}%)")+".");
    }
    private void CancelDisplayPreview()
    {
        if(_displayPreview is not {} original)return;placementDue=null;
        _preferences=original.System;_characterPreferences=original.Character;_storedCharacterPreferences=original.StoredCharacter;_characterPreviewDraft=null;_displayScope=original.Scope;
        try{RestoreDisplay(original.Visible,original.State);_restoreNormalPlacementAfterPreview|=original.State is DisplayState.Maximized or DisplayState.FullScreen;}
        finally{_displayPreview=null;}
        ScheduleNormalPlacementCapture();
    }
    private void ApplyDisplayChoice(EnbDisplayChoice choice,bool persist=true)
    {
        var desktop=DesktopDisplayChoice();if(!ActiveDisplayPreferences.WouldChangeDisplay(choice,desktop.Width,desktop.Height))return;
        placementDue=null;_applyingDisplayChoice=true;
        try
        {
            if(choice.Mode==EnbDisplayMode.BorderlessFullscreen)
            {
                var screen=SdlDisplays.Current(window);
                if(CurrentWindowState==DisplayState.Normal)CaptureNormalPlacement();
                SetActiveDisplayPreferences(ActiveDisplayPreferences.WithDisplayChoice(choice));
                unsafe
                {
                    int width,height;NativePins.Check(SDL3.SDL_GetWindowSize(window.Window,&width,&height),"window size");
                    window.Position(screen.Bounds.X+(screen.Bounds.Width-width)/2,screen.Bounds.Y+(screen.Bounds.Height-height)/2);
                }
                window.SetFullscreen(true);
            }
            else
            {
                var screen=SdlDisplays.Current(window);_restoreNormalPlacementAfterPreview=false;
                SetActiveDisplayPreferences(ActiveDisplayPreferences.WithDisplayChoice(choice));window.SetFullscreen(false);window.Restore();window.SetPixelSize(choice.Width,choice.Height);
                var area=screen.WorkingArea;window.Position(area.X+(area.Width-(int)Math.Round(choice.Width/screen.Density))/2,area.Y+(area.Height-(int)Math.Round(choice.Height/screen.Density))/2);
                // The delayed DPI correction must retain the newly chosen monitor,
                // not the normal placement saved before the window was dragged.
                var position=window.Position();
                SetActiveDisplayPreferences(ActiveDisplayPreferences with{X=position.X,Y=position.Y});
                _restoreNormalPlacementAfterPreview=true;
            }
        }
        finally{_applyingDisplayChoice=false;}
        ScheduleNormalPlacementCapture();if(persist)SavePreferences();
    }
    private void ToggleFullScreen()
    {
        if(!window.Fullscreen){ApplyDisplayChoice(DesktopDisplayChoice());return;}
        SetActiveDisplayPreferences(ActiveDisplayPreferences with{FullScreen=false});window.SetFullscreen(false);
        if(ActiveDisplayPreferences.Maximized)window.Maximize();else window.Restore();
        ScheduleNormalPlacementCapture();SavePreferences();
    }
    private void RestoreDisplay(EnbStartupPreferences preferences,DisplayState state)
    {
        window.UiScaleOverride=preferences.UiScale;
        window.RefreshSize();cursor?.SetScale(window.Scale);frontendScreen?.Resize();worldScreen?.Resize();
        if(hidden)return;
        _restoreNormalPlacementAfterPreview=false;_applyingDisplayChoice=true;
        try
        {
            window.SetFullscreen(false);window.Restore();
            var screen=preferences.X is {} x&&preferences.Y is {} y?SdlDisplays.All().FirstOrDefault(s=>s.WorkingArea.Contains(x,y)):null;
            screen??=SdlDisplays.Current(window);
            if(preferences.X is {} px&&preferences.Y is {} py)
            {
                var area=screen.WorkingArea;int w=Math.Min(area.Width,Math.Max(1,(int)Math.Round(preferences.DisplayWidth/screen.Density))),h=Math.Min(area.Height,Math.Max(1,(int)Math.Round(preferences.DisplayHeight/screen.Density)+40));
                window.Position(Math.Clamp(px,area.X,Math.Max(area.X,area.Right-w)),Math.Clamp(py,area.Y,Math.Max(area.Y,area.Bottom-h)));
            }
            window.SetPixelSize(preferences.DisplayWidth,preferences.DisplayHeight);
            if(state==DisplayState.FullScreen)window.SetFullscreen(true);else if(state==DisplayState.Maximized)window.Maximize();
            _restoreNormalPlacementAfterPreview=state==DisplayState.Normal;
        }
        finally{_applyingDisplayChoice=false;}
        ScheduleNormalPlacementCapture();
    }
    private void SavePreferences()
    {
        if(hidden || !_preferencesOpened || _displayPreview is not null)return;
        SaveLoginSelection();
        _preferences=_preferences with { Server=_frontend?.Owner.SelectedServer ?? _preferences.Server };
        if(!focusActive)SetActiveDisplayPreferences(ActiveDisplayPreferences with { Maximized=CurrentWindowState==DisplayState.Maximized ||
            ((CurrentWindowState==DisplayState.Minimized || CurrentWindowState==DisplayState.FullScreen) && ActiveDisplayPreferences.Maximized) });
        try
        {
            var json=JsonSerializer.Serialize(_preferences);
            if(json!=_savedPreferences)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!);
                string temporary=_preferencesPath+"."+Guid.NewGuid().ToString("N")+".tmp";
                try{File.WriteAllText(temporary,json);File.Move(temporary,_preferencesPath,true);}
                finally{if(File.Exists(temporary))File.Delete(temporary);}
                _savedPreferences=json;
            }
            SaveCharacterDisplayPreferences();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { AppendDiagnostic("Startup preferences could not be saved: "+ex.Message); }
    }

    private void ActivateCharacterDisplay(CharacterSummary character)
    {
        var path=activeGroup is {} g && activeGroupMember is {} m ? SavedGroupStore.DisplayPath(_preferencesPath,g.Id,m.Id) : EnbCharacterDisplayPreferences.PathFor(_preferencesPath,character.AccountId,character.AvatarId);
        if(_characterDisplayPath==path)return;
        if(_characterDisplayPath is not null)DeactivateCharacterDisplay();
        placementDue=null;
        CaptureNormalPlacement();
        SavePreferences();
        _characterDisplayPath=activeGroup is {} group && activeGroupMember is {} member
            ? SavedGroupStore.DisplayPath(_preferencesPath,group.Id,member.Id)
            : EnbCharacterDisplayPreferences.PathFor(_preferencesPath,character.AccountId,character.AvatarId);
        ReadCharacterDisplay();
        if(_characterPreferences is not null)RestoreDisplay(_characterPreferences,
            _characterPreferences.FullScreen ? DisplayState.FullScreen : _characterPreferences.Maximized ? DisplayState.Maximized : DisplayState.Normal);
    }

    private void ReadCharacterDisplay()
    {
        _characterPreferences=null;
        _storedCharacterPreferences=null;
        try
        {
            if(File.Exists(_characterDisplayPath))
            {
                var profile=JsonSerializer.Deserialize<EnbCharacterDisplayPreferences>(File.ReadAllText(_characterDisplayPath));
                if(profile is not null)
                {
                    _storedCharacterPreferences=profile.ApplyTo(_preferences);
                    if(profile.Enabled)_characterPreferences=_storedCharacterPreferences;
                }
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        { AppendDiagnostic("Character display preferences could not be read: "+ex.Message); }
        if(activeGroup is not null)_characterPreferences??=_preferences;
        _displayScope=_characterPreferences is null ? EnbDisplayScope.System : EnbDisplayScope.Character;
    }

    private void DeactivateCharacterDisplay()
    {
        if(_characterDisplayPath is null)return;
        if(_frontendClosed)
        {
            // Closed invokes client disposal after the native window is gone.
            // Closing already saved the profile; Screens is invalid now.
            _characterPreferences=null;
            _storedCharacterPreferences=null;
            _characterPreviewDraft=null;
            _characterDisplayPath=null;
            _displayScope=EnbDisplayScope.System;
            return;
        }
        placementDue=null;
        CaptureNormalPlacement();
        SavePreferences();
        bool restoreDisplay = _characterPreferences is not null;
        _characterPreferences=null;
        _storedCharacterPreferences=null;
        _characterPreviewDraft=null;
        _characterDisplayPath=null;
        _displayScope=EnbDisplayScope.System;
        if (restoreDisplay) RestoreDisplay(_preferences,_preferences.FullScreen ? DisplayState.FullScreen :
            _preferences.Maximized ? DisplayState.Maximized : DisplayState.Normal);
    }

    private void SelectDisplayScope(EnbDisplayScope scope)
    {
        if(activeGroup is not null || _characterDisplayPath is null || _displayScope==scope)return;
        placementDue=null;
        CaptureNormalPlacement();
        if(scope==EnbDisplayScope.Character)
            _characterPreferences = _characterPreviewDraft ?? _storedCharacterPreferences ?? _preferences;
        else
        {
            _characterPreviewDraft=_characterPreferences;
            _storedCharacterPreferences=_characterPreferences;
            _characterPreferences=null;
        }
        _displayScope=scope;
        var active=ActiveDisplayPreferences;
        RestoreDisplay(active,active.FullScreen ? DisplayState.FullScreen :
            active.Maximized ? DisplayState.Maximized : DisplayState.Normal);
    }

    private void SaveCharacterDisplayPreferences()
    {
        if(_characterDisplayPath is null ||
            (_characterPreferences is null && _storedCharacterPreferences is null &&
                !File.Exists(_characterDisplayPath)))return;
        try
        {
            var profile=EnbCharacterDisplayPreferences.From(
                _characterPreferences??_storedCharacterPreferences??_preferences,
                _characterPreferences is not null);
            Directory.CreateDirectory(Path.GetDirectoryName(_characterDisplayPath)!);
            File.WriteAllText(_characterDisplayPath+".tmp",JsonSerializer.Serialize(profile));
            File.Move(_characterDisplayPath+".tmp",_characterDisplayPath,true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        { AppendDiagnostic("Character display preferences could not be saved: "+ex.Message); }
    }
}
