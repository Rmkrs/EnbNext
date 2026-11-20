using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;
public sealed class EnbTextureBindingListPoolTests
{
    [Fact]
    public void SharesOnlyIdenticalReferencesAndPreservesNullStageOrder()
    {
        var pool=new EnbW3dTextureBindingListPool();
        var a=new EnbW3dRenderTextureBinding(1,"same",null);var b=new EnbW3dRenderTextureBinding(1,"same",null);
        Assert.Equal(a,b);Assert.NotSame(pool.Get(1,a),pool.Get(1,b));
        Assert.Same(pool.Get(2,a,b),pool.Get(2,a,b));
        Assert.NotSame(pool.Get(2,a,null),pool.Get(2,null,a));
        Assert.Equal(new EnbW3dRenderTextureBinding?[]{null,a},pool.Get(2,null,a));
        var mutable=new EnbW3dRenderTextureBinding?[]{a,b};var frozen=pool.Freeze(mutable);mutable[0]=null;
        Assert.Same(a,frozen[0]);Assert.Throws<NotSupportedException>(()=>((IList<EnbW3dRenderTextureBinding?>)frozen)[0]=null);
    }
    [Fact]
    public void WiderStageListsStillDetachWithoutChangingElements()
    {
        var pool=new EnbW3dTextureBindingListPool();var a=new EnbW3dRenderTextureBinding(1,"a",null);
        var source=new EnbW3dRenderTextureBinding?[]{a,null,a};var frozen=pool.Freeze(source);Array.Fill(source,null);
        Assert.Equal(new EnbW3dRenderTextureBinding?[]{a,null,a},frozen);Assert.Empty(pool.Get(0));
    }
}
