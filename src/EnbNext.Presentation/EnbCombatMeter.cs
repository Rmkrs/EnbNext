namespace EnbNext.Presentation;

public readonly record struct EnbCombatHit(long Sequence,long Time,uint Sector,uint Source,uint Victim,ulong SourceLife,ulong VictimLife,uint Local,float Damage,uint Type,ulong Epoch=0);
public readonly record struct EnbCombatTarget(ulong Epoch,ulong Life);
public sealed record EnbCombatBatch(Guid Journal,EnbCombatHit[] Hits,long Through,bool Attacked,long Cursor=0);
public sealed class EnbCombatTotals
{
    public double[] Types {get;}=new double[7];
    public double Total {get {double n=0;for(int i=0;i<Types.Length;i++)if(i!=4)n+=Types[i];return n;}}
    internal void Add(uint type,float amount)=>Types[type<6?(int)type:6]+=amount;
}

// A local display owner. Each journal supplies only that character's observations;
// sequence cursors prevent replayed transport batches from counting twice.
public sealed class EnbCombatMeter
{
    public sealed class Member(string name)
    {
        public string Name {get;}=name;
        internal Guid Journal;
        public ulong TargetLife {get;set;}
        public ulong TargetEpoch {get;set;}
        internal long Sequence;
        internal long Through;
        internal bool Attacked;
        internal readonly Dictionary<(bool Incoming,uint Sector,ulong Life,Guid Journal,ulong Epoch),EnbCombatTotals> Fight=[];
        internal readonly Dictionary<(bool Incoming,uint Sector,ulong Life,Guid Journal,ulong Epoch),EnbCombatTotals> Session=[];
        public EnbCombatTotals Read(bool incoming,bool session,uint? sector=null,ulong? life=null,ulong? epoch=null)
        {
            var result=new EnbCombatTotals();if(life==0)return result;
            foreach(var pair in session?Session:Fight)
                if(pair.Key.Incoming==incoming&&(sector is null||pair.Key.Sector==sector)&&(life is null||pair.Key.Life==life&&pair.Key.Journal==Journal&&(epoch is null||pair.Key.Epoch==epoch)))
                    for(int i=0;i<7;i++)result.Types[i]+=pair.Value.Types[i];
            return result;
        }
    }
    private readonly List<KeyValuePair<Guid,Member>> members=[];
    public IReadOnlyList<KeyValuePair<Guid,Member>> Members=>members;
    public Member? Find(Guid id)=>members.FirstOrDefault(p=>p.Key==id).Value;
    public Guid Journal(Guid id)=>Find(id)?.Journal??Guid.Empty;
    public void ClearTargets(){foreach(var pair in members)pair.Value.TargetLife=0;}
    public void SetTarget(Guid id,ulong life,ulong epoch=0){if(Find(id) is {} member){member.TargetLife=life;member.TargetEpoch=epoch;}}
    public bool Active {get;private set;}
    public long Started {get;private set;}
    public long LastDamage {get;private set;}
    private long completedTime;
    private bool hasEncounter;
    private long resetBefore=long.MinValue;
    public long Since=>resetBefore;
    public int PauseSeconds {get;set;}=10;
    public long Duration(bool session,long now)=> (session?completedTime:0)+(!hasEncounter?0:Math.Max(0,(Active?now:LastDamage)-Started));
    public long Cursor(Guid member,Guid journal)=>Find(member) is {} m&&m.Journal==journal?m.Sequence:0;
    public void Accept(Guid id,string name,EnbCombatBatch batch)
    {
        var member=Find(id);if(member is null)members.Add(new(id,member=new(name)));
        if(member.Journal!=batch.Journal){member.Journal=batch.Journal;member.Sequence=0;}
        member.Through=batch.Through;member.Attacked=batch.Attacked;
        foreach(var hit in batch.Hits)
        {
            if(hit.Sequence<=member.Sequence)continue;member.Sequence=hit.Sequence;
            if(hit.Time<resetBefore||!float.IsFinite(hit.Damage)||hit.Damage<=0||hit.Source!=hit.Local&&hit.Victim!=hit.Local)continue;
            if(!Active)
            {
                if(hasEncounter)completedTime+=Math.Max(0,LastDamage-Started);
                foreach(var m in members.Select(p=>p.Value))m.Fight.Clear();
                Started=hit.Time;LastDamage=hit.Time;Active=true;hasEncounter=true;
            }
            Started=Math.Min(Started,hit.Time);LastDamage=Math.Max(LastDamage,hit.Time);
            if(hit.Source==hit.Local)Add(false,hit.VictimLife);
            if(hit.Victim==hit.Local)Add(true,hit.SourceLife);
            void Add(bool incoming,ulong life)
            {
                var key=(incoming,hit.Sector,life,batch.Journal,hit.Epoch);
                if(!member.Fight.TryGetValue(key,out var fight))member.Fight.Add(key,fight=new());
                if(!member.Session.TryGetValue(key,out var session))member.Session.Add(key,session=new());
                fight.Add(hit.Type,hit.Damage);session.Add(hit.Type,hit.Damage);
            }
        }
        member.Sequence=Math.Max(member.Sequence,batch.Cursor);
    }
    public void Advance(long now,IReadOnlyCollection<Guid> participants)
    {
        if(!Active)return;
        foreach(var id in participants)
        {
            if(Find(id) is not {} m)return;
            if(m.Attacked)return;
            now=Math.Min(now,m.Through);
        }
        if(now-LastDamage>=Math.Max(1,PauseSeconds)*1000L)Active=false;
    }
    public void Reset(long before=long.MinValue)
    {
        foreach(var m in members.Select(p=>p.Value)){m.Fight.Clear();m.Session.Clear();}
        resetBefore=before;Active=false;hasEncounter=false;Started=LastDamage=completedTime=0;
    }
}
