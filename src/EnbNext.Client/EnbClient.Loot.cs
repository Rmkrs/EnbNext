using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> LootItemAsync(EnbInventoryItemRequest request,CancellationToken cancellationToken=default)
    {
        if(request.Inventory==0x12)return HarvestItemAsync(request,cancellationToken);
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld || request.Inventory is not (5 or 6))return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            if(request.Character!=session.FixedSessionId || request.HulkId is not uint id ||
                session.Gameplay.CurrentHulkId!=id || session.Gameplay.LocalSelectedTargetId!=id ||
                world.Objects.FindFirst(id) is not {} hulk || hulk.CreationOrdinal!=request.HulkOwner ||
                LatestPresentationSnapshot is not {IsTransitioning:false} snapshot ||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner)return Task.FromResult(false);
            var current=EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,session.ItemTemplates,null,hulk);
            if(current.Find(request.Inventory,request.Slot)?.TemplateId!=request.TemplateId)return Task.FromResult(false);
            return session.TransferHulkItemAsync(request.Inventory,checked((uint)request.Slot),cancellationToken);
        });
    }

    public Task<bool> CloseLootAsync(uint id,ulong owner,CancellationToken cancellationToken=default)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            if(session.Gameplay.CurrentHarvestableId==id && world.Objects.FindFirst(id)?.CreationOrdinal==owner)
                return session.CloseHarvestableAsync(cancellationToken);
            if(session.Gameplay.CurrentHulkId!=id || world.Objects.FindFirst(id)?.CreationOrdinal!=owner)return Task.FromResult(false);
            return Close();
            async Task<bool> Close(){await session.CloseHulkAsync(cancellationToken);return true;}
        });
    }

    private Task<bool> HarvestItemAsync(EnbInventoryItemRequest request,CancellationToken cancellationToken)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            if(request.Character!=session.FixedSessionId || request.HarvestableId is not uint id ||
                session.Gameplay.CurrentHarvestableId!=id || session.Gameplay.LocalSelectedTargetId!=id ||
                world.Objects.FindFirst(id) is not {ObjectType:0x26} cloud || cloud.CreationOrdinal!=request.HarvestableOwner ||
                LatestPresentationSnapshot is not {IsTransitioning:false} snapshot ||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner)return Task.FromResult(false);
            var current=EnbSceneInventorySnapshotBuilder.Build(null,world.LocalPlayerAuxData,session.ItemTemplates,null,harvestable:cloud);
            if(current.Find(0x12,request.Slot)?.TemplateId!=request.TemplateId)return Task.FromResult(false);
            return session.TransferHarvestableItemAsync(checked((uint)request.Slot),cancellationToken);
        });
    }
}
