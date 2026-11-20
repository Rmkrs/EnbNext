namespace EnbNext.Presentation;

public readonly record struct EnbUiInputEvent(ushort Code, ushort Modifiers, float X, float Y, char Character = '\0');

/// <summary>0042E4A0/0042EEC0/0042E8A0: native-sized event ring, retained press
/// modifiers, and one non-modifier-key event per UI frame. Coordinates are normalized
/// by the host's physical client extent minus one (004314C0).</summary>
public sealed class EnbUiInputQueue
{
    private readonly EnbUiInputEvent[] events = new EnbUiInputEvent[255];
    private readonly uint[] pressed = new uint[0x103];
    private int read, write;
    public float X { get; private set; }
    public float Y { get; private set; }
    public bool LeftDown => (pressed[1] & 0x10000) != 0;
    public bool RightDown => (pressed[2] & 0x10000) != 0;
    public ushort HeldModifiers => (ushort)(((pressed[0x10] & 0x10000) != 0 ? 0x100 : 0) |
        ((pressed[0x11] & 0x10000) != 0 ? 0x200 : 0) | ((pressed[0x12] & 0x10000) != 0 ? 0x400 : 0));
    public int Count => (write - read + events.Length) % events.Length;

    public void SetPosition(float x, float y) { X = x; Y = y; }

    public bool Push(EnbUiInputEvent input)
    {
        ushort modifiers = input.Modifiers;
        // Our host translation emits virtual-key identities 0..255 and the
        // two wheel identities. No unchecked native out-of-bounds key access.
        if (input.Code < pressed.Length)
        {
            ref uint state = ref pressed[input.Code];
            if ((state & 0x10000) != 0)
            {
                modifiers = (ushort)state;
                if ((input.Modifiers & 0x1000) != 0) { modifiers |= 0x1000; state = 0; }
            }
            else if ((modifiers & 0x1000) == 0) state = (uint)modifiers | 0x10000;
        }
        int next = (write + 1) % events.Length;
        if (next == read) return false; // state update deliberately precedes overflow
        events[write] = input with { Modifiers = modifiers };
        write = next;
        return true;
    }

    public EnbUiInputEvent TakeFrame()
    {
        while (read != write)
        {
            var input = events[read]; read = (read + 1) % events.Length;
            if (input.Code is not (0x10 or 0x11 or 0x12)) return input;
        }
        return new(0, 0, X, Y);
    }

    public void Clear()
    {
        read = write = 0;
        Array.Clear(pressed);
    }
}
