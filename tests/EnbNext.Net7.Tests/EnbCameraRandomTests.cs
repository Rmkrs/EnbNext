using System.Numerics;
using EnbNext.Rendering;
using EnbNext.Net7.Gameplay;
namespace EnbNext.Net7.Tests;
public sealed class EnbCameraRandomTests
{
    [Fact]
    public void InclusiveIntegerUsesLowBitsAndRejectsRatherThanScaling()
    {
        var words = new Queue<uint>([0x80000000, 1, 3, 7, 2, 99]);
        var random = new EnbRetailCameraRandomSource(words.Dequeue);
        Assert.Equal(0, random.Next(0,1)); // upper-half word is not a true coin
        Assert.Equal(1, random.Next(0,1));
        Assert.Equal(2, random.Next(0,2)); // rejects both 3 and 7
        Assert.Equal(4, random.Next(4,4)); // singleton consumes nothing
        Assert.Single(words);
    }
    [Theory]
    [InlineData(0x80000001u,-3)] [InlineData(0xffffffffu,-1)]
    [InlineData(0x80000000u,0)] [InlineData(3u,3)]
    public void GateUsesSignedRemainder(uint word,int quadrant)
        => Assert.Equal(quadrant,new EnbRetailCameraRandomSource(()=>word).GateQuadrant());
    [Fact]
    public void SeededCameraStreamIsIndependentOfMapperAndKeepsMixedDrawOrder()
    {
        // 00444240 seeds BDB1C0 with 1105, yielding the same words as the
        // independently seeded mapper owner, without sharing its cursor.
        var random = new EnbRetailCameraRandomSource();
        Assert.Equal(1,random.Next(0,1)); // D13C8AF5
        Assert.Equal(-2,random.GateQuadrant()); // FFC27482
        _ = new EnbW3dRetailMapperRandomSource().NextUInt32();
        Assert.Equal(1,random.Next(0,1)); // 82A6958B
        Assert.Equal(2,random.Next(0,2)); // 21AC240A
    }
    [Fact]
    public void GateMissingTargetConsumesNothingAndNegativeQuadrantKeepsApproachAngle()
    {
        var words=new Queue<uint>([0,0xffffffff,0]);
        var runtime=new EnbGateCameraRuntime(new EnbRetailCameraRandomSource(words.Dequeue));
        var ship=EnbCameraControlTests.Ship(0,1);
        var request=new Net7GateCameraTransition(1,99,0);
        Assert.Null(runtime.Update(request,ship,null,100,Vector3.Zero,0,out _));
        Assert.Equal(3,words.Count);
        runtime.Reset();
        var gate=ship with {ObjectType=11, Spatial=ship.Spatial! with
            {Transform=ship.Spatial.Transform with {Position=new(10000,0,0)}}};
        var start=runtime.Update(request,ship,gate,100,Vector3.Zero,0,out bool commit);
        Assert.False(commit);Assert.Equal(new Vector3(225,112.5f,0),start!.Value.Position);
        Assert.Single(words);
        runtime.Update(request,ship,gate,100,Vector3.Zero,1,out _);
        Assert.Single(words); // ordinary update is not a new draw
        var end=runtime.Update(request,ship,gate,100,Vector3.Zero,7001,out commit);
        Assert.True(commit);Assert.Empty(words);
        Assert.Equal(new Vector3(10375,225,0),end!.Value.Position);
    }
    [Fact]
    public void DockWithoutAuthoredBoneStillConsumesItsEntryDrawsForExistingTarget()
    {
        int draws=0;
        var runtime=new EnbDockCameraRuntime(new EnbRetailCameraRandomSource(()=>{draws++;return 0;}));
        var ship=EnbCameraControlTests.Ship(0,1);
        var request=new Net7DockCameraTransition(1,99,0,false);
        Assert.Null(runtime.Update(request,ship,null,0,out bool complete,targetExists:true));
        Assert.True(complete);Assert.Equal(2,draws);
        runtime.Update(request,ship,null,1,out complete,targetExists:true);
        Assert.False(complete);Assert.Equal(2,draws);
        runtime.Reset();
        runtime.Update(request,ship,null,2,out complete,targetExists:false);
        Assert.True(complete);Assert.Equal(2,draws);
    }

}
