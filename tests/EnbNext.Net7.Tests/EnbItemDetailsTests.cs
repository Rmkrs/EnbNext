using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbItemDetailsTests
{
    private static Net7ItemTemplateSharedString Text(string s)=>new(Encoding.Latin1.GetBytes(s));
    private static Net7ItemTemplateVectorEntry Effect(string name,string help,float[] names,float[] descriptions)=>
        new(Text("id"),Text(name),Text(help),names.Select(BitConverter.SingleToUInt32Bits).ToArray(),
            descriptions.Select(BitConverter.SingleToUInt32Bits).ToArray(),0,0);
    private static Net7ItemTemplate Template()=>new(123,10,102,15,new([],new([
        Effect("Power %value0.0f% (Activate)","Adds %value0.1f% power for %value0.0f% seconds.",[1],[2,3])],null),new([
        Effect("Targeting (Equip)","Improves targeting by %value0.1f%%.",[],[4]),
        Effect("Second (Equip)","Adds %value0.0f% units.",[],[5])],null)),1,2,3,100,1,0,0,Text("Example"),Text(""),Text(""));
    private static void Set(Net7AuxDataSchema schema,string field,object value)
    {
        var property=schema.Properties.Single(p=>p.Path==schema.Name+".Inventory.Cargo.0."+field);
        property.Value=value is string s?new Net7AuxDataString(Encoding.Latin1.GetBytes(s)):value;property.State=2;
    }
    [Theory]
    [InlineData("5:12junk", "12", "2")]
    [InlineData("5:12.9", "12", "2")]
    [InlineData("5suffix:1e3", "1", "2")]
    [InlineData("5:4294967297", "1", "2")]
    [InlineData("5:-4294967297", "-1", "2")]
    [InlineData("5:9^5:0^5:bad", "9", "2")]
    [InlineData("9:0.00001", "10", "2")]
    [InlineData("9:0.0001", "10", "0.0001")]
    [InlineData("9:-0.0001", "10", "-0.0001")]
    [InlineData("9:1d2tail", "10", "100")]
    [InlineData("9:3e+", "10", "3")]
    [InlineData("9:1e999", "10", "Infinity")]
    public void InstanceNumericUpdatesUseNativePrefixesAndZeroSuppression(string delta, string damage, string energy)
    {
        var template = Template();
        template = template with { Info = template.Info with { Attributes = [
            new(5, Net7ItemTemplateAttributeType.Int, 10, null),
            new(9, Net7ItemTemplateAttributeType.Float, BitConverter.SingleToUInt32Bits(2), null)] } };
        var schemas = new Net7AuxDataSchemaCatalog();
        var catalog = new Net7ItemTemplateCatalog(); catalog.Materialize(template);
        Set(schemas.Ship, "ItemTemplateID", 123); Set(schemas.Ship, "InstanceInfo", delta);
        var item = Assert.Single(EnbSceneInventorySnapshotBuilder.Build(schemas.Ship, schemas.LocalPlayer, catalog, null).Cargo);
        Assert.Equal(damage, item.Attributes.Single(a => a.Id == 5).Value);
        Assert.Equal(energy, item.Attributes.Single(a => a.Id == 9).Value);
    }
    [Fact]
    public void RepeatedTemplateFieldsRemainDisplayableAndUseRetailLookupValues()
    {
        var template = Template();
        template = template with { Info = template.Info with { VectorA = new([], null), VectorB = new([], null), Attributes = [
            new(5, Net7ItemTemplateAttributeType.Int, 10, null),
            new(1, Net7ItemTemplateAttributeType.String, 0, Encoding.Latin1.GetBytes("First\0")),
            new(5, Net7ItemTemplateAttributeType.Int, 20, null),
            new(1, Net7ItemTemplateAttributeType.String, 0, Encoding.Latin1.GetBytes("Second\0"))] } };
        var schemas = new Net7AuxDataSchemaCatalog();
        var catalog = new Net7ItemTemplateCatalog();
        // Exercise the incoming definition codec and its presentation consumer together.
        catalog.Materialize(Net7ItemTemplateWire.Decode(Net7ItemTemplateWire.Encode(template)));
        Set(schemas.Ship, "ItemTemplateID", 123);
        var item = Assert.Single(EnbSceneInventorySnapshotBuilder.Build(schemas.Ship, schemas.LocalPlayer, catalog, null).Cargo);
        Assert.Equal(new[] { "20", "First", "20", "Second" }, item.Attributes.Select(a => a.Value));
        var rows = EnbItemInformation.Attributes(item);
        Assert.Equal(new[] { "20 units", "20 units" }, rows.Where(r => r.Name == "Damage").Select(r => r.Value));
        Assert.Equal(new[] { "First", "Second" }, rows.Where(r => r.Name == "Ammo").Select(r => r.Value));
    }
    [Fact]
    public void ItemDetailsRetainBothEffectDescriptionsAndRefreshMutableInstanceValues()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(schemas.Ship,"ItemTemplateID",123);Set(schemas.Ship,"StackCount",1);
        var owner=new EnbInventoryPublicationOwner();
        EnbSceneItemSnapshot Item()=>Assert.Single(EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null,owner:owner).Cargo);
        var original=Item();
        Assert.Equal(new EnbItemSpecial("Effect: Power 1 (Activate)","Adds 2.0 power for 3 seconds."),original.Effects[0]);
        Set(schemas.Ship,"InstanceActivatedEffectInfo","7^8.5,12");
        byte[] bytes=Encoding.Latin1.GetBytes("^6.5#^9");
        Set(schemas.Ship,"InstanceEquipEffectInfo",new Net7AuxDataString(bytes));
        var changed=Item();
        Assert.Equal(new EnbItemSpecial("Effect: Power 7 (Activate)","Adds 8.5 power for 12 seconds."),changed.Effects[0]);
        Assert.Equal("Improves targeting by 6.5%.",changed.Effects[1].Tooltip);
        Assert.Equal("Adds 9 units.",changed.Effects[2].Tooltip);
        Assert.Same(changed,Item());
        bytes[1]=(byte)'8';var mutated=Item();
        Assert.Equal("Improves targeting by 8.5%.",mutated.Effects[1].Tooltip);
        Assert.Equal("Improves targeting by 6.5%.",changed.Effects[1].Tooltip);
        Assert.Equal("Improves targeting by 4.0%.",original.Effects[1].Tooltip);
        Assert.Same(mutated,Item());
        Set(schemas.Ship,"InstanceEquipEffectInfo","");
        Assert.Equal(original.Effects[1],Item().Effects[1]);
    }
    [Fact]
    public void SummarySplitAndPartialUpdatesFollowNativeVectorBounds()
    {
        var schemas=new Net7AuxDataSchemaCatalog();var catalog=new Net7ItemTemplateCatalog();catalog.Materialize(Template());
        Set(schemas.Ship,"ItemTemplateID",123);
        Set(schemas.Ship,"InstanceActivatedEffectInfo","9^11");Set(schemas.Ship,"InstanceEquipEffectInfo","^7#invalid#ignored");
        var effects=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null).Cargo[0].Effects;
        Assert.Equal("Adds 11.0 power for 3 seconds.",effects[0].Tooltip);
        Assert.Equal("Improves targeting by 7.0%.",effects[1].Tooltip);
        Assert.Equal("Adds 5 units.",effects[2].Tooltip);
        Set(schemas.Ship,"InstanceActivatedEffectInfo","9^,12,");Set(schemas.Ship,"InstanceEquipEffectInfo","^6junk#^0#");
        effects=EnbSceneInventorySnapshotBuilder.Build(schemas.Ship,schemas.LocalPlayer,catalog,null).Cargo[0].Effects;
        Assert.Equal("Adds 0.0 power for 12 seconds.",effects[0].Tooltip);
        Assert.Equal("Improves targeting by 6.0%.",effects[1].Tooltip);
        Assert.Equal("Adds 0 units.",effects[2].Tooltip);
    }
    [Fact]
    public void SpecialsHaveNativeHelpForRequirementsRestrictionsAndFlags()
    {
        var item=new EnbSceneItemSnapshot(1,0,123,"Example",10,102,15,3,2,1,1,1,null,"","",[
            new(11,"Missile Weapon"),new(27,"Device"),new(28,"2"),new(18,"2"),new(3,"4"),new(4,"10"),new(14,"20")],
            [new("Effect: Targeting (Equip)","Adds 6 points.")],1|2|4|8|16|128);
        var rows=EnbItemInformation.Specials(item);
        Assert.Equal("You must have this skill level to equip this item.",rows[0].Tooltip);
        Assert.Equal("You must have this skill level to activate this item.",rows[1].Tooltip);
        Assert.Contains(new EnbItemSpecial("Jenquai Restricted","May not be equipped on Jenquai ships."),rows);
        Assert.Contains(new EnbItemSpecial("Explorers Restricted","May not be used by Explorers."),rows);
        Assert.Contains(new EnbItemSpecial("Requires Combat level 10","You must have this level of Combat to use this item."),rows);
        Assert.Contains(new EnbItemSpecial("Requires Overall level 20","You must be this level to use."),rows);
        Assert.Contains(item.Effects[0],rows);Assert.All(rows,r=>Assert.NotEmpty(r.Tooltip));
    }
    [Fact]
    public void EachRequirementRowCarriesItsOwnPredicateResult()
    {
        // 00538350: a failed row selects the _UNUSABLE style (INV-D04).
        var item=new EnbSceneItemSnapshot(1,0,123,"Example",10,102,15,3,2,1,1,1,null,"","",[
            new(11,"Missile Weapon"),new(18,"2"),new(4,"10"),new(14,"20")],[],0);
        var state=new EnbItemRequirementState(true,true,
            System.Collections.Immutable.ImmutableDictionary<string,int>.Empty.Add("Missile Weapon",2),
            System.Collections.Immutable.ImmutableDictionary<string,int>.Empty.Add("CombatLevel",12).Add("Race",1));
        var rows=EnbItemInformation.Specials(item,state);
        Assert.True(rows.Single(r=>r.Text.StartsWith("Requires Missile")).Unmet);
        Assert.True(rows.Single(r=>r.Text=="Jenquai Restricted").Unmet);
        Assert.False(rows.Single(r=>r.Text=="Requires Combat level 10").Unmet);
        Assert.False(rows.Single(r=>r.Text=="Requires Overall level 20").Unmet);
        Assert.All(EnbItemInformation.Specials(item),r=>Assert.False(r.Unmet));
    }
    [Fact]
    public void ComparisonShowsEachSharedAttributesChangeAndWhetherItIsBetter()
    {
        EnbSceneItemSnapshot Beam(string damage,string reload,string range)=>new(1,0,123,"Beam",10,102,15,3,2,1,1,1,null,"","",
            [new(5,damage),new(21,reload),new(19,range)],[],0);
        var changes=EnbItemInformation.Compare(Beam("120","2.0","3000"),Beam("100","2.5","3500"));
        Assert.Contains(("Damage","+20",true),changes);
        Assert.Contains(("Reload Time","-0.5",true),changes); // lower is better
        Assert.Contains(("Range","-0.5K",false),changes);
        Assert.Empty(EnbItemInformation.Compare(Beam("100","2.5","3500"),Beam("100","2.5","3500")));
        // Effects compare by name, on the first number of their explanation.
        EnbSceneItemSnapshot Device(params EnbItemSpecial[] effects)=>new(1,0,124,"Device",10,108,15,6,2,1,1,1,null,"","",[],[..effects],0);
        var beam30=new EnbItemSpecial("Effect: Enhance Beam (Equip)","Increases Beam Damage by 30.0% when equipped.");
        var beam19=new EnbItemSpecial("Effect: Enhance Beam (Equip)","Increases Beam Damage by 19.5% when equipped.");
        var missiles=new EnbItemSpecial("Effect: Enhance Missiles (Equip)","Increases Missile Damage by 19.5% when equipped.");
        var recharge=new EnbItemSpecial("Effect: Recharge Reactor (Equip)","+6.4 Reactor Recharge when equipped.");
        var effects=EnbItemInformation.Compare(Device(beam30,recharge),Device(beam19,missiles));
        Assert.Contains(("Enhance Beam (Equip)","+10.5%",true),effects);
        Assert.Contains(("Recharge Reactor (Equip)","+6.4",true),effects);
        Assert.Contains(("Enhance Missiles (Equip)","-19.5%",false),effects);
    }
    [Fact]
    public void ManufacturerRowPrefersThePlayerAndFallsBackToTheCorporation()
    {
        // 00521C10/0052C770 (INV-D48).
        var item=new EnbSceneItemSnapshot(1,0,123,"Example",10,102,15,3,2,1,1,1,null,"","",[],[],0){CorporationBuilder="Example Corp"};
        Assert.Equal(new EnbItemSpecial("Manufactured by: Example Corp","This item was manufactured by a corporation."),EnbItemInformation.Specials(item)[^1]);
        Assert.Equal(new EnbItemSpecial("Manufactured by: Pilot","This item was manufactured by a player."),EnbItemInformation.Specials(item with {Builder="Pilot"})[^1]);
        Assert.Empty(EnbItemInformation.Specials(item with {CorporationBuilder=""}));
    }
}
