using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7InventorySessionPolicyTests
{
    [Fact]
    public async Task UnsupportedSortTargetFailsButConfiguredKeysReachTheSessionGate()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.SortInventoryAsync(2, 5, 4, 1, false));
        // A nondefault key tuple is accepted by retail option lookup; lack of a local player is the remaining gate.
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SortInventoryAsync(1, 5, 4, 8, false));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.SortInventoryAsync((Net7InventorySortTarget)2, Net7InventorySortPreset.Category, false));
    }

    [Fact]
    public async Task RawUiSentinelAndPartnerTradeMovesFailBeforeTransportIsRequired()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        var identity = new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            session.TransferInventoryAsync(identity, 1, 0, 0x0B, 0, 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTransfer(identity, 0x11, 0, 1, 0, 1));
    }
}
