using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7GameplayProvenanceTests
{

    public static TheoryData<ushort> ActiveOutboundGameplayOpcodes => new()
    {
        0x0012, 0x0013, 0x0014, 0x0015, 0x0017, 0x0018, 0x001A, 0x001D,
        0x0027, 0x0028, 0x0029, 0x002C, 0x002D, 0x002E, 0x0033, 0x004E,
        0x0051, 0x0055, 0x0057, 0x0058, 0x005A, 0x005D, 0x005E, 0x0065,
        0x0068, 0x0079, 0x007A, 0x007B, 0x007C, 0x007E, 0x0080, 0x0082,
        0x0084, 0x0086, 0x0087, 0x0088, 0x008D, 0x008E, 0x008F, 0x0098,
        0x009B, 0x009D, 0x009F, 0x00A1, 0x00A3, 0x00B9, 0x00BC, 0x00C0,
        0x00C5, 0x00C9, 0x00CD, 0x00D4, 0x00E8, 0x00EC, 0x00ED,
    };

    [Fact]
    public void IntegratedOutboundUniverseIsExactlyFiftyEightFamilies()
    {
        ushort[] integrated =
        [
            0x0002, 0x0006, 0x0044,
            0x0012, 0x0013, 0x0014, 0x0015, 0x0017, 0x0018, 0x001A, 0x001D,
            0x0027, 0x0028, 0x0029, 0x002C, 0x002D, 0x002E, 0x0033, 0x004E,
            0x0051, 0x0055, 0x0057, 0x0058, 0x005A, 0x005D, 0x005E, 0x0065,
            0x0068, 0x0079, 0x007A, 0x007B, 0x007C, 0x007E, 0x0080, 0x0082,
            0x0084, 0x0086, 0x0087, 0x0088, 0x008D, 0x008E, 0x008F, 0x0098,
            0x009B, 0x009D, 0x009F, 0x00A1, 0x00A3, 0x00B9, 0x00BC, 0x00C0,
            0x00C5, 0x00C9, 0x00CD, 0x00D4, 0x00E8, 0x00EC, 0x00ED,
        ];
        Assert.Equal(58, integrated.Length);
        Assert.Equal(58, integrated.Distinct().Count());
        foreach (ushort compatibilityOnly in new ushort[] { 0x0000, 0x0035, 0x006D, 0x006E, 0x0071, 0x0072, 0x0077 })
            Assert.DoesNotContain(compatibilityOnly, integrated);
    }

    [Theory]
    [MemberData(nameof(ActiveOutboundGameplayOpcodes))]
    public void EveryActiveNativeGameplaySendHasStaticClosedOwner(ushort opcode)
    {
        Assert.True(Net7GameplayProvenance.IsImplemented(opcode));
        Assert.NotEmpty(Net7GameplayProvenance.GetOwners(opcode));
        Net7GameplayProvenance.RequireImplemented(opcode);
    }

    [Theory]
    [InlineData(0x1004)]
    [InlineData(0x2012)]
    [InlineData(0x2013)]
    [InlineData(0x2014)]
    [InlineData(0x2025)]
    [InlineData(0x202A)]
    public void ProxyOwnedGameplayExtensionsHaveStaticClosedOwners(int opcode)
    {
        ushort value = checked((ushort)opcode);
        Net7GameplayProvenance.RequireImplemented(value);
        Assert.NotEmpty(Net7GameplayProvenance.GetOwners(value));
    }
    [Theory]
    [InlineData(0x0012, "MOVE-01")]
    [InlineData(0x0027, "INV-04")]
    [InlineData(0x0086, "MISSION-02")]
    [InlineData(0x0079, "MFG-01")]
    [InlineData(0x00C5, "SOCIAL-06")]
    [InlineData(0x2012, "COMBAT-05")]
    [InlineData(0x2013, "COMBAT-06")]
    [InlineData(0x2014, "COMBAT-07")]
    [InlineData(0x2025, "ASSET-01")]
    [InlineData(0x202A, "MOVE-06")]
    public void ImplementedGameplayOpcodesHaveStaticClosedOwners(int opcode, string expectedOwner)
    {
        IReadOnlyList<string> owners = Net7GameplayProvenance.GetOwners(checked((ushort)opcode));
        Assert.Contains(expectedOwner, owners);
        Assert.True(Net7GameplayProvenance.IsImplemented(checked((ushort)opcode)));
    }

    [Fact]
    public void UnknownOpcodeHasNoInventedOwner()
    {
        Assert.Empty(Net7GameplayProvenance.GetOwners(0xFFFF));
        Assert.False(Net7GameplayProvenance.IsImplemented(0xFFFF));
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => Net7GameplayProvenance.RequireImplemented(0xFFFF));
        Assert.Contains("0xFFFF", error.Message, StringComparison.Ordinal);
    }
}
