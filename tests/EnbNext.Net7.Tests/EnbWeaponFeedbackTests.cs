using EnbNext.Rendering;
using EnbNext.Presentation;
using EnbNext.Net7.World;
namespace EnbNext.Net7.Tests;

public sealed class EnbWeaponFeedbackTests
{
    [Fact]
    public void InventoryRetainsRangeAndLocalReadyDeadlineInDetachedSnapshot()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        void Set(string name,object value)
        {var property=schemas.Ship.Properties.Single(p=>p.Path=="ShipAuxData.Inventory.Equipped.0."+name);property.Value=value;property.State=0;}
        Set("ItemTemplateID",101);Set("StackCount",1);Set("TargetRange",1660f);Set("ReadyTime",new Net7AuxDataTime(5000));
        Set("Effects.Range",1000f);Set("Effects.Usage",BitConverter.GetBytes(1u));Set("Effects.Targets",BitConverter.GetBytes(4u));Set("Effects.Validity",BitConverter.GetBytes(0u));
        var snapshot=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null);
        var item=Assert.Single(snapshot.Equipped);
        Assert.Equal(1660f,item.TargetRange);Assert.Equal(5000L,item.ReadyTime);
        Assert.Equal(1000f,item.EffectRange);Assert.Equal(1u,item.EffectUsage);Assert.Equal(4u,item.EffectTargets);Assert.Equal(0u,item.EffectValidity);
        Set("TargetRange",2200f);Set("ReadyTime",new Net7AuxDataTime(0));
        Assert.Equal(1660f,item.TargetRange);Assert.Equal(5000L,item.ReadyTime);
    }
    [Fact]
    public void EquipmentEffectRangeUsesEffectFlagsAndKeepsPlayerCueSilent()
    {
        EnbRetailWeaponFeedback At(float? d,uint usage=1,uint targets=4,uint? validity=0,uint state=0)=>
            EnbRetailWeaponFeedback.EvaluateEffect(state,d,1000,usage,targets,validity,5000,1000,false,0,100);
        Assert.Equal(7,At(1500).Frame);Assert.Equal(15,At(500).Frame);Assert.Equal(0,At(500).Cue);
        Assert.Equal(-1,At(null).Frame);Assert.Equal(-1,At(500,usage:2).Frame);
        Assert.Equal(-1,At(500,targets:0x40).Frame);Assert.Equal(-1,At(500,validity:0x100).Frame);
        Assert.Equal(-1,At(500,validity:null).Frame);
        Assert.Equal(15,At(500,state:0x37).Frame);Assert.True(At(500,state:1).Active);Assert.True(At(500).Cooling);
    }
    [Fact]
    public void RangeAtlasBoundariesAndServerActiveStateAreIndependent()
    {
        EnbRetailWeaponFeedback At(float? distance,uint state=0,long ready=0)=>
            EnbRetailWeaponFeedback.Evaluate(state,distance,1000,ready,1000,true,0,100);
        Assert.Equal(-1,At(null).Frame);
        Assert.Equal(0,At(2001).Frame);
        Assert.Equal(0,At(2000).Frame);
        Assert.Equal(7,At(1500).Frame);
        Assert.Equal(15,At(1000).Frame);
        Assert.Equal(14,At(1000,0x800).Frame);
        Assert.Equal(-1,At(500,0x20).Frame);
        Assert.True(At(2200,1).Active); // Primed while out of range remains server-owned.
        Assert.False(At(500).Active);
        Assert.True(At(500,1,1001).Cooling);
        Assert.False(At(500,1,1000).Cooling);
    }
    [Fact]
    public void RotationAndCueResetFollowRetailBranches()
    {
        var green=EnbRetailWeaponFeedback.Evaluate(0,500,1000,0,0,true,0,100);
        Assert.Equal(2,green.Cue);Assert.Equal(.1f,green.Angle,5);
        var red=EnbRetailWeaponFeedback.Evaluate(0,1500,1000,0,0,true,green.Angle,100);
        Assert.Equal(1,red.Cue);Assert.Equal(.08f,red.Angle,5);
        Assert.Equal(0,EnbRetailWeaponFeedback.Evaluate(0,2500,1000,0,0,true,0,0).Cue);
        Assert.Equal(0,EnbRetailWeaponFeedback.Evaluate(0,500,1000,0,0,false,0,0).Cue);
        Assert.Equal(-1,EnbRetailWeaponFeedback.Evaluate(0,500,float.NaN,0,0,true,0,0).Frame);
        Assert.InRange(EnbRetailWeaponFeedback.Evaluate(0,0,1000,0,0,true,0,10000).Angle,0,MathF.PI/2);
    }
}
