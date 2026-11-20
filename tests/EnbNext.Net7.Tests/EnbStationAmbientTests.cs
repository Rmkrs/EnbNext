using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationAmbientTests
{
    [Fact]
    public void TrackedSlotsPreserveDefaultsGuardsAndSeparateTimers()
    {
        var room=new EnbRetailStationSoundCatalog(EnbRetailIniDocument.Parse("""
            [General]
            DefaultMinDelay=2.25
            DefaultMaxDelay=4
            [Hangar]
            TrackedMesh2=ROBOT
            TrackedSound2=beep
            OnlyAddSoundIfBoneUsed2=true
            TrackedMinDelay2=.5
            LoopingTrackedMesh64=ROBOT
            LoopingTrackedSound64=engine
            LoopingTrackedBone64=MOTOR
            OnlyAddLoopingSoundIfBoneUsed64=true
            TrackedMesh65=ignored
            TrackedSound65=ignored
            [Empty]
            """)).Room(0,"missing");
        Assert.Empty(room.Entries);
        Assert.Equal(2,room.Tracked.Count);
        Assert.Equal(new EnbStationTrackedSound("ROBOT","ROOTTRANSFORM","beep",false,true,500,4000),room.Tracked[0]);
        Assert.Equal(new EnbStationTrackedSound("ROBOT","MOTOR","engine",true,true,0,0),room.Tracked[1]);
    }

    [Fact]
    public void BindingRequiresActualObjectAndBoneAndHonorsUsedGuard()
    {
        var hierarchy=new EnbW3dRenderHierarchy(0,"ROBOT",default,false,
            [new(0,"ROOTTRANSFORM",uint.MaxValue,default,default,System.Numerics.Quaternion.Identity,System.Numerics.Matrix4x4.Identity),
             new(1,"MOTOR",0,new(5,0,0),default,System.Numerics.Quaternion.Identity,System.Numerics.Matrix4x4.CreateTranslation(5,0,0)),
             new(2,"UNUSED",0,default,default,System.Numerics.Quaternion.Identity,System.Numerics.Matrix4x4.Identity)],[]);
        var resource=new EnbW3dRenderResource(new("robot","offline",0,0),"robot",[],[hierarchy],[],[],
            [new("ROBOT","ROBOT",[new(0,0,0,[new(1,"body")])])]);
        var obj=new EnbStationSoundObject("instance",resource,null);
        var room=new EnbStationAmbientRoom("Hangar",[]) {Tracked=[
            new("ROBOT","MOTOR","loop",true,true,0,0),
            new("ROBOT","MOTOR","timed",false,false,1000,2000),
            new("ROBOT","UNUSED","unused",true,true,0,0),
            new("ROBOT","missing","missing",true,false,0,0),
            new("OTHER","MOTOR","other",true,false,0,0),
            new("robot","MOTOR","case",true,false,0,0),
            new("ROBOT","ROOTTRANSFORM","root",true,false,0,0)]};
        Assert.Empty(EnbStationSoundObject.Bind(room,[]).Entries);
        var bound=EnbStationSoundObject.Bind(room,[obj]);
        Assert.Equal(new[]{"loop","timed"},bound.Entries.Select(e=>e.Sound));
        Assert.True(bound.Entries[0].Binding!.Follow);
        Assert.False(bound.Entries[1].Binding!.Follow);
        Assert.True(obj.TryPosition("motor",true,out var position));
        Assert.Equal(new System.Numerics.Vector3(5,0,0),position);
    }
    [Fact]
    public void RoomModelOverridesGenericSectionAndSparseSlotsSurvive()
    {
        var catalog = new EnbRetailStationSoundCatalog(EnbRetailIniDocument.Parse("""
            [General]
            DefaultMinDelay=1.25
            DefaultMaxDelay=2.5
            [Hangar]
            LoopingAmbient1=generic
            [PSD01A]
            LoopingAmbient2=hangar
            ;NonLoopingAmbient1=disabled
            NonLoopingAmbient3=announcement
            FixedPosition3=false
            MinDelay3=45
            MaxDelay3=120
            NonLoopingAmbient64=misc
            NonLoopingAmbient65=outside-retail-bound
            """));
        var room = catalog.Room(0, "psd01a");
        Assert.Equal(new[] { "hangar", "announcement", "misc" }, room.Entries.Select(e => e.Sound));
        Assert.False(room.Entries[1].FixedPosition);
        Assert.Equal(45000, room.Entries[1].MinDelayMilliseconds);
        Assert.Equal(120000, room.Entries[1].MaxDelayMilliseconds);
        Assert.True(room.Entries[2].FixedPosition);
        Assert.Equal(1250, room.Entries[2].MinDelayMilliseconds);
        Assert.Equal("generic", Assert.Single(catalog.Room(0, "missing").Entries).Sound);
        Assert.Empty(catalog.Room(2, "missing").Entries);
    }

    [Fact]
    public void FiniteVoiceDefersRoomChangeAndTimersDoNotCatchUpOrRepeatLoops()
    {
        var first = new EnbStationAmbientRoom("one", [new("loop", true, 0, 0, .5f, .5f, false),
            new("pa", false, 1000, 1000, .5f, .5f, false)]);
        var second = first with { Section = "two" };
        var runtime = new EnbStationAmbientRuntime(new Random(1));
        Assert.Equal("loop", Assert.Single(runtime.Update(first, 0, false)).Sound);
        Assert.Empty(runtime.Update(first, 999, false));
        Assert.Equal("pa", Assert.Single(runtime.Update(first, 1000, false)).Sound);
        Assert.Empty(runtime.Update(second, 2000, true));
        Assert.False(runtime.ChangedRoom);
        Assert.Equal("loop", Assert.Single(runtime.Update(second, 3000, false)).Sound);
        Assert.True(runtime.ChangedRoom);
        Assert.Empty(runtime.Update(second, 3999, false));
        Assert.Equal("pa", Assert.Single(runtime.Update(second, 12000, false)).Sound);
        Assert.Empty(runtime.Update(second, 12000, false));
    }
}
