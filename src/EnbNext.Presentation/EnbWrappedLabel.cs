namespace EnbNext.Presentation;

/// <summary>Retail 004F9D90 wrapping and 006CF930/006CA0C0 wrapped-line motion.</summary>
public static class EnbWrappedLabel
{
    public static string[] Lines(string text,double width,Func<char,double> advance)
        // 64BCA0 and 6CF930 both use 4FA8F0's default wrapper flags.
        => EnbRetailDialogText.Wrap(text,(float)width,c=>(float)advance(c));
}

public sealed class EnbWrappedLabelScroll
{
    private string? _text;
    private string? _identity;
    private readonly Dictionary<int,EnbWrappedLabelRow> _rows=[];
    private long? _tick;
    public float Offset {get;private set;}
    public void Pause()=>_tick=null;
    public float Update(string text,long now,bool hovered,float limit,float speed,string? identity=null)
    {
        if(_text!=text || _identity!=identity){_text=text;_identity=identity;Offset=0;_tick=null;_rows.Clear();}
        long delta=_tick is {} tick?Math.Max(0,now-tick):0;_tick=now;
        float rendered=Offset;
        // Wrapped lines revert to their construction positions on mouse exit.
        Offset=hovered?Math.Min(Math.Max(0,limit),Offset+delta*speed*.02f):0;
        return rendered;
    }
    public bool UpdateEllipsis(int row,float y,float bottom,int height,bool last)
    {
        if(!_rows.TryGetValue(row,out var state))_rows[row]=state=new(0);
        return state.UpdateEllipsis(y,bottom,height,last);
    }
}
