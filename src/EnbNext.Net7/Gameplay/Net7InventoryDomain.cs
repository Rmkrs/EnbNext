namespace EnbNext.Net7.Gameplay;

/// <summary>
/// Typed policy for the current retail inventory sort/transfer producers.
/// Wire grammar remains owned; this layer constrains the
/// producer values and provenance that the current client actually reaches.
/// </summary>
public enum Net7InventorySortTarget : uint
{
    Cargo = 1,
    Vault = 3,
}

public enum Net7InventorySortPreset
{
    Category,
    Name,
    Value,
}

public enum Net7InventoryTransferIdSource
{
    ClientShip,
    LocalPlayer,
}

public enum Net7VendorTransactionPath
{
    VendorWindowBuyOne,
    VendorWindowBuyStack,
    ShortcutBuyOne,
    ShortcutBuyStack,
    VendorWindowSellOne,
    VendorWindowSellStack,
    ShortcutSellOne,
    ShortcutSellStack,
}

public readonly record struct Net7InventorySortKeys(uint Sort1, uint Sort2, uint Sort3);

public readonly record struct Net7InventoryTransferIdentity(uint Id, Net7InventoryTransferIdSource Source);

public readonly record struct Net7InventoryTransferPlan(
    Net7InventoryTransferIdentity Identity,
    uint FromInventory,
    uint FromSlot,
    uint ToInventory,
    uint ToSlot,
    uint Count);

public static class Net7InventoryDomain
{
    public const uint CargoInventory = 1;
    public const uint EquippedInventory = 2;
    public const uint VaultInventory = 3;
    public const uint RetailUiDestinationSentinel = 0x0B;
    public const uint LocalPlayerTradeInventory = 0x10;
    public const uint PartnerTradeInventory = 0x11;

    public static string ResolveCollectionName(uint inventoryType, bool secondary = false)
    {
        if (secondary)
            return inventoryType == EquippedInventory ? "Inventory.Ammo" : string.Empty;

        return inventoryType switch
        {
            CargoInventory => "Inventory.Cargo",
            EquippedInventory => "Inventory.Equipped",
            VaultInventory => "SecureInventory",
            4 => "VendorInventory",
            5 => "Equipped",
            6 => "Cargo",
            0x0C => "ManufactureTarget",
            0x0D => "ManufactureComponents",
            0x0E => "ManufactureOverride",
            LocalPlayerTradeInventory or PartnerTradeInventory => "Inventory.PlayerTrade",
            0x12 => "HarvestableResources",
            0x13 => "RewardInventory",
            0x14 => "OverflowInventory",
            _ => throw new ArgumentOutOfRangeException(nameof(inventoryType),
                $"Inventory type 0x{inventoryType:X} has no current STATIC-CLOSED collection binding."),
        };
    }

    private static readonly IReadOnlyDictionary<Net7InventorySortPreset, Net7InventorySortKeys> SortPresets =
        new Dictionary<Net7InventorySortPreset, Net7InventorySortKeys>
        {
            [Net7InventorySortPreset.Category] = new(5, 4, 1),
            [Net7InventorySortPreset.Name] = new(1, 4, 8),
            [Net7InventorySortPreset.Value] = new(10, 1, 8),
        };

    public static Net7InventorySortKeys ResolveSortPreset(Net7InventorySortPreset preset)
        => SortPresets.TryGetValue(preset, out Net7InventorySortKeys keys)
            ? keys
            : throw new ArgumentOutOfRangeException(nameof(preset));

    public static Net7InventorySortTarget RequireCurrentSortTarget(uint targetInventory)
        => targetInventory switch
        {
            CargoInventory => Net7InventorySortTarget.Cargo,
            VaultInventory => Net7InventorySortTarget.Vault,
            _ => throw new ArgumentOutOfRangeException(nameof(targetInventory),
                "Current retail inventory sort targets are Cargo (1) and Vault (3)."),
        };

    // Classifies default tuples only. Retail option-resolved keys are not limited to these.
    public static Net7InventorySortPreset RequireCurrentSortPreset(uint sort1, uint sort2, uint sort3)
    {
        foreach (KeyValuePair<Net7InventorySortPreset, Net7InventorySortKeys> entry in SortPresets)
        {
            Net7InventorySortKeys keys = entry.Value;
            if (keys.Sort1 == sort1 && keys.Sort2 == sort2 && keys.Sort3 == sort3)
                return entry.Key;
        }

        throw new ArgumentOutOfRangeException(nameof(sort1),
            $"Current retail sort presets are category (5,4,1), name (1,4,8), and value (10,1,8); got ({sort1},{sort2},{sort3}).");
    }

    private static uint ResolveRetailUiDestination(uint destination)
        => destination == RetailUiDestinationSentinel ? CargoInventory : destination;

    public static void RequireServerTransferDestination(uint destination)
    {
        if (destination == RetailUiDestinationSentinel)
            throw new ArgumentOutOfRangeException(nameof(destination),
                "Destination 0x0B requires the explicit confirmed-destruction path; ordinary transfers cannot emit it.");
    }

    public static void RequireWritableLocalInventory(uint inventory)
    {
        if (inventory == PartnerTradeInventory)
            throw new ArgumentOutOfRangeException(nameof(inventory),
                "Inventory 0x11 is the read-only partner-trade presentation and is not a local move producer.");
    }

    public static Net7InventoryTransferPlan CreateTransfer(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint toInventory,
        uint toSlot,
        uint count)
    {
        RequireWritableLocalInventory(fromInventory);
        RequireWritableLocalInventory(toInventory);
        RequireServerTransferDestination(toInventory);
        return new(identity, fromInventory, fromSlot, toInventory, toSlot, count);
    }

    /// <summary>
    /// Normal retail equipment/cargo/vault transaction controller.
    /// The packet ID is sourced from the separate retail-local selected-target
    /// cache, not authoritative ShipAuxData.TargetGameID. This typed planner
    /// deliberately admits only request shapes the current UI controller
    /// can emit; other 0x0027 owners use the neutral CreateTransfer envelope.
    /// </summary>
    public static Net7InventoryTransferPlan CreateInv04Transfer(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint toInventory,
        uint toSlot,
        uint count)
    {
        if (identity.Source != Net7InventoryTransferIdSource.ClientShip)
            throw new ArgumentException(
                "INV-04 normal transfers require the Client Ship (0047, SClient+1130) ID provenance.",
                nameof(identity));

        RequireInv04Inventory(fromInventory, nameof(fromInventory));
        RequireInv04Inventory(toInventory, nameof(toInventory));

        if (fromSlot == uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(fromSlot),
                "INV-04 normal retail transactions require a concrete source slot.");
        if (count == 0)
            throw new ArgumentOutOfRangeException(nameof(count),
                "INV-04 normal retail transactions reject zero quantity before packet construction.");

        return CreateTransfer(identity, fromInventory, fromSlot, toInventory, toSlot, count);
    }

    public static Net7InventoryTransferPlan CreateInv04UiTransfer(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint uiDestination,
        uint toSlot,
        uint count)
        => CreateInv04Transfer(
            identity, fromInventory, fromSlot, ResolveRetailUiDestination(uiDestination), toSlot, count);

    private static void RequireInv04Inventory(uint inventory, string parameterName)
    {
        if (inventory is CargoInventory or EquippedInventory or VaultInventory)
            return;

        throw new ArgumentOutOfRangeException(parameterName,
            "INV-04 equipment/cargo/vault transfers use inventory IDs 1 (Cargo), 2 (Equipped), or 3 (Vault).");
    }

    /// <summary>
    /// Exact current vendor producer. Vendor transactions are semantic
    /// uses of the shared 0x0027 envelope, not a separate purchase protocol.
    /// The ID is always SClient+1130, the 0047 Client Ship. Stack paths
    /// receive the caller's retail stack count; Price, AveCost and Money are
    /// deliberately not inputs. Null where InventoryTransaction_Start 00690AA0
    /// starts nothing: NO_SLOT (005A02E0/005A03B0) or Num=0.
    /// </summary>
    public static Net7InventoryTransferPlan? CreateVendorTransfer(
        Net7InventoryTransferIdentity identity,
        Net7VendorTransactionPath path,
        uint sourceSlot,
        uint materializedStackCount = 0)
    {
        if (identity.Source != Net7InventoryTransferIdSource.ClientShip)
            throw new ArgumentException(
                "ECON-01 vendor transfers require the Client Ship (0047, SClient+1130) ID provenance.",
                nameof(identity));

        uint count = VendorPathUsesWholeStack(path) ? materializedStackCount : 1u;
        if (sourceSlot == uint.MaxValue || count == 0)
            return null;

        return path switch
        {
            Net7VendorTransactionPath.VendorWindowBuyOne
                or Net7VendorTransactionPath.VendorWindowBuyStack
                or Net7VendorTransactionPath.ShortcutBuyOne
                or Net7VendorTransactionPath.ShortcutBuyStack
                => CreateTransfer(identity, 4, sourceSlot, CargoInventory, uint.MaxValue, count),

            Net7VendorTransactionPath.VendorWindowSellOne
                or Net7VendorTransactionPath.VendorWindowSellStack
                => CreateTransfer(identity, CargoInventory, sourceSlot, 4, uint.MaxValue, count),

            Net7VendorTransactionPath.ShortcutSellOne
                or Net7VendorTransactionPath.ShortcutSellStack
                => CreateTransfer(identity, CargoInventory, sourceSlot, 4, 0, count),

            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    public static bool VendorPathUsesWholeStack(Net7VendorTransactionPath path)
        => path switch
        {
            Net7VendorTransactionPath.VendorWindowBuyStack
                or Net7VendorTransactionPath.ShortcutBuyStack
                or Net7VendorTransactionPath.VendorWindowSellStack
                or Net7VendorTransactionPath.ShortcutSellStack => true,
            Net7VendorTransactionPath.VendorWindowBuyOne
                or Net7VendorTransactionPath.ShortcutBuyOne
                or Net7VendorTransactionPath.VendorWindowSellOne
                or Net7VendorTransactionPath.ShortcutSellOne => false,
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

    /// <summary>
    /// Current dedicated vendor-window Buy Stack and Sell Whole Stack have an
    /// unconditional first confirmation gate. Shortcut stack paths do not.
    /// Sell paths may additionally present a loss warning from Price/AveCost;
    /// that local presentation predicate is intentionally not invented here.
    /// </summary>
    public static bool VendorPathRequiresPrimaryConfirmation(Net7VendorTransactionPath path)
        => path switch
        {
            Net7VendorTransactionPath.VendorWindowBuyStack
                or Net7VendorTransactionPath.VendorWindowSellStack => true,
            Net7VendorTransactionPath.VendorWindowBuyOne
                or Net7VendorTransactionPath.ShortcutBuyOne
                or Net7VendorTransactionPath.ShortcutBuyStack
                or Net7VendorTransactionPath.VendorWindowSellOne
                or Net7VendorTransactionPath.ShortcutSellOne
                or Net7VendorTransactionPath.ShortcutSellStack => false,
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

    public static bool VendorPathMayRequireLossWarning(Net7VendorTransactionPath path)
        => path switch
        {
            Net7VendorTransactionPath.VendorWindowSellOne
                or Net7VendorTransactionPath.VendorWindowSellStack => true,
            Net7VendorTransactionPath.VendorWindowBuyOne
                or Net7VendorTransactionPath.VendorWindowBuyStack
                or Net7VendorTransactionPath.ShortcutBuyOne
                or Net7VendorTransactionPath.ShortcutBuyStack
                or Net7VendorTransactionPath.ShortcutSellOne
                or Net7VendorTransactionPath.ShortcutSellStack => false,
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };

    /// <summary>
    /// Local trade-offer moves remain ordinary 0x0027 transactions.
    /// Even while a trade partner is active, retail sources ID from the separate
    /// SClient+0x1130 (the 0047 Client Ship), never from the partner field at +0x1134.
    /// </summary>
    public static Net7InventoryTransferPlan CreateTradeOffer(
        Net7InventoryTransferIdentity identity,
        uint fromInventory,
        uint fromSlot,
        uint tradeSlot,
        uint count)
    {
        RequireTradeIdentity(identity);
        if (fromInventory != CargoInventory)
            throw new ArgumentOutOfRangeException(nameof(fromInventory),
                "ECON-02 current trade offers originate from Cargo inventory 1.");
        RequireConcreteSlot(fromSlot, nameof(fromSlot), "trade offer source");
        RequireTradeSlot(tradeSlot, nameof(tradeSlot));
        RequirePositiveCount(count);
        return CreateTransfer(identity, CargoInventory, fromSlot, LocalPlayerTradeInventory, tradeSlot, count);
    }

    public static Net7InventoryTransferPlan CreateTradeRetraction(
        Net7InventoryTransferIdentity identity,
        uint tradeSlot,
        uint count)
    {
        RequireTradeIdentity(identity);
        RequireTradeSlot(tradeSlot, nameof(tradeSlot));
        RequirePositiveCount(count);
        return CreateTransfer(identity, LocalPlayerTradeInventory, tradeSlot, CargoInventory, uint.MaxValue, count);
    }

    public static Net7InventoryTransferPlan CreateTradeReorder(
        Net7InventoryTransferIdentity identity,
        uint fromTradeSlot,
        uint toTradeSlot,
        uint count)
    {
        RequireTradeIdentity(identity);
        RequireTradeSlot(fromTradeSlot, nameof(fromTradeSlot));
        RequireTradeSlot(toTradeSlot, nameof(toTradeSlot));
        RequirePositiveCount(count);
        return CreateTransfer(identity, LocalPlayerTradeInventory, fromTradeSlot, LocalPlayerTradeInventory, toTradeSlot, count);
    }

    private static void RequireTradeIdentity(Net7InventoryTransferIdentity identity)
    {
        if (identity.Source != Net7InventoryTransferIdSource.ClientShip)
            throw new ArgumentException(
                "ECON-02 trade item transfers require the Client Ship (0047, SClient+1130) ID provenance; the active trade partner is a separate state value.",
                nameof(identity));
    }

    private static void RequireTradeSlot(uint slot, string parameterName)
    {
        if (slot < 6)
            return;
        throw new ArgumentOutOfRangeException(parameterName,
            "ECON-02 Inventory.PlayerTrade contains exactly six local offer slots (0..5).");
    }

    private static void RequireConcreteSlot(uint slot, string parameterName, string description)
    {
        if (slot != uint.MaxValue)
            return;
        throw new ArgumentOutOfRangeException(parameterName, $"ECON-02 {description} requires a concrete slot.");
    }

    private static void RequirePositiveCount(uint count)
    {
        if (count != 0)
            return;
        throw new ArgumentOutOfRangeException(nameof(count), "ECON-02 trade item transfers require a positive quantity.");
    }
}
