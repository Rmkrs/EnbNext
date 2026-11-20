using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7ManufacturingSessionPolicyTests
{
    [Fact]
    public async Task InvalidOrSentinelManufacturingIntentFailsBeforeAnyTransportIsRequired()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });

        Assert.False(await session.CloseManufacturingTerminalAsync());
        Assert.False(await session.SelectManufactureItemAsync(uint.MaxValue));
        Assert.False(await session.SelectRefineryItemAsync(uint.MaxValue));
        Assert.False(await session.SetManufacturingTechFilterAsync(true, 0));
        Assert.False(await session.SetManufacturingTechFilterAsync(true, 0x003));
        Assert.False(await session.SetManufacturingTechFilterAsync(true, 0x200));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.SetManufacturingModeAsync((Net7ManufacturingMode)3));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.SetManufacturingModeAsync((Net7ManufacturingMode)5));
    }
}
