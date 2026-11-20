
namespace EnbNext.Rendering;

/// <summary>Current signed texture-format table consumers. Live tables remain
/// caller-supplied storage; no format default is substituted for a negative index.</summary>
public static class EnbCurrentRetailTextureFormatStorage
{
    /// <summary>Whole00968AC0, including its unsigned format-minus-one
    /// bound and the current968AF4/968B08 jump tables.</summary>
    public static uint BytesPerPixel(uint format)=>format switch
    {
        1=>3,2 or 3 or 19=>4,4 or 6 or 7 or 17 or 18=>2,
        8 or 9 or 13 or 14=>1,_=>0,
    };
}
