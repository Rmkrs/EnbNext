using EnbNext.Presentation;
using EnbNext.Net7.Social;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    public Task<bool> InviteOwnCharacterAsync(uint avatar,CancellationToken cancellationToken=default)
    {
        if(_net7 is not {} session||State!=ClientLifecycleState.InWorld||avatar==0)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            var group=session.PlayerSemantics.GetGroupState();
            if(!ReferenceEquals(session,_net7)||world.IsTransitioning||avatar==(world.ClientAvatarId??world.LocalPlayerId)||
                group.IsInGroup&&!group.IsGroupLeader||group.Members.Any(m=>m.GameId==avatar))return Task.FromResult(false);
            return session.InviteStationPlayerToGroupAsync(avatar,cancellationToken);
        });
    }
    public void PublishGroupAttention(Net7GroupAttentionPresentation presentation)
    {
        if(_net7 is not {} session)return;
        session.ReadPresentationFrame((_,_)=>{session.Gameplay.Social.PublishGroupAttention(presentation);return true;});
    }

    public Task<bool> GroupAsync(EnbGroupRequest request, CancellationToken cancellationToken=default)
    {
        if (_net7 is not {} session || State!=ClientLifecycleState.InWorld) return Task.FromResult(false);
        return session.ReadPresentationFrame((world,time)=>
        {
            var snapshot=LatestPresentationSnapshot;
            if (!ReferenceEquals(session,_net7) || request.Character!=session.FixedSessionId ||
                snapshot is not {IsTransitioning:false} ||
                snapshot.Objects.FirstOrDefault(o=>o.IsLocalShip)?.RenderIdentity!=request.ShipOwner)
                return Task.FromResult(false);
            var group=session.PlayerSemantics.GetGroupState();
            if(request.Action is EnbGroupAction.Accept or EnbGroupAction.Reject)
            {
                if(session.Gameplay.Social.GroupInvite is not {} invite || invite.InviteText!=request.Invitation ||
                    session.Gameplay.Social.GroupInviteRevision!=request.InvitationRevision)
                    return Task.FromResult(false);
                var result=request.Action==EnbGroupAction.Accept?session.AcceptGroupInviteAsync(cancellationToken):session.RefuseGroupInviteAsync(cancellationToken);
                OnNet7PresentationStateChanged(time);
                return result;
            }
            if(!group.IsInGroup)return Task.FromResult(false);
            if(request.Action==EnbGroupAction.ToggleFormation)return session.ToggleFormationAsync(cancellationToken);
            if(request.Action==EnbGroupAction.Symbol)
                return string.IsNullOrEmpty(request.SymbolAsset)?Task.FromResult(false):session.SendGroupSymbolAsync(request.SymbolAsset,request.ContextSymbol,cancellationToken);
            if(request.Action is EnbGroupAction.TargetMember or EnbGroupAction.Assist or EnbGroupAction.AskMember or EnbGroupAction.Kick)
            {
                if((uint)request.MemberIndex>=group.Members.Count)return Task.FromResult(false);
                var member=group.Members[request.MemberIndex];
                if(member.Name.Length==0 || member.GameId!=request.MemberId || member.Name!=request.MemberName)return Task.FromResult(false);
                if(request.Action==EnbGroupAction.Kick)
                    return group.IsGroupLeader?session.KickGroupMemberByIndexAsync(request.MemberIndex,cancellationToken):Task.FromResult(false);
                if(member.GameId==0)return Task.FromResult(false);
                return request.Action switch
                {
                    EnbGroupAction.TargetMember=>RequestTargetAsync(member.GameId,cancellationToken),
                    EnbGroupAction.Assist=>session.RequestTargetsTargetAsync(member.GameId,cancellationToken),
                    _=>session.SendGroupCtaMember12Async(request.MemberIndex,cancellationToken)
                };
            }
            if(request.Action==EnbGroupAction.Leave)return session.LeaveGroupAsync(cancellationToken);
            if(request.Action==EnbGroupAction.AskGroup)return session.SendGroupCtaTopLevel12Async(cancellationToken);
            // 005946D0, controls 4..9; table 00B7F4D0 binds the authored names.
            uint type=request.Action switch {EnbGroupAction.Pipe=>6,EnbGroupAction.Slot=>4,EnbGroupAction.Block=>5,
                EnbGroupAction.Form=>7,EnbGroupAction.LeaveFormation=>8,EnbGroupAction.BreakFormation=>9,_=>0};
            return type==0?Task.FromResult(false):session.SendGroupCtaPanelRequestAsync((Net7GroupCtaPanelRequestType)type,cancellationToken);
        });
    }
}
