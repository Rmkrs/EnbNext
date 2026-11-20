using EnbNext.Net7.World;

namespace EnbNext.Net7.Gameplay;

public enum Net7TradeStatusMessage { None, Waiting, Confirmed, Completed }

/// <summary>
/// Current player-trade coordinator. The coordinator packet owns only
/// the trade-session UI/control state. Inventory slots, TradeMoney and Money
/// remain server-authored AuxData and are never speculatively materialized here.
/// </summary>
public sealed class Net7TradeDomain
{
    private const string LocalTradePath = "ShipAuxData.Inventory.PlayerTrade";
    private const string LocalTradeMoneyPath = "ShipAuxData.TradeMoney";

    public uint? ActivePartnerId { get; private set; }
    public ulong SessionRevision { get; private set; }
    public bool WaitingForConfirmation { get; private set; }
    public bool Confirmed { get; private set; }
    public bool? LastCloseSuccessful { get; private set; }
    //739DE0 dispatches to5E2F70/5E3050, each of which writes the same label.
    //It is notification order, not a priority over the two retained flags.
    public Net7TradeStatusMessage StatusMessage { get; private set; }

    /// <summary>
    /// Client-local credit edit cache. Retail validates a new offer against
    /// authoritative Money plus this previous local offer; server-authored TradeMoney
    /// is presentation/materialization state and is deliberately not substituted here.
    /// </summary>
    public uint LocalOfferedCredits { get; private set; }

    public bool ApplyCoordinator(Net7TradeState update)
    {
        ArgumentNullException.ThrowIfNull(update);
        switch (update.Type)
        {
            case 0:
                StatusMessage=Net7TradeStatusMessage.None;
                SessionRevision++;
                ActivePartnerId = update.PartnerId;
                WaitingForConfirmation = false;
                Confirmed = false;
                LastCloseSuccessful = null;
                LocalOfferedCredits = 0;
                return true;
            case 1:
                StatusMessage=Net7TradeStatusMessage.None;
                ActivePartnerId = null;
                WaitingForConfirmation = false;
                Confirmed = false;
                LastCloseSuccessful = false;
                LocalOfferedCredits = 0;
                return true;
            case 2:
                StatusMessage=Net7TradeStatusMessage.Completed;
                ActivePartnerId = null;
                WaitingForConfirmation = false;
                Confirmed = false;
                LastCloseSuccessful = true;
                LocalOfferedCredits = 0;
                return true;
            case 3:
                StatusMessage=Net7TradeStatusMessage.Waiting;
                WaitingForConfirmation = true;
                return true;
            case 4:
                StatusMessage=Net7TradeStatusMessage.None;
                WaitingForConfirmation = false;
                return true;
            case 5:
                StatusMessage=Net7TradeStatusMessage.Confirmed;
                Confirmed = true;
                return true;
            case 6:
                StatusMessage=Net7TradeStatusMessage.None;
                Confirmed = false;
                return true;
            default:
                return false;
        }
    }

    public uint RequireActivePartnerId()
        => ActivePartnerId ?? throw new InvalidOperationException("Player-trade action requires an active server-coordinated trade partner.");

    public uint ValidateOfferedCredits(long requestedAmount, ulong authoritativeMoney)
        => ValidateOfferedCredits(requestedAmount, authoritativeMoney, LocalOfferedCredits);

    public void RecordLocalOfferedCredits(uint amount)
    {
        _ = RequireActivePartnerId();
        LocalOfferedCredits = amount;
    }

    public static uint ValidateOfferedCredits(long requestedAmount, ulong authoritativeMoney, ulong previousOffer)
    {
        if (requestedAmount < 0 || requestedAmount > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestedAmount), "Trade credits must be a nonnegative 32-bit value.");

        ulong requested = checked((ulong)requestedAmount);
        bool withinAvailable = requested <= previousOffer || requested - previousOffer <= authoritativeMoney;
        if (!withinAvailable)
            throw new InvalidOperationException("Trade credit offer exceeds authoritative Money plus the previous offer.");
        return checked((uint)requested);
    }

    public bool TryGetPendingLocalSync(Net7AuxDataSchema localShipAuxData, out uint partnerId)
    {
        ArgumentNullException.ThrowIfNull(localShipAuxData);
        partnerId = 0;
        if (ActivePartnerId is not uint activePartner)
            return false;

        Net7AuxDataProperty trade = Require(localShipAuxData, LocalTradePath);
        Net7AuxDataProperty money = Require(localShipAuxData, LocalTradeMoneyPath);
        if (trade.State <= 1 && money.State <= 1)
            return false;

        partnerId = activePartner;
        return true;
    }

    public static ulong ReadTradeMoney(Net7AuxDataSchema localShipAuxData)
    {
        Net7AuxDataProperty property = Require(localShipAuxData, LocalTradeMoneyPath);
        if (property.State == 0 || property.Value is null)
            return 0;
        if (property.Value is ulong value)
            return value;
        throw new InvalidDataException("ShipAuxData.TradeMoney is not a materialized UInt64 value.");
    }

    public static void NormalizeLocalSync(Net7AuxDataSchema localShipAuxData)
    {
        Net7AuxDataCodec.Normalize(Require(localShipAuxData, LocalTradePath));
        Net7AuxDataCodec.Normalize(Require(localShipAuxData, LocalTradeMoneyPath));
    }

    public void Reset()
    {
        StatusMessage=Net7TradeStatusMessage.None;
        ActivePartnerId = null;
        WaitingForConfirmation = false;
        Confirmed = false;
        LastCloseSuccessful = null;
        LocalOfferedCredits = 0;
    }

    private static Net7AuxDataProperty Require(Net7AuxDataSchema schema, string path)
        => schema.Properties.SingleOrDefault(p => p.Path == path)
            ?? throw new InvalidDataException($"AuxData schema '{schema.Name}' is missing required trade property '{path}'.");
}
