using EnbNext.Presentation;
using EnbNext.Rendering;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailShipDamageTests
{
    private static EnbSceneShipPresentationSnapshot Ship(params float[] health)
        =>new(false,null,null,null,false,null,0,[],[]){VisibleQuadrantHealth=[..health]};

    [Fact]
    public void QuadrantsAndOverallChangeIndependentlyAtRetailBoundaries()
    {
        var state=new EnbRetailShipDamageRuntime();
        state.Update(Ship(1,.99f,.75f,.5f),new(1,100,90,100));
        Assert.Equal(new[]{1,2,3,4},state.Quadrants.Select(q=>q.Level));
        Assert.Equal(new uint[]{1,2,2,2},state.Quadrants.Select(q=>q.Generation));
        Assert.Equal(2,state.Overall.Level);
        var quadrants=state.Quadrants;var overall=state.Overall;
        state.Update(Ship(1,.99f,.75f,.5f),new(1,100,90,100));
        Assert.Equal(quadrants.ToArray(),state.Quadrants.ToArray());Assert.Equal(overall,state.Overall);
        state.Update(Ship(1,.99f,.75f,.5f),new(1,100,50,100));
        Assert.Equal(quadrants.ToArray(),state.Quadrants.ToArray());Assert.Equal(4,state.Overall.Level);
        Assert.Equal(overall.Generation+1,state.Overall.Generation);
        state.Update(Ship(1,1,1,1),new(1,100,100,100));
        Assert.All(state.Quadrants,q=>Assert.Equal(1,q.Level));Assert.Equal(1,state.Overall.Level);
    }

    [Fact]
    public void MissingVitalsAndQuadrantsAreHealthyAndOrganicUsesShield()
    {
        var state=new EnbRetailShipDamageRuntime();state.Update(null,null);
        Assert.All(state.Quadrants,q=>Assert.Equal(new(1,1),q));Assert.Equal(new(1,1),state.Overall);
        state.Update(Ship() with {IsOrganic=true},new(.5f,100,100,100));Assert.Equal(4,state.Overall.Level);
        state.Update(Ship(),new(.1f,100,null,0));Assert.Equal(1,state.Overall.Level);
        state.Update(Ship(2,-2,1,1),new(0,100,-5,100));
        Assert.Equal(1,state.Quadrants[0].Level);Assert.Equal(4,state.Quadrants[1].Level);Assert.Equal(4,state.Overall.Level);
    }

    [Fact]
    public void DamageSoundUsesPriorCacheAndStrictDeadlineWithoutCatchUp()
    {
        var state=new EnbRetailShipDamageRuntime();
        Assert.Null(state.TakeSound(100));
        state.Update(Ship(1,.9f,1,1),null);
        Assert.Equal("Level_1_Ship_Damage",state.TakeSound(101));
        state.Update(Ship(1,.5f,1,1),null);
        Assert.Null(state.TakeSound(6101));
        Assert.Equal("Level_3_Ship_Damage",state.TakeSound(6102));
        Assert.Null(state.TakeSound(10102));
        Assert.Equal("Level_3_Ship_Damage",state.TakeSound(20000));
        Assert.Null(state.TakeSound(20000));
        state.Update(Ship(1,1,1,1),null);
        Assert.Null(state.TakeSound(30000));
        state.Update(Ship(),new(1,100,70,100));
        Assert.Equal("Level_2_Ship_Damage",state.TakeSound(30001));
        Assert.Null(state.TakeSound(35001));
    }

    [Fact]
    public void ModelReplacementResetsVisualTiersWithoutResettingTheObjectSoundDeadline()
    {
        var state=new EnbRetailShipDamageRuntime();state.Update(Ship(1,1,.5f,1),null);
        Assert.Equal("Level_3_Ship_Damage",state.TakeSound(100));
        state.ResetVisuals();Assert.All(state.Quadrants,q=>Assert.Equal(1,q.Level));
        state.Update(Ship(1,1,.9f,1),null);
        Assert.Null(state.TakeSound(4100));Assert.Equal("Level_1_Ship_Damage",state.TakeSound(4101));
    }

    [Fact]
    public void ReceivedQuadrantsReachDetachedPresentationAndRetainUntouchedDefaults()
    {
        var schema=new Net7AuxDataSchemaCatalog().CreateObjectSchema(1)!;
        var array=schema.Root.Children.Single(p=>p.Path=="ShipAuxData.VisibleQuadrantDamagePercent");
        var world=new Net7WorldState();world.MaterializeCreate(new Net7CreateUpdate(23,1,10,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();
        Assert.Empty(builder.Build(world,null,0).Objects[0].ShipPresentation!.VisibleQuadrantHealth);
        using var delta=new MemoryStream();
        using(var writer=new BinaryWriter(delta,System.Text.Encoding.UTF8,true))
        {writer.Write((byte)0);writer.Write(1u);writer.Write((uint)array.Children[2].Id);writer.Write(.5f);writer.Write(0u);writer.Write(0u);}
        using var payload=new MemoryStream();
        using(var writer=new BinaryWriter(payload,System.Text.Encoding.UTF8,true))
        {writer.Write(23u);writer.Write((ushort)delta.Length);writer.Write(delta.ToArray());}
        world.Apply(Packet.ForOpcode(0x001B,payload.ToArray()),0);
        Assert.Equal(new[]{1f,1f,.5f,1f},builder.Build(world,null,0).Objects[0].ShipPresentation!.VisibleQuadrantHealth.ToArray());
    }
}
