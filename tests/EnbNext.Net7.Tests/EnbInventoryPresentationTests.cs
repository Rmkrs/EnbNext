using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbInventoryPresentationTests
{
    [Fact]
    public void CargoAndVaultKeysRetainEmptySlotsAndExcludeUnavailableSlots()
    {
        var s=new Net7AuxDataSchemaCatalog();
        void Set(Net7AuxDataSchema schema,string path,int value)
        {var p=schema.Properties.Single(p=>p.Path==schema.Name+"."+path);p.Value=value;p.State=2;}
        Set(s.Ship,"Inventory.Cargo.0.ItemTemplateID",-1);Set(s.Ship,"Inventory.Cargo.1.ItemTemplateID",-2);
        Set(s.Ship,"Inventory.Cargo.3.ItemTemplateID",123);Set(s.Ship,"Inventory.Cargo.3.StackCount",0);
        Set(s.LocalPlayer,"SecureInventory.0.ItemTemplateID",-2);Set(s.LocalPlayer,"SecureInventory.1.ItemTemplateID",-1);
        Set(s.LocalPlayer,"SecureInventory.3.ItemTemplateID",456);
        var before=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,null,null);
        Assert.Equal(new[]{0,2,3},before.CargoSlotKeys.Take(3));
        Assert.Equal(new[]{1,2,3},before.VaultSlotKeys.Take(3));
        Assert.Null(before.Find(1,0));Assert.Equal(0,before.Find(1,3)!.Count);
        Set(s.Ship,"Inventory.Cargo.0.ItemTemplateID",-2);Set(s.LocalPlayer,"SecureInventory.1.ItemTemplateID",456);
        var after=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,null,null);
        Assert.Equal(0,before.CargoSlotKeys[0]);Assert.Equal(2,after.CargoSlotKeys[0]);
        Assert.Equal(before.VaultSlotKeys.ToArray(),after.VaultSlotKeys.ToArray());Assert.Equal(456,after.Find(3,1)!.TemplateId);
    }
    [Fact]
    public void SkillPointsReceiptSurvivesProjectionWithoutRequiringValueChange()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        var points=schemas.LocalPlayer.Properties.Single(p=>p.Path=="LocalPlayerAuxData.RPGInfo.SkillPoints");
        points.Value=8;points.State=2;points.UpdateRevision=12;
        var before=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null);
        points.UpdateRevision++;
        var after=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null);
        Assert.Equal(before.CharacterStats["SkillPoints"],after.CharacterStats["SkillPoints"]);
        Assert.Equal(12ul,before.SkillPointsRevision);Assert.Equal(13ul,after.SkillPointsRevision);
    }

    [Theory]
    [InlineData(0f,"no longer operational")]
    [InlineData(.5f,"severely damaged")]
    [InlineData(.5001f,"taken damage")]
    [InlineData(1f,"functioning perfectly")]
    public void StructureHoverHelpFollowsRetailConditionBoundaries(float structure,string expected)
    {
        var item=new EnbSceneItemSnapshot(1,0,1,"Item",10,122,2,8,0,1,structure,1,null,"","",[],[],0);
        Assert.Contains(expected,EnbItemInformation.AttributeTooltip(item,"Structure"));
        Assert.Equal("",EnbItemInformation.AttributeTooltip(item,"Unknown"));
        Assert.Contains("shield energy",EnbItemInformation.AttributeTooltip(item with {Attributes=[new(26,"10")]},"Recharge"));
        Assert.DoesNotContain("shield",EnbItemInformation.AttributeTooltip(item with {Attributes=[new(20,"10")]},"Recharge"));
    }
    private static Net7ItemTemplate Template()
    {
        var empty=new Net7ItemTemplateVector([],null);
        return new(123,10,101,16,new([
            new(2,Net7ItemTemplateAttributeType.Int,1,null),
            new(21,Net7ItemTemplateAttributeType.Float,BitConverter.SingleToUInt32Bits(2.6f),null),
            new(22,Net7ItemTemplateAttributeType.Int,3,null)],empty,empty),
            1,2,8,100,1,0,4,new(Encoding.Latin1.GetBytes("Test weapon")),new([]),new([]));
    }
    private static void Set(Net7AuxDataSchema schema,string path,object value)
    {
        var p=schema.Properties.Single(p=>p.Path==schema.Name+"."+path);
        p.Value=value is string s?new Net7AuxDataString(Encoding.Latin1.GetBytes(s)):value;p.State=2;
    }
    [Fact]
    public void SnapshotCopiesRetainedAuxStringsStateAndInstanceOverridesWithoutAliasing()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var ship=schemas.Ship;var player=schemas.LocalPlayer;
        var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(ship,"Rank","Consul");Set(ship,"Title","A different title");Set(ship,"Owner","Pilot");Set(ship,"Inventory.CargoSpace",30);Set(player,"Money",123456ul);
        Set(ship,"Inventory.Equipped.4.ItemTemplateID",123);Set(ship,"Inventory.Equipped.4.StackCount",1);
        Set(ship,"Inventory.Equipped.4.Quality",2f);Set(ship,"Inventory.Equipped.4.Structure",1f);
        Set(ship,"Inventory.Equipped.4.InstanceInfo","21:1.6^22:0");
        Set(ship,"Inventory.Equipped.4.BuilderName","Builder");
        byte[] state=[0,32,0,0];Set(ship,"Inventory.Equipped.4.ItemState",state);
        var snapshot=EnbSceneInventorySnapshotBuilder.Build(ship,player,catalog,null);
        var item=Assert.Single(snapshot.Equipped);
        Assert.Equal("Consul",snapshot.CharacterStats["Rank"]);Assert.Equal("Pilot",snapshot.CharacterStats["Name"]);Assert.Equal(123456ul,snapshot.Credits);
        Assert.Equal(4,item.Slot);Assert.Equal("Builder",item.Builder);Assert.True(item.AutoFire);Assert.True(item.HasAutoFire);
        Assert.Contains(("Reload Time","1.6 seconds"),EnbItemInformation.Attributes(item));
        Assert.Contains(("Rounds Fired","3 rounds"),EnbItemInformation.Attributes(item));
        Assert.Contains("Quality 200%",item.Tooltip);Assert.Null(snapshot.Find(3,4));
        state[1]=0;Set(ship,"Inventory.Equipped.4.BuilderName","Changed");
        Assert.True(item.AutoFire);Assert.Equal("Builder",item.Builder);
        Assert.False(EnbSceneInventorySnapshotBuilder.Build(ship,player,catalog,null).Equipped[0].AutoFire);
    }
    [Fact]
    public void SparseCargoAndMissingTemplatesKeepTheirSlotIdentity()
    {
        var s=new Net7AuxDataSchemaCatalog();
        Set(s.Ship,"Inventory.Cargo.13.ItemTemplateID",888);Set(s.Ship,"Inventory.Cargo.13.StackCount",7);
        Set(s.Ship,"Inventory.Cargo.2.ItemTemplateID",999);Set(s.Ship,"Inventory.Cargo.2.StackCount",0);
        var data=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,null,null);
        Assert.Equal(2,data.Cargo.Length);Assert.Equal(0,data.Find(1,2)!.Count);
        var item=data.Find(1,13)!;Assert.Equal(13,item.Slot);Assert.Equal(7,item.Count);
        Assert.Null(data.Find(1,0));Assert.Same(item,data.Find(1,13));Assert.False(item.HasAutoFire);Assert.Null(item.State);
        Assert.Equal("Null Item",item.Name);Assert.Equal(-1,item.IconBaseAsset);
        Assert.Equal(uint.MaxValue,item.Category);Assert.Equal(uint.MaxValue,item.Subcategory);Assert.Equal(uint.MaxValue,item.ItemType);Assert.Equal(1u,item.MaxStack);
    }
    [Fact]
    public void RealTemplateReplacesNullPresentationWithoutChangingSlotIdentity()
    {
        var s=new Net7AuxDataSchemaCatalog();var catalog=new Net7ItemTemplateCatalog();
        Set(s.Ship,"Inventory.Cargo.3.ItemTemplateID",123);
        var before=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,catalog,null).Cargo.Single();
        Assert.Equal("Null Item",before.Name);Assert.Equal(0,catalog.Count);
        catalog.Materialize(Template());
        var after=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,catalog,null).Cargo.Single();
        Assert.Equal("Test weapon",after.Name);Assert.Equal(before.TemplateId,after.TemplateId);Assert.Equal(before.Slot,after.Slot);Assert.Equal(before.Count,after.Count);
        Assert.Equal("Null Item",before.Name);Assert.Equal(1,catalog.Count);
    }
    [Theory]
    [InlineData("Cargo",1)] [InlineData("Equipped",2)] [InlineData("Ammo",2)]
    public void TemplateKeepsItemVisibleBeforeAndAfterQuantityArrives(string collection,byte inventory)
    {
        var s=new Net7AuxDataSchemaCatalog();var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(s.Ship,$"Inventory.{collection}.0.ItemTemplateID",123);
        EnbSceneItemSnapshot Read()
        {
            var snapshot=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,catalog,null);
            return Assert.Single(collection=="Ammo"?snapshot.Ammo:inventory==1?snapshot.Cargo:snapshot.Equipped);
        }
        Assert.Equal(0,Read().Count);Assert.Equal("Test weapon",Read().Name);
        foreach(int count in new[]{7,0,-1,1})
        {Set(s.Ship,$"Inventory.{collection}.0.StackCount",count);Assert.Equal(count,Read().Count);}
        Set(s.Ship,$"Inventory.{collection}.0.ItemTemplateID",-1);
        var empty=EnbSceneInventorySnapshotBuilder.Build(s.Ship,s.LocalPlayer,catalog,null);
        Assert.Empty(collection=="Ammo"?empty.Ammo:inventory==1?empty.Cargo:empty.Equipped);
    }
    [Fact]
    public void TerminalItemsKeepInstanceAttributesAndBuilderInformation()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(900,1f,0,0x27,0,0,0));
        var lab=world.Objects.FindFirst(900)!.AuxData!;
        Set(lab,"ManufactureTarget.0.ItemTemplateID",123);Set(lab,"ManufactureTarget.0.StackCount",1);
        Set(lab,"ManufactureTarget.0.InstanceInfo","21:1.6^22:0");Set(lab,"ManufactureTarget.0.BuilderName","Builder");
        var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        var domain=new Net7ManufacturingDomain(id=>world.Objects.FindFirst(id));domain.SetActiveObject(900);
        var item=Assert.Single(EnbStationTerminalSnapshot.Build(domain,catalog).Items);
        Assert.Equal((byte)12,item.Inventory);Assert.Equal("Builder",item.Builder);
        Assert.Contains(("Reload Time","1.6 seconds"),EnbItemInformation.Attributes(item));
        Assert.Contains(("Rounds Fired","3 rounds"),EnbItemInformation.Attributes(item));
    }
    [Theory]
    [InlineData("ManufactureTarget",12)] [InlineData("ManufactureComponents",13)] [InlineData("ManufactureOverride",14)]
    public void TerminalItemIdentityIsIndependentOfQuantity(string collection,byte inventory)
    {
        var world=new Net7WorldState();
        var lab=world.MaterializeCreate(new Net7CreateUpdate(900,1f,0,0x27,0,0,0)).AuxData!;
        var domain=new Net7ManufacturingDomain(id=>world.Objects.FindFirst(id));domain.SetActiveObject(900);
        Set(lab,collection+".0.ItemTemplateID",123);
        foreach(int count in new[]{0,3,0,-1})
        {
            Set(lab,collection+".0.StackCount",count);
            var item=Assert.Single(EnbStationTerminalSnapshot.Build(domain,null).Items);
            Assert.Equal(inventory,item.Inventory);Assert.Equal(count,item.Count);
        }
        Set(lab,collection+".0.ItemTemplateID",-1);
        Assert.Empty(EnbStationTerminalSnapshot.Build(domain,null).Items);
    }
    [Theory]
    [InlineData(14,100,1)] [InlineData(15,102,2)] [InlineData(16,101,3)] [InlineData(10,103,4)]
    [InlineData(2,122,5)] [InlineData(7,120,6)] [InlineData(6,121,7)] [InlineData(11,110,8)]
    public void FilterUsesRuntimeItemTypeAndSubtitleUsesSubcategory(uint type,uint subcategory,int filter)
    {
        var item=new EnbSceneItemSnapshot(1,0,1,"Item",10,subcategory,type,8,0,1,null,null,null,"","",[],[],0);
        Assert.True(EnbItemInformation.Matches(item,filter));Assert.True(EnbItemInformation.Matches(item,0));
        Assert.DoesNotContain(Enumerable.Range(1,11).Where(i=>i!=filter),i=>EnbItemInformation.Matches(item,i));
        Assert.NotEqual("Item",EnbItemInformation.TypeName(subcategory));
    }
    [Fact]
    public void GeneralItemFiltersRetainCurrentRetailFlagsAndCategoryDistinction()
    {
        var item=new EnbSceneItemSnapshot(1,0,1,"Item",80,200,13,1,0,1,null,null,null,"","",[],[],0x80);
        Assert.True(EnbItemInformation.Matches(item,10));Assert.False(EnbItemInformation.Matches(item,9));
        Assert.True(EnbItemInformation.Matches(item with {Flags=0x88},11));
        Assert.True(EnbItemInformation.Matches(item with {Category=81},9));
    }

    [Fact]
    public void InventorySnapshotCarriesBaseStatsAndMissionStagesWithWireBooleanBytes()
    {
        var schemas=new Net7AuxDataSchemaCatalog();
        Set(schemas.Ship,"BaseStats.Speed",400);
        Set(schemas.Ship,"CurrentStats.Speed",525);
        Set(schemas.LocalPlayer,"Reputation.Affiliation","PW");
        Set(schemas.LocalPlayer,"Missions.4.ID",123);
        Set(schemas.LocalPlayer,"Missions.4.Name","Mission");
        Set(schemas.LocalPlayer,"Missions.4.Summary","Summary");
        Set(schemas.LocalPlayer,"Missions.4.Reward","Reward description");
        Set(schemas.LocalPlayer,"Missions.4.Stage",2);
        Set(schemas.LocalPlayer,"Missions.4.StageCount",2);
        Set(schemas.LocalPlayer,"Missions.4.Stages.0.Text","First step");
        Set(schemas.LocalPlayer,"Missions.4.Stages.1.Text","Second step");
        Set(schemas.LocalPlayer,"Missions.4.IsForfeitable",(byte)1);
        var snapshot=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null);
        Assert.Equal("400",snapshot.ShipStats["Base.Speed"]);
        Assert.Equal("525",snapshot.ShipStats["Speed"]);
        Assert.Equal("PW",snapshot.CharacterStats["Affiliation"]);
        var mission=Assert.Single(snapshot.Missions);
        Assert.Equal(4,mission.Slot);Assert.Equal(123,mission.Id);
        Assert.Equal("(2/2) Second step",mission.CurrentStep);
        Assert.Contains("1. First step\n2. Second step",mission.Description);
        Assert.DoesNotContain("20. ",mission.Description);
        Assert.Equal("Reward: Reward description\n\nSummary\n\nMission Steps:\n1. First step\n2. Second step\n",mission.Description);
        Assert.True(mission.CanForfeit);
        Set(schemas.LocalPlayer,"Missions.4.IsComplete",(byte)1);
        Assert.False(Assert.Single(EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,null,null).Missions).CanForfeit);
        Assert.True(mission.CanForfeit);
    }

    [Theory]
    [InlineData(0,0,1,8,true)]
    [InlineData(3,0,1,8,false)]
    [InlineData(0,1,1,8,false)]
    [InlineData(0,0,8,8,false)]
    public void CharacterUpgradePresentationUsesRetailQuestThreshold(int raw0,int raw1,int level,int questThreshold,bool expected)
    {
        var skill=new EnbCharacterSkillSnapshot(1,"Skill","",level,9,"")
            {QuestOnlyLevels=questThreshold,Availability=new(raw0,raw1,140,8)};
        Assert.Equal(expected,skill.CanTrain);
        Assert.False((skill with {Availability=null}).CanTrain);
    }
}
