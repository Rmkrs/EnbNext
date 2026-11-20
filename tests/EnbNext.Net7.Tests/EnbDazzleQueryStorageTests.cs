using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbDazzleQueryStorageTests
{
    private static EnbResolvedRenderCamera Camera(float x=0)
    {
        Assert.True(EnbResolvedRenderCamera.TryCreate(new(x,0,0),Quaternion.CreateFromAxisAngle(Vector3.UnitY,MathF.PI/2),new(640,480,1),Math.PI/2,new(1,1000),out var camera));return camera;
    }
    [Fact]
    public void NestedQueriesKeepOuterCandidatesAndProjectionIntact()
    {
        var storage=new EnbDazzleQueryStorage();var camera=Camera();int casts=0,nested=0;
        EnbDazzleCollisionRoot[] inner=[new(new(0,0,-5,20),true,false,static(_,_,f,b)=>(f,b))];
        var root=new EnbDazzleCollisionRoot(new(0,0,-5,20),true,false,(s,e,f,b)=>
        {
            casts++;nested++;
            Assert.Equal(1,EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,Camera(1),inner,storage:storage));
            return(f,b);
        });
        Assert.Equal(1,EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,camera,[root],storage:storage));
        Assert.Equal(16,casts);Assert.Equal(16,nested);
    }
    [Fact]
    public void ExceptionsDrainQuerySlotsAndReleaseSceneRoots()
    {
        var storage=new EnbDazzleQueryStorage();var camera=Camera();
        var bad=new EnbDazzleCollisionRoot(new(0,0,-5,20),true,false,(_,_,_,_)=>throw new InvalidOperationException("fixture"));
        Assert.Throws<InvalidOperationException>(()=>EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,camera,[bad],storage:storage));
        var weak=UseRoot(storage,camera);
        GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();Assert.False(weak.IsAlive);GC.KeepAlive(storage);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UseRoot(EnbDazzleQueryStorage storage,EnbResolvedRenderCamera camera)
    {
        var root=new EnbDazzleCollisionRoot(new(0,0,-5,20),true,false,static(_,_,f,b)=>(f,b));
        Assert.Equal(1,EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,camera,[root],storage:storage));return new(root);
    }
    [Fact]
    public void WarmQueriesAndChangingCamerasReuseStorage()
    {
        var storage=new EnbDazzleQueryStorage();
        EnbDazzleCollisionRoot[] roots=[new(new(0,0,-5,20),true,false,static(_,_,f,b)=>(f,b))];
        var cameras=new[]{Camera(),Camera(.1f),Camera(-.1f)};
        for(int i=0;i<100;i++)EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,cameras[i%3],roots,storage:storage);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<1000;i++)EnbRetailDazzleVisibility.Evaluate(new(0,0,-10),1,1,cameras[i%3],roots,storage:storage);
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }
}
