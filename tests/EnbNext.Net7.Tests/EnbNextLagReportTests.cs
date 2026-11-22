using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using EnbNext.Engine;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbNextLagReportTests
{
    // Frames start an hour ahead of now, clear of any hold.
    private static readonly long Origin = Stopwatch.GetTimestamp() + 3600 * Stopwatch.Frequency;

    private static long At(double milliseconds) => Origin + (long)(milliseconds * Stopwatch.Frequency / 1000);

    /// <summary>Frames of the given lengths, one after another from <paramref name="start"/>; returns where the next one starts.</summary>
    private static double Run(EnbLagMonitor monitor, double start, IEnumerable<double> frames, bool eligible = true)
    {
        double t = start;
        foreach (double length in frames)
        {
            long begin = At(t);
            monitor.Frame(begin, begin, begin, begin, At(t + length / 2), eligible);
            t += length;
        }
        return t;
    }

    private static IEnumerable<double> Steady(double milliseconds, double seconds) => Enumerable.Repeat(milliseconds, (int)(seconds * 1000 / milliseconds));

    [Fact]
    public void SteadyFramesAreNoProblem()
    {
        var monitor = new EnbLagMonitor();
        Run(monitor, 0, Steady(16.6, 90));
        Assert.Null(monitor.TakeDetection());
        var (fps, hitches, worst) = monitor.LastMinute();
        Assert.InRange(fps, 59, 61);
        Assert.Equal(0, hitches);
        Assert.InRange(worst, 16, 17);
    }

    [Fact]
    public void AFrameOfASecondIsAFreezeReportedOnce()
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(16.6, 10));
        t = Run(monitor, t, [1500]);
        Run(monitor, t, Steady(16.6, 5));
        var found = monitor.TakeDetection();
        Assert.Equal(EnbLagKind.Freeze, found?.Kind);
        Assert.Contains("1.5 seconds", found!.Value.Summary, StringComparison.Ordinal);
        Assert.Null(monitor.TakeDetection());
        Assert.Equal(EnbLagKind.Freeze, monitor.Detected?.Kind);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void FiveHitchesWithinAMinuteAreStutter(int count, bool reported)
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(16.6, 5));
        for (int i = 0; i < count; i++) { t = Run(monitor, t, [150]); t = Run(monitor, t, Steady(16.6, 8)); }
        var found = monitor.TakeDetection();
        Assert.Equal(reported ? EnbLagKind.Stutter : null, found?.Kind);
    }

    [Fact]
    public void HitchesFurtherApartThanAMinuteAreNoStutter()
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(16.6, 5));
        for (int i = 0; i < 8; i++) { t = Run(monitor, t, [150]); t = Run(monitor, t, Steady(16.6, 20)); }
        Assert.Null(monitor.TakeDetection());
    }

    [Fact]
    public void AFrameFourTimesTheUsualCountsAsAHitchAboveFiftyMilliseconds()
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(8, 5));
        for (int i = 0; i < 5; i++) { t = Run(monitor, t, [60]); t = Run(monitor, t, Steady(8, 3)); }
        Assert.Equal(EnbLagKind.Stutter, monitor.TakeDetection()?.Kind);

        var fast = new EnbLagMonitor();
        t = Run(fast, 0, Steady(4, 5));
        for (int i = 0; i < 5; i++) { t = Run(fast, t, [40]); t = Run(fast, t, Steady(4, 3)); }
        Assert.Null(fast.TakeDetection());
    }

    [Fact]
    public void TwentySecondsBelowThirtyFramesPerSecondIsALowFrameRate()
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(16.6, 5));
        Run(monitor, t, Steady(40, 23));
        Assert.Equal(EnbLagKind.LowFrameRate, monitor.TakeDetection()?.Kind);

        var shorter = new EnbLagMonitor();
        t = Run(shorter, 0, Steady(16.6, 5));
        t = Run(shorter, t, Steady(40, 15));
        Run(shorter, t, Steady(16.6, 10));
        Assert.Null(shorter.TakeDetection());

        // A frame rate the player capped low is not a problem.
        var capped = new EnbLagMonitor { FrameRateCap = 30 };
        Run(capped, 0, Steady(40, 30));
        Assert.Null(capped.TakeDetection());
    }

    [Fact]
    public void FramesNotEligibleOrHeldAreNotJudged()
    {
        var background = new EnbLagMonitor();
        double t = Run(background, 0, Steady(16.6, 5), eligible: false);
        t = Run(background, t, [2000], eligible: false);
        Run(background, t, Steady(16.6, 5), eligible: false);
        Assert.Null(background.TakeDetection());

        // The frame that spans a pause (the window went to the background and back) is not judged either.
        var returning = new EnbLagMonitor();
        t = Run(returning, 0, Steady(16.6, 5));
        t = Run(returning, t, [3000], eligible: false);
        Run(returning, t, Steady(16.6, 5));
        Assert.Null(returning.TakeDetection());

        var held = new EnbLagMonitor();
        held.Hold(TimeSpan.FromHours(2));
        t = Run(held, 0, Steady(16.6, 5));
        Run(held, t, [2000]);
        Assert.Null(held.TakeDetection());
    }

    [Fact]
    public void RecordingFramesAllocatesNothing()
    {
        var monitor = new EnbLagMonitor();
        double t = Run(monitor, 0, Steady(16.6, 5));
        var frames = Steady(16.6, 200).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (double length in frames)
        {
            long begin = At(t);
            monitor.Frame(begin, begin, begin, begin, begin, true);
            t += length;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Null(monitor.TakeDetection());
    }

    [Fact]
    public void TimelineAndFramesCoverTheirWindows()
    {
        var monitor = new EnbLagMonitor();
        Run(monitor, 0, Steady(10, 200));
        string[] timeline = monitor.TimelineCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("utc,frames,fps", timeline[0], StringComparison.Ordinal);
        Assert.Equal(EnbLagMonitor.SecondCapacity + 1, timeline.Length);
        string[] frames = monitor.FramesCsv().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(EnbLagMonitor.FrameCapacity + 1, frames.Length);
        Assert.StartsWith("0.0,10.00,", frames[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void PackageHoldsTheSummaryAndFilesWithoutTheNames()
    {
        var summary = new JsonObject { ["trigger"] = new JsonObject { ["kind"] = "freeze" }, ["note"] = "Huronimous in Earth" };
        byte[] package = EnbLagReport.Package(summary,
            [("session.log", "RECONNECT: logging huronimous Smith in again from C:\\Users\\bjorn\\AppData"), ("frames.csv", "ms\n16\n")],
            ["Huronimous Smith", "Huronimous", "bjorn", null, "ab"]);
        using var archive = new ZipArchive(new MemoryStream(package));
        Assert.Equal(["report.json", "session.log", "frames.csv"], archive.Entries.Select(e => e.FullName));
        string Read(string name) { using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8); return reader.ReadToEnd(); }
        Assert.Equal("RECONNECT: logging <name> in again from C:\\Users\\<name>\\AppData", Read("session.log"));
        Assert.Contains("<name> in Earth", Read("report.json"), StringComparison.Ordinal);
        Assert.Equal("freeze", JsonNode.Parse(Read("report.json"))!["trigger"]!["kind"]!.GetValue<string>());
        Assert.Equal("ms\n16\n", Read("frames.csv"));
    }

    [Fact]
    public void AnOversizedTranscriptIsCutFromItsStart()
    {
        var random = new Random(3);
        var chars = new char[7_000_000];
        for (int i = 0; i < chars.Length; i++) chars[i] = (char)random.Next(33, 127);
        string noise = new(chars);
        byte[] package = EnbLagReport.Package(new JsonObject(), [("session.log", noise + "LAST LINE")], []);
        Assert.True(package.Length <= EnbLagReport.MaximumPackageBytes);
        using var archive = new ZipArchive(new MemoryStream(package));
        using var reader = new StreamReader(archive.GetEntry("session.log")!.Open());
        Assert.EndsWith("LAST LINE", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"report\":42}", 42L)]
    [InlineData("{\"error\":\"x\"}", null)]
    [InlineData("not json", null)]
    [InlineData("[42]", null)]
    public void ReportNumberComesFromTheForgeAnswer(string answer, long? number) => Assert.Equal(number, EnbLagReport.ReportNumber(answer));
}
