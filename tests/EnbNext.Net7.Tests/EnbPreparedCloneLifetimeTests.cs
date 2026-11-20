using System.Runtime.CompilerServices;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbPreparedCloneLifetimeTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void CopiedDescriptorDoesNotRetainAnUnusableOriginalCache(int kind)
    {
        var (source,child)=CopyPopulated(kind);
        Assert.Null(child.ConsumerState);Assert.Null(child.MeshPreparation);Assert.Null(child.QueryProjection);
        for(int i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
        Assert.False(source.IsAlive);GC.KeepAlive(child);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference,EnbW3dMeshDescriptor) CopyPopulated(int kind)
    {
        var source=new EnbW3dMeshDescriptor{GameId=1};
        if(kind==0)source.ConsumerState=new object();
        if(kind==1)source.MeshPreparation=new object();
        if(kind==2)source.QueryProjection=new EnbSubmittedProjectionBinding();
        return(new WeakReference(source),source with{GameId=2});
    }
}
