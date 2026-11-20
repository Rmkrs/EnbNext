using System.Net;
using System.Net.Sockets;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.Transport;

namespace EnbNext.Net7.Tests;

public sealed class Net7WireCaptureTests
{
    [Fact]
    public async Task CaptureFile_RoundTripsExactRawFrames()
    {
        string path = Path.Combine(Path.GetTempPath(), $"enbnext-capture-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var capture = new Net7WireCapture();
            capture.Start(path);
            capture.Mark("test-start", "fixture");

            byte[] first = [0x04, 0x00, 0x34, 0x12];
            byte[] second = new ServerUdpFrame(
                0x2016,
                0x11223344,
                77,
                new byte[] { 1, 2, 3, 4, 5 }).ToWireBytes();

            capture.RecordFrame(
                Net7WireDirection.Outbound,
                Net7WireTransport.Tcp,
                "account-tcp",
                new IPEndPoint(IPAddress.Loopback, 12345),
                new IPEndPoint(IPAddress.Loopback, 3807),
                first);
            capture.RecordFrame(
                Net7WireDirection.Inbound,
                Net7WireTransport.Udp,
                "routed-udp",
                new IPEndPoint(IPAddress.Loopback, 23456),
                new IPEndPoint(IPAddress.Loopback, 3501),
                second);

            await capture.StopAsync("test-complete");

            IReadOnlyList<Net7WireCaptureFrame> frames = Net7WireCapture.ReadFrames(path);
            Assert.Equal(2, frames.Count);
            Assert.Equal(Net7WireDirection.Outbound, frames[0].Direction);
            Assert.Equal(Net7WireTransport.Tcp, frames[0].Transport);
            Assert.Equal("account-tcp", frames[0].Channel);
            Assert.Equal(first, frames[0].WireBytes);
            Assert.Equal(Net7WireDirection.Inbound, frames[1].Direction);
            Assert.Equal(Net7WireTransport.Udp, frames[1].Transport);
            Assert.Equal("routed-udp", frames[1].Channel);
            Assert.Equal(second, frames[1].WireBytes);
            Assert.True(frames[0].Index < frames[1].Index);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RoutedCapture_RecordsRawMalformedDatagramBeforeAdmissionDropsIt()
    {
        string path = Path.Combine(Path.GetTempPath(), $"enbnext-capture-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var capture = new Net7WireCapture();
            capture.Start(path);

            var state = new Net7TransportState();
            await using var channel = await RoutedSectorUdpChannel.CreateAsync(
                "127.0.0.1",
                state,
                wireCapture: capture);
            using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var target = new IPEndPoint(IPAddress.Loopback, channel.LocalEndPoint.Port);

            byte[] malformed = [0x01, 0x02, 0x03, 0x04, 0x05];
            byte[] admitted = new ServerUdpFrame(
                0x1001,
                0,
                0,
                ReadOnlyMemory<byte>.Empty).ToWireBytes();

            await server.SendAsync(malformed, target);
            await server.SendAsync(admitted, target);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            (ServerUdpFrame frame, _) = await channel.ReceiveAsync(cts.Token);
            Assert.Equal(0x1001, frame.Opcode);

            await capture.StopAsync("test-complete");
            IReadOnlyList<Net7WireCaptureFrame> frames = Net7WireCapture.ReadFrames(path)
                .Where(record => record.Direction == Net7WireDirection.Inbound)
                .ToArray();

            Assert.True(frames.Count >= 2);
            Assert.Equal(malformed, frames[0].WireBytes);
            Assert.Equal(admitted, frames[1].WireBytes);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
