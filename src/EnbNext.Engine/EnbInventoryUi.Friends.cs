using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Net7.Social;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _friendsOpen, _friendsByLocation = true, _friendEditing;
    private int _friendsPage;
    private (Guid Stream,long Revision)? _friendListSeen;
    private string? _friendSelected;
    private EnbUiTextInput? _friendInput;
    private bool _friendOverwrite { get=>_friendInput is {} input && !input.Editor.InsertMode; set {if(_friendInput is {} input)input.Editor.InsertMode=!value;} }
    private readonly Dictionary<int,long> _friendClickTimes=[];
    private IReadOnlyDictionary<string, string>? _friendTips;
    private readonly Dictionary<string, EnbRgb> _friendColors = [];
    private readonly Dictionary<string, float> _friendCycleSpeeds = [];
    private readonly Dictionary<string, EnbAttachedTextStyle> _socialTextStyles = [];
    private readonly Dictionary<string, EnbAttachedLabelScroll> _socialTextScroll = [];
    private readonly Dictionary<string, EnbAttachedLabelWipe> _friendOpening = [];
    public Func<EnbFriendsRequest, Task<bool>>? FriendsRequested { get; set; }
    public event Action<string>? FriendMessageRequested;
    public Action<uint,byte,string>? LocalChatMessageRequested {get;set;}
    internal bool FriendsEditing => _friendEditing;
    private EnbSceneChatSnapshot? FriendsData => _scene?.Hud.Chat;
    private Net7FriendEntry[] FriendRows => (FriendsData?.Friends ?? []).OrderBy(f => _friendsByLocation && f.IsOffline)
        .ThenBy(f => _friendsByLocation ? f.Presence : f.Identity, StringComparer.Ordinal)
        .ThenBy(f => f.Identity, StringComparer.Ordinal).ToArray();
    private string? FriendTarget => _scene?.ActiveStarbaseId is null
        ? _scene?.Objects.FirstOrDefault(o => o.IsCurrentTarget && o.ObjectType == 1) is {} target ? EnbRetailObjectDisplayName.Recipient(target) : null : null;

    internal async Task OpenFriends()
    {
        if (!_interactive || _friendsOpen || FriendsRequested is null || _owner is not uint owner) return;
        // 00569A00 allocates a fresh 0058F210 parent and its string-entry child.
        _friendOverwrite = false;
        CloseAll(); _socialSliders.Remove("friends"); _friendOpening.Clear(); _friendListSeen=null; _friendsOpen = true; _friendsPage = 0; _friendSelected = null;
        Opened?.Invoke(); Publish();
        await Send(() => FriendsRequested(new(owner, _ship, EnbFriendsAction.Open)));
        Publish();
    }
    private void CloseFriends()
    {
        bool open = _friendsOpen; _friendsOpen = _friendEditing = false;
        if (_friendInput is not null) _friendInput.IsVisible = false;
        if (open && _owner is uint owner && FriendsRequested is {} send) _ = send(new(owner, _ship, EnbFriendsAction.Close));
    }
    private bool FriendEnabled(string action) => !_busy && !_friendEditing && action switch
    {
        "Remove" or "Message" => FriendRows.Any(f => f.Identity == _friendSelected),
        "AddTarget" => FriendTarget is not null,
        "AddGroup" => Group?.IsInGroup == true,
        _ => true
    };
    private void FriendsPanel()
    {
        if (!_friendsOpen) return;
        // Next Modern UI: the same list and actions in a Modern UI window.
        if (ModernUi) { ModernFriendsPanel(); return; }
        // 005913E0/005916B0/00591880 rebuild through 005908B0, which
        // clears selection even when the selected identity still exists.
        var revision=(FriendsData?.StreamId??Guid.Empty,FriendsData?.FriendsRevision??0);
        if(_friendListSeen!=revision){_friendListSeen=revision;_friendSelected=null;}
        int first = _draws.Count;
        var rows = FriendRows;
        if (!rows.Any(f => f.Identity == _friendSelected)) _friendSelected = null;
        _friendsPage = Math.Clamp(_friendsPage, 0, Math.Max(0, rows.Length - 7));
        Hit("friends/Block", Native("friends/FRAME_DETAIL"));
        foreach (string mesh in new[] { "FRAME_DETAIL", "FRAME_DETAIL01", "FRAME_DETAIL02", "TITLE", "ANYONE", "FRIENDS_ONLY" })
            Draw("friends/" + mesh, Native("friends/" + mesh));
        Draw("friends/EXIT", Native("friends/EXIT"), "friends/Close", !_friendEditing);
        _tips["friends/Close"] = _friendTips?.GetValueOrDefault("exit", "") ?? "";
        FriendLabel("ignore00_TSI28A"); FriendLabel("ANYONE_TSI28A"); FriendLabel("FRIENDS_TSI28A");
        foreach (var (mesh, id, label) in new[] { ("NAME", "Name", "NAME"), ("STATUS", "Location", "STATUS"),
            ("ADD", "Add", "ADD"), ("REMOVE", "Remove", "REMOVE"), ("ADD_TARGET", "AddTarget", "ADD_TARGET"),
            ("ADD_GROUP", "AddGroup", "ADD_GROUP"), ("SEND_MESSAGE", "Message", "CHAT") })
        {
            bool enabled = FriendEnabled(id);
            Draw("friends/" + mesh, Native("friends/" + mesh), "friends/" + id, enabled);
            _tips["friends/" + id] = _friendTips?.GetValueOrDefault(mesh.ToLowerInvariant(), "") ?? "";
            FriendLabel(label + "_TSI28A", hovered: enabled && _hover == "friends/" + id);
        }
        for (int i = 0; i < 2; i++)
        {
            bool selected = FriendsData?.RestrictedStatus == (i == 1);
            Draw(selected ? "check/on" : "friends/CHECK0" + i, Native("friends/CHECK0" + i),
                "friends/" + (i == 0 ? "Unrestricted" : "Restricted"), !_friendEditing && !_busy);
        }
        SocialSlider("friends",rows.Length,7,!_friendEditing);
        for (int row = 0; row < 7; row++)
        {
            if(row+_friendsPage>=rows.Length)
            {
                // 00590550 writes empty text to unused slots. Their opening sequence
                // still runs and completes; a later list receipt does not restart it.
                FriendLabel("name"+row.ToString("00")+"_TSI28A", "");
                FriendLabel("LOCATION"+row.ToString("00")+"_TSI28A", "");
                continue;
            }
            var friend = rows[row + _friendsPage]; string suffix = row.ToString("00"), id = "friends/Row/" + (row + _friendsPage);
            Draw("friends/NAME" + suffix, Native("friends/NAME" + suffix), id, !_friendEditing);
            // Retail 00590C70: hover brightens the name; selection brightens both columns,
            // overriding offline dimming (006CE910 selects the authored alternate color).
            bool selected = friend.Identity == _friendSelected;
            FriendLabel("name" + suffix + "_TSI28A", friend.Identity, friend.IsOffline, _hover == id || selected, directHighlight:true);
            FriendLabel("LOCATION" + suffix + "_TSI28A", friend.Presence, friend.IsOffline, selected, directHighlight:true);
        }
        if (_message is {} message) Text("friends/Error", message, new(125, Native("friends/FRAME_DETAIL").Bottom + 4, 390, 16), new(1, .5f, .5f));
        Flush();
        for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { CompositeOpaqueCoverage = true };
        if (_friendEditing) FriendDialog();
    }
    private void FriendLabel(string id, string? value = null, bool offline = false, bool hovered = false, bool directHighlight = false)
    {
        if (!_guildLayouts["friends"].TryResolve(id, out var definition)) return;
        var color = _friendColors.GetValueOrDefault(definition.Properties.GetValueOrDefault(hovered && directHighlight ? "AltColor" : "Color", "FA_WHITE"), new(1, 1, 1));
        if(hovered && !directHighlight)
            foreach(string effect in definition.Properties.GetValueOrDefault("MouseOver", "").Split(','))
                if(_friendCycleSpeeds.TryGetValue(effect.Trim(),out float speed))
                {
                    var alternate=_friendColors.GetValueOrDefault(definition.Properties.GetValueOrDefault("AltColor","FA_WHITE"),new(1,1,1));
                    color=EnbAttachedLabelCycle.Color(color,alternate,Environment.TickCount64-_hoverSince,speed);
                    _animatingText=true;
                }
        int before = _images.Count;
        if(!_socialTextScroll.TryGetValue(id,out var scroll))_socialTextScroll[id]=scroll=new();
        var style=_socialTextStyles["friends/"+id];
        if(id.StartsWith("name",StringComparison.Ordinal))style=style with {Underline=!offline};
        if(!_friendOpening.TryGetValue(id,out var opening))_friendOpening[id]=opening=new();
        AttachedText("friends/label/"+id,value??definition.Properties.GetValueOrDefault("String0",""),style,scroll,color,opening,fixedLabel:true);
        // 00590550/00590C70 set +2FC; 006D64C0 carries it to +C4 alpha,
        // and 006D80A0 packs it independently of RGB.
        if(offline && !hovered)
            for(int i=before;i<_images.Count;i++)_images[i]=_images[i] with {Opacity=.5f};
    }
    /// <summary>A text box for another Next window (the Modern UI map's find box), on this window's keyboard route.</summary>
    internal EnbUiTextInput CreateHostedTextInput(int length,Func<ushort,Task<bool>> command)=>CreateTextInput(new(length),command);
    private EnbUiTextInput CreateTextInput(EnbRetailTextEditor editor,Func<ushort,Task<bool>> command)
    {
        var input=new EnbUiTextInput(editor){Bounds=new(0,0,1,1)};
        TextInputCreated?.Invoke(input);
        input.KeyDown+=async (_,e,handled)=>
        {
            var key=e.Code switch{9=>EnbInventoryKeys.Tab,13=>EnbInventoryKeys.Enter,27=>EnbInventoryKeys.Escape,45=>EnbInventoryKeys.Insert,_=>EnbInventoryKeys.None};
            if(key!=EnbInventoryKeys.None){handled?.Invoke();await command(key);}
        };
        return input;
    }
    private void BeginFriendEntry()
    {
        if(_friendInput is null)
        {
            _friendInput=CreateTextInput(new(20){CharacterClass=EnbRetailEditorCharacterClass.Letters},FriendsKey);
            _friendInput.Changed+=(_,_)=>
            {
                //58F6B0 constructs mode1 with CD=1;649FE0 calls4BE9D0.
                string text=RetailStringEntry.NormalizeName(_friendInput.Text);
                if(text!=_friendInput.Text){_friendInput.Text=text;return;}
                Publish();
            };
        }
        _friendEditing=true;_friendInput.IsVisible=true;_friendInput.Text="";_friendInput.Focus();
    }
    internal async Task<bool> FriendsKey(ushort key)
    {
        if (!_friendEditing) return false;
        if (key==EnbInventoryKeys.Insert) { _friendOverwrite=!_friendOverwrite;Publish();return true; }
        if (key is not (EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)) return false;
        // Shared string-entry focus completion is acceptance; explicit Cancel/X cancels.
        await ActivateFriends("friends/Submit", false); return true;
    }
    private void FriendDialog()
    {
        int first = _draws.Count;
        Hit("friends/ModalBlock", new(0, 0, 640, 480));
        const string p = "tradeCredits/";
        Draw(p + "FRAME_DETAIL", Native(p + "FRAME_DETAIL"));
        Draw(p + "EXIT", Native(p + "EXIT"), "friends/Cancel", !_busy);
        Draw(p + "VALUE_BUTTON", Native(p + "VALUE_BUTTON"), "friends/Input", !_busy);
        TradeLabel("TITLE01", "Friend Player", true);
        string text = _friendInput?.Text ?? ""; int caret = Math.Clamp(_friendInput?.CaretIndex ?? 0, 0, text.Length);
        StringEntryText(text,caret,!_friendOverwrite);
        foreach (string key in new[] { "CANCEL", "CONFIRM" })
        {
            string id = "friends/" + (key == "CANCEL" ? "Cancel" : "Submit");
            Draw(p + key, Native(p + key), id, !_busy); TradeLabel(key + "_TRADE01", popup: true, button: id);
            _tips[id] = _creditTips?.GetValueOrDefault(key, "") ?? "";
        }
        Flush();
        for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { CompositeOpaqueCoverage = true };
    }
    private async Task<bool> ActivateFriends(string id, bool right)
    {
        if (!id.StartsWith("friends/", StringComparison.Ordinal)) return false;
        if (right || _busy || !_friendsOpen) return true;
        string action = id[8..];
        if (_friendEditing && action is not ("Submit" or "Cancel" or "Input")) return true;
        if (action == "Input") { if (_friendInput is not null) { _friendInput.Text = ""; _friendInput.Focus(); } InteractionSoundRequested?.Invoke("message_1.wav"); return true; }
        if (action == "Cancel") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); _friendEditing = false; if (_friendInput is not null) _friendInput.IsVisible = false; Publish(); return true; }
        if (action == "Close") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); CloseFriends(); Publish(); return true; }
        if (action is "Name" or "Location") { InteractionSoundRequested?.Invoke("fontclick.wav"); _friendsByLocation = action == "Location"; _friendSelected=null; Publish(); return true; }
        if (action.StartsWith("Row/") && int.TryParse(action[4..], out int index) && (uint)index < FriendRows.Length)
        {
            string name = FriendRows[index].Identity;
            //0065F500 consumes click pairs per gadget;005902BD queries the
            //visible row gadget, then requires the row to remain selected.
            int gadget=index-_friendsPage;
            long now=Environment.TickCount64;
            bool pair=_friendClickTimes.TryGetValue(gadget,out long first) && now-first<500;
            if(pair)_friendClickTimes.Remove(gadget);else _friendClickTimes[gadget]=now;
            bool twice = _friendSelected == name && pair;
            _friendSelected = name;
            InteractionSoundRequested?.Invoke("fontclick.wav");
            if (twice) OpenFriendMessage(); Publish(); return true;
        }
        if (action != "Submit" && !FriendEnabled(action)) return true;
        if (action == "Add") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); BeginFriendEntry(); Publish(); return true; }
        if (action == "Message") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); OpenFriendMessage(); Publish(); return true; }
        if (!Enum.TryParse<EnbFriendsAction>(action == "Submit" ? "Add" : action, out var command) || _owner is not uint owner || FriendsRequested is not {} send) return true;
        string? identity = command == EnbFriendsAction.Add ? _friendInput?.Text?.Trim() : command == EnbFriendsAction.AddTarget ? FriendTarget : _friendSelected;
        if (command == EnbFriendsAction.Add && string.IsNullOrWhiteSpace(identity))
        { _friendEditing = false; if (_friendInput is not null) _friendInput.IsVisible = false; Publish(); return true; }
        InteractionSoundRequested?.Invoke(command is EnbFriendsAction.Restricted or EnbFriendsAction.Unrestricted or EnbFriendsAction.AddGroup ? "fontclick.wav" : "Roll_Select02b.wav");
        uint character = owner; ulong ship = _ship;
        await Send(async () =>
        {
            bool sent = await send(new(character, ship, command, identity));
            if (sent && _owner == character && _ship == ship) { _friendEditing = false; if (_friendInput is not null) _friendInput.IsVisible = false; }
            return sent;
        });
        Publish(); return true;
    }
    private void OpenFriendMessage()
    {
        if (FriendRows.FirstOrDefault(f => f.Identity == _friendSelected) is not {} friend) return;
        if (friend.IsOffline)
        {
            //00591FE6: AddChatMessage(type17), not a label on the Friends window.
            if(_owner is uint owner)LocalChatMessageRequested?.Invoke(owner,17,"Friend not online.");
            return;
        }
        FriendMessageRequested?.Invoke(friend.Identity);
    }
}
