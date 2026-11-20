using EnbNext.Net7.Gameplay;

namespace EnbNext.Client;

/// <summary>Owned lifetime and actions of the authored OPT02A modal (005CE830/005CEB00).
/// Presentation adapters retain their existing admission and parent-close ordering.</summary>
public sealed class EnbQuitMenuState(Func<uint?> session)
{
    private uint _character;
    public uint Character => _character;
    public int Generation { get; private set; }
    public ulong Handoff { get; private set; }
    public bool IsOpen { get; private set; }
    public bool IsSending { get; private set; }
    public Func<uint, ulong, Net7LogoffDestination, Task<bool>>? LogoffRequested { get; set; }
    public Action<bool>? CloseRequested { get; set; }
    public Action<string>? SoundRequested { get; set; }
    public Action? Changed { get; set; }

    // 005CE830: complete-word Escape / Alt-Q / Alt-C / Alt-L bindings.
    public static string? InputAction(ushort code, ushort modifiers) => (code, modifiers) switch
    {
        (0x1b, 0) => "quit/EXIT", (0x51, 0x400) => "quit/BTTN00",
        (0x43, 0x400) => "quit/BTTN01", (0x4c, 0x400) => "quit/BTTN02", _ => null,
    };

    public void Open(uint character, ulong handoff)
    {
        _character = character; Handoff = handoff; Generation++;
        IsSending = false; IsOpen = true;
    }

    public void Retire()
    {
        IsOpen = false; Generation++; IsSending = false;
    }

    public async Task ActivateAsync(string key, bool interactive)
    {
        uint character = _character;
        if (!IsOpen || !interactive || session() != character || IsSending) return;
        if (key is "quit/EXIT" or "quit/BTTN03")
        { SoundRequested?.Invoke("Roll_Select02b.wav"); CloseRequested?.Invoke(false); return; }
        Net7LogoffDestination? destination = key switch
        {
            "quit/BTTN00" => Net7LogoffDestination.Desktop,
            "quit/BTTN01" => Net7LogoffDestination.CharacterSelection,
            "quit/BTTN02" => Net7LogoffDestination.LoginScreen,
            _ => null,
        };
        if (destination is null || LogoffRequested is not { } request) return;
        int generation = Generation;
        IsSending = true;
        SoundRequested?.Invoke("Roll_Select02b.wav");
        try
        {
            bool accepted = await request(character, Handoff, destination.Value);
            if (generation != Generation || !IsOpen || session() != character) return;
            if (accepted) CloseRequested?.Invoke(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        { /* Retirement/failure never triggers a local destination or retry. */ }
        finally
        {
            if (generation == Generation) { IsSending = false; Changed?.Invoke(); }
        }
    }
}
