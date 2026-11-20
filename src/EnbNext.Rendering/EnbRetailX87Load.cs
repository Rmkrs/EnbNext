namespace EnbNext.Rendering;

/// <summary>x87 float load semantics.</summary>
internal static class EnbRetailX87Load
{
    /// <summary>Masked x87 FLD m32/FSTP m32 preserves finite/inf words and
    /// quiets signaling NaNs. Hardware controls cover both signs/all exponents.</summary>
    public static uint LoadedFloatWord(uint word)=>(word&0x7f800000)==0x7f800000&&(word&0x7fffff)!=0?word|0x400000:word;
}
