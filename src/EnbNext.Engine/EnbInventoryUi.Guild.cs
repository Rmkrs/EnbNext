using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private bool _guildOpen, _guildDisplay, _guildHadMembership;
    private int _guildPage, _guildDisplayPage;
    private long _guildDisplaySeen;
    private string? _guildMember;
    private EnbGuildAction? _guildEntry;
    private EnbUiTextInput? _guildInput;
    private EnbRetailTextEditor? _guildNameEditor,_guildGeneralEditor;
    private Net7GuildPrompt? _guildAnswered;
    private readonly Dictionary<string, Point> _guildAnchors = [];
    private readonly Dictionary<string, EnbInterfaceLayoutCatalog> _guildLayouts = [];
    private readonly Dictionary<string, EnbAttachedLabelWipe> _guildOpening = [];
    private IReadOnlyDictionary<string, string>? _guildTips;
    private EnbRetailIniDocument? _guildDialogs;
    public Func<EnbGuildRequest, Task<bool>>? GuildRequested { get; set; }
    private EnbSceneGuildSnapshot? Guild => _scene?.Hud.Guild??(_layoutPreviewOwner?LayoutGuild:null);
    private int GuildRank => _scene?.Objects.FirstOrDefault(o => o.IsLocalShip)?.PlayerPresentation?.GuildRank ?? -1;
    internal bool GuildEditing => _guildEntry is not null;
    internal bool GuildConfirmationOpen => Guild?.Prompt is {} prompt && !ReferenceEquals(prompt, _guildAnswered);

    private void LoadGuild(EnbRetailAssetBundle assets)
    {
        _guildAnchors.Clear(); _guildLayouts.Clear();
        _friendColors.Clear();
        _friendCycleSpeeds.Clear();
        _socialTextStyles.Clear();_socialTextScroll.Clear();
        foreach (var section in EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fontart.ini")).EnumerateSections())
        {
            if (section.Value.GetValueOrDefault("ColorID") is {} colorId && section.Value.GetValueOrDefault("RGB")?.Split(',') is { Length: 3 } rgb)
                _friendColors[colorId] = new(byte.Parse(rgb[0]) / 255f, byte.Parse(rgb[1]) / 255f, byte.Parse(rgb[2]) / 255f);
            if(section.Value.GetValueOrDefault("EffectType")=="CYCLE" && section.Value.GetValueOrDefault("EffectID") is {} effectId)
                _friendCycleSpeeds[effectId]=float.Parse(section.Value["Speed"],System.Globalization.CultureInfo.InvariantCulture);
        }
        _guildDialogs = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "dialogbox.ini"));
        EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini")).TryGetSection("UI_GUILD", out _guildTips);
        EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory, "tooltip.ini")).TryGetSection("UI_FRIENDS", out _friendTips);
        foreach (var (prefix, name) in new[] { ("guild", "tsi35a"), ("guildDisplay", "tsi36a"), ("friends", "tsi28a") })
        {
            _guildLayouts[prefix] = EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory, "fa_" + name + ".ini"));
            var asset = EnbAuthoredUiAsset.Load(assets, name + ".w3d") ?? throw new InvalidDataException(name);
            var transforms = EnbCockpitUi.ResolveTransforms(asset);
            foreach(var label in _guildLayouts[prefix].Definitions)
                _socialTextStyles[prefix+"/"+label.StringId]=EnbAttachedTextStyle.Load(assets,asset,label,Matrix4x4.Identity,transforms);
            foreach (var pivot in asset.Hierarchy.Pivots)
            {
                var p = Vector3.Transform(Vector3.Zero, transforms[pivot.Ordinal]);
                _guildAnchors[prefix + "/" + pivot.Name] = new(p.X + 320, 240 - p.Y);
            }
            foreach (var mesh in asset.Resource.Meshes.Where(m => asset.Hierarchy.Pivots.Any(p => p.Name == m.Name)))
                _art[prefix + "/" + mesh.Name] = new(asset with { Resource = asset.Resource.Copy().SetMeshes([mesh]) },
                    EnbCockpitUi.MeshBounds(asset, mesh.Name).Translate(new(-320, -240)));
        }
    }

    internal async Task OpenGuild()
    {
        if (!_interactive || GuildRequested is null || _owner is not uint owner || _guildOpen) return;
        // MainHudHandleCommandMenuSelection constructs both entry owners anew.
        _guildNameOverwrite = _guildGeneralOverwrite = false;
        CloseAll(); _guildOpening.Clear(); _socialSliders.Remove("guild"); _socialSliders.Remove("guildDisplay"); _guildOpen = true; _guildHadMembership = GuildRank != -1; _guildPage = _guildDisplayPage = 0; _guildMember = null;
        ulong ship = _ship;
        _guildDisplaySeen = Guild?.DisplayRevision ?? 0;
        Opened?.Invoke(); Publish();
        await Send(() => GuildRequested(new(owner, _ship, EnbGuildAction.Open)));
        if (_owner != owner || _ship != ship) return;
        if (_guildOpen && GuildRank == -1) BeginGuildEntry(EnbGuildAction.Create);
        Publish();
    }

    private void CloseGuild()
    {
        bool wasOpen = _guildOpen;
        _guildOpen = _guildDisplay = false; _guildMember = null;
        EndGuildEntry();
        if (wasOpen && _owner is uint owner && GuildRequested is {} send) _ = send(new(owner, _ship, EnbGuildAction.Close));
    }

    private bool GuildAllowed(EnbGuildAction action) => !_busy && Guild?.Allows(action, GuildRank, _guildMember) == true;
    private static readonly (string Mesh, EnbGuildAction Action, string Tip)[] GuildButtons =
    [
        ("SHOWSTATS", EnbGuildAction.Stats, "STATS"), ("CONTRIBUTIONS", EnbGuildAction.Contributions, "CONTRIBUTION"),
        ("LEAVE", EnbGuildAction.Leave, "LEAVE"), ("RECRUIT", EnbGuildAction.Recruit, "RECRUIT"),
        ("REMOVE", EnbGuildAction.Remove, "REMOVE"), ("PROMOTE", EnbGuildAction.Promote, "PROMOTE"),
        ("DEMOTE", EnbGuildAction.Demote, "DEMOTE"), ("DISBAND", EnbGuildAction.Disband, "DISBAND"),
        ("TAG", EnbGuildAction.Tag, "TAG"), ("MESSAGE", EnbGuildAction.Message, "MESSAGE"),
        ("PUBLIC", EnbGuildAction.Public, "MAKE_PUBLIC"), ("PRIVATE", EnbGuildAction.Private, "MAKE_PRIVATE")
    ];

    private void GuildPanel()
    {
        if (!_guildOpen || Guild is not {} guild) return;
        if (_guildHadMembership && GuildRank == -1) { CloseGuild(); return; }
        _guildHadMembership |= GuildRank != -1;
        if (guild.DisplayRevision != _guildDisplaySeen)
        { _guildDisplaySeen = guild.DisplayRevision; if (!_guildDisplay) { _guildDisplayPage = 0; _socialSliders.Remove("guildDisplay"); } _guildDisplay = true; }
        if (_guildMember is not null && !guild.Members.Any(m => m.Identity == _guildMember)) _guildMember = null;
        if(!_guildDisplay)
            foreach(var (key,effect) in _guildOpening)
                if(key.StartsWith("guildDisplay/",StringComparison.Ordinal))effect.Pause(Environment.TickCount64);
        // Next Modern UI: the same members, abilities, entries, questions and display in a Modern UI window.
        if (ModernUi) { ModernGuildPanel(guild); return; }
        bool modal = GuildEditing || GuildConfirmationOpen || _guildDisplay;
        int firstDraw = _draws.Count;
        string prefix = "guild";
        Hit("guild/Block", Native("guild/FRAME_DETAIL"));
        foreach (string mesh in new[] { "FRAME_DETAIL", "FRAME_DETAIL01", "FRAME_DETAIL02", "TITLE", "NAME", "STATUS", "LEADER_TITLE", "MEMBER_TITLE" })
            Draw(prefix + "/" + mesh, Native(prefix + "/" + mesh));
        Draw("guild/EXIT", Native("guild/EXIT"), "guild/Close", !modal);
        _tips["guild/Close"] = _guildTips?.GetValueOrDefault("EXIT", "") ?? "";
        foreach (string id in new[] { "ignore00_TSI35a", "NAME_TSI35a", "STATUS_TSI35a", "MEMBER_TITLE_TSI35a", "LEADER_TITLE_TSI35a" })
            GuildLabel(prefix, id);
        foreach (var button in GuildButtons)
        {
            bool enabled = !modal && GuildAllowed(button.Action);
            string id = "guild/" + button.Action;
            Draw(prefix + "/" + button.Mesh, Native(prefix + "/" + button.Mesh), id, enabled);
            GuildLabel(prefix, button.Mesh + "_TSI35a", hovered: enabled && _hover == id);
            _tips[id] = _guildTips?.GetValueOrDefault(button.Tip, "") ?? "";
        }
        _guildPage = Math.Clamp(_guildPage, 0, Math.Max(0, guild.Members.Length - 7));
        SocialSlider(prefix,guild.Members.Length,7,!modal);
        for (int row = 0; row < 7; row++)
        {
            string suffix = row.ToString("00");
            if (_guildPage + row >= guild.Members.Length) continue;
            var member = guild.Members[_guildPage + row];
            string id = "guild/Member/" + (_guildPage + row);
            Draw("guild/NAME" + suffix, Native("guild/NAME" + suffix), id, !modal, member.Identity == _guildMember);
            bool highlight=member.Identity==_guildMember || _hover==id;
            GuildLabel(prefix, "name" + suffix + "_TSI35a", member.Identity, hovered:highlight,directHighlight:true);
            GuildLabel(prefix, "LOCATION" + suffix + "_TSI35a", member.Status,hovered:highlight,directHighlight:true);
        }
        if (_message is {} message) Text("guild/Error", message, new(125, Native("guild/FRAME_DETAIL").Bottom + 4, 390, 16), new(1, .5f, .5f));
        Flush();
        if (_guildDisplay)
        {
            prefix = "guildDisplay";
            Hit("guild/DisplayBlock", new(0, 0, 640, 480));_hits["guild/DisplayBlock"].LayoutGroup=EnbUiGroup.None;
            foreach (string mesh in new[] { "FRAME_DETAIL", "FRAME_DETAIL01", "TITLE" }) Draw(prefix + "/" + mesh, Native(prefix + "/" + mesh));
            Draw(prefix + "/EXIT", Native(prefix + "/EXIT"), "guild/DisplayClose");
            Draw(prefix + "/BUTTON01", Native(prefix + "/BUTTON01"), "guild/DisplayClose");
            GuildLabel(prefix, "TITLE_TSI36a"); GuildLabel(prefix, "CLOSE_TSI36a",hovered:_hover=="guild/DisplayClose");
            var lines = guild.DisplayLines;
            _guildDisplayPage = Math.Clamp(_guildDisplayPage, 0, Math.Max(0, lines.Length - 9));
            SocialSlider(prefix,lines.Length,9,true);
            for (int row = 0; row < 9 && row + _guildDisplayPage < lines.Length; row++)
                GuildLabel(prefix, "name" + row.ToString("00") + "_TSI36a", lines[row + _guildDisplayPage]);
            Flush();
        }
        // Preserve the panel's coverage when an alpha-blended child is composited
        // into the transparent HUD layer; otherwise the world shows through it.
        for (int i = firstDraw; i < _draws.Count; i++)
            _draws[i] = _draws[i] with { CompositeOpaqueCoverage = true };
    }

    private void GuildLabel(string prefix, string id, string? value = null, bool disabled = false, bool hovered = false,bool directHighlight=false)
    {
        if (!_guildLayouts[prefix].TryResolve(id, out var definition)) return;
        var color=_friendColors.GetValueOrDefault(definition.Properties.GetValueOrDefault(hovered&&directHighlight?"AltColor":"Color","FA_WHITE"),new(1,1,1));
        if(hovered&&!directHighlight)
            foreach(string effect in definition.Properties.GetValueOrDefault("MouseOver", "").Split(','))
                if(_friendCycleSpeeds.TryGetValue(effect.Trim(),out float speed))
                {
                    color=EnbAttachedLabelCycle.Color(color,_friendColors.GetValueOrDefault(definition.Properties.GetValueOrDefault("AltColor","FA_WHITE"),new(1,1,1)),Environment.TickCount64-_hoverSince,speed);
                    _animatingText=true;
                }
        if(disabled)color=new(.35f,.35f,.35f);
        string key=prefix+"/"+id;
        if(!_socialTextScroll.TryGetValue(key,out var scroll))_socialTextScroll[key]=scroll=new();
        if(!_guildOpening.TryGetValue(key,out var opening))_guildOpening[key]=opening=new();
        AttachedText(prefix+"/label/"+id,value??definition.Properties.GetValueOrDefault("String0",""),_socialTextStyles[key],scroll,color,opening,fixedLabel:true);
    }

    private void BeginGuildEntry(EnbGuildAction action)
    {
        //008199F0 retains separate mode1/20 and mode2/254 buffers.
        _guildEntry=action;
        var editor=action is EnbGuildAction.Recruit or EnbGuildAction.Tag
            ? _guildNameEditor??=new(20){CharacterClass=EnbRetailEditorCharacterClass.Letters}
            : _guildGeneralEditor??=new(254);
        if(_guildInput is null)
        {
            _guildInput=CreateTextInput(editor,GuildKey);
            _guildInput.Changed+=(_,_)=>
            {
                bool name=_guildEntry is EnbGuildAction.Recruit or EnbGuildAction.Tag;
                string text=name?RetailStringEntry.NormalizeName(_guildInput.Text)
                    :new(_guildInput.Text.TakeWhile(c=>c!='\0').Where(c=>c<=255).Take(254).ToArray());
                if(text!=_guildInput.Text){_guildInput.Text=text;return;}
                Publish();
            };
        }
        else _guildInput.Use(editor);
        _guildInput.IsVisible=true;_guildInput.Text="";_guildInput.Focus();
    }
    private void EndGuildEntry() { _guildEntry = null; if (_guildInput is not null) _guildInput.IsVisible = false; }
    // 008199F0 retains separate mode1 (+C4) and mode2 (+C0) string editors.
    private bool _guildNameOverwrite { get=>_guildNameEditor is {} editor && !editor.InsertMode; set {if(_guildNameEditor is {} editor)editor.InsertMode=!value;} }
    private bool _guildGeneralOverwrite { get=>_guildGeneralEditor is {} editor && !editor.InsertMode; set {if(_guildGeneralEditor is {} editor)editor.InsertMode=!value;} }
    private bool GuildEntryOverwrite => _guildEntry is EnbGuildAction.Recruit or EnbGuildAction.Tag ? _guildNameOverwrite : _guildGeneralOverwrite;

    internal async Task<bool> GuildKey(ushort key)
    {
        if (GuildConfirmationOpen)
        {
            var definition = GuildPromptDefinition(Guild!.Prompt!);
            for (int i = 0; i < 2; i++)
                if (ParseRetailKey(definition?.GetValueOrDefault("Key" + i, "") ?? "") == key)
                    await ActivateGuild(i == 0 ? "guild/Accept" : "guild/Reject", false);
            return key != EnbInventoryKeys.Escape;
        }
        if (!GuildEditing) return false;
        if (key==EnbInventoryKeys.Insert)
        {
            if (_guildEntry is EnbGuildAction.Recruit or EnbGuildAction.Tag) _guildNameOverwrite=!_guildNameOverwrite;
            else _guildGeneralOverwrite=!_guildGeneralOverwrite;
            Publish();return true;
        }
        //006AA200 ends focus for all three keys;00649FE0 returns result2,
        //which81CB90 accepts. Only explicit Cancel/X returns cancellation.
        if (key is EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab) { await ActivateGuild("guild/Submit", false); return true; }
        return false; // Native transport delivers ordinary keys/text to the engine buffer.
    }

    private void GuildDialog()
    {
        if (!GuildEditing && !GuildConfirmationOpen) return;
        // Next Modern UI: the open guild window asks in itself; a question without it gets its own small window.
        if (ModernUi) { if (!_guildOpen && GuildConfirmationOpen) ModernGuildQuestion(Guild!); return; }
        Hit("guild/ModalBlock", new(0, 0, 640, 480)); _hits["guild/ModalBlock"].LayoutGroup=EnbUiGroup.None; Flush();
        var prompt = GuildConfirmationOpen ? Guild!.Prompt : null;
        string title = prompt is not null ? GuildPromptText(prompt) : _guildEntry switch
        {
            EnbGuildAction.Recruit => "Recruit Member", EnbGuildAction.Tag => "Set Member Tag",
            EnbGuildAction.Message => "Enter Guild Message", EnbGuildAction.Stats => "Enter Guild Name",
            _ => "Create a new Guild"
        };
        if (prompt is null)
        {
            //00649890/00649AA0 use TRADE01A and FA_TRADE01A.INI for every entry mode.
            int first = _draws.Count;
            const string p = "tradeCredits/";
            Draw(p + "FRAME_DETAIL", Native(p + "FRAME_DETAIL"));
            Draw(p + "EXIT", Native(p + "EXIT"), "guild/Cancel", !_busy);
            Draw(p + "VALUE_BUTTON", Native(p + "VALUE_BUTTON"), "guild/Input", !_busy);
            TradeLabel("TITLE01", title, true);
            string text = _guildInput?.Text ?? "";
            int caret = Math.Clamp(_guildInput?.CaretIndex ?? 0, 0, text.Length);
            StringEntryText(text,caret,!GuildEntryOverwrite);
            foreach (string key in new[] { "CANCEL", "CONFIRM" })
            {
                string id = "guild/" + (key == "CANCEL" ? "Cancel" : "Submit");
                Draw(p + key, Native(p + key), id, !_busy);
                TradeLabel(key + "_TRADE01", popup: true, button: id);
                _tips[id] = _creditTips?.GetValueOrDefault(key, "") ?? "";
            }
            _tips["guild/Input"] = _creditTips?.GetValueOrDefault("VALUE_BUTTON", "") ?? "";
            Flush();
            for (int i = first; i < _draws.Count; i++) _draws[i] = _draws[i] with { CompositeOpaqueCoverage = true };
            return;
        }
        StandardConfirmationDialog("guild", title, GuildPromptDefinition(prompt), ["Accept", "Reject"], !_busy);
        Flush();
    }

    private IReadOnlyDictionary<string, string>? GuildPromptDefinition(Net7GuildPrompt prompt)
    {
        string name = prompt.Opcode == 0x00C8 ? "RECRUIT" : prompt.Opcode == 0x00D8 ? "GM_RENAME" : prompt.Type switch
        { 0 => "LEADER", 1 => "PROMOTE", 3 => "NEWLEADER", 5 => "DEMOTE", 7 => "REMOVE", 9 => "LEAVE", 11 => "DISBAND",
          13 => "GM_DISBAND", 16 => "GM_REMOVE", 18 => "GM_PROMOTE", 20 => "GM_DEMOTE", _ => "" };
        return _guildDialogs?.TryGetSection("GUILD_" + name + "_CONFIRM", out var definition) == true ? definition : null;
    }

    private static string GuildPromptText(Net7GuildPrompt prompt)
    {
        //73DD00/73E720/73EEF0 construct these bodies before74E580 opens
        //the authored dialog. GM and ordinary operations have distinct wording.
        if (prompt.Opcode == 0x00C8) return $"{prompt.First} is inviting you to join {prompt.Second}. Do you accept?";
        if (prompt.Opcode == 0x00D8) return $"Are you sure you want to rename '{prompt.First}' to '{prompt.Second}'?";
        return prompt.Type switch
        {
            0 => $"Are you sure you want to create a guild named '{prompt.First}'?",
            1 => $"Are you sure you want to promote {prompt.First}?",
            3 => $"Are you sure you want {prompt.First} to become the guild leader?",
            5 => $"Are you sure you want to demote {prompt.First}?",
            7 => $"Are you sure you want to remove {prompt.First} from the guild?",
            9 => "Are you sure you want to leave the guild?",
            11 => "Are you sure you want to disband your whole guild?",
            13 => $"Are you sure you want to disband '{prompt.First}'?",
            16 => $"Are you sure you want to remove '{prompt.First}' from their guild?",
            18 => $"Are you sure you want to promote '{prompt.First}' within their guild?",
            20 => $"Are you sure you want to demote '{prompt.First}' within their guild?",
            _ => "Confirm guild action: " + prompt.First
        };
    }

    private async Task<bool> ActivateGuild(string id, bool right)
    {
        if (!id.StartsWith("guild/", StringComparison.Ordinal)) return false;
        if (right || _busy) return true;
        string command = id[6..];
        if (GuildConfirmationOpen && command is not ("Accept" or "Reject")) return true;
        if (GuildEditing && command is not ("Submit" or "Cancel" or "Input")) return true;
        if (command == "Input") { if (_guildInput is not null) { _guildInput.Focus(); _guildInput.Text = ""; } InteractionSoundRequested?.Invoke("message_1.wav"); Publish(); return true; }
        if (command == "Cancel") { InteractionSoundRequested?.Invoke("Roll_Select02b.wav"); bool create = _guildEntry == EnbGuildAction.Create; EndGuildEntry(); if (create) CloseGuild(); Publish(); return true; }
        if (command == "Close") { CloseGuild(); Publish(); return true; }
        if (command == "DisplayClose")
        { _guildDisplay = false; if (_owner is uint c && GuildRequested is {} close) await close(new(c, _ship, EnbGuildAction.CloseDisplay)); Publish(); return true; }
        if (command.StartsWith("Member/", StringComparison.Ordinal))
        { if (int.TryParse(command[7..], out int index) && Guild is {} g && (uint)index < g.Members.Length) _guildMember = g.Members[index].Identity; Publish(); return true; }
        var action = command == "Submit" ? _guildEntry : Enum.TryParse<EnbGuildAction>(command, out var parsed) ? parsed : (EnbGuildAction?)null;
        if (action is not {} value || GuildRequested is not {} send || _owner is not uint owner) return true;
        if (!GuildAllowed(value)) return true;
        if (command != "Submit" && value is EnbGuildAction.Recruit or EnbGuildAction.Tag or EnbGuildAction.Message or EnbGuildAction.Stats)
        { BeginGuildEntry(value); Publish(); return true; }
        string? text = command == "Submit" ? _guildInput?.Text?.Trim() : null;
        if (command == "Submit" && string.IsNullOrEmpty(text)) return true;
        var prompt = value is EnbGuildAction.Accept or EnbGuildAction.Reject ? Guild?.Prompt : null;
        var request = new EnbGuildRequest(owner, _ship, value, _guildMember, text, prompt);
        // Retire the previous display before sending: a response can publish during the await.
        if (value is EnbGuildAction.Stats or EnbGuildAction.Contributions)
        { _guildDisplay = false; _guildDisplayPage = 0; }
        await Send(async () =>
        {
            bool sent = await send(request);
            if (_owner != request.Character || _ship != request.ShipOwner) return sent;
            if (prompt is not null && (sent || !ReferenceEquals(prompt, Guild?.Prompt))) _guildAnswered = prompt;
            if (!sent) return false;
            EndGuildEntry();
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
            return true;
        });
        Publish(); return true;
    }
}
