namespace EnbNext.Rendering;

/// <summary>006AB190's temporary text window. The editor retains the complete draft.</summary>
public static class EnbRetailChatInputViewport
{
    public static (int Start,int Length) Resolve(string text,int caret,float width,
        Func<string,float> measure,Func<char,float> advance)
    {
        using var precision=new EnbRetailLineArithmetic.Scope(EnbRetailLinePrecision.Single24);
        caret=Math.Clamp(caret,0,text.Length);
        width=width>0?width:1;
        float remaining=measure(text);
        if(remaining<=width)return (0,text.Length);
        int end=text.Length-1;
        while(end>caret && remaining>width)
        {
            remaining=(float)((EnbRetailLineArithmetic.Register)remaining-advance(text[end]));
            end--;
        }
        int start=0;
        while(start<=end && remaining>width)
        {
            remaining=(float)((EnbRetailLineArithmetic.Register)remaining-advance(text[start]));
            start++;
        }
        return(start,end+1-start);
    }
}
