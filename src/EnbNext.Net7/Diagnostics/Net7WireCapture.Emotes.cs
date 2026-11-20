using System.Buffers.Binary;
using System.Net;

namespace EnbNext.Net7.Diagnostics;

public sealed partial class Net7WireCapture
{
    private string? _emoteReceiptDirectory;
    private Net7WireCapture? _emoteReceipts;
    private bool _emoteReceiptsClosed;
    private int _emoteReceiptCount;

    /// <summary>Bounded, gesture-only receipts survive the short login capture window.</summary>
    public void EnableEmoteReceipts(string? directory)
    {
        lock (_sync) _emoteReceiptDirectory = string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    private void RecordEmoteReceipt(Net7WireDirection direction, Net7WireTransport transport,
        string channel, IPEndPoint local, IPEndPoint remote, ReadOnlySpan<byte> wire)
    {
        if (transport != Net7WireTransport.Udp || !IsGestureFrame(wire)) return;
        lock (_sync)
        {
            if (_emoteReceiptDirectory is null || _emoteReceiptsClosed || _emoteReceiptCount >= 512) return;
            try
            {
                if (_emoteReceipts is null)
                {
                    var capture = new Net7WireCapture(_trace);
                    capture.Start(Path.Combine(_emoteReceiptDirectory, $"emotes-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.enbcap.jsonl"));
                    _emoteReceipts = capture;
                }
                _emoteReceipts.RecordFrame(direction, transport, channel, local, remote, wire);
                _emoteReceiptCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _emoteReceiptsClosed = true;
                _trace?.Invoke($"emote receipts disabled: {ex.Message}");
            }
        }
    }

    // Capture only fully bounded, text-free NLP streams containing a manual gesture.
    // No credentials, ordinary chat, or mixed text/gesture payloads are retained.
    private static bool IsGestureFrame(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < 19 || BinaryPrimitives.ReadUInt16LittleEndian(wire) != wire.Length) return false;
        ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(wire[2..]);
        var payload = wire[12..];
        int length;
        if (opcode == 0x005E)
        {
            if (payload.Length < 9) return false;
            length = BinaryPrimitives.ReadUInt16LittleEndian(payload[5..]);
            if (payload.Length != 9 + length || payload[^2] != 0 || payload[^1] != 0) return false;
        }
        else if (opcode == 0x005F)
        {
            length = BinaryPrimitives.ReadUInt16LittleEndian(payload);
            if (payload.Length != 7 + length) return false;
        }
        else return false;
        var stream = payload.Slice(7, length);
        bool gesture = false;
        for (int offset = 0; offset + 4 <= stream.Length; offset += 4)
        {
            var record = stream.Slice(offset, 4);
            if (record[2] != 0 || record[3] != 0) return false;
            if (record[0] == 8) return gesture && record[1] == 0 && offset + 4 == stream.Length;
            if (record[0] == 2) gesture = true;
            else if (record[0] != 0) return false;
        }
        return false;
    }
}
