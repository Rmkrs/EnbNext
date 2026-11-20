namespace EnbNext.Engine;

/// <summary>Retail0084AA10/0084AAD0 bounded editor; OS text translation is a host boundary.</summary>
public enum EnbRetailEditorCharacterClass { Printable, Digits, Letters }

public sealed partial class EnbRetailTextEditor(int maxLength, bool multiline = false) : IDisposable
{
    private char[] _buffer = new char[checked(maxLength + 2)];
    private string? _text = "";
    private int _length;
    private int _capacity = maxLength;
    private int _caret;
    public int MaxLength
    {
        get => _capacity;
        set
        {
            if (value == _capacity) return;
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            string retained = Text;
            _buffer = new char[checked(value + 2)]; _capacity = value;
            Text = retained;
        }
    }
    internal int StoredLength => _length;
    public bool Multiline { get; } = multiline;
    public EnbRetailEditorCharacterClass CharacterClass { get; set; }
    public bool InsertMode { get; set; } = true;
    private bool focused;
    private EnbSingleLineInputOwner? singleLineOwner;
    public bool IsFocused
    {
        get=>focused;
        set { if(singleLineOwner is {} owner)owner.SetFocus(this,value);else focused=value; }
    }
    internal void SetFocusStorage(bool value)=>focused=value;
    internal void BindSingleLineOwner(EnbSingleLineInputOwner owner)
    {
        if(singleLineOwner==owner)return;
        bool active=focused;singleLineOwner?.SetFocus(this,false);singleLineOwner=owner;
        if(active)owner.SetFocus(this,true);
    }
    public bool Entered { get; set; }
    public string Text
    {
        get => _text ??= new string(_buffer, 0, StringLength(0));
        set
        {
            Array.Clear(_buffer);
            int terminator = value.IndexOf('\0');
            _length = Math.Min(terminator < 0 ? value.Length : terminator, MaxLength);
            value.AsSpan(0, _length).CopyTo(_buffer);
            _caret = _length; _text = value[.._length]; Entered = true;
        }
    }
    public int CaretIndex { get => _caret; set => _caret = Math.Clamp(value, 0, _length); }
    private int StringLength(int start)
    {
        int end = start;
        while (end < _buffer.Length && _buffer[end] != '\0') end++;
        return end - start;
    }
    private void Changed() { _text = null; Entered = true; }

    //0084B020: bulk insertion bypasses keyboard character classification.
    public bool InsertBulk(string value)
    {
        int terminator = value.IndexOf('\0');
        int count = terminator < 0 ? value.Length : terminator;
        if (count == 0) return false;
        if (InsertMode)
        {
            count = Math.Min(count, MaxLength - _length);
            if (count == 0) return false;
            Array.Copy(_buffer, _caret, _buffer, _caret + count, StringLength(_caret) + 1);
            value.AsSpan(0, count).CopyTo(_buffer.AsSpan(_caret));
            _caret += count; _length += count; Changed();
            return true;
        }
        for (int i = 0; i < count; i++)
        {
            if (_length >= MaxLength)
            { _buffer[_caret] = '\0'; Changed(); return false; }
            _buffer[_caret++] = value[i];
            //0084B12F..137 compares the advanced caret with retained length.
            if (_caret >= _length) { _length++; _buffer[_caret] = '\0'; }
        }
        Changed(); return true;
    }

    //0084B270 forces insertion without changing the retained editing mode.
    public void Insert(string value)
    {
        bool mode = InsertMode; InsertMode = true;
        try { InsertBulk(value); }
        finally { InsertMode = mode; }
    }

    public bool Key(ushort key, ushort modifiers = 0, char translated = '\0')
    {
        if ((modifiers & 0x1000) != 0) return false;
        switch (key)
        {
            case 0x23: _caret = _length; return true;
            case 0x24: _caret = 0; return true;
            case 0x25:
                if (_caret > 0) do { _caret--; } while ((modifiers & 0x200) != 0 && _caret > 0 && !Space(_buffer[_caret]));
                return true;
            case 0x27:
                if (_caret < _length) do { _caret++; } while ((modifiers & 0x200) != 0 && _caret < _length && !Space(_buffer[_caret]));
                return true;
            case 0x2d: InsertMode = !InsertMode; return true;
            case 8:
            case 0x2e:
                if (key == 8 || _caret == _length)
                { if (_caret == 0) return true; _caret--; }
                Array.Copy(_buffer, _caret + 1, _buffer, _caret, StringLength(_caret));
                _length--; Changed(); return true;
            case 1 or 2 or 9 or 12 or 16 or 17 or 18 or 19 or 20 or 27 or 33 or 34 or 38 or 40 or 44:
            case >= 0x70 and <= 0x7b:
            case 0x90 or 0x91: return false;
        }
        if ((modifiers & 0x700) != 0 && (modifiers & 0x100) == 0) return false;
        if (key == 13)
        { if (!Multiline) return false; translated = '\n'; }
        else
        {
            if (translated is < ' ' or > '~') return false; //0084AAD0: C-locale print before class.
            if (CharacterClass == EnbRetailEditorCharacterClass.Digits && !char.IsAsciiDigit(translated)) return false;
            if (CharacterClass == EnbRetailEditorCharacterClass.Letters && !char.IsAsciiLetter(translated)) return false;
        }
        if (_length == MaxLength) return true;
        if (InsertMode)
        {
            Array.Copy(_buffer, _caret, _buffer, _caret + 1, StringLength(_caret) + 1);
            _length++;
        }
        else if (_caret == _length) { _buffer[_caret + 1] = '\0'; _length++; }
        _buffer[_caret++] = translated; Changed(); return true;
    }
    private static bool Space(char c) => c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f';
}
