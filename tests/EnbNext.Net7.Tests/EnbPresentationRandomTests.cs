using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbPresentationRandomTests
{
    [Fact] public void MixedInclusiveDrawsMatchExecutedRetailInstructions()
    {
        // IntegerRandomOracle.java executes current client.exe 009D0000 in Ghidra.
        // SHA256 249f852496f341c00361be95b66e7c0becc2c39e7cba026f9227e38b602651c4.
        (int Low,int High,int Result,uint State)[] oracle=[
            (0,22,12,12345u),
            (0,1,1,3554416254u),
            (0,3,1,2802067423u),
            (2,16,9,3596950572u),
            (0,1,1,229283573u),
            (2,10,9,3256818826u),
            (1,20,20,3441282840u),
            (5,30,16,2941955441u),
            (-10,10,2,2951033815u),
            (-5,5,-4,1772930244u),
            (0,1000,747,2518396845u),
            (0,4,4,639546082u),
            (0,2,1,1381971571u),
            (0,3,2,1695770928u),
            (0,1,0,2121308585u),
            (0,0,0,2121308585u),
            (12000,45000,12070,3866696494u),
            (0,100,49,3144468175u),
            (10,-10,-1,3490719589u),
            (4,4,4,3490719589u),
            (-2147483648,2147483647,-2147483648,3490719589u),
            (0,65536,1,2684337210u),
            (1,4,1,1511588075u),
            (0,22,11,1538207304u),
            (0,22,21,2103497953u),
            (0,1,0,2854052358u),
            (0,3,0,3104096455u),
            (2,16,11,3668764404u),
            (0,1,1,1588911645u),
            (2,10,8,2518522002u),
            (1,20,9,33727075u),
            (5,30,20,88489753u),
            (-10,10,0,3430460382u),
            (-5,5,-5,527630783u),
            (0,1000,648,1194991756u),
            (0,4,4,3253908437u),
            (0,2,1,3001001962u),
            (0,3,2,2539649755u),
            (0,1,0,1387182456u),
            (0,0,0,1387182456u),
            (12000,45000,12238,1538766929u),
            (0,100,22,654858422u),
            (10,-10,-8,2985199757u),
            (4,4,4,2985199757u),
            (-2147483648,2147483647,-2147483648,2985199757u),
            (0,65536,0,3661187650u),
            (1,4,3,2417027667u),
            (0,22,15,3452649360u),
            (0,22,8,1179132041u),
            (0,1,0,3650472078u),
            (0,3,2,1941297327u),
            (2,16,3,2999764156u),
            (0,1,1,1787378757u),
            (2,10,9,1328144282u),
            (1,20,12,2942447271u),
            (5,30,16,3873419005u),
            (-10,10,7,154978290u),
            (-5,5,-2,2331577923u),
            (0,1000,363,422948032u),
            (0,4,4,1179349310u),
            (0,2,2,1049906079u),
            (0,3,1,2955465964u),
            (0,1,0,214614197u),
            (0,0,0,214614197u),
            (12000,45000,12088,973693770u),
            (0,100,0,662438587u),
            (10,-10,10,809784280u),
            (4,4,4,809784280u),
            (-2147483648,2147483647,-2147483648,809784280u),
            (0,65536,0,263435057u),
            (1,4,3,2030201366u),
            (0,22,3,3848146839u)];
        var random=new EnbRetailPresentationRandom();
        foreach(var row in oracle)
        {
            Assert.Equal(row.Result,random.Next(row.Low,row.High));
            Assert.Equal(row.State,random.State);
        }
    }
    [Fact] public void AvatarConstructionConsumesInitialJitterBeforeOtherConsumers()
    {
        var random=new EnbRetailPresentationRandom();
        var idle=new EnbRetailAvatarIdle(EnbRetailIniDocument.Parse("[HEADER]"),random.Next);
        Assert.Equal(12,idle.Jitter); Assert.Equal(12345u,random.State);
        Assert.Equal("breathe1",idle.Next(random.Next));Assert.Equal(12345u,random.State);
        Assert.Equal(1,random.Next(0,1));Assert.Equal(3554416254u,random.State);
        idle.Advance(57,true);Assert.StartsWith("breathe",idle.Next(random.Next));
        idle.Advance(.01f,true);Assert.StartsWith("bored",idle.Next(random.Next));
    }
    [Fact] public void MusicInterleavesThemeAndComponentDrawsAndQueuesOnlyChosenComponent()
    {
        var calls=new List<(int,int)>();
        int Draw(int low,int high){calls.Add((low,high));return high;}
        var catalog=new EnbRetailMusicCatalog([
            new(1,8,"Wander",0,-1,0,[new("a",0,0),new("b",0,0)]),
            new(2,8,"Wander",0,-1,0,[new("c",0,0),new("d",0,0),new("e",0,0)])],0,0);
        var playlist=catalog.Playlist("Wander",0,0,null,Draw);
        Assert.Equal(new[]{(0,1),(0,2),(0,0),(0,1)},calls);
        Assert.Equal(new[]{"e","b"},playlist.Select(t=>Assert.Single(t.Components).ResourceName));
    }
    [Fact] public void BridgeAdmissionPrecedesVariantDrawAndUnsuffixedReactionsDoNotDraw()
    {
        var actor=new EnbBridgeAvatarSteering();int draws=0;
        int Draw(int low,int high){draws++;Assert.Equal((1,4),(low,high));return 4;}
        Assert.True(actor.Request(EnbBridgeAvatarReaction.UseControls,Draw));Assert.Equal(1,draws);Assert.Equal("use_controls4",actor.Tag);
        Assert.False(actor.Request(EnbBridgeAvatarReaction.UseControls,Draw));Assert.Equal(1,draws);
        Assert.True(actor.Request(EnbBridgeAvatarReaction.StartWarp,Draw));Assert.Equal(1,draws);
        Assert.False(actor.Request(EnbBridgeAvatarReaction.GetHit,Draw));Assert.Equal(1,draws);
        Assert.True(actor.Request(EnbBridgeAvatarReaction.StopHard,Draw));Assert.Equal(1,draws);
    }
    [Fact] public void RejectedRadiusAttemptDoesNotDrawButEligibleCallbackPrecedesInterval()
    {
        var random=new EnbRetailPresentationRandom();
        var schedule=new EnbCurrentRetailRadiusSoundSchedule(100,0,22);
        Assert.False(schedule.TryAttempt(99,true,()=>throw new Exception(),random.Next));
        Assert.Equal(0u,random.State);
        Assert.True(schedule.TryAttempt(100,true,()=>Assert.Equal(12,random.Next(0,22)),random.Next));
        Assert.NotEqual(12345u,random.State);
        uint before=random.State;schedule.Retire();
        Assert.False(schedule.TryAttempt(1000,true,()=>throw new Exception(),random.Next));
        Assert.Equal(before,random.State);
    }
}
