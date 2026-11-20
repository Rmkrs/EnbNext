using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbNavigationMapProjectionTests
{
    [Fact]
    public void CoordinatesRoundTripAndPositiveYPointsUp()
    {
        var map=new EnbNavigationMapProjection(); map.CenterAt(new(42,-30,999));
        Vector2 viewport=new(360,260); Vector3 position=new(42.2f,-29.9f,500);
        var screen=map.Project(position,viewport);
        Assert.True(screen.Y<viewport.Y/2);
        var result=map.Unproject(screen,viewport);
        Assert.Equal(position.X,result.X,4); Assert.Equal(position.Y,result.Y,4); Assert.Equal(0,result.Z);
    }
    [Fact]
    public void DragMovesMapWithPointerAndReleasesObjectLock()
    {
        var map=new EnbNavigationMapProjection(); map.CenterAt(new(100,200,0),7);
        Vector2 viewport=new(360,260); Vector3 position=new(100,200,0);
        Vector2 before=map.Project(position,viewport), delta=new(10,-25);
        map.Pan(delta,viewport);
        var after=map.Project(position,viewport);
        Assert.True(Vector2.Distance(before+delta,after)<.003f);
        Assert.Null(map.LockedObjectId);
    }
    [Fact]
    public void ZoomUsesRetailStepAndFloorWithoutMovingCenter()
    {
        var map=new EnbNavigationMapProjection(); map.CenterAt(new(10,20,0),8);
        map.StepZoom(.25f); Assert.Equal(1.5f,map.Zoom);
        map.StepZoom(-1); Assert.Equal(.5f,map.Zoom);
        map.StepZoom(float.PositiveInfinity); Assert.Equal(.5f,map.Zoom);
        Assert.Equal(new Vector2(10,20),map.Center); Assert.Equal(8u,map.LockedObjectId);
        map.Follow([]); Assert.Equal(8u,map.LockedObjectId);
    }
    [Fact]
    public void MovingPresentationExpandsBoundsThenPreservesScaleOnNextRefresh()
    {
        var map=new EnbNavigationMapProjection();
        map.FitBounds(new(-100,-50),new(100,50));
        Vector2 viewport=new(400,200);
        map.BeginRefresh(viewport);
        Assert.Equal(2,map.Scale(viewport));
        map.ObservePresentedObject(7,new(300,0,0),10);
        map.EndRefresh();
        Assert.Equal(new Vector2(410,100),map.Extent);
        Assert.Equal(1,map.Zoom);
        Assert.Equal(2,map.Scale(viewport));
        map.CenterAt(new(1000,-1000,0),7);
        Assert.Equal(new Vector2(310,-50),map.Center);
        map.BeginRefresh(viewport);
        Assert.Equal(2.05f,map.Zoom,5);
        Assert.Equal(2,map.Scale(viewport),5);
        map.Pan(new(10000,-10000),viewport);
        Assert.Equal(new Vector2(-100,-50),map.Center);
        Assert.Null(map.LockedObjectId);
        map.BeginRefresh(viewport*2);
        Assert.Equal(4,map.Scale(viewport*2),5);
        Assert.Equal(2.05f,map.Zoom,5);
    }
    [Fact]
    public void RadiusAloneDoesNotExpandUntilPresentationReentersOrMoves()
    {
        var map=new EnbNavigationMapProjection();
        map.FitBounds(new(-100),new(100));
        map.BeginRefresh(new(400));
        map.ObservePresentedObject(7,Vector3.Zero,10); map.EndRefresh();
        map.ObservePresentedObject(7,Vector3.Zero,300); map.EndRefresh();
        Assert.Equal(new Vector2(200),map.Extent);
        map.EndRefresh(); // No presentation this refresh: detached from map scene.
        map.ObservePresentedObject(7,Vector3.Zero,300); map.EndRefresh();
        Assert.Equal(new Vector2(600),map.Extent);
        map.BeginRefresh(new(400));
        Assert.Equal(3,map.Zoom);
        map.ObservePresentedObject(7,new(1,0,0),400); map.EndRefresh();
        Assert.Equal(new Vector2(800),map.Extent);
    }
    [Fact]
    public void AlternateCenterRetainsBoundsAndLocksShipAtHalfZoom()
    {
        var map=new EnbNavigationMapProjection();
        map.FitBounds(new(-100),new(100)); map.Scale(new(400));
        map.StepZoom(.5f);
        map.CenterOnLocalShip(new(25,30,0),7,false);
        Assert.Equal(2,map.Zoom);
        map.CenterOnLocalShip(new(25,30,0),7,true);
        Assert.Equal(.5f,map.Zoom); Assert.Equal(1,map.Scale(new(400)));
        Assert.Equal(new Vector2(25,30),map.Center); Assert.Equal(7u,map.LockedObjectId);
        Assert.Equal(new Vector2(200),map.Extent);
    }
    [Theory]
    [InlineData(0x25,false,"m_deco.w3d")]
    [InlineData(12,false,"m_sbase.w3d")]
    [InlineData(3,false,"m_planet.w3d")]
    [InlineData(3,true,"m_unknown.w3d")]
    public void MarkersUseMapAssetsNotGutterTextures(byte type,bool unknown,string expected)
        => Assert.Equal(expected,EnbNavigationMapProjection.Icon(type,unknown));
}
