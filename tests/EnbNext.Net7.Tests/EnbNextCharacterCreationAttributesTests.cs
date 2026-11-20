using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextCharacterCreationAttributesTests
{
    [Theory]
    [InlineData(0, 2, 2, 2, 3, 5, 4, 5, 2)]
    [InlineData(1, 1, 2, 2, 5, 3, 3, 3, 4)]
    [InlineData(2, 1, 4, 4, 4, 1, 3, 3, 2)]
    public void MissingRetailProfessionHasNextOwnedRatings(
        uint race,
        uint profession,
        params int[] expected)
    {
        Assert.True(EnbNextCharacterCreationAttributes.TryGetRatings(race,profession,out var ratings));
        Assert.Equal(expected,ratings.ToArray());
        Assert.All(ratings.ToArray(),value=>Assert.InRange(value,1,5));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    public void RetailProfessionDoesNotHaveNextOwnedRatings(uint race, uint profession)
    {
        Assert.False(EnbNextCharacterCreationAttributes.TryGetRatings(race,profession,out var ratings));
        Assert.True(ratings.IsEmpty);
    }
}
