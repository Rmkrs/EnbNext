using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7InventoryDomainTests
{
    [Fact]
    public void CurrentSortTargetsAndPresetsAreExactAndNoOtherTupleIsPromoted()
    {
        Assert.Equal(Net7InventorySortTarget.Cargo, Net7InventoryDomain.RequireCurrentSortTarget(1));
        Assert.Equal(Net7InventorySortTarget.Vault, Net7InventoryDomain.RequireCurrentSortTarget(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.RequireCurrentSortTarget(2));

        Assert.Equal(new Net7InventorySortKeys(5, 4, 1), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Category));
        Assert.Equal(new Net7InventorySortKeys(1, 4, 8), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Name));
        Assert.Equal(new Net7InventorySortKeys(10, 1, 8), Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Value));
        Assert.Equal(Net7InventorySortPreset.Category, Net7InventoryDomain.RequireCurrentSortPreset(5, 4, 1));
        Assert.Equal(Net7InventorySortPreset.Name, Net7InventoryDomain.RequireCurrentSortPreset(1, 4, 8));
        Assert.Equal(Net7InventorySortPreset.Value, Net7InventoryDomain.RequireCurrentSortPreset(10, 1, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => Net7InventoryDomain.RequireCurrentSortPreset(5, 4, 8));
    }

    [Fact]
    public void SortPresetsProduceExactCargoAndVaultWirePayloads()
    {
        const uint cargoId = 0x11223344;
        Net7InventorySortKeys category = Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Category);
        Assert.Equal(
            Convert.FromHexString("112233440000000100000005000000040000000100"),
            Net7GameplayWire.BuildInventorySort(cargoId, (uint)Net7InventorySortTarget.Cargo, category.Sort1, category.Sort2, category.Sort3, false));

        const uint vaultId = 0x01020304;
        Net7InventorySortKeys value = Net7InventoryDomain.ResolveSortPreset(Net7InventorySortPreset.Value);
        Assert.Equal(
            Convert.FromHexString("01020304000000030000000A000000010000000801"),
            Net7GameplayWire.BuildInventorySort(vaultId, (uint)Net7InventorySortTarget.Vault, value.Sort1, value.Sort2, value.Sort3, true));
    }

    [Fact]
    public void TransferIdentityKeepsIdSemanticsNeutralAndContextSpecific()
    {
        var currentTarget = new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip);
        var localPlayer = new Net7InventoryTransferIdentity(0x55667788, Net7InventoryTransferIdSource.LocalPlayer);
        Net7InventoryTransferPlan normal = Net7InventoryDomain.CreateInv04Transfer(currentTarget, 1, 2, 3, uint.MaxValue, 4);
        Net7InventoryTransferPlan auxiliary = Net7InventoryDomain.CreateTransfer(localPlayer, 1, 2, 2, uint.MaxValue, uint.MaxValue);

        Assert.Equal(0x11223344u, normal.Identity.Id);
        Assert.Equal(Net7InventoryTransferIdSource.ClientShip, normal.Identity.Source);
        Assert.Equal(0x55667788u, auxiliary.Identity.Id);
        Assert.Equal(Net7InventoryTransferIdSource.LocalPlayer, auxiliary.Identity.Source);
        Assert.Equal(uint.MaxValue, auxiliary.ToSlot);
        Assert.Equal(uint.MaxValue, auxiliary.Count);
        Assert.NotEqual(normal.Identity, auxiliary.Identity);
    }

    [Fact]
    public void RetailUiDestination0BResolvesToCargoButCannotReachSerializerBoundary()
    {
        var identity = new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip);
        Net7InventoryTransferPlan plan = Net7InventoryDomain.CreateInv04UiTransfer(identity, 2, 4, 0x0B, 5, 1);
        Assert.Equal(Net7InventoryDomain.CargoInventory, plan.ToInventory);
        Assert.Equal(
            Convert.FromHexString("112233440000000200000004000000010000000500000001"),
            Net7GameplayWire.BuildInventoryTransfer(plan.Identity.Id, plan.FromInventory, plan.FromSlot, plan.ToInventory, plan.ToSlot, plan.Count));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7GameplayWire.BuildInventoryTransfer(0x11223344, 2, 4, 0x0B, 5, 1));
    }

    [Fact]
    public void PlayerTradePlansUseLocalInventory10AndPartner11IsReadOnly()
    {
        var identity = new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip);

        Net7InventoryTransferPlan offer = Net7InventoryDomain.CreateTradeOffer(identity, 1, 2, 3, 4);
        Assert.Equal(1u, offer.FromInventory);
        Assert.Equal(0x10u, offer.ToInventory);
        Assert.Equal(3u, offer.ToSlot);

        Net7InventoryTransferPlan retract = Net7InventoryDomain.CreateTradeRetraction(identity, 3, 4);
        Assert.Equal(0x10u, retract.FromInventory);
        Assert.Equal(1u, retract.ToInventory);
        Assert.Equal(uint.MaxValue, retract.ToSlot);

        Net7InventoryTransferPlan reorder = Net7InventoryDomain.CreateTradeReorder(identity, 3, 5, 4);
        Assert.Equal(0x10u, reorder.FromInventory);
        Assert.Equal(0x10u, reorder.ToInventory);
        Assert.Equal(5u, reorder.ToSlot);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateTransfer(identity, 0x11, 0, 1, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Net7InventoryDomain.CreateTransfer(identity, 1, 0, 0x11, 0, 1));
    }

    [Fact]
    public void TradeOfferRetractionAndReorderSerializeAsOrdinary0027NetworkOrderTransfers()
    {
        var identity = new Net7InventoryTransferIdentity(0x11223344, Net7InventoryTransferIdSource.ClientShip);
        Net7InventoryTransferPlan offer = Net7InventoryDomain.CreateTradeOffer(identity, 1, 2, 3, 4);
        Net7InventoryTransferPlan retract = Net7InventoryDomain.CreateTradeRetraction(identity, 3, 4);
        Net7InventoryTransferPlan reorder = Net7InventoryDomain.CreateTradeReorder(identity, 3, 5, 4);

        Assert.Equal(Convert.FromHexString("112233440000000100000002000000100000000300000004"), Wire(offer));
        Assert.Equal(Convert.FromHexString("11223344000000100000000300000001FFFFFFFF00000004"), Wire(retract));
        Assert.Equal(Convert.FromHexString("112233440000001000000003000000100000000500000004"), Wire(reorder));
    }


    private static byte[] Wire(Net7InventoryTransferPlan plan)
        => Net7GameplayWire.BuildInventoryTransfer(
            plan.Identity.Id, plan.FromInventory, plan.FromSlot, plan.ToInventory, plan.ToSlot, plan.Count);
}
