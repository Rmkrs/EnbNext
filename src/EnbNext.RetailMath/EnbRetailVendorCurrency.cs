using EnbNext.Rendering;

namespace EnbNext.RetailMath;

public static class EnbRetailVendorCurrency
{
    // 00652A04 stores the signed template count as float; 004F05C0 loads
    // signed currency, multiplies by that float, then calls __ftol.
    public static long PurchaseTotal(ulong price, uint count, EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        => Total(price,(float)unchecked((int)count),precision);

    //00652CF9 zero-extends the slot count before converting it to float.
    public static long SaleTotal(ulong price,uint count,EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
        => Total(price,(float)count,precision);

    private static long Total(ulong price,float count,EnbRetailLinePrecision precision)
    {
        using var scope = new EnbRetailLineArithmetic.Scope(precision);
        ushort control = precision == EnbRetailLinePrecision.Single24 ? (ushort)0x7f : (ushort)0x27f;
        var value = EnbRetailExtendedX87Register.FromInteger64(unchecked((long)price), control);
        value = EnbRetailExtendedX87Register.MultiplyFloatMemory(value, BitConverter.SingleToUInt32Bits(count));
        return unchecked((long)EnbRetailExtendedX87Register.TruncateInteger64(value));
    }
}
