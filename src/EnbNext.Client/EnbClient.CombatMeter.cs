using EnbNext.Net7.Gameplay;
using EnbNext.Presentation;

namespace EnbNext.Client;

public sealed partial class EnbClient
{
    private readonly object combatJournalGate=new();
    private readonly List<EnbCombatHit> combatJournal=[];
    private Net7GameplayState? combatGameplay;
    private Guid combatJournalId=Guid.NewGuid();
    public void CollectCombat(bool enabled)
    {
        var session=_net7;var next=enabled?session?.Gameplay:null;
        if(ReferenceEquals(next,combatGameplay))return;
        if(combatGameplay is {} old)old.DamageObserved=null;
        lock(combatJournalGate){combatGameplay=next;combatJournal.Clear();combatJournalId=Guid.NewGuid();}
        if(next is null||session is null)return;
        next.DamageObserved=damage=>
        {
            if(!ReferenceEquals(session,_net7)||!ReferenceEquals(next,combatGameplay)||session.World.LocalShipObjectId is not uint local||
                damage.SourceId!=local&&damage.VictimId!=local)return;
            var scene=LatestPresentationSnapshot;if(scene?.Environment is not {} environment)return;
            ulong source=session.World.Objects.FindFirst(damage.SourceId)?.CreationOrdinal??0;
            ulong victim=session.World.Objects.FindFirst(damage.VictimId)?.CreationOrdinal??0;
            lock(combatJournalGate){if(!ReferenceEquals(next,combatGameplay))return;combatJournal.Add(new(combatJournal.Count+1L,Environment.TickCount64,environment.SectorNum,damage.SourceId,damage.VictimId,source,victim,local,damage.Damage,damage.Type,session.World.Objects.LifetimeEpoch));}
        };
    }
    public Guid CombatJournalId {get {lock(combatJournalGate)return combatJournalId;}}
    public EnbCombatTarget CombatTargetLife(uint target)=>_net7?.ReadPresentationFrame((world,_)=>new EnbCombatTarget(world.Objects.LifetimeEpoch,world.Objects.FindFirst(target)?.CreationOrdinal??0))??default;
    public EnbCombatBatch ReadCombat(Guid journal,long after,long since=long.MinValue)
    {
        bool attacked=LatestPresentationSnapshot?.Objects.Any(o=>o.IsAttacking)??false;
        lock(combatJournalGate)
        {
            int start=journal==combatJournalId?(int)Math.Clamp(after,0,combatJournal.Count):0;
            while(start<combatJournal.Count&&combatJournal[start].Time<since)start++;
            int count=Math.Min(128,combatJournal.Count-start);
            var hits=new EnbCombatHit[count];combatJournal.CopyTo(start,hits,0,count);
            long through=start+count<combatJournal.Count?hits[^1].Time:Environment.TickCount64;
            return new(combatJournalId,hits,through,attacked,start+count);
        }
    }
}
