using System.Buffers.Binary;
using System.Diagnostics;
using EnbNext.Rendering;

namespace EnbNext.App;

/// <summary>Owns one hidden x86 codec process and its pipe lifetime.</summary>
internal sealed class RetailBinkDecoder : IEnbRetailVideoDecoder
{
    private readonly Process _process;
    private readonly Stream _input, _output;
    private bool _disposed;
    private readonly CancellationToken _lifetime;
    internal int ProcessId => _process.Id;
    internal EnbRetailVideoFrame Metadata { get; }

    public RetailBinkDecoder(string codecPath, EnbRetailVideoEntry entry, bool enableAudio, CancellationToken lifetime = default)
        : this(codecPath, entry, enableAudio, lifetime, null) { }

    public RetailBinkDecoder(string codecPath, EnbRetailVideoEntry entry, bool enableAudio, CancellationToken lifetime, Action<string>? diagnostic)
    {
        _lifetime = lifetime;
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "EnbNext.BinkHost.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.GetFullPath(codecPath)); start.ArgumentList.Add(Path.GetFullPath(entry.File));
        start.ArgumentList.Add(enableAudio && Environment.GetEnvironmentVariable("ENB_AUDIO_SILENT") != "1" ? "1" : "0");
        start.ArgumentList.Add(entry.Volume.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(EnbNext.Engine.LiveWorldOpenAlAudio.FocusMuted?"1":"0");
        _process = Process.Start(start) ?? throw new IOException("Unable to start the installed video codec.");
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } message) diagnostic?.Invoke(message); };
        _process.BeginErrorReadLine();
        _input = _process.StandardInput.BaseStream; _output = _process.StandardOutput.BaseStream;
        try { Metadata = ReadFrame(); }
        catch { Dispose(); throw; }
    }

    public EnbRetailVideoFrame Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _input.WriteByte((byte)(EnbNext.Engine.LiveWorldOpenAlAudio.FocusMuted?'M':'P')); _input.Flush(); return ReadFrame();
    }

    private EnbRetailVideoFrame ReadFrame()
    {
        byte[] header = new byte[36]; Read(header);
        uint At(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(offset));
        if (At(0) != 0x314b4245 || At(4) > 3) throw new InvalidDataException("Invalid video codec response.");
        if (At(4) == 3) throw new IOException("The installed Bink codec could not open the movie.");
        int width = checked((int)At(8)), height = checked((int)At(12)), bytes = checked((int)At(32));
        if (width <= 0 || height <= 0 || At(24) == 0 || At(28) == 0 ||
            (bytes != 0 && bytes != checked(width * height * 2)) || (At(4) == 1) != (bytes != 0))
            throw new InvalidDataException("Invalid video frame metadata.");
        byte[]? pixels = bytes == 0 ? null : new byte[bytes];
        if (pixels is not null) Read(pixels);
        return new(width, height, At(20), At(16), At(24), At(28), pixels, At(4) == 2);
    }

    private void Read(byte[] buffer)
    {
        // A crashed or hung external decoder must not stall view teardown.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { _output.ReadExactlyAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult(); }
        catch (OperationCanceledException ex) { throw new IOException("Video codec stopped responding.", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // EOF closes Bink normally. A terminated helper can reject the buffered
        // pipe's final flush; that must not prevent handle/process reclamation.
        try { _input.Dispose(); } catch (IOException) { }
        try { _output.Dispose(); } catch (IOException) { }
        try
        {
            if (!_process.WaitForExit(1000))
            {
                try { _process.Kill(); } catch (InvalidOperationException) when (_process.HasExited) { }
                _process.WaitForExit();
            }
        }
        finally { _process.Dispose(); }
    }
}
