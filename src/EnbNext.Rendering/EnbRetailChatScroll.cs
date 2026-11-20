namespace EnbNext.Rendering;

/// <summary>The retained offset and visible-row mutation in retail 00682080.</summary>
public static class EnbRetailChatScroll
{
    public static (int Offset, int Shown, int Change) Move(int count, int visible,
        int offset, int shown, int amount, bool clampToPage = false)
    {
        if (visible == 0 || amount == 0) return (offset, shown, 0);
        int previous = count - shown - offset;
        if (clampToPage) amount = Math.Clamp(amount, -visible, visible);
        offset += amount;
        while (offset > 0 && shown < visible) { offset--; shown++; }
        offset = Math.Min(offset, count - shown);
        while (offset < 0)
        {
            offset++;
            if (shown != 0) shown--;
        }
        return (offset, shown, count - offset - shown - previous);
    }
}
