using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbMaterialBatchIdentityTests
{
    private static readonly EnbW3dMaterialUniforms Material=new(Vector3.One,Vector3.One,
        Vector3.Zero,Vector3.Zero,0,1,default,default);
    private static EnbCurrentRetailMaterialBatchIdentity Key(object? mapper=null,
        EnbW3dMaterialUniforms? material=null, int coordinate=0, bool lighting=true, uint flags=0)
        => EnbCurrentRetailMaterialBatchIdentity.Create(material ?? Material,flags,
            EnbW3dVertexLightingSemantics.RetailDefault with {LightingEnabled=lighting},
            [coordinate,1,2,3,4,5,6,7],0,[mapper,null,null,null,null,null,null,null]);

    [Fact]
    public void EqualStateSharesAKeyButEqualLookingMapperClonesDoNot()
    {
        object first=new Mapper(3), clone=new Mapper(3);
        Assert.Equal(Key(),Key());
        Assert.Equal(Key(first),Key(first));
        Assert.Equal(Key(first).GetHashCode(),Key(first).GetHashCode());
        Assert.NotEqual(Key(first),Key(clone));
        Assert.NotEqual(Key(first),Key());
    }

    [Fact]
    public void BackingBitsCoordinatesFlagsAndLightingRemainDistinct()
    {
        Assert.NotEqual(Key(),Key(material:Material with {DiffuseAlpha=.5f}));
        Assert.NotEqual(Key(),Key(material:Material with {Power=-0.0f}));
        Assert.NotEqual(Key(),Key(coordinate:1));
        Assert.NotEqual(Key(),Key(lighting:false));
        Assert.NotEqual(Key(),Key(flags:4));
    }

    [Fact]
    public void SnapshotDoesNotFollowCallerArrayMutation()
    {
        int[] coordinates=[0,1,2,3,4,5,6,7];
        object?[] mappers=new object?[8];
        var snapshot=EnbCurrentRetailMaterialBatchIdentity.Create(Material,0,
            EnbW3dVertexLightingSemantics.RetailDefault,coordinates,0,mappers);
        coordinates[0]=7; mappers[0]=new();
        Assert.Equal(Key(),snapshot);
    }

    private sealed record Mapper(int Value);

    [Fact]
    public void ConfiguredMaterialWordSeparatesOtherwiseEqualBatchState()
    {
        object material=new();using var owner=new EnbCurrentRetailMeshModelOwner([(material,null,null)]);
        uint nextId=91;owner.PrepareConfiguredMesh(false,true,0,ref nextId);
        var configured=EnbCurrentRetailMaterialBatchIdentity.Create(Material,0,EnbW3dVertexLightingSemantics.RetailDefault,
            [0,1,2,3,4,5,6,7],EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(material),new object?[8]);
        Assert.NotEqual(Key(),configured);
        using var clone=owner.CloneRenderObject();clone.EnsureUniqueModel(0);
        var copied=EnbCurrentRetailMaterialBatchIdentity.Create(Material,0,EnbW3dVertexLightingSemantics.RetailDefault,
            [0,1,2,3,4,5,6,7],clone.MaterialRuntimeWord64At(0),new object?[8]);
        Assert.Equal(configured,copied);Assert.Equal(92u,nextId);
    }
}
