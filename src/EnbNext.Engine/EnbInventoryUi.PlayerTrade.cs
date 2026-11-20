using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string,Point> _playerTradeAnchors=[];
    private EnbInterfaceLayoutCatalog? _playerTradeLabels, _creditLabels;
    private IReadOnlyDictionary<string,string>? _playerTradeTips, _creditTips;
    private EnbPlayerTradeSnapshot? _playerTrade;
    private long _tradeCompletedAt;
    private uint? _tradeVisualPartner;
    private bool _tradeOpenedCargo;
    private bool _creditEntry;
    private string _creditDigits="";
    private int _creditCaret;
    private bool _creditOverwrite;
    private sealed record TradeFont(EnbAttachedTextStyle Style,EnbRgb Color,EnbRgb Alternate,float OpeningBlink);
    private sealed class TradeFontOwner
    {
        public readonly Dictionary<string,EnbAttachedLabelWipe> Wipes=[];
        public readonly Dictionary<string,EnbAttachedLabelAlpha> Alpha=[];
        public readonly Dictionary<string,EnbAttachedLabelScroll> Scroll=[];
    }
    private readonly Dictionary<string,TradeFont> _tradeFonts=[];
    private TradeFontOwner _spaceTradeFonts=new();
    private TradeFontOwner? _stationTradeFonts;
    private readonly HashSet<string> _tradeFontsDrawn=[];
    private TradeFontOwner TradeFonts=>_stationTradeFonts??_spaceTradeFonts;

    private void ResetTradeFonts(){_spaceTradeFonts=new();_stationTradeFonts=null;}
    private void SwitchTradeFontOwner(bool station)
    {
        //0050C8F3/0069BA85 construct separate trade controllers. Closing the
        //dialog (005E2B30) detaches its mesh without replacing its fonts.
        if(station)_stationTradeFonts??=new();
        else _stationTradeFonts=null;
    }
    private void PauseTradeFonts()
    {
        long now=Environment.TickCount64;
        foreach(var owner in new[]{_spaceTradeFonts,_stationTradeFonts})
            if(owner is not null)
            {
                foreach(var (id,wipe) in owner.Wipes)
                    if(owner!=TradeFonts||!_tradeFontsDrawn.Contains(id))wipe.Pause(now);
                foreach(var (id,alpha) in owner.Alpha)
                    if(owner!=TradeFonts||!_tradeFontsDrawn.Contains(id))alpha.Pause(now);
            }
    }
    private static readonly EnbUiRaster StringEntryCaret = new(1,1,System.Collections.Immutable.ImmutableArray.Create<byte>(25,178,0,127));
    public Func<EnbPlayerTradeRequest,Task<bool>>? PlayerTradeRequested {get;set;}

    private void LoadPlayerTrade(EnbRetailAssetBundle assets)
    {
        _playerTradeLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_trade00a.ini"));
        _creditLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_trade01a.ini"));
        var tips=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"tooltip.ini"));
        tips.TryGetSection("UI_PTRADE",out _playerTradeTips);
        tips.TryGetSection("UI_STRINGENTRY",out _creditTips);
        _playerTradeAnchors.Clear();
        _tradeFonts.Clear();ResetTradeFonts();
        var fontArt=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        var definitions=fontArt.EnumerateSections().Select(s=>s.Value).ToArray();
        EnbRgb Color(string id)
        {
            var rgb=definitions.First(d=>d.GetValueOrDefault("ColorID")==id)["RGB"].Split(',');
            return new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
        }
        foreach(var (prefix,file) in new[]{("playerTrade","trade00a.w3d"),("tradeCredits","trade01a.w3d")})
        {
            var asset=EnbAuthoredUiAsset.Load(assets,file)??throw new InvalidDataException(file);
            var layout=prefix=="playerTrade"?_playerTradeLabels:_creditLabels;
            var ini=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,prefix=="playerTrade"?"fa_trade00a.ini":"fa_trade01a.ini"));
            foreach(var entry in ini.EnumerateSections())
            {
                if(entry.Value.GetValueOrDefault("StringID") is not {} labelId||!layout.TryResolve(labelId,out var label))continue;
                if(!asset.Hierarchy.Pivots.Any(p=>p.Name.Equals(label.Properties["Attach"],StringComparison.OrdinalIgnoreCase)))continue;
                float blink=0;
                foreach(var effect in definitions)
                    if(effect.GetValueOrDefault("EffectType")=="BLINK"&&label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==effect.GetValueOrDefault("EffectID")))
                        blink=float.Parse(effect["Speed"],CultureInfo.InvariantCulture);
                _tradeFonts[prefix+"/"+labelId]=new(
                    EnbAttachedTextStyle.Load(assets,asset,label,Matrix4x4.Identity),
                    Color(label.Properties["Color"]),Color(label.Properties["AltColor"]),blink);
            }
            var transforms=EnbCockpitUi.ResolveTransforms(asset);
            foreach(var pivot in asset.Hierarchy.Pivots)
            {
                var p=Vector3.Transform(Vector3.Zero,transforms[pivot.Ordinal]);
                _playerTradeAnchors[prefix+"/"+pivot.Name]=new(p.X+320,240-p.Y);
            }
            foreach(var mesh in asset.Resource.Meshes.Where(m=>asset.Hierarchy.Pivots.Any(p=>p.Name==m.Name)))
                _art[prefix+"/"+mesh.Name]=new(asset with {Resource=asset.Resource.Copy().SetMeshes(new[]{mesh})},
                    EnbCockpitUi.MeshBounds(asset,mesh.Name).Translate(new(-320,-240)));
        }
    }

    private void UpdatePlayerTrade()
    {
        var next=Data?.PlayerTrade;
        if(next is {PartnerId:not null})
        {
            if(_playerTrade?.Revision!=next.Revision || _playerTrade.PartnerId!=next.PartnerId)
            {
                _creditEntry=false;_tradeCompletedAt=0;_reloadItem=null;CancelPointer();
                OpenTradeCargo();if(!ModernUi)_equip=_character=_filterOpen=false;Opened?.Invoke();
            }
            _playerTrade=next;
            _tradeVisualPartner=next.PartnerId;
        }
        else if(_playerTrade is {PartnerId:not null} old && next is {Successful:true} && old.Revision==next.Revision)
        {
            // 005E3130: retain the completed view for two seconds, with controls disabled.
            _playerTrade=old with {PartnerId=null,Successful=true,StatusMessage=next.StatusMessage};_tradeCompletedAt=Environment.TickCount64;
            _creditEntry=false;_reloadItem=null;CancelPointer();
        }
        //739DE0 type1 invokes5E2B30 immediately, including during completion.
        else if(next is {Successful:false} || _playerTrade is not {Successful:true} || Environment.TickCount64-_tradeCompletedAt>=2000)
        { if(_playerTrade is not null)CloseTradeCargo();_playerTrade=null;_creditEntry=false; }
        else if(next is not null && next.Revision==_playerTrade.Revision)
            _playerTrade=_playerTrade with {StatusMessage=next.StatusMessage};
    }

    private void PlayerTradePanel()
    {
        if(_playerTrade is not {} trade)return;
        const string p="playerTrade/";
        bool enabled=trade.PartnerId is not null && !_creditEntry && !_busy;
        Draw(p+"FRAME_DETAIL",Native(p+"FRAME_DETAIL"));Hit("playerTradeFrame",Native(p+"FRAME_DETAIL"));
        foreach(var (frame,picture,local) in new[]{("FRAME_DETAIL01","AVATAR_YOU",true),("FRAME_DETAIL02","AVATAR_THEM",false)})
        {
            Draw(p+frame,Native(p+frame));
            var obj=_scene?.Objects.FirstOrDefault(o=>local?o.IsLocalShip:o.GameId==_tradeVisualPartner);
            if(obj?.Avatar is {} avatar)
            {
                var bounds=Native(p+picture);
                _draws[^1]=_draws[^1] with {PortraitCacheKey=local?"trade/local":"trade/peer",Portrait=avatar,PortraitEnvironment=_scene!.Environment,
                    PortraitSharedActor=true,PortraitStationLocal=local && _scene.ActiveStarbaseId is not null,
                    PortraitStationRemoteId=!local && _scene.ActiveStarbaseId is not null?obj.GameId:null,
                    PortraitPose=_scene.ActiveStarbaseId is null?_callerPortrait.PoseFor(obj.RenderIdentity):null,
                    PortraitBounds=(Screen(bounds)),PortraitAspect=bounds.Width/bounds.Height};
            }
        }
        for(int i=0;i<12;i++)
        {
            string frame=p+"FRAME"+i.ToString("00"),id="playerTradeSlot/"+i;
            Draw(frame,Native(frame));
            // Retail binds column-major C00..C05 and X00..X05 attachments.
            string attachment=p+(i<6?"C":"X")+(i%6).ToString("00");
            var item=trade.PartnerId is not null && !_creditEntry?(i<6?trade.Give:trade.Receive).FirstOrDefault(x=>x.Slot==i%6):null;
            Slot(id,Native(attachment),item,terminalSlot:true);
            if(i<6 && enabled)_dropSlots[id]=(16,i);
        }
        Draw(p+"FRAME_MONEY",Native(p+"FRAME_MONEY"));
        Draw(p+"MONEY",Native(p+"MONEY"),"playerTradeMoney",enabled);
        Draw(p+"EXIT",Native(p+"EXIT"),"playerTradeClose",enabled);
        foreach(string key in new[]{"CANCEL","CONFIRM"})
        {
            string id="playerTrade"+(key=="CANCEL"?"Cancel":"Confirm");
            Draw(p+key,Native(p+key),id,enabled);
            TradeLabel(key+"_TRADE00",null,button:id,disabled:!enabled);
            _tips[id]=_playerTradeTips?.GetValueOrDefault(key,"")??"";
        }
        _tips["playerTradeClose"]=_playerTradeTips?.GetValueOrDefault("CANCEL","")??"";
        _tips["playerTradeMoney"]=_playerTradeTips?.GetValueOrDefault("CREDITS","")??"";
        TradeLabel("PLAYER_NAME_TRADE00",Data?.CharacterStats.GetValueOrDefault("Name","")??"");
        TradeLabel("RECIPIENT_NAME_TRADE00",_scene?.Objects.FirstOrDefault(o=>o.GameId==_tradeVisualPartner)?.Name??"");
        foreach(string id in new[]{"GIVE_TRADE00","RECEIVE_NAME_TRADE00","CREDITS_TIT_TRADE00","CRED1TIT_TRADE00","CRED2TIT_TRADE00"})TradeLabel(id);
        TradeLabel("CREDITS_TRADE00",Data?.Credits?.ToString(CultureInfo.InvariantCulture)??"0");
        TradeLabel("CRED1_TRADE00",trade.OfferedCredits.ToString(CultureInfo.InvariantCulture));
        TradeLabel("CRED2_TRADE00",trade.ReceivedCredits.ToString(CultureInfo.InvariantCulture));
        TradeLabel("REC_CONFIRM_TRADE00",trade.StatusText);
        if(trade.Successful==true)_animatingText=true;
        Flush();
    }

    private void TradeLabel(string id,string? value=null,bool popup=false,string? button=null,bool disabled=false)
    {
        var layout=popup?_creditLabels:_playerTradeLabels;
        if(layout?.TryResolve(id,out var d)!=true)return;
        string prefix=popup?"tradeCredits/":"playerTrade/";
        if(!_playerTradeAnchors.TryGetValue(prefix+d.Properties["Attach"].ToUpperInvariant(),out var anchor))return;
        value??=d.Properties.GetValueOrDefault("String0","");
        string key=prefix+id;
        if(!_tradeFonts.TryGetValue(key,out var definition))return;
        var owner=TradeFonts;var style=definition.Style;
        if(!owner.Alpha.TryGetValue(key,out var alpha))owner.Alpha[key]=alpha=new();
        if(!owner.Scroll.TryGetValue(key,out var scroll))owner.Scroll[key]=scroll=new();
        EnbAttachedLabelWipe? wipe=null;
        if(style.OpeningWipeRightSpeed>0&&!owner.Wipes.TryGetValue(key,out wipe))owner.Wipes[key]=wipe=new();
        //0066B840 (flags3) and0066C2D0/360 select authored alternate ink
        //on disable/press. Hover BLINK is admitted only in the constant stage.
        bool hovered=!disabled&&button is not null&&_hover==button&&(wipe is null||wipe.Complete);
        bool blinking=definition.OpeningBlink>0||hovered;
        float opacity=alpha.Update(Environment.TickCount64,blinking,0,1,0,
            definition.OpeningBlink>0?definition.OpeningBlink:style.BlinkSpeed,style.RevertSpeed);
        int first=_images.Count;
        AttachedText(key,value,style,scroll,disabled||button is not null&&_pressed==button?definition.Alternate:definition.Color,wipe,fixedLabel:true);
        for(int i=first;i<_images.Count;i++)_images[i]=_images[i] with {Opacity=opacity};
        _tradeFontsDrawn.Add(key);
        _animatingText|=blinking||opacity<1;
    }

    private void TradeCreditPanel()
    {
        const string p="tradeCredits/";
        Flush();Hit("tradeCreditModal",new(0,0,640,480));
        Draw(p+"FRAME_DETAIL",Native(p+"FRAME_DETAIL"));
        Draw(p+"EXIT",Native(p+"EXIT"),"tradeCreditClose");
        Draw(p+"VALUE_BUTTON",Native(p+"VALUE_BUTTON"),"tradeCreditField");
        TradeLabel("TITLE01","Enter Amount of Credits",true);
        foreach(string key in new[]{"CANCEL","CONFIRM"})
        {
            string id="tradeCredit"+(key=="CANCEL"?"Cancel":"Confirm");
            Draw(p+key,Native(p+key),id);TradeLabel(key+"_TRADE01",popup:true,button:id);
            _tips[id]=_creditTips?.GetValueOrDefault(key,"")??"";
        }
        _tips["tradeCreditField"]=_creditTips?.GetValueOrDefault("VALUE_BUTTON","")??"";
        _tips["tradeCreditClose"]=_creditTips?.GetValueOrDefault("EXIT","")??"";
        StringEntryText(_creditDigits,_creditCaret,!_creditOverwrite);
        Flush();
    }

    internal async Task<bool> TradeCreditKey(ushort key)
    {
        if(!_creditEntry)return false;
        // Next Modern UI credit entry: Escape keeps the current offer (its Cancel); Enter and Tab offer.
        if(ModernUi && key==EnbInventoryKeys.Escape){await ActivatePlayerTrade("tradeCreditCancel");Publish();return true;}
        // 006AA200 ends editor focus for Return, Escape and unlinked Tab.
        // 00649FE0 accepts that focus completion as result 2; only the
        // explicit Cancel/close gadgets return result 1.
        if(key is EnbInventoryKeys.Enter or EnbInventoryKeys.Escape or EnbInventoryKeys.Tab)
        {await ActivatePlayerTrade("tradeCreditConfirm");Publish();return true;}
        if(key==EnbInventoryKeys.Insert)_creditOverwrite=!_creditOverwrite;
        else if(key==EnbInventoryKeys.Left)_creditCaret=Math.Max(0,_creditCaret-1);
        else if(key==EnbInventoryKeys.Right)_creditCaret=Math.Min(_creditDigits.Length,_creditCaret+1);
        else if(key==EnbInventoryKeys.Home)_creditCaret=0;
        else if(key==EnbInventoryKeys.End)_creditCaret=_creditDigits.Length;
        else if(key==EnbInventoryKeys.Back && _creditCaret>0)_creditDigits=_creditDigits.Remove(--_creditCaret,1);
        else if(key==EnbInventoryKeys.Delete && _creditCaret<_creditDigits.Length)_creditDigits=_creditDigits.Remove(_creditCaret,1);
        else if(key is >=EnbInventoryKeys.D0 and <=EnbInventoryKeys.D9) CreditDigit((int)(key-EnbInventoryKeys.D0));
        else if(key is >=EnbInventoryKeys.NumPad0 and <=EnbInventoryKeys.NumPad9) CreditDigit((int)(key-EnbInventoryKeys.NumPad0));
        Publish();return true;
    }

    private void CreditDigit(int digit)
    {
        if (_creditOverwrite && _creditCaret < _creditDigits.Length)
            _creditDigits=_creditDigits.Remove(_creditCaret,1).Insert(_creditCaret++,digit.ToString(CultureInfo.InvariantCulture));
        else if (_creditDigits.Length<9)
            _creditDigits=_creditDigits.Insert(_creditCaret++,digit.ToString(CultureInfo.InvariantCulture));
    }

    private void StringEntryText(string text,int caret,bool insert)
    {
        // 00649AA0's modes all construct centered 006AA810 gadgets on TEXT_BOX.
        // 00649FE0 updates that gadget; no underscore is inserted into the draft.
        if (_creditLabels?.TryResolve("VALUE_BUTTON01",out var definition)!=true) return;
        string fontId=definition.Properties.GetValueOrDefault("FontType","FONT_SMALL");
        if (Font(fontId) is not {} font) return;
        double ux=_size.Width/640,uy=_size.Height/480;
        float Width(string value)=>(float)(font.MeasureWidth(value)*font.Scale/ux);
        float Advance(char c)=>(float)(font.CharacterAdvance(c)*font.Scale/ux);
        var window=EnbRetailChatInputViewport.Resolve(text,caret,(float)Native("tradeCredits/TEXT_BOX").Width,Width,Advance);
        string shown=text.Substring(window.Start,window.Length);
        int column=Math.Clamp(caret-window.Start,0,shown.Length);
        var anchor=_playerTradeAnchors["tradeCredits/"+definition.Properties["Attach"].ToUpperInvariant()];
        string colorId=definition.Properties.GetValueOrDefault("AltColor",definition.Properties["Color"]);
        var color=_friendColors[colorId];
        float width=Width(shown);
        Text("tradeCredits/VALUE_BUTTON01",shown,new(anchor.X-width/2,anchor.Y,Math.Max(1,width),0),color,fontId,false);
        var rectangle=EnbRetailCaretGeometry.Resolve(new((float)anchor.X,(float)-anchor.Y),width,Width(shown[..column]),
            Advance(column<shown.Length?shown[column]:'n'),(float)(font.CellHeight*font.Scale/uy),true,true,insert);
        _images.Add(new(StringEntryCaret,(Screen(new(rectangle.X,-rectangle.Y,rectangle.Z,rectangle.W)))));
    }

    private async Task<bool> ActivatePlayerTrade(string id)
    {
        if(_creditEntry)
        {
            if(id=="tradeCreditField")
            {
                // 0064A110, action 4: every click plays this cue and clears the edit buffer.
                InteractionSoundRequested?.Invoke("message_1.wav");_creditDigits="";_creditCaret=0;
            }
            else if(id is "tradeCreditCancel" or "tradeCreditClose")
            {InteractionSoundRequested?.Invoke("Roll_Select02b.wav");_creditEntry=false;}
            else if(id=="tradeCreditConfirm")
            {
                InteractionSoundRequested?.Invoke("Roll_Select02b.wav");
                long amount=long.TryParse(_creditDigits,out var n)?n:0;
                _creditEntry=false;
                await SendPlayerTrade(EnbPlayerTradeAction.Credits,amount);
            }
            return true;
        }
        if(!id.StartsWith("playerTrade",StringComparison.Ordinal))return false;
        if(_busy || _playerTrade?.PartnerId is null)return true;
        switch(id)
        {
            case "playerTradeClose":case "playerTradeCancel":
                InteractionSoundRequested?.Invoke("Roll_Select02b.wav");await SendPlayerTrade(EnbPlayerTradeAction.Cancel);break;
            case "playerTradeConfirm":
                InteractionSoundRequested?.Invoke("Roll_Select02b.wav");await SendPlayerTrade(EnbPlayerTradeAction.Confirm);break;
            case "playerTradeMoney":
                InteractionSoundRequested?.Invoke("Roll_Select02b.wav");_creditEntry=true;
                _creditDigits=_playerTrade.OfferedCredits==0?"":_playerTrade.OfferedCredits.ToString(CultureInfo.InvariantCulture);_creditCaret=_creditDigits.Length;break;
        }
        return true;
    }

    private Task SendPlayerTrade(EnbPlayerTradeAction action,long credits=0)
    {
        if(_owner is not uint owner || _playerTrade is not {PartnerId:{} partner} trade || PlayerTradeRequested is not {} send)return Task.CompletedTask;
        return Send(()=>send(new(owner,_ship,trade.Revision,partner,action,credits)),()=>_playerTrade?.Revision==trade.Revision && _playerTrade.PartnerId==partner);
    }
}
