using System.Reflection;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class EnbClientCharacterMappingTests
{
    [Fact]
    public void RosterPresentationCarriesTheSelectedShardGalaxyLoad()
    {
        Net7AvatarRoster decoded = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster());
        Net7AvatarSlot occupied = decoded.Slots.First(slot => !slot.IsEmpty);
        var route = new Net7GalaxyRoutingRecord("Sunrise", occupied.Meta.ShardId, 1, 2, 4, 100, 1);
        Net7AvatarRoster roster = decoded with { RoutingRecords = [route] };

        MethodInfo map = typeof(EnbClient).GetMethod("MapCharacters", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("EnbClient.MapCharacters was not found.");
        var characters = Assert.IsAssignableFrom<IReadOnlyList<CharacterSummary>>(map.Invoke(null, [roster]));
        CharacterSummary character = Assert.Single(characters, entry => !entry.IsEmpty);

        Assert.Equal("Sunrise", character.GalaxyName);
        Assert.Equal(4u, character.GalaxyCurrentLoad);
        Assert.Equal(100u, character.GalaxyCapacity);
    }

    [Fact]
    public void Meta06_Login06RosterPresentation_PreservesFullU32WireDomain()
    {
        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster());
        Net7AvatarSlot baseline = roster.Slots[0];
        Net7AvatarSlot slot = baseline with
        {
            Meta = baseline.Meta with
            {
                Sector = uint.MaxValue,
                ShardId = uint.MaxValue,
                SaveCount = uint.MaxValue,
                AdminLevel = uint.MaxValue,
                GmFlag = uint.MaxValue,
                CombatLevel = uint.MaxValue,
                ExploreLevel = uint.MaxValue,
                TradeLevel = uint.MaxValue,
            },
            Avatar = baseline.Avatar with
            {
                Race = uint.MaxValue,
                Profession = uint.MaxValue,
                Gender = uint.MaxValue,
            },
        };

        MethodInfo map = typeof(EnbClient).GetMethod("MapCharacter", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("EnbClient.MapCharacter was not found.");
        CharacterSummary character = Assert.IsType<CharacterSummary>(map.Invoke(null, [slot, 0]));

        Assert.Equal(uint.MaxValue, character.Race);
        Assert.Equal(uint.MaxValue, character.Profession);
        Assert.Equal(uint.MaxValue, character.Gender);
        Assert.Equal(uint.MaxValue, character.CombatLevel);
        Assert.Equal(uint.MaxValue, character.ExploreLevel);
        Assert.Equal(uint.MaxValue, character.TradeLevel);
        Assert.Equal(uint.MaxValue, character.SectorId);
        Assert.Equal(uint.MaxValue, character.ShardId);
        Assert.Equal(uint.MaxValue, character.SaveCount);
        Assert.Equal(uint.MaxValue, character.AdminLevel);
        Assert.Equal(uint.MaxValue, character.GmFlag);
        Assert.Equal(3UL * uint.MaxValue, character.OverallLevel);
        Assert.Equal(string.Empty, character.RaceName);
        Assert.Equal(string.Empty, character.ClassName);
    }
}
