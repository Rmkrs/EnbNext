using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbRetailVideoEntry(string File, int Volume = 0x8000);
public sealed record EnbRetailVideoFrame(int Width, int Height, uint CurrentFrame, uint TotalFrames,
    uint FrameRate, uint FrameRateDivisor, byte[]? Rgb565, bool Complete = false);

public interface IEnbRetailVideoDecoder : IDisposable
{
    EnbRetailVideoFrame Poll();
}

/// <summary>76C140/76C230/76C430/76C4D0's playlist, with local failure containment.</summary>
public sealed class EnbRetailVideoRuntime : IDisposable
{
    private readonly IReadOnlyList<EnbRetailVideoEntry> _entries;
    private readonly Func<EnbRetailVideoEntry, IEnbRetailVideoDecoder?> _open;
    private readonly Func<string, IReadOnlyList<EnbRetailVideoSubtitle>> _subtitles;
    private readonly Func<uint>? _renderClock;
    private IEnbRetailVideoDecoder? _decoder;
    private readonly object _gate = new();
    private uint _firstRender;
    private bool _rendered, _disposed;
    public int Index { get; private set; }
    public bool Ended { get; private set; }
    public bool Skipped { get; private set; }
    public string? Failure { get; private set; }
    public EnbRetailVideoFrame? Frame { get; private set; }
    public EnbRetailVideoSubtitleState Subtitles { get; private set; } = new([]);

    public EnbRetailVideoRuntime(IReadOnlyList<EnbRetailVideoEntry> entries,
        Func<EnbRetailVideoEntry, IEnbRetailVideoDecoder?> open,
        Func<string, IReadOnlyList<EnbRetailVideoSubtitle>> subtitles, Func<uint>? renderClock = null)
    { _entries = entries; _open = open; _subtitles = subtitles; _renderClock = renderClock; Ended = entries.Count == 0; }

    public void Tick(uint wallClock, bool active = true)
    {
        lock (_gate)
        {
            if (Ended || _disposed || !active) return;
            try
            {
                if (_decoder is null)
                {
                    _decoder = _open(_entries[Index]);
                    if (_decoder is null) { Advance(); return; }
                    try { Subtitles = new(_subtitles(_entries[Index].File)); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException)
                    { Failure = ex.Message; Subtitles = new([]); }
                }
                // Retail opens on task activation, before its first render.
                // Lazy codec/subtitle loading must not consume the skip gate.
                uint renderTime = _renderClock?.Invoke() ?? wallClock;
                var frame = _decoder.Poll();
                if (frame.Complete) { Advance(); return; }
                if (!_rendered) { _rendered = true; _firstRender = renderTime; }
                // A16870 advances the codec before updating subtitle time.
                Frame = frame.Rgb565 is null && Frame is { } previous ? frame with { Rgb565 = previous.Rgb565 } : frame;
                Subtitles.Update(EnbRetailVideoSubtitleState.Clock(frame.CurrentFrame, frame.FrameRate, frame.FrameRateDivisor));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OverflowException or System.ComponentModel.Win32Exception)
            { Failure = ex.Message; Advance(); }
        }
    }

    public bool Input(uint key, uint flags, uint wallClock)
    {
        lock (_gate)
        {
            if (Ended || !_rendered || key == 0 || (flags & 0x1000) != 0 || unchecked(wallClock - _firstRender) < 3000) return false;
            Skipped = Ended = true; Release(); return true; // Skip removes the entire task, not one entry.
        }
    }
    private void Advance() { Release(); Index++; Ended = Index >= _entries.Count; _rendered = false; }
    private void Release() { _decoder?.Dispose(); _decoder = null; Frame = null; Subtitles = new([]); }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = Ended = true; Release(); } }
}

/// <summary>A16BA3..A16C58 and A170E0: file-order events and retained geometry.</summary>
public sealed class EnbRetailVideoSubtitleState(IReadOnlyList<EnbRetailVideoSubtitle> events)
{
    public int NextIndex { get; private set; }
    public EnbRetailVideoSubtitle? Current { get; private set; }
    public EnbRetailVideoSubtitle? Displayed { get; private set; }
    public void Update(uint clock)
    {
        while (NextIndex < events.Count && events[NextIndex].Time <= clock) Displayed = Current = events[NextIndex++];
        if (Current is { } current && unchecked(current.Time + current.Duration) <= clock) Current = null;
    }
    public static uint Clock(uint frame, uint rate, uint divisor)
    {
        if (rate == 0 || divisor == 0) throw new InvalidDataException("Invalid video frame rate.");
        uint cs = unchecked((uint)(unchecked(100u * frame) / (rate / (double)divisor)));
        uint c = cs / 100, s = c % 60, m = c / 3600, h = m / 60;
        return unchecked((cs % 100) * 60 / 100 + 60 * (s + 60 * (m + 60 * h)));
    }
}
