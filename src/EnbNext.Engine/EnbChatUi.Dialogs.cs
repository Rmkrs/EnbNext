using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using EnbNext.Presentation;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbChatUi
{
    private enum ChatDialog { None, Channels, Messages, Ignore, Private, PrivateMembers, PrivateJoin, PrivatePassword, Commands }

    private static readonly (string? Option, string Label, int? Type)[] ChannelOptions =
    [
        ("Channel_Broadcast", "Broadcast", 2), ("Channel_General", "General Chat", null),
        ("Channel_Explorers", "Explorers", null), ("Channel_Private", "Private Channel", 13),
        ("Channel_OOC", "Out of Context", null), ("Channel_Warriors", "Warriors", null),
        ("Channel_Group", "Group", 4), ("Channel_Market", "Market", null),
        ("Channel_Traders", "Tradesman", null), ("Channel_Guild", "Guild", 8),
        ("Channel_Newbie", "New Players", null), ("Channel_Sentinels", "Sentinels", null),
        ("Channel_Local", "Local", 7), ("Channel_Terran", "Terran", null),
        ("Channel_Defenders", "Defenders", null), (null, "", null),
        ("Channel_Jenquai", "Jenquai", null), ("Channel_Enforcers", "Enforcers", null),
        (null, "", null), ("Channel_Progen", "Progen", null), (null, "", null),
    ];
    private static readonly (string Option, string Label, int Type)[] MessageOptions =
    [
        ("Chat_Tell", "Player Tells", 3), ("Chat_NPC", "NPC Messages", 5),
        ("Chat_Tips", "Game Tips", 6), ("Chat_Status_IC", "Computer (General)", 0),
        ("Chat_Warnings", "Computer (Warnings)", 21), ("Chat_Errors_IC", "Computer (Errors)", 17),
        ("Chat_Attention", "Attention", 22),
    ];
    private static readonly (string Option, string Channel)[] NamedChannelOptions =
    [
        ("Channel_General", "General"), ("Channel_OOC", "Out of Context"), ("Channel_Market", "Market"),
        ("Channel_Newbie", "New Players"), ("Channel_Terran", "Terran"), ("Channel_Jenquai", "Jenquai"),
        ("Channel_Progen", "Progen"), ("Channel_Explorers", "Explorers"), ("Channel_Warriors", "Warriors"),
        ("Channel_Traders", "Tradesmen"), ("Channel_Sentinels", "Sentinels"),
        ("Channel_Defenders", "Defenders"), ("Channel_Enforcers", "Enforcers"),
    ];

    private readonly Dictionary<string, EnbAuthoredUiAsset> _dialogPieces = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rect> _dialogDesign = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnbAuthoredUiAsset> _dialogAssets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EnbInterfaceLayoutCatalog> _dialogLayouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _dialogValues = new(StringComparer.OrdinalIgnoreCase);
    private string _privatePasswordPrompt = "";
    private ChatDialog _chatDialog;
    private readonly Dictionary<string, (float Initial, float Target, float Fade, float Blink, float Revert)> _dialogAlphaStyles = [];
    private readonly Dictionary<string, EnbAttachedLabelAlpha> _dialogAlpha = [];
    private bool _dialogAlphaActive;
    private readonly Dictionary<string, EnbAttachedLabelWipe> _ignoreOpening = [];
    private bool _ignoreOpeningActive;
    private int _ignoreSelectionIndex = -1, _privateMemberSelectionIndex = -1;
    private string? _ignoreSelection;
    private string? _ignoreTarget;
    private double _ignoreScroll;
    private bool _ignoreReady;
    private ImmutableArray<string> IgnoreRows => _ignoreReady ? _data?.Ignored ?? [] : [];
    private bool _ignoreEntry;
    private bool _replyPopup;
    private int _privatePanelState;
    private string _privateChannel = "";
    private bool _privateMembersReady;
    private double _privateMembersScroll;
    private int _privateMembersPage;
    private string? _privateMemberSelection;
    private EnbAuthoredUiAsset? _privateMemberThumb;
    private readonly Dictionary<string, (EnbAuthoredUiAsset Asset, Rect Bounds)> _privateMemberSlider = [];
    private bool _privateJoinEntry;
    private bool _privateJoinNotFound;
    private Vector _ignoreOffset, _ignoreEntryOffset;
    private bool _publishingIgnoreEntry;
    private bool _privatePasswordEntry;
    private bool? _privatePasswordMismatch;
    private int _privatePasswordMode = 2;
    // 005EB2F0 retains the requested channel for 005E6EC0, before membership.
    private string _privateJoinChannel = "";
    private bool _privateRequestPending;
    private long _privateRequestGeneration;
    private readonly EnbUiTextInput _passwordVerifyEditor;
    private bool _awaitingPrivateCreate;
    private long _privateCreateRevision;

    private void PlayFontClick() => InteractionSoundRequested?.Invoke("fontclick.wav");
    // MainHud event 00563280: category events 8..12, then row event 15.
    private void PlayCommandMenuOpen() => InteractionSoundRequested?.Invoke("SR_scifielectronic42.wav");
    private void PlayCommandMenuSelection() => InteractionSoundRequested?.Invoke("TextClick03.wav");
    private void PlayRollSelect() => InteractionSoundRequested?.Invoke("Roll_Select02b.wav");

    private void LoadChatDialogArt()
    {
        _wrappedLabels.Clear();
        _dialogPieces.Clear(); _dialogDesign.Clear(); _dialogAssets.Clear(); _dialogLayouts.Clear();
        if (_assets is null) return;
        var passwordIni = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "cht04.ini"));
        _privatePasswordPrompt = passwordIni.TryGetSection("STR-2", out var promptSection)
            ? promptSection.GetValueOrDefault("String0", "") : "";
        foreach (var (key, model, ini) in new[]
        {
            ("channels", "opt06a.w3d", "fa_opt06a.ini"),
            ("messages", "opt03a.w3d", "fa_opt03a-chat.ini"),
            ("ignore", "tsi27a.w3d", "fa_tsi27a.ini"),
            ("ignoreentry", "trade01a.w3d", "fa_trade01a.ini"),
            ("private", "cht01.w3d", "cht01.ini"),
            ("privatemembers", "cht05.w3d", "cht05.ini"),
            ("privatejoin", "cht03.w3d", "cht03.ini"),
            ("privatepassword", "cht04.w3d", "cht04.ini"),
        })
        {
            var asset = EnbAuthoredUiAsset.Load(_assets, model);
            if (asset is null) continue;
            _dialogAssets[key] = asset;
            _dialogLayouts[key] = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, ini));
            foreach (var mesh in asset.Resource.Meshes)
            {
                string piece = key + "/" + mesh.Name;
                _dialogPieces[piece] = asset with { Resource = asset.Resource.Copy().SetMeshes([mesh]) };
                _dialogDesign[piece] = EnbCockpitUi.MeshBounds(asset, mesh.Name);
                if (!mesh.Name.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase)) continue;
                EnbW3dRenderTextureBinding Checked(EnbW3dRenderTextureBinding texture) => texture with
                    { LogicalName = "if_check_on.tga", Location = null, ResolveLogicalNameAtRuntime = true };
                _dialogPieces[piece + "/on"] = asset with { Resource = asset.Resource.Copy().SetMeshes([(mesh).Copy().SetMaterialPasses(mesh.MaterialPasses.Select(pass => (pass).Copy().SetTextureStages(pass.TextureStages.Select(stage => (stage).Copy().SetTextures(stage.Textures.Select(Checked).ToArray())).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(binding => binding with
                            { Textures = binding.Textures.Select(texture => texture is null ? null : Checked(texture)).ToArray() }).ToArray())).ToArray())]) };
            }
        }
        _dialogAlphaStyles.Clear();
        var effects = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fontart.ini"));
        foreach (string prefix in new[] { "channels", "privatepassword", "privatejoin", "private", "ignoreentry" })
        if (_dialogLayouts.TryGetValue(prefix, out var alphaLayout))
            foreach (var label in alphaLayout.Definitions)
            {
                float initial = 0, target = 1, fade = 0, blink = 0, revert = 0;
                foreach (var effect in effects.EnumerateSections().Select(s => s.Value))
                {
                    string? id = effect.GetValueOrDefault("EffectID"); if (id is null) continue;
                    bool Uses(string property) => label.Properties.GetValueOrDefault(property, "").Split(',').Any(v => v.Trim() == id);
                    float Number(string key) => float.Parse(effect.GetValueOrDefault(key, "0"), System.Globalization.CultureInfo.InvariantCulture);
                    if (Uses("EffectOn") && effect.GetValueOrDefault("EffectType") == "FADE") { initial = Number("Initial"); target = Number("Intensity"); fade = Number("Speed"); }
                    if (Uses("MouseOver") && effect.GetValueOrDefault("EffectType") == "BLINK") blink = Number("Speed");
                    if (Uses("EffectConst") && effect.GetValueOrDefault("EffectType") == "REVERT") revert = Number("Speed");
                }
                _dialogAlphaStyles[prefix + "/" + label.StringId] = (initial, target, fade, blink, revert);
            }
        LoadPrivateMemberSlider();
    }

    private void LoadPrivateMemberSlider()
    {
        _privateMemberSlider.Clear(); _privateMemberThumb = null;
        if (_assets is null) return;
        foreach (var (key, file, mesh) in new[] { ("up", "slider_up.w3d", "CLICK_UP"), ("down", "slider_dn.w3d", "CLICK_DOWN") })
            if (EnbAuthoredUiAsset.Load(_assets, file) is { } part)
                _privateMemberSlider[key] = (part with { Resource = part.Resource.Copy().SetMeshes(part.Resource.Meshes.Where(m => m.Name == mesh).ToArray()) },
                    EnbCockpitUi.MeshBounds(part, mesh).Translate(-320, -240));
        var catalog = _assets.RetailMixResources;
        if (catalog is not null && EnbAuthoredUiAsset.Load(_assets, "slider_vert.w3d") is { } thumb &&
            catalog.TryReadResource("slider_vert.w3d", out var bytes, out var location) &&
            catalog.TryReadResource("slider_vert-1.w3d", out var animation, out _) &&
            EnbW3dModel.TryDecode([.. bytes, .. animation], out var model) &&
            EnbAuthoredUiAsset.TryCompile(catalog, location, model, "slider_vert", out var resource))
            _privateMemberThumb = thumb with { Resource = resource };
    }

    private void PublishChatListSlider()
    {
        // 005E4500/0057BE50 add a new GDSlider (0056D8D0 -> 0066DFC0) and build it
        // from slider_up/dn/vert on the SLIDER_00 bones (0066E460), layered above
        // the screen model. The model's own BAR_CARGO01/PULL_CARGO01 meshes are not
        // referenced by either path and keep drawing. 0066EE20 hides the slider
        // when total == page size.
        int count = ChatListCount;
        string prefix = ChatListPrefix;
        void Scroll(int amount) { SetChatListScroll(ChatListScroll + amount); Publish(); }
        if (count <= 7 || _privateMemberThumb is not { } thumb ||
            !_privateMemberSlider.TryGetValue("up", out var up) || !_privateMemberSlider.TryGetValue("down", out var down) ||
            !_dialogAssets.TryGetValue(prefix, out var asset)) return;
        var transforms = EnbCockpitUi.ResolveTransforms(asset);
        Point Anchor(string name)
        {
            var pivot = asset.Hierarchy.Pivots.First(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var point = transforms[pivot.Ordinal].Translation;
            return new(point.X + 320, 240 - point.Y);
        }
        var top = Anchor("SLIDER_00_MIN"); var bottom = Anchor("SLIDER_00_MAX");
        void Part(string key, EnbAuthoredUiAsset part, Rect source, Rect design, Action action)
        {
            string id = "chatdialog/" + prefix + "/slider/" + key;
            Hit(id, DialogScreen(design), action, wheel: delta => Scroll(delta > 0 ? -1 : 1));
            _draws.Add(new(part, 0, (DialogScreen(design)), _hover == id) { LayoutGroup=EnbUiGroup.Chat, GadgetOwner = _hits[id], DesignBounds = (source) });
        }
        var upBounds = up.Bounds.Translate(top.X, top.Y);
        var downBounds = down.Bounds.Translate(bottom.X, bottom.Y);
        Part("up", up.Asset, up.Bounds, upBounds, () => Scroll(-1));
        Part("down", down.Asset, down.Bounds, downBounds, () => Scroll(1));
        double start = upBounds.Bottom, end = downBounds.Top, trackHeight = Math.Max(0, end - start);
        double height = Math.Max(16, trackHeight * 7 / count);
        var animation = thumb.Resource.Animations.Single(a => a.Name.Equals("slider_vert-1", StringComparison.OrdinalIgnoreCase));
        var hierarchy = thumb.Resource.Hierarchies.Single();
        var poseTransforms = new System.Numerics.Matrix4x4[hierarchy.Pivots.Count];
        foreach (var pivot in hierarchy.Pivots)
        {
            if (!EnbW3dAnimationSampler.TrySamplePivot(animation, pivot.Ordinal, (float)Math.Max(0, height - 16), out var sample, out _)) return;
            var local = System.Numerics.Matrix4x4.CreateTranslation(sample.Translation) * pivot.LocalTransform;
            poseTransforms[pivot.Ordinal] = pivot.IsRoot ? local : local * poseTransforms[pivot.ParentIndex];
        }
        var pose = new EnbW3dHierarchyPoseOwner(poseTransforms.ToImmutableArray(), Enumerable.Repeat(true, poseTransforms.Length).ToImmutableArray(), null, null);
        if (!EnbW3dSkinDeformer.TryDeform(thumb.Resource.Meshes.Single(), pose, out var geometry, out _)) return;
        var min = geometry.Positions.Aggregate(System.Numerics.Vector3.Min); var max = geometry.Positions.Aggregate(System.Numerics.Vector3.Max);
        var source = new Rect(min.X, -max.Y, max.X - min.X, max.Y - min.Y);
        var track = new Rect(top.X + source.X, start, source.Width, trackHeight);
        var thumbBounds = new Rect(track.X, start + Math.Max(0, trackHeight - height) *
            ChatListScroll / (count - 7), source.Width, height);
        _listTrack = DialogScreen(track); _listThumb = DialogScreen(thumbBounds);
        Hit("chatdialog/" + prefix + "/slider/track", DialogScreen(track), () => Scroll(_last.Y < _listThumb.Top ? -7 : 7));
        Part("thumb", thumb with { Geometry = geometry }, source, thumbBounds, () => { });
    }

    private IEnumerable<string> MonitoredPublicChannels()
        => NamedChannelOptions.Where(value => _options.GetValueOrDefault(value.Option,
            value.Option == "Channel_Newbie" ? "yes" : "no").Equals("yes", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Channel);

    // MainHudOpenCommandMenuCategory 00568D42 and SelectChatInputChannel
    // 00564D24 both format a monitored private destination as "Private %s".
    private static string ChatChannelLabel(string channel)
        => PublicChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) || channel == "OOC" ||
           channel.StartsWith("Private ", StringComparison.OrdinalIgnoreCase) || channel.Length == 0
            ? channel : "Private " + channel;

    private void OpenReplyList()
    {
        _menu = "";
        if (_data?.RecentPeers.Length > 0) { _replyPopup = true; Publish(); return; }
        _replyPopup = false;
        if (_session() is { } character)
        {
            // Retail AddChatMessage 0074D990 plays Text_Error for a visible
            // type-17 COMPUTER error, separately from the menu widget click.
            if (!_hiddenTypes.Contains(17)) InteractionSoundRequested?.Invoke("Text_Error");
            LocalChatMessageRequested?.Invoke(character, 17, "Unable to reply - no one to reply to.");
        }
        Publish();
    }

    private void SelectReply(string peer)
    {
        PlayRollSelect();
        SelectInputDestination("Reply", peer);
    }

    private void SelectDestination(string label, string destination, string? target)
    {
        PlayRollSelect();
        _menu = "";
        SetDefaultDestination(DestinationKind(destination, target ?? ""), label);
        Error = null; SaveSettings();
        if (_session() is { } character)
        {
            // Current retail AddChatMessage 0074D990 plays Text_Error for
            // type 0x13 when Chat_Errors_System is enabled.
            if (!_options.GetValueOrDefault("Chat_Errors_System", "yes").Equals("no", StringComparison.OrdinalIgnoreCase))
                InteractionSoundRequested?.Invoke("Text_Error");
            LocalChatMessageRequested?.Invoke(character, 19, "Setting chat channel to " + label + ".");
        }
        Publish();
    }

    private void OpenChatDialog(ChatDialog dialog)
    {
        _wrappedLabels.Clear();
        if (IsChatList) CloseChatDialog();
        _menu = ""; _replyPopup = false; _chatDialog = dialog; _dialogValues.Clear(); _ignoreSelection = null; _ignoreSelectionIndex = -1;
        _ignoreEntry = _privateJoinEntry = _privatePasswordEntry = false;
        _dialogAlpha.Clear(); _dialogAlphaActive = false;
        if (dialog == ChatDialog.Ignore) { _ignoreInput.InsertMode = true; _ignoreOpening.Clear(); _ignoreOpeningActive = false; _ignoreOffset = default; _ignoreScroll = 0; _ignoreReady = false; }
        _editor.IsVisible = false; _editor.Text = ""; _passwordVerifyEditor.IsVisible = false; _passwordVerifyEditor.Text = "";
        Error = null;
        if (dialog == ChatDialog.Private)
        {
            _privatePanelState = 0;
            _privateChannel = (_data?.Channels ?? []).FirstOrDefault(channel =>
                !PublicChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) && channel != "OOC") ?? "";
        }
        if (dialog == ChatDialog.Channels)
        {
            foreach (var value in ChannelOptions)
                if (value.Option is { } option)
                    _dialogValues[option] = value.Type is { } type ? !_hiddenTypes.Contains(type)
                        : _options.GetValueOrDefault(option, option == "Channel_Newbie" ? "yes" : "no").Equals("yes", StringComparison.OrdinalIgnoreCase);
        }
        else if (dialog == ChatDialog.Messages)
            foreach (var value in MessageOptions) _dialogValues[value.Option] = !_hiddenTypes.Contains(value.Type);
        Publish();
        if (dialog == ChatDialog.Ignore) _ = RequestIgnoredAsync();
    }

    private void CloseChatDialog()
    {
        _wrappedLabels.Clear();
        if (IsChatList) { if (_listCaptured) CaptureRequested?.Invoke(null); _listCaptured = false; _drag = null; _listRepeat = 0; }
        _chatDialog = ChatDialog.None; _dialogValues.Clear(); _ignoreSelection = null; _ignoreSelectionIndex = -1; _ignoreEntry = false; _privateJoinEntry = false; _privatePasswordEntry = false; _privatePasswordMismatch = null; _awaitingPrivateCreate = false;
        _editor.Text = ""; _editor.Use(_chatInput); _editor.PasswordChar = '\0';
        _passwordVerifyEditor.IsVisible = false; _passwordVerifyEditor.Text = "";
        _editor.IsVisible = false; _editor.Text = ""; Error = null; Publish();
    }

    private async Task CommitChannelsAsync()
    {
        // 005C93B0 walks the authored option rows (Explorers before Out of Context; CHAT-D20).
        foreach (var row in ChannelOptions)
        {
            if (Array.FindIndex(NamedChannelOptions, n => n.Option == row.Option) is not (>= 0 and var at)) continue;
            var value = NamedChannelOptions[at];
            bool oldValue = _options.GetValueOrDefault(value.Option, value.Option == "Channel_Newbie" ? "yes" : "no")
                .Equals("yes", StringComparison.OrdinalIgnoreCase);
            bool newValue = _dialogValues.GetValueOrDefault(value.Option);
            if (oldValue == newValue || ChannelMonitoringRequested is null) continue;
            try
            {
                if (!await ChannelMonitoringRequested(value.Channel, newValue))
                { Error = "Unable to update the " + value.Channel + " channel."; Publish(); return; }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            { Error = ex.Message; Publish(); return; }
        }
        foreach (var value in ChannelOptions)
        {
            if (value.Option is not { } option) continue;
            bool enabled = _dialogValues.GetValueOrDefault(option);
            _options[option] = enabled ? "yes" : "no";
            if (value.Type is { } type) { if (enabled) _hiddenTypes.Remove(type); else _hiddenTypes.Add(type); }
        }
        // 005C93B0 updates subscriptions/options and rebuilds the destination
        // combo. Existing pane rows are not replayed or retrospectively filtered.
        ReconcileDefaultDestination();
        SaveSettings(); CloseChatDialog();
    }

    private void CommitMessages()
    {
        foreach (var value in MessageOptions)
        {
            bool enabled = _dialogValues.GetValueOrDefault(value.Option);
            _options[value.Option] = enabled ? "yes" : "no";
            if (enabled) _hiddenTypes.Remove(value.Type); else _hiddenTypes.Add(value.Type);
        }
        // 005C9F20 changes admission for subsequent 0067DB40 calls. Only an
        // actual chat-layout reconstruction replays the shared history.
        ReconcileDefaultDestination();
        SaveSettings(); CloseChatDialog();
    }

    private void PublishChatDialog()
    {
        _modernDialogShown = false;
        if (_chatDialog == ChatDialog.None) return;
        if (_modernChat && PublishModernChatDialog()) return;
        string prefix = _chatDialog switch { ChatDialog.Commands => "commands", ChatDialog.Channels => "channels", ChatDialog.Messages => "messages", ChatDialog.Private => "private", ChatDialog.PrivateMembers => "privatemembers", ChatDialog.PrivateJoin => "privatejoin", ChatDialog.PrivatePassword => "privatepassword", _ => "ignore" };
        if (!_dialogAssets.TryGetValue(prefix, out var asset)) return;
        if (prefix is "ignore" or "commands")
            Hit("chatdialog/block", DialogScreen(_dialogDesign.Where(p => p.Key.StartsWith(prefix + "/", StringComparison.Ordinal))
                .Select(p => p.Value).Aggregate((a,b) => a.Union(b))), () => { });
        int imageStart = _images.Count;
        foreach (var key in _dialogDesign.Keys.Where(key => key.StartsWith(prefix + "/", StringComparison.Ordinal)).OrderBy(DialogPieceOrder))
        {
            string mesh = key[(prefix.Length + 1)..];
            if (!DialogPieceVisible(prefix, mesh)) continue;
            string piece = key;
            if (mesh.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(5), out int check) && DialogCheckValue(prefix, check)) piece += "/on";
            Rect design = _dialogDesign[key];
            var screen = DialogScreen(design);
            bool enabled = DialogPieceEnabled(prefix, mesh);
            bool hovered = enabled && _hover.Equals("chatdialog/" + prefix + "/" + mesh, StringComparison.OrdinalIgnoreCase);
            _draws.Add(new(_dialogPieces[piece], enabled ? 0u : 2u, (screen), hovered)
                { LayoutGroup=EnbUiGroup.Chat, GadgetOwner = _hits.GetValueOrDefault("chatdialog/" + prefix + "/" + mesh), DesignBounds = (_dialogDesign[key].Translate(-320, -240)) });
            if (prefix == "ignore" && mesh.Equals("FRAME_DETAIL", StringComparison.OrdinalIgnoreCase))
                PublishIgnoreListFrame();
        }
        if (prefix == "ignore" && _pingFrames.TryGetValue("DIALOG_BOX_OPTION", out var optionButton))
            foreach (string mesh in new[] { "BUTTON02", "BUTTON03" })
                if (_dialogDesign.TryGetValue(prefix + "/" + mesh, out var design))
                    PingFrame("ignore/button/" + mesh, optionButton, design.Translate(_ignoreOffset.X, _ignoreOffset.Y),
                        verb: DialogPieceEnabled(prefix, mesh) ? 0u : 2u);
        PublishDialogLabels(prefix, asset);
        var labels = _images.Skip(imageStart).ToImmutableArray();
        while (_images.Count > imageStart) _images.RemoveAt(_images.Count - 1);
        if (_draws.Count > 0) _draws[^1] = _draws[^1] with { Images = _draws[^1].Images.AddRange(labels) };
        AddDialogHits(prefix);
        if (prefix is "privatemembers" or "commands" or "ignore") PublishChatListSlider();
        if (prefix == "ignore" && _ignoreEntry) PublishIgnoreEntry();
        if (prefix == "privatejoin" && _privateJoinEntry) PublishPrivateJoinEntry();
        if (prefix == "privatepassword" && _privatePasswordEntry) PublishPrivatePasswordEntry();
    }

    private static int DialogPieceOrder(string key)
        => key[(key.IndexOf('/') + 1)..] is var mesh && mesh.Equals("FRAME_DETAIL", StringComparison.OrdinalIgnoreCase)
            ? 0 : mesh.StartsWith("FRAME", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
    private bool DialogPieceVisible(string prefix, string mesh)
    {
        if (prefix == "privatepassword" && _privatePasswordMode < 2 && mesh.Equals("TEXT_FIELD01", StringComparison.OrdinalIgnoreCase)) return false;
        if (prefix == "channels" && mesh.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(5), out int channel))
            return channel < ChannelOptions.Length && ChannelOptions[channel].Option is not null;
        if (prefix == "messages" && mesh.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(5), out int message))
            return message < MessageOptions.Length;
        if (prefix == "private" && mesh.Equals("BUTTON02", StringComparison.OrdinalIgnoreCase) &&
            (_privatePanelState != 0 || _privateChannel.Length == 0)) return false;
        return true;
    }
    private bool DialogPieceEnabled(string prefix, string mesh)
    {
        if (prefix == "private" && mesh.Equals("BUTTON02", StringComparison.OrdinalIgnoreCase))
            return _privatePanelState == 0 && _privateChannel.Length > 0;
        if (prefix == "privatemembers" && mesh.StartsWith("BUTTON", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(mesh.AsSpan(6), out int memberButton) && memberButton is >= 1 and <= 5)
            return _data?.PrivateChannelOwner == true && _data.PrivateChannel == _privateChannel &&
                _privateMemberSelection is not null && _data.ChannelMembers.TryGetValue(_privateChannel, out var names) &&
                names.Contains(_privateMemberSelection);
        if (prefix != "ignore") return true;
        if (mesh.Equals("BUTTON02", StringComparison.OrdinalIgnoreCase)) return _ignoreSelection is { Length: > 0 };
        if (mesh.Equals("BUTTON03", StringComparison.OrdinalIgnoreCase)) return _ignoreTarget is not null;
        return true;
    }
    private bool DialogCheckValue(string prefix, int index)
        => prefix == "channels" && index < ChannelOptions.Length && ChannelOptions[index].Option is { } channel
            ? _dialogValues.GetValueOrDefault(channel)
            : prefix == "messages" && index < MessageOptions.Length && _dialogValues.GetValueOrDefault(MessageOptions[index].Option);
    private string PrivatePanelLabel(string attach)
    {
        bool monitoring = _privateChannel.Length > 0;
        return (_privatePanelState, attach.ToUpperInvariant()) switch
        {
            (0, "HELP TEXT") => monitoring ? "Channel " + _privateChannel : "No Channel",
            (0, "MESSAGE") => monitoring ? "You are currently monitoring Channel " + _privateChannel : "You are not monitoring a Channel. Click 'join' to join or create a Channel.",
            (0, "BUTTON01") => monitoring ? "Leave" : "Cancel",
            (0, "BUTTON02") => monitoring ? "Members" : "",
            (0, "BUTTON03") => monitoring ? "Change" : "Join",
            (1, "HELP TEXT") => "Private Channels",
            (1, "MESSAGE") => "Which Channel do you wish to join?",
            (1, "BUTTON01") => "Existing",
            (1, "BUTTON02") => "",
            (1, "BUTTON03") => "New",
            (2, "HELP TEXT") => "No Channels Available",
            (2, "MESSAGE") => "Sorry, there are no private channels available. Please try again later.",
            (2, "BUTTON01") => "OK",
            (3, "HELP TEXT") => "Channel " + _privateChannel + " Created",
            (3, "MESSAGE") => "You are the first person in channel " + _privateChannel + ". Do you wish to create a password?",
            (3, "BUTTON01") => "No",
            (3, "BUTTON03") => "Yes",
            _ => "",
        };
    }
    private void PrivatePanelButton(int button)
    {
        if (_privatePanelState == 0)
        {
            if (button == 1 && _privateChannel.Length == 0) { CloseChatDialog(); return; }
            if (button == 3) { _dialogAlpha.Clear(); _privatePanelState = 1; Publish(); return; }
            if (button == 1 && _privateChannel.Length > 0)
            { _ = SubmitPrivateControlAsync(EnbNext.Net7.Social.Net7ClientChatRequestKind.ExitChannel, _privateChannel); return; }
            if (button == 2 && _privateChannel.Length > 0)
            { OpenPrivateMembers(); return; }
        }
        else if (_privatePanelState == 1)
        {
            if (button == 1) { OpenPrivateJoin(); return; }
            if (button == 3) { _ = CreatePrivateChannelAsync(); return; }
        }
        else if (_privatePanelState == 2 && button == 1) { _dialogAlpha.Clear(); _privatePanelState = 1; Publish(); }
        else if (_privatePanelState == 3 && button == 1)
            _ = SubmitPrivateControlAsync(EnbNext.Net7.Social.Net7ClientChatRequestKind.Access, _privateChannel, "none");
        else if (_privatePanelState == 3 && button == 3) OpenPrivatePassword();
    }
    private void OpenPrivateMembers()
    {
        _chatDialog = ChatDialog.PrivateMembers; _privateMembersReady = false; _privateMembersScroll = 0; _privateMembersPage = 0; _privateMemberSelection = null; _privateMemberSelectionIndex = -1;
        Publish();
        _ = RequestPrivateMembersAsync();
    }
    private ImmutableArray<string> PrivateMembers => _privateMembersReady ? _data?.ChannelMembers.GetValueOrDefault(_privateChannel, []) ?? [] : [];
    private int PrivateMembersCount() => PrivateMembers.Length;
    private void ScrollPrivateMembers(int amount)
    {
        SetPrivateMembersScroll(_privateMembersScroll + amount);
        Publish();
    }
    private void SetPrivateMembersScroll(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, PrivateMembersCount() - 7));
        if (Math.Round(next) != Math.Round(_privateMembersScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _privateMembersScroll = next;
        // 005E4AC8..005E4ACE consumes GDSlider's float with __ftol, without +0.5.
        _privateMembersPage = (int)next;
    }

    private async Task RequestPrivateMembersAsync()
    {
        if (PrivateChannelRequested is null) return;
        try
        {
            if (!await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.ListChannelMembers, _privateChannel, null))
            { Error = "Unable to list the channel members."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
    }
    private string PrivateMembersLabel(string attach)
    {
        if (attach.Equals("TITLE", StringComparison.OrdinalIgnoreCase)) return "Channel Members";
        if (attach.StartsWith("BUTTON", StringComparison.OrdinalIgnoreCase))
            return attach.ToUpperInvariant() switch { "BUTTON01" => "Gag", "BUTTON02" => "Ungag", "BUTTON03" => "Ban",
                "BUTTON04" => "Kick", "BUTTON05" => "Transfer", "BUTTON06" => "Close", _ => "" };
        if (attach.StartsWith("NAME", StringComparison.OrdinalIgnoreCase) && int.TryParse(attach.AsSpan(4), out int index) &&
            index < 7 && index + _privateMembersPage < PrivateMembers.Length)
            return PrivateMembers[index + _privateMembersPage];
        return "";
    }
    private async Task PrivateMemberButtonAsync(int button)
    {
        if (button == 6) { CloseChatDialog(); return; }
        if (_privateMemberSelection is not { } member || PrivateMemberRequested is null ||
            !DialogPieceEnabled("privatemembers", $"BUTTON{button:00}")) return;
        var kind = button switch { 1 => EnbNext.Net7.Social.Net7ClientChatRequestKind.Gag,
            2 => EnbNext.Net7.Social.Net7ClientChatRequestKind.Ungag, 3 => EnbNext.Net7.Social.Net7ClientChatRequestKind.Ban,
            4 => EnbNext.Net7.Social.Net7ClientChatRequestKind.Kick, 5 => EnbNext.Net7.Social.Net7ClientChatRequestKind.Owner,
            _ => throw new ArgumentOutOfRangeException(nameof(button)) };
        await PrivateMemberRequested(kind, member, _privateChannel);
    }
    private bool RejectPendingPrivateRequest(string operation)
    {
        if (!_privateRequestPending) return false;
        if (_session() is { } character)
            LocalChatMessageRequested?.Invoke(character, 19, operation switch
            {
                "create" => "Another request is pending. Cannot create a new channel.",
                "leave" => "Another request is pending. Cannot leave the channel.",
                _ => "Another request is pending. Cannot join a new channel. ",
            });
        return true;
    }
    private async Task CreatePrivateChannelAsync()
    {
        if (PrivateChannelRequested is null || RejectPendingPrivateRequest("create")) return;
        long generation = ++_privateRequestGeneration;
        _privateRequestPending = true;
        bool queued = false;
        CloseChatDialog();
        try
        {
            if (_privateChannel.Length > 0 &&
                !await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.ExitChannel, _privateChannel, null))
            { Error = "Unable to leave the current private channel."; Publish(); return; }
            _privateCreateRevision = _data?.CreatedChannelRevision ?? 0;
            if (await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.CreateChannel, null, null))
            {
                queued = true;
                _awaitingPrivateCreate = true;
            }
            else { Error = "Unable to create the private channel."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
        finally { if (!queued && generation == _privateRequestGeneration) _privateRequestPending = false; }
    }
    private async Task SubmitPrivateControlAsync(EnbNext.Net7.Social.Net7ClientChatRequestKind kind, string? channel, string? value = null)
    {
        if (PrivateChannelRequested is null || (kind == EnbNext.Net7.Social.Net7ClientChatRequestKind.ExitChannel && RejectPendingPrivateRequest("leave"))) return;
        try
        {
            if (await PrivateChannelRequested(kind, channel, value)) CloseChatDialog();
            else { Error = "Unable to update the private channel."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
    }
    private string PrivateJoinLabel(string attach, string authored) => attach.ToUpperInvariant() switch
    {
        "TITLE" => _privateJoinNotFound ? "Channel Does not exist" : "Join Private Channel",
        "MAIN_TEXT" => _privateJoinNotFound ? "The channel you have selected does not exist. Please enter another channel." : "Enter the channel number you wish to join.",
        _ => authored,
    };
    private string PrivatePasswordLabel(string attach, string authored) => _privatePasswordMode < 2
        ? attach.ToUpperInvariant() switch
        {
            "TITLE" => _privatePasswordMode == 0 ? "Enter Password" : "Password Incorrect",
            "MAIN_TEXT" => _privatePasswordMode == 0 ? "This Channel Requires a password."
                : "The password you entered is not correct. Please try reentering the password again.",
            "PASSWORD_TEXT" => "Password",
            "VARIFY_TEXT" => "",
            _ => authored,
        }
        : attach.ToUpperInvariant() switch
    {
        "TITLE" => _privatePasswordMismatch is null ? "Create Password" :
            _privatePasswordMismatch.Value ? "Passwords do not match" : "Invalid Passwords entered",
        "MAIN_TEXT" => _privatePasswordMismatch is null ? "" : _privatePasswordMismatch.Value
            ? "The passwords did not match. Please try re-entering them again."
            : "You have entered invalid passwords. Please try re-entering them again.",
        "PASSWORD_TEXT" => "Enter Password",
        "VARIFY_TEXT" => "Verify Password",
        _ => authored,
    };
    private void OpenPrivatePassword() => OpenPrivatePasswordMode(2);
    private void OpenPrivatePasswordMode(int mode)
    {
        _wrappedLabels.Clear();
        // 00566AF0 constructs a new 005E6290 owner for each password request.
        _passwordInput.InsertMode = _verifyInput.InsertMode = true;
        _privatePasswordMode = mode;
        _privateJoinEntry = false;
        _dialogAlpha.Clear(); _dialogAlphaActive = false;
        _chatDialog = ChatDialog.PrivatePassword; _privatePasswordEntry = true; _privatePasswordMismatch = null;
        // 005E6456/005E64DE: eight-character gadgets; 005E648D/005E650D: mask enabled.
        _editor.Use(_passwordInput); _editor.PasswordChar = '*';
        _editor.IsVisible = true; _editor.Text = ""; _editor.CaretIndex = 0;
        _passwordVerifyEditor.IsVisible = mode == 2; _passwordVerifyEditor.Text = "";
        InputOpened?.Invoke(); Publish();
        Defer(() =>
        {
            if (_privatePasswordEntry && _chatDialog == ChatDialog.PrivatePassword) _editor.Focus();
        });
    }
    private async Task ConfirmPrivatePasswordAsync()
    {
        string password = _editor.Text ?? "";
        if (_privatePasswordMode < 2)
        {
            // 005E6A20 closes first; 005E6EC0 submits even an empty password.
            string channel = _privateJoinChannel;
            CloseChatDialog();
            if (PrivateChannelRequested is null) return;
            try
            {
                if (!await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.EnterChannel, channel, password))
                { Error = "Unable to join the private channel."; Publish(); }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
            { Error = ex.Message; Publish(); }
            return;
        }
        if (password.Length == 0 || password != (_passwordVerifyEditor.Text ?? ""))
        {
            _privatePasswordMismatch = password.Length > 0;
            InteractionSoundRequested?.Invoke("cost.wav");
            _editor.Text = _passwordVerifyEditor.Text = ""; _editor.Focus(); Publish(); return;
        }
        if (PrivateChannelRequested is null) return;
        try
        {
            if (await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.Password, _privateChannel, password)) CloseChatDialog();
            else { Error = "Unable to set the private channel password."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
    }
    private void CancelPrivatePassword()
    {
        int mode = _privatePasswordMode;
        CloseChatDialog();
        if (mode < 2) OpenPrivateJoin();
        else
        {
            _chatDialog = ChatDialog.Private;
            _privatePanelState = 3;
            Publish();
        }
    }
    private void PublishPrivatePasswordEntry()
    {
        int imageStart = _images.Count;
        foreach (var (name, editor) in new[] { ("TEXT_FIELD00", _editor), ("TEXT_FIELD01", _passwordVerifyEditor) })
        {
            if (_privatePasswordMode < 2 && name == "TEXT_FIELD01") continue;
            if (!_dialogDesign.TryGetValue("privatepassword/" + name, out var field)) continue;
            var input = DialogScreen(field.Deflate(2));
            editor.Bounds = input;
            DrawDialogInput("privatepassword", name, input, editor);
            Hit("chatdialog/privatepassword/" + name, DialogScreen(field), () => FocusDialogEditor(editor));
        }
        AttachImagesToLastDraw(imageStart);
    }
    private void DrawPrivatePasswordInput(string mesh, Rect clip, EnbUiTextInput editor)
        => DrawDialogInput("privatepassword", mesh, clip, editor);

    private void DrawDialogInput(string prefix, string mesh, Rect clip, EnbUiTextInput editor)
    {
        if (!_dialogLayouts.TryGetValue(prefix, out var layout) ||
            !_dialogAssets.TryGetValue(prefix, out var asset)) return;
        var definition = layout.Definitions.FirstOrDefault(d =>
            d.Properties.GetValueOrDefault("Attach", "").Equals(prefix == "ignoreentry" ? "value_button" : mesh, StringComparison.OrdinalIgnoreCase));
        if (definition is null || !definition.Properties.TryGetValue("FontType", out var fontId) || Font(fontId) is not { } font) return;
        var pivot = asset.Hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals(definition.Properties["Attach"], StringComparison.OrdinalIgnoreCase));
        if (pivot is null) return;
        var point = EnbCockpitUi.ResolveTransforms(asset)[pivot.Ordinal].Translation;
        double unitX = _size.Width / 640, unitY = _size.Height / 480;
        double Measure(string value) => value.Length == 0 ? 0 : font.MeasureWidth(value) * font.Scale;
        // 006AA340 masks a display copy, leaving the submitted buffer intact.
        string text = editor.PasswordChar == '\0' ? editor.Text ?? "" : new(editor.PasswordChar, (editor.Text ?? "").Length);
        int caret = Math.Clamp(editor.CaretIndex, 0, text.Length);
        // Ignore owns the same 00649AA0 TEXT_BOX gadget as Friends/Guild.
        // 006AAD10 windows its display against that retained mesh, not the
        // padded input hit rectangle. The submitted name remains complete.
        if (prefix == "ignoreentry" && _dialogDesign.TryGetValue("ignoreentry/TEXT_BOX", out var box))
        {
            var window = EnbNext.Rendering.EnbRetailChatInputViewport.Resolve(text, caret, (float)box.Width,
                value => (float)(Measure(value) / unitX),
                c => (float)(font.CharacterAdvance(c) * font.Scale / unitX));
            text = text.Substring(window.Start, window.Length);
            caret = Math.Clamp(caret - window.Start, 0, text.Length);
        }
        double width = Measure(text), height = font.CellHeight * font.Scale;
        var anchor = DialogScreen(new(point.X + 320, 240 - point.Y, 0, 0));
        double x = anchor.X, y = anchor.Y;
        string justify = definition.Properties.GetValueOrDefault("Justify", "");
        if (justify.EndsWith(",CENTER", StringComparison.OrdinalIgnoreCase)) x -= width / 2;
        if (justify.StartsWith("CENTER,", StringComparison.OrdinalIgnoreCase)) y -= height / 2;
        string colorId = definition.Properties.GetValueOrDefault(editor.IsFocused ? "AltColor" : "Color", "");
        Text(text, new(x, y, Math.Max(width, 1), height), _chatLabelColors.GetValueOrDefault(colorId, new(1, 1, 1)), fontId);
        if (!editor.IsFocused) return;
        // 00685AB0: current glyph (or 'n' at end) width + 1, minimum 2;
        // insertion underline is 2 design units high at the bottom of the font.
        double glyphWidth = font.CharacterAdvance(caret < text.Length ? text[caret] : 'n') * font.Scale;
        var rectangle = EnbNext.Rendering.EnbRetailCaretGeometry.Resolve(new((float)(anchor.X / unitX), (float)(-anchor.Y / unitY)),
            (float)(width / unitX), (float)(Measure(text[..caret]) / unitX), (float)(glyphWidth / unitX), (float)(height / unitY),
            justify.EndsWith(",CENTER", StringComparison.OrdinalIgnoreCase), justify.StartsWith("CENTER,", StringComparison.OrdinalIgnoreCase), !EditorOverwrite(editor));
        var bounds = new Rect(rectangle.X * unitX, -rectangle.Y * unitY, rectangle.Z * unitX, rectangle.W * unitY);
        // 00685680 constructs RGBA (0, .7, .1, .5).
        _images.Add(new(Pixel(0, 178, 25, 127), (bounds)) { Clip = (clip) });
    }
    private void FocusDialogEditor(EnbUiTextInput editor)
    {
        // A pointer press on the transparent hit region can move focus again
        // during the platform's pointer default action. Focus after that action.
        editor.Focus();
        Defer(() =>
        {
            if (editor.IsVisible && _chatDialog is ChatDialog.PrivateJoin or ChatDialog.PrivatePassword or ChatDialog.Ignore)
                editor.Focus();
        });
    }
    private void OpenPrivateJoin() => OpenPrivateJoinWithStatus(false);
    private void OpenPrivateJoinWithStatus(bool channelNotFound)
    {
        _wrappedLabels.Clear();
        // 00566AF0 constructs the 005EAB40 owner again, including retry mode.
        _joinInput.InsertMode = true;
        _dialogAlpha.Clear(); _dialogAlphaActive = false;
        _privateJoinNotFound = channelNotFound;
        _chatDialog = ChatDialog.PrivateJoin; _privateJoinEntry = true;
        // 005EAC50 -> 006AA810 -> 0084AA10: four-character input buffer.
        _editor.Use(_joinInput); _editor.PasswordChar = '\0';
        _editor.IsVisible = true; _editor.Text = ""; _editor.CaretIndex = 0;
        InputOpened?.Invoke(); Publish();
        Defer(() =>
        {
            if (_privateJoinEntry && _chatDialog == ChatDialog.PrivateJoin) _editor.Focus();
        });
    }
    private void ReturnToPrivateChoice()
    {
        _wrappedLabels.Clear();
        _dialogAlpha.Clear(); _dialogAlphaActive = false;
        _chatDialog = ChatDialog.Private; _privatePanelState = 1;
        _privateJoinEntry = false; _editor.IsVisible = false; _editor.Text = ""; Publish();
    }
    private async Task ConfirmPrivateJoinAsync()
    {
        string number = _editor.Text ?? ""; //005EB2F0 queues the literal buffer, including empty.
        if (PrivateChannelRequested is null || RejectPendingPrivateRequest("join")) return;
        long generation = ++_privateRequestGeneration;
        string previousChannel = _privateJoinChannel;
        _privateRequestPending = true;
        bool queued = false;
        CloseChatDialog();
        try
        {
            if (_privateChannel.Length > 0 &&
                !await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.ExitChannel, _privateChannel, null))
            { Error = "Unable to leave the current private channel."; Publish(); return; }
            // Retain the request before awaiting the asynchronous transport: a
            // response may already be dispatched when the send continuation runs.
            _privateJoinChannel = number;
            if (await PrivateChannelRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.EnterChannel, number, null))
            {
                queued = true;
            }
            else { Error = "Unable to join the private channel."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
        finally
        {
            if (!queued && generation == _privateRequestGeneration)
            { _privateRequestPending = false; _privateJoinChannel = previousChannel; }
        }
    }
    private void PublishPrivateJoinEntry()
    {
        if (!_dialogDesign.TryGetValue("privatejoin/TEXT_FIELD", out var field)) return;
        var input = DialogScreen(field.Deflate(2));
        _editor.Bounds = input;
        int imageStart = _images.Count;
        DrawDialogInput("privatejoin", "TEXT_FIELD", input, _editor);
        AttachImagesToLastDraw(imageStart);
        Hit("chatdialog/privatejoin/TEXT_FIELD", DialogScreen(field), () => FocusDialogEditor(_editor));
    }
    private Vector DialogOffset => _publishingIgnoreEntry ? _ignoreEntryOffset : _chatDialog == ChatDialog.Ignore ? _ignoreOffset : _chatDialog == ChatDialog.Commands ? _commandsOffset : default;
    private Rect DialogScreen(Rect design) => new((design.X + DialogOffset.X) * _size.Width / 640, (design.Y + DialogOffset.Y) * _size.Height / 480,
        design.Width * _size.Width / 640, design.Height * _size.Height / 480);

    private void DragIgnoreDialog(bool child, Vector delta)
    {
        string prefix = child ? "ignoreentry/" : "ignore/";
        var bounds = _dialogDesign.Where(p => p.Key.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p.Value).Aggregate((a, b) => a.Union(b));
        var old = child ? _ignoreEntryOffset : _ignoreOffset;
        // MovableUIBackGadget 006748F0 rounds design-space displacement and
        // clamps the render-object extents inside (-320,-240)..(320,240).
        var next = new Vector(Math.Clamp(Math.Round(old.X + delta.X * 640 / _size.Width), -bounds.Left, 640 - bounds.Right),
            Math.Clamp(Math.Round(old.Y + delta.Y * 480 / _size.Height), -bounds.Top, 480 - bounds.Bottom));
        if (child) _ignoreEntryOffset = next; else _ignoreOffset = next;
    }

    private float DialogBlinkOpacity(bool hovered)
    {
        if (!hovered) return 1;
        float phase = Math.Max(0, Environment.TickCount64 - _hoverStarted) % _chatBlinkPeriod / (float)_chatBlinkPeriod;
        return phase <= .5f ? phase * 2 : 2 - phase * 2;
    }

    private void PublishDialogLabels(string prefix, EnbAuthoredUiAsset asset)
    {
        if (!_dialogLayouts.TryGetValue(prefix, out var layout)) return;
        if (prefix == "commands") { PublishCommandLabels(asset, layout); return; }
        int nameIndex = 0;
        if (prefix == "ignore") _ignoreOpeningActive = false;
        _dialogAlphaActive = false;
        foreach (var section in layout.Definitions)
        {
            var properties = section.Properties;
            if (!properties.TryGetValue("Attach", out string? attach) || !properties.TryGetValue("FontType", out string? fontId)) continue;
            var pivot = asset.Hierarchy.Pivots.FirstOrDefault(value => value.Name.Equals(attach, StringComparison.OrdinalIgnoreCase));
            if (pivot is null || Font(fontId) is not { } font) continue;
            string text = properties.GetValueOrDefault("String0", "");
            if (prefix == "ignore" && attach.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                text = nameIndex + (int)_ignoreScroll < IgnoreRows.Length ? IgnoreRows[nameIndex++ + (int)_ignoreScroll] : "";
            if (prefix == "private") text = PrivatePanelLabel(attach);
            if (prefix == "privatemembers") text = PrivateMembersLabel(attach);
            if (prefix == "privatejoin") text = PrivateJoinLabel(attach, text);
            if (prefix == "privatepassword") text = PrivatePasswordLabel(attach, text);
            if (prefix == "ignoreentry" && attach.Equals("title", StringComparison.OrdinalIgnoreCase)) text = "Ignore Player";
            if (prefix == "ignoreentry" && attach.Equals("value_button", StringComparison.OrdinalIgnoreCase)) continue;
            EnbAttachedLabelWipe? opening = null;
            if (prefix == "ignore" && properties.GetValueOrDefault("EffectOn", "").Contains("FA_WIPE_ON_RIGHT", StringComparison.Ordinal))
            {
                if (!_ignoreOpening.TryGetValue(section.StringId, out opening)) _ignoreOpening[section.StringId] = opening = new();
                // 005D9B10 updates all seven labels with SetText(..., 0), including blanks.
                if (text.Length == 0) opening.Update(CommandsClock(), 0, (float)_commandsWipeSpeed);
            }
            if (text.Length == 0) continue;
            var point = EnbCockpitUi.ResolveTransforms(asset)[pivot.Ordinal].Translation;
            double width = font.MeasureWidth(text) * font.Scale;
            var anchor = DialogScreen(new(point.X + 320, 240 - point.Y, 0, 0));
            double x = anchor.X;
            string justify = properties.GetValueOrDefault("Justify", "");
            if (!justify.EndsWith("LEFT", StringComparison.OrdinalIgnoreCase)) x -= width / 2;
            var color = properties.GetValueOrDefault("Color", "") switch
            {
                "FA_GREEN" => new EnbRgb(0, 1, 0), "FA_YELLOW" => new EnbRgb(1, 1, 0),
                "FA_GRAY" => new EnbRgb(.5f, .5f, .5f), _ => new EnbRgb(1, 1, 1),
            };
            string hoverId = prefix == "ignore" && attach.StartsWith("name", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(attach.AsSpan(4), out int nameSlot) ? "chatdialog/ignore/name" + nameSlot :
                "chatdialog/" + (prefix == "ignoreentry" ? "ignore/entry" : prefix) + "/" + attach;
            bool hovered = _hover.Equals(hoverId, StringComparison.OrdinalIgnoreCase);
            bool alternateSelected = prefix == "ignore" && attach.StartsWith("name", StringComparison.OrdinalIgnoreCase) &&
                (hovered || int.TryParse(attach.AsSpan(4), out int ignoreRow) && ignoreRow + (int)_ignoreScroll == _ignoreSelectionIndex) ||
                prefix == "privatemembers" && attach.StartsWith("name", StringComparison.OrdinalIgnoreCase) &&
                (hovered || int.TryParse(attach.AsSpan(4), out int memberRow) && memberRow + _privateMembersPage == _privateMemberSelectionIndex);
            color = _chatLabelColors.GetValueOrDefault(properties.GetValueOrDefault(alternateSelected ? "AltColor" : "Color", ""), color);
            if (hovered && properties.GetValueOrDefault("MouseOver") == "FA_CYCLE" &&
                _chatLabelColors.TryGetValue(properties.GetValueOrDefault("AltColor", ""), out var alternateColor))
                // 005D9730 selects +44C for row hover/selection before 006CA0C0 case18.
                color = EnbAttachedLabelCycle.Color(color, alternateColor, Environment.TickCount64 - _hoverStarted, _chatCycleSpeed);
            double height = font.CellHeight * font.Scale;
            float opacity = properties.GetValueOrDefault("MouseOver") == "FA_BLINK" ? DialogBlinkOpacity(hovered) : 1;
            if (_dialogAlphaStyles.TryGetValue(prefix + "/" + section.StringId, out var alphaStyle))
            {
                if (!_dialogAlpha.TryGetValue(prefix + "/" + section.StringId, out var alpha)) _dialogAlpha[prefix + "/" + section.StringId] = alpha = new();
                opacity = alpha.Update(CommandsClock(), hovered, alphaStyle.Initial, alphaStyle.Target, alphaStyle.Fade, alphaStyle.Blink, alphaStyle.Revert);
                _dialogAlphaActive |= !alpha.OpeningComplete && alphaStyle.Fade > 0 || opacity < 1 || hovered && alphaStyle.Blink > 0;
            }
            if (section.StringId.StartsWith("WRAP_", StringComparison.OrdinalIgnoreCase))
            {
                PublishWrappedDialogLabel(prefix, section.StringId, properties, text, point, font, color, opacity);
                continue;
            }
            int imageStart = _images.Count;
            Text(text, new(x, anchor.Y - height / 2, Math.Max(width + 4, 10), height), color, fontId, opacity: opacity);
            if (opening is not null && _images.Count > imageStart)
            {
                var image = _images[imageStart];
                int extent = opening.Update(CommandsClock(), (int)Math.Round(image.Bounds.Width * _scale), (float)_commandsWipeSpeed);
                _images[imageStart] = image with { Clip = (image.Clip ?? image.Bounds).Intersect(new Rect(image.Bounds.X, image.Bounds.Y, extent / _scale, image.Bounds.Height)) };
                _ignoreOpeningActive |= !opening.Complete;
            }
        }
        // CHT04 STR-2 has an intentionally blank StringID. The generic catalog
        // omits it, but retail still renders its authored String0 at PASSWORD_TEXT.
        if (prefix == "privatepassword" && _privatePasswordPrompt.Length > 0 && Font("FONT_SMALL") is { } passwordFont &&
            asset.Hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals("PASSWORD_TEXT", StringComparison.OrdinalIgnoreCase)) is { } passwordPivot)
        {
            var point = EnbCockpitUi.ResolveTransforms(asset)[passwordPivot.Ordinal].Translation;
            double width = passwordFont.MeasureWidth(_privatePasswordPrompt) * passwordFont.Scale;
            Text(_privatePasswordPrompt, new((point.X + 320) * _size.Width / 640, (240 - point.Y) * _size.Height / 480 - 10,
                Math.Max(width + 4, 10), 20), new(1, 1, 1), "FONT_SMALL", centerVertically: true);
        }
    }

    private void AddDialogHits(string prefix)
    {
        foreach (var pair in _dialogDesign.Where(pair => pair.Key.StartsWith(prefix + "/", StringComparison.Ordinal)))
        {
            string mesh = pair.Key[(prefix.Length + 1)..];
            if (!DialogPieceVisible(prefix, mesh)) continue;
            Action? action = null;
            if (mesh.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(5), out int index))
                action = () => ToggleDialogCheck(prefix, index);
            else if (mesh.Equals("OK", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); if (prefix == "channels") _ = CommitChannelsAsync(); else CommitMessages(); };
            else if (mesh.Equals("CANCEL", StringComparison.OrdinalIgnoreCase) || mesh.Equals("EXIT", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); CloseChatDialog(); };
            else if (prefix == "commands" && mesh.Equals("ADD", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); CloseChatDialog(); };
            else if (prefix == "ignore" && mesh.Equals("BUTTON01", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); OpenIgnoreEntry(); };
            else if (prefix == "ignore" && mesh.Equals("BUTTON02", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); _ = RemoveIgnoredPlayerAsync(); };
            else if (prefix == "ignore" && mesh.Equals("BUTTON03", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); _ = IgnoreSelectedTargetAsync(); };
            else if (prefix == "private" && mesh.StartsWith("BUTTON", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(6), out int button))
                action = () => { PlayRollSelect(); PrivatePanelButton(button); };
            else if (prefix == "privatemembers" && mesh.StartsWith("BUTTON", StringComparison.OrdinalIgnoreCase) && int.TryParse(mesh.AsSpan(6), out int memberButton))
                action = () => { PlayRollSelect(); _ = PrivateMemberButtonAsync(memberButton); };
            else if (prefix == "privatejoin" && mesh.Equals("BUTTON01", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); ReturnToPrivateChoice(); };
            else if (prefix == "privatejoin" && mesh.Equals("BUTTON03", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); _ = ConfirmPrivateJoinAsync(); };
            else if (prefix == "privatepassword" && mesh.Equals("BUTTON02", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); CancelPrivatePassword(); };
            else if (prefix == "privatepassword" && mesh.Equals("BUTTON03", StringComparison.OrdinalIgnoreCase)) action = () => { PlayRollSelect(); _ = ConfirmPrivatePasswordAsync(); };
            if (action is not null)
            {
                string id = "chatdialog/" + prefix + "/" + mesh;
                Hit(id, DialogScreen(pair.Value), DialogPieceEnabled(prefix, mesh) ? action : () => { });
            }
        }
        if (prefix == "ignore" && _dialogDesign.TryGetValue("ignore/FRAME_DETAIL01", out var ignoreFrame))
            Hit("chatdialog/ignore/list", DialogScreen(ignoreFrame), () => { }, wheel: delta => { SetIgnoreScroll(_ignoreScroll + (delta > 0 ? -1 : 1)); Publish(); });
        if (prefix == "privatemembers" && _dialogDesign.TryGetValue("privatemembers/FRAME_DETAIL01", out var membersFrame))
            Hit("chatdialog/privatemembers/list", DialogScreen(membersFrame), () => { }, wheel: delta => ScrollPrivateMembers(delta > 0 ? -1 : 1));
        if (prefix is not ("ignore" or "privatemembers") || !_dialogAssets.TryGetValue(prefix, out var asset)) return;
        var transforms = EnbCockpitUi.ResolveTransforms(asset);
        for (int i = 0; i < Math.Min(7, prefix == "ignore" ? IgnoreRows.Length - (int)_ignoreScroll : PrivateMembersCount() - _privateMembersPage); i++)
        {
            int index = i;
            var pivot = asset.Hierarchy.Pivots.FirstOrDefault(value => value.Name.Equals($"name{i:00}", StringComparison.OrdinalIgnoreCase));
            if (pivot is null) continue;
            var point = transforms[pivot.Ordinal].Translation;
            Hit("chatdialog/" + prefix + (prefix == "ignore" ? "/name" + i : $"/NAME{i:00}"), DialogScreen(new(point.X + 320,
                240 - point.Y - 8, 180, 16)),
                () => { PlayFontClick(); if (prefix == "ignore") { _ignoreSelectionIndex = index + (int)_ignoreScroll; _ignoreSelection = IgnoreRows[_ignoreSelectionIndex]; }
                    else { _privateMemberSelectionIndex = index + _privateMembersPage; _privateMemberSelection = PrivateMembers[_privateMemberSelectionIndex]; } Publish(); },
                wheel: delta => { SetChatListScroll(ChatListScroll + (delta > 0 ? -1 : 1)); Publish(); });
        }
    }

    private void ToggleDialogCheck(string prefix, int index)
    {
        string? option = prefix == "channels" && index < ChannelOptions.Length ? ChannelOptions[index].Option
            : prefix == "messages" && index < MessageOptions.Length ? MessageOptions[index].Option : null;
        if (option is not null) _dialogValues[option] = !_dialogValues.GetValueOrDefault(option);
        PlayFontClick();
        Publish();
    }
    private void SetIgnoreScroll(double value)
    {
        double next = Math.Clamp(value, 0, Math.Max(0, IgnoreRows.Length - 7));
        if (Math.Round(next) != Math.Round(_ignoreScroll)) InteractionSoundRequested?.Invoke("slider_bar_1.wav");
        _ignoreScroll = next;
    }

    private async Task RequestIgnoredAsync()
    {
        if (IgnorePlayerRequested is null) return;
        try
        {
            if (!await IgnorePlayerRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.ListIgnores, ""))
            { Error = "Unable to list ignored players."; Publish(); }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { Error = ex.Message; Publish(); }
    }

    private async Task RemoveIgnoredPlayerAsync()
    {
        if (_ignoreSelection is not { Length: > 0 } player || IgnorePlayerRequested is null) return;
        if (await IgnorePlayerRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.Unignore, player))
        { Publish(); }
    }
    private async Task IgnoreSelectedTargetAsync()
    {
        if (_ignoreTarget is not { } player || IgnorePlayerRequested is null) return;
        if (await IgnorePlayerRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.Ignore, player)) Publish();
    }

    private void PublishIgnoreListFrame()
    {
        if (!_pingFrames.TryGetValue("DIALOG_BOX_INFO", out var frame) ||
            !_dialogDesign.TryGetValue("ignore/FRAME_DETAIL01", out var list)) return;
        PingFrame("ignore/list", frame, list.Translate(_ignoreOffset.X, _ignoreOffset.Y));
    }

    private void OpenIgnoreEntry()
    {
        foreach (string key in _dialogAlpha.Keys.Where(k => k.StartsWith("ignoreentry/", StringComparison.Ordinal)).ToArray()) _dialogAlpha.Remove(key);
        _ignoreEntryOffset = default; _editor.Use(_ignoreInput); _editor.PasswordChar = '\0';
        _ignoreEntry = true; _editor.IsVisible = true; _editor.Text = ""; _editor.CaretIndex = 0;
        InputOpened?.Invoke(); Publish();
        Defer(() => { if (_ignoreEntry) _editor.Focus(); });
    }

    private void CloseIgnoreEntry()
    {
        _ignoreEntry = false; _editor.IsVisible = false; _editor.Text = ""; Publish();
    }

    private async Task ConfirmIgnoreEntryAsync()
    {
        string player = (_editor.Text ?? "").Trim();
        if (player.Length == 0 || IgnorePlayerRequested is null) { CloseIgnoreEntry(); return; }
        if (await IgnorePlayerRequested(EnbNext.Net7.Social.Net7ClientChatRequestKind.Ignore, player)) CloseIgnoreEntry();
    }

    private void PublishIgnoreEntry()
    {
        // 005D9100 -> 00649890/00649AA0 uses TRADE01A, not a sized
        // dynamic-window approximation. Preserve its authored meshes/anchors.
        if (!_dialogAssets.TryGetValue("ignoreentry", out var asset)) return;
        _publishingIgnoreEntry = true;
        try
        {
            var parts = _dialogDesign.Where(p => p.Key.StartsWith("ignoreentry/", StringComparison.Ordinal)).ToArray();
            Hit("chatdialog/ignore/entry/drag", DialogScreen(parts.Select(p => p.Value).Aggregate((a, b) => a.Union(b))), () => { });
            foreach (var pair in parts.OrderBy(p => DialogPieceOrder(p.Key)))
            {
                string mesh = pair.Key["ignoreentry/".Length..];
                string control = mesh.ToLowerInvariant();
                string id = "chatdialog/ignore/entry/" + (control == "exit" ? "close" : control == "text_box" ? "input" : control);
                _draws.Add(new(_dialogPieces[pair.Key], 0, (DialogScreen(pair.Value)), _hover.Equals(id, StringComparison.OrdinalIgnoreCase))
                    { LayoutGroup=EnbUiGroup.Chat, GadgetOwner = _hits.GetValueOrDefault(id), DesignBounds = (pair.Value.Translate(-320, -240)) });
                if (control is "cancel" or "exit") Hit(id, DialogScreen(pair.Value), () => { PlayRollSelect(); CloseIgnoreEntry(); });
                if (control == "confirm") Hit(id, DialogScreen(pair.Value), () => { PlayRollSelect(); _ = ConfirmIgnoreEntryAsync(); });
                if (control == "text_box")
                {
                    var input = DialogScreen(pair.Value.Deflate(2));
                    _editor.Bounds = input;
                    Hit(id, DialogScreen(pair.Value), () => FocusDialogEditor(_editor));
                }
            }
            int first = _images.Count;
            PublishDialogLabels("ignoreentry", asset);
            if (_dialogDesign.TryGetValue("ignoreentry/TEXT_BOX", out var field))
                DrawDialogInput("ignoreentry", "TEXT_BOX", DialogScreen(field.Deflate(2)), _editor);
            AttachImagesToLastDraw(first);
        }
        finally { _publishingIgnoreEntry = false; }
    }

    private void PublishReplyPopup(double chatX, double chatY, double unit)
    {
        if (_modernChat && MdFrames() is not null) { PublishModernReplyPopup(); return; }
        if (!_replyPopup || _data is null || !_pingFrames.TryGetValue("SYMBOL_CHAT_WINDOW", out var window) ||
            !_pingFrames.TryGetValue("SYMBOL_CHAT_SECTION", out var section)) return;
        double row = 16, width = Math.Max(39, _data.RecentPeers.Max(peer =>
            (Font("FONT_SMALL")?.MeasureWidth(peer) ?? 24)  / (_size.Width / 640) + 12));
        double x = chatX / (_size.Width / 640), y = chatY / (_size.Height / 480) + 52;
        PingFrame("reply/window", window, new(x, y, width + 8, _data.RecentPeers.Length * row + 8));
        PingFrame("reply/section", section, new(x + 4, y + 4, width, _data.RecentPeers.Length * row));
        int labelStart = _images.Count;
        for (int i = 0; i < _data.RecentPeers.Length; i++)
        {
            string peer = _data.RecentPeers[i];
            var design = new Rect(x + 4, y + 4 + i * row, width, row);
            var screen = DialogScreen(design);
            double textWidth = (Font("FONT_SMALL")?.MeasureWidth(peer) ?? 24) ;
            Text(peer, new(screen.Center.X - textWidth / 2, screen.Y, textWidth + 2, screen.Height),
                _hover == "replypopup/" + i ? new(1, 0, 0) : new(1, 1, 0), "FONT_SMALL", true);
            Hit("replypopup/" + i, DialogScreen(design), () => { PlayFontClick(); SelectReply(peer); }, () => { PlayFontClick(); SelectReply(peer); }); // 0056E7F0: right press selects too (CHAT-D32)
        }
        AttachImagesToLastDraw(labelStart);
    }

    private void AttachImagesToLastDraw(int first)
    {
        if (_draws.Count == 0 || _images.Count == first) return;
        var labels = _images.Skip(first).ToImmutableArray();
        while (_images.Count > first) _images.RemoveAt(_images.Count - 1);
        _draws[^1] = _draws[^1] with { Images = _draws[^1].Images.AddRange(labels) };
    }

    private void TextInCenteredButton(string label, Rect design)
    {
        if (Font("FONT_SMALL") is not { } font) return;
        var screen = DialogScreen(design);
        double width = font.MeasureWidth(label) * font.Scale;
        Text(label, new(screen.Center.X - width / 2, screen.Y, width + 2, screen.Height),
            new(1, 1, 0), "FONT_SMALL", true);
    }
}
