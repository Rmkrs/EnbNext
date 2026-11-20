namespace EnbNext.Presentation;

public enum EnbVendorConfirmationStage { None, BuyStack, SellOneLoss, SellStackLoss, SellStack }
public enum EnbVendorLoss { None, Ordinary, Significant }

public static class EnbVendorConfirmation
{
    //00652890 ->005A00C0 uses template+50, not the vendor slot's stock count.
    public static string PurchasePrompt(ulong price,uint maximumStack,string name)
    {
        long total=EnbNext.RetailMath.EnbRetailVendorCurrency.PurchaseTotal(price,maximumStack);
        //004FAEE0 adds commas only when every source character is a digit.
        string amount=FormatCurrency(total);
        return FormattableString.Invariant($"Confirm purchase of {unchecked((int)maximumStack)} {name} for {amount} credits?");
    }

    public static string SalePrompt(ulong price,int count)
        => $"Are you sure you want to sell the whole stack for {FormatCurrency(EnbNext.RetailMath.EnbRetailVendorCurrency.SaleTotal(price,unchecked((uint)count)))} credits?";

    private static string FormatCurrency(long value)=>value.ToString(value<0?"D":"N0",System.Globalization.CultureInfo.InvariantCulture);

    //006531F0: template+0C must be13. Currency comparison004F0680 is signed;
    // both average-cost thresholds pass through retail __ftol before comparison.
    public static EnbVendorLoss Loss(uint itemType, ulong price, float? averageCost)
    {
        if (itemType != 13 || averageCost is not {} cost) return EnbVendorLoss.None;
        long signedPrice = unchecked((long)price);
        if (signedPrice >= Truncate(cost)) return EnbVendorLoss.None;
        return signedPrice < Truncate((double)cost * .5) ? EnbVendorLoss.Significant : EnbVendorLoss.Ordinary;
    }

    public static EnbVendorConfirmationStage Begin(bool buy, bool stack, EnbVendorLoss loss)
        => stack ? buy ? EnbVendorConfirmationStage.BuyStack : EnbVendorConfirmationStage.SellStack
            : !buy && loss != EnbVendorLoss.None ? EnbVendorConfirmationStage.SellOneLoss : EnbVendorConfirmationStage.None;

    //00652FF0 case4 closes the primary dialog then calls006531F0(stack=1).
    // The other accepted stages execute their transaction and clear the dialog.
    public static EnbVendorConfirmationStage AfterAccept(EnbVendorConfirmationStage stage, EnbVendorLoss loss)
        => stage == EnbVendorConfirmationStage.SellStack && loss != EnbVendorLoss.None
            ? EnbVendorConfirmationStage.SellStackLoss : EnbVendorConfirmationStage.None;

    private static long Truncate(double value)
        => double.IsNaN(value) || value >= 9223372036854775808d || value < -9223372036854775808d
            ? long.MinValue : (long)Math.Truncate(value);
}
