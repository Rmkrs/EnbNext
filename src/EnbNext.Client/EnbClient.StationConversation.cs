using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private (Net7Session Session, EnbStationInteractionRequest Owner)? _stationConversation;
    private ulong? _stationReplyRevision;
    private (Net7Session Session, ulong Revision, bool Close)? _spaceReplyRevision;
    private bool SpaceConversationOwner(Net7Session session, EnbStationInteractionRequest owner)
        => owner.StarbaseId is null && ReferenceEquals(_net7,session) && State==ClientLifecycleState.InWorld &&
           session.FixedSessionId==owner.Character && !session.World.IsTransitioning &&
           session.PlayerSemantics.ActiveStarbaseId is null && session.World.PresentationHandoffVersion==owner.HandoffVersion;
    private bool StationConversationOwner(Net7Session session, EnbStationInteractionRequest owner)
        => owner.StarbaseId is not null && ReferenceEquals(_net7,session) && State==ClientLifecycleState.InWorld &&
           session.FixedSessionId==owner.Character && !session.World.IsTransitioning &&
           session.World.PresentationHandoffVersion==owner.HandoffVersion &&
           session.PlayerSemantics.ActiveStarbaseId==owner.StarbaseId &&
           session.Gameplay.LoungeDefinition?.Npcs.Any(n=>n.RoomType==owner.Room && n.Slot==owner.Slot && n.Field2==owner.NpcId)==true;

    public Task<bool> BeginStationConversationAsync(EnbStationInteractionRequest owner)
    {
        if(_net7 is not {} session)return Task.FromResult(false);
        return session.ReadPresentationFrame((_,time)=>
        {
            if(!StationConversationOwner(session,owner))return Task.FromResult(false);
            _stationConversation=(session,owner);
            _stationReplyRevision=null;
            session.Gameplay.BeginStationConversation();
            OnNet7PresentationStateChanged(time);
            // 006390A0 -> 0069F8F0 -> 008A2D70: definition +4, action 4.
            return session.SendStarbaseRequestAsync(owner.NpcId,4);
        });
    }

    public Task<bool> ReplyStationConversationAsync(EnbStationReplyRequest request)
    {
        if(_net7 is not {} session)return Task.FromResult(false);
        return session.ReadPresentationFrame((_,_)=>
        {
            if(request.Owner.StarbaseId is null)
            {
                // Retail's shared talk interface accepts server-pushed 0054 in
                // space. Its replies use the same 0055 producer as station talk.
                if(request.Exit)
                {
                    // 0064D400 events 6/0B queue E6 at +154/+150; 0064D770 sends it without
                    // closing the owner. A later close acknowledgement stays allowed.
                    if(!SpaceConversationOwner(session,request.Owner) || session.Gameplay.TalkTreeRevision!=request.Revision ||
                        _spaceReplyRevision is {} closed && closed.Session==session && closed.Revision==request.Revision && closed.Close)return Task.FromResult(false);
                    return session.SelectTalkTreeAsync(0xe6);
                }
                if(!SpaceConversationOwner(session,request.Owner) ||
                    session.Gameplay.TalkTreeRevision!=request.Revision ||
                    session.Gameplay.TalkTree is null && (!request.Close || session.Gameplay.TalkTreeTradeOpenRevision==0) ||
                    !request.Close && session.Gameplay.TalkTreeRevision>session.Gameplay.TalkTreeTextRevision &&
                        session.Gameplay.TalkTreeAction is {} ending && ending.EndsConversation ||
                    _spaceReplyRevision is {} previous && previous.Session==session && previous.Revision==request.Revision && (previous.Close || !request.Close) ||
                    !request.Close && !ValidTalkSelection(session.Gameplay.TalkTree!,request.Selection,session.Gameplay.TalkTreeSkillAvailable))return Task.FromResult(false);
                _spaceReplyRevision=(session,request.Revision,request.Close);
                return session.SelectTalkTreeAsync(request.Close?(byte)0xe6:unchecked((byte)request.Selection));
            }
            if(_stationConversation!=(session,request.Owner) || !StationConversationOwner(session,request.Owner))return Task.FromResult(false);
            if(!request.Close && (_stationReplyRevision==request.Revision || session.Gameplay.TalkTreeRevision!=request.Revision ||
                session.Gameplay.TalkTree is not {} stationTree || !ValidTalkSelection(stationTree,request.Selection,session.Gameplay.TalkTreeSkillAvailable)))return Task.FromResult(false);
            if(request.Close)_stationConversation=null;
            else _stationReplyRevision=request.Revision;
            // 0064D524/0064D7C7: selected response DWORD narrowed to its low byte.
            // 0064D42A: Exit requests -26 (E6), not a fabricated talk-tree choice.
            return session.SelectTalkTreeAsync(request.Close?(byte)0xe6:unchecked((byte)request.Selection));
        });
    }

    // 0064DB20 queues selection zero after the last NPC-only page; 0064D770
    // sends it through the ordinary 0055 producer without closing the owner.
    private static bool ValidTalkSelection(EnbNext.Net7.Presentation.Net7TalkTreeUpdate tree,uint selection,bool skill)
        => tree.Responses.Count==0 ? selection==0 : tree.Responses.Take(5).Any(r=>r.ResponseActionOrId==selection) || skill && tree.Responses.Count>7 && tree.Responses[7].ResponseActionOrId==selection;

    public Task<bool> TradeStationItemAsync(EnbStationPurchaseRequest request)
    {
        if(_net7 is not {} session)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            bool validOwner=request.Owner.StarbaseId is null ? SpaceConversationOwner(session,request.Owner) &&
                !(_spaceReplyRevision is {} closed && closed.Session==session && closed.Close &&
                    closed.Revision>=Math.Max(session.Gameplay.TalkTreeTradeOpenRevision,session.Gameplay.TalkTreeTextRevision)) :
                _stationConversation==(session,request.Owner) && StationConversationOwner(session,request.Owner);
            if(!validOwner ||
               !session.Gameplay.TalkTreeVendorAuthorized || request.Item.Character!=session.FixedSessionId ||
               request.Item.Inventory is not (1 or 4) || LatestPresentationSnapshot?.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.Item.ShipOwner)
                return Task.FromResult(false);
            var ship=world.LocalShipObjectId is uint id?world.Objects.FindFirst(id):null;
            if(ship?.AuxData is null)return Task.FromResult(false);
            var item=EnbSceneInventorySnapshotBuilder.Build(ship.AuxData,world.LocalPlayerAuxData,session.ItemTemplates,null).Find(request.Item.Inventory,request.Item.Slot);
            if(item is null || item.TemplateId!=request.Item.TemplateId || item.Price!=request.Price || item.Count<1)return Task.FromResult(false);
            bool buy=item.Inventory==4;
            if((request.Stack || !buy && EnbVendorConfirmation.Loss(item.ItemType,request.Price,item.AverageCost)!=EnbVendorLoss.None) && !request.Confirmed)return Task.FromResult(false);
            var path=buy ? request.Stack?Net7VendorTransactionPath.VendorWindowBuyStack:Net7VendorTransactionPath.VendorWindowBuyOne
                :request.Stack?Net7VendorTransactionPath.VendorWindowSellStack:Net7VendorTransactionPath.VendorWindowSellOne;
            return session.ExecuteVendorTransactionAsync(path,(uint)item.Slot,request.Confirmed);
        });
    }
}
