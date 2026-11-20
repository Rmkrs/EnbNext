using EnbNext.Engine;
using EnbNext.Presentation;
using SDL;
namespace EnbNext.App;
internal sealed partial class SdlApplication
{
    /// <summary>Applies the Modern UI option (live while its options page previews it).</summary>
    /// <summary>The folder for one's own music, made with its Combat and Planet folders when missing, shown in Explorer.</summary>
    private static void OpenMusicFolder(string folder) => OpenFolder(folder, "Combat", "Planet");
    /// <summary>A folder (made when missing, with the given subfolders) shown in the system's file manager.</summary>
    private static void OpenFolder(string folder, params string[] subfolders)
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (string sub in subfolders) Directory.CreateDirectory(Path.Combine(folder, sub));
            var start = OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + folder + "\"")
                : new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { folder } };
            start.UseShellExecute = OperatingSystem.IsWindows();
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        { System.Diagnostics.Trace.TraceWarning("Folder " + folder + ": " + ex.Message); }
    }
    /// <summary>Where Modern UI chat logs are written: the player's documents.</summary>
    private static string ChatLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EnbNext", "Chat logs");

    private void TickModernUi()
    {
        if (worldScreen is not { } world) return;
        world.Owner.Ui.ModernUi = EffectiveQualityOfLife.ModernUi;
        world.Owner.Ui.Chat.ModernTextSize = EffectiveQualityOfLife.ChatTextSize;
        // Times preview while Options is open; the kept history and the logs follow the saved choice.
        var savedQuality = _preferences.QualityOfLife ?? new();
        world.Owner.Ui.Chat.ModernTabsPath ??= ModernChatTabsPath;
        world.Owner.Ui.Chat.ModernTimes = EffectiveQualityOfLife.ModernUi && EffectiveQualityOfLife.ChatTimes;
        world.Owner.Ui.Chat.ModernKeepHistory = savedQuality.ChatKeepHistory;
        world.Owner.Ui.Chat.ModernSaveLogs = savedQuality.ModernUi && savedQuality.ChatSaveLogs;
        world.Owner.Ui.Chat.ModernChatLogDirectory ??= ChatLogDirectory;
        world.Owner.Options.ChatLogFolderRequested ??= () => OpenFolder(ChatLogDirectory);
        world.Owner.HideCorpses = EffectiveQualityOfLife.ModernUi && EffectiveQualityOfLife.HideCorpses;
        world.Owner.Ui.Inventory.ModernLocalKeptOnEscape = EffectiveQualityOfLife.KeepLocalOnEscape;
        world.Owner.StationFreeMovement = EffectiveQualityOfLife.StationFreeMovement;
        world.Owner.StationLookSpeed = EffectiveQualityOfLife.LookSpeedPercent / 100f;
        world.Owner.World.AttackerNames = EffectiveQualityOfLife.ModernUi && EffectiveQualityOfLife.AttackerNames;
        if (world.Owner.Ui.ModernHud is { } hud)
        {
            hud.BarText = (EffectiveQualityOfLife.BarPercentages, EffectiveQualityOfLife.ExperiencePercentages, EffectiveQualityOfLife.ExperienceValues);
            hud.RightClickCancelsTarget = EffectiveQualityOfLife.RightClickCancelsTarget;
            hud.ConnectionShown = EffectiveQualityOfLife.ConnectionIndicator;
            hud.ConnectionSource ??= () => _client?.Connection;
        }
        // The damage meter: Modern UI style, right-click menu and a kept place while Modern UI is on.
        if (damagePanel is { } meter)
        {
            meter.Modern = EffectiveQualityOfLife.ModernUi;
            meter.ModernMenuRequested ??= (heading, items, at) => worldScreen?.Owner.Ui.ShowModernMenu("damage/meter", heading, items, at);
            if (meter.ModernMoved is null)
            {
                if (_preferences.ModernMeterX is double mx && _preferences.ModernMeterY is double my) meter.ModernPosition = new(mx, my);
                meter.ModernMoved = at => { _preferences = _preferences with { ModernMeterX = at.X, ModernMeterY = at.Y }; SavePreferences(); };
            }
        }
        // The My characters panel: Modern UI style and right-click menus while Modern UI is on.
        if (launchPanel is { } launch)
        {
            launch.Modern = EffectiveQualityOfLife.ModernUi;
            launch.ModernMenuRequested ??= (heading, items, at) => worldScreen?.Owner.Ui.ShowModernMenu("multibox/panel", heading, items, at);
            if (launch.ModernMoved is null)
            {
                if (_preferences.ModernCharactersX is double kx && _preferences.ModernCharactersY is double ky) launch.ModernPosition = new(kx, ky);
                launch.ModernMoved = at => { _preferences = _preferences with { ModernCharactersX = at.X, ModernCharactersY = at.Y }; SavePreferences(); };
            }
        }
        // The Modern UI inventory remembers where the player dragged it.
        if (world.Owner.Ui.Inventory.ModernInventoryMoved is null)
        {
            if (_preferences.ModernInventoryX is double x && _preferences.ModernInventoryY is double y) world.Owner.Ui.Inventory.ModernInventoryPosition = new(x, y);
            world.Owner.Ui.Inventory.ModernInventoryMoved = at => { _preferences = _preferences with { ModernInventoryX = at.X, ModernInventoryY = at.Y }; SavePreferences(); };
            if (_preferences.ModernEquipmentX is double ex && _preferences.ModernEquipmentY is double ey) world.Owner.Ui.Inventory.ModernEquipmentPosition = new(ex, ey);
            world.Owner.Ui.Inventory.ModernEquipmentMoved = at => { _preferences = _preferences with { ModernEquipmentX = at.X, ModernEquipmentY = at.Y }; SavePreferences(); };
            if (_preferences.ModernSkillsX is double sx && _preferences.ModernSkillsY is double sy) world.Owner.Ui.Inventory.ModernSkillsPosition = new(sx, sy);
            world.Owner.Ui.Inventory.ModernSkillsMoved = at => { _preferences = _preferences with { ModernSkillsX = at.X, ModernSkillsY = at.Y }; SavePreferences(); };
            if (_preferences.ModernMissionsX is double mx && _preferences.ModernMissionsY is double my) world.Owner.Ui.Inventory.ModernMissionsPosition = new(mx, my);
            world.Owner.Ui.Inventory.ModernMissionsMoved = at => { _preferences = _preferences with { ModernMissionsX = at.X, ModernMissionsY = at.Y }; SavePreferences(); };
            if (_preferences.ModernFactionsX is double fx && _preferences.ModernFactionsY is double fy) world.Owner.Ui.Inventory.ModernFactionsPosition = new(fx, fy);
            world.Owner.Ui.Inventory.ModernFactionsMoved = at => { _preferences = _preferences with { ModernFactionsX = at.X, ModernFactionsY = at.Y }; SavePreferences(); };
            if (_preferences.ModernCommandsX is double cx && _preferences.ModernCommandsY is double cy) world.Owner.Ui.Inventory.ModernCommandsPosition = new(cx, cy);
            world.Owner.Ui.Inventory.ModernCommandsMoved = at => { _preferences = _preferences with { ModernCommandsX = at.X, ModernCommandsY = at.Y }; SavePreferences(); };
            if (_preferences.ModernFriendsX is double rx && _preferences.ModernFriendsY is double ry) world.Owner.Ui.Inventory.ModernFriendsPosition = new(rx, ry);
            world.Owner.Ui.Inventory.ModernFriendsMoved = at => { _preferences = _preferences with { ModernFriendsX = at.X, ModernFriendsY = at.Y }; SavePreferences(); };
            if (_preferences.ModernGuildX is double ux && _preferences.ModernGuildY is double uy) world.Owner.Ui.Inventory.ModernGuildPosition = new(ux, uy);
            world.Owner.Ui.Inventory.ModernGuildMoved = at => { _preferences = _preferences with { ModernGuildX = at.X, ModernGuildY = at.Y }; SavePreferences(); };
            if (_preferences.ModernFinderX is double fdx && _preferences.ModernFinderY is double fdy) world.Owner.Ui.Inventory.ModernFinderPosition = new(fdx, fdy);
            world.Owner.Ui.Inventory.ModernFinderMoved = at => { _preferences = _preferences with { ModernFinderX = at.X, ModernFinderY = at.Y }; SavePreferences(); };
            if (_preferences.ModernTalkX is double tkx && _preferences.ModernTalkY is double tky) world.Owner.Ui.Inventory.ModernTalkPosition = new(tkx, tky);
            if (_preferences.ModernDialogueTextScale is double dialogueScale) world.Owner.Ui.Inventory.ModernTalkTextScale = dialogueScale;
            world.Owner.Ui.Inventory.ModernTalkTextScaleChanged = scale => { _preferences = _preferences with { ModernDialogueTextScale = scale }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernTalkMoved = at => { _preferences = _preferences with { ModernTalkX = at.X, ModernTalkY = at.Y }; SavePreferences(); };
            if (_preferences.ModernTradeX is double trx && _preferences.ModernTradeY is double tr_y) world.Owner.Ui.Inventory.ModernTradePosition = new(trx, tr_y);
            world.Owner.Ui.Inventory.ModernTradeMoved = at => { _preferences = _preferences with { ModernTradeX = at.X, ModernTradeY = at.Y }; SavePreferences(); };
            if (_preferences.ModernLootX is double lx && _preferences.ModernLootY is double ly) world.Owner.Ui.Inventory.ModernLootPosition = new(lx, ly);
            world.Owner.Ui.Inventory.ModernLootMoved = at => { _preferences = _preferences with { ModernLootX = at.X, ModernLootY = at.Y }; SavePreferences(); };
            if (_preferences.ModernVendorX is double vx && _preferences.ModernVendorY is double vy) world.Owner.Ui.Inventory.ModernVendorPosition = new(vx, vy);
            world.Owner.Ui.Inventory.ModernVendorMoved = at => { _preferences = _preferences with { ModernVendorX = at.X, ModernVendorY = at.Y }; SavePreferences(); };
            // Space and a station keep their own Local place and size.
            if (_preferences.ModernLocalX is double lcx && _preferences.ModernLocalY is double lcy) world.Owner.Ui.Inventory.ModernLocalSpacePosition = new(lcx, lcy);
            if (_preferences.ModernLocalStationX is double lsx && _preferences.ModernLocalStationY is double lsy) world.Owner.Ui.Inventory.ModernLocalStationPosition = new(lsx, lsy);
            world.Owner.Ui.Inventory.ModernLocalMoved = at => { _preferences = world.Owner.Ui.Inventory.ModernLocalInStation ? _preferences with { ModernLocalStationX = at.X, ModernLocalStationY = at.Y } : _preferences with { ModernLocalX = at.X, ModernLocalY = at.Y }; SavePreferences(); };
            // Next music player: its choices and its window's place are kept.
            var music = world.Owner.World.MusicPlayer;
            music.CombatMusic = _preferences.MusicCombat ?? true; music.Races = _preferences.MusicRaces ?? 0;
            world.Owner.Ui.Inventory.ModernMusicChanged = () => { _preferences = _preferences with { MusicCombat = music.CombatMusic, MusicRaces = music.Races }; SavePreferences(); };
            if (_preferences.ModernMusicX is double mux && _preferences.ModernMusicY is double muy) world.Owner.Ui.Inventory.ModernMusicPosition = new(mux, muy);
            world.Owner.Ui.Inventory.ModernMusicMoved = at => { _preferences = _preferences with { ModernMusicX = at.X, ModernMusicY = at.Y }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernMusicFolderRequested = OpenMusicFolder;
            // Next wiki window: its place and size are kept.
            if (_preferences.ModernWikiX is double wkx && _preferences.ModernWikiY is double wky) world.Owner.Ui.Inventory.ModernWikiPosition = new(wkx, wky);
            if (_preferences.ModernWikiWidth is double wkw && _preferences.ModernWikiHeight is double wkh) world.Owner.Ui.Inventory.ModernWikiSize = new(wkw, wkh);
            world.Owner.Ui.Inventory.ModernWikiMoved = at => { _preferences = _preferences with { ModernWikiX = at.X, ModernWikiY = at.Y }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernWikiResized = size => { _preferences = _preferences with { ModernWikiWidth = size.X, ModernWikiHeight = size.Y }; SavePreferences(); };
            // Next item catalogue: its place and size are kept.
            if (_preferences.ModernItemsX is double itx && _preferences.ModernItemsY is double ity) world.Owner.Ui.Inventory.ModernItemsPosition = new(itx, ity);
            if (_preferences.ModernItemsWidth is double itw && _preferences.ModernItemsHeight is double ith) world.Owner.Ui.Inventory.ModernItemsSize = new(itw, ith);
            world.Owner.Ui.Inventory.ModernItemsMoved = at => { _preferences = _preferences with { ModernItemsX = at.X, ModernItemsY = at.Y }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernItemsResized = size => { _preferences = _preferences with { ModernItemsWidth = size.X, ModernItemsHeight = size.Y }; SavePreferences(); };
            if (_preferences.ModernTerminalX is double tmx && _preferences.ModernTerminalY is double tmy) world.Owner.Ui.Inventory.ModernTerminalPosition = new(tmx, tmy);
            world.Owner.Ui.Inventory.ModernTerminalMoved = at => { _preferences = _preferences with { ModernTerminalX = at.X, ModernTerminalY = at.Y }; SavePreferences(); };
            if (_preferences.ModernStationMapX is double smx && _preferences.ModernStationMapY is double smy) world.Owner.Ui.Inventory.ModernStationMapPosition = new(smx, smy);
            world.Owner.Ui.Inventory.ModernStationMapMoved = at => { _preferences = _preferences with { ModernStationMapX = at.X, ModernStationMapY = at.Y }; SavePreferences(); };
            if (_preferences.ModernVaultX is double vtx && _preferences.ModernVaultY is double vty) world.Owner.Ui.Inventory.ModernVaultPosition = new(vtx, vty);
            world.Owner.Ui.Inventory.ModernVaultMoved = at => { _preferences = _preferences with { ModernVaultX = at.X, ModernVaultY = at.Y }; SavePreferences(); };
            if (_preferences.ModernLocalWidth is double lw && _preferences.ModernLocalRows is double lr) world.Owner.Ui.Inventory.ModernLocalSpaceSize = new(lw, lr);
            if (_preferences.ModernLocalStationWidth is double lsw && _preferences.ModernLocalStationRows is double lsr) world.Owner.Ui.Inventory.ModernLocalStationSize = new(lsw, lsr);
            if (_preferences.ModernLocalOpacity is double lo) world.Owner.Ui.Inventory.ModernLocalOpacity = lo;
            world.Owner.Ui.Inventory.ModernLocalWanted = _preferences.ModernLocalOpen == true;
            world.Owner.Ui.Inventory.ModernLocalWantedChanged = open => { _preferences = _preferences with { ModernLocalOpen = open }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernLocalOpacityChanged = o => { _preferences = _preferences with { ModernLocalOpacity = o }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernLocalColumns = _preferences.ModernLocalColumns ?? 0;
            world.Owner.Ui.Inventory.ModernLocalColumnsChanged = c => { _preferences = _preferences with { ModernLocalColumns = c }; SavePreferences(); };
            world.Owner.Ui.Inventory.ModernLocalResized = size => { _preferences = world.Owner.Ui.Inventory.ModernLocalInStation ? _preferences with { ModernLocalStationWidth = size.X, ModernLocalStationRows = size.Y } : _preferences with { ModernLocalWidth = size.X, ModernLocalRows = size.Y }; SavePreferences(); };
            if (_preferences.ModernLogX is double lgx && _preferences.ModernLogY is double lgy) world.Owner.Ui.Inventory.ModernLogPosition = new(lgx, lgy);
            world.Owner.Ui.Inventory.ModernLogMoved = at => { _preferences = _preferences with { ModernLogX = at.X, ModernLogY = at.Y }; SavePreferences(); };
            if (_preferences.ModernOptionsX is double opx && _preferences.ModernOptionsY is double opy) world.Owner.Ui.Inventory.ModernOptionsPosition = new(opx, opy);
            world.Owner.Ui.Inventory.ModernOptionsMoved = at => { _preferences = _preferences with { ModernOptionsX = at.X, ModernOptionsY = at.Y }; SavePreferences(); };
            var map = world.Owner.Ui.Map;
            if (_preferences.ModernMapX is double ax && _preferences.ModernMapY is double ay) map.ModernMapPosition = new(ax, ay);
            map.ModernMapMoved = at => { _preferences = _preferences with { ModernMapX = at.X, ModernMapY = at.Y }; SavePreferences(); };
            if (_preferences.ModernMapWidth is double mw && _preferences.ModernMapHeight is double mh) map.ModernMapSize = new(mw, mh);
            map.ModernMapResized = size => { _preferences = _preferences with { ModernMapWidth = size.X, ModernMapHeight = size.Y }; SavePreferences(); };
            map.ModernMapNames = _preferences.ModernMapNames; map.ModernMapPointerCoordinates = _preferences.ModernMapPointerCoordinates;
            map.ModernMapSeeThrough = _preferences.ModernMapSeeThrough;
            map.ModernMapSettingsChanged = () => { _preferences = _preferences with { ModernMapNames = map.ModernMapNames, ModernMapPointerCoordinates = map.ModernMapPointerCoordinates,
                ModernMapSeeThrough = map.ModernMapSeeThrough }; SavePreferences(); };
            // The galaxy map is its own window: its own place, size and choices.
            var galaxy = world.Owner.Ui.GalaxyMap;
            if (_preferences.ModernGalaxyX is double gx && _preferences.ModernGalaxyY is double gy) galaxy.ModernMapPosition = new(gx, gy);
            galaxy.ModernMapMoved = at => { _preferences = _preferences with { ModernGalaxyX = at.X, ModernGalaxyY = at.Y }; SavePreferences(); };
            if (_preferences.ModernGalaxyWidth is double gw && _preferences.ModernGalaxyHeight is double gh) galaxy.ModernMapSize = new(gw, gh);
            galaxy.ModernMapResized = size => { _preferences = _preferences with { ModernGalaxyWidth = size.X, ModernGalaxyHeight = size.Y }; SavePreferences(); };
            galaxy.ModernMapSeeThrough = _preferences.ModernGalaxySeeThrough; galaxy.ModernGalaxySchematic = _preferences.ModernGalaxySchematic;
            galaxy.ModernMapSettingsChanged = () => { _preferences = _preferences with { ModernGalaxySeeThrough = galaxy.ModernMapSeeThrough, ModernGalaxySchematic = galaxy.ModernGalaxySchematic }; SavePreferences(); };
        }
    }

    /// <summary>A character's Modern UI chat tabs, kept with the Next preferences (per account and character).</summary>
    private string ModernChatTabsPath(uint character) => ModernChatTabsPathFor(_savedAccountServer, _savedAccountLogin, character);
    private string ModernChatTabsPathFor(string? server, string? login, uint character) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_preferencesPath))!, "modern-chat",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes((server ?? "").ToUpperInvariant() + "/" + (login ?? "").ToUpperInvariant())))[..16] + "-" + character + ".json");

    /// <summary>An open Modern UI menu takes the pointer over it; a press elsewhere closes it (and still reaches the game,
    /// except on the button that opened it, which would open it again).</summary>
    private bool HandleModernMenu(SDL_Event e)
    {
        if (worldScreen is not { } world || world.Owner.Ui.ModernMenu is not { Open: true } menu) return false;
        var type = (SDL_EventType)e.type;
        if (type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED) { menu.Close(); return false; }
        if (type == SDL_EventType.SDL_EVENT_KEY_DOWN && e.key.key == SDL_Keycode.SDLK_ESCAPE) { menu.Close(); world.Owner.Hud.QueueUtilitySound("SR_scifielectronic42.wav"); return true; }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) { var p = new EnbUiPoint(e.motion.x, e.motion.y); menu.Pointer(p); return menu.Contains(p); }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)
        {
            var p = new EnbUiPoint(e.button.x, e.button.y);
            // 0064A9B0: menu options act on an ordinary left press only (mask 5); another button over the menu does nothing (GROUP-D26).
            if (e.button.button != 1 && menu.Contains(p)) return true;
            if (menu.Press(p)) return true;
            bool own = menu.Button is { } button && button.Contains(p);
            menu.Close(); world.Owner.Hud.QueueUtilitySound("SR_scifielectronic42.wav");
            return own;
        }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP) return menu.Contains(new(e.button.x, e.button.y));
        if (type == SDL_EventType.SDL_EVENT_MOUSE_WHEEL) return menu.Contains(new(e.wheel.mouse_x, e.wheel.mouse_y));
        return false;
    }

    private bool HandleModernHud(SDL_Event e)
    {
        if (worldScreen is not { } world || world.Owner.Ui.ModernHud is not { } hud || world.Owner.Ui.Layout.Editing) return false;
        var type = (SDL_EventType)e.type;
        if (PointerUnderGameUi(world, e)) { if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) hud.Pointer(new(-100000, -100000)); return false; }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_MOTION) { var p = new EnbUiPoint(e.motion.x, e.motion.y); hud.Pointer(p); return hud.Contains(p); }
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN) return e.button.button is 1 or 3 && hud.Press(new(e.button.x, e.button.y), e.button.button == 3);
        if (type == SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP) return hud.Contains(new(e.button.x, e.button.y));
        return false;
    }
}
