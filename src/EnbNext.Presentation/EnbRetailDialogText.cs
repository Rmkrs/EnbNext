namespace EnbNext.Presentation;

public static class EnbRetailDialogText
{
    //004F9D90 with the default004F9BF0 flags and cursor index0 used by00588DC0.
    public static string[] Wrap(string text, float limit, Func<char, float> advance)
        => WrapCore(text, limit, advance, true, true);

    public sealed record TooltipLayout(string[] Lines, int Width);
    // 006ABC60 grows the candidate width until the row count fits either the
    // attempt count or the wrapper's explicit-break count plus one.
    public static TooltipLayout WrapTooltip(string text,Func<char,float> advance)
    {
        int width=100;
        string[] lines=[];
        for(int attempt=1;attempt<=20;attempt++)
        {
            lines=WrapCore(text,width,advance,true,true,true,0,out _,out _,out int breaks);
            if(lines.Length<=attempt || lines.Length<=breaks+1)break;
            if(width<600)width+=50;
        }
        return new(lines.Take(20).ToArray(),width);
    }

    // 00671790 clears all three wrapper flags. 00672B00 consumes both
    // the untrimmed rows and the cursor coordinates produced by 004F9D90.
    public sealed record EditorLayout(string[] Lines, int Row, int Column);
    public static EditorLayout WrapEditor(string text, int caret, float limit, Func<char, float> advance)
    {
        var lines = WrapCore(text, limit, advance, false, false, false, caret, out int row, out int column,out _);
        return new(lines, row, column);
    }

    public enum EditorMove { Up, Down, PageUp, PageDown, Home, End }

    // 006D5B10 -> 00671E02: convert pixel height to reference units,
    // add one reference unit, then floor before constructing every row.
    // A26D75 installs BCF788=173F (round-down), despite its old Ghidra name.
    public static float EditorRowStep(float pixelHeight, float viewportHeight)
    {
        float height = (float)(480d / viewportHeight * pixelHeight);
        return (float)Math.Floor((double)height + 1);
    }

    // 006732F0 measures complete prefixes, stopping before the final byte.
    // It does not choose the nearest glyph midpoint.
    public static int HitEditorRow(string line, float x, Func<string, float> measure)
    {
        int column = 0;
        for (int i = 1; i < line.Length; i++)
        {
            column = i;
            if (x <= measure(line[..i])) break;
        }
        return column;
    }

    // 00672120, 00672DC0/00672E40 and 0084B2F0. Page keys repeat against
    // the retained row/column, rebuilding the wrapper only after the last step.
    public static int MoveEditor(EditorLayout layout, int caret, int length, EditorMove move, int visibleRows)
    {
        int row = layout.Row, column = layout.Column;
        int Relative(int offset) => (int)Math.Min((uint)(caret + offset), (uint)length);
        if (move == EditorMove.Home) return column == 0 ? 0 : Relative(-column);
        if (move == EditorMove.End)
        {
            if (row >= layout.Lines.Length) return caret;
            int offset = layout.Lines[row].Length - column - 1;
            return offset == 0 ? length : Relative(offset);
        }
        int delta = 0;
        if (move is EditorMove.Up or EditorMove.PageUp)
        {
            if (row > 0)
            {
                int previous = layout.Lines[row-1].Length;
                delta = -(previous < column ? column+1 : previous);
            }
        }
        else if (row + 1 < layout.Lines.Length)
            delta = layout.Lines[row].Length-column+Math.Min(column,layout.Lines[row+1].Length);
        int repeats = move is EditorMove.PageUp or EditorMove.PageDown ? Math.Max(1,visibleRows) : 1;
        for (int i = 0; i < repeats; i++) caret = Relative(delta);
        return caret;
    }

    // 6821F0 clears wrapper flags24/25; a single resulting row retains the
    // original message, while multiple rows use the wrapper's strings.
    public static string[] WrapChat(string text, float limit, Func<char, float> advance)
    {
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        var lines = WrapCore(text, limit, advance, false, false);
        return lines.Length == 1 ? [text] : lines;
    }

    private static string[] WrapCore(string text, float limit, Func<char, float> advance, bool escapedNewline, bool trimFinalLine)
        => WrapCore(text, limit, advance, escapedNewline, trimFinalLine, true, 0, out _, out _,out _);

    private static string[] WrapCore(string text, float limit, Func<char, float> advance, bool escapedNewline,
        bool trimFinalLine, bool trimWrappedLines, int caret, out int row, out int column,out int explicitBreaks)
    {
        row = column = explicitBreaks = 0;
        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        if (text.Length == 0) return [];
        var lines = new List<string>();
        char[] buffer = new char[text.Length + 3];
        int read = 0, write = 0;
        float width = 0;
        while (read < text.Length)
        {
            if (read == caret) { row = lines.Count; column = write; }
            char c = text[read];
            float nextWidth = width + advance(c);
            int newline = 0;
            if (limit <= 0 || nextWidth <= limit)
            {
                if ((escapedNewline && c == '\\' && read + 1 < text.Length && text[read + 1] == 'n') ||
                    (c == '\r' && read + 1 < text.Length && text[read + 1] == '\n')) newline = 2;
                else if (c == '\n') newline = 1;
                else { buffer[write++] = c; read++; width = nextWidth; continue; }
            }
            else
            {
                int oldWrite = write;
                while (write > 0 && buffer[write] != ' ') { write--; read--; }
                if (write > 0)
                {
                    buffer[write + 1] = '\0';
                    read++;
                }
                else
                {
                    if (oldWrite == 0) return [text];
                    read += oldWrite;
                    write = oldWrite;
                }
            }
            if (newline != 0)
            {
                explicitBreaks++;
                for (int i = 0; i < newline; i++) buffer[write++] = ' ';
                read += newline;
            }
            int end = Array.IndexOf(buffer, '\0');
            string wrapped = new(buffer, 0, end < 0 ? buffer.Length : end);
            lines.Add(trimWrappedLines ? wrapped.TrimEnd(' ') : wrapped);
            Array.Clear(buffer);
            write = 0; width = 0;
        }
        if (read <= caret) { row = lines.Count; column = write; }
        if (write != 0)
        {
            string last = new string(buffer, 0, write);
            if (trimFinalLine) last = last.TrimEnd(' ');
            if (last.Length != 0) lines.Add(last);
        }
        while (lines.Count - 1 > row && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        while (lines.Count <= row) lines.Add("");
        return lines.ToArray();
    }
}
