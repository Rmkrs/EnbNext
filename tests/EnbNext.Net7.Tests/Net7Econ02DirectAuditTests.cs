using System.Buffers.Binary;
using System.Reflection;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Econ02DirectAuditTests
{
    private static readonly Net7InventoryTransferIdentity LocalSelection = new(
        0x11223344,
        Net7InventoryTransferIdSource.ClientShip);

    [Fact]
    public void TradeItemPlannerUsesClientShipAndExactInventoryRoles()
    {
        Net7InventoryTransferPlan offer = Net7InventoryDomain.CreateTradeOffer(LocalSelection, 1, 2, 3, 4);
        Net7InventoryTransferPlan retract = Net7InventoryDomain.CreateTradeRetraction(LocalSelection, 3, 4);
        Net7InventoryTransferPlan reorder = Net7InventoryDomain.CreateTradeReorder(LocalSelection, 3, 5, 4);

        Assert.Equal(Convert.FromHexString("112233440000000100000002000000100000000300000004"), Wire(offer));
        Assert.Equal(Convert.FromHexString("11223344000000100000000300000001FFFFFFFF00000004"), Wire(retract));
        Assert.Equal(Convert.FromHexString("112233440000001000000003000000100000000500000004"), Wire(reorder));
        Assert.Equal(Net7InventoryTransferIdSource.ClientShip, offer.Identity.Source);
    }

    [Fact]
    public void TradeItemPlannerRejectsPartnerDisplayAndUnprovenProducerShapes()
    {
        var localPlayer = new Net7InventoryTransferIdentity(1, Net7InventoryTransferIdSource.LocalPlayer);
        Assert.Throws<ArgumentException>(() => Net7InventoryDomain.CreateTradeOffer(localPlayer, 1, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTradeOffer(LocalSelection, 2, 0, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTradeOffer(LocalSelection, 1, 0, 6, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTradeRetraction(LocalSelection, 6, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTradeReorder(LocalSelection, 0, 6, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.CreateTradeOffer(LocalSelection, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateTransfer(LocalSelection, 1, 0, Net7InventoryDomain.PartnerTradeInventory, 0, 1));
    }

    [Fact]
    public void TradeCoordinatorKeepsPartnerSeparateFromLocalCreditEditCache()
    {
        var trade = new Net7TradeDomain();
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0x55667788, 0)));
        Assert.Equal(0x55667788u, trade.ActivePartnerId);
        Assert.Equal(0u, trade.LocalOfferedCredits);

        Assert.Equal(60u, trade.ValidateOfferedCredits(60, 100));
        trade.RecordLocalOfferedCredits(60);
        Assert.Equal(60u, trade.LocalOfferedCredits);
        Assert.Equal(120u, trade.ValidateOfferedCredits(120, 100));
        Assert.Throws<InvalidOperationException>(() => trade.ValidateOfferedCredits(161, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => trade.ValidateOfferedCredits(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => trade.ValidateOfferedCredits((long)uint.MaxValue + 1, ulong.MaxValue));

        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0xDEADBEEF, 2)));
        Assert.Null(trade.ActivePartnerId);
        Assert.Equal(0u, trade.LocalOfferedCredits);
    }

    [Fact]
    public void TradeCoordinatorImplementsOnlyCurrentTypesZeroThroughSix()
    {
        var trade = new Net7TradeDomain();
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0x11223344, 0)));
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0xAAAAAAAA, 3)));
        Assert.True(trade.WaitingForConfirmation);
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0xBBBBBBBB, 4)));
        Assert.False(trade.WaitingForConfirmation);
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0xCCCCCCCC, 5)));
        Assert.True(trade.Confirmed);
        Assert.True(trade.ApplyCoordinator(new Net7TradeState(0xDDDDDDDD, 6)));
        Assert.False(trade.Confirmed);
        Assert.False(trade.ApplyCoordinator(new Net7TradeState(0xEEEEEEEE, 7)));
        Assert.Equal(0x11223344u, trade.ActivePartnerId); // PartnerID is not a nonce/matcher for 3..6/unknown.
    }

    [Fact]
    public async Task TradeRequestDoesNotInventPartnerState()
    {
        // Self-trade is reported by the 00658430 verb before this request
        // (EnbClientTargetVerbTests); the request itself carries no partner state.
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        session.World.SetLocalPlayerId(0x11223344);
        Assert.False(await session.RequestTradeAsync(0x55667788)); // no route; request transmission never authors partner state
        Assert.Null(session.Gameplay.TradeDomain.ActivePartnerId);
    }

    [Fact]
    public async Task NumericInboundTradeActionsCannotMasqueradeAsAcknowledgements()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x002C)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (Net7PlayerAction action in new[]
        {
            Net7PlayerAction.TradeRequest,
            Net7PlayerAction.TradeConfirm,
            Net7PlayerAction.TradeCancel,
            Net7PlayerAction.TradeSync,
            Net7PlayerAction.TradeCredits,
        })
        {
            Task task = (Task)dispatch.Invoke(session,
                [Packet.ForOpcode(0x002C, Net7GameplayWire.BuildAction(1, action, 2, 0)), "decoded-inner", false, CancellationToken.None])!;
            await task;
        }

        Assert.Equal(0, delivered);

        Task nonTrade = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x002C, Net7GameplayWire.BuildAction(1, Net7PlayerAction.FireAllWeapons, 2, 0)), "decoded-inner", false, CancellationToken.None])!;
        await nonTrade;
        Assert.Equal(0, delivered); // All Action subtypes use the same retail no-op processor.
    }

    [Fact]
    public void SessionTradeItemAndCreditProducersKeepAllThreeTradeStatesDistinct()
    {
        string source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "EnbNext.Net7", "Net7Session.cs"));
        string item = Method(source, "public Task<bool> OfferTradeItemAsync", "public Task<bool> RetractTradeItemAsync");
        Assert.Contains("Net7InventoryTransferIdSource.ClientShip", item, StringComparison.Ordinal);
        Assert.DoesNotContain("RequireActivePartnerId", item, StringComparison.Ordinal);

        string credits = Method(source, "public async Task<bool> SetTradeCreditsAsync", "public async Task<bool> RequestDockAsync");
        Assert.Contains("Gameplay.TradeDomain.RequireActivePartnerId()", credits, StringComparison.Ordinal);
        Assert.Contains("Gameplay.TradeDomain.ValidateOfferedCredits", credits, StringComparison.Ordinal);
        Assert.Contains("Gameplay.TradeDomain.RecordLocalOfferedCredits", credits, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadTradeMoney", credits, StringComparison.Ordinal);
    }

    [Fact]
    public void Econ02WireAndOwnershipArePinned()
    {
        byte[] action = Net7GameplayWire.BuildAction(0x11223344, Net7PlayerAction.TradeCredits, 0x55667788, 0x99AABBCC);
        Assert.Equal(16, action.Length);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(0, 4)));
        Assert.Equal(0x18, BinaryPrimitives.ReadInt32LittleEndian(action.AsSpan(4, 4)));
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(8, 4)));
        Assert.Equal(0x99AABBCCu, BinaryPrimitives.ReadUInt32LittleEndian(action.AsSpan(12, 4)));

        Net7TradeState state = Net7GameplayWire.DecodeTradeState(Convert.FromHexString("4433221105"));
        Assert.Equal(0x11223344u, state.PartnerId);
        Assert.Equal((byte)5, state.Type);
        Assert.Contains("ECON-02", Net7GameplayProvenance.GetOwners(0x001F));
        Assert.Contains("ECON-02", Net7GameplayProvenance.GetOwners(0x0027));
        Assert.Contains("ECON-02", Net7GameplayProvenance.GetOwners(0x002C));
    }

    private static byte[] Wire(Net7InventoryTransferPlan plan)
        => Net7GameplayWire.BuildInventoryTransfer(
            plan.Identity.Id, plan.FromInventory, plan.FromSlot, plan.ToInventory, plan.ToSlot, plan.Count);

    private static string Method(string source, string start, string end)
    {
        int begin = source.IndexOf(start, StringComparison.Ordinal);
        int finish = source.IndexOf(end, begin + start.Length, StringComparison.Ordinal);
        Assert.True(begin >= 0 && finish > begin, $"Could not isolate source method between '{start}' and '{end}'.");
        return source[begin..finish];
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EnbNext.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate EnbNext repository root.");
    }
}
