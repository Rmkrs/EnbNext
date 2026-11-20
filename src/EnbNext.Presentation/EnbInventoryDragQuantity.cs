namespace EnbNext.Presentation;

public readonly record struct EnbInventoryDragQuantity(int Count,bool Prompt)
{
    // Retail 005A05B0/005A0D00 and 00690AA0. The input producer is 004314C0.
    public static EnbInventoryDragQuantity Resolve(int count,bool control,bool shift,bool rightButton=false)
        => control ? new(1,false) : new(count,count>1 && !shift && !rightButton);
}
