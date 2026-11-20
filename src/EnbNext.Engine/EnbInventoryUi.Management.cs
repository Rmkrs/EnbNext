using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Assets;
using System.Globalization;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private readonly Dictionary<string,(byte Inventory,int Slot)> _dropSlots=[];
    private bool _draggingItem;
    private EnbSceneItemSnapshot? _dragItem;
    private EnbInventoryMoveRequest? _destroyItem;
    private Point _destroyOrigin;
    private IReadOnlyDictionary<string,string>? _destroyDialog;
    private EnbInterfaceLayoutCatalog? _dragItemLayout;
    private EnbRetailIniDocument? _dragItemPalette;
    public Func<EnbInventoryMoveRequest,Task<bool>>? MoveItemRequested {get;set;}
    public Func<Point,bool>? WorldDropAllowed {get;set;}
    public Func<EnbInventoryItemRequest,Point,bool>? ItemShortcutDropped {get;set;}

    private void BeginItemDrag(Point point)
    {
        if(!_draggingItem)_weaponAfterUnload=null;
        if(_busy || _creditEntry || _destroyItem is not null || _reloadItem is not null || _pressedItem is not {Inventory:1 or 2 or 3 or 12 or 14 or 16} source || Resolve(source) is not {} item ||
            source.Inventory is 12 or 14 && Terminal?.AnalysisTransfersInhibited==true)return; // 005A063B (INV-D73)
        if(!_draggingItem && Math.Pow(point.X-_pressOrigin.X,2)+Math.Pow(point.Y-_pressOrigin.Y,2)>16)
        {
            _draggingItem=true;_dragItem=item;
            InteractionSoundRequested?.Invoke("SR_ziprollover02.wav");
            ItemCursor(item);ClearHover();
        }
    }
    private void ItemCursor(EnbSceneItemSnapshot item)
    {
        // The cursor is drawn by the GPU so the authored animated overlay remains live.
        HideCursorRequested?.Invoke();
    }
    private void DraggedItem()
    {
        if(!_draggingItem || _dragItem is not {} item || !_art.TryGetValue("dragItem",out var art))return;
        if((_pressed?.StartsWith("minv/slot/",StringComparison.Ordinal)==true||_pressed?.StartsWith("mvault/slot/",StringComparison.Ordinal)==true||_terminalCompact&&_pressed?.StartsWith("terminalSlot/",StringComparison.Ordinal)==true||_pressed?.StartsWith("mequip/",StringComparison.Ordinal)==true||ModernUi&&_pressed?.StartsWith("playerTradeSlot/",StringComparison.Ordinal)==true) && _hits.TryGetValue(_pressed,out var compactSource)){ModernDraggedItem(item,compactSource.Bounds);return;}
        var screenPoint=_pressed is {} source?LayoutOutput(source,_point):_point;
        var r=art.Design.Translate(new(screenPoint.X*640/_size.Width,screenPoint.Y*480/_size.Height));
        // 006851B0 owns a separate drag renderer: its screen-space images must
        // never inherit the last panel's placement or visibility.
        Flush();Draw("dragLayer",r);
        if(Icon(item.IconBaseAsset) is {} icon)_images.Add(new(icon,(Screen(r))));
        Flush();ItemRestriction(item,r);
        DragItemLabel("INVENTORY_LEVEL",Roman(item.Level),r.Width);
        int count=item.Count;
        if(item.Inventory==2 && item.IsWeapon)count=Data?.Ammo.FirstOrDefault(a=>a.Slot==item.Slot)?.Count??0;
        if(count>1 || item.Inventory==2 && item.IsWeapon && count>0)
            DragItemLabel("INVENTORY_COUNT",count.ToString(CultureInfo.InvariantCulture),r.Width);
    }
    private void DragItemLabel(string id,string value,double width)
    {
        if(string.IsNullOrEmpty(value)||_assets is not {} assets)return;
        _dragItemLayout??=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fc_inv.ini"));
        _dragItemPalette??=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fontart.ini"));
        if(!_dragItemLayout.TryResolve(id,out var definition))return;
        var offset=definition.Properties.GetValueOrDefault("Offset","0,0").Split(',');
        double x=double.Parse(offset[0],CultureInfo.InvariantCulture)*640;
        double y=double.Parse(offset[1],CultureInfo.InvariantCulture)*480;
        var color=new EnbRgb(1,1,1);
        foreach(var section in _dragItemPalette.EnumerateSections().Select(s=>s.Value))
            if(section.GetValueOrDefault("ColorID")==definition.Properties.GetValueOrDefault("Color") &&
                section.GetValueOrDefault("RGB")?.Split(',') is {Length:3} rgb)
            {color=new(float.Parse(rgb[0],CultureInfo.InvariantCulture)/255,float.Parse(rgb[1],CultureInfo.InvariantCulture)/255,float.Parse(rgb[2],CultureInfo.InvariantCulture)/255);break;}
        var screenPoint=_pressed is {} source?LayoutOutput(source,_point):_point;
        Text("drag/"+id,value,new(screenPoint.X*640/_size.Width+x-width/2,screenPoint.Y*480/_size.Height-y,width,0),
            color,definition.Properties.GetValueOrDefault("FontType","FONT_TINY"));
    }
    private void ItemRestriction(EnbSceneItemSnapshot item,Rect bounds)
    {
        if(Data is not {} data || !EnbItemInformation.HasUnmetRequirements(item,data))return;
        Flush();Draw("cannotUse",bounds);
        _draws[^1]=_draws[^1] with {AnimateMaterials=true};_animatingText=true;
    }
    internal async Task DropItem(EnbInventoryItemRequest source,EnbSceneItemSnapshot captured,Point point,EnbInventoryItemRequest? loadedAmmo=null)
    {
        // Modern UI: a loaded weapon dragged to cargo or the vault takes its ammunition along, as two retail
        // moves (the ammunition unload, then the weapon). Anywhere else the drag is the retail one: the ammunition.
        if(loadedAmmo is {} ammoLayer && Resolve(ammoLayer) is {} ammo)
        {
            if(await UnloadWithWeapon(source,ammoLayer,ammo,point))return;
            source=ammoLayer;captured=ammo;
        }
        if(_busy || Resolve(source) is not {} current || current.TemplateId!=captured.TemplateId || current.Count!=captured.Count)return;
        string? destinationId=DropTargetAt(point);
        if(destinationId is not null)
        {
            if(!_dropSlots.TryGetValue(destinationId,out var destination))return;
            var request=new EnbInventoryMoveRequest(source,captured.Count,destination.Inventory,destination.Slot,
                (destination.Inventory is 12 or 14?Terminal?.Items.FirstOrDefault(i=>i.Inventory==destination.Inventory && i.Slot==destination.Slot):
                    Data?.Find(destination.Inventory,destination.Slot))?.TemplateId,captured.Count);
            if(source.Inventory==16 || destination.Inventory==16)
            {
                if(source.Inventory==16 && destination.Inventory==1)request=request with {DestinationSlot=-1,DestinationTemplate=null};
                request=request with {TradeRevision=source.TradeRevision};
                if(Data is not {} tradeData || !EnbInventoryManagement.IsCurrent(request,tradeData,false))return;
                await SelectMoveQuantity(request,point);
                return;
            }
            if(Data is {} data && (source.AmmoLayer || EnbInventoryManagement.IsAmmoReload(current,data.Find(destination.Inventory,destination.Slot))))
            {
                var loaded=data.Ammo.FirstOrDefault(a=>a.Slot==destination.Slot);
                request=request with {ReloadAmmoTemplate=loaded?.TemplateId};
                if(!EnbInventoryManagement.IsCurrent(request,data,_scene?.ActiveStarbaseId is not null))return;
                await SelectMoveQuantity(request,point);return;
            }
            if(!await DropTerminalItem(request,point))await SelectMoveQuantity(request,point);return;
        }
        if(source.Inventory is 12 or 14){await DropTerminalItem(new(source,captured.Count,1,-1,null,captured.Count),point);return;}
        if(source.Inventory==16){await SelectMoveQuantity(new(source,captured.Count,1,-1,null,captured.Count){TradeRevision=source.TradeRevision},point);return;}
        if(ItemShortcutDropped?.Invoke(source,point)==true)
        {InteractionSoundRequested?.Invoke("shortcut_created_sn_1.wav");return;}
        // 005A0740 -> 005A0EB0 starts the 0x0B dump transaction for every non-vendor
        // inventory, equipment included; 00690D20 then asks UI_TRANSACTION_DUMP_ITEM.
        if(WorldDropAllowed?.Invoke(point)!=true)return;
        _destroyOrigin=new(point.X*640/_size.Width,point.Y*480/_size.Height);
        var destroy=new EnbInventoryMoveRequest(source,captured.Count,0x0B,-1,null,captured.Count,true);
        await SelectMoveQuantity(destroy,point);
    }
    private string? DropTargetAt(Point point)
    {
        string? destinationId=null;int top=0;
        foreach(var(id,hit) in _hits)if(hit.IsVisible&&hit.Order>top&&LayoutContains(hit,point)){destinationId=id;top=hit.Order;}
        return destinationId;
    }
    /// <summary>The weapon still to move once its ammunition has left the mount (Modern UI weapon drag to cargo or vault).</summary>
    private (EnbInventoryItemRequest Weapon,byte Inventory,int Slot,long Deadline)? _weaponAfterUnload;
    private async Task<bool> UnloadWithWeapon(EnbInventoryItemRequest weapon,EnbInventoryItemRequest ammoLayer,EnbSceneItemSnapshot ammo,Point point)
    {
        if(_busy || Data is not {} data || DropTargetAt(point) is not {} id || !_dropSlots.TryGetValue(id,out var target) || target.Inventory is not (1 or 3) ||
            data.Find(2,weapon.Slot)?.TemplateId!=weapon.TemplateId)return false;
        // An occupied target is the retail ammunition drop (a merge or a swap), not a place for both.
        var occupant=data.Find(target.Inventory,target.Slot);
        if(occupant is not null && occupant.TemplateId!=ammo.TemplateId)return false;
        var unload=new EnbInventoryMoveRequest(ammoLayer,ammo.Count,target.Inventory,target.Slot,occupant?.TemplateId,ammo.Count);
        if(!EnbInventoryManagement.IsCurrent(unload,data,_scene?.ActiveStarbaseId is not null))return false;
        _weaponAfterUnload=(weapon,target.Inventory,target.Slot,Environment.TickCount64+30000);
        await MoveItem(unload);
        return true;
    }
    /// <summary>Sends the weapon move once the server has taken the ammunition out; gives up if the weapon changed or the unload never lands.</summary>
    private void ContinueWeaponAfterUnload()
    {
        if(_weaponAfterUnload is not {} follow || _busy)return;
        var (weapon,inventory,preferred,deadline)=follow;
        if(Environment.TickCount64>deadline || Data is not {} data || data.Find(2,weapon.Slot)?.TemplateId!=weapon.TemplateId){_weaponAfterUnload=null;return;}
        if(data.Ammo.Any(a=>a.Slot==weapon.Slot&&a.Count>0))return;
        _weaponAfterUnload=null;
        var keys=inventory==1?data.CargoSlotKeys:data.VaultSlotKeys;
        int slot=keys.Contains(preferred)&&data.Find(inventory,preferred) is null?preferred:keys.FirstOrDefault(k=>data.Find(inventory,k) is null,-1);
        if(slot>=0)_=MoveItem(new(weapon,1,inventory,slot,null,1));
    }
    private async Task SelectMoveQuantity(EnbInventoryMoveRequest request,Point point)
    {
        if(Data is not {} data || !EnbInventoryManagement.IsCurrent(request,data,_scene?.ActiveStarbaseId is not null))return;
        // 004314C0 -> 005A05B0 -> 005A0D00 -> 00690AA0: Ctrl wins over Shift.
        var selection=EnbInventoryDragQuantity.Resolve(request.SourceCount,
            (_itemPressModifiers&KeyModifiers.Control)!=0,(_itemPressModifiers&KeyModifiers.Shift)!=0,_itemPressRight);
        request=request with {Count=selection.Count};
        if(!selection.Prompt)
        {
            if(request.DestroyConfirmed){_destroyItem=request;Opened?.Invoke();Publish();}
            else await MoveItem(request);
            return;
        }
        _reloadItem=request;_reloadCount=selection.Count;_quantityEditor.Text="";_quantityEditor.InsertMode=true;_quantityPointer=null;
        _reloadOrigin=new(point.X*640/_size.Width,point.Y*480/_size.Height);
        Opened?.Invoke();Publish();
    }
    internal async Task QuickTransfer(EnbInventoryItemRequest source,bool stack)
    {
        // 005A0740 requires only the station interface (SClient+0x1350 == +0x135C), not an open vault pane.
        if(_busy || _scene?.ActiveStarbaseId is null || Resolve(source) is not {} current || source.Inventory is not (1 or 3) || MoveItemRequested is null)return;
        int count=stack?current.Count:1;
        string destination=source.Inventory==1?"vault":"cargo";
        string feedback=count==1?$"Transferring {current.Name} to {destination}.":
            FormattableString.Invariant($"Transferring stack of {count} {current.Name} to {destination}.");
        await MoveItem(new(source,current.Count,source.Inventory==1?(byte)3:(byte)1,-1,null,count));
        if(source.Character==session() && source.ShipOwner==_ship)
            LocalMessageRequested?.Invoke(source.Character,feedback);
    }
    private async Task MoveItem(EnbInventoryMoveRequest request)
    {
        if(_busy || Data is not {} data || !EnbInventoryManagement.IsCurrent(request,data,_scene?.ActiveStarbaseId is not null) || MoveItemRequested is not {} send)return;
        await Send(()=>send(request));
    }
    private void DestroyConfirmation()
    {
        if(_destroyDialog is not {} dialog)return;
        if(ModernUi && _destroyItem is {} destroy){ModernDestroyQuestion(dialog,destroy);return;} // Next Modern UI: the same question in a Modern UI window.
        string[] headers=[dialog.GetValueOrDefault("Header0",""),dialog.GetValueOrDefault("Header1","")];
        Flush();Hit("destroyModal",new(0,0,640,480));
        StandardConfirmationDialog("destroy",string.Join('\n',headers),dialog,["destroyNo","destroyYes"],
            qualifyActions:false,origin:_destroyOrigin,authoredLines:headers);
        Flush();
    }
}
