using EnbNext.Presentation;
using Xunit;
namespace EnbNext.Net7.Tests;
public sealed class LocalCombatLedgerTests
{
    private static readonly Guid Journal=Guid.NewGuid();
    // Local ship 1; source, victim, time, damage, type, the other object's creation ordinal, sector.
    private static EnbCombatHit Hit(long seq,long time,uint source,uint victim,float amount=100,uint type=0,ulong life=22,uint sector=7)
        =>new(seq,time,sector,source,victim,source==1?11:life,victim==1?11:life,1,amount,type);
    private static EnbCombatBatch Batch(params EnbCombatHit[] hits)=>new(Journal,hits,0,false,hits.Length==0?0:hits[^1].Sequence);
    [Fact] public void DealtAndTakenPerObjectWithFightRates()
    {
        var l=new EnbLocalCombatLedger();
        l.Accept(Batch(Hit(1,1000,1,5),Hit(2,2000,5,1,40),Hit(3,3000,1,5),Hit(4,3000,1,6,300)));
        Assert.True(l.TryRead(5,5000,out var row));
        Assert.Equal(200,row.Dealt);Assert.Equal(40,row.Taken);Assert.True(row.Fighting);
        Assert.Equal(50,row.DealtPerSecond,6);Assert.Equal(10,row.TakenPerSecond,6);
        Assert.True(l.TryRead(6,3000,out var other));Assert.Equal(300,other.Dealt);Assert.Equal(0,other.DealtPerSecond);
        Assert.False(l.TryRead(9,3000,out _));
    }
    [Fact] public void FightEndsAfterPauseAndTheRateSpansAllFightingTime()
    {
        var l=new EnbLocalCombatLedger{PauseSeconds=10};
        l.Accept(Batch(Hit(1,1000,1,5),Hit(2,3000,1,5)));
        Assert.True(l.TryRead(5,13000,out var idle));Assert.False(idle.Fighting);Assert.Equal(0,idle.DealtPerSecond);Assert.Equal(200,idle.Dealt);
        // A new fight after the pause: 250 over the first fight's 2 s plus this one's 3 s so far, not the new fight alone.
        l.Accept(Batch(Hit(3,20000,1,5,50)));
        Assert.True(l.TryRead(5,23000,out var again));Assert.Equal(50,again.DealtPerSecond,6);Assert.Equal(250,again.Dealt);
    }
    [Fact] public void AnAttackerWithRareHitsStaysInOneFight()
    {
        // Hits 15 s apart, each longer than the pause: the server still lists the mob as attacking us in between.
        var l=new EnbLocalCombatLedger{PauseSeconds=10};
        l.Accept(Batch(Hit(1,1000,5,1,30)));
        for(long t=1250;t<16000;t+=250)l.MarkAttacking(5,t);
        l.Accept(Batch(Hit(2,16000,5,1,30)));
        Assert.True(l.TryRead(5,21000,out var row));Assert.True(row.Fighting);Assert.Equal(60,row.Taken);
        Assert.Equal(3,row.TakenPerSecond,6); // 60 over the 20 s since its first hit.
        // Without the attacker flag the lone hits would be separate zero-length fights.
        var bare=new EnbLocalCombatLedger{PauseSeconds=10};bare.Accept(Batch(Hit(1,1000,5,1,30),Hit(2,16000,5,1,30)));
        Assert.True(bare.TryRead(5,16000,out var lone));Assert.Equal(0,lone.TakenPerSecond);
        // An object never hit has nothing to mark.
        l.MarkAttacking(9,21000);Assert.False(l.TryRead(9,21000,out _));
    }
    [Fact] public void EmpOthersAndInvalidHitsDoNotCountAndReplayIsIgnored()
    {
        var l=new EnbLocalCombatLedger();var b=Batch(Hit(1,1000,1,5,500,4),Hit(2,1000,8,9),Hit(3,1000,1,5,float.NaN),Hit(4,1000,1,5,-3),Hit(5,1000,1,5,10));
        l.Accept(b);l.Accept(b);
        Assert.True(l.TryRead(5,1000,out var row));Assert.Equal(10,row.Dealt);Assert.False(l.TryRead(8,1000,out _));Assert.False(l.TryRead(9,1000,out _));
        Assert.Equal(5,l.Cursor(Journal));Assert.Equal(0,l.Cursor(Guid.NewGuid()));
    }
    [Fact] public void ReusedIdSectorChangeAndLeavingLocalStartOver()
    {
        var l=new EnbLocalCombatLedger();
        l.Accept(Batch(Hit(1,1000,1,5),Hit(2,1000,1,6)));
        l.Accept(Batch(Hit(3,2000,1,5,30,life:23)));
        Assert.True(l.TryRead(5,2000,out var reused));Assert.Equal(30,reused.Dealt);
        l.Retain(id=>id==5);Assert.False(l.TryRead(6,2000,out _));
        l.Accept(Batch(Hit(4,3000,1,7,20,sector:8)));
        Assert.False(l.TryRead(5,3000,out _));Assert.True(l.TryRead(7,3000,out var moved));Assert.Equal(20,moved.Dealt);
    }
}
