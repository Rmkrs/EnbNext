using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    public Func<EnbStationInteractionRequest,Task<bool>>? ConversationRequested {get;set;}
    public Func<EnbStationReplyRequest,Task<bool>>? ReplyRequested {get;set;}
    public Func<EnbStationPurchaseRequest,Task<bool>>? PurchaseRequested {get;set;}
    public Action<string>? InteractionSoundRequested {get;set;}
    private EnbStationInteractionRequest? _conversationOwner;
    private EnbStationInteractionRequest? _conversationEntering;
    private long _conversationEntryGeneration;
    public EnbStationInteractionRequest? ConversationCameraOwner => _conversationEntering ?? _conversationOwner;
    public Func<EnbStationInteractionRequest,Task<bool>>? ConversationCameraRequested {get;set;}
    private string _conversationName="";
    private ulong _conversationRevision;
    private ulong _conversationEndRevision, _spaceTalkEndRevision;
    private ulong _spaceTradeOpenRevision;
    private ulong _dialogueOpeningRevision;
    private bool _vendorClosesConversation;
    private object? _lastSpaceTalkTree;
    private (Guid Stream, ulong Handoff)? _spaceTalkContext;
    private EnbNext.Net7.Presentation.Net7TalkTreeActionUpdate? _lastSpaceTalkAction;
    private bool _conversationWaiting, _trading;
    private bool _conversationCloseSoundPlayed;
    private int _talkPage, _talkPageCount=1, _vendorPage;
    private bool _vendorSort, _vendorCompare;
    public Action<uint,bool>? VendorSortPersistence {get;set;}
    private uint _vendorCategoryMask;
    private int _vendorCategory;
    private ImmutableArray<int> _vendorCategories=[];
    private ulong _tradeOpenRevision;
    private EnbNext.Net7.Presentation.Net7TalkTreeActionUpdate? _lastConversationAction;
    private readonly Dictionary<string,Rect> _conversationAnchors=[];
    private readonly Dictionary<string,EnbInterfaceLayoutCatalog> _conversationLayouts=[];
    private readonly Dictionary<string,EnbRgb> _conversationColors=[];
    private IReadOnlyDictionary<string,string>? _vendorTooltips;
    private string? _talkAsset;
    private EnbInventoryItemRequest? _tradeSelection;
    private EnbStationPurchaseRequest? _tradeConfirmation;
    private EnbVendorConfirmationStage _tradeConfirmationStage;
    private IReadOnlyDictionary<string,string>? _tradeDialog;
    private EnbStationConversation? Conversation=>_scene?.StationConversation;
    public EnbStationInteractionRequest? ConversationOwner=>_conversationOwner;
    private object? _speechTree;
    private int _speechPageIndex;
    private EnbDialogueSpeechPage? _speechPage;
    private EnbSceneAvatarSnapshot? _dialoguePortrait;
    private EnbDialogueSpeechPage? _portraitSpeechPage,_portraitSeenPage;
    public EnbDialogueSpeechPage? DialogueSpeechPage
    {
        get
        {
            if(_conversationOwner is null || !_dialogueHasText || _dialogueTree is not EnbNext.Net7.Presentation.Net7TalkTreeUpdate tree)return null;
            if(!ReferenceEquals(_speechTree,tree) || _speechPageIndex!=_talkPage || _speechPage is null)
            {
                _speechTree=tree;_speechPageIndex=_talkPage;
                // 0064DD50 appends each raw wrapped line and a trailing space.
                // Slash commands remain in speech even though display removes them.
                string text=string.Concat(DialogueLines(tree.NpcSay).Skip(_talkPage*6).Take(6).Select(line=>line+" "));
                _speechPage=new(text);
            }
            return _speechPage;
        }
    }
    public bool VendorOpen => _interactive && _trading && _conversationOwner is not null;
    public Action<uint,string>? LocalMessageRequested { get; set; }
    public async Task<bool> VendorQuickClick(string id, EnbInventoryItemRequest? item, KeyModifiers modifiers)
    {
        if (!VendorOpen || (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0) return false;
        if (item is null && id.StartsWith("vendorSelect/", StringComparison.Ordinal) &&
            int.TryParse(id.Split('/')[1], out int slot) && Data?.Find(4, slot) is {} ware)
            item = new(_conversationOwner!.Character, _ship, 4, slot, ware.TemplateId);
        if (item is not { Inventory: 1 or 4 }) return false;
        if (_busy || _tradeConfirmation is not null || Resolve(item) is not { Price: {} price } current ||
            PurchaseRequested is not {} purchase) return true;
        _tradeSelection = item;
        // Retail 005A0740 enters InventoryTransaction_Start directly: no vendor confirmation wrapper.
        bool stack = (modifiers & KeyModifiers.Control) == 0;
        var owner = _conversationOwner!;
        string feedback = item.Inventory == 4
            ? stack ? $"Buying stack of {current.Name}." : $"Buying {current.Name}."
            : stack ? FormattableString.Invariant($"Selling stack of {current.Count} {current.Name}.") : $"Selling {current.Name}.";
        await Send(() => purchase(new(owner, item, stack, true, price, current.Count)));
        if (owner == _conversationOwner && owner.Character == session() && item.ShipOwner == _ship)
            LocalMessageRequested?.Invoke(owner.Character, feedback);
        return true;
    }
    public async Task VendorKey(bool buy)
    {
        if (!VendorOpen || _busy || _tradeConfirmation is not null ||
            _tradeSelection is not {} selection || selection.Inventory != (buy ? 4 : 1) ||
            Resolve(selection) is not { Price: not null }) return;
        await ActivateConversation(buy ? "vendorBuy" : "vendorSell", null);
        Publish();
    }
    public object? ConversationPreviousTree {get;private set;}
    public async Task OpenStationNpc(uint station,ulong version,uint room,EnbSceneStationNpc npc)
    {
        if(!_interactive || _conversationOwner is not null || _conversationEntering is not null ||
            ConversationRequested is null || session() is not uint character)return;
        var owner=new EnbStationInteractionRequest(character,station,version,room,npc.Slot,npc.Id);
        // 0063D2F0 sets the transition latch for ordinary and alternate actors;
        // 006390A0 opens command B only after its camera completes. The actorless
        // black-market booth has neither branch; retain its existing handling.
        if(!(npc.Role==7 && npc.Variant==0) && ConversationCameraRequested is {} transition)
        {
            CloseAll();
            long generation=_conversationEntryGeneration;
            ConversationPreviousTree=Conversation?.Tree;
            _conversationEntering=owner;
            bool completed;
            try {completed=await transition(owner);}
            finally {if(generation==_conversationEntryGeneration && _conversationEntering==owner)_conversationEntering=null;}
            if(!completed || generation!=_conversationEntryGeneration || session()!=character)return;
        }
        await OpenConversation(owner,npc.DisplayName);
    }

    public async Task OpenConversation(EnbStationInteractionRequest owner,string name)
    {
        if(!_interactive || _conversationOwner is not null || ConversationRequested is null)return;
        try { LoadConversationArt(owner.StarbaseId is null); }
        catch(Exception ex) when(ex is IOException or InvalidOperationException) {Error=ex.Message;return;}
        CloseAll();_conversationOwner=owner;_conversationName=name;
        _conversationCloseSoundPlayed=false;
        ConversationPreviousTree=Conversation?.Tree;
        _conversationRevision=Conversation?.Revision??0;_conversationWaiting=true;_talkPage=0;_dialogueTree=Conversation?.Tree;_dialogueHasText=false;_dialogueDone=false;_dialogueWipe.Clear();
        _tradeOpenRevision=Conversation?.TradeOpenRevision??0;
        _conversationEndRevision=Conversation?.EndRevision??0;
        _dialogueOpeningRevision=Conversation?.Revision??0;
        Opened?.Invoke();
        InteractionSoundRequested?.Invoke("message_1.wav");
        await Send(()=>ConversationRequested(owner));
        if(_message is not null && _conversationOwner==owner)ClearConversation();
        Publish();
    }
    private void ClearConversation(bool restoreStationUi=false)
    {
        _conversationEntering=null;_conversationEntryGeneration++;
        // StarbaseViewUpdate 0069CB0E restores the group panel after dialogs close;
        // the forced member refresh reaches the sound request at 00595DC9.
        if(restoreStationUi && _conversationOwner?.StarbaseId is not null)InteractionSoundRequested?.Invoke("SR_ziprollover02.wav");
        _speechTree=null;_speechPage=null;_dialoguePortrait=null;_portraitSpeechPage=null;_portraitSeenPage=null;
        _conversationOwner=null;_trading=false;_vendorClosesConversation=false;_vendorCompare=false;_tradeSelection=null;_tradeConfirmation=null;_conversationWaiting=false;_lastConversationAction=null;
    }
    private void UpdateConversation()
    {
        bool spaceCloseAction=false;
        if(_tradeSelection is {} selection && Resolve(selection) is null)_tradeSelection=null;
        if(_conversationOwner is {} oldOwner && (_scene is null || _scene.ActiveStarbaseId!=oldOwner.StarbaseId ||
            (_scene.LocationTransition?.Version??0)!=oldOwner.HandoffVersion || session()!=oldOwner.Character))
            ClearConversation();
        if(_scene is {} scene)
        {
            var context=(scene.ClientSoundStreamId,scene.LocationTransition?.Version??0);
            if(_spaceTalkContext!=context){_spaceTalkContext=context;_lastSpaceTalkTree=null;_lastSpaceTalkAction=null;_spaceTalkEndRevision=0;_spaceTradeOpenRevision=0;}
            if(scene.ActiveStarbaseId is null && Conversation?.Action is {} spaceAction)
            {
                spaceCloseAction=Conversation.EndRevision>_spaceTalkEndRevision ||
                    Conversation.EndRevision==0 && !ReferenceEquals(spaceAction,_lastSpaceTalkAction) && spaceAction.EndsConversation;
                _spaceTalkEndRevision=Conversation.EndRevision;
                _lastSpaceTalkAction=spaceAction;
            }
            bool tradePushed=Conversation is {} command && command.TradeOpenRevision>_spaceTradeOpenRevision;
            bool textPushed=Conversation?.Tree is {} incoming && !ReferenceEquals(incoming,_lastSpaceTalkTree);
            _spaceTradeOpenRevision=Conversation?.TradeOpenRevision??0;
            if(scene.ActiveStarbaseId is not null)_lastSpaceTalkTree=Conversation?.Tree;
            else if(_interactive && Conversation is {} pushed &&
                (tradePushed || textPushed))
            {
                if(_conversationOwner is null && (tradePushed && pushed.TradeOpenRevision>pushed.EndRevision ||
                    textPushed && pushed.TextRevision>pushed.EndRevision))spaceCloseAction=false;
                _lastSpaceTalkTree=pushed.Tree;
                // 0064CC90 activates on text; 0064D230/0064E430 also activates
                // the shared owner for a vendor command without any text.
                // No NPC selection, starbase request, or invented speaker name.
                if(session() is uint character && !spaceCloseAction)
                {
                    try { LoadConversationArt(true); }
                    catch(Exception ex) when(ex is IOException or InvalidOperationException){Error=ex.Message;return;}
                    if(_conversationOwner is null)
                    {
                        CloseAll();_conversationOwner=new(character,null,context.Item2,0,0,0);
                        // 0064E430 captures the prior inactive flag; 0064DA40
                        // exits instead of returning to an unopened talk panel.
                        _vendorClosesConversation=tradePushed && (!textPushed || pushed.TradeOpenRevision<pushed.TextRevision);
                        _conversationName="";_conversationCloseSoundPlayed=false;
                        _tradeOpenRevision=0;
                        _dialogueOpeningRevision=tradePushed && (!textPushed || pushed.TradeOpenRevision<pushed.TextRevision)
                            ? pushed.TradeOpenRevision : pushed.TextRevision;
                        _conversationRevision=0;_dialogueTree=null;_dialogueHasText=false;_dialogueDone=false;_dialogueWipe.Clear();
                        Opened?.Invoke();InteractionSoundRequested?.Invoke("message_1.wav");
                    }
                    _conversationWaiting=false;
                }
            }
        }
        if(_conversationOwner is null)return;
        if(Conversation is {} c && (c.Revision!=_conversationRevision || !ReferenceEquals(c.Tree,_dialogueTree)))
        {
            _conversationRevision=c.Revision;
            if(!ReferenceEquals(c.Tree,_dialogueTree))
            {_dialogueTree=c.Tree;_dialogueHasText=c.Tree is not null;_conversationWaiting=false;_talkPage=0;}
            // 0064C800 resets More on activation. Only later 6/7 commands
            // belong to this opening; later text on the same owner retains Done.
            if(c.DoneRevision>_dialogueOpeningRevision ||
                c.DoneRevision==0 && (c.DoneCaption || c.Action?.Action is 6 or 7))_dialogueDone=true;
            if(_conversationOwner.StarbaseId is null ? spaceCloseAction :
                c.EndRevision>_conversationEndRevision || c.EndRevision==0 && c.Action is {} action && action.EndsConversation)
            {
                // 0064D780 -> virtual +5C -> 0064C8D0: an active talk
                // interface sends E6 even when a negative server action closes it.
                var owner=_conversationOwner;
                PlayConversationCloseSound();ClearConversation(true);
                if(ReplyRequested is {} reply)_=Send(()=>reply(new(owner,c.Revision,0,true)));
                return;
            }
            if(c.TradeOpenRevision!=0 && c.TradeOpenRevision!=_tradeOpenRevision ||
                !ReferenceEquals(c.Action,_lastConversationAction) && c.Action?.Action==5)
            {_trading=true;_dialoguePortrait=null;_portraitSpeechPage=null;OpenVendorCargo();_vendorPage=0;_vendorCategory=0;_vendorCompare=false;}
            _tradeOpenRevision=c.TradeOpenRevision;
            _lastConversationAction=c.Action;
            if(!_conversationWaiting && c.Tree is {Responses.Count:0} empty && DialogueLines(empty.NpcSay).Length==0)_ = ContinueDialogue();
        }
    }
    private void LoadConversationArt(bool space=false, bool recovery=false)
    {
        // Retail 0064B6E0 selects mode 0 TLI00A and mode 1 TLI02A
        // through 00B8BBC4. Their meshes include the authored placement.
        string talkAsset=recovery?"tli01a":space?"tli00a":"tli02a";
        if(_talkAsset==talkAsset && _art.ContainsKey("talk/FRAME01"))return;
        foreach(string key in _art.Keys.Where(k=>k.StartsWith("talk/")).ToArray())_art.Remove(key);
        foreach(string key in _conversationAnchors.Keys.Where(k=>k.StartsWith("talk/")).ToArray())_conversationAnchors.Remove(key);
        foreach(var color in EnbRetailIniDocument.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,"fontart.ini")).EnumerateSections().Select(s=>s.Value))
            if(color.GetValueOrDefault("ColorID") is {} id && color.GetValueOrDefault("RGB")?.Split(',') is {Length:3} values)
                _conversationColors[id]=new(byte.Parse(values[0])/255f,byte.Parse(values[1])/255f,byte.Parse(values[2])/255f);
        if(EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"tooltip.ini")).TryGetSection("UI_TRADE",out var vendorTips))_vendorTooltips=vendorTips;
        if(EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"dialogbox.ini")).TryGetSection("BUY_CONFIRMATION",out var labels))
        _tradeDialog=labels;
        foreach(var (prefix,file) in new[]{("talk",talkAsset+".w3d"),("vendor","ce02a.w3d")})
        {
            _conversationLayouts[prefix]=EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets!.Paths.ClientIniDirectory,(recovery && prefix=="talk"?"fc_":"fa_")+Path.GetFileNameWithoutExtension(file)+".ini"));
            var asset=EnbAuthoredUiAsset.Load(_assets!,file)??throw new InvalidDataException(file+" unavailable");
            if(prefix=="talk")LoadDialogueText(asset,recovery?"tli01a_av":talkAsset);
            if(prefix=="talk" && recovery)LoadRecoveryAnswers(asset);
            if(prefix=="vendor")
            {
                LoadVendorButtonText(asset);
                var transforms=EnbCockpitUi.ResolveTransforms(asset);
                foreach(var pivot in asset.Hierarchy.Pivots.Where(p=>p.Name.StartsWith("SLIDER_",StringComparison.OrdinalIgnoreCase)))
                {var origin=transforms[pivot.Ordinal].Translation;_guildAnchors["vendor/"+pivot.Name.ToUpperInvariant()]=new(origin.X+320,240-origin.Y);}
            }
            foreach(var m in asset.Resource.Meshes)
                if(asset.Hierarchy.Pivots.Any(p=>p.Name.Equals(m.Name,StringComparison.OrdinalIgnoreCase)))
                    _art[prefix+"/"+m.Name.ToUpperInvariant()]=new(asset with{Resource=asset.Resource.Copy().SetMeshes([m])},
                        EnbCockpitUi.MeshBounds(asset,m.Name).Translate(new(-320,-240)));
            // Text-only attachments do not have meshes (NAME, COST, CREDITS, etc.).
            foreach(var p in asset.Hierarchy.Pivots)
            {
                double x=320+p.Translation.X,y=240-p.Translation.Y;
                double width=prefix=="talk"?440:110;
                _conversationAnchors[prefix+"/"+p.Name.ToUpperInvariant()]=new(x,y-(prefix=="talk"?12:5),width,prefix=="talk"?16:7);
            }
        }
        _talkAsset=talkAsset;
    }
    private Rect? ConversationRect(string prefix,string name)
        =>_art.TryGetValue(prefix+"/"+name.ToUpperInvariant(),out var art)?art.Design.Translate(new(320,240)):
          _conversationAnchors.TryGetValue(prefix+"/"+name.ToUpperInvariant(),out var rect)?rect:null;
    private void ConversationButton(string prefix,string name,string id,string label,bool enabled=true,bool runtimeLabel=false)
    {
        if(ConversationRect(prefix,name) is not {} r)return;
        string font="FONT_SMALL";EnbRgb color=new(1,1,0);
        if(label.Length>0 && _conversationLayouts[prefix].TryResolve(name+"_"+(prefix=="talk"?_talkAsset:"ce02a"),out var authored))
        {
            if(!runtimeLabel && label is not ("Back" or "Done"))label=authored.Properties.GetValueOrDefault("String0",label);
            font=authored.Properties.GetValueOrDefault("FontType",font);
            bool selected=id.StartsWith("vendorCategory/",StringComparison.Ordinal)&&!_vendorCompare&&id=="vendorCategory/"+_vendorCategory;
            if(authored.Properties.GetValueOrDefault(selected?"AltColor":"Color") is {} colorId && _conversationColors.TryGetValue(colorId,out var ink))color=ink;
        }
        Draw(prefix+"/"+name.ToUpperInvariant(),r,enabled?id:null,enabled);
        if(prefix=="talk" && label.Length>0)DialogueText(name.ToUpperInvariant(),id,label);
        else if(prefix!="vendor" || !VendorButtonText(name,id,label,enabled?color:new(.5f,.5f,.5f),enabled))
            Text(id,label,r,enabled?color:new(.5f,.5f,.5f),font);
    }
    private void ConversationPanel()
    {
        if(_trading){if(ModernUi)ModernVendorPanel();else VendorPanel();return;} // Next Modern UI: the vendor in a Modern UI window.
        if(ModernUi){ModernConversationPanel();return;} // Next Modern UI: the same conversation in a Modern UI window.
        // 007E3A20 resolves AvatarData only while the retained actor is absent.
        // 0064F630 clears the page even if that resolution fails this update.
        _dialoguePortrait??=Conversation?.Portrait;
        var speechPage=DialogueSpeechPage;
        if(!ReferenceEquals(_portraitSeenPage,speechPage))
        {
            _portraitSeenPage=speechPage;
            _portraitSpeechPage=_dialoguePortrait is null?null:speechPage;
        }

        foreach(var entry in _art.Where(a=>a.Key.StartsWith("talk/") && a.Key.Contains("FRAME")).OrderBy(a=>a.Key,StringComparer.Ordinal))
        {
            var r=entry.Value.Design.Translate(new(320,240));Draw(entry.Key,r);Hit("talkFrame/"+entry.Key,r);
            if(entry.Key=="talk/FRAME02" && _dialoguePortrait is {} avatar && ConversationRect("talk","PICTURE") is {} portraitBounds)
                _draws[^1]=_draws[^1] with {Portrait=avatar,PortraitEnvironment=_scene!.Environment,
                    PortraitCacheKey="conversation/avatar",PortraitSpeech=_portraitSpeechPage,PortraitBounds=(Screen(portraitBounds)),PortraitAspect=portraitBounds.Width/portraitBounds.Height};
        }
        string speaker=_dialoguePortrait is {} portrait ? string.Join(" ",new[]{portrait.FirstName,portrait.LastName}.Where(s=>!string.IsNullOrWhiteSpace(s))) : _conversationName;
        DialogueText("NAME","talkName",speaker);
        var slots=Enumerable.Range(0,6).Select(i=>ConversationRect("talk",$"TALK{i:00}")).Where(r=>r is not null).Select(r=>r!.Value).ToArray();
        var tree=_dialogueHasText?_dialogueTree as EnbNext.Net7.Presentation.Net7TalkTreeUpdate:null;
        var lines=DialogueLines(tree?.NpcSay??"");
        _talkPageCount=Math.Max(1,(lines.Length+5)/6);
        bool more=(_talkPage+1)*6<lines.Length;
        for(int i=0;i<6;i++)
        {
            string line=lines.ElementAtOrDefault(_talkPage*6+i)??"";
            // 0064DD50 wraps first, then removes slash commands in each line.
            DialogueText($"TALK{i:00}","talkBody/"+i,EnbStationDialogueText.Display(line));
        }
        if(tree is not null && !_conversationWaiting)
        {
            if(more || tree.Responses.Count==0)
                ConversationButton("talk","MORE",more?"talkMore":"talkDone",_dialogueDone?"Done":"More",!_busy,true);
            else
            {
                for(int i=0;i<Math.Min(5,tree.Responses.Count);i++)
                {
                    if(ConversationRect("talk",$"ANSWER{i:00}") is not {} r)continue;
                    string id="talkReply/"+i;Draw("talk/ANSWER"+i.ToString("00"),r,id,!_busy);
                    var response=DialogueLines(tree.Responses[i].Text,true);
                    DialogueText($"ANSWER{i:00}",id,(response.FirstOrDefault()??"")+(response.Length>1?"...":""));
                    if(response.Length>1)_tips[id]=tree.Responses[i].Text;
                }
                if(_conversationLayouts["talk"].TryResolve("REPLY_"+_talkAsset,out var replyLabel))
                    DialogueText("REPLY","talkResponses",replyLabel.Properties.GetValueOrDefault("String0",""));
            }
        }
        if(Conversation?.SkillAvailable==true)ConversationButton("talk","SKILL","talkSkill","Skill",!_busy);
        if(Conversation?.TradeAvailable==true)ConversationButton("talk","TRADE","talkTrade","Trade",!_busy);
        ConversationButton("talk","EXIT","talkClose","",!_busy);
        if(_message is not null && slots.Length>0)Wrap("talkError",_message,slots[0],new(1,.4f,.4f),false,"FONT_SMALL");
        Flush();
    }
    private string[] ConversationLines(string text,double width)
    {
        if(Font("FONT_SMALL") is not {} font)return [text];
        // Wrapping uses the same atlas metrics as inventory descriptions.
        var result=new List<string>();string line="";
        foreach(string paragraph in text.Replace("\r","").Split('\n'))
        {
            foreach(string word in paragraph.Split(' ',StringSplitOptions.RemoveEmptyEntries))
            {
                string next=line.Length==0?word:line+" "+word;
                if(!_labels.TryGetValue("talkMeasure",out var label))_labels["talkMeasure"]=label=new();
                label.SetText(next,font,new(1,1,1));label.SetDisplayScale(1,1);
                if(line.Length>0 && label.Width>Screen(new Rect(0,0,width,1)).Width){result.Add(line);line=word;}else line=next;
            }
            result.Add(line);line="";
        }
        return result.ToArray();
    }
    private void VendorPanel()
    {
        foreach(var entry in _art.Where(a=>a.Key.StartsWith("vendor/") && (a.Key.Contains("FRAME")||a.Key.Contains("BODY"))).OrderBy(a=>a.Key.Contains("FRAME")?0:1))
        {var r=entry.Value.Design.Translate(new(320,240));Draw(entry.Key,r);Hit("vendorFrame/"+entry.Key,r);}
        uint mask=EnbVendorList.CategoryMask(Data?.Vendor??[]);
        if(mask!=_vendorCategoryMask)
        {
            _vendorCategoryMask=mask;_vendorCategories=EnbVendorList.Categories(mask);
            _vendorCategory=Math.Clamp(_vendorCategory,0,Math.Max(0,_vendorCategories.Length-1));
            _tradeSelection=null;
        }
        var wares=Data is {} inventory?EnbVendorList.Rows(inventory,_vendorCategories.IsEmpty?null:_vendorCategories[_vendorCategory],_vendorCompare,_vendorSort):[];
        SocialSlider("vendor",wares.Length,5,_tradeConfirmation is null&&!_busy);
        for(int i=0;i<5;i++)
        {
            var item=wares.ElementAtOrDefault(_vendorPage+i);
            if(item is not null && ConversationRect("vendor",$"HIGHLIGHT{i:00}") is {} highlight)
                Draw($"vendor/HIGHLIGHT{i:00}",highlight,"vendorSelect/"+item.Slot,selected:_tradeSelection is {Inventory:4} chosen && chosen.Slot==item.Slot);
            // 006538E0 assigns -1 beyond the list; 0062EC1F hides that gadget.
            if(item is not null && ConversationRect("vendor",$"W{i:00}") is {} slot)Slot("vendorSelect/"+item.Slot+"/icon",slot,item with {Count=1});
            if(item is not null && ConversationRect("vendor",$"OWNED{i:00}") is {} ownedRect)
            {
                VendorButtonText($"OWNED{i:00}","vendorOwned"+i,Data!.OwnedText(item.TemplateId),new(1,1,1),true);
                Hit("vendorSelect/"+item.Slot+"/owned",ownedRect);
            }
            foreach(var (mesh,value) in new[]{($"NAME{i:00}",item?.Name??""),($"COST{i:00}",item is null?"":"Cost: "+item.Price.GetValueOrDefault().ToString("N0")+" each"),($"STACK{i:00}",EnbVendorList.StackText(item?.MaxStack))})
                if(ConversationRect("vendor",mesh) is {} r)
                {if(item is not null){if(mesh.StartsWith("NAME"))VendorName(mesh,"vendorSelect/"+item.Slot+"/"+mesh,value);else VendorButtonText(mesh,"vendor"+mesh,value,new(1,1,1),true);Hit("vendorSelect/"+item.Slot+"/"+mesh,r);}}
        }
        VendorButtonText("CREDITS00","vendorCredits",Data?.Credits?.ToString("N0")??"",new(1,1,1),true);
        VendorButtonText("CREDITS_TITLE00","vendorCreditsTitle","Credits:",new(1,1,1),true);
        if(_tradeSelection?.Inventory==4){ConversationButton("vendor","BUY","vendorBuy","Buy",!_busy);ConversationButton("vendor","BUY_STACK","vendorBuyStack","Buy stack",!_busy);}
        if(_tradeSelection is {Inventory:1} sell && Resolve(sell) is {} selling){ConversationButton("vendor","SELL","vendorSell","Sell",!_busy);if(selling.MaxStack>1 && selling.Count>1)ConversationButton("vendor","SELLALL","vendorSellStack","Sell stack",!_busy);}
        ConversationButton("vendor","EXIT","vendorClose","",!_busy);
        for(int category=0;category<_vendorCategories.Length;category++)
            ConversationButton("vendor",$"CATEGORY{category:00}","vendorCategory/"+category,EnbVendorList.CategoryNames[_vendorCategories[category]],true,true);
        ConversationButton("vendor","SORT","vendorSort",_vendorSort?"Price sort":"Name sort",true,true);
        ConversationButton("vendor","COMPARE","vendorCompare","Compare");
        if(_vendorTooltips is {} tips)
        {
            _tips["vendorCompare"]=tips.GetValueOrDefault("COMPARE","");
            _tips["vendorSort"]=tips.GetValueOrDefault(_vendorSort?"SORT_PRICE":"SORT_NAME","");
        }
        if(_message is not null && ConversationRect("vendor","BODY04") is {} error)Wrap("vendorError",_message,error,new(1,.4f,.4f),false,"FONT_SMALL");
        Flush();
        if(_tradeConfirmation is {} confirm)
        {
            var item=Resolve(confirm.Item);
            string prompt=_tradeConfirmationStage is EnbVendorConfirmationStage.SellOneLoss or EnbVendorConfirmationStage.SellStackLoss
                ? (item is not null && EnbVendorConfirmation.Loss(item.ItemType,confirm.Price,item.AverageCost)==EnbVendorLoss.Significant ? "You will take significant losses on this sale. Do you wish to proceed?" : "You will take a loss on this sale. Do you wish to proceed?")
                : confirm.Item.Inventory==4 && item is not null
                    ? EnbVendorConfirmation.PurchasePrompt(confirm.Price,item.MaxStack??1,item.Name)
                    : EnbVendorConfirmation.SalePrompt(confirm.Price,confirm.Count);
            StandardConfirmationDialog("tradeConfirm",prompt,_tradeDialog,["tradeYes","tradeNo"],qualifyActions:false);Flush();
        }
    }
    private async Task<bool> ActivateConversation(string id,EnbInventoryItemRequest? item)
    {
        if(_conversationOwner is not {} owner)return false;
        if(_tradeConfirmation is not null && id is not ("tradeYes" or "tradeNo" or "vendorClose" or "cargoClose" or "talkClose" or "talkDone"))return true;
        if(_busy)return true;
        if(_trading && item is {} selected){_tradeSelection=selected;return true;}
        if(id.StartsWith("vendorSelect/")){int slot=int.Parse(id.Split('/')[1]);if(Data?.Find(4,slot) is {} ware)_tradeSelection=new(owner.Character,_ship,4,slot,ware.TemplateId);return true;}
        if(id=="talkClose") {await CloseConversation();return true;}
        if(id=="vendorClose" || _trading && id=="cargoClose")
        {
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");_tradeConfirmation=null;_trading=false;CloseVendorCargo();_tradeSelection=null;
            if(_vendorClosesConversation){_conversationCloseSoundPlayed=true;await CloseConversation();}
            return true;
        }
        if(id is "talkMore" or "talkDone")
        {
            if(_conversationWaiting || Conversation?.Tree is not {} currentTree)return true;
            if(_talkPage+1>=_talkPageCount && currentTree.Responses.Count!=0)return true;
            InteractionSoundRequested?.Invoke("fontclick.wav");_talkPage++;
            if(_talkPage>=_talkPageCount)await ContinueDialogue();
            return true;
        }
        if(id=="talkTrade" && Conversation?.TradeAvailable==true){_trading=true;_dialoguePortrait=null;_portraitSpeechPage=null;OpenVendorCargo();_vendorPage=0;_vendorCategory=0;_vendorCompare=false;return true;}
        if(id=="vendorSort")
        {
            _vendorSort=!_vendorSort;_tradeSelection=null;InteractionSoundRequested?.Invoke("fontclick.wav");
            if(VendorSortPersistence is {} save)save(owner.Character,_vendorSort);
            else _assets?.SaveTradeSortByName(owner.Character,_vendorSort);
            return true;
        }
        if(id.StartsWith("vendorCategory/",StringComparison.Ordinal))
        {
            _vendorCategory=Math.Clamp(int.Parse(id.AsSpan(15)),0,Math.Max(0,_vendorCategories.Length-1));
            _vendorCompare=false;_tradeSelection=null;InteractionSoundRequested?.Invoke("fontclick.wav");return true;
        }
        if(id=="vendorCompare")
        {
            if(!_vendorCompare)_tradeSelection=null;
            _vendorCompare=true;InteractionSoundRequested?.Invoke("fontclick.wav");return true;
        }
        if((id.StartsWith("talkReply/") || id=="talkSkill" && Conversation?.SkillAvailable==true) && !_conversationWaiting && Conversation is {Tree:{} tree} c && ReplyRequested is {} reply)
        {
            // 0064BCA0 binds Skill to event 8. 0064D400's default
            // consumer subtracts one and selects response index 7 when present.
            int index=id=="talkSkill"?7:int.Parse(id.AsSpan(10));
            if(index<0||index>=tree.Responses.Count||id!="talkSkill"&&(index>=5||_talkPage+1<_talkPageCount))return true;
            // Retail 0064D67C: accepting a reply uses the text-button cue.
            InteractionSoundRequested?.Invoke("fontclick.wav");
            _conversationWaiting=true;await Send(()=>reply(new(owner,c.Revision,tree.Responses[index].ResponseActionOrId)));
            if(_message is not null)_conversationWaiting=false;return true;
        }
        if(id=="tradeNo"){_tradeConfirmation=null;return true;}
        if(id=="tradeYes" && _tradeConfirmation is {} confirmed && PurchaseRequested is {} confirmPurchase)
        {
            var confirmedItem=Resolve(confirmed.Item);
            var loss=confirmedItem is null?EnbVendorLoss.None:EnbVendorConfirmation.Loss(confirmedItem.ItemType,confirmed.Price,confirmedItem.AverageCost);
            _tradeConfirmationStage=EnbVendorConfirmation.AfterAccept(_tradeConfirmationStage,loss);
            if(_tradeConfirmationStage!=EnbVendorConfirmationStage.None)
            {InteractionSoundRequested?.Invoke("Roll_Select02b.wav");return true;}
            _tradeConfirmation=null;await Send(()=>confirmPurchase(confirmed with{Confirmed=true}));return true;
        }
        if(id is "vendorBuy" or "vendorBuyStack" or "vendorSell" or "vendorSellStack" &&
            _tradeSelection is {} selection && Resolve(selection) is {Price:{} price} current && PurchaseRequested is {} purchase)
        {
            // 00651830 cases 0C..0F play the button cue before either the
            // direct transaction or its confirmation wrapper.
            InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
            bool stack=id.EndsWith("Stack");var request=new EnbStationPurchaseRequest(owner,selection,stack,false,price,current.Count);
            _tradeConfirmationStage=EnbVendorConfirmation.Begin(current.Inventory==4,stack,EnbVendorConfirmation.Loss(current.ItemType,price,current.AverageCost));
            if(_tradeConfirmationStage!=EnbVendorConfirmationStage.None)
            {_tradeConfirmation=request;}
            else await Send(()=>purchase(request));return true;
        }
        return id.StartsWith("talk")||id.StartsWith("vendor");
    }
    private async Task CloseConversation(string cue="Roll_Select02b.wav")
    {
        if(_conversationOwner is not {} owner)return;
        if(owner.StarbaseId is null && !_trading && Conversation?.Tree is not null && ReplyRequested is {} exit)
        {
            // Space talk Exit/Escape (0064D400 events 6/0B): play Roll, queue the E6 selection and keep the
            // dialogue until the server's negative 0056 deactivates it (0064D770 -> +5C -> 0064C8D0, which
            // sends the closing E6). Closing locally at once left the server's talk state open: a later
            // rescue tow was refused (16 Nov 2026 live comparison with retail captures 185606/191413).
            PlayConversationCloseSound(cue);
            // Each Exit press queues E6 again, as 0064D400 does.
            _conversationWaiting=true;
            await Send(()=>exit(new(owner,Conversation?.Revision??0,0xE6){Exit=true}));
            // An unsendable Exit is contained locally, as before.
            if(_message is not null && _conversationOwner==owner){ClearConversation(true);_cargo=false;Publish();}
            return;
        }
        // Retail 0064D458 uses fontclick for More/Done; Exit at 0064D41B uses Roll.
        PlayConversationCloseSound(cue);
        if(ReplyRequested is {} reply)await Send(()=>reply(new(owner,Conversation?.Revision??0,0,true)));
        ClearConversation(true);_cargo=false;Publish();
    }
    private void PlayConversationCloseSound(string cue="Roll_Select02b.wav")
    {
        if(_conversationCloseSoundPlayed)return;
        _conversationCloseSoundPlayed=true;InteractionSoundRequested?.Invoke(cue);
    }
}


