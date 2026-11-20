using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> MoveInventoryItemAsync(EnbInventoryMoveRequest request,CancellationToken cancellationToken=default)
    {
        if(_net7 is not {} session || State!=ClientLifecycleState.InWorld)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            var source=request.Source;
            var snapshot=LatestPresentationSnapshot;
            var ship=world.LocalShipObjectId is uint id?world.Objects.FindFirst(id):null;
            if(!ReferenceEquals(session,_net7) || source.Character!=session.FixedSessionId || ship?.AuxData is null ||
                snapshot is not {IsTransitioning:false} || snapshot.LocalShipObjectId!=world.LocalShipObjectId ||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=source.ShipOwner)
                return Task.FromResult(false);
            var current=EnbSceneInventorySnapshotBuilder.Build(ship.AuxData,world.LocalPlayerAuxData,session.ItemTemplates,RetailAssets?.ResolveShortcutSkills(),trade:session.Gameplay.TradeDomain);
            if(!EnbInventoryManagement.IsCurrent(request,current,session.PlayerSemantics.ActiveStarbaseId is not null))return Task.FromResult(false);
            // 00690D20: the first 0027 word is SClient+1130, the 0047 Client Ship (INV-D102).
            uint selected=session.ClientShipTransferId;
            if(request.Destination==16)return source.Inventory==16
                ?session.ReorderTradeItemAsync((uint)source.Slot,(uint)request.DestinationSlot,(uint)request.Count,cancellationToken)
                :session.OfferTradeItemAsync(source.Inventory,(uint)source.Slot,(uint)request.DestinationSlot,(uint)request.Count,cancellationToken);
            if(source.Inventory==16)return session.RetractTradeItemAsync((uint)source.Slot,(uint)request.Count,cancellationToken);
            return request.DestroyConfirmed
                ?session.DestroyInventoryItemConfirmedAsync(selected,source.Inventory,(uint)source.Slot,(uint)request.Count,cancellationToken)
                :session.TransferInventoryAsync(new(selected,Net7InventoryTransferIdSource.ClientShip),
                    source.Inventory,(uint)source.Slot,request.Destination,unchecked((uint)request.DestinationSlot),(uint)request.Count,cancellationToken);
        });
    }
}
