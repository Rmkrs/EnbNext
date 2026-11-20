using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbConfiguredDazzleBoundsTests
{
    [Fact]
    public void NamedHierarchyKeepsMeshAndPivotScaleSeparateFromStoredDazzle()
    {
        const float stored=.30131307f,modelScale=3,sourceScale=7;
        var box=new EnbCurrentRetailCollectionBounds(new(0,0,0,stored),Vector3.Zero,new(stored));
        var leaf=new EnbW3dRenderCompositeChild("dazzle",32,box,0){DazzleDefinitionRadius=1,DazzleInstanceScale=stored};
        var mesh=new EnbW3dRenderCompositeChild("mesh",0,box,0){MeshMinimum=new(10,0,0),MeshMaximum=new(11,0,0)};
        var hierarchy=new EnbW3dRenderHierarchy(0x40000,"inner",Vector3.Zero,false,
            [new(0,"root",uint.MaxValue,Vector3.Zero,Vector3.Zero,Quaternion.Identity,Matrix4x4.Identity),
             new(1,"pivot",0,new(2,0,0),Vector3.Zero,Quaternion.Identity,Matrix4x4.CreateTranslation(2,0,0))],[]);
        var hlod=new EnbW3dRenderHlod("inner","inner",[new(0,0,0,[new(1,"dazzle"),new(1,"mesh")])]);
        var inner=new EnbW3dRenderResource(new("fixture","fixture",0,0),"fixture",[],[hierarchy],[],[],[hlod])
            {CompositeRenderChildren=[leaf,mesh]};
        var child=new EnbW3dRenderCompositeChild("inner",25,box,0){HlodResource=inner,Hlod=hlod};
        var parentHierarchy=hierarchy.Copy().SetName("parent").SetPivots([hierarchy.Pivots[0]]);
        var parent=new EnbW3dRenderHlod("parent","parent",[new(0,0,0,[new(0,"inner")])]);
        var resource=inner.Copy().SetHierarchies([parentHierarchy]).SetHierarchicalLods([parent]).SetCompositeRenderChildren([child]);
        var post=new EnbW3dHierarchyState("parent",Matrix4x4.Identity,modelScale,new Dictionary<int,Matrix4x4>())
            {PendingHierarchyScale=sourceScale};
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,parent,Matrix4x4.Identity,
            Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),post,out var actual,out var error),error);
        float radius=stored*sourceScale;
        Assert.Equal(new Vector3((6-radius+39)*.5f,0,0),actual.LocalBox.Center);
        Assert.Equal(new Vector3((39-(6-radius))*.5f,radius,radius),actual.LocalBox.Extents);
    }

    [Theory]
    [InlineData(1f,7f)]
    [InlineData(3f,1f)]
    [InlineData(2f,1.31f)]
    public void CollectionQueriesStoredDazzleWithRemainingSourceScale(float modelScale,float sourceScale)
    {
        //98EF60/991220 ->96BAB0 stores child scale;96BEE0 reads that
        //store, independent of the mesh scale used by the same parent.
        const float stored=.30131307f;
        var box=new EnbCurrentRetailCollectionBounds(new(0,0,0,stored),Vector3.Zero,new(stored));
        var leaf=new EnbW3dRenderCompositeChild("dazzle",32,box,0){DazzleDefinitionRadius=1,DazzleInstanceScale=stored};
        var collection=new EnbW3dRenderCompositeChild("collection",23,box,0){CollectionChildren=[leaf]};
        var hierarchy=new EnbW3dRenderHierarchy(0x40000,"parent",Vector3.Zero,false,
            [new(0,"root",uint.MaxValue,Vector3.Zero,Vector3.Zero,Quaternion.Identity,Matrix4x4.Identity)],[]);
        var hlod=new EnbW3dRenderHlod("parent","parent",[new(0,0,0,[new(0,"collection")])]);
        var resource=new EnbW3dRenderResource(new("fixture","fixture",0,0),"fixture",[],[hierarchy],[],[],[hlod])
            {CompositeRenderChildren=[collection]};
        var post=new EnbW3dHierarchyState("parent",Matrix4x4.Identity,modelScale,new Dictionary<int,Matrix4x4>())
            {PendingHierarchyScale=sourceScale};
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,
            Array.Empty<EnbW3dResolvedWeightedAnimationSample>(),post,out var actual,out var error),error);
        Assert.Equal(new Vector3(stored*sourceScale),actual.LocalBox.Extents);
    }
}
