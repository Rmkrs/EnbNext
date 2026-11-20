namespace EnbNext.Presentation;

/// <summary>Independent visible groups plus retail's momentary bank shift.</summary>
public sealed class EnbShortcutPages
{
    private string? _owner;
    private readonly int[] _groups = new int[2];
    private readonly int[] _counts = [1, 1];
    public bool Shifted { get; private set; }
    public int Group(int bar) => _groups[bar];
    public bool CanToggle(int bar) => _counts[bar] > 1;
    public void Update(EnbSceneShortcutSnapshot? snapshot)
    {
        string? owner = snapshot?.Saved.CharacterKey;
        if (_owner != owner) { Array.Clear(_groups); Shifted = false; _owner = owner; }
        for (int bar = 0; bar < 2; bar++)
        {
            // Both banks are destinations even before they contain assignments.
            _counts[bar] = snapshot is not null ? 2 : 1;
            _groups[bar] %= _counts[bar];
        }
    }
    public void Toggle(int bar) => _groups[bar] = (_groups[bar] + 1) % _counts[bar];
    /// <summary>Next Modern UI shows both banks at once; its keys use bank A unless the momentary shift is held.</summary>
    public void ShowFirstGroups() { for (int bar = 0; bar < 2; bar++) _groups[bar] = Shifted ? 1 % _counts[bar] : 0; }
    public bool SetShift(bool shifted)
    {
        if (Shifted == shifted) return false;
        // 00610980: +1 on Alt-down, -1 on Alt-up. With two groups both
        // are a toggle; starting on page B must not force page B again.
        Shifted = shifted;
        Toggle(0); Toggle(1);
        return true;
    }
}
