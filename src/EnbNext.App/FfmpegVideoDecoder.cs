using System.Diagnostics;
using System.Globalization;
using EnbNext.Rendering;
using Silk.NET.OpenAL;

namespace EnbNext.App;

/// <summary>The intro movie on platforms without the retail Bink codec host.
/// FFmpeg decodes the installed Bink file: video as RGB565 frames paced to the
/// movie's frame rate (as BinkWait paces them), audio as PCM16 played on a
/// short-lived OpenAL device with the intro volume and focus mute.</summary>
internal sealed class FfmpegVideoDecoder : IEnbRetailVideoDecoder
{
    private readonly Process _video;
    private readonly Stream _pixels;
    private readonly int _width, _height, _frameBytes;
    private readonly uint _rate, _divisor, _total;
    private readonly Stopwatch _clock = new();
    private readonly MovieAudio? _audio;
    private uint _shown;
    private bool _ended, _disposed;

    public FfmpegVideoDecoder(EnbRetailVideoEntry entry, bool enableAudio, CancellationToken lifetime, Action<string>? diagnostic)
    {
        string file = EnbNext.Installation.EnbPathCase.Resolve(Path.GetFullPath(entry.File));
        if (!File.Exists(file)) throw new IOException("The intro movie is not installed.");
        var probe = Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height,r_frame_rate:format=duration", "-of", "default=nw=1", file]);
        string Field(string name) => probe.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? "";
        if (!int.TryParse(Field("width"), out _width) || !int.TryParse(Field("height"), out _height) || _width <= 0 || _height <= 0)
            throw new InvalidDataException("FFmpeg did not report the movie size.");
        string[] rate = Field("r_frame_rate").Split('/');
        if (rate.Length != 2 || !uint.TryParse(rate[0], out _rate) || !uint.TryParse(rate[1], out _divisor) || _rate == 0 || _divisor == 0)
            throw new InvalidDataException("FFmpeg did not report the movie frame rate.");
        _total = double.TryParse(Field("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? (uint)Math.Round(seconds * _rate / _divisor) : uint.MaxValue;
        _frameBytes = checked(_width * _height * 2);
        _video = Start("ffmpeg", ["-v", "error", "-nostdin", "-i", file, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "rgb565le", "pipe:1"], diagnostic);
        _pixels = _video.StandardOutput.BaseStream;
        if (enableAudio && Environment.GetEnvironmentVariable("ENB_AUDIO_SILENT") != "1")
            _audio = new MovieAudio(file, entry.Volume, lifetime, diagnostic);
    }

    public EnbRetailVideoFrame Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_clock.IsRunning) _clock.Start();
        _audio?.Update(_clock.Elapsed);
        if (_ended || _shown >= _total) return Frame(null, true);
        // Frames due so far, the first immediately; catch up by skipping.
        uint due = (uint)Math.Min(uint.MaxValue, Math.Floor(_clock.Elapsed.TotalSeconds * _rate / _divisor) + 1);
        if (_shown >= due) return Frame(null, false);
        byte[]? pixels = null;
        while (_shown < due)
        {
            byte[] next = new byte[_frameBytes];
            try { _pixels.ReadExactly(next); }
            catch (EndOfStreamException) { _ended = true; break; }
            pixels = next; _shown++;
        }
        return pixels is null ? Frame(null, true) : Frame(pixels, false);
    }

    private EnbRetailVideoFrame Frame(byte[]? pixels, bool complete) =>
        new(_width, _height, Math.Max(1, _shown), _total == uint.MaxValue ? _shown : _total, _rate, _divisor, pixels, complete);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _audio?.Dispose();
        try { if (!_video.HasExited) _video.Kill(); } catch (InvalidOperationException) { }
        _video.Dispose();
    }

    private static Process Start(string program, string[] arguments, Action<string>? diagnostic)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("FFmpeg could not be started."); }
        catch (System.ComponentModel.Win32Exception)
        { throw new IOException("Playing the intro movie needs FFmpeg (install the ffmpeg package)."); }
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } message) diagnostic?.Invoke("FFmpeg: " + message); };
        process.BeginErrorReadLine();
        return process;
    }

    private static string Run(string program, string[] arguments)
    {
        using var process = Start(program, arguments, null);
        string output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new IOException("FFmpeg did not finish reading the movie."); }
        return output;
    }

    /// <summary>The soundtrack, decoded in the background and started at the
    /// video clock's position once ready.</summary>
    private sealed class MovieAudio : IDisposable
    {
        private readonly Task<byte[]> _decode;
        private readonly float _gain;
        private readonly Action<string>? _diagnostic;
        private ALContext? _alc;
        private AL? _al;
        private unsafe Device* _device;
        private unsafe Context* _context;
        private uint _source, _buffer;
        private bool _started, _failed;

        public MovieAudio(string file, int volume, CancellationToken lifetime, Action<string>? diagnostic)
        {
            _gain = Math.Clamp(volume / 32768f, 0f, 1f);
            _diagnostic = diagnostic;
            _decode = Task.Run(() => DecodeAsync(file, lifetime, diagnostic), lifetime);
        }

        private static async Task<byte[]> DecodeAsync(string file, CancellationToken lifetime, Action<string>? diagnostic)
        {
            using var process = Start("ffmpeg", ["-v", "error", "-nostdin", "-i", file, "-map", "0:a:0", "-f", "s16le", "-ac", "2", "-ar", "44100", "pipe:1"], diagnostic);
            using var pcm = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(pcm, lifetime);
            await process.WaitForExitAsync(lifetime);
            return pcm.ToArray();
        }

        public unsafe void Update(TimeSpan position)
        {
            if (_failed) return;
            try
            {
                if (!_started)
                {
                    if (!_decode.IsCompleted) return;
                    _started = true;
                    byte[] pcm = _decode.Result;
                    if (pcm.Length == 0) return;
                    _alc = ALContext.GetApi(soft: true);
                    _al = AL.GetApi(soft: true);
                    _device = _alc.OpenDevice("");
                    if (_device == null) throw new IOException("No audio output device.");
                    _context = _alc.CreateContext(_device, null);
                    if (_context == null || !_alc.MakeContextCurrent(_context)) throw new IOException("No audio context.");
                    _buffer = _al.GenBuffer();
                    fixed (byte* data = pcm) _al.BufferData(_buffer, BufferFormat.Stereo16, data, pcm.Length, 44100);
                    _source = _al.GenSource();
                    _al.SetSourceProperty(_source, SourceInteger.Buffer, (int)_buffer);
                    _al.SetSourceProperty(_source, SourceFloat.SecOffset, (float)position.TotalSeconds);
                    _al.SetSourceProperty(_source, SourceFloat.Gain, Gain());
                    _al.SourcePlay(_source);
                }
                else if (_al is not null && _source != 0) _al.SetSourceProperty(_source, SourceFloat.Gain, Gain());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or AggregateException or DllNotFoundException)
            {
                _failed = true;
                _diagnostic?.Invoke("Intro audio unavailable (" + ex.GetBaseException().Message + "); continuing video.");
                Release();
            }
        }

        private float Gain() => EnbNext.Engine.LiveWorldOpenAlAudio.FocusMuted || Environment.GetEnvironmentVariable("ENB_AUDIO_MUTE_OUTPUT") == "1" ? 0f : _gain;

        private unsafe void Release()
        {
            if (_al is not null && _alc is not null && _context != null && _alc.MakeContextCurrent(_context))
            {
                if (_source != 0) { _al.SourceStop(_source); _al.DeleteSource(_source); }
                if (_buffer != 0) _al.DeleteBuffer(_buffer);
            }
            _source = _buffer = 0;
            if (_alc is not null)
            {
                _alc.MakeContextCurrent(null);
                if (_context != null) _alc.DestroyContext(_context);
                if (_device != null) _alc.CloseDevice(_device);
            }
            _context = null; _device = null;
            _al?.Dispose(); _alc?.Dispose(); _al = null; _alc = null;
        }

        public void Dispose() => Release();
    }
}
