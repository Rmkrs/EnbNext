using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EnbNext.Net7.Diagnostics;

public enum Net7WireDirection
{
    Inbound,
    Outbound,
}

public enum Net7WireTransport
{
    Tcp,
    Udp,
}

/// <summary>
/// One raw transport frame from an EnbNext wire capture. <see cref="WireBytes"/>
/// are the exact bytes observed at the socket boundary before semantic packet
/// dispatch, or the exact bytes supplied to the socket for transmission.
/// </summary>
public sealed record Net7WireCaptureFrame(
    long Index,
    long ElapsedMicroseconds,
    DateTimeOffset TimestampUtc,
    Net7WireDirection Direction,
    Net7WireTransport Transport,
    string Channel,
    string LocalEndPoint,
    string RemoteEndPoint,
    byte[] WireBytes);

/// <summary>
/// Lightweight append-only raw wire recorder used for live sector-entry capture.
/// Recording is queued to a background writer so file I/O never blocks a socket
/// receive/send path. The file is JSONL so a captured session can later become a
/// deterministic replay fixture without a proprietary parser.
/// </summary>
public sealed partial class Net7WireCapture : IAsyncDisposable
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();
    private readonly Action<string>? _trace;
    private ActiveCapture? _active;

    public Net7WireCapture(Action<string>? trace = null)
    {
        _trace = trace;
    }

    public bool IsActive => Volatile.Read(ref _active) is not null;

    public long? CurrentElapsedMicroseconds => Volatile.Read(ref _active) is { } state ? ElapsedMicroseconds(state) : null;

    public string? ActivePath => Volatile.Read(ref _active)?.Path;

    public string? LastCompletedPath { get; private set; }

    public void Start(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        lock (_sync)
        {
            if (_active is not null)
                throw new InvalidOperationException($"A Net7 wire capture is already active at '{_active.Path}'.");

            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var stream = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            StreamWriter? writer = null;
            try
            {
                writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };

                var state = new ActiveCapture(fullPath, writer);
                writer.WriteLine(JsonSerializer.Serialize(
                    new CaptureHeaderLine(
                        Type: "header",
                        Format: "enbnext-wire-capture",
                        Version: CurrentFormatVersion,
                        StartedUtc: state.StartedUtc),
                    JsonOptions));
                state.WriterTask = Task.Run(() => WriterLoopAsync(state));
                _active = state;
                _trace?.Invoke($"wire capture started -> {fullPath}");
            }
            catch
            {
                if (writer is not null)
                    writer.Dispose();
                else
                    stream.Dispose();
                throw;
            }
        }
    }

    public void Mark(string name, string? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ActiveCapture? state = Volatile.Read(ref _active);
        if (state is null)
            return;

        long index = state.AllocateIndex();
        var item = CaptureItem.ForMarker(
            index,
            ElapsedMicroseconds(state),
            DateTimeOffset.UtcNow,
            name,
            value);
        state.Channel.Writer.TryWrite(item);
    }

    public void RecordFrame(
        Net7WireDirection direction,
        Net7WireTransport transport,
        string channel,
        IPEndPoint localEndPoint,
        IPEndPoint remoteEndPoint,
        ReadOnlySpan<byte> wireBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(localEndPoint);
        ArgumentNullException.ThrowIfNull(remoteEndPoint);

        RecordEmoteReceipt(direction, transport, channel, localEndPoint, remoteEndPoint, wireBytes);

        ActiveCapture? state = Volatile.Read(ref _active);
        if (state is null)
            return;

        long index = state.AllocateIndex();
        var item = CaptureItem.ForFrame(
            index,
            ElapsedMicroseconds(state),
            DateTimeOffset.UtcNow,
            direction,
            transport,
            channel,
            localEndPoint.ToString(),
            remoteEndPoint.ToString(),
            wireBytes.ToArray());
        state.Channel.Writer.TryWrite(item);
    }

    public async ValueTask StopAsync(string reason = "stopped")
    {
        ActiveCapture? state;
        lock (_sync)
        {
            state = _active;
            if (state is null)
                return;

            long index = state.AllocateIndex();
            state.Channel.Writer.TryWrite(CaptureItem.ForMarker(
                index,
                ElapsedMicroseconds(state),
                DateTimeOffset.UtcNow,
                "capture-stop",
                reason));
            state.Channel.Writer.TryComplete();
            _active = null;
        }

        try
        {
            await state.WriterTask.ConfigureAwait(false);
            LastCompletedPath = state.Path;
            _trace?.Invoke($"wire capture saved -> {state.Path}");
        }
        catch (Exception ex)
        {
            LastCompletedPath = state.Path;
            _trace?.Invoke($"wire capture writer failed for '{state.Path}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync("capture-disposed");
        Net7WireCapture? receipts;
        lock (_sync) { _emoteReceiptsClosed = true; receipts = _emoteReceipts; }
        if (receipts is not null) await receipts.DisposeAsync();
    }

    /// <summary>
    /// Reads only raw frame records from a capture, ordered by their global capture
    /// index. Marker records are intentionally ignored so tests can feed the exact
    /// wire stream directly into transport/replay code.
    /// </summary>
    public static IReadOnlyList<Net7WireCaptureFrame> ReadFrames(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var frames = new List<Net7WireCaptureFrame>();

        foreach (string line in File.ReadLines(EnbNext.Installation.EnbPathCase.Resolve(path)))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("type", out JsonElement type) || type.GetString() != "frame")
                continue;

            string directionText = root.GetProperty("direction").GetString()
                ?? throw new InvalidDataException("Capture frame direction is missing.");
            string transportText = root.GetProperty("transport").GetString()
                ?? throw new InvalidDataException("Capture frame transport is missing.");
            string wireBase64 = root.GetProperty("wireBase64").GetString()
                ?? throw new InvalidDataException("Capture frame wire bytes are missing.");

            frames.Add(new Net7WireCaptureFrame(
                root.GetProperty("index").GetInt64(),
                root.GetProperty("elapsedMicroseconds").GetInt64(),
                root.GetProperty("timestampUtc").GetDateTimeOffset(),
                ParseDirection(directionText),
                ParseTransport(transportText),
                root.GetProperty("channel").GetString() ?? string.Empty,
                root.GetProperty("localEndPoint").GetString() ?? string.Empty,
                root.GetProperty("remoteEndPoint").GetString() ?? string.Empty,
                Convert.FromBase64String(wireBase64)));
        }

        frames.Sort(static (left, right) => left.Index.CompareTo(right.Index));
        return frames;
    }

    private static async Task WriterLoopAsync(ActiveCapture state)
    {
        try
        {
            await foreach (CaptureItem item in state.Channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                if (item.Frame is CaptureFrameItem frame)
                {
                    var line = new CaptureFrameLine(
                        "frame",
                        frame.Index,
                        frame.ElapsedMicroseconds,
                        frame.TimestampUtc,
                        frame.Direction == Net7WireDirection.Inbound ? "in" : "out",
                        frame.Transport == Net7WireTransport.Tcp ? "tcp" : "udp",
                        frame.Channel,
                        frame.LocalEndPoint,
                        frame.RemoteEndPoint,
                        Convert.ToBase64String(frame.WireBytes));
                    await state.Writer.WriteLineAsync(JsonSerializer.Serialize(line, JsonOptions)).ConfigureAwait(false);
                }
                else if (item.Marker is CaptureMarkerLine marker)
                    await state.Writer.WriteLineAsync(JsonSerializer.Serialize(marker, JsonOptions)).ConfigureAwait(false);
            }
        }
        finally
        {
            await state.Writer.FlushAsync().ConfigureAwait(false);
            await state.Writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static long ElapsedMicroseconds(ActiveCapture state)
        => checked((long)(state.Stopwatch.ElapsedTicks * (1_000_000d / Stopwatch.Frequency)));

    private static Net7WireDirection ParseDirection(string value)
        => value switch
        {
            "in" => Net7WireDirection.Inbound,
            "out" => Net7WireDirection.Outbound,
            _ => throw new InvalidDataException($"Unknown capture direction '{value}'."),
        };

    private static Net7WireTransport ParseTransport(string value)
        => value switch
        {
            "tcp" => Net7WireTransport.Tcp,
            "udp" => Net7WireTransport.Udp,
            _ => throw new InvalidDataException($"Unknown capture transport '{value}'."),
        };

    private sealed class ActiveCapture
    {
        public ActiveCapture(string path, StreamWriter writer)
        {
            Path = path;
            Writer = writer;
            StartedUtc = DateTimeOffset.UtcNow;
            Stopwatch = Stopwatch.StartNew();
            Channel = System.Threading.Channels.Channel.CreateUnbounded<CaptureItem>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
            WriterTask = Task.CompletedTask;
        }

        public string Path { get; }
        public StreamWriter Writer { get; }
        public DateTimeOffset StartedUtc { get; }
        public Stopwatch Stopwatch { get; }
        public Channel<CaptureItem> Channel { get; }
        public Task WriterTask { get; set; }

        private long _nextIndex;

        public long AllocateIndex() => Interlocked.Increment(ref _nextIndex);
    }

    private sealed record CaptureHeaderLine(
        string Type,
        string Format,
        int Version,
        DateTimeOffset StartedUtc);

    private sealed record CaptureFrameItem(
        long Index,
        long ElapsedMicroseconds,
        DateTimeOffset TimestampUtc,
        Net7WireDirection Direction,
        Net7WireTransport Transport,
        string Channel,
        string LocalEndPoint,
        string RemoteEndPoint,
        byte[] WireBytes);

    private sealed record CaptureFrameLine(
        string Type,
        long Index,
        long ElapsedMicroseconds,
        DateTimeOffset TimestampUtc,
        string Direction,
        string Transport,
        string Channel,
        string LocalEndPoint,
        string RemoteEndPoint,
        string WireBase64);

    private sealed record CaptureMarkerLine(
        string Type,
        long Index,
        long ElapsedMicroseconds,
        DateTimeOffset TimestampUtc,
        string Name,
        string? Value);

    private sealed record CaptureItem(CaptureFrameItem? Frame, CaptureMarkerLine? Marker)
    {
        public static CaptureItem ForFrame(
            long index,
            long elapsedMicroseconds,
            DateTimeOffset timestampUtc,
            Net7WireDirection direction,
            Net7WireTransport transport,
            string channel,
            string localEndPoint,
            string remoteEndPoint,
            byte[] wireBytes)
            => new(
                new CaptureFrameItem(
                    index,
                    elapsedMicroseconds,
                    timestampUtc,
                    direction,
                    transport,
                    channel,
                    localEndPoint,
                    remoteEndPoint,
                    wireBytes),
                null);

        public static CaptureItem ForMarker(
            long index,
            long elapsedMicroseconds,
            DateTimeOffset timestampUtc,
            string name,
            string? value)
            => new(
                null,
                new CaptureMarkerLine(
                    "marker",
                    index,
                    elapsedMicroseconds,
                    timestampUtc,
                    name,
                    value));
    }
}
