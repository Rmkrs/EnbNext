using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

public sealed partial class EnbRetailTextEditor
{
    public static readonly EnbUiRaster CaretRaster = new(1, 1, ImmutableArray.Create<byte>(25,178,0,127));
    private EnbRetailText?[] _rowLabels = [];
    private string _windowText = "";
    private int _windowStart;
    private EnbUiSize _size;
    public EnbUiRect Bounds { get; private set; }
    public EnbRetailFontFace? Font { get; private set; }
    public double Step { get; private set; }
    public int Scroll { get; private set; }
    public EnbRetailDialogText.EditorLayout? Wrapped { get; private set; }
    public bool HasLayout => Font is not null;
    public int TextRowCount => Wrapped?.Lines.Length ?? 1;
    public int RowCount => Wrapped is { } wrapped ? wrapped.Lines.Length + (wrapped.Row == wrapped.Lines.Length ? 1 : 0) : 1;
    public int VisibleRows => Math.Max(1,(int)(Bounds.Height / Step));
    public EnbRetailText? RenderedRow(int visibleRow) => (uint)visibleRow < _rowLabels.Length ? _rowLabels[visibleRow] : null;

    public (int Start, string Text) Row(int index)
    {
        if (Wrapped is not { } wrapped) return (_windowStart, _windowText);
        int start = 0;
        for (int i = 0; i < index && i < wrapped.Lines.Length; i++) start += wrapped.Lines[i].Length;
        return (start, index < wrapped.Lines.Length ? wrapped.Lines[index] : "");
    }

    public void Dispose()
    { RetireGadgetInput(); foreach(var label in _rowLabels) label?.Dispose(); _rowLabels=[]; Font=null; Wrapped=null; _windowText=""; }

    public int HitMultiline(EnbUiPoint point)
    {
        if (Wrapped is null || Font is null) return CaretIndex;
        int row = Math.Clamp((int)Math.Floor((point.Y-Bounds.Y)/Step)+Scroll,0,RowCount-1);
        var line = Row(row);
        float reference = 640f/(float)_size.Width;
        return line.Start + EnbRetailDialogText.HitEditorRow(line.Text,(float)(point.X-Bounds.X)*reference,
            value=>Font.MeasureWidth(value)*Font.Scale*reference);
    }

    public void Move(EnbRetailDialogText.EditorMove move)
    {
        if (Wrapped is not null) CaretIndex = EnbRetailDialogText.MoveEditor(Wrapped,CaretIndex,Text.Length,move,VisibleRows);
    }

    public void Draw(ImmutableArray<EnbUiTextDraw>.Builder images, EnbUiRect bounds, EnbRetailFontFace font,
        EnbUiSize size, double scaling, EnbRgb color, string placeholder, int scroll, bool followCaret)
    {
        Bounds=bounds; Font=font; _size=size;
        Step=Math.Max(1,(font.CellHeight+font.LineSpacing)*font.Scale);
        if(Multiline) Step=EnbRetailDialogText.EditorRowStep(font.CellHeight*font.Scale,(float)size.Height)*size.Height/480;
        float reference=640f/(float)size.Width;
        if(Multiline)
            Wrapped=EnbRetailDialogText.WrapEditor(Text,CaretIndex,(int)(bounds.Width*reference),
                c=>font.CharacterAdvance(c)*font.Scale*reference);
        else
        {
            Wrapped=null;
            var window=EnbRetailChatInputViewport.Resolve(Text,CaretIndex,(float)bounds.Width*reference,
                value=>font.MeasureWidth(value)*font.Scale*reference,
                c=>font.CharacterAdvance(c)*font.Scale*reference);
            _windowStart=window.Start;_windowText=Text.Substring(window.Start,window.Length);
        }
        int visible=Multiline?VisibleRows:1, caretLine=Wrapped?.Row??0;
        Scroll=Multiline?scroll:0;
        if(IsFocused && Multiline && followCaret) Scroll=Math.Clamp(Scroll,Math.Max(0,caretLine-visible+1),Math.Max(0,caretLine));
        if(Multiline && Wrapped!.Lines.Length<=visible) Scroll=0;
        if(_rowLabels.Length!=visible)
        {
            for(int i=visible;i<_rowLabels.Length;i++)_rowLabels[i]?.Dispose();
            Array.Resize(ref _rowLabels,visible);
        }
        int used=0;
        if(!Entered && Text.Length==0)
        {
            var rows=Multiline?EnbRetailDialogText.WrapEditor(placeholder,0,(int)(bounds.Width*reference),
                c=>font.CharacterAdvance(c)*font.Scale*reference).Lines:[placeholder];
            for(int i=0;i<Math.Min(rows.Length,visible);i++)
                DrawText(i,rows[i],Multiline?bounds.Y+i*Step:bounds.Y+bounds.Height/2-font.CellHeight*font.Scale/2);
            used=Math.Min(rows.Length,visible);
        }
        else for(int i=Scroll;i<Math.Min(RowCount,Scroll+visible);i++)
        {
            var line=Row(i);
            DrawText(i-Scroll,line.Text,Multiline?bounds.Y+(i-Scroll)*Step:bounds.Y+bounds.Height/2-font.CellHeight*font.Scale/2);
            used=i-Scroll+1;
            if(IsFocused && i==caretLine) DrawCaret();
        }
        for(int i=used;i<_rowLabels.Length;i++)_rowLabels[i]?.Clear();
        void DrawCaret()
        {
            var line=Row(caretLine);
            double y=Multiline?bounds.Y+(caretLine-Scroll)*Step:bounds.Y+bounds.Height/2-font.CellHeight*font.Scale/2;
            int column=Math.Clamp(Wrapped?.Column??CaretIndex-line.Start,0,line.Text.Length);
            char character=column<line.Text.Length?line.Text[column]:'n';
            double ux=size.Width/640,uy=size.Height/480;
            double width=font.MeasureWidth(line.Text[..column])*font.Scale;
            var rectangle=EnbRetailCaretGeometry.Resolve(new((float)(bounds.X/ux),(float)(-y/uy)),0,(float)(width/ux),
                (float)(font.CharacterAdvance(character)*font.Scale/ux),(float)(font.CellHeight*font.Scale/uy),false,false,InsertMode);
            images.Add(new(CaretRaster,new(rectangle.X*ux,-rectangle.Y*uy,rectangle.Z*ux,rectangle.W*uy)){Clip=bounds});
        }
        void DrawText(int index,string value,double y)
        {
            var label=_rowLabels[index]??=new();label.SetText(value,font,color);label.SetDisplayScale(1,1);
            if(label.Raster is { } raster)images.Add(new(raster,new(bounds.X,y,label.Width,label.Height)){Clip=bounds});
        }
    }
}
