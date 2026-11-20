using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbUiModelQueryTests
{
    private static EnbW3dRenderMesh Triangle() => new(0, "ui", "", 3,
        [new(-20,-20,0),new(20,-20,0),new(0,20,0)],[],[],[0,1,2],[],[],[])
        { BoundingBoxMin=new(-20,-20,0), BoundingBoxMax=new(20,20,0), BoundingRadius=30 };

    [Fact]
    public void MainViewUsesItsInterfaceCameraAndAuthoredTriangle()
    {
        var pick=EnbUiModelQuery.BindMainView(Triangle(),Matrix4x4.Identity);
        Assert.Equal(new Vector3(0,0,320),pick.Camera.Position);
        Assert.Equal(new Vector4(-1,-.75f,1,.75f),pick.Camera.Projection.StoredProjectionBounds);
        Assert.Equal(new EnbRenderClipRange(300,1000),pick.Camera.Projection.ClipRange);
        Assert.True(pick.Contains(.5,.5));
        // Inside the bounding rectangle, outside the authored triangle.
        Assert.False(pick.Contains(338d/640,222d/480));
        Assert.False(pick.Contains(double.NaN,.5));
        Assert.False(pick.Contains(-.1,.5));
        Assert.False(pick.Contains(.5,1.1));
    }

    [Fact]
    public void WarmQueriesDoNotAllocate()
    {
        var pick=EnbUiModelQuery.BindMainView(Triangle(),Matrix4x4.Identity);
        for(int i=0;i<1024;i++)pick.Contains(.5,.5);
        long before=GC.GetAllocatedBytesForCurrentThread();
        int hits=0;
        for(int i=0;i<1024;i++)if(pick.Contains(.5,.5))hits++;
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(1024,hits);
        Assert.Equal(0,allocated);
    }

    [Fact]
    public void PreparedGeometryIsOwnedByMeshAndCopyStartsEmpty()
    {
        var mesh=Triangle();
        var first=EnbUiModelQuery.BindMainView(mesh,Matrix4x4.Identity);
        var translated=EnbUiModelQuery.BindMainView(mesh,Matrix4x4.CreateTranslation(50,0,0));
        Assert.Same(first.Mesh,translated.Mesh);
        Assert.False(translated.Contains(.5,.5));
        Assert.True(translated.Contains(370d/640,.5));
        var copy=mesh.Copy();
        Assert.Null(copy.DerivedUiModelRay);
        var copied=EnbUiModelQuery.BindMainView(copy,Matrix4x4.Identity);
        Assert.NotSame(first.Mesh,copied.Mesh);
        Assert.True(copied.Contains(.5,.5));
    }
}
