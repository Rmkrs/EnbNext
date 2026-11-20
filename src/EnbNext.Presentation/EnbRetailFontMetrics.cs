namespace EnbNext.Presentation;

/// <summary>Font3D width consumer006BCBB0; character advance006BCC40.</summary>
public static class EnbRetailFontMetrics
{
    public static int MeasureWidth(ReadOnlySpan<char> text,ReadOnlySpan<int> glyphWidths,int spacing)
    {
        int width=0;
        foreach(char character in text)
        {
            byte index=unchecked((byte)character);
            if(index==0)break;
            width=unchecked(width+glyphWidths[index]+spacing);
        }
        //The native consumer removes the final tracking only when negative,
        //including an empty string. Bitmap allocation has a separate minimum.
        return spacing<0?unchecked(width-spacing):width;
    }
}
