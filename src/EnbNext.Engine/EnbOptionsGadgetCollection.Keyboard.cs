namespace EnbNext.Engine;

internal sealed partial class EnbOptionsGadgetCollection
{
    private ushort textCode,textModifiers;

    // Hosts map their native key identity to the shared retail vocabulary.
    // Editing commands stay key events; printable input arrives as translated text.
    public bool PrepareTextKey(ushort code,ushort modifiers)
    {
        textCode=code;textModifiers=modifiers;
        return code is not (0x08 or 0x09 or 0x0d or 0x1b or >=0x21 and <=0x28 or 0x2d or 0x2e or 0x10 or 0x11 or 0x12 or 0x100);
    }

    public void QueueText(ReadOnlySpan<char> text,float x,float y)
    {
        foreach(char c in text)Input.Push(new(textCode,textModifiers,x,y,c));
    }
}
