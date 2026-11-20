using System.Globalization;
using EnbNext.Net7;

namespace EnbNext.Engine;

/// <summary>
/// Next: the connection indicator, the micro menu's first button (Modern UI option, off by default). Its signal bars
/// show the connection's state in colour and in number; its tooltip gives the latency, the server's traffic and the
/// frame rate. It reads the session's own traffic and sends nothing (retail's ping view never had a working source).
/// </summary>
internal sealed partial class EnbModernHud
{
    /// <summary>Latency below <see cref="GoodLatency"/> is good, below <see cref="FairLatency"/> fair, otherwise poor.
    /// A time request still unanswered after <see cref="SlowAnswer"/> makes it at least fair, after <see cref="LateAnswer"/>
    /// poor; after <see cref="LostAnswer"/> it counts as lost and no longer colours the indicator.
    /// The Net7 server sends on a beat of about 125 ms, so its answer waits up to two beats on top of the network's round trip
    /// (live session 17 Nov 2026: 110 to 251 ms from Europe); the limits leave room for that.</summary>
    public const long GoodLatency = 400, FairLatency = 800, SlowAnswer = 2000, LateAnswer = 5000, LostAnswer = 30000;
    /// <summary>An awaited dock, gate, launch or conversation answer this late counts as lost: the tooltip offers a reconnect.</summary>
    public const long StuckAnswer = 20000;
    private const long ConnectionRefresh = 500, RateWindow = 1000;

    /// <summary>Next Modern UI option "Connection indicator".</summary>
    internal bool ConnectionShown
    {
        get => connectionShown;
        set { if (connectionShown != value) { connectionShown = value; cached = null; cachedTooltip = null; } }
    }
    private bool connectionShown;
    /// <summary>The session's connection reading (null without a session).</summary>
    internal Func<Net7ConnectionSample?>? ConnectionSource { get; set; }
    /// <summary>The renderer's frame rate reading.</summary>
    internal Func<LivePerformanceSnapshot?>? PerformanceSource { get; set; }
    /// <summary>0: nothing measured yet; 1: good; 2: fair; 3: poor.</summary>
    internal int ConnectionLevel => connectionLevel;
    internal string ConnectionText => connectionTip;
    private int connectionLevel;
    private string connectionTip = "";
    private long connectionRead = long.MinValue / 2, rateTick;
    private long rateReceived = -1, rateSent;
    private double receivedRate, sentRate;

    /// <summary>Refreshes the reading twice a second; the button redraws when its level changes, the tooltip when its text does.</summary>
    internal void UpdateConnection()
    {
        if (!connectionShown) return;
        long now = Environment.TickCount64;
        if (now - connectionRead < ConnectionRefresh) return;
        connectionRead = now;
        var sample = ConnectionSource?.Invoke();
        if (sample is { } s)
        {
            if (rateReceived < 0 || s.BytesReceived < rateReceived || s.BytesSent < rateSent) { rateReceived = s.BytesReceived; rateSent = s.BytesSent; rateTick = now; receivedRate = sentRate = 0; }
            else if (now - rateTick >= RateWindow)
            {
                double seconds = (now - rateTick) / 1000d;
                receivedRate = (s.BytesReceived - rateReceived) / seconds; sentRate = (s.BytesSent - rateSent) / seconds;
                rateReceived = s.BytesReceived; rateSent = s.BytesSent; rateTick = now;
            }
        }
        else { rateReceived = -1; receivedRate = sentRate = 0; }
        int level = sample is { } reading ? Level(reading) : 0;
        string text = ConnectionTip(sample, receivedRate, sentRate, PerformanceSource?.Invoke());
        if (level != connectionLevel) { connectionLevel = level; cached = null; }
        if (text != connectionTip) { connectionTip = text; if (hovered == "next/Status") cachedTooltip = null; }
    }

    internal static int Level(Net7ConnectionSample sample)
    {
        int level = sample.LatencyMilliseconds is long latency ? latency < GoodLatency ? 1 : latency < FairLatency ? 2 : 3 : 0;
        if (sample.PendingMilliseconds is long waiting && waiting < LostAnswer)
            level = Math.Max(level, waiting >= LateAnswer ? 3 : waiting >= SlowAnswer ? 2 : level);
        if (sample.Awaited != Net7AwaitedAnswer.None)
            level = Math.Max(level, sample.AwaitedMilliseconds >= LateAnswer ? 3 : sample.AwaitedMilliseconds >= SlowAnswer ? 2 : level);
        return level;
    }

    internal static string ConnectionTip(Net7ConnectionSample? sample, double received, double sent, LivePerformanceSnapshot? performance)
    {
        var text = new System.Text.StringBuilder("Connection\n");
        if (sample is not { } s) text.Append("Not connected\n");
        else
        {
            text.Append(s.LatencyMilliseconds is long latency
                ? "Latency: " + latency.ToString(CultureInfo.InvariantCulture) + " ms, measured " + Ago(s.LatencyAgeMilliseconds) + "\n"
                : "Latency: not measured yet\n");
            if (s.PendingMilliseconds is long waiting)
                text.Append(waiting >= LostAnswer ? "The server did not answer the last measurement\n"
                    : waiting >= SlowAnswer ? "Waiting " + Seconds(waiting) + " for the server to answer\n" : "");
            if (s.Awaited != Net7AwaitedAnswer.None && s.AwaitedMilliseconds >= SlowAnswer)
            {
                text.Append("Waiting ").Append(Seconds(s.AwaitedMilliseconds)).Append(" for the server to ").Append(Awaiting(s.Awaited)).Append('\n');
                if (s.ServerAnswersWhileAwaiting == true)
                    text.Append("The server still answers").Append(s.LatencyMilliseconds is long answered ? " (latency " + answered.ToString(CultureInfo.InvariantCulture) + " ms)" : "")
                        .Append(",\nso the delay is on the server's side\n");
                else if (s.ServerAnswersWhileAwaiting == false)
                    text.Append("The server does not answer at all: your internet\nconnection or the server may be down\n");
                else if (s.AwaitedMilliseconds >= Net7ConnectionMeter.ProbeAfter)
                    text.Append("Checking whether the server still answers\n");
                if (s.AwaitedMilliseconds >= StuckAnswer) text.Append("Type /reconnect to log in again\n");
            }
            text.Append("Last data from the server: ").Append(s.LastReceivedAgeMilliseconds is long age ? Ago(age) : "none yet").Append('\n');
            text.Append("Receiving ").Append(Rate(received)).Append(" per second\n");
            text.Append("Sending ").Append(Rate(sent)).Append(" per second\n");
        }
        if (performance is { Fps: > 0 } p)
            text.Append("\nFrame rate: ").Append(Math.Round(p.Fps).ToString(CultureInfo.InvariantCulture)).Append(" frames per second\n")
                .Append("Frame time: ").Append((1000 / p.Fps).ToString("0.0", CultureInfo.InvariantCulture)).Append(" ms on average\n");
        text.Append("\nLatency is measured when you enter a sector\nand from time to time while you fly.");
        return text.ToString();
    }

    private static string Awaiting(Net7AwaitedAnswer answer) => answer switch
    {
        Net7AwaitedAnswer.Dock => "dock you",
        Net7AwaitedAnswer.Landing => "land you",
        Net7AwaitedAnswer.Gate => "take you through the gate",
        Net7AwaitedAnswer.Launch => "launch you from the station",
        _ => "answer the conversation",
    };
    private static string Ago(long milliseconds) => milliseconds < 1000 ? "just now" : Seconds(milliseconds) + " ago";
    private static string Seconds(long milliseconds)
    {
        long seconds = milliseconds / 1000;
        if (seconds < 60) return Count(seconds, "second");
        if (seconds < 3600) return Count(seconds / 60, "minute");
        return Count(seconds / 3600, "hour");
        static string Count(long n, string unit) => n.ToString(CultureInfo.InvariantCulture) + " " + unit + (n == 1 ? "" : "s");
    }
    private static string Rate(double bytes) => bytes < 1024
        ? Math.Round(bytes).ToString(CultureInfo.InvariantCulture) + " bytes"
        : (bytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
}
