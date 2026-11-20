using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private EnbStationPlayerInteraction? _stationPlayerMenu;
    private Point _stationPlayerMenuOrigin;
    private bool _stationPlayerInviteEnabled;
    private EnbInterfaceLayoutCatalog? _stationPlayerText;
    private EnbRetailScannerTooltipDefinition? _stationPlayerOption;
    public Func<uint,EnbWorldRenderer.StationPlayerPick?>? StationPlayerQuery {get;set;}
    public Func<EnbStationPlayerInteraction,Task<bool>>? StationPlayerRequested {get;set;}
    public event Action<string>? StationPlayerChatRequested;
    public event Action? StationPlayerActionCompleted;
    internal bool StationPlayerMenuOpen=>_stationPlayerMenu is not null;

    /// <summary>Opens the menu for the player clicked; true when it opened (the click is the menu's).</summary>
    internal bool OpenStationPlayer(uint station,ulong handoff,uint room,uint id,Point point)
    {
        if(!_interactive || _owner is not uint character || _stationPlayerMenu is not null ||
            StationLabelInteractionOpen || StationPlayerQuery?.Invoke(id) is not {Busy:false,InRange:true,Departing:false} peer)return false;
        var opened=new EnbStationPlayerInteraction(character,station,handoff,room,id,peer.FirstName,EnbStationPlayerAction.Cancel);
        bool invite=Group?.IsInGroup!=true || Group.IsLeader;
        // Next Modern UI: the same choices in a Modern UI menu at the pointer (a click elsewhere is Cancel).
        if(ModernUi && ModernMenuRequested is {} show)
        {
            InteractionSoundRequested?.Invoke("fontclick.wav");
            show("stationPlayer",peer.FirstName,[
                new("Chat",()=>_=StationPlayerAction(opened,invite,EnbStationPlayerAction.Chat)),
                new("Trade",()=>_=StationPlayerAction(opened,invite,EnbStationPlayerAction.Trade)),
                new("Group Invite",()=>_=StationPlayerAction(opened,invite,EnbStationPlayerAction.GroupInvite),Enabled:invite)],point);
            return true;
        }
        _stationPlayerMenu=opened;
        _stationPlayerMenuOrigin=new(point.X*640/_size.Width,point.Y*480/_size.Height);
        _stationPlayerInviteEnabled=invite;
        InteractionSoundRequested?.Invoke("fontclick.wav");Publish();
        return true;
    }

    /// <summary>
    /// A choice from the Modern UI menu: the menu's player is checked again as the open retail menu is each update
    /// (RefreshStationPlayerMenu, 63DFE0), then the retail action runs.
    /// </summary>
    private async Task StationPlayerAction(EnbStationPlayerInteraction owner,bool invite,EnbStationPlayerAction action)
    {
        if(!_interactive || _scene?.ActiveStarbaseId!=owner.StarbaseId || (_scene.LocationTransition?.Version??0)!=owner.HandoffVersion || StationLabelInteractionOpen)return;
        if(StationPlayerQuery?.Invoke(owner.AvatarId) is not {} peer)
        {LocalChatMessageRequested?.Invoke(owner.Character,0x13,"The player you are interacting with has left the area.");return;}
        if(peer.Busy || action==EnbStationPlayerAction.GroupInvite && !invite)return;
        await RunStationPlayerAction(owner,action,peer);
    }

    internal void RefreshStationPlayerMenu()
    {
        if(_stationPlayerMenu is not {} owner)return;
        if(_scene?.ActiveStarbaseId!=owner.StarbaseId || (_scene.LocationTransition?.Version??0)!=owner.HandoffVersion || StationLabelInteractionOpen)
        {_stationPlayerMenu=null;return;}
        //63DFE0 rechecks the room record each update, including while no row is clicked.
        if(StationPlayerQuery?.Invoke(owner.AvatarId) is not {} peer)
        {
            _stationPlayerMenu=null;
            LocalChatMessageRequested?.Invoke(owner.Character,0x13,"The player you are interacting with has left the area.");
        }
        else if(peer.Busy)_stationPlayerMenu=null;
    }

    private async Task ActivateStationPlayer(string id)
    {
        RefreshStationPlayerMenu();
        if(_stationPlayerMenu is not {} owner || !int.TryParse(id.AsSpan("stationPlayer/".Length),out int value) ||
            value is <1 or >4 || value==4 && !_stationPlayerInviteEnabled)return;
        var action=(EnbStationPlayerAction)value;
        var peer=StationPlayerQuery?.Invoke(owner.AvatarId);
        _stationPlayerMenu=null;
        await RunStationPlayerAction(owner,action,peer);
    }

    private async Task RunStationPlayerAction(EnbStationPlayerInteraction owner,EnbStationPlayerAction action,EnbWorldRenderer.StationPlayerPick? peer)
    {
        InteractionSoundRequested?.Invoke("fontclick.wav");
        if(action==EnbStationPlayerAction.Chat && peer is not null)StationPlayerChatRequested?.Invoke(peer.FirstName);
        else if(action is EnbStationPlayerAction.Trade or EnbStationPlayerAction.GroupInvite &&
            (action!=EnbStationPlayerAction.Trade || _playerTrade is null) && StationPlayerRequested is {} send)
            await Send(()=>send(owner with {Action=action}));
        StationPlayerActionCompleted?.Invoke();Publish();
    }

    private void StationPlayerMenu()
    {
        if(_stationPlayerMenu is null || _stationPlayerText is null || _stationPlayerOption is not {} option ||
            !_stationPlayerText.TryResolve("Symbol_Chat_Default",out var normal) ||
            !_stationPlayerText.TryResolve("Symbol_Chat_Unusable",out var disabled) ||
            Font(normal.Properties["FontType"]) is not {} font)return;
        string[] labels=["Cancel","Chat","Trade","Group Invite"];
        EnbDialogBorder Border(string name)=>new((float)_frames[name].Definition.BorderX,(float)_frames[name].Definition.BorderY);
        var layout=EnbRetailDialogLayout.MeasureMenu(labels.Select(s=>(float)(font.MeasureWidth(s)*font.Scale*640/_size.Width)).ToArray(),
            (float)(font.CellHeight*font.Scale*480/_size.Height),Border("SYMBOL_CHAT_WINDOW"),Border("SYMBOL_CHAT_SECTION"),new(option.BorderX,option.BorderY));
        var placed=EnbRetailDialogLayout.Place(layout.Window.Width,layout.Window.Height,(float)_stationPlayerMenuOrigin.X,(float)_stationPlayerMenuOrigin.Y);
        Rect At(EnbDialogRect r)=>new(r.X+placed.X,r.Y+placed.Y,r.Width,r.Height);
        Flush();
        Frame("SYMBOL_CHAT_WINDOW",At(layout.Window),"stationPlayer/frame");
        Frame("SYMBOL_CHAT_SECTION",At(layout.Info),"stationPlayer/section");
        for(int i=0;i<labels.Length;i++)
        {
            string id="stationPlayer/"+(i+1);
            bool enabled=i!=3 || _stationPlayerInviteEnabled;
            var style=enabled?normal:disabled;
            if(option.MeshFile.Equals("DynaWinNull",StringComparison.OrdinalIgnoreCase))Hit(id,At(layout.Options[i]));
            else Frame("SYMBOL_CHAT_OPTION",At(layout.Options[i]),id);
            var color=_friendColors[style.Properties["Color"]];
            //6D559A..6D55CB: an unresolved alternate color reuses the base.
            //The installed menu names RED, which is absent from fontart.ini.
            if(enabled && _hover==id && style.Properties.GetValueOrDefault("MouseOver")=="FA_TOGGLE_COLOR")
                color=_friendColors.GetValueOrDefault(style.Properties.GetValueOrDefault("AltColor",""),color);
            Text(id,labels[i],At(layout.Labels[i]),color,style.Properties["FontType"]);
        }
        Flush();
    }
}
