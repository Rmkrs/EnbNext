using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>005CF310/005CD0C0 settings draft, preview and save/rollback lifetime.</summary>
internal sealed class EnbSettingsState(EnbRetailAssetBundle assets, Func<EnbFontDefinition, int?> fontHeight)
{
    public EnbSettingsState(EnbRetailAssetBundle assets) : this(assets, EnbRenderSession.Of(assets).SettingsFontMetrics.Height) { }
    private uint? owner;
    public int Page { get; private set; }
    // Next categories share the Interface panel art; 0 is the retail Interface page.
    public const int RetailSection=0,SocialSection=1,NavigationSection=2,QualitySection=3,ModernSection=4,LabelsSection=5;
    public int Section { get; private set; }
    public EnbNext.Presentation.EnbMultiboxOptions Multibox {get;set;}=new();
    public Action<EnbNext.Presentation.EnbMultiboxOptions>? SaveMultibox {get;set;}
    public int Rows => Page == 7 ? (Section == RetailSection ? 1 : 0) : Page == 8 ? 3 : 5;
    public EnbSliderInput[] Sliders { get; } = CreateSliders(assets);
    private static EnbSliderInput[] CreateSliders(EnbRetailAssetBundle assets)
    {
        var resources = EnbRenderSession.Of(assets).SliderPanels;
        var result = new EnbSliderInput[5];
        for (int i = 0; i < result.Length; i++)
        {
            var t = resources.Tracks[i]; double span = resources.ArrowSpan;
            var track = new EnbNext.Presentation.EnbUiRect(320+t.X,240-t.Y-span/2,t.Width,span);
            result[i] = new(track,new(track.X-span,track.Y,track.Width+2*span,span),horizontal:true,singlePrecision:true);
        }
        return result;
    }
    public EnbFontProfile? Profile;
    public EnbFontDefinition[] Fonts { get; private set; } = [];
    public EnbNext.Presentation.EnbQualityOfLifeOptions QualityOfLife { get; set; } = new();
    public EnbNext.Presentation.EnbDiscordPresenceOptions DiscordPresence { get; set; } = new();
    public Action<EnbNext.Presentation.EnbDiscordPresenceOptions>? SaveDiscordPresence { get; set; }
    // The background frame cap lives on Advanced Graphics.
    public Action<EnbNext.Presentation.EnbQualityOfLifeOptions>? SaveQualityOfLife { get; set; }
    /// <summary>Next options apply live while their page is open; OK keeps them, Cancel restores the opening value.</summary>
    public Action<EnbNext.Presentation.EnbQualityOfLifeOptions>? PreviewQualityOfLife { get; set; }
    private EnbNext.Presentation.EnbQualityOfLifeOptions? previewOrigin, previewed;
    public void BeginQualityPreview() { previewOrigin = previewed = QualityOfLife; }
    public void PreviewQualityChanges()
    {
        if (previewOrigin is null || QualityOfLife == previewed) return;
        previewed = QualityOfLife; PreviewQualityOfLife?.Invoke(QualityOfLife);
    }
    private void EndQualityPreview(bool saved)
    {
        if (!saved && previewOrigin is { } origin && previewed != origin) PreviewQualityOfLife?.Invoke(origin);
        previewOrigin = previewed = null;
    }
    public void SetArrowSize(int percent) => QualityOfLife = QualityOfLife with { WaypointArrowSize = percent };
    public void SetChatTextSize(int size) => QualityOfLife = QualityOfLife with { ModernChatTextSize = size };
    public void SetStationLookSpeed(int percent) => QualityOfLife = QualityOfLife with { StationLookSpeed = percent };
    // Local multiboxing is enabled here; its settings live in the in-game multibox menu.
    /// <summary>Modern UI: a labelled category's icon on or off (EnbQualityOfLifeOptions.HiddenIconKinds).</summary>
    public void ToggleLabelIcon(int kind) { if (kind >= 0 && kind < EnbObjectLabels.Count) QualityOfLife = QualityOfLife with { HiddenIconKinds = QualityOfLife.HiddenIconKinds ^ (1 << kind) }; }
    public void ToggleGutterRail() => QualityOfLife = QualityOfLife with { HideGutterRail = !QualityOfLife.HideGutterRail };
    /// <summary>Modern UI chat: message times, the kept history and the chat logs.</summary>
    public void ToggleChatTimes() => QualityOfLife = QualityOfLife with { ChatTimes = !QualityOfLife.ChatTimes };
    public void ToggleChatHistory() => QualityOfLife = QualityOfLife with { ChatKeepHistory = !QualityOfLife.ChatKeepHistory };
    public void ToggleChatLogs() => QualityOfLife = QualityOfLife with { ChatSaveLogs = !QualityOfLife.ChatSaveLogs };
    public bool ConvenienceCheck(int row) => row switch {12=>Multibox.Enabled,13=>QualityOfLife.LootAll,2=>QualityOfLife.MapSectorShading,14=>QualityOfLife.MapMarkers,15=>QualityOfLife.WaypointArrow,17=>QualityOfLife.WaypointArrowDistance,18=>QualityOfLife.WaypointArrowHeight,19=>QualityOfLife.MissionTracker,20=>QualityOfLife.MissionTrackerStations,21=>QualityOfLife.MissionTrackerBackground,22=>QualityOfLife.MissionTrackerAutoTrack,23=>QualityOfLife.ModernUi,24=>QualityOfLife.FixedTooltips,26=>QualityOfLife.AlwaysShowLabels,36=>QualityOfLife.BarPercentages,37=>QualityOfLife.ExperiencePercentages,38=>QualityOfLife.ExperienceValues,39=>QualityOfLife.RightClickCancelsTarget,40=>QualityOfLife.HideCorpses,41=>QualityOfLife.KeepLocalOnEscape,42=>QualityOfLife.StationFreeMovement,43=>QualityOfLife.ConnectionIndicator,44=>QualityOfLife.AttackerNames,45=>QualityOfLife.RouteWormholes,>=27 and <27+EnbObjectLabels.Count=>EnbObjectLabels.Shows(QualityOfLife.LabelKinds,row-27),3=>QualityOfLife.MuteBackgroundAudio,4=>QualityOfLife.AutoAcceptGroupInvites,5=>QualityOfLife.AutoAcceptTrade,6=>QualityOfLife.AutoFormUp,7=>DiscordPresence.Enabled,8=>DiscordPresence.IncludeCharacterName,9=>Multibox.EnableDamageMeter,_=>false};
    public void ToggleConvenience(int row)
    {
        if(row==13){QualityOfLife=QualityOfLife with {LootAll=!QualityOfLife.LootAll};return;}
        if(row==2){QualityOfLife=QualityOfLife with {MapSectorShading=!QualityOfLife.MapSectorShading};return;}
        if(row==14){QualityOfLife=QualityOfLife with {MapMarkers=!QualityOfLife.MapMarkers};return;}
        if(row==15){QualityOfLife=QualityOfLife with {WaypointArrow=!QualityOfLife.WaypointArrow};return;}
        if(row==17){QualityOfLife=QualityOfLife with {WaypointArrowDistance=!QualityOfLife.WaypointArrowDistance};return;}
        if(row==18){QualityOfLife=QualityOfLife with {WaypointArrowHeight=!QualityOfLife.WaypointArrowHeight};return;}
        if(row==19){QualityOfLife=QualityOfLife with {MissionTracker=!QualityOfLife.MissionTracker};return;}
        if(row==20){QualityOfLife=QualityOfLife with {MissionTrackerStations=!QualityOfLife.MissionTrackerStations};return;}
        if(row==21){QualityOfLife=QualityOfLife with {MissionTrackerBackground=!QualityOfLife.MissionTrackerBackground};return;}
        if(row==22){QualityOfLife=QualityOfLife with {MissionTrackerAutoTrack=!QualityOfLife.MissionTrackerAutoTrack};return;}
        if(row==23){QualityOfLife=QualityOfLife with {ModernUi=!QualityOfLife.ModernUi};return;}
        if(row==24){QualityOfLife=QualityOfLife with {FixedTooltips=!QualityOfLife.FixedTooltips};return;}
        if(row==26){QualityOfLife=QualityOfLife with {AlwaysShowLabels=!QualityOfLife.AlwaysShowLabels};return;}
        if(row==36){QualityOfLife=QualityOfLife with {BarPercentages=!QualityOfLife.BarPercentages};return;}
        if(row==37){QualityOfLife=QualityOfLife with {ExperiencePercentages=!QualityOfLife.ExperiencePercentages};return;}
        if(row==38){QualityOfLife=QualityOfLife with {ExperienceValues=!QualityOfLife.ExperienceValues};return;}
        if(row==39){QualityOfLife=QualityOfLife with {RightClickCancelsTarget=!QualityOfLife.RightClickCancelsTarget};return;}
        if(row==40){QualityOfLife=QualityOfLife with {HideCorpses=!QualityOfLife.HideCorpses};return;}
        if(row==41){QualityOfLife=QualityOfLife with {KeepLocalOnEscape=!QualityOfLife.KeepLocalOnEscape};return;}
        if(row==42){QualityOfLife=QualityOfLife with {StationFreeMovement=!QualityOfLife.StationFreeMovement};return;}
        if(row==43){QualityOfLife=QualityOfLife with {ConnectionIndicator=!QualityOfLife.ConnectionIndicator};return;}
        if(row==44){QualityOfLife=QualityOfLife with {AttackerNames=!QualityOfLife.AttackerNames};return;}
        if(row==45){QualityOfLife=QualityOfLife with {RouteWormholes=!QualityOfLife.RouteWormholes};return;}
        if(row>=27&&row<27+EnbObjectLabels.Count){QualityOfLife=QualityOfLife with {LabelKinds=QualityOfLife.LabelKinds^(1<<(row-27))};return;}
        if(row>=9){Multibox=row switch {12=>Multibox with {Enabled=!Multibox.Enabled},9=>Multibox with {EnableDamageMeter=!Multibox.EnableDamageMeter},10=>Multibox with {EncounterPauseSeconds=Math.Max(1,Multibox.EncounterPauseSeconds-1)},11=>Multibox with {EncounterPauseSeconds=Math.Min(60,Multibox.EncounterPauseSeconds+1)},_=>Multibox};return;}
        if(row==7){DiscordPresence=DiscordPresence with {Enabled=!DiscordPresence.Enabled};return;}
        if(row==8){if(DiscordPresence.Enabled)DiscordPresence=DiscordPresence with {IncludeCharacterName=!DiscordPresence.IncludeCharacterName};return;}
        QualityOfLife=row switch {3=>QualityOfLife with {MuteBackgroundAudio=!QualityOfLife.MuteBackgroundAudio},4=>QualityOfLife with {AutoAcceptGroupInvites=!QualityOfLife.AutoAcceptGroupInvites},5=>QualityOfLife with {AutoAcceptTrade=!QualityOfLife.AutoAcceptTrade},_=>QualityOfLife with {AutoFormUp=!QualityOfLife.AutoFormUp}};
    }
    public bool RoutePlanner { get; private set; }
    public void ToggleRoutePlanner() => RoutePlanner = !RoutePlanner;
    public bool Check { get; private set; }
    public int FontIndex { get; private set; }
    public string? Error { get; private set; }
    public event Action<string>? SoundRequested;

    public void Begin(uint character, int page, int width, int height, bool split, int section = RetailSection)
    {
        Retire(); owner = character; Page = page; Section = page == 7 ? section : RetailSection;
        var options = assets.ResolvePlayerOptions(owner).Options;
        RoutePlanner = assets.CharacterInterface.RoutePlanner;
        Check = page == 7 ? assets.CharacterInterface.AutoPilot : options.DopplerEnabled;
        if (Page == 9)
        {
            if (assets.Fonts is not { AvailableFonts.Count: > 0 } fonts ||
                !fonts.TrySelectProfile(width, height, out Profile))
                throw new InvalidDataException("Retail chat font profile unavailable.");
            string[] ids = !split ? ["FONT_CHAT_LINE_00"] : ["FONT_CHAT_LINE_00", "FONT_CHAT_LINE_01"];
            Fonts = ids.Select(id => assets.ResolveChatFont(owner, Profile,
                Profile.Fonts.First(f => f.FontId == id))).ToArray();
            Check = Fonts.All(f => f.Resource == Fonts[0].Resource && f.Scale == Fonts[0].Scale &&
                f.Spacing == Fonts[0].Spacing && f.LineSpacing == Fonts[0].LineSpacing);
            FontIndex = 0;
        }
        if (Page == 9) RefreshFont(configure:true);
        else
        {
            foreach (var slider in Sliders) slider.Configure(100,1);
            Sliders[0].SetValue(Page == 7 ? options.InterfaceTooltipDelay : options.SoundMusic);
            Sliders[1].SetValue(options.SoundSfx); Sliders[2].SetValue(options.SoundDialog);
        }
        if (Page == 8) assets.SetSoundPreview(owner.Value, Sound());
    }

    public int Range(int row) => Page != 9 ? 100 : row switch
    { 0 => Fonts.Length, 1 => assets.Fonts!.AvailableFonts.Count, 2 => 16, 3 => 8, _ => 18 };

    private void RefreshFont(int row = -1, bool configure = false)
    {
        var f = Fonts[FontIndex];
        for (int i = 0; i < Sliders.Length; i++)
        {
            if (row >= 0 && i != row) continue;
            if (configure) Sliders[i].Configure(Range(i),1);
            float value = i switch
            {
                0 => FontIndex,
                1 => Math.Max(0, assets.Fonts!.AvailableFonts.ToList().IndexOf(f.Resource!)),
                2 => (f.Scale - .5f) / .1f,
                3 => f.Spacing + 3,
                _ => f.LineSpacing + 12
            };
            Sliders[i].SetValue(value);
            if (Sliders[i].NotchChanged) SoundRequested?.Invoke("slider_bar_1.wav");
        }
    }
    public void RefreshAfterMove() { if (Page == 9) RefreshFont(0); }
    public void Change(int row, float value)
    {
        if (owner is null || row < 0 || row >= Rows) return;
        var slider = Sliders[row]; slider.SetValue(value);
        value = (float)slider.Value;
        bool changed = slider.NotchChanged;
        if (changed) SoundRequested?.Invoke("slider_bar_1.wav");
        ApplySliderCommand(row);
    }
    public void ApplySliderCommand(int row)
    {
        if(owner is null || row<0 || row>=Rows) return;
        float value=(float)Sliders[row].Value;
        if (Page == 9)
        {
            // 0066F330 rewrites the event for 005CD0C0 independently of the rounded audio cue.
            // 005CD0C0 reads ftol(slider.D4), independently of the gadget's rounded cue value.
            if (row == 0) { FontIndex = (int)value; RefreshFont(); return; }
            for (int i = 0; i < Fonts.Length; i++)
                if (Check || i == FontIndex)
                    Fonts[i] = row switch
                    {
                        1 => Fonts[i] with { Resource = assets.Fonts!.AvailableFonts[(int)value] },
                        2 => Fonts[i] with { Scale = .5f + value * .1f },
                        3 => Fonts[i] with { Spacing = (int)value - 3 },
                        _ => Fonts[i] with { LineSpacing = (int)value - 12 },
                    };
            PreviewFonts(); RefreshFont(row);
        }
        else if (Page == 8) assets.SetSoundPreview(owner!.Value, Sound());
    }
    private void CopyFont()
    {
        var selected = Fonts[FontIndex];
        for (int i = 0; i < Fonts.Length; i++)
            Fonts[i] = selected with { Slot = Fonts[i].Slot, FontId = Fonts[i].FontId };
        PreviewFonts();
    }

    public EnbRetailSoundOptions Sound() => new((float)Sliders[0].Value, (float)Sliders[1].Value, (float)Sliders[2].Value, Check);
    private void PreviewFonts()
    {
        Error = null;
        for (int i = 0; i < Fonts.Length; i++)
        {
            var f = Fonts[i];
            if (fontHeight(f) is not int height)
            {
                Error = "Chat font asset unavailable: " + f.Resource;
                Fonts[i] = assets.ResolveChatFont(owner, Profile!, Profile!.Fonts.First(a => a.FontId == f.FontId));
                continue;
            }
            // 006BCF00 uses half the scanned atlas cell height.
            Fonts[i] = f with { LineSpacing = Math.Max(-(height / 2), f.LineSpacing) };
        }
        assets.SetChatFontPreview(owner!.Value, Profile!, Fonts);
    }

    public void ToggleCheck(bool apply = true)
    {
        if (Page == 7 && (Section != NavigationSection || !RoutePlanner)) return;
        Check = !Check;
        if(!apply) return;
        if (Page == 9) { RefreshFont(0); if (Check) CopyFont(); }
    }

    public void PreviewSound() { if(owner is uint character && Page==8) assets.SetSoundPreview(character,(float)Sliders[0].Value,(float)Sliders[1].Value,(float)Sliders[2].Value,Check); }

    public void DefaultFont()
    {
        Fonts[FontIndex] = Profile!.Fonts.First(f => f.FontId == Fonts[FontIndex].FontId);
        if (Check) CopyFont(); else PreviewFonts();
        RefreshFont();
    }

    public bool TrySave(Action<float>? saveInterface, Action<EnbRetailSoundOptions>? saveSound,
        Action<EnbFontProfile, IReadOnlyList<EnbFontDefinition>>? saveFont)
    {
        if (owner is not uint character) return false;
        try
        {
            if (Page == 7 && Section == RetailSection) { if (saveInterface is not null) saveInterface((float)Sliders[0].Value); else assets.SaveInterfaceOptions(character, (float)Sliders[0].Value); }
            else if (Page == 7 && Section == NavigationSection) { assets.CharacterInterface.Save(RoutePlanner, Check, assets.CharacterInterface.LayoutProfile); SaveQualityOfLife?.Invoke(QualityOfLife); }
            else if (Page == 7 && Section == SocialSection) { SaveQualityOfLife?.Invoke(QualityOfLife); SaveDiscordPresence?.Invoke(DiscordPresence); }
            else if (Page == 7) { SaveQualityOfLife?.Invoke(QualityOfLife); SaveMultibox?.Invoke(Multibox); }
            else if (Page == 8) { if (saveSound is not null) saveSound(Sound()); else assets.SaveSoundOptions(character, Sound()); SaveQualityOfLife?.Invoke(QualityOfLife); }
            else { if (saveFont is not null) saveFont(Profile!, Fonts); else assets.SaveChatFonts(character, Profile!, Fonts); }
            EndQualityPreview(saved: true); Retire(); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { Error = "Could not save settings: " + ex.Message; return false; }
    }

    public void Retire()
    {
        EndQualityPreview(saved: false);
        if (owner is uint character)
        {
            if (Page == 8) assets.SetSoundPreview(character, null);
            if (Page == 9 && Profile is not null) assets.SetChatFontPreview(character, Profile, null);
        }
        foreach (var slider in Sliders) slider.Cancel();
        owner = null; Page = 0; Section = RetailSection; Profile = null; Fonts = []; Error = null;
    }
}
