using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationAmbientNpcTests
{
    private static EnbStationAmbientNpcDefinition Definition()=>EnbStationAmbientNpcDefinition.Read(
        EnbRetailIniDocument.Parse("[NPC]\nSequential=true\nOffsetBoneName=walk\nDestBoneName=stop\nLocation00=5\nLocation01=8\nLocation02=12\nMinWait=2\nMaxWait=5\nRelocateChance=40\nActionChance=60\nAction00=201\nChance00=30\nAction01=205\nChance01=70\nExtraName01=bottlep\nExtraAnim01=bottlep.bottlepanim-205"),"NPC",
        name=>Matrix4x4.CreateTranslation(name.StartsWith("walk")?1:2,int.Parse(name[4..]),0));
    [Fact]
    public void FirstDecisionConsumesSelectionButForcesLocationOneAndStrictWait()
    {
        var definition=Definition();var scheduler=new EnbStationAmbientNpcSchedule(definition,3);
        List<(int,int)> calls=[];Queue<int> returns=new([100,2,5]);
        int Random(int low,int high){calls.Add((low,high));return returns.Dequeue();}
        Assert.Null(scheduler.Advance(1000,true,false,Random));
        var decision=scheduler.Advance(1,true,false,Random)!;
        Assert.Equal(new[]{(0,100),(0,2),(2,5)},calls);
        Assert.Equal(1,scheduler.Location);
        Assert.Equal(new[]{definition.Offsets[1],definition.Destinations[1]},decision.Route);
        Assert.Null(decision.Action);Assert.False(decision.TrimHistory);
        Assert.Equal(5,scheduler.Wait);
    }
    [Fact]
    public void AuthoredWeightsUseInclusiveRangesAndPauseDuringSpeechOrInteraction()
    {
        var scheduler=new EnbStationAmbientNpcSchedule(Definition(),3);
        Queue<int> draws=new([0,2,2,41,30,3]);
        int Random(int low,int high){int value=draws.Dequeue();Assert.InRange(value,low,high);return value;}
        scheduler.Advance(1001,true,false,Random);
        Assert.Null(scheduler.Advance(10000,false,false,Random));
        Assert.Null(scheduler.Advance(10000,true,true,Random));
        Assert.Equal(0,scheduler.Elapsed);
        Assert.Null(scheduler.Advance(2000,true,false,Random));
        var decision=scheduler.Advance(1,true,false,Random)!;
        Assert.Empty(decision.Route);
        Assert.Equal(new(205,30,99,"bottlep","bottlep.bottlepanim-205"),decision.Action);
        Assert.Empty(draws);
    }
    [Fact]
    public void SequentialReturnUsesEveryInterveningOffsetAndOnlyTheFinalDestination()
    {
        var definition=Definition();var scheduler=new EnbStationAmbientNpcSchedule(definition,3);
        Queue<int> draws=new([0,0,2,0,2,2,0,0,2]);
        int Random(int low,int high)=>draws.Dequeue();
        scheduler.Advance(1001,true,false,Random);
        scheduler.Advance(2001,true,false,Random);
        var decision=scheduler.Advance(2001,true,false,Random)!;
        Assert.Equal(new[]{definition.Offsets[1],definition.Offsets[0],definition.Destinations[0]},decision.Route);
        Assert.Equal(0,scheduler.Location);
    }
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void OtherVariantsNeverRunAutonomy(uint variant)
    {
        Assert.Null(new EnbStationAmbientNpcSchedule(Definition(),variant).Advance(100000,true,false,
            (_,_)=>throw new Exception("Unexpected presentation random draw")));
    }
    [Fact]
    public void WaypointMovementWaitsForWalkSelectionThenAcceleratesAndStopsInsideRetailRadius()
    {
        var motion=new EnbStationAmbientNpcMotion(Matrix4x4.Identity);
        motion.Append([Matrix4x4.CreateTranslation(20,0,0)],false);
        motion.Advance(.1f);
        Assert.True(motion.RequestWalk);Assert.Equal(Vector3.Zero,motion.Transform.Translation);
        motion.WalkAnimationSelected();motion.Advance(.1f);
        Assert.Equal(3,motion.Speed);Assert.Equal(.3f,motion.Transform.M41,5);
        for(int i=0;i<200 && motion.Remaining>0;i++)motion.Advance(.025f);
        Assert.Equal(0,motion.Remaining);Assert.Equal(2,motion.MovementState);
        for(int i=0;i<20;i++)motion.Advance(.025f);
        Assert.Equal(0,motion.Speed);Assert.InRange(motion.Transform.M41,17,20);
    }
    [Fact]
    public void NearbyWaypointBehindTheActorUsesBackwardLocomotion()
    {
        var motion=new EnbStationAmbientNpcMotion(Matrix4x4.Identity);
        motion.Append([Matrix4x4.CreateTranslation(-8,0,0)],false);
        motion.Advance(.1f);Assert.True(motion.Backward);motion.WalkAnimationSelected();
        motion.Advance(.1f);
        Assert.Equal(-3,motion.Speed);Assert.Equal(-.3f,motion.Transform.M41,5);
    }
    [Fact]
    public void IntermediateNearbyWaypointsAreConsumedWithoutStoppingThePath()
    {
        var motion=new EnbStationAmbientNpcMotion(Matrix4x4.Identity);
        motion.Append([Matrix4x4.CreateTranslation(1,0,0),Matrix4x4.CreateTranslation(20,0,0)],false);
        motion.Advance(.1f);Assert.Equal(1,motion.Remaining);Assert.True(motion.RequestWalk);
        motion.BeginInteraction(Matrix4x4.Identity);Assert.Equal(0,motion.Remaining);
        motion.EndInteraction();Assert.Equal(1,motion.Remaining);
    }
    [Fact]
    public void InstalledProgenLoungeProvidesTheAuthoredRouteInsteadOfAvatarSlotBones()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var room=EnbCurrentRetailStationInterior.Load(assets,new EnbSceneStationDefinition(2,[new(3,0,[])]),3);
        Assert.NotNull(room.ExtraAttachments);Assert.Null(room.HangarAttachments);
        var ini=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientDataDirectory,"ini","pstarbase_ambient.ini"));
        Assert.True(EnbW3dHierarchyPose.TryEvaluate(room.ExtraAttachments!,Matrix4x4.Identity,out var pose,out _));
        var definition=EnbStationAmbientNpcDefinition.Read(ini,"Drusus Cauponius",name=>
        {
            Assert.Contains(room.ExtraAttachments!.Pivots,p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            return pose[EnbCurrentRetailAggregateBoneLookup.Resolve(room.ExtraAttachments!,name)];
        });
        Assert.Equal(8,definition.Destinations.Count);Assert.Equal(8,definition.Offsets.Count);
        Assert.Equal(new[]{201,205,206},definition.Actions.Select(a=>a.Number));
        Assert.Equal(99,definition.Actions[^1].High);
        Assert.NotEqual(definition.InitialTransform,definition.Destinations[1]);
    }
    [Fact]
    public void MissingAmbientDocumentCreatesNoActor()
    {
        // 00642740 returns NULL when %sstarbase_ambient.ini does not load; only the
        // Progen document is installed. 00634140 then reads that NULL actor's +700
        // (undefined), so EnbNext stops at the missing actor.
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var room=EnbCurrentRetailStationInterior.Load(assets,new EnbSceneStationDefinition(2,[new(3,0,[])]),3);
        Assert.False(File.Exists(Path.Combine(assets.Paths.ClientDataDirectory,"ini","tstarbase_ambient.ini")));
        var npc=new EnbSceneStationNpc(3,0,1,0,1,"Drusus Cauponius",null!);
        Assert.Null(EnbStationAmbientNpcDefinition.Load(assets,0,npc,room));
        Assert.NotNull(EnbStationAmbientNpcDefinition.Load(assets,2,npc,room));
    }
}
