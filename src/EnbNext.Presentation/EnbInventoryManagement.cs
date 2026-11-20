namespace EnbNext.Presentation;

public static class EnbInventoryManagement
{
    // UI intent freshness, not a substitute for the server's item eligibility rules.
    public static bool IsCurrent(EnbInventoryMoveRequest request,EnbSceneInventorySnapshot inventory,bool inStation)
    {
        var source=request.Source;
        if(source.Inventory==16 || request.Destination==16)
        {
            if(inventory.PlayerTrade is not {PartnerId:not null} trade || request.TradeRevision!=trade.Revision || request.DestroyConfirmed || source.AmmoLayer ||
                request.Count<1 || request.Count>request.SourceCount || inventory.Find(source.Inventory,source.Slot) is not {} offered || offered.TemplateId!=source.TemplateId || offered.Count!=request.SourceCount)return false;
            if(source.Inventory==16 && source.Slot is <0 or >=6)return false;
            if(request.Destination==1)return source.Inventory==16 && request.DestinationSlot==-1;
            return source.Inventory is 1 or 16 && request.Destination==16 && request.DestinationSlot is >=0 and <6 &&
                !(source.Inventory==16 && source.Slot==request.DestinationSlot) && inventory.Find(16,request.DestinationSlot)?.TemplateId==request.DestinationTemplate;
        }
        if(source.Inventory is not (1 or 2 or 3) || source.Slot<0 || request.Count<1 || request.Count>request.SourceCount ||
            inventory.Find(source.Inventory,source.Slot,source.AmmoLayer) is not {} item || item.TemplateId!=source.TemplateId || item.Count<1)
            return false;
        if(source.AmmoLayer && source.Inventory!=2)return false;
        // Retail's secondary binding owns drags while a weapon contains ammunition.
        if(source.Inventory==2 && !source.AmmoLayer && inventory.Ammo.Any(a=>a.Slot==source.Slot&&a.Count>0))return false;
        if((source.Inventory==3 || request.Destination==3) && !inStation)return false;
        if(request.DestroyConfirmed)return request.Destination==0x0B && request.DestinationSlot==-1;
        if(request.Destination is not (1 or 2 or 3) || request.DestinationSlot< -1 ||
            source.Inventory==request.Destination && source.Slot==request.DestinationSlot)return false;
        if(request.DestinationSlot==-1)return source.Inventory!=request.Destination && request.Destination!=2;
        var target=inventory.Find(request.Destination,request.DestinationSlot);
        if(target?.TemplateId!=request.DestinationTemplate)return false;
        // 005A0740 -> 005A0D00 dispatches without the requirement-artwork visitor.
        // Skill/race/level eligibility is reported by the server, not a local move gate.
        if(request.Destination==2)
        {
            if(IsAmmoReload(item,target))
            {
                var loaded=inventory.Ammo.FirstOrDefault(a=>a.Slot==request.DestinationSlot);
                return inventory.Mounts.Any(m=>m.Slot==request.DestinationSlot) &&
                    request.ReloadAmmoTemplate==loaded?.TemplateId;
            }
            // 005A0D00 does not inspect a loaded destination; 0062F5B0 sets the target's
            // +0x68 drop flag from the whole container, not from its ammunition.
            if(!HasMount(request.DestinationSlot,inventory))return false;
        }
        // The equipment source must still resolve to a current mounted slot.
        if(source.Inventory==2 && !source.AmmoLayer && target is not null && !HasMount(source.Slot,inventory))return false;
        if(request.Destination==2)return true;
        // 688B90 / 6A7A00: the visible destination is a retained source key,
        // not a capacity ordinal. A -2 update can invalidate an empty target.
        var keys=request.Destination==1?inventory.CargoSlotKeys:inventory.VaultSlotKeys;
        return keys.Contains(request.DestinationSlot);
    }
    public static bool IsAmmoReload(EnbSceneItemSnapshot item,EnbSceneItemSnapshot? target)
        => item.ItemType==10 && target is {Inventory:2,ItemType:15 or 16};
    private static bool HasMount(int slot,EnbSceneInventorySnapshot inventory)
        => inventory.Mounts.Any(m=>m.Slot==slot && m.ItemTypeMask!=0);
}
