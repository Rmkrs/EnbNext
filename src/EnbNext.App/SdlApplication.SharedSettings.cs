using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnbNext.Assets;
using EnbNext.Client.Models;
using EnbNext.Host.Sdl;
using EnbNext.Presentation;

namespace EnbNext.App;

/// <summary>The earlier "same settings for all characters" choice (one character as the source); read once to move it
/// to <see cref="SettingsScopes"/>.</summary>
internal sealed record SharedCharacterSettings(string Name, string Server, string Login, ulong Account, ulong Avatar, uint Session);

/// <summary>Which settings sections are one set for all characters. A section set to all characters holds for every
/// character, old and new; there is no per-character exception.</summary>
internal sealed record SettingsScopes(bool GameOptions=false,bool Navigation=false,bool Multibox=false,bool ChatTabs=false);

/// <summary>
/// Next settings scopes: each settings section (game options: camera, interface and chat display; navigation and the UI
/// layout profile; multibox options; Modern UI chat tabs) is either each character's own or one set for all characters.
/// A section for all characters keeps its settings in one shared file: every running client takes a change made by any
/// of them into its own file and applies it within a second, and a character takes them when it is selected. What
/// belongs to the character or its screen stays its own: graphics, sound, chat channels, the captain's log page, window
/// and display. Quality of Life options are shared by all characters anyway.
/// </summary>
internal sealed partial class SdlApplication
{
    private enum SettingsSection{GameOptions,Navigation,Multibox,ChatTabs}
    private static readonly SettingsSection[] SettingsSections=Enum.GetValues<SettingsSection>();
    private static readonly Encoding Utf8=new UTF8Encoding(false);
    private readonly EnbSettingsMirror[] settingsMirrors=
    [
        new(Encoding.Latin1,(shared,own)=>own is null?shared:EnbRetailCinematicOptionsWriter.ShareOptions(shared,own)),
        new(Utf8,(shared,_)=>shared),
        new(Utf8,(shared,_)=>shared),
        new(Utf8,MergeChatTabs),
    ];
    private string SettingsDirectory=>Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!;
    private string SettingsScopesPath=>Path.Combine(SettingsDirectory,"settings-scopes.json");
    private string EarlierSharedSettingsPath=>Path.Combine(SettingsDirectory,"shared-character-settings.json");
    private string AllCharactersPath(SettingsSection section)=>Path.Combine(SettingsDirectory,"all-characters",section switch
    {
        SettingsSection.GameOptions=>"game-options.ini",SettingsSection.Navigation=>"navigation.json",SettingsSection.Multibox=>"multibox.json",_=>"chat-tabs.json",
    });
    private SettingsScopes settingsScopes=new();
    private DateTime settingsScopesSeen,preferencesSeen;
    private long settingsScopesNext;

    private SettingsScopes ReadSettingsScopes()
    {
        try
        {
            var written=File.Exists(SettingsScopesPath)?File.GetLastWriteTimeUtc(SettingsScopesPath):default;
            if(written!=settingsScopesSeen)
            {
                settingsScopesSeen=written;
                settingsScopes=written==default?new():JsonSerializer.Deserialize<SettingsScopes>(File.ReadAllText(SettingsScopesPath))??new();
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){AppendDiagnostic("Settings scopes could not be read: "+ex.Message);}
        return settingsScopes;
    }
    private static bool ForAll(SettingsScopes scopes,SettingsSection section)=>section switch
    {
        SettingsSection.GameOptions=>scopes.GameOptions,SettingsSection.Navigation=>scopes.Navigation,SettingsSection.Multibox=>scopes.Multibox,_=>scopes.ChatTabs,
    };
    private static SettingsSection? SectionFor(string key)=>key switch
    {
        "game"=>SettingsSection.GameOptions,"navigation"=>SettingsSection.Navigation,"multibox"=>SettingsSection.Multibox,"chat"=>SettingsSection.ChatTabs,_=>null,
    };
    private static string SectionName(SettingsSection section)=>section switch
    {
        SettingsSection.GameOptions=>"Game options",SettingsSection.Navigation=>"Navigation and UI layout",SettingsSection.Multibox=>"Multibox settings",_=>"Chat tabs",
    };

    /// <summary>Options: whether a section is for all characters (null without a character in game).</summary>
    private bool? SettingsScopeOf(string key)=>_client?.SelectedCharacter is not null&&SectionFor(key) is {} section?ForAll(ReadSettingsScopes(),section):null;

    /// <summary>Makes a section one set for all characters, starting from the character in game's settings, or each
    /// character's own again (each keeps the settings it has).</summary>
    private void SetSettingsScope(string key,bool all)
    {
        if(SectionFor(key) is not {} section)return;
        try
        {
            var scopes=ReadSettingsScopes();
            scopes=section switch
            {
                SettingsSection.GameOptions=>scopes with {GameOptions=all},SettingsSection.Navigation=>scopes with {Navigation=all},
                SettingsSection.Multibox=>scopes with {Multibox=all},_=>scopes with {ChatTabs=all},
            };
            var mirror=settingsMirrors[(int)section];
            if(all&&OwnSettingsPath(section) is {} own)mirror.Seed(own,AllCharactersPath(section));else mirror.Forget();
            mirror.Write(SettingsScopesPath,JsonSerializer.Serialize(scopes));
            settingsScopes=scopes;settingsScopesSeen=File.GetLastWriteTimeUtc(SettingsScopesPath);
            AppendTranscript(all?SectionName(section)+" are now the same for all your characters.":SectionName(section)+" are now each character's own again.");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppendDiagnostic("Settings scope could not be saved: "+ex.Message);}
    }

    /// <summary>The earlier choice (one character's settings for all) becomes every section for all characters,
    /// starting from that character's settings.</summary>
    private void MoveEarlierSharedSettings(EnbRetailAssetBundle? assets)
    {
        if(assets is null||!File.Exists(EarlierSharedSettingsPath))return;
        try
        {
            if(JsonSerializer.Deserialize<SharedCharacterSettings>(File.ReadAllText(EarlierSharedSettingsPath)) is {} earlier)
            {
                Seed(SettingsSection.GameOptions,Path.Combine(assets.Paths.ClientOutputDirectory,$"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(earlier.Session)}_options.ini"));
                Seed(SettingsSection.Navigation,EnbCharacterInterfaceSettings.PathFor(earlier.Account,earlier.Avatar));
                Seed(SettingsSection.Multibox,MultiboxOptionsPathFor(earlier.Server,earlier.Login,earlier.Session));
                Seed(SettingsSection.ChatTabs,ModernChatTabsPathFor(earlier.Server,earlier.Login,earlier.Session));
                settingsMirrors[0].Write(SettingsScopesPath,JsonSerializer.Serialize(new SettingsScopes(true,true,true,true)));
            }
            File.Delete(EarlierSharedSettingsPath);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){AppendDiagnostic("Earlier shared settings could not be moved: "+ex.Message);}

        void Seed(SettingsSection section,string source){var mirror=settingsMirrors[(int)section];if(mirror.Read(source) is {} text)mirror.Write(AllCharactersPath(section),text);mirror.Forget();}
    }

    /// <summary>The client selected a character: each section for all characters is copied into its files before they are read.</summary>
    private void PrepareCharacterSettings(EnbNext.Client.EnbClient client,CharacterSummary character,uint session)
    {
        MoveEarlierSharedSettings(client.RetailAssets);
        var scopes=ReadSettingsScopes();
        foreach(var section in SettingsSections)
        {
            var mirror=settingsMirrors[(int)section];mirror.Forget();
            try{if(ForAll(scopes,section)&&OwnSettingsPath(section,character,session,client.RetailAssets) is {} own)mirror.Take(own,AllCharactersPath(section));}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppendDiagnostic(SectionName(section)+" for all characters: "+ex.Message);}
        }
        client.RetailAssets?.ForgetPlayerOptions(session);
    }

    /// <summary>Every second: a section for all characters shares a change made here, or takes and applies a change
    /// another client made.</summary>
    private void TickSettingsScopes()
    {
        long now=Environment.TickCount64;if(now<settingsScopesNext)return;settingsScopesNext=now+1000;
        FollowQualityOfLife();
        MoveEarlierSharedSettings(_client?.RetailAssets);
        if(_client is not {SelectedCharacter:not null,SelectedFixedSessionId:uint})return;
        var scopes=ReadSettingsScopes();
        foreach(var section in SettingsSections)
        {
            var mirror=settingsMirrors[(int)section];
            if(!ForAll(scopes,section)){mirror.Forget();continue;}
            if(OwnSettingsPath(section) is not {} own)continue;
            try{if(mirror.Step(own,AllCharactersPath(section))==EnbSettingsMirror.Change.Taken)ApplySettings(section);}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){AppendDiagnostic(SectionName(section)+" for all characters: "+ex.Message);}
        }
    }

    /// <summary>Quality of Life options are one set for all characters (the startup preferences): a change saved by
    /// another running client is taken here too (not while this client previews its own unsaved change).</summary>
    private void FollowQualityOfLife()
    {
        try
        {
            if(!File.Exists(_preferencesPath))return;
            var written=File.GetLastWriteTimeUtc(_preferencesPath);if(written==preferencesSeen)return;
            preferencesSeen=written;
            if(previewQualityOfLife is not null||JsonSerializer.Deserialize<EnbStartupPreferences>(File.ReadAllText(_preferencesPath)) is not {QualityOfLife:{} shared})return;
            if(shared==(_preferences.QualityOfLife??new()))return;
            _preferences=_preferences with {QualityOfLife=shared};
            _client?.ConfigureQualityOfLife(shared);
            if(worldScreen is {} world)ApplyNextOptions(world.Owner,shared);
            LiveWorldOpenAlAudio.SetFocusMuted(shared.MuteBackgroundAudio&&!window.Focused);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException){AppendDiagnostic("Quality of Life options could not be read: "+ex.Message);}
    }

    private string? OwnSettingsPath(SettingsSection section)
        =>_client is {SelectedCharacter:{} character,SelectedFixedSessionId:uint session} client?OwnSettingsPath(section,character,session,client.RetailAssets):null;
    private string? OwnSettingsPath(SettingsSection section,CharacterSummary character,uint session,EnbRetailAssetBundle? assets)=>section switch
    {
        SettingsSection.GameOptions=>assets is null?null:Path.Combine(assets.Paths.ClientOutputDirectory,$"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(session)}_options.ini"),
        SettingsSection.Navigation=>EnbCharacterInterfaceSettings.PathFor(character.AccountId,character.AvatarId),
        SettingsSection.Multibox=>MultiboxOptionsPathFor(_savedAccountServer,_savedAccountLogin,session),
        _=>ModernChatTabsPathFor(_savedAccountServer,_savedAccountLogin,session),
    };

    /// <summary>Chat tabs: the shared tabs, keeping this character's tab in view and the dialog's place.</summary>
    private static string MergeChatTabs(string shared,string? own)
    {
        if(own is null)return shared;
        try
        {
            if(JsonNode.Parse(own) is not JsonObject mine||JsonNode.Parse(shared) is not JsonObject theirs)return shared;
            mine["Tabs"]=theirs["Tabs"]?.DeepClone();
            if(mine["Active"] is JsonValue active&&active.TryGetValue(out int index)&&theirs["Tabs"] is JsonArray tabs&&index>=tabs.Count)mine["Active"]=0;
            return mine.ToJsonString();
        }
        catch(JsonException){return shared;}
    }

    /// <summary>A section's file was replaced with the shared settings: the running client uses them at once.</summary>
    private void ApplySettings(SettingsSection section)
    {
        if(_client is not {SelectedCharacter:{} character,SelectedFixedSessionId:uint session} client)return;
        switch(section)
        {
            case SettingsSection.GameOptions:client.RetailAssets?.ForgetPlayerOptions(session);break;
            case SettingsSection.Navigation:
                if(client.RetailAssets is {} assets)
                {
                    var read=EnbCharacterInterfaceSettings.Load(character.AccountId,character.AvatarId);var current=assets.CharacterInterface;
                    current.RoutePlanner=read.RoutePlanner;current.AutoPilot=read.AutoPilot;current.LayoutProfile=read.LayoutProfile;
                    worldScreen?.Owner.Ui.Layout.Reload();
                }
                break;
            case SettingsSection.Multibox:
                if(multiboxCharacter==session&&File.Exists(MultiboxOptionsPath)&&JsonSerializer.Deserialize<EnbMultiboxOptions>(File.ReadAllText(MultiboxOptionsPath)) is {} options)
                {multiboxOptions=options with {ShortcutButtons=Math.Clamp(options.ShortcutButtons,0,12),EncounterPauseSeconds=Math.Clamp(options.EncounterPauseSeconds,1,60)};multiboxNext=0;}
                break;
            default:worldScreen?.Owner.Ui.Chat.ReloadModernTabs();break;
        }
    }
}
