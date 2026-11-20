using System.Reflection;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

public sealed class EnbShipLegoPathParseTests
{
    private static (bool Ok, string[] Path) Parse(string chain)
    {
        object?[] args = [chain, null, null];
        bool ok = (bool)typeof(EnbWorldRenderer).GetMethod("TryParseShipLegoPath", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, args)!;
        return (ok, (string[])args[1]!);
    }

    [Fact]
    public void EmptyChainAttachesNothingAndSplitKeepsInteriorTokens()
    {
        // 843970 -> 4FC2F0 split on '/'; 72B7F0 cannot attach a child to its own root.
        Assert.False(Parse("").Ok);
        Assert.Equal(new[] { "A", "", "B" }, Parse("A//B").Path);
        Assert.Equal(new[] { "A", "B" }, Parse("A/B/").Path);
        Assert.Equal(new[] { "", "A" }, Parse("/A").Path);
    }
}
