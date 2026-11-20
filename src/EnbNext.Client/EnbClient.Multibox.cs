namespace EnbNext.Client;

public sealed record EnbMultiboxContext(uint Character,uint Avatar,uint Ship,ulong Owner,uint Sector,ulong Transition,Guid Session=default);
public sealed record EnbMultiboxInput(Guid Id,string Action,uint Target,EnbMultiboxContext Context)
{
    /// <summary>The leader's arrival spawn draw (0..1) carried by a dock press; 0 when none.</summary>
    public double SpawnDraw {get;init;}
    /// <summary>Next shared harvesting: the harvest window slot a "harvestItem" command takes.</summary>
    public int Slot {get;init;}
    /// <summary>Next shared harvesting: a follower's game message passed to the leader ("harvestNote").</summary>
    public string? Text {get;init;}
}

public sealed partial class EnbClient
{
    private Guid multiboxSessionIdentity=Guid.NewGuid();
    public event Action<EnbMultiboxInput,Task<bool>>? MultiboxInput;

    public EnbMultiboxContext? MultiboxContext
    {
        get
        {
            if(State!=ClientLifecycleState.InWorld||_net7 is not {} session||
                LatestPresentationSnapshot is not {IsTransitioning:false,Environment:{} environment} scene||
                scene.LocalAvatarObjectId is not uint avatar||scene.LocalShipObjectId is not uint ship||
                scene.Objects.FirstOrDefault(o=>o.GameId==ship) is not {} owner)return null;
            return new(session.FixedSessionId,avatar,ship,owner.RenderIdentity,environment.SectorNum,scene.LocationTransition?.Version??0,multiboxSessionIdentity);
        }
    }
    /// <summary>Station identity: no ship; Sector carries the starbase id.</summary>
    public EnbMultiboxContext? StationMultiboxContext
    {
        get
        {
            if(State!=ClientLifecycleState.InWorld||_net7 is not {} session||
                LatestPresentationSnapshot is not {IsTransitioning:false,ActiveStarbaseId:uint starbase} scene||
                scene.LocalAvatarObjectId is not uint avatar)return null;
            return new(session.FixedSessionId,avatar,0,0,starbase,scene.LocationTransition?.Version??0,multiboxSessionIdentity);
        }
    }
    private Task<bool> PublishMultiboxInput(string action,uint target,Task<bool> result,bool station=false)
    {
        if(MultiboxInput is {} listener&&(station?StationMultiboxContext:MultiboxContext) is {} context)
            listener(new(Guid.NewGuid(),action,action=="fire"?(_net7?.Gameplay.HasLocalTargetSelection==true?_net7.Gameplay.LocalSelectedTargetId??0:_net7?.World.CurrentTargetId??0):target,context),result);
        return result;
    }
    public bool CanLeadMultibox(EnbMultiboxContext context,uint member,bool requireFormation=true)
    {
        var group=LatestPresentationSnapshot?.Hud.Group;
        if(context.Ship==0)
            return StationMultiboxContext==context&&group is {IsLeader:true,IsInGroup:true}&&
                member!=context.Avatar&&group.Members.Any(m=>m.GameId==member);
        return MultiboxContext==context&&group is {IsLeader:true,IsInGroup:true}&&(!requireFormation||group.Formation!=0)&&
            _net7?.Gameplay.PresentationMode is (0 or 1 or 5)&&
            member!=context.Avatar&&group.Members.Any(m=>m.GameId==member);
    }
    /// <summary>An alt in the leader's station follows its launch. Group membership
    /// is checked against the receiver's authoritative group state; formation is not
    /// required inside a station.</summary>
    public bool CanFollowMultiboxLaunch(EnbMultiboxInput input,EnbMultiboxContext expected)
    {
        if(_net7 is not {} session||input.Id==Guid.Empty||input.Action!="launch"||input.Context.Ship!=0)return false;
        var group=session.PlayerSemantics.GetGroupState();
        return StationMultiboxContext==expected&&expected.Ship==0&&input.Context.Sector==expected.Sector&&
            input.Target==expected.Sector&&input.Context.Avatar!=expected.Avatar&&
            session.PlayerSemantics.ActiveStarbaseId==expected.Sector&&!session.World.IsTransitioning&&
            group.IsInGroup&&!group.IsGroupLeader&&group.Members.Any(m=>m.GameId==input.Context.Avatar);
    }
    public Task<bool> ApplyMultiboxInputAsync(EnbMultiboxInput input,EnbMultiboxContext expected,CancellationToken token=default,bool requireTarget=false)
    {
        if(_net7 is not {} session||input.Id==Guid.Empty)return Task.FromResult(false);
        return session.ReadPresentationFrame((world,_)=>
        {
            var group=session.PlayerSemantics.GetGroupState();
            if(!ReferenceEquals(session,_net7)||MultiboxContext!=expected||world.IsTransitioning||
                input.Context.Sector!=expected.Sector||input.Context.Avatar==expected.Avatar||
                !group.IsInGroup||group.IsGroupLeader||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(group.Formation)==0||
                session.Gameplay.PresentationMode is not (0 or 1 or 5)||!group.Members.Any(m=>m.GameId==input.Context.Avatar))
                return Task.FromResult(false);
            // Received commands deliberately use the same producers without publishing input.
            return input.Action switch
            {
                "target" when input.Target==0||world.Objects.FindFirst(input.Target) is not null=>RequestTargetCoreAsync(input.Target,token),
                "fire" when !requireTarget||(session.Gameplay.HasLocalTargetSelection?session.Gameplay.LocalSelectedTargetId??0:world.CurrentTargetId??0)==input.Target=>FireAllCoreAsync(expected.Ship,expected.Owner,token),
                // Press the alt's own Dock/Land button for the same target, only while
                // the server shows it enabled (low word zero) for that alt.
                "dock" or "land" when FollowVerb(input.Action=="dock"?0x00080000u:0x00020000u) is uint verb=>ExecuteTargetVerbCoreAsync(input.Target,verb,token),
                _=>Task.FromResult(false)
            };
            uint? FollowVerb(uint type)
            {
                uint selected=session.Gameplay.HasLocalTargetSelection?session.Gameplay.LocalSelectedTargetId??0:world.CurrentTargetId??0;
                if(input.Target==0||selected!=input.Target||!session.Gameplay.VerbLists.TryGetValue(input.Target,out var verbs))return null;
                foreach(uint value in verbs)if((value&0xFFFF0000u)==type&&(value&0xFFFF)==0)return value;
                return null;
            }
        });
    }
}
