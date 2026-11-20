using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using EnbNext.Engine;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Social;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

/// <summary>Chat input and hit regions; all visible pixels are submitted to the HUD GPU.</summary>
internal sealed partial class EnbChatUi : IDisposable
{
    internal EnbUiLayoutProfiles? UiLayout {get;set;}
    private static readonly IReadOnlyList<string> ColorNames = EnbRetailChatColors.Names;
    private static readonly string?[] FilterNames = ["Chat_Status_IC", null, "Channel_Broadcast", "Chat_Tell", "Channel_Group", "Chat_NPC", "Chat_Tips", "Channel_Local", "Channel_Guild", null, null, null, null, "Channel_Private", null, null, null, "Chat_Errors_IC", null, null, null, "Chat_Warnings", "Chat_Attention", null];
    private static readonly string[] PublicChannels = ["General", "Out of Context", "Market", "New Players", "Terran", "Jenquai", "Progen", "Explorers", "Warriors", "Tradesmen", "Sentinels", "Defenders", "Enforcers"];
    // Current client.exe 00B7C398: menu labels are distinct from personality animation tags.
    private static readonly string[] AvatarEmotes = ["Agree", "Bored", "Bow", "Celebrate", "Chuckle", "Clap", "Crash", "Crazy", "Give", "No", "No Way!", "Point", "Rude", "Shocked", "Shoot", "Shrug", "Sick", "Sleep", "Smash", "Take", "Threaten", "Thumbs", "Wave", "Mime", "Disco", "Robot", "Drunk", "Drink", "Sneak", "Salute"];
    private static readonly EnbUiRaster BackgroundPixel = Pixel(0, 0, 0, 95), EdgePixel = Pixel(24, 69, 90, 210), ButtonPixel = Pixel(37, 39, 84, 240);
    private readonly EnbMessageLineUi _messageLines;
    private readonly EnbChatWrappedLabels _wrappedLabels;
    internal Func<long> MessageClock { get => _messageLines.MessageClock; set => _messageLines.MessageClock = value; }
    internal Func<int, int> EphemeralDraw { get => _messageLines.EphemeralDraw; set => _messageLines.EphemeralDraw = value; }
    private readonly Func<uint?> _session;
    private EnbRetailAssetBundle? _assets;
    private EnbSceneChatSnapshot? _data;
    private IEnbSceneFrame? _scene;
    private Guid _stream;
    private Size _size;
    private double _scale = 1;
    private readonly Dictionary<(string Resource, float Scale, int Spacing, int LineSpacing), EnbRetailFontFace> _fonts = [];
    private readonly Dictionary<string, Net7PlayerNlpRecord> _emotes = new(StringComparer.OrdinalIgnoreCase);
    private byte? _personality;
    private bool _station;
    private readonly Dictionary<string, (EnbAuthoredUiAsset Asset, Rect Bounds)> _art = [];
    private EnbAuthoredUiAsset? _menuLayer;
    private int _menuTab;
    private bool _inGroup, _inGuild, _female;
    private ImmutableList<EnbUiModelDraw>.Builder _draws = ImmutableList.CreateBuilder<EnbUiModelDraw>();
    public ImmutableList<EnbUiModelDraw> ArtPresentation { get; private set; } = [];
    public event Action<ImmutableList<EnbUiModelDraw>>? ArtChanged;
    private readonly Dictionary<int, EnbRetailText> _text = [];
    private readonly EnbRetailChatHistory[] _paneHistory = [new(), new()];
    private readonly (EnbRetailFontFace? Font, double Width)[] _paneWrapping = new (EnbRetailFontFace?, double)[2];
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    public ImmutableArray<EnbRgb> SpeakerColors { get; private set; } = EnbRetailChatColors.Defaults;
    private readonly Dictionary<string, EnbChatHit> _hits = [];
    private readonly EnbChatInputHistory _history = new();
    private readonly HashSet<int> _hiddenTypes = [];
    private readonly int[] _offset = new int[2];
    private readonly int[] _visibleLines = new int[2];
    private readonly int[] _shownLines = [-1, -1];
    private readonly int[] _paneRowCounts = new int[2];
    private readonly long[] _paneLastSequence = new long[2];
    private int _publishedPaneMode = -1;
    private readonly string[] _quickMessages = new string[9];
    private string? _selectedPeer;
    private int _textIndex, _mode = 1, _menuOffset;
    private long _eventCount;
    private string _destination = "Broadcast", _target = "", _menu = "", _hover = "";
    private readonly Dictionary<string, string> _targets = [];
    private bool _hidden, _enabled, _sending;
    private readonly EnbRetailTextEditor _chatInput = new(EnbChatInputHistory.InputLimit);
    private readonly EnbRetailTextEditor _joinInput = new(4) { CharacterClass = EnbRetailEditorCharacterClass.Digits };
    private readonly EnbRetailTextEditor _ignoreInput = new(20) { CharacterClass = EnbRetailEditorCharacterClass.Letters };
    private readonly EnbRetailTextEditor _passwordInput = new(8), _verifyInput = new(8);
    private static bool EditorOverwrite(EnbUiTextInput editor) => !editor.Editor.InsertMode;
    private bool _logging;
    private bool _logStarted;
    public bool PersistenceEnabled { get; set; } = true;
    private readonly Dictionary<int, EnbRetailChatLayout> _layouts = [];
    private long _loggedSequence;
    private double _height = 100, _split = 60, _previousHeight = 100, _previousSplit = 60;
    private Rect _bounds;
    private string? _drag;
    private Point _last;
    private string? _attention;
    private string? _settingsPath;
    private readonly long[] _anchor = new long[2];
    private readonly int[] _anchorWrappedRow = new int[2];
    private int _inputStart;
    private long _attentionUntil;
    private readonly EnbUiTextInput _editor;
    private ImmutableArray<EnbUiTextDraw>.Builder _images = ImmutableArray.CreateBuilder<EnbUiTextDraw>();
    public event Action<ImmutableArray<EnbUiTextDraw>>? Changed;
    public event Action? InputOpened;
    public event Action? PanesResized;
    public event Action? PrivateMessageReceived;
    public bool HasSplitChat => _mode != 0;
    private long _fontSettingsRevision = -1;
    public event Action<string>? GuildSoundRequested;
    public event Action<string>? InteractionSoundRequested;
    public Func<Task>? GuildInfoRequested { get; set; }
    public Func<Task>? FriendsListRequested { get; set; }
    public Func<Task>? GroupFinderRequested { get; set; }
    public Func<Net7ChatInputRequest, Task<bool>>? SubmitRequested { get; set; }
    public Action<Guid, bool, bool, string>? ChatListWindowChanged { get; set; }
    private (Guid Stream, bool Ignore, bool Members, string Channel) _reportedChatListOwner;
    private void ReportChatListOwner(bool dispose = false)
    {
        var next = (_stream, !dispose && _chatDialog == ChatDialog.Ignore,
            !dispose && _chatDialog == ChatDialog.PrivateMembers, !dispose && _chatDialog == ChatDialog.PrivateMembers ? _privateChannel : "");
        if (_reportedChatListOwner == next) return;
        _reportedChatListOwner = next;
        ChatListWindowChanged?.Invoke(next.Item1, next.Item2, next.Item3, next.Item4);
    }
    public Action<uint, byte, string>? LocalChatMessageRequested { get; set; }
    public Action<uint, byte, string>? LocalChatPreviewRequested { get; set; }
    public Action<uint, ImmutableArray<EnbRgb>>? ChatColorsChanged { get; set; }
    public Func<string, bool, Task<bool>>? ChannelMonitoringRequested { get; set; }
    public Func<Net7ClientChatRequestKind, string?, string?, Task<bool>>? PrivateChannelRequested { get; set; }
    public Func<Net7ClientChatRequestKind, string, string, Task<bool>>? PrivateMemberRequested { get; set; }
    public Func<Net7ClientChatRequestKind, string, Task<bool>>? IgnorePlayerRequested { get; set; }
    public Action<uint, int>? QuickMessageSaved { get; set; }
    public Func<IEnbSceneFrame?>? SceneAccessor { get; set; }
    public Func<Vector3?>? StationPositionAccessor { get; set; }
    public Action<string>? InformationRequested { get; set; }
    public Action<string>? OptionsRequested { get; set; }
    internal static readonly string[] OptionsMenuCategories = ["Camera", "Control", "Interface", "Chat Font", "Sound", "Credits",
        "Graphics", "Advanced Graphics", "Social", "Navigation", "Quality of Life", "Modern UI", "Labels"];
    public bool Editing => _editor.IsVisible;
    public string? Error { get; private set; }
    public void ShowError(string message) { Error = message; Publish(); }
    public ImmutableArray<EnbUiTextDraw> Presentation { get; private set; } = [];
    public EnbChatUi(Func<uint?> session)
    {
        _session = session;
        _editor = new(_chatInput) { IsVisible = false };
        _passwordVerifyEditor = new(_verifyInput) { IsVisible = false, PasswordChar = '*' };
        _editor.FocusRequested = () => FocusInput(_editor);
        _passwordVerifyEditor.FocusRequested = () => FocusInput(_passwordVerifyEditor);
        _messageLines = new(Font);
        _wrappedLabels = new(r => DialogScreen(new(r.X, r.Y, r.Width, r.Height)),
            (text, r, color, font, opacity, clip) => Text(text, new(r.X, r.Y, r.Width, r.Height), color, font,
                opacity: opacity, clip: new Rect(clip.X, clip.Y, clip.Width, clip.Height)), () => CommandsClock());



        _editor.Changed += (_, _) =>
        {
            if (_ignoreEntry)
            {
                string normalized = RetailStringEntry.NormalizeName(_editor.Text);
                if (_editor.Text != normalized)
                {
                    int caret = _editor.CaretIndex;
                    _editor.Text = normalized; _editor.CaretIndex = caret;
                }
            }
            Publish();
        };
        _editor.GotFocus += (_, _) => Publish();
        _editor.KeyDown += EditorKey;
        _editor.LostFocus += (_, _) => { if (Editing && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry) CloseInput(); else Publish(); };
        _passwordVerifyEditor.Changed += (_, _) => Publish();
        _passwordVerifyEditor.GotFocus += (_, _) => Publish();
        _passwordVerifyEditor.LostFocus += (_, _) => Publish();
        _passwordVerifyEditor.KeyDown += EditorKey;
    }

    /// <summary>The retail chat's own hits: game UI that covers the Next panels (the Modern UI chat panel is one of those panels).</summary>
    public bool ContainsGameUi(Point point) => _enabled && _hits.Values.Any(hit => hit.Published && hit.Group != EnbUiGroup.ModernChat && hit.Bounds.Contains(UiLayout?.Inverse(hit.Group,point)??point));
    public bool Contains(Point point) => _enabled && _hits.Values.Any(hit => hit.Published && hit.Bounds.Contains(UiLayout?.Inverse(hit.Group,point)??point));
    // History text is not a station interaction widget. Keep actual chat
    // controls (including menus, editor, scrollbars and resize handles) blocking.
    public bool ContainsControl(Point point) => _enabled && _hits.Any(r =>
        r.Value.Published && !r.Key.StartsWith("pane", StringComparison.Ordinal) && !r.Key.StartsWith("mchat/history", StringComparison.Ordinal) && r.Value.Bounds.Contains(UiLayout?.Inverse(r.Value.Group,point)??point));
    public void DismissMenuOutside(Point point)
    {
        point=UiLayout?.Inverse(EnbUiGroup.Chat,point)??point;
        if (_menu.Length > 0 && (!TryRegion("menu", out var r) || !r.Contains(point)) && !_hits.Any(p => p.Value.Published && p.Key.StartsWith("tab/", StringComparison.Ordinal) && p.Value.Bounds.Contains(point)))
        {
            if(_comboAsset is not null)
            {
                // 00579DC0 only examines outside clicks in fully open state3.
                if(_comboState==3){_comboState=2;_comboTick=ComboClock();Publish();}
            }
            else {PlayCommandMenuOpen();_menu="";Publish();}
        }
        if (_replyPopup && !_hits.Any(p => p.Value.Published && p.Key.StartsWith("replypopup/", StringComparison.Ordinal) && p.Value.Bounds.Contains(point)))
        { _replyPopup = false; Publish(); }
    }
    private bool _edgeRailHidden;
    public void Update(IEnbSceneFrame scene, EnbRetailAssetBundle? assets, Size size, double scale, bool enabled)
    {
        _scene = scene;
        _messageLines.SetViewport(new(size.Width, size.Height), scale);
        var next = scene.Hud.Chat;
        bool destinationMembershipChanged = _data?.PrivateChannel != next?.PrivateChannel;
        bool changed = !ReferenceEquals(_data, next) || size != _size || scale != _scale || enabled != _enabled;
        if (!ReferenceEquals(_assets, assets))
        {
            DisposeFonts(); _assets = assets; LoadArt(); LoadOptions(); _messageLines.Load(_assets, _art.GetValueOrDefault("resize").Asset); LoadPing(); changed = true;
            LoadCommands();
        }
        if (_fontSettingsRevision != (assets?.SettingsRevision ?? 0)) { _fontSettingsRevision = assets?.SettingsRevision ?? 0; DisposeFonts(); changed = true; }
        // The Modern panel's place follows the gutter rail option (off: it moves to the screen edge).
        if (_edgeRailHidden != EnbModernHud.EdgeRailHidden) { _edgeRailHidden = EnbModernHud.EdgeRailHidden; changed = true; }
        if (_stream != (scene.Hud.Chat?.StreamId ?? Guid.Empty))
        {
            _stream = scene.Hud.Chat?.StreamId ?? Guid.Empty; _eventCount = 0;
            _privateJoinChannel = ""; _privateRequestPending = false; ++_privateRequestGeneration;
            _messageLines.Queue.Clear();
            Array.Fill(_shownLines,-1); Array.Clear(_paneRowCounts); Array.Clear(_paneLastSequence);
            if (IsChatList) CloseChatDialog();
            _pingVisible = _pingDetails = false;
            _offset[0] = _offset[1] = 0; _anchor[0] = _anchor[1] = 0; _loggedSequence = 0; _history.Clear(); _targets.Clear(); _destination = "Broadcast"; _target = ""; ResetPaneHistory(); CloseInput(); LoadOptions(); changed = true;
        }
        if (next is not null && _data is not null && !next.Channels.SequenceEqual(_data.Channels))
        {
            if (_privateChannel.Length > 0 && !next.Channels.Contains(_privateChannel, StringComparer.OrdinalIgnoreCase))
            {
                if (_privateJoinChannel == _privateChannel) _privateJoinChannel = "";
                _privateChannel = ""; _privateMemberSelection = null; _privateMemberSelectionIndex = -1;
            }
        }
        if (_chatDialog == ChatDialog.Ignore && next is not null && _data is not null &&
            (next.IgnoredRevision != _data.IgnoredRevision || !next.Ignored.SequenceEqual(_data.Ignored)))
        {
            _ignoreReady = true; _ignoreSelection = null; _ignoreSelectionIndex = -1;
            double refreshedScroll = Math.Clamp(_ignoreScroll, 0, Math.Max(0, next.Ignored.Length - 7));
            if (Math.Round(refreshedScroll) != Math.Round(_ignoreScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
            _ignoreScroll = refreshedScroll;
        }
        if (_chatDialog == ChatDialog.PrivateMembers && next is not null && _data is not null &&
            (next.ChannelMemberRevisions.GetValueOrDefault(_privateChannel) != _data.ChannelMemberRevisions.GetValueOrDefault(_privateChannel) ||
             !next.ChannelMembers.GetValueOrDefault(_privateChannel, []).SequenceEqual(_data.ChannelMembers.GetValueOrDefault(_privateChannel, []))))
        {
            _privateMembersReady = true;
            _privateMemberSelection = null; _privateMemberSelectionIndex = -1;
            double refreshedScroll = Math.Clamp(_privateMembersScroll, 0, Math.Max(0, next.ChannelMembers.GetValueOrDefault(_privateChannel, []).Length - 7));
            if (Math.Round(refreshedScroll) != Math.Round(_privateMembersScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
            _privateMembersScroll = refreshedScroll;
            _privateMembersPage = (int)_privateMembersScroll;
        }
        _size = size; _scale = scale; _enabled = enabled && assets is not null; _data = next;
        if (_awaitingPrivateCreate && next is { LastCreatedChannel: { Length: > 0 } } && next.CreatedChannelRevision > _privateCreateRevision)
        {
            // 00566AF0 mode 8 constructs a new 005E9410 owner (state 3).
            // Its font effects must not inherit the previous choice dialog.
            _dialogAlpha.Clear(); _dialogAlphaActive = false;
            _awaitingPrivateCreate = false;
            _privateChannel = next.LastCreatedChannel;
            _privateJoinChannel = next.LastCreatedChannel;
            _chatDialog = ChatDialog.Private;
            _privatePanelState = 3;
            changed = true;
        }
        _station = scene.ActiveStarbaseId is not null;
        bool inGroup = scene.Hud.GroupMemberIds.Length > 0;
        destinationMembershipChanged |= _inGroup != inGroup;
        _inGroup = inGroup;
        var localShip = scene.Objects.FirstOrDefault(o => o.IsLocalShip);
        var localAvatar = localShip?.Avatar;
        _female = localAvatar?.Gender == 1;
        if (localShip is not null) _localName = EnbRetailObjectDisplayName.Recipient(localShip);
        bool inGuild = !string.IsNullOrEmpty(scene.Objects.FirstOrDefault(o => o.IsLocalShip)?.PlayerPresentation?.GuildName);
        destinationMembershipChanged |= _inGuild != inGuild;
        _inGuild = inGuild;
        if (_reconcileDefault || destinationMembershipChanged)
        {
            _reconcileDefault = false;
            if (ReconcileDefaultDestination()) SaveSettings();
            changed = true;
        }
        var selectedObject = scene.Objects.FirstOrDefault(o => o.GameId == scene.Hud.LocalSelectedTargetId);
        _selectedPeer = EnbRetailObjectDisplayName.TargetMessageRecipient(selectedObject);
        // 005D9730/005DAB50 admit only a player ship outside avatar mode;
        // 006FF4B0 resolves the player's Owner before Name, then Unknown.
        string? ignoreTarget = !_station && selectedObject is { ObjectType: 1 }
            ? EnbRetailObjectDisplayName.Recipient(selectedObject) : null;
        changed |= _ignoreTarget != ignoreTarget;
        _ignoreTarget = ignoreTarget;
        var personality = scene.Objects.FirstOrDefault(o => o.IsLocalShip)?.Avatar?.Personality;
        if (_personality != personality && _assets is not null)
        {
            _personality = personality; _emotes.Clear();
            if (personality is { } id)
            {
                string path = Path.Combine(_assets.Paths.ClientDataDirectory, "personalities", $"Personality{id:00}.ini");
                if (EnbNext.Installation.EnbPathCase.FileExists(path)) foreach (var section in EnbRetailIniDocument.Load(path).EnumerateSections())
                {
                    if (section.Value.TryGetValue("Tag", out var tag) && section.Value.TryGetValue("AnimNumber", out var animation) && byte.TryParse(animation, out var number)) _emotes[tag] = new(2, number);
                    if (section.Value.TryGetValue("MoodName", out var mood) && section.Value.TryGetValue("MoodNumber", out var index) && byte.TryParse(index, out var moodNumber)) _emotes[mood] = new(0, moodNumber);
                }
            }
        }
        if (!_enabled && Editing) CloseInput();
        if ((!_enabled || scene.IsTransitioning) && IsChatList) CloseChatDialog();
        if (_data is { } data)
        {
            // Modern UI keeps its own history and, when chosen, its own chat logs instead of the retail chat.log.
            ModernAbsorb(data);
            if (_modernChat) _loggedSequence = data.Lines.LastOrDefault()?.Sequence ?? _loggedSequence;
            else if (_logging) AppendLog(data);
            foreach (var e in data.Events.Where(e => e.Sequence > _eventCount))
            {
                if (e.Kind == Net7ChatPresentationKind.ClearScreenLines) { _messageLines.Queue.ClearPending(); changed = true; }
                if (e.Kind == Net7ChatPresentationKind.NamedScreenLine) _messageLines.EnqueueNamed(e.Text);
                if (e.ScreenLine is {} line)
                    _messageLines.Enqueue(line);
                if (e.Kind is Net7ChatPresentationKind.MessageLine or Net7ChatPresentationKind.QuickLine)
                { _attention = e.Text; _attentionUntil = Environment.TickCount64 + 3000; }
                if (e.Kind == Net7ChatPresentationKind.PrivateBeep && !_hiddenTypes.Contains(3)) PrivateMessageReceived?.Invoke();
                if (e.Kind == Net7ChatPresentationKind.SelectSecureRecipient)
                { _destination="Secure"; _target=e.Text; _targets[_destination]=_target; changed=true; }
                if (e.Kind == Net7ChatPresentationKind.Error && !_hiddenTypes.Contains(19)) InteractionSoundRequested?.Invoke(e.Text);
                if (e.Kind == Net7ChatPresentationKind.GuildSound) GuildSoundRequested?.Invoke(e.Text);
                if (e.Kind == Net7ChatPresentationKind.InterfaceSound) InteractionSoundRequested?.Invoke(e.Text);
                if (e.Kind == Net7ChatPresentationKind.PrivateChannelRequestCompleted) { _privateRequestPending = false; ++_privateRequestGeneration; }
                if (e.Kind == Net7ChatPresentationKind.PrivateChannelNotFound) { _privateJoinChannel = ""; OpenPrivateJoinWithStatus(true); changed = true; }
                if (e.Kind == Net7ChatPresentationKind.PrivateChannelJoinEntry) { _privateJoinChannel = ""; OpenPrivateJoinWithStatus(false); changed = true; }
                if (e.Kind == Net7ChatPresentationKind.PrivateChannelPasswordRequired) { OpenPrivatePasswordMode(0); changed = true; }
                if (e.Kind == Net7ChatPresentationKind.PrivateChannelPasswordIncorrect) { OpenPrivatePasswordMode(1); changed = true; }
            }
            _eventCount = data.Events.LastOrDefault()?.Sequence ?? 0;
        }
        if (_attention is not null && Environment.TickCount64 >= _attentionUntil) { _attention = null; changed = true; }
        changed |= _messageLines.Advance();
        changed |= AdvanceChatList();
        changed |= AdvanceHistorySlider();
        changed |= AdvanceCombo();
        changed |= AdvanceModernFade();
        changed |= AdvanceModernPlace();
        changed |= AdvanceIncomingMessage(next?.IncomingMessage);
        if (changed || (_chatDialog != ChatDialog.None && _dialogAlphaActive) || (_chatDialog == ChatDialog.Ignore && _ignoreOpeningActive) || _chatDialog == ChatDialog.Commands || _hover.StartsWith("chatdialog/", StringComparison.Ordinal)) Publish();
        UpdatePingTooltip();
    }
    private void LoadOptions()
    {
        _options.Clear(); _hiddenTypes.Clear(); _settingsPath = null; _logging = true; _mode = 1; Array.Clear(_quickMessages);
        SpeakerColors = EnbRetailChatColors.Defaults; _colorsChanged=false;
        if (_assets is null) return;
        var session = _session();
        string? path = session is {} character ? Path.Combine(_assets.Paths.ClientOutputDirectory, $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(character)}_options.ini") : null;
        if (EnbNext.Installation.EnbPathCase.FileExists(path))
            foreach (var section in EnbRetailIniDocument.Load(path).EnumerateSections())
                if (section.Value.TryGetValue("OptionName", out var name) && section.Value.TryGetValue("OptionValue", out var value)) _options[name] = value;
        SpeakerColors = EnbRetailChatColors.Load(_options);
        if(session is {} paletteOwner)ChatColorsChanged?.Invoke(paletteOwner,SpeakerColors);
        for (int type = 0; type < 24; type++) if (FilterNames[type] is { } filter && _options.GetValueOrDefault(filter) == "no") _hiddenTypes.Add(type);
        _mode = (int)Number("DefaultChatWindow", 3) switch { 1 => 0, 2 => 2, _ => 1 };
        _layouts.Clear(); _layoutPoses.Clear();
        for (int mode = 0; mode < 3; mode++) _layouts[mode] = EnbRetailChatLayout.Load(_assets, mode, 10);
        LoadLayoutSize();
        _defaultDestination = (int)Number("Chat_Default_Channel", 1);
        _defaultLabel = _options.GetValueOrDefault("Channel_Label", "Local");
        _reconcileDefault = true;
        _settingsPath = PersistenceEnabled ? path : null;
        if (session is {} quickMessageOwner)
            Array.Copy(EnbRetailQuickMessages.Load(Path.Combine(_assets.Paths.ClientOutputDirectory, "chat.ini"), quickMessageOwner), _quickMessages, 9);
    }
    private void LoadLayoutSize()
    {
        if (!_layouts.TryGetValue(_mode, out var layout)) return;
        _height = layout.Height((int)Number(layout.Prefix + "Size", 73));
        _split = layout.Split((int)Number(layout.Prefix + "Split", _mode == 2 ? 180 : 30), _mode == 2);
    }
    private void AppendLog(EnbSceneChatSnapshot data)
    {
        var lines = data.Lines.Where(l => l.Sequence > _loggedSequence).ToArray();
        if (lines.Length == 0 || _settingsPath is null || _assets is null) return;
        try
        {
            // 00563760 opens the literal chat.log with the native file class,
            // relative to retail's release directory, then seeks to the end.
            string path = Path.Combine(Path.GetDirectoryName(_assets.Paths.ClientExecutablePath)!, "chat.log");
            DateTime now = DateTime.Now;
            string text = _logStarted ? "" : "\r\n\r\nLog Started at: " + now.ToString("ddd MMM ", CultureInfo.InvariantCulture) + $"{now.Day,2}" + now.ToString(" HH:mm:ss yyyy", CultureInfo.InvariantCulture) + "\n\r\n";
            text += string.Concat(lines.Select(l => string.Create(CultureInfo.InvariantCulture, $"  ({l.Type}):{now.Hour,2}:{now.Minute,2}:{now.Second,2}  {l.Text}\r\n")));
            // 00563760 retains the native mode-3 handle; 006841D0 opens
            // OPEN_ALWAYS, GENERIC_READ|WRITE, share mode zero.
            if (_chatLog is null)
            {
                // Retail's process-wide chatLogNeedsOpening is cleared even
                // when opening fails: do not retry through settings reloads.
                if (_chatLogAttempted) return;
                _chatLogAttempted = true;
                _chatLog = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                _chatLog.Seek(0, SeekOrigin.End);
            }
            _chatLog.Write(System.Text.Encoding.Latin1.GetBytes(text));
            _chatLog.Flush();
            _logStarted = true;
            _loggedSequence = lines[^1].Sequence;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { _logging = false; _chatLog?.Dispose(); _chatLog = null; }
    }
    private bool SaveSettings()
    {
        if (_settingsPath is null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var values = new List<(string Kind, string Name, string Value)>();
            void Integer(string name, int value) => values.Add(("INT", name, value.ToString(CultureInfo.InvariantCulture)));
            Integer("DefaultChatWindow", _mode == 0 ? 1 : _mode == 2 ? 2 : 3);
            if (_layouts.TryGetValue(_mode, out var layout))
            {
                Integer(layout.Prefix + "Size", layout.HeightFrame(_height));
                Integer(layout.Prefix + "Split", _mode == 0 ? 0 : layout.SplitFrame(_split, _mode == 2));
            }
            Integer("Chat_Default_Channel", _defaultDestination);
            values.Add(("STRING", "Channel_Label", _defaultLabel));
            values.Add(("BOOL", "Show_Self_Name", ShowSelfName ? "yes" : "no"));
            if (_colorsChanged)
                for (int type=0;type<ColorNames.Count;type++)
                {
                    var color=SpeakerColors[type];
                    values.Add(("VECTOR3", "Color_"+ColorNames[type], string.Create(CultureInfo.InvariantCulture,$"{color.R:F6},{color.G:F6},{color.B:F6}")));
                }
            for (int type = 0; type < FilterNames.Length; type++)
                if (FilterNames[type] is { } name) values.Add(("BOOL", name, _hiddenTypes.Contains(type) ? "no" : "yes"));
            foreach (var channel in NamedChannelOptions)
                values.Add(("BOOL", channel.Option, _options.GetValueOrDefault(channel.Option,
                    channel.Option == "Channel_Newbie" ? "yes" : "no")));
            string existing = EnbNext.Installation.EnbPathCase.FileExists(_settingsPath) ? File.ReadAllText(EnbNext.Installation.EnbPathCase.Resolve(_settingsPath)) : "";
            File.WriteAllText(_settingsPath + ".tmp", EnbRetailCinematicOptionsWriter.UpdateValues(existing, values.ToArray()));
            File.Move(_settingsPath + ".tmp", EnbNext.Installation.EnbPathCase.Resolve(_settingsPath), true);
            foreach (var value in values) _options[value.Name] = value.Value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = "Could not save chat preferences: " + ex.Message; Publish(); return false; }
    }
    private void LoadArt()
    {
        _art.Clear(); LoadChatDialogArt();
        _layoutShells.Clear();_layoutShellParts.Clear();_layoutShellKey=null;
        if (_assets is null) return;
        foreach (var (key, file, meshName) in new[] { ("button", "tsi04a.w3d", "BUTTON02"), ("up", "slider_up.w3d", "CLICK_UP"), ("down", "slider_dn.w3d", "CLICK_DOWN"), ("mode", "tsi05b.w3d", "GIZMO_EXPAND"), ("resize", "tsi05d.w3d", "GIZMO_HORIZ01"), ("split", "tsi05d.w3d", "GIZMO_HORIZ01"), ("vertical", "tsi05d.w3d", "GIZMO_HORIZ") })
        {
            var asset = EnbAuthoredUiAsset.Load(_assets, file);
            var mesh = asset?.Resource.Meshes.FirstOrDefault(m => m.Name == meshName);
            if (asset is null || mesh is null) continue;
            _art[key] = (asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { mesh }) }, EnbCockpitUi.MeshBounds(asset, meshName).Translate(-320, -240));
        }
        // Retail 0056C180 replaces GIZMO_EXPAND's texture using 00B7C2E0.
        if (_art.TryGetValue("mode", out var modeArt))
            for (int mode = 0; mode < 3; mode++)
            {
                // Local modes are single/horizontal/vertical; the icon offers the next layout.
                string texture = mode switch { 0 => "if_chat.tga", 1 => "if_chat03.tga", _ => "if_chat02.tga" };
                EnbNext.Rendering.EnbW3dRenderTextureBinding Replace(EnbNext.Rendering.EnbW3dRenderTextureBinding t) => t with
                { LogicalName = texture, Location = null, ResolveLogicalNameAtRuntime = true };
                _art["mode" + mode] = (modeArt.Asset with { Resource = modeArt.Asset.Resource.Copy().SetMeshes(modeArt.Asset.Resource.Meshes.Select(m => (m).Copy().SetMaterialPasses(m.MaterialPasses.Select(p => (p).Copy().SetTextureStages(p.TextureStages.Select(s => (s).Copy().SetTextures(s.Textures.Select(Replace).ToArray())).ToArray()).SetTriangleBindings(p.TriangleBindings.Select(t => t with { Textures = t.Textures.Select(b => b is null ? null : Replace(b)).ToArray() }).ToArray())).ToArray())).ToArray()) }, modeArt.Bounds);
            }
        _menuLayer = _art.TryGetValue("button", out var carrier) ? carrier.Asset with { Resource = carrier.Asset.Resource.Copy().SetMeshes([]) } : null;
        foreach (int mode in Enumerable.Range(0,3))
        {
            var asset = EnbAuthoredUiAsset.Load(_assets, (mode switch {0=>"tsi05f",1=>"tsi05e",_=>"tsi05d"})+".w3d");
            if(asset is null)continue;
            _layoutShells[mode]=asset;
            foreach (string name in new[] { "DEST_BOX", "TEXTBOX" })
            if(asset.Resource.Meshes.FirstOrDefault(m=>m.Name==name) is {} mesh)
                _art[name+mode] = (asset with { Resource = asset.Resource.Copy().SetMeshes(new[] { mesh }) },
                    EnbCockpitUi.MeshBounds(asset, name).Translate(-320, -240));
        }
        _chatInputLayout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fc_chat.ini"));
        LoadChatCombo();
        LoadIncomingMessage();
    }
    private double Number(string name, double fallback) => double.TryParse(_options.GetValueOrDefault(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    private EnbRgb Color(byte type) => type<SpeakerColors.Length?SpeakerColors[type]:new(1,1,1);
    // Presentation indices are reversed from retail 0067D660: game=0,
    // social=1 here, whereas native social=0 and game/status=1 (with two panes).
    private static int Pane(byte type) => type is 0 or 1 or 5 or 6 or 17 or 18 or 19 or 20 or 21 or 22 or 23 ? 0 : 1;
    public void OpenInput(string? destination = null, string? target = null, string text = "")
        => OpenInputCore(destination, target, text, false);

    //5658B0 selects the peer before744E70 ->570F50 ->65E7C0. The latter
    //opens/clears only a closed editor; an existing draft and caret survive.
    public void OpenSecureRecipient(string target) => SelectInputDestination("Secure", target);

    private void SelectInputDestination(string destination, string? target = null)
        => OpenInputCore(destination, target, "", Editing && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry);

    private void OpenInputCore(string? destination, string? target, string text, bool preserveDraft)
    {
        if (!_enabled) return;
        if (destination is null && !ApplyDefaultToInput()) return;
        if (destination == "Reply")
        {
            // 005646A0 snapshots HUD+1D8 into +248; arrivals at 00565D80
            // update only the reply history. 0065CCD0 sends the retained +248.
            target ??= _data?.ReplyIdentity ?? "";
            _destination = "Reply"; _target = target;
            if (target.Length == 0) { DestinationMessage(17, "No one to reply to."); return; }
        }
        _hidden = false; _menu = ""; _replyPopup = false; _ignoreEntry = false; Error = null;
        if (destination is not null) _destination = destination;
        if (target is not null) _targets[_destination] = target;
        _target = _targets.GetValueOrDefault(_destination, "");
        if (preserveDraft) { _editor.Focus(); Publish(); return; }
        _inputStart = 0;
        _editor.Use(_chatInput); _editor.PasswordChar = '\0';
        _editor.IsVisible = true; _editor.Text = text[..Math.Min(text.Length, EnbChatInputHistory.InputLimit)];
        _editor.Focus(); _editor.CaretIndex = text.Length; InputOpened?.Invoke(); Publish();
    }
    public void CloseInput() { _editor.IsVisible = false; _editor.Text = ""; _ignoreEntry = false; _modernRename = false; Publish(); }
    public bool HandleKey(EnbUiInputEvent e, string? action)
    {
        if (action is "Target Position" or "Player Position")
        {
            if (!_enabled || _sending) return true;
            var scene = SceneAccessor is { } current ? current() : _scene;
            if (scene is null) return true;
            string? text;
            if (action == "Player Position")
            {
                Vector3? position = scene.ActiveStarbaseId is not null
                    ? StationPositionAccessor?.Invoke()
                    : scene.Objects.FirstOrDefault(o => o.GameId == scene.LocalShipObjectId)?.Spatial?.Transform.Position;
                text = position is { } p ? "Position " + ChatCoordinates(p) : null;
            }
            else
            {
                var target = scene.Objects.FirstOrDefault(o => o.GameId == scene.Hud.LocalSelectedTargetId);
                text = target is null ? "No target." : target.Spatial?.Transform is { } spatial
                    ? (string.IsNullOrEmpty(target.Name) ? "Target at " : $"Target '{target.Name}' at ") + ChatCoordinates(spatial.Position)
                    : null;
            }
            if (text is null) return true;
            // Retail 0065DE90 / 0065E5E0: insert while editing; otherwise
            // submit through the selected chat destination, like a quick message.
            if (Editing)
            {
                //0084B020 bulk insertion uses the retained engine buffer mode.
                _editor.InsertBulk(text);
            }
            else { OpenInput(_destination, _target, text); EditorKey(null, new EnbUiInputEvent(0x0d, 0, 0, 0)); }
            return true;
        }
        if (action is not null && (action.StartsWith("Save Quick Message ", StringComparison.Ordinal) || action.StartsWith("Send Quick Message ", StringComparison.Ordinal)) && int.TryParse(action.AsSpan(action.Length - 1), out int slot) && slot is >= 1 and <= 9)
        {
            if (!_enabled || _sending) return true;
            bool save = action.StartsWith("Save", StringComparison.Ordinal);
            if (_settingsPath is not null && _assets is not null && _session() is { } owner)
            {
                try { Array.Copy(EnbRetailQuickMessages.Load(Path.Combine(_assets.Paths.ClientOutputDirectory, "chat.ini"), owner), _quickMessages, 9); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error = ex.Message; Publish(); return true; }
            }
            if (save && Editing)
            {
                string? previous = _quickMessages[slot - 1];
                _quickMessages[slot - 1] = _editor.Text ?? "";
                // A disabled persistence path is used only by offline input probes.
                if (_settingsPath is null) return true;
                try
                {
                    if (_assets is null || _session() is not { } character) return true;
                    EnbRetailQuickMessages.Save(Path.Combine(_assets.Paths.ClientOutputDirectory, "chat.ini"), character, slot, _quickMessages[slot - 1]);
                    Error = null; QuickMessageSaved?.Invoke(character, slot);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                { _quickMessages[slot - 1] = previous!; Error = ex.Message; Publish(); }
            }
            else
            {
                string text = _quickMessages[slot - 1] ?? "";
                // 0065DE90 / 0065E3E0: Send inserts into an open draft.
                // Save from closed chat recalls a draft, even for an empty slot.
                if (Editing) _editor.InsertBulk(text);
                else if (save) OpenInput(_destination, _target, text);
                else if (text.Length > 0) { OpenInput(_destination, _target, text); EditorKey(null, new EnbUiInputEvent(0x0d, 0, 0, 0)); }
                else _history.Submit(""); // CHAT-D02: empty submission sends nothing but resets recall.
            }
            return true;
        }
        //65CA10 offers unconsumed editor keys to65DE90. Explicit destination
        //actions update the recipient while preserving an already open buffer.
        //84AAD0 consumes printable unmodified/shifted keys before action lookup.
        //The native host delivers translated text after this bubbling KeyDown.
        bool editorText = Editing && e.Character != '\0' &&
            (e.Modifiers & 0xe00) == 0;
        if (!editorText && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry)
        {
            if (action == "Target Message")
            {
                if (_selectedPeer is { } peer) SelectInputDestination("Secure", peer);
                else SelectInputDestination("Broadcast", "");
                return true;
            }
            string? explicitDestination = action switch
            {
                "Local Message" => "Local", "Sector Message" => "Broadcast", "Group Message" => "Group",
                "Guild Message" => "Guild", "Reply Last Message" => "Reply", "Radio Message" => "Channel", _ => null
            };
            if (explicitDestination is not null)
            { OpenExplicitDestination(explicitDestination, action == "Radio Message"); return true; }
        }
        if (Editing) return false;
        if (action is "Prev Ephemeral History" or "Next Ephemeral History")
        {
            if (_enabled && _scene?.IsTransitioning != true)
            {
                _messageLines.Queue.NavigateHistory(action == "Prev Ephemeral History");
                _messageLines.Advance(); Publish();
            }
            return true;
        }
        if (action == "Toggle Chat Window") { _hidden = !_hidden; Publish(); return true; }
        if (action == "Reply Menu") { OpenReplyList(); return true; }
        if (e.Code == 0x1b && _replyPopup) { _replyPopup = false; Publish(); return true; }
        if (e.Code == 0x1b && _chatDialog != ChatDialog.None) { CloseChatDialog(); return true; }
        if (e.Code == 0x1b && _menu.Length > 0) { _menu = ""; Publish(); return true; }
        string? destination = action switch { "Local Message" => "Local", "Sector Message" => "Broadcast", "Group Message" => "Group", "Guild Message" => "Guild", "Reply Last Message" => "Reply", "Radio Message" => "Channel", _ => null };
        if (destination is not null || action is "Channel Message" or "Command Message")
        {
            if (action == "Command Message") OpenInput(_destination, _target, "/");
            else if (destination is not null) OpenExplicitDestination(destination, action == "Radio Message");
            else OpenInput();
            return true;
        }
        if (action is "Scroll Chat Up" or "Chat Page Up" or "Scroll Chat Down" or "Chat Page Down")
        {
            //570390 targets native pane0: social, or merged in single-pane mode.
            //Next's split-layout presentation index for that owner is1.
            int pane = _mode == 0 ? 0 : 1;
            int amount = action.Contains("Page", StringComparison.Ordinal) ? Math.Max(1, _visibleLines[pane]) : 1;
            Scroll(pane, action.EndsWith("Up", StringComparison.Ordinal) ? amount : -amount);
            return true;
        }
        return false;
    }
    // The retail x87 path multiplies by the authored single-precision .001
    // constant before passing doubles to printf. Keep the trailing separator.
    private static string ChatCoordinates(Vector3 p) => string.Create(CultureInfo.InvariantCulture,
        $"({(double)p.X * .001f:F2}, {(double)p.Y * .001f:F2}, {(double)p.Z * .001f:F2}) ");
    private async Task PerformShipEmote(string command)
    {
        if (!_enabled || _station || _sending || SubmitRequested is null) return;
        _sending = true; _menu = ""; Publish();
        try
        {
            if (!await SubmitRequested(new(command, "Local"))) Error = "Unable to send ship emote.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { Error = ex.Message; }
        finally { _sending = false; Publish(); }
    }
    private async Task PerformStationEmote(string tag)
    {
        if (!_enabled || !_station || _sending || SubmitRequested is null) return;
        if (!_emotes.TryGetValue(tag, out var emote) || emote.RecordType != 2)
        { Error = "Avatar animation is unavailable: " + tag; Publish(); return; }
        _sending = true;
        _menu = ""; Publish();
        try
        {
            // Menu selection bypasses the editor. Retail 0056AFBB builds a
            // finalized PlayerNLP stream and sends 005E; 005F starts the avatar.
            if (!await SubmitRequested(new("/" + tag, "Local") { AvatarRecords = new[] { emote } }))
                Error = "Unable to send avatar emote.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { Error = ex.Message; }
        finally { _sending = false; Publish(); }
    }
    internal async void EditorKey(EnbUiTextInput? sender, EnbUiInputEvent e, Action? handled = null)
    {
        if (e.Code == 0x2d)
        {
            (sender ?? _editor).Editor.Key(0x2d);
            handled?.Invoke(); Publish(); return;
        }
        if (_modernRename)
        {
            if (e.Code is 0x0d or 0x1b) { ModernRenameKey(e.Code); handled?.Invoke(); }
            return;
        }
        if (_privateJoinEntry)
        {
            // 005EADD0/005EB2F0: Escape deactivates the editor and the frame submits its text, as Enter does (CHAT-D22).
            if (e.Code is 0x1b or 0x0d) { await ConfirmPrivateJoinAsync(); handled?.Invoke(); }
            return;
        }
        if (_privatePasswordEntry)
        {
            // 005E63C0/005E6A20: in modes 0/1 Escape submits the typed password, as Enter does (CHAT-D21).
            if (e.Code == 0x1b && _privatePasswordMode is 0 or 1) { await ConfirmPrivatePasswordAsync(); handled?.Invoke(); }
            else if (e.Code == 0x1b) { CloseChatDialog(); handled?.Invoke(); }
            else if (e.Code == 0x0d)
            {
                if (_privatePasswordMode == 2 && ReferenceEquals(sender, _editor)) _passwordVerifyEditor.Focus();
                else await ConfirmPrivatePasswordAsync();
                handled?.Invoke();
            }
            return;
        }
        if (_ignoreEntry)
        {
            // 005D9100/005DA0D0: Escape accepts the entry, as Enter does (CHAT-D18).
            if (e.Code is 0x1b or 0x0d) { await ConfirmIgnoreEntryAsync(); handled?.Invoke(); }
            return;
        }
        switch (e.Code)
        {
            case 0x1b: CloseInput(); break;
            case 0x0d:
                if (_sending) break;
                string value = _editor.Text ?? "";
                if (value.Length == 0) { _history.Submit(value); CloseInput(); break; }
                // 00749A70 without a body invokes 00567540 (Reply List),
                // then 0065CCD0 retires the submitted editor as usual.
                if (value is "/reply" or "/r" or "/reply " or "/r ")
                { _history.Submit(value); CloseInput(); OpenReplyList(); break; }
                var inputStream = _stream;
                _sending = true;
                try
                {
                    var request = new Net7ChatInputRequest(value, _destination, _target);
                    bool sent = SubmitRequested is not null && await SubmitRequested(request);
                    if (sent && inputStream == _stream) { _history.Submit(value); CloseInput(); }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { Error = ex.Message; }
                finally { _sending = false; Publish(); }
                break;
            case 0x26: Recall(true); break;
            case 0x28: Recall(false); break;
            case 0x09:
                CycleInputDestination((e.Modifiers & 0x100) != 0); break;
            default: return;
        }
        handled?.Invoke();
    }
    private void Recall(bool previous)
    {
        if (_history.Recall(previous) is not { } text) return;
        _editor.Text = text; _editor.CaretIndex = text.Length;
    }
    private void Scroll(int pane, int amount)
    {
        int shown = _shownLines[pane] < 0 ? Math.Min(_visibleLines[pane],_paneRowCounts[pane]) : _shownLines[pane];
        var next = EnbRetailChatScroll.Move(_paneRowCounts[pane],_visibleLines[pane],_offset[pane],shown,amount);
        _offset[pane]=next.Offset; _shownLines[pane]=next.Shown; _anchor[pane]=0;
        Publish();
    }

    private void Publish()
    {
        if(_menu.Length==0){_comboMenu="";_comboState=0;_comboOpening=0;_comboShown=false;}
        ReportChatListOwner();
        _images = ImmutableArray.CreateBuilder<EnbUiTextDraw>(); _draws = ImmutableList.CreateBuilder<EnbUiModelDraw>(); _textIndex = 0; foreach (var hit in _hits.Values) hit.Published = false;
        if (!_enabled || _size.Width < 100 || _size.Height < 100) { Finish(); return; }
        double unit = _size.Width / 640, x = _size.Width * .17, y = 8 * unit, w = _size.Width * .62;
        double header = 10 * unit;
        string[] tabs = ["Chat", "Group", "Options", "Help", "Emote"];
        double h = header;
        if (_modernChat) { _bounds = ModernChatArea; if (!_hidden) PublishModernChat(); }
        else
        {
        ResolveLayoutAnchors();
        PublishLayoutShell();
        h = LayoutPoint("CHAT_VP_TL").Y - y;
        _bounds = new(x, y, w, h);
        for (int i = 0; i < tabs.Length; i++)
        {
            string tab = tabs[i]; int tabIndex = i;
            void Toggle() { ToggleChatCombo(tab,tabIndex); }
            if(ModernMenus)continue; // Modern UI: the help button and the player menu offer these entries.
            if(!PublishChatTab(i,tab,Toggle))
                Button("tab/" + tab, tab, new(x + i * w * .145, y, w * .145, header), Toggle);
        }
        PublishIncomingMessage();
        var modeBounds=_art.TryGetValue("mode",out var modeArt)
            ? ComboScreen(modeArt.Bounds.Translate(320,240)) : new Rect(x+w-header,y,header,header);
        Button("splitMode", "+", modeBounds, () => { SaveSettings(); _mode = _mode switch { 0 => 2, 2 => 1, _ => 0 }; _hidden = false; LoadLayoutSize(); SaveSettings(); Publish(); }, right: () => { _hidden = !_hidden; Publish(); });
        if (_mode == 0) PaneView(0, LayoutViewport(0), all: true);
        else
        {
            PaneView(0, LayoutViewport(1));
            PaneView(1, LayoutViewport(0));
        }
        // 00571210 hides the history/frame meshes and pane scrollbars, not
        // these independent authored gizmos or the input-bar hierarchy.
        if(_mode!=0)Handle("split", LayoutControl(_mode == 2 ? "vertical" : "split", LayoutPoint("GIZMO_HORIZ")), _mode == 2 ? "↔" : "↕");
        Handle("resize", LayoutControl("resize", LayoutPoint("GIZMO_HORIZ01")), "↕");
        if (Editing && !_ignoreEntry && !_privateJoinEntry && !_privatePasswordEntry)
        {
            var tl=LayoutPoint("CHAT_VP_TL");var br=LayoutPoint("CHAT_VP_BR");
            var input = new Rect(tl,br);
            Hit("editor", input, () => _editor.Focus());
            _editor.Bounds = input;
            string caption = _destination == "Channel" ? ChatChannelLabel(_target)
                : _destination is "Secure" or "Reply" ? "To " + _target : _destination;
            DrawChatInput(caption, input, unit);
        }
        }
        if (_menu.Length > 0)
        {
            int tabIndex = Array.IndexOf(tabs, _menu);
            if (tabIndex >= 0) _menuTab = tabIndex;
            double menuWidth = (_menu == "Chat" ? 95 : _menu == "Commands" ? 180 : 87) * unit;
            Menu(new(Math.Min(x + _menuTab * w * .145, _size.Width - menuWidth), y + header, menuWidth, _size.Height - y - header - 4 * unit), unit);
        }
        PublishReplyPopup(x, y, unit);
        if (_attention is not null) Text(_attention, new(_size.Width * .35, _size.Height * .08, _size.Width * .6, 25 * unit), new(0, 1, 0), "FONT_SMALL");
        if (_messageLines.Draw(y + (_hidden ? header : h)) is { } messageLine) _images.Add(messageLine);
        PublishPing();
        if (Error is not null) Text(Error, new(x, y + h + header, w, header), new(1, .3f, .3f), "FONT_TINY");
        PublishChatDialog();
        Finish();
    }
    private EnbInterfaceLayoutCatalog? _chatInputLayout;
    private void DrawChatInput(string caption, Rect input, double unit)
    {
        if (!_art.TryGetValue("DEST_BOX"+_mode, out var destination)) return;
        // 0056F9F0 retains the authored meshes and attaches the caption to
        // DEST_BOX's bone. 00570CD0 separately supplies the text viewport.
        var button = LayoutControl("DEST_BOX"+_mode,LayoutPoint("DEST_BOX"));
        if (_art.TryGetValue("TEXTBOX"+_mode, out var textBox))
            _draws.Add(new(textBox.Asset,0,(LayoutControl("TEXTBOX"+_mode,LayoutPoint("TEXTBOX"))),false)
                { LayoutGroup=EnbUiGroup.Chat,DesignBounds=(textBox.Bounds)});
        var captionOrigin=LayoutPoint("DEST_BOX");
        int first = _images.Count;
        if (_chatInputLayout?.TryResolve("Chat_Dest_Bttn", out var style) == true)
            Text(caption + ":", new(captionOrigin.X, button.Y, Math.Max(0, button.Right-captionOrigin.X), button.Height),
                _messageLines.Color(style.Properties.GetValueOrDefault("Color", "FA_YELLOW"), new(1,1,0)),
                style.Properties.GetValueOrDefault("FontType", "FONT_TINY"), centerVertically: true);
        var labels = _images.Skip(first).ToImmutableArray();
        while (_images.Count > first) _images.RemoveAt(_images.Count - 1);
        _draws.Add(new(destination.Asset, 0, (button), false) { LayoutGroup=EnbUiGroup.Chat, DesignBounds = (destination.Bounds), Images = labels });
        Hit("destination", button, () => CycleInputDestination(false), () => CycleInputDestination(false)); // 0056F9F0: right press cycles forward too (CHAT-D29)
        DrawInput(input,centerVertically:true);
    }
    private void CycleInputDestination(bool reverse)
    {
        // 00565120 walks the whole B7C530 table: the four monitored category destinations, then every
        // monitored public channel (00568700 fills General..Enforcers; CHAT-D27), then HUD+214 (the
        // joined private channel, whatever Channel_Private says). 00565390 wraps either way.
        var destinations = new List<(string Destination, string Target)>();
        foreach (string category in new[] { "Broadcast", "Local", "Guild", "Group" })
            if (_options.GetValueOrDefault("Channel_" + category, "yes").Equals("yes", StringComparison.OrdinalIgnoreCase) &&
                (category != "Guild" || _inGuild) && (category != "Group" || _inGroup)) destinations.Add((category, ""));
        foreach (string publicChannel in MonitoredPublicChannels()) destinations.Add(("Channel", publicChannel));
        if (_data?.PrivateChannel is { Length: > 0 } channel) destinations.Add(("Channel", channel));
        if (destinations.Count == 0) return;
        int current = destinations.FindIndex(value => value.Destination == _destination && value.Target == _target);
        if (current < 0) current = 0; // Retail search defaults to the first row when the label is absent.
        int next = (current + (reverse ? -1 : 1) + destinations.Count) % destinations.Count;
        (_destination, _target) = destinations[next];
        if (_target.Length > 0) _targets[_destination] = _target;
        SetDefaultDestination(DestinationKind(_destination, _target), _destination == "Channel" ? ChatChannelLabel(_target) : _destination);
        ReconcileDefaultDestination();
        SaveSettings(); PlayRollSelect(); Publish();
    }
    private void DrawInput(Rect area, EnbUiTextInput? source = null, bool greenCaret = false, string? dialogFontId = null, bool centerVertically = false)
    {
        var editor = source ?? _editor;
        string fontId = dialogFontId ?? (_chatInputLayout?.TryResolve("Chat_Line", out var style) == true
            ? style.Properties.GetValueOrDefault("FontType", "FONT_TINY") : "FONT_TINY");
        var font = Font(fontId);
        if (font is null) return;
        if(centerVertically)
        {
            double height=font.CellHeight*font.Scale;
            area=new(area.X,area.Center.Y-height/2,area.Width,height);
        }
        double Measure(string value) => font.MeasureWidth(value) * font.Scale;
        string text = editor.Text ?? "";
        int caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
        int inputStart = source is null ? Math.Min(_inputStart, caret) : 0;
        int inputEnd=text.Length;
        if(centerVertically)
        {
            double unit=_size.Width/640;
            float width=(int)(_layoutAnchors["CHAT_VP_BR"].X-_layoutAnchors["CHAT_VP_TL"].X);
            var window=EnbRetailChatInputViewport.Resolve(text,caret,width,
                s=>(float)(Measure(s)/unit),c=>(float)(font.CharacterAdvance(c)*font.Scale/unit));
            inputStart=window.Start;inputEnd=window.Start+window.Length;
        }
        else while (inputStart < caret && Measure(text[inputStart..caret]) > area.Width - 8) inputStart++;
        if (source is null) _inputStart = inputStart;
        Text(text[inputStart..inputEnd], area, new(1, 1, 1), fontId);
        if (editor.IsFocused)
        {
            int visibleCaret=Math.Clamp(caret,inputStart,inputEnd);
            double prefix=Measure(text[inputStart..visibleCaret]);
            if(centerVertically)
            {
                // 0065EBF0/006AA340 supplies the insertion flag to 00685AB0.
                // It selects a two-unit underline or a full-height overwrite block.
                double ux=_size.Width/640,uy=_size.Height/480;
                double glyph=font.CharacterAdvance(visibleCaret<inputEnd?text[visibleCaret]:'n')*font.Scale;
                double width=Math.Max(2,(int)(glyph/ux)+1)*ux;
                var bounds=new Rect(area.X+(int)(prefix/ux)*ux,
                    !_chatInput.InsertMode?area.Y:area.Bottom-2*uy,width,!_chatInput.InsertMode?Math.Max(0,area.Height-uy):2*uy);
                _images.Add(new(Pixel(0,178,25,127),(bounds)){Clip=(area)});
            }
            else _images.Add(new(greenCaret ? Pixel(0, 190, 0, 255) : Pixel(255, 255, 255, 255), new(area.X+prefix, area.Y, Math.Max(1, 1 / _scale), area.Height)) { Clip = (area) });
        }
    }
    private void PaneView(int pane, Rect r, bool all = false)
    {
        if(_publishedPaneMode!=_mode)
        { _publishedPaneMode=_mode; Array.Fill(_shownLines,-1); ResetPaneHistory(); }
        if(!_hidden)
        {
            // Keep the prior local placeholder only if optional shell acquisition failed.
            if(!_authoredShell)Box(r, BackgroundPixel);
            Hit("pane" + pane, r, () => { }, wheel: delta => Scroll(pane, delta > 0 ? 1 : -1));
        }
        double unit = _size.Width / 640;
        var textArea = r;
        // Native 0067D660/0067DB40: social pane uses font 00; game messages use 01.
        string fontId = all || pane == 1 ? "FONT_CHAT_LINE_00" : "FONT_CHAT_LINE_01";
        var font = Font(fontId);
        if (font is null) return;
        float lineDesign=(float)((font.CellHeight+font.LineSpacing)*(double)font.Scale*(480/(double)(float)_size.Height));
        double step = Math.Max(1, lineDesign*_size.Height/480);
        int visible = Math.Max(1, (int)(textArea.Height / step));
        var history = _paneHistory[pane];
        // 0067E670 chooses 1..3 prefix spaces from an eight-design-unit
        // allowance. 006804E0 subtracts that prefix, then 006817E0 truncates
        // the resulting wrap width to a multiple of eight (minimum one).
        double designScale=font.Scale*(640/(double)(float)_size.Width);
        double spaceWidth=font.CharacterAdvance(' ')*designScale;
        int prefixSpaces=Math.Clamp(8/Math.Max(1,(int)spaceWidth),1,3);
        string continuationPrefix=new(' ',prefixSpaces);
        double prefixWidth=font.MeasureWidth(continuationPrefix)*designScale;
        int wrapDesign=Math.Max(1,Math.Max(1,(int)((float)(textArea.Width/unit)-prefixWidth))&~7);
        var wrapping = (font, wrapDesign/(double)designScale);
        // 004FAB70 calls the font's 640-reference metric for each glyph;
        // 004F9D90 accumulates those float widths in design coordinates.
        string[] WrapLine(string text) => EnbRetailDialogText.WrapChat(text,wrapDesign,c=>(float)(font.CharacterAdvance(c)*designScale));
        if (_paneWrapping[pane] != wrapping)
        {
            history.Rewrap(WrapLine);
            _paneWrapping[pane] = wrapping;
        }
        int arrived = 0;
        foreach (var line in _data?.Lines ?? [])
            // Retail 0067DB40 does not filter delivered private-channel rows (13).
            // Channel_Private controls the send destination, not pane admission.
            if (line.Sequence > _paneLastSequence[pane] && (line.Type == 13 || !_hiddenTypes.Contains(line.Type)) && (all || Pane(line.Type) == pane))
                arrived += history.Append(line.Text, line.Type, line.Sequence, visible, WrapLine, Color(line.Type));
        var lines = history.Rows.ToList();
        //6817E0 preserves a partial window on resize;6821F0 adds arriving
        //rows to it. Scrolling past the newest row can leave the window empty.
        int oldVisible=_visibleLines[pane];
        int shown=_shownLines[pane]<0 ? Math.Min(visible,lines.Count) : _shownLines[pane];
        if(_shownLines[pane]>=0)
        {
            if(shown==oldVisible)shown=visible;
            else shown=Math.Min(shown,visible);
            if(_offset[pane]==0)
                shown=Math.Min(visible,shown+arrived);
            shown=Math.Min(shown,lines.Count);
        }
        _paneLastSequence[pane]=Math.Max(_paneLastSequence[pane],_data?.Lines.LastOrDefault()?.Sequence??0);
        _paneRowCounts[pane]=lines.Count;
        _shownLines[pane]=shown;
        _visibleLines[pane] = visible;
        if (_offset[pane] > 0 && _anchor[pane] > 0)
        {
            int anchored = lines.FindIndex(l => l.Sequence == _anchor[pane]);
            if (anchored >= 0) _offset[pane] = Math.Max(0, lines.Count - shown - anchored - _anchorWrappedRow[pane]);
        }
        _offset[pane] = Math.Clamp(_offset[pane], 0, Math.Max(0, lines.Count - shown));
        int first = Math.Max(0, lines.Count - shown - _offset[pane]);
        _anchor[pane] = _offset[pane] > 0 && first < lines.Count ? lines[first].Sequence : 0;
        _anchorWrappedRow[pane] = _anchor[pane] > 0 ? first - lines.FindIndex(l => l.Sequence == _anchor[pane]) : 0;
        // 00570FF0 still delivers arrivals to hidden panes. 00680700 only
        // removes font objects/scrollbars; it does not suspend history storage.
        if(_hidden)return;
        // 006809B0 starts at VP_TL.y+3. 00680320 removes authored leading
        // spaces, adds the row marker column, and indents continuation rows.
        double textTop=textArea.Y-3*_size.Height/480;
        var textClip=new Rect(textArea.X,textTop,textArea.Width,textArea.Height+3*_size.Height/480);
        for (int n = first; n < Math.Min(lines.Count, first + shown); n++)
        {
            string text=lines[n].Format(continuationPrefix);
            Text(text, new(textArea.X, textTop + (n - first) * step, textArea.Width, step), lines[n].Color ?? Color(lines[n].Type), fontId, clip: textClip);
        }
        PublishPaneSlider(pane);
    }
    /// <summary>The Emote menu's entries: the ship's emotes in space, the avatar's in a station (also the Modern UI player menu's).</summary>
    internal List<(string Label, Action Run)> EmoteEntries()
    {
        var entries = new List<(string, Action)>();
        if (_station)
        {
            foreach (string label in AvatarEmotes)
            {
                string tag = label switch { "No Way!" => "noway", "Wave" => "handwave", _ => label.ToLowerInvariant() };
                entries.Add((label, () => _ = PerformStationEmote(tag)));
            }
            string[] genderLabels = _female ? ["Flip", "Pirouette", "Rumpshake"] : ["Doh", "Judo", "Moonwalk"];
            for (int i = 0; i < genderLabels.Length; i++) { string tag = "gender" + (i + 1); entries.Add((genderLabels[i], () => _ = PerformStationEmote(tag))); }
        }
        else foreach (var (label, command) in new[] { ("Loop", "/loop"), ("Dive", "/dive"), ("Barrel Left", "/leftroll"), ("Barrel Right", "/rightroll"), ("Spin Left", "/leftspin"), ("Spin Right", "/rightspin"), ("Wing Wave", "/wave") })
            entries.Add((label, () => _ = PerformShipEmote(command)));
        return entries;
    }
    /// <summary>The Chat menu's settings entries (also the Modern UI player menu's).</summary>
    internal List<(string Label, Action Run)> ChatSettingEntries() =>
        [("Set Channels", () => OpenChatDialog(ChatDialog.Channels)), ("Set Messages", () => OpenChatDialog(ChatDialog.Messages)), ("Ignore List", () => OpenChatDialog(ChatDialog.Ignore)),
         ("Reply List", OpenReplyList), ("Private Channel", () => OpenChatDialog(ChatDialog.Private))];
    private void ShowMenu(string menu) { _replyPopup = false; _menu = menu; _menuOffset = 0; Publish(); }
    private void Unavailable(string feature) { _menu = ""; Error = feature + " is not implemented yet."; Publish(); }
    private void Menu(Rect outer, double unit)
    {
        int imageStart = _images.Count;
        var entries = new List<(string Label, Action? Action, bool Selected)>();
        void Add(string label, Action action, bool selected = false) => entries.Add((label,()=>SelectComboEntry(action),selected));
        void Destination(string label, string dest, string? target = null) => Add(label, () => SelectDestination(label, dest, target), _defaultLabel.Equals(label, StringComparison.OrdinalIgnoreCase));
        switch (_menu)
        {
            case "Chat":
                foreach (var (label, run) in ChatSettingEntries()) Add(label, run);
                // 0057AAB0/0057A060: separator and header keep ordinary bindings: they click and close the menu (CHAT-D30).
                entries.Add(("~~~~~~~~~~~~", () => SelectComboEntry(() => { }), false)); entries.Add(("-send to:-", () => SelectComboEntry(() => { }), true));
                foreach(string destination in new[]{"Broadcast","Local","Guild","Group"})
                    if(_options.GetValueOrDefault("Channel_"+destination,"yes").Equals("yes",StringComparison.OrdinalIgnoreCase) &&
                        (destination!="Guild"||_inGuild) && (destination!="Group"||_inGroup))
                        Destination(destination,destination);
                foreach (string channel in MonitoredPublicChannels()) Destination(channel, "Channel", channel);
                // 00568D0F..D61 appends HUD+214 once, gated by Channel_Private;
                // the server's channel-list array is not this menu's producer.
                if(_data?.PrivateChannel is {Length:>0} privateChannel &&
                    _options.GetValueOrDefault("Channel_Private","yes").Equals("yes",StringComparison.OrdinalIgnoreCase))
                    Destination(ChatChannelLabel(privateChannel),"Channel",privateChannel);
                break;
            case "Group":
                Add("Find / Leave", () => { _menu = ""; CloseChatDialog(); if (GroupFinderRequested is {} open) _ = open(); });
                Add("Friends List", () => { _menu = ""; CloseChatDialog(); if (FriendsListRequested is {} open) _ = open(); });
                Add("Guild Info", () => { _menu = ""; CloseChatDialog(); if (GuildInfoRequested is {} open) _ = open(); }); break;
            case "Options":
                // Next categories follow the Game Options grid order after the retail entries.
                foreach (string category in OptionsMenuCategories)
                    Add(category, () => { if (OptionsRequested is not null) { _menu = ""; CloseChatDialog(); OptionsRequested(category); Publish(); } else Unavailable(category); });
                break;
            case "Help":
                Add("Commands", OpenCommands);
                foreach (string feature in new[] { "Get Help", "Self Help", "Captain's Log" }) Add(feature, () => { _menu = ""; CloseChatDialog(); InformationRequested?.Invoke(feature); Publish(); });
                break;
            case "Emote":
                foreach (var (label, run) in EmoteEntries()) Add(label, run);
                break;
        }
        if(PublishChatCombo(entries))return;
        double row = 8.3 * unit, top = 2 * unit, bottom = 17 * unit;
        int count = Math.Max(1, (int)((outer.Height - top - bottom) / row));
        outer = new(outer.X, outer.Y, outer.Width, Math.Min(outer.Height, top + Math.Max(1, Math.Min(count, entries.Count)) * row + bottom));
        Box(outer, Pixel(28, 37, 73, 215));
        Hit("menu", outer, () => { }, wheel: d => { _menuOffset += d > 0 ? -3 : 3; Publish(); });
        _menuOffset = Math.Clamp(_menuOffset, 0, Math.Max(0, entries.Count - count));
        for (int i = _menuOffset; i < Math.Min(entries.Count, _menuOffset + count); i++)
        {
            var entry = entries[i]; var r = new Rect(outer.X + 7 * unit, outer.Y + top + (i - _menuOffset) * row, outer.Width - 10 * unit, row);
            string id = "entry" + i;
            Text(entry.Label, r, _hover == id || entry.Selected ? new(1, 1, 0) : new(1, 1, 1), "FONT_SMALL");
            if (entry.Action is { } action) Hit(id, r, action, wheel: d => { _menuOffset += d > 0 ? -3 : 3; Publish(); });
        }
        // Keep the dropdown above native arrow/tab meshes as well as chat text.
        if (_menuLayer is { } layer)
        {
            var images = _images.Skip(imageStart).ToImmutableArray();
            while (_images.Count > imageStart) _images.RemoveAt(_images.Count - 1);
            _draws.Add(new(layer, 0, (outer), false) { LayoutGroup=EnbUiGroup.Chat, Images = images });
        }
    }
    private void Handle(string id, Rect r, string label)
    {
        Button(id, label, r, () => { }, right: () => { if (id == "resize") (_height, _previousHeight) = (_previousHeight, _height); else (_split, _previousSplit) = (_previousSplit, _split); Publish(); });
    }
    private bool _historyPassThrough;
    public void SetHistoryPassThrough(bool value)
    {
        _historyPassThrough=value;
        foreach(var (id,hit) in _hits) if(id.StartsWith("pane",StringComparison.Ordinal))hit.IsHitTestVisible=!value;
        TransportChanged?.Invoke();
    }
    private void Hit(string id, Rect r, Action action, Action? right = null, Action<double>? wheel = null, EnbUiGroup group = EnbUiGroup.Chat)
    {
        if (!_hits.TryGetValue(id, out var hit)) _hits[id] = hit = new();
        hit.Group = group;
        hit.IsHitTestVisible = !_historyPassThrough || !id.StartsWith("pane", StringComparison.Ordinal) && id != "mchat/history";
        hit.Left = action; hit.Right = right; hit.Wheel = wheel;
        hit.Bounds = r; hit.Published = hit.IsVisible = true;
        hit.ZIndex = id == "mchat/history" ? 0 : id == "replypopup/frame" ? 1 : id.StartsWith("chatdialog/m", StringComparison.Ordinal) && id.Contains("/scroll", StringComparison.Ordinal) ? 22 : id.StartsWith("mchat/menu", StringComparison.Ordinal) ? (id == "mchat/menu" ? 11 : 12) : id.StartsWith("chatdialog/ignore/entry/", StringComparison.Ordinal) ? (id.EndsWith("/drag", StringComparison.Ordinal) ? 22 : 23) : id.StartsWith("chatdialog/", StringComparison.Ordinal) ? (id == "chatdialog/block" ? 20 : 21) : id.StartsWith("entry", StringComparison.Ordinal) ? 12 : id == "menu" ? 11 : id is "resize" or "split" || id.StartsWith("thumb",StringComparison.Ordinal) ? 3 : id.StartsWith("pane", StringComparison.Ordinal) ? 0 : 2;
    }
    private long _hoverStarted;
    private void Button(string id, string label, Rect r, Action action, Action? right = null, Action<double>? wheel = null)
    {
        string key = id.StartsWith("up", StringComparison.Ordinal) ? "up" : id.StartsWith("down", StringComparison.Ordinal) ? "down" : id == "splitMode" ? "mode" + _mode : id == "resize" ? "resize" : id == "split" ? _mode == 2 ? "vertical" : "split" : "button";
        bool menuEntry = id.StartsWith("entry", StringComparison.Ordinal);
        int first = _images.Count;
        if (!_art.ContainsKey(key) && !menuEntry) Box(r, ButtonPixel);
        if (key == "button")
        {
            double inset = id.StartsWith("tab/", StringComparison.Ordinal) ? 7 * _size.Width / 640 : 2;
            Text(label, new(r.X + inset, r.Y, Math.Max(0, r.Width - inset - 2), r.Height), _hover == id ? new(1, 1, 0) : new(1, 1, 1), "FONT_SMALL", centerVertically: true);
        }
        if (!menuEntry && _art.TryGetValue(key, out var art))
        {
            var captions = _images.Skip(first).ToImmutableArray();
            while (_images.Count > first) _images.RemoveAt(_images.Count - 1);
            var artBounds = id.StartsWith("tab/", StringComparison.Ordinal) ? new Rect(r.X, r.Y, r.Width + 2 * _size.Width / 640, r.Height) : r;
            _draws.Add(new(art.Asset, 0, (artBounds), false) { LayoutGroup=EnbUiGroup.Chat, DesignBounds = (art.Bounds), Images = captions });
        }
        Hit(id, r, action, right, wheel);
    }
    private void Box(Rect r, EnbUiRaster fill)
    { _images.Add(new(fill, (r))); _images.Add(new(EdgePixel, new(r.X, r.Y, r.Width, 1))); _images.Add(new(EdgePixel, new(r.X, r.Bottom - 1, r.Width, 1))); _images.Add(new(EdgePixel, new(r.X, r.Y, 1, r.Height))); _images.Add(new(EdgePixel, new(r.Right - 1, r.Y, 1, r.Height))); }
    private void Text(string value, Rect r, EnbRgb color, string fontId, bool centerVertically = false, float opacity = 1, float verticalScale = 1, Rect? clip = null)
    {
        var font = Font(fontId); if (font is null) return;
        if (!_text.TryGetValue(_textIndex, out var label)) _text[_textIndex] = label = new(); _textIndex++;
        label.SetText(value, font, color); label.SetDisplayScale(1,1);
        if(centerVertically)label.FitTo(r.Size,_scale);
        if (label.Raster is { } raster) _images.Add(new(raster, new(r.X, centerVertically ? r.Y + (r.Height - label.Height) / 2 : r.Y, label.Width, label.Height)) { Clip = (clip ?? r), Opacity = opacity, VerticalScale = verticalScale });
    }
    private EnbRetailFontFace? Font(string id)
    {
        if (_assets is null || !EnbRetailFontFactory.TryResolveDefinition(_assets, id, (int)(_size.Width * _scale), (int)(_size.Height * _scale), out var d) || d.Resource is null) return null;
        if (id.StartsWith("FONT_CHAT_LINE_", StringComparison.Ordinal) && _assets.Fonts!.TrySelectProfile((int)(_size.Width * _scale), (int)(_size.Height * _scale), out var profile)) d = _assets.ResolveChatFont(_session(), profile, d);
        var key = (d.Resource!, d.Scale, d.Spacing, d.LineSpacing);
        if (_fonts.TryGetValue(key, out var font)) return font;
        return EnbRetailFontFactory.TryCreate(_assets, d, out font) ? _fonts[key] = font : null;
    }
    private static IEnumerable<string> Wrap(string text, EnbRetailFontFace font, double width)
        => EnbRetailDialogText.WrapChat(text, (float)width, c => font.CharacterAdvance(c));
    private void Finish() { foreach (var (id, hit) in _hits) if (!hit.Published) hit.IsVisible = false; Presentation = _images.ToImmutable(); ArtPresentation = _draws.ToImmutable(); Changed?.Invoke(Presentation); ArtChanged?.Invoke(ArtPresentation); TransportChanged?.Invoke(); }
    private static EnbUiRaster Pixel(byte r, byte g, byte b, byte a) => new(1, 1, ImmutableArray.Create(b, g, r, a));
    private void ResetPaneHistory() { EndHistorySlider(); foreach (var pane in _paneHistory) pane.Clear(); Array.Clear(_paneLastSequence); Array.Clear(_paneWrapping); Array.Clear(_historySliderValues); }
    private void PublishWrappedDialogLabel(string prefix, string id, IReadOnlyDictionary<string, string> properties,
        string text, Vector3 point, EnbRetailFontFace font, EnbRgb color, float opacity)
    {
        if (!_dialogDesign.TryGetValue(prefix + "/" + properties["Attach"], out var bounds)) return;
        _wrappedLabels.Publish(prefix, id, properties, text, point, font, color, opacity, _assets!,
            _size, _scale, bounds, _hover, _pingPointer);
    }
    private void DisposeFonts() { _messageLines.Dispose(); foreach (var t in _text.Values) t.Dispose(); _text.Clear(); _fonts.Clear(); }
    private FileStream? _chatLog;
    private bool _chatLogAttempted;
    public void Dispose() { _wrappedLabels.Clear(); _chatInput.Dispose(); _joinInput.Dispose(); _ignoreInput.Dispose(); _passwordInput.Dispose(); _verifyInput.Dispose(); EndHistorySlider(); _chatDialog = ChatDialog.None; ReportChatListOwner(dispose: true); if (_listCaptured) CaptureRequested?.Invoke(null); _listCaptured = false; _listRepeat = 0; _chatLog?.Dispose(); DisposeFonts(); foreach (var tip in _pingTips) tip.Dispose(); }
}
