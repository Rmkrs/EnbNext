using EnbNext.Presentation;
using EnbNext.Net7.World;
namespace EnbNext.Net7.Tests;
public sealed class EnbFactionParityTests
{
    [Theory]
    [InlineData(0,.05f,.05f)]
    [InlineData(250,.075f,.025f)]
    [InlineData(-250,.025f,.075f)]
    [InlineData(500,.1f,0)]
    [InlineData(-500,0,.1f)]
    [InlineData(999,.1499f,0)]
    [InlineData(-999,0,.1499f)]
    [InlineData(1000,.15f,0)]
    [InlineData(1001,.1001f,0)]
    [InlineData(-1000,0,.15f)]
    [InlineData(-1001,0,.1001f)]
    [InlineData(12000,1,0)]
    [InlineData(-12000,0,1)]
    public void NativeNeutralWindowAndGaugeClamp(float reaction,float love,float hate)
    {
        var actual=new EnbCharacterFactionSnapshot("Test",reaction).GaugeFractions;
        Assert.Equal(love,actual.Love,5);Assert.Equal(hate,actual.Hate,5);
    }
    [Fact]
    public void CatalogUsesSectionKeyOnlyForMissingAuthoredFields()
    {
        string path=Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,"[Key]\nOther=value\n[Blank]\nName=\nDescription=");
            var catalog=EnbNext.Assets.EnbFactionCatalog.Load(path);
            Assert.True(catalog.TryResolve("Key",out var missing));Assert.Equal("Key",missing.Name);Assert.Equal("Key",missing.Description);
            Assert.True(catalog.TryResolve("Blank",out var blank));Assert.Equal("",blank.Name);Assert.Equal("",blank.Description);
            Assert.False(catalog.TryResolve("key",out _));
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public void FactionOrderScansNativeRangeAndFirstMatchingSlot()
    {
        var schema=new Net7AuxDataSchemaCatalog();
        void Set(int slot,string field,object value){var p=schema.LocalPlayer.Properties.Single(p=>p.Path==$"LocalPlayerAuxData.Reputation.Faction.{slot}.{field}");p.Value=value is string s?new Net7AuxDataString(System.Text.Encoding.Latin1.GetBytes(s)):value;p.State=2;}
        Set(0,"Order",4);Set(0,"Name","four");Set(1,"Order",2);Set(1,"Name","two");
        Set(2,"Order",4);Set(2,"Name","duplicate");Set(3,"Order",22);Set(3,"Name","out of range");
        Set(4,"Order",-1);Set(4,"Name","negative");
        var factions=EnbSceneInventorySnapshotBuilder.Build(schema.Ship,schema.LocalPlayer,null,null).Factions;
        Assert.Equal(new[]{"two","four"},factions.Select(f=>f.Name));
    }
}
