using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationNpcSlotsTests
{
    [Fact]
    public void RoomConstructionUsesFixedSlotsAndFirstDefinitionWithoutChangingWireData()
    {
        var definition=new EnbSceneStationDefinition(2,[])
        {
            Npcs=[new(0,13,1,0,3,"last",null!),new(0,2,2,8,255,"first",null!),
                new(1,0,3,0,1,"other room",null!),new(0,2,4,7,1,"duplicate",null!),
                new(0,14,5,0,0,"outside",null!),new(0,0,6,8,0,"zero",null!)]
        };
        var actors=definition.RoomNpcs(0).ToArray();
        Assert.Equal(new uint[]{0,2,13},actors.Select(a=>a.Slot));
        Assert.Equal("first",actors[1].Name);
        Assert.Equal(0u,actors[1].Variant);
        Assert.Equal(3u,actors[2].Variant);
        Assert.Equal(255u,definition.Npcs[1].Variant);
        Assert.Single(definition.RoomNpcs(1));
    }
    [Theory]
    [InlineData(0u,0u)] [InlineData(1u,1u)] [InlineData(2u,2u)]
    [InlineData(3u,3u)] [InlineData(4u,0u)] [InlineData(uint.MaxValue,0u)]
    public void VariantSwitchMatchesDefinitionConstructor(uint wire,uint expected)
    {
        var definition=new EnbSceneStationDefinition(0,[]){Npcs=[new(0,0,0,8,wire,"NPC",null!)]};
        Assert.Equal(expected,definition.RoomNpcs(0).Single().Variant);
    }
}
