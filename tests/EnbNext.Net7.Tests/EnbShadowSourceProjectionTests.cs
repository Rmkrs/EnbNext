using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbShadowSourceProjectionTests
{
    private static readonly EnbW3dAxisAlignedBounds Bounds = new(new(1,2,3),new(4,5,6));

    [Theory]
    [InlineData((byte)0, true, true)]
    [InlineData((byte)0x26, true, true)]
    [InlineData((byte)0x29, true, false)]
    [InlineData((byte)0, false, false)]
    public void SpaceUpdatePropagatesIdenticalTransformExceptType29OrMissingRenderObject(
        byte objectType, bool hasRenderObject, bool refresh)
    {
        var source = new EnbCurrentRetailShadowSourceProjection();
        source.Update(Bounds,Matrix4x4.Identity,Vector3.UnitZ,64,false);
        var first = source.Projection;
        source.NotifySpaceObjectUpdate(objectType,hasRenderObject);
        Assert.Equal(refresh,source.Update(Bounds,Matrix4x4.Identity,-Vector3.UnitZ,64,false));
        if(refresh) Assert.NotEqual(first!.TextureFromWorld,source.Projection!.TextureFromWorld);
        else Assert.Same(first,source.Projection);
    }

    [Fact]
    public void OrdinarySourceRetainsProjectionUntilTransformSetterRuns()
    {
        var source = new EnbCurrentRetailShadowSourceProjection();
        Assert.True(source.Update(Bounds,Matrix4x4.Identity,Vector3.UnitZ,64,false));
        var first = source.Projection;
        // A light change alone does not invalidate ordinary retained source geometry.
        Assert.False(source.Update(Bounds,Matrix4x4.Identity,-Vector3.UnitZ,64,false));
        Assert.Same(first,source.Projection);
        source.MarkTransformDirty();
        Assert.True(source.Update(Bounds,Matrix4x4.Identity,-Vector3.UnitZ,64,false));
        Assert.NotEqual(first!.TextureFromWorld,source.Projection!.TextureFromWorld);
        Assert.False(source.TransformDirty);
    }

    [Fact]
    public void RecomputedAggregateBoundsDoNotReplaceOrdinaryProjectionWithoutTransformNotification()
    {
        // 0098E4B0 -> 0098F1D0 -> 00461800 recomputes the owner bounds,
        // but does not write projector-dirty D4. 00460BF0 still gates refresh.
        var source = new EnbCurrentRetailShadowSourceProjection();
        source.Update(Bounds,Matrix4x4.Identity,Vector3.UnitZ,64,false);
        var retained = source.Projection;
        var expanded = new EnbW3dAxisAlignedBounds(Bounds.Center,Bounds.Extents * 2);
        Assert.False(source.Update(expanded,Matrix4x4.Identity,Vector3.UnitZ,64,false));
        Assert.Same(retained,source.Projection);
        source.MarkTransformDirty();
        Assert.True(source.Update(expanded,Matrix4x4.Identity,Vector3.UnitZ,64,false));
        Assert.Equal(EnbCurrentRetailShadowProjection.Create(expanded.Center,expanded.Extents,
            Matrix4x4.Identity,Vector3.UnitZ,64),source.Projection);
    }

    [Fact]
    public void InstanceDataSourceRefreshesBoundsAndDirectionWithoutTransformWrite()
    {
        var source = new EnbCurrentRetailShadowSourceProjection();
        source.Update(Bounds,Matrix4x4.Identity,Vector3.UnitZ,64,true);
        var changedBounds = new EnbW3dAxisAlignedBounds(new(9,8,7),new(3,2,1));
        Assert.True(source.Update(changedBounds,Matrix4x4.Identity,-Vector3.UnitZ,64,true));
        Assert.Equal(EnbCurrentRetailShadowProjection.Create(changedBounds.Center,changedBounds.Extents,
            Matrix4x4.Identity,-Vector3.UnitZ,64),source.Projection);
    }

    [Fact]
    public void MissingCurrentObjectPreservesProjectionAndPendingInvalidation()
    {
        var source = new EnbCurrentRetailShadowSourceProjection();
        Assert.False(source.Update(null,Matrix4x4.Identity,Vector3.UnitZ,64,false));
        Assert.True(source.TransformDirty);
        Assert.Null(source.Projection);
        source.Update(Bounds,Matrix4x4.Identity,Vector3.UnitZ,64,false);
        var first = source.Projection;
        source.MarkTransformDirty();
        Assert.False(source.Update(null,Matrix4x4.Identity,Vector3.UnitZ,64,false));
        Assert.True(source.TransformDirty);
        Assert.Same(first,source.Projection);
        var moved = Matrix4x4.CreateTranslation(20,30,40);
        Assert.True(source.Update(Bounds,moved,Vector3.UnitZ,64,false));
        Assert.Equal(EnbCurrentRetailShadowProjection.Create(Bounds.Center,Bounds.Extents,moved,Vector3.UnitZ,64),source.Projection);
    }
}
