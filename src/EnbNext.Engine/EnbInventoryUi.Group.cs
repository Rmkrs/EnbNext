using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

// Shares the retained GPU mesh/text and hit-test machinery with the other authored panels.
internal sealed partial class EnbInventoryUi
{
    private EnbRetainedGroupMembers _groupMembers=new();
    public event Action<uint,bool>? RadarLabelsInvalidated;
    private Dictionary<(int Index,string Kind),EnbRetailGaugeTrail> _groupGaugeTrails=[];
    private (string Name,bool Present)[] _groupGaugeMembers=[];
    private ulong _groupGaugeRevision;
    private Dictionary<long,string> _groupMemberLabels=[];
    public Action<uint,EnbSceneAvatarSnapshot>? StationGroupAvatarAcquisitionRequested {get;set;}
    private uint _groupGaugeTime;
    private readonly Dictionary<string,Point> _groupAnchors=[];
    private readonly Dictionary<string,float> _groupDepths=[];
    private EnbInterfaceLayoutCatalog? _groupLabels;
    private EnbInterfaceLayoutCatalog? _groupDialogLabels;
    private EnbRetailScannerTooltipDefinition? _groupHealthTooltip;
    private string _groupContextTip="";
    private IReadOnlyDictionary<string,string>? _groupTips, _groupInviteDialog, _groupLeaveDialog;
    private bool _groupFormationOpen, _groupLeaveConfirm;
    private long? _answeredGroupInvite;
    private Point? _groupSymbolOrigin;
    private readonly List<(int Section,string Text,string Asset)> _groupSymbols=[];
    private sealed record ReceivedGroupSymbol(string Asset,bool Context,long Started)
    {
        public EnbGroupSymbolLifetime Lifetime {get;}=new();
    }
    private readonly Dictionary<string,ReceivedGroupSymbol> _receivedGroupSymbols=new(StringComparer.Ordinal);
    private uint _groupSymbolTime;
    private Guid _groupSymbolStream;
    private long _groupSymbolSequence;
    private string LoadGroupSymbolArt(string name)
    {
        string key="groupSymbol/"+name;
        if(!_art.ContainsKey(key) && !_missingGroupSymbolArt.Contains(key) && _assets is {} assets)
        {
            try
            {
                if(EnbAuthoredUiAsset.Load(assets,name+".w3d",allowStandalone:true) is {} asset && asset.Resource.Meshes.Count>0)
                {
                    var bounds=asset.Resource.Meshes.Select(m=>EnbCockpitUi.MeshBounds(asset,m.Name)).Aggregate((a,b)=>a.Union(b));
                    _art[key]=new(asset,bounds.Translate(new(-320,-240)));
                }
                else _missingGroupSymbolArt.Add(key);
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException)
            { _missingGroupSymbolArt.Add(key); }
        }
        return key;
    }
    private void UpdateGroupSymbols(EnbSceneGroupSnapshot group,long now,EnbRetainedGroupMembers members)
    {
        uint symbolNow=unchecked((uint)now),symbolElapsed=unchecked(symbolNow-_groupSymbolTime);_groupSymbolTime=symbolNow;
        if(_groupSymbolStream!=group.SymbolStream){_receivedGroupSymbols.Clear();_groupSymbolSequence=0;_groupSymbolStream=group.SymbolStream;}
        foreach(var symbol in group.Symbols.Where(s=>s.Sequence>_groupSymbolSequence))
        {
            _groupSymbolSequence=symbol.Sequence;
            if(!members.Items.Any(m=>m.Name==symbol.Member))continue;
            if(_art.ContainsKey(LoadGroupSymbolArt(symbol.Asset)))
                _receivedGroupSymbols[symbol.Member]=new(symbol.Asset,symbol.Context,now);
            else _receivedGroupSymbols.Remove(symbol.Member);
            InteractionSoundRequested?.Invoke("symbol_chat_1.wav");
        }
        // Packets are drained before the active group frame (742B60/7427C0).
        // A newly admitted symbol also consumes this frame's elapsed time.
        foreach(var retained in _receivedGroupSymbols.ToArray())
        {
            if(!members.Items.Any(m=>m.Name==retained.Key)){_receivedGroupSymbols.Remove(retained.Key);continue;}
            retained.Value.Lifetime.Advance(symbolElapsed);
            if(retained.Value.Lifetime.Expired)_receivedGroupSymbols.Remove(retained.Key);
        }

    }
    private readonly HashSet<string> _missingGroupSymbolArt=new(StringComparer.Ordinal);
    public Func<EnbGroupRequest,Task<bool>>? GroupRequested {get;set;}
    public void FormationToggleCompleted()
    {
        _groupFormationOpen=false;
        InteractionSoundRequested?.Invoke("fontclick.wav");
    }
    private bool StationGroup => _scene?.ActiveStarbaseId is not null;
    private EnbSceneGroupSnapshot? Group => _scene?.Hud.Group??(_layoutPreviewOwner?LayoutGroup:null);
    internal bool GroupConfirmationOpen => _groupLeaveConfirm ||
        Group?.Invitation is not null && Group.InvitationRevision!=_answeredGroupInvite;
    internal async Task GroupConfirmationKey(ushort key)
    {
        bool invite=Group?.Invitation is not null && Group.InvitationRevision!=_answeredGroupInvite;
        var dialog=invite?_groupInviteDialog:_groupLeaveDialog;
        for(int index=0;index<2;index++)
            if(dialog?.ContainsKey("Option"+index)==true &&
                ParseRetailKey(dialog.GetValueOrDefault("Key"+index,""))==key)
            {
                if(!_busy)
                {
                    string action=invite?(index==0?"Accept":"Reject"):(index==0?"Leave":"CancelLeave");
                    await ActivateGroup("group/"+action,false);
                    ClearHover();Publish();
                }
                return;
            }
    }
    private bool HasGroupUi => _groupMembers.Items.Count!=0 || Group is {IsInGroup:true} || Group?.Invitation is not null && Group.InvitationRevision!=_answeredGroupInvite;

    private void LoadGroup(EnbRetailAssetBundle assets)
    {
        _groupGaugeTrails.Clear();_groupGaugeMembers=[];_groupFormationSource=null;_missingFormationArt.Clear();
        _groupHealthTooltip=EnbRetailScannerTooltipDefinition.Load(assets.Paths.ClientIniDirectory);
        _groupLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_tsi03a.ini"));
        _groupDialogLabels=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_dialogbox.ini"));
        _confirmationColors.Clear();
        foreach(var section in EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini")).EnumerateSections().Select(s=>s.Value))
            if(section.GetValueOrDefault("ColorID") is {} colorId && section.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
                _confirmationColors[colorId]=new(byte.Parse(rgb[0])/255f,byte.Parse(rgb[1])/255f,byte.Parse(rgb[2])/255f);
        var tooltips=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"tooltip.ini"));
        tooltips.TryGetSection("UI_FORMATION",out _groupTips);
        tooltips.TryGetSection("UI_GROUP",out var contextTips);
        _groupContextTip=contextTips?.GetValueOrDefault("CONTEXT","")??"";
        var dialogs=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"dialogbox.ini"));
        dialogs.TryGetSection("GROUP_INVITE",out _groupInviteDialog);dialogs.TryGetSection("GROUP_LEAVE",out _groupLeaveDialog);
        dialogs.TryGetSection("CONFIRMED_ACTION_DIALOG",out _confirmedActionDialog);
        var symbols=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"symbolchatdef.ini"));
        _groupSymbols.Clear();
        foreach(var (prefix,section) in new[]{("GENERIC",0),("TEAM",1),("CONTEXT",2)})
            for(int i=0;symbols.TryGetSection(prefix+"_INFO_"+i,out var symbol);i++)
                _groupSymbols.Add((section,symbol.GetValueOrDefault("SymbolString", ""),symbol.GetValueOrDefault("SymbolAsset", "")));
        _groupAnchors.Clear();
        _groupDepths.Clear();
        _missingGroupSymbolArt.Clear();
        foreach(var (prefix,file) in new[]{("group","tsi03a.w3d"),("groupRow","tsi03a_frame.w3d")})
        {
            var asset=EnbAuthoredUiAsset.Load(assets,file)??throw new InvalidDataException(file);
            if(prefix=="group")LoadGroupFonts(assets,asset);
            var transforms=EnbCockpitUi.ResolveTransforms(asset);
            foreach(var pivot in asset.Hierarchy.Pivots)
            {
                var p=Vector3.Transform(Vector3.Zero,transforms[pivot.Ordinal]);
                _groupAnchors[prefix+"/"+pivot.Name]=new(p.X+320,240-p.Y);
                _groupDepths[prefix+"/"+pivot.Name]=p.Z;
            }
            foreach(var mesh in asset.Resource.Meshes.Where(m=>asset.Hierarchy.Pivots.Any(p=>p.Name==m.Name)))
                _art[prefix+"/"+mesh.Name]=new(asset with {Resource=asset.Resource.Copy().SetMeshes(new[]{mesh})},
                    EnbCockpitUi.MeshBounds(asset,mesh.Name).Translate(new(-320,-240)));
        }
    }

    private EnbGroupFrameClock _groupFrameClock=new();
    internal Func<uint> GroupFrameTime {get;set;}=()=>unchecked((uint)Environment.TickCount64);
    internal void PauseGroupFrame()
    {
        uint now=GroupFrameTime();_groupFrameClock.Advance(now,false);
        AdvanceGroupFontClocks(now,false);
        if(StationGroup)_spaceGroupVisual.Clock.Advance(now,false);
    }
    // Next Modern UI: in space the Modern UI group frame previews and shows the members (EnbInventoryUi.ModernGroup).
    // The edit mode preview (a full group) also stands in for the group in the panel preview owner, which has no layout.
    private void GroupPanel(){if(UiLayout?.Editing==true||_layoutPreviewOwner){if(!ModernGroupShown)LayoutGroupPreview();}else GroupPanelAt(_groupFrameClock.Time);}

    private void GroupPanelAt(long now)
    {
        if(Group is not {} group || _groupMembers.Items.Count==0)
        {
            AdvanceGroupAttention(unchecked((uint)now));
            _groupFormationOpen=_groupLeaveConfirm=false;_groupSymbolOrigin=null;_modernMembers=[];
            _groupGaugeTrails.Clear();_groupGaugeMembers=[];_groupMemberLabels.Clear();
            if(!StationGroup)
            {
                _receivedGroupSymbols.Clear();
                if(Group is {} absent){_groupSymbolStream=absent.SymbolStream;_groupSymbolSequence=absent.Symbols.IsEmpty?0:absent.Symbols[^1].Sequence;}
            }
            return;
        }
        var gaugeMembers=_groupMembers.Items
            .Select(m=>(m.Name,_scene?.Objects.Any(o=>o.GameId==m.GameId)==true)).ToArray();
        uint gaugeNow=unchecked((uint)now);
        // 596770 rebuilds member widgets on a GroupInfo update; 5944D0
        // also rebuilds all widgets when any member enters/leaves object range.
        if(_groupGaugeRevision!=group.AuxRevision || !_groupGaugeMembers.SequenceEqual(gaugeMembers))
        {
            RebuildGroupAttentionWidgets();_groupGaugeTrails.Clear();_groupGaugeRevision=group.AuxRevision;_groupGaugeMembers=gaugeMembers;_groupGaugeTime=gaugeNow;
            // 595530 uses cached avatar+1CC (AvatarData first name), falling
            // back to the membership name only when that avatar is absent.
            // Labels are retained until this widget reconstruction path runs.
            _groupMemberLabels=_groupMembers.Items.ToDictionary(m=>m.PresentationLifetime,
                m=>_scene!.AvatarFirstNames.TryGetValue(m.GameId,out var name)?name:m.Name);
            foreach(var member in _groupMembers.Items)
                if(_scene!.AvatarDescriptions?.GetValueOrDefault(member.GameId) is {} appearance && _assets is {} bundle)
                {
                    if(StationGroup)StationGroupAvatarAcquisitionRequested?.Invoke(member.GameId,appearance);
                    else _callerPortrait.AcquireGroupAvatar(_scene,bundle,member.GameId,Environment.TickCount64*.001);
                }
        }
        uint gaugeElapsed=unchecked(gaugeNow-_groupGaugeTime);_groupGaugeTime=gaugeNow;

        AdvanceGroupAttention(gaugeNow);
        if(!StationGroup)UpdateGroupSymbols(group,now,_groupMembers);
        bool available=!_groupLeaveConfirm && Group.Invitation is null;
        if(StationGroup)_groupFormationOpen=false;
        if(ModernGroupShown){_groupFormationOpen=false;ModernGroupRows(now);return;}
        else _modernMembers=[];
        Draw("group/FORMATION_BUTTO",Native("group/FORMATION_BUTTO"));
        GroupButton("DISBAND_ICON","LeavePrompt",group.IsLeader?"GRP_DISBAND":"GRP_LEAVE",available);
        DrawGroupFormation(now,available);
        GroupButton("TARGET","AskGroup","GROUP_HELP",available && !StationGroup);
        if(_groupFormationOpen)
        {
            foreach(var (mesh,action,tip,leader) in new[]{("FORM_UP","Form","FORM",false),("LEAVE","LeaveFormation","LEAVE",false),
                ("DISBAND","BreakFormation","BREAK",true),("FORMATION01","Slot","SLOT",true),("FORMATION02","Pipe","PIPE",true),("FORMATION03","Block","BLOCK",true)})
                GroupButton(mesh,action,tip,available && CanGroupFormation(action));
            foreach(var (label,button) in new[]{("FORM_UP_TSI03A","Form"),("LEAVE_TSI03A","LeaveFormation"),("DISBAND_TSI03A","BreakFormation")})
                GroupLabel(label,null,!CanGroupFormation(button));
        }
        for(int displayIndex=0;displayIndex<_groupMembers.Items.Count;displayIndex++)
        {
            var member=_groupMembers.Items[displayIndex];
            int firstDraw=_draws.Count;
            var origin=_groupAnchors["group/GROUP"+displayIndex.ToString("00")];
            var offset=new Vector(origin.X-320,origin.Y-240);
            Rect Row(string mesh)
            {
                var r=Native("groupRow/"+mesh).Translate(offset);
                // ClientViewConfigureCameras 0065AB10: UI camera Z=320,
                // aperture +/-1,+/-.75. These row meshes are planar.
                double z=_groupDepths["group/GROUP"+displayIndex.ToString("00")]+_groupDepths["groupRow/"+mesh];
                double perspective=320/(320-z);
                return new(320+(r.X-320)*perspective,240+(r.Y-240)*perspective,r.Width*perspective,r.Height*perspective);
            }
            string id="group/TargetMember/"+displayIndex;
            Draw("groupRow/GROUP",Row("GROUP"));
            DrawRemoteShortcuts(member.GameId,Row("GROUP"));
            Draw("groupRow/PLAYER01",Row("PLAYER01"));
            if(!StationGroup)Hit(id,Row("PLAYER01"));
            var vitals=_scene?.Objects.FirstOrDefault(o=>o.GameId==member.GameId)?.TargetVitals;
            Draw("groupRow/MAIN",Row("MAIN"));
            foreach(string kind in new[]{"HULL","SHIELD"})
            {
                float shield=vitals?.ShieldPercent??0;
                float fraction=kind=="HULL"?(vitals?.MaximumHullPoints>=1?(vitals.HullPoints??0)/vitals.MaximumHullPoints.Value:0):shield;
                var gaugeKey=(displayIndex,kind);
                if(!_groupGaugeTrails.TryGetValue(gaugeKey,out var gauge))
                    _groupGaugeTrails[gaugeKey]=gauge=new();
                float trailing=gauge.Update(fraction,gaugeElapsed);
                _animatingText|=gauge.Active;
                foreach(int trail in new[]{1,0})
                {
                    string mesh=kind+"_BAR"+trail;Draw("groupRow/"+mesh,Row(mesh));
                    _draws[^1]=_draws[^1] with {Stage0UvOffset=new(0,EnbCurrentRetailGaugeRuntime.TextureOffset(trail==1?trailing:fraction)),AnimateMaterials=true};
                }
                string hit="group/"+kind+"/"+displayIndex;Hit(hit,Row(kind+"_BAR0"));
                var current=kind=="HULL"?vitals?.HullPoints:vitals?.MaximumShieldPower*shield;
                var maximum=kind=="HULL"?vitals?.MaximumHullPoints:vitals?.MaximumShieldPower;
                if(_groupHealthTooltip is {} tooltip)_tips[hit]=tooltip.FormatGauge(kind=="HULL",current,maximum);
            }
            GroupLabel("NAME"+displayIndex.ToString("00")+"_TSI03A",_groupMemberLabels.GetValueOrDefault(member.PresentationLifetime,member.Name),false);
            foreach(var (mesh,action,tip) in new[]{("P_INTARGET01","Assist","ASSIST"),("P_OUTTARGET01","AskMember","HELP"),("P_BOOT01","Kick","BOOT")})
            {
                if(action=="Kick"&&!group.IsLeader)continue;
                string hit="group/"+action+"/"+displayIndex;
                Draw("groupRow/"+mesh,Row(mesh),StationGroup && action!="Kick"?null:hit,available && (!StationGroup || action=="Kick") && (member.GameId!=0||action=="Kick"));
                if(action=="Assist" && _spaceGroupVisual.MemberAttention.TryGetValue(member.PresentationLifetime,out var attention))
                    ApplyGroupAttention(attention,hit,GroupFrameTime());
                _tips[hit]=_groupTips?.GetValueOrDefault(tip,"")??"";
            }
            for(int draw=firstDraw;draw<_draws.Count;draw++)_draws[draw]=_draws[draw] with {CompositeOpaqueCoverage=true};
            if(!StationGroup && _receivedGroupSymbols.TryGetValue(member.Name,out var received))
            {
                string key=LoadGroupSymbolArt(received.Asset);
                if(!_art.ContainsKey(key))_receivedGroupSymbols.Remove(member.Name);
                else
                {
                    _animatingText|=received.Lifetime.Remaining>0;
                    var icon=_groupAnchors["groupRow/ICON"];
                    var r=Native(key).Translate(offset+new Vector(icon.X-320,icon.Y-240));
                    double z=_groupDepths["group/GROUP"+displayIndex.ToString("00")]+_groupDepths["groupRow/ICON"];
                    double perspective=320/(320-z);
                    r=new(320+(r.X-320)*perspective,240+(r.Y-240)*perspective,r.Width*perspective,r.Height*perspective);
                    Draw(key,r);
                    _draws[^1]=_draws[^1] with {AnimateMaterials=true,MaterialTime=unchecked((uint)(now-received.Started))};
                    if(received.Context)
                    {
                        string contextId="group/ContextSymbol/"+displayIndex;
                        Hit(contextId,r);_tips[contextId]=_groupContextTip;
                    }
                }
            }
        }
        Flush();
    }

    private void GroupButton(string mesh,string action,string tip,bool enabled)
    {
        string id="group/"+action;Draw("group/"+mesh,Native("group/"+mesh),enabled?id:null,enabled);
        _tips[id]=_groupTips?.GetValueOrDefault(tip,"")??"";
    }
    private bool CanGroupFormation(string action)=>!StationGroup && Group is {} group && (action switch
    {
        "Pipe" or "Slot" or "Block"=>group.IsLeader,
        "BreakFormation"=>group.IsLeader && group.Formation!=0,
        "LeaveFormation"=>!group.IsLeader && group.Formation!=0,
        "Form"=>!group.IsLeader && group.Formation==0 && group.FormationName.Length!=0,
        _=>false
    });
    private void GroupLabel(string id,string? value,bool disabled)
    {
        GroupFont(id,value,disabled,true);
    }
    private void GroupDialog()
    {
        string? invitation=Group?.Invitation;
        if(invitation is null)_answeredGroupInvite=null;
        bool invite=invitation is not null && Group!.InvitationRevision!=_answeredGroupInvite;
        if(!invite&&!_groupLeaveConfirm)return;
        var definition=invite?_groupInviteDialog:_groupLeaveDialog;
        string message=invite?invitation!:Group?.IsLeader==true?"Are you sure you want to disband this group?":"Are you sure you want to leave this group?";
        if(ModernUi){ModernGroupQuestion(invite,message,definition);return;} // Next Modern UI: the same question in a Modern UI window.
        Flush();Hit("group/Modal",new(0,0,640,480));
        StandardConfirmationDialog("group", message, definition,
            invite ? ["Accept", "Reject"] : ["Leave", "CancelLeave"]);
        Flush();
    }
    private async Task<bool> ActivateGroup(string id,bool right,bool contextAssist=false)
    {
        if(!id.StartsWith("group/",StringComparison.Ordinal))return false;
        if(right)
        {
            if(!StationGroup && id.StartsWith("group/TargetMember/",StringComparison.Ordinal))
            { _groupSymbolOrigin=new(_point.X*640/_size.Width,_point.Y*480/_size.Height);InteractionSoundRequested?.Invoke("fontclick.wav"); }
            return true;
        }
        string[] parts=id.Split('/');string command=parts[1];
        if(command=="CloseSymbols"){_groupSymbolOrigin=null;return true;}
        if(command=="Toggle"){if(StationGroup)return true;_spaceGroupVisual.FormationAttention.Clear();if(Group is {} formation)RefreshGroupFormation(formation,true);_groupFormationOpen=!_groupFormationOpen;InteractionSoundRequested?.Invoke("fontclick.wav");return true;}
        if(command=="LeavePrompt"){_groupLeaveConfirm=true;return true;}
        if(command=="CancelLeave"){_groupLeaveConfirm=false;return true;}
        // 5946D0 cases17..1B: only the exact retained G_TARGET asset sends assist.
        if(command=="ContextSymbol")
        {
            if(StationGroup || parts.Length!=3 || !int.TryParse(parts[2],out int index) || (uint)index>=_groupMembers.Items.Count)return true;
            if(!_receivedGroupSymbols.TryGetValue(_groupMembers.Items[index].Name,out var symbol) || !symbol.Context)return true;
            if(symbol.Asset=="G_TARGET")return await ActivateGroup("group/Assist/"+index,false,true);
            _groupSymbolOrigin=null;InteractionSoundRequested?.Invoke("fontclick.wav");return true;
        }
        if(!Enum.TryParse<EnbGroupAction>(command,out var action)||GroupRequested is not {} send ||_owner is not uint owner)return true;
        if(StationGroup && !contextAssist && action is EnbGroupAction.TargetMember or EnbGroupAction.Assist or EnbGroupAction.AskMember or EnbGroupAction.AskGroup or EnbGroupAction.Pipe or EnbGroupAction.Slot or EnbGroupAction.Block or EnbGroupAction.Form or EnbGroupAction.LeaveFormation or EnbGroupAction.BreakFormation)return true;
        if(action is EnbGroupAction.Pipe or EnbGroupAction.Slot or EnbGroupAction.Block or EnbGroupAction.BreakFormation && Group?.IsLeader!=true)return true;
        if(action is EnbGroupAction.Form or EnbGroupAction.LeaveFormation && Group?.IsLeader!=false)return true;
        if(action is EnbGroupAction.Form or EnbGroupAction.LeaveFormation or EnbGroupAction.BreakFormation && !CanGroupFormation(command))return true;
        int displayIndex=parts.Length==3&&int.TryParse(parts[2],out int parsed)?parsed:-1;
        var member=(uint)displayIndex<_groupMembers.Items.Count?_groupMembers.Items[displayIndex]:null;
        if(action==EnbGroupAction.TargetMember && member is not null)_groupFormationVisual.Select(member.Position);
        // The retained row number is not the current Aux/wire slot. Preserve
        // the existing request validation using the matching current identity.
        var wireMember=member is null?null:Group?.Members.FirstOrDefault(m=>m.Name==member.Name&&m.GameId==member.GameId);
        var request=new EnbGroupRequest(owner,_ship,action,wireMember?.Index??-1,member?.GameId??0,member?.Name,Group?.Invitation,InvitationRevision:Group?.InvitationRevision??0);
        if(action==EnbGroupAction.Symbol)
        {
            if(parts.Length!=3 || !int.TryParse(parts[2],out int index) || (uint)index>=_groupSymbols.Count)return true;
            request=request with {SymbolAsset=_groupSymbols[index].Asset,ContextSymbol=_groupSymbols[index].Section==2};
        }
        await Send(async()=>
        {
            bool sent=await send(request);
            if(sent)
            {
                if(action==EnbGroupAction.Assist && !contextAssist && member is not null && _spaceGroupVisual.MemberAttention.TryGetValue(member.PresentationLifetime,out var attention))attention.Clear();
                if(action is EnbGroupAction.Accept or EnbGroupAction.Reject)_answeredGroupInvite=request.InvitationRevision;
                _groupLeaveConfirm=false;
                _groupSymbolOrigin=null;
                if(action is EnbGroupAction.Pipe or EnbGroupAction.Slot or EnbGroupAction.Block or EnbGroupAction.Form or EnbGroupAction.LeaveFormation or EnbGroupAction.BreakFormation)_groupFormationOpen=false;
                InteractionSoundRequested?.Invoke("fontclick.wav");
            }
            return sent;
        });
        return true;
    }

    private void GroupSymbolMenu()
    {
        if(_groupSymbolOrigin is not {} point || Font("FONT_SMALL") is not {} font)return;
        double row=font.CellHeight*font.Scale*480/_size.Height;
        double width=_groupSymbols.Max(s=>font.MeasureWidth(s.Text))*font.Scale*640/_size.Width+16;
        double height=_groupSymbols.Count*row+32;
        // 00517C50 (via 0064B050): centred on the pointer, then a 5-unit inset, left/top checked first (GROUP-D24).
        double x=point.X-width/2,y=point.Y-height/2;
        if(x<5)x=5;else if(x>635-width)x=635-width;
        if(y<5)y=5;else if(y>475-height)y=475-height;
        Flush();Hit("group/CloseSymbols",new(0,0,640,480));
        Frame("SYMBOL_CHAT_WINDOW",new(x,y,width,height),"group/SymbolFrame");y+=4;
        foreach(int section in new[]{0,1,2})
        {
            var entries=_groupSymbols.Select((s,i)=>(Symbol:s,Index:i)).Where(s=>s.Symbol.Section==section).ToArray();
            Frame("SYMBOL_CHAT_SECTION",new(x+4,y,width-8,entries.Length*row+8),"group/SymbolSection"+section);y+=4;
            foreach(var entry in entries)
            {
                string id="group/Symbol/"+entry.Index;var r=new Rect(x+8,y,width-16,row);Hit(id,r);
                Text(id,entry.Symbol.Text,r,_hover==id?new(1,0,0):new(1,1,0));y+=row;
            }
            y+=4;
        }
        Flush();
    }
}
