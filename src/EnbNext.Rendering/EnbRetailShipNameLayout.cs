using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>006AAD10's combined root window, focused ship-name trim and clip.</summary>
public static class EnbRetailShipNameLayout
{
    public static (string Text,Vector4 Caret,Vector4 Clip) Resolve(string text,int caret,bool focused,bool insert,
        Vector2 anchor,Vector4 attachmentBounds,float rootHalfWidth,float displayScaleX,float fontHeight,
        Func<string,float> referenceWidth,Func<char,float> referenceAdvance,Func<string,float> displayWidth)
    {
        var window=EnbRetailChatInputViewport.Resolve(text,caret,rootHalfWidth*2,referenceWidth,referenceAdvance);
        // 006AB190 terminates the original buffer and returns a pointer into it.
        // The later memmove changes that same buffer; preserve the pointer offset.
        Span<char> buffer=stackalloc char[text.Length+1];buffer.Clear();text.AsSpan().CopyTo(buffer);
        int end=window.Start+window.Length;buffer[end]='\0';
        if(focused)
        {
            string whole=new(buffer[..end]);
            float width=displayScaleX*((attachmentBounds.Z-attachmentBounds.X)-20);
            string trimmed=EnbRetailFrontendTextWindow.TrimLeading(whole,width,displayWidth);
            if(trimmed.Length!=whole.Length){trimmed.AsSpan().CopyTo(buffer);buffer[trimmed.Length]='\0';}
        }
        var tail=buffer[window.Start..];int terminator=tail.IndexOf('\0');
        string shown=new(tail[..terminator]);int column=Math.Clamp(caret-window.Start,0,shown.Length);
        var rectangle=EnbRetailCaretGeometry.Resolve(anchor,referenceWidth(shown),referenceWidth(shown[..column]),
            referenceAdvance(column<shown.Length?shown[column]:'n'),fontHeight,true,true,insert);
        float halfHeight=(attachmentBounds.W-attachmentBounds.Y)*.5f;
        var clip=new Vector4((int)(attachmentBounds.X+10),(int)(anchor.Y+halfHeight),
            (int)(attachmentBounds.Z-10),(int)(anchor.Y-halfHeight));
        return(shown,rectangle,clip);
    }
}
