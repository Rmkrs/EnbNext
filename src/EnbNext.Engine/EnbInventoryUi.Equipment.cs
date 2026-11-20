using Point = EnbNext.Presentation.EnbUiPoint;
using Vector = EnbNext.Presentation.EnbUiPoint;
using Size = EnbNext.Presentation.EnbUiSize;
using Rect = EnbNext.Presentation.EnbUiRect;
using KeyModifiers = EnbNext.Assets.EnbKeyModifiers;
using EnbNext.Engine;
using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbInventoryUi
{
    private EnbInventoryMoveRequest? _reloadItem;
    private int _reloadCount;
    private Point _reloadOrigin;
    // 0066CC00 -> 0084AA10: a ten-character digit editor with its own caret (INV-D18/D49).
    private readonly EnbRetailTextEditor _quantityEditor=new(10){CharacterClass=EnbRetailEditorCharacterClass.Digits};
    private Point? _quantityPointer;
    private IReadOnlyDictionary<string,string>? _equipmentTips;

    private void ReloadQuantity()
    {
        if(_reloadItem is not {} request)return;
        if(ModernUi){ModernQuantity(request);return;} // Next Modern UI: the same prompt in a small Modern UI window.
        var font=Font("FONT_SMALL");
        double sx=_size.Width/640,sy=_size.Height/480;
        int digits=request.SourceCount.ToString().Length;
        double numberWidth=((font?.MeasureWidth("5")??6)*(font?.Scale??1)/sx+2)*digits;
        double width=Math.Min(192,Math.Max(request.SourceCount,numberWidth*3));
        double height=(font?.CellHeight??10)*(font?.Scale??1)/sy+1;
        double x=Math.Clamp(_reloadOrigin.X-width,1,Math.Max(1,639-width-numberWidth-2)),y=Math.Clamp(_reloadOrigin.Y-height,1,479-height);
        Flush();Draw("dragLayer",new(x-1,y-1,width+numberWidth+3,height+2));Hit("reloadModal",new(0,0,640,480));
        foreach(var edge in new[]{new Rect(x-1,y-1,width+numberWidth+3,1),new Rect(x-1,y+height,width+numberWidth+3,1),new Rect(x-1,y,1,height),new Rect(x+width+numberWidth+1,y,1,height)})
            Fill("quantityBorder",edge,26,255,26,255);
        Fill("quantityBackground",new(x,y,width+numberWidth+1,height),26,26,26,26);
        Fill("quantityFill",new(x,y,width*_reloadCount/request.SourceCount,height),0,204,77,255);
        Hit("reloadTrack",new(x,y,width,height));
        Text("reloadCount",_reloadCount.ToString(System.Globalization.CultureInfo.InvariantCulture),new(x+width+2,y,numberWidth,height),new(0,1,0),"FONT_SMALL",false);
        Flush();
    }

    private void Fill(string key,Rect r,byte red,byte green,byte blue,byte alpha)
    {
        if(!_icons.TryGetValue(key,out var raster)||raster is null)_icons[key]=raster=new(1,1,ImmutableArray.Create(blue,green,red,alpha));
        if(r.Width>0)_images.Add(new(raster,(Screen(r))));
    }

    private void SetReloadQuantity(Point point)
    {
        if(_reloadItem is not {} request || !_hits.TryGetValue("reloadTrack",out var hit)||!hit.IsVisible)return;
        var track=hit.Design;
        _quantityPointer=point;
        if(_quantityEditor.Text.Length!=0)return;
        double fraction=Math.Clamp((point.X*640/_size.Width-track.X)/track.Width,0,1);
        _reloadCount=(int)(fraction*request.SourceCount);
    }

    internal async Task<bool> QuantityKey(ushort key)
    {
        if(_reloadItem is not {} request)return false;
        if(key==EnbInventoryKeys.Escape){_reloadItem=null;Publish();return true;}
        if(key==EnbInventoryKeys.Enter){_reloadItem=null;await CompleteQuantity(request);Publish();return true;}
        char digit=key is >=EnbInventoryKeys.D0 and <=EnbInventoryKeys.D9?(char)('0'+key-EnbInventoryKeys.D0):
            key is >=EnbInventoryKeys.NumPad0 and <=EnbInventoryKeys.NumPad9?(char)('0'+key-EnbInventoryKeys.NumPad0):'\0';
        if(digit=='\0' && key is not (EnbInventoryKeys.Back or EnbInventoryKeys.Delete or EnbInventoryKeys.Home or EnbInventoryKeys.End or
            EnbInventoryKeys.Left or EnbInventoryKeys.Right or EnbInventoryKeys.Insert))return true;
        _quantityEditor.Key(key,0,digit);
        ApplyQuantityText(request);
        Publish();return true;
    }

    // 0066D1E0: while the value exceeds the maximum, divide by ten and drop the rightmost
    // character (reloading the editor); empty text takes the count from the pointer.
    // The value is accumulated without native signed32 wrap, so no negative count arises.
    private void ApplyQuantityText(EnbInventoryMoveRequest request)
    {
        string text=_quantityEditor.Text;
        if(text.Length==0){if(_quantityPointer is {} point)SetReloadQuantity(point);return;}
        long value=0;foreach(char c in text)value=value*10+(c-'0');
        bool truncated=false;
        while(value>request.SourceCount&&text.Length>0){value/=10;text=text[..^1];truncated=true;}
        if(truncated)_quantityEditor.Text=text;
        _reloadCount=(int)value;
    }

    private async Task CompleteQuantity(EnbInventoryMoveRequest request)
    {
        if(IsTerminalMove(request)){if(_reloadCount>0)await SendTerminalMove(request with {Count=_reloadCount});return;}
        if(_reloadCount<=0 || Data is not {} data || !EnbInventoryManagement.IsCurrent(request,data,_scene?.ActiveStarbaseId is not null))return;
        request=request with {Count=_reloadCount};
        if(request.DestroyConfirmed)_destroyItem=request;
        else await MoveItem(request);
    }

    /// <summary>One equipment slot as 005D2210 places it: a core mount, a weapon or device mount in AuxData order, or a future (not yet usable) weapon/device slot.</summary>
    private readonly record struct EquipmentSlotPlan(string Id,string Art,string Icon,int Index,EnbEquipmentMountSnapshot? Mount,bool Future,bool Weapon);
    private static List<EquipmentSlotPlan> PlanEquipmentSlots(EnbSceneInventorySnapshot data)
    {
        var plan=new List<EquipmentSlotPlan>();
        int weapons=0,devices=0;
        // 005D2210: core masks have fixed positions; other mounts fill the weapon
        // or device column in AuxData order. Future mounts are visible but disabled.
        foreach(var mount in data.Mounts.OrderBy(m=>m.Slot))
        {
            switch(mount.ItemTypeMask)
            {
                case 4:plan.Add(new("Shields","C00F","C00",0,mount,false,false));break;
                case 0x80:plan.Add(new("Reactor","C00F01","C01",1,mount,false,false));break;
                case 0x40:plan.Add(new("Engines","C00F02","C02",2,mount,false,false));break;
                default:
                    bool weaponMount=(mount.ItemTypeMask&0x1c022)!=0;
                    int index=weaponMount?weapons++:devices++;
                    if(index>=6)continue;
                    string art=(weaponMount?"W":"S")+index.ToString("00");
                    plan.Add(new((weaponMount?"weapon":"device")+index,art+"F",art,index,mount,false,weaponMount));
                    break;
            }
        }
        for(int i=weapons;i<Math.Min(6,data.FutureWeapons);i++)plan.Add(new("weapon"+i,"W"+i.ToString("00")+"F","W"+i.ToString("00"),i,null,true,true));
        for(int i=devices;i<Math.Min(6,data.FutureDevices);i++)plan.Add(new("device"+i,"S"+i.ToString("00")+"F","S"+i.ToString("00"),i,null,true,false));
        return plan;
    }
    private (EnbSceneItemSnapshot? Item,int? WeaponLevel) EquippedItem(EnbSceneInventorySnapshot data,EnbEquipmentMountSnapshot mount)
    {
        var weapon=data.Find(2,mount.Slot);
        var ammo=weapon is {IsWeapon:true}?data.Find(2,mount.Slot,true):null;
        return ammo is {Count:>0}?(ammo with {IsAmmoLayer=true},weapon!.Level):(weapon,null);
    }

    private void EquipmentSlots()
    {
        if(Data is not {} data)return;
        foreach(var slot in PlanEquipmentSlots(data))
        {
            var r=Native("equip/"+slot.Art);
            if(slot.Future)
            {
                Draw("equip/"+slot.Art,r,slot.Id);
                ImageTexture("im_blank.tga",Native("equip/"+slot.Icon),System.Numerics.Vector3.Zero);
                _tips[slot.Id]=_equipmentTips?.GetValueOrDefault(slot.Weapon?"FUTURE_WEAPON":"FUTURE_DEVICE","")??"";
                continue;
            }
            var mount=slot.Mount!;
            var (item,level)=EquippedItem(data,mount);
            if(item is null)
            {
                Draw("equip/"+slot.Art,r,slot.Id);
                Draw("equip/"+slot.Icon,Native("equip/"+slot.Icon));
                _tips[slot.Id]=(_inventorySlotTips?.GetValueOrDefault("NORMAL_SLOT","")??"").Replace("\\n","\n").Replace("%s",EnbNext.Rendering.EnbRetailItemTooltip.EmptyDescription(mount.ItemTypeMask));
            }
            else Slot(slot.Id,r,item,level);
            _dropSlots[slot.Id]=(2,mount.Slot);
        }
    }
}
