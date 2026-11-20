using EnbNext.Presentation;
using EnbNext.App;
using System.Text.Json;
using Xunit;
namespace EnbNext.Net7.Tests;
public sealed class CombatMeterTests
{
    private static readonly Guid Pilot=Guid.NewGuid(),Alt=Guid.NewGuid(),Journal=Guid.NewGuid();
    private static EnbCombatHit Hit(long seq,long time,uint source=1,uint victim=2,uint type=0,float amount=100,ulong life=22)=>new(seq,time,7,source,victim,11,life,1,amount,type);
    private static EnbCombatBatch Batch(long through,params EnbCombatHit[] hits)=>new(Journal,hits,through,false);
    [Fact] public void OwnDamageBothDirectionsAndEmpBreakdown()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(2000,Hit(1,0),Hit(2,1000,type:4,amount:500),Hit(3,2000,2,1,2,40),Hit(4,2000,9,8),Hit(5,2000,1,1,5,20)));
        var row=m.Find(Pilot)!;Assert.Equal(120,row.Read(false,false).Total);Assert.Equal(500,row.Read(false,false).Types[4]);Assert.Equal(60,row.Read(true,false).Total);Assert.Equal(2000,m.Duration(false,2000));
    }
    [Fact] public void DuplicateTransportAndInvalidValuesDoNotCount()
    {
        var m=new EnbCombatMeter();var b=Batch(2000,Hit(1,1000),Hit(2,2000,amount:float.NaN),Hit(3,2000,amount:-1));m.Accept(Pilot,"Pilot",b);m.Accept(Pilot,"Pilot",b);
        Assert.Equal(100,m.Find(Pilot)!.Read(false,true).Total);Assert.Equal(3,m.Cursor(Pilot,Journal));
    }
    [Fact] public void TargetViewsNeverMutateEncounterAndReusedObjectHasSeparateLife()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(2000,Hit(1,0),Hit(2,1000,life:23),Hit(3,2000)));
        var row=m.Find(Pilot)!;Assert.Equal(200,row.Read(false,false,7,22).Total);Assert.Equal(100,row.Read(false,false,7,23).Total);Assert.Equal(0,row.Read(false,false,7,0).Total);Assert.Equal(0,row.Read(false,false,8,22).Total);Assert.Equal(300,row.Read(false,false).Total);Assert.True(m.Active);
    }
    [Fact] public void AltActivityAndWatermarksKeepGroupEncounterOpen()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(30000,Hit(1,1000)));m.Advance(30000,[Pilot,Alt]);Assert.True(m.Active);
        m.Accept(Alt,"Alt",new(Guid.NewGuid(),[],30000,true));m.Advance(30000,[Pilot,Alt]);Assert.True(m.Active);
        m.Accept(Alt,"Alt",new(Guid.NewGuid(),[],5000,false));m.Advance(30000,[Pilot,Alt]);Assert.True(m.Active);
        m.Accept(Alt,"Alt",new(Guid.NewGuid(),[],30000,false));m.Advance(30000,[Pilot,Alt]);Assert.False(m.Active);
    }
    [Fact] public void NextEncounterKeepsSessionDamageAndExcludesDowntime()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(20000,Hit(1,0),Hit(2,2000)));m.Advance(20000,[Pilot]);Assert.False(m.Active);Assert.Equal(2000,m.Duration(true,20000));
        m.Accept(Pilot,"Pilot",Batch(50000,Hit(3,40000),Hit(4,43000)));m.Advance(60000,[Pilot]);Assert.True(m.Active);
        m.Accept(Pilot,"Pilot",Batch(60000));m.Advance(60000,[Pilot]);Assert.False(m.Active);Assert.Equal(200,m.Find(Pilot)!.Read(false,false).Total);Assert.Equal(400,m.Find(Pilot)!.Read(false,true).Total);Assert.Equal(5000,m.Duration(true,60000));
    }
    [Fact] public void ResetRetainsCursorsAndDoesNotReplayOldHits()
    {
        var m=new EnbCombatMeter();var b=Batch(2000,Hit(1,0),Hit(2,2000));m.Accept(Pilot,"Pilot",b);m.Reset();m.Accept(Pilot,"Pilot",b);Assert.False(m.Active);Assert.Equal(0,m.Duration(true,20000));Assert.Equal(0,m.Find(Pilot)!.Read(false,true).Total);
        m.Accept(Pilot,"Pilot",Batch(3000,Hit(3,3000)));Assert.Equal(100,m.Find(Pilot)!.Read(false,true).Total);
    }
    [Fact] public void ResetRejectsLateUndeliveredDamageAndEmptyBatchAdvancesCursor()
    {
        var m=new EnbCombatMeter();m.Reset(5000);m.Accept(Pilot,"Pilot",new(Journal,[Hit(1,1000),Hit(2,6000)],6000,false,2));
        Assert.Equal(100,m.Find(Pilot)!.Read(false,true).Total);m.Accept(Pilot,"Pilot",new(Journal,[],7000,false,99));Assert.Equal(99,m.Cursor(Pilot,Journal));
    }
    [Fact] public void ReconnectedCharacterDoesNotReusePreviousTargetIdentity()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(2000,Hit(1,1000)));m.Accept(Pilot,"Pilot",new(Guid.NewGuid(),[Hit(1,3000,amount:25)],3000,false));
        Assert.Equal(125,m.Find(Pilot)!.Read(false,true).Total);Assert.Equal(25,m.Find(Pilot)!.Read(false,true,7,22).Total);
        m.Accept(Pilot,"Pilot",new(m.Journal(Pilot),[Hit(2,4000,life:0)],4000,false));Assert.Equal(0,m.Find(Pilot)!.Read(false,true,7,0).Total);
    }
    [Fact] public void SectorReturnDoesNotMergeReusedObjectOrdinals()
    {
        var m=new EnbCombatMeter();m.Accept(Pilot,"Pilot",Batch(2000,Hit(1,1000),Hit(2,2000,amount:50) with {Epoch=1}));
        Assert.Equal(150,m.Find(Pilot)!.Read(false,true).Total);Assert.Equal(50,m.Find(Pilot)!.Read(false,true,7,22,1).Total);
    }
    [Fact] public void DefaultHiddenAndPreferencesRoundTrip()
    {
        var o=JsonSerializer.Deserialize<EnbMultiboxOptions>("{}")!;Assert.False(o.ShowDamageMeter);Assert.Equal(3,o.ShortcutButtons);Assert.Equal(10,o.EncounterPauseSeconds);
        var changed=o with {ShowDamageMeter=true,EncounterPauseSeconds=17};Assert.Equal(changed,JsonSerializer.Deserialize<EnbMultiboxOptions>(JsonSerializer.Serialize(changed)));
    }
    [Fact] public async Task MaximumCombatBatchFitsExistingBusFrame()
    {
        var hits=Enumerable.Range(1,128).Select(i=>new EnbCombatHit(i,long.MaxValue,uint.MaxValue,uint.MaxValue,uint.MaxValue,ulong.MaxValue,ulong.MaxValue,uint.MaxValue,float.MaxValue,uint.MaxValue)).ToArray();
        var message=new MultiboxMessage(MultiboxPeer.Version,"combat",Pilot,Guid.Empty,"",null){Combat=new(Journal,hits,long.MaxValue,true)};
        using var stream=new MemoryStream();await MultiboxPeer.WriteAsync(stream,message,default);Assert.True(stream.Length<=MultiboxPeer.MaximumMessage+4);stream.Position=0;var read=await MultiboxPeer.ReadAsync(stream,default);Assert.Equal(hits,read.Combat!.Hits);
    }
}
