using System.Net;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class Net7EmoteReceiptTests
{
    [Fact]
    public async Task ReceiptsSurviveEntryCaptureStopAndExcludeText()
    {
        string directory = Path.Combine(Path.GetTempPath(), "emote-receipts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var capture = new Net7WireCapture();
            capture.EnableEmoteReceipts(directory);
            capture.Start(Path.Combine(directory, "entry.jsonl"));
            await capture.StopAsync();
            byte[] gesture = Net7GameplayWire.BuildPlayerNlpStream([new(2, 18)]);
            byte[] outgoing = new ServerUdpFrame(0x005E, 42, 10, Net7GameplayWire.BuildChatstream(42, gesture)).ToWireBytes();
            byte[] incoming = new ServerUdpFrame(0x005F, 42, 11, new byte[] { 8, 0, 1, 42, 0, 0, 0, 2, 18, 0, 0, 8, 0, 0, 0 }).ToWireBytes();
            var local = new IPEndPoint(IPAddress.Loopback, 12000);
            var remote = new IPEndPoint(IPAddress.Loopback, 3501);
            capture.RecordFrame(Net7WireDirection.Outbound, Net7WireTransport.Udp, "routed-udp", local, remote, outgoing);
            capture.RecordFrame(Net7WireDirection.Inbound, Net7WireTransport.Udp, "routed-udp", local, remote, incoming);
            byte[] mixed = new ServerUdpFrame(0x005E, 42, 12, Net7GameplayWire.BuildChatstream(42,
                Net7GameplayWire.BuildPlayerNlpStream([new(2, 18), new(1, 0, "private text")]))).ToWireBytes();
            capture.RecordFrame(Net7WireDirection.Outbound, Net7WireTransport.Udp, "routed-udp", local, remote, mixed);
            incoming[12] = 255; // Malformed size must not throw or be retained.
            capture.RecordFrame(Net7WireDirection.Inbound, Net7WireTransport.Udp, "routed-udp", local, remote, incoming);
            await capture.DisposeAsync();
            var frames = Net7WireCapture.ReadFrames(Assert.Single(Directory.GetFiles(directory, "emotes-*.jsonl")));
            Assert.Equal(2, frames.Count);
            Assert.Equal(outgoing, frames[0].WireBytes);
            Assert.Equal(Net7WireDirection.Inbound, frames[1].Direction);
            Assert.Equal(11u, ServerUdpFrame.Parse(frames[1].WireBytes).Sequence);
        }
        finally { Directory.Delete(directory, true); }
    }
}
