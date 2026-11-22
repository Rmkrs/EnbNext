using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using static SDL.SDL3;

namespace EnbNext.App;

/// <summary>
/// Next: lag reports for the developer. The frame loop feeds <see cref="EnbLagMonitor"/>; when it finds a freeze,
/// stutter or a low frame rate the chat says so and offers a report (a click in the Modern UI, /report in the classic
/// chat), or sends it at once with "Send them automatically". Help > Send a report records thirty more seconds first.
/// A report goes to the Forge, which answers with its number; one that cannot be sent waits in the temporary folder
/// and goes at the next start.
/// </summary>
internal sealed partial class SdlApplication
{
    private const string ReportCommand = "/report";
    private static readonly TimeSpan WorldSettle = TimeSpan.FromSeconds(20), ForegroundSettle = TimeSpan.FromSeconds(5);
    private const long ManualRecordMilliseconds = 30000;
    private const int PendingReportsKept = 5;
    private static readonly TimeSpan PendingReportAge = TimeSpan.FromDays(14);
    private static readonly HttpClient reportHttp = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static string reportAddress = EnbLagReport.UploadAddress;
    private static string PendingReportDirectory => Path.Combine(Path.GetTempPath(), "EnbNext", "Reports");

    private readonly EnbLagMonitor lagMonitor = new();
    private bool lagInWorld, lagForeground, lagReportSending, lagPendingChecked;
    private uint? lagStarbase;
    private long lagManualDue = -1;
    private EnbLagDetection? lagOffered;
    private readonly List<string> lagChatQueue = [];

    /// <summary>The frame just finished, judged when the player is in the world with the game in front.</summary>
    private void ObserveLagFrame(long begin, long handled, long drained, long ticked, long rendered)
    {
        var scene = worldScreen is { } world && !world.Owner.AwaitingWorld ? _client?.LatestPresentationSnapshot : null;
        bool inWorld = scene is { IsTransitioning: false, LocationTransition: null };
        bool foreground = inWorld && videoScreen is null && movieScreen is null && window.Focused && !window.Minimized;
        // Entering the world, a sector or a station loads for a while; coming back to the game settles shortly.
        if (inWorld && (!lagInWorld || scene!.ActiveStarbaseId != lagStarbase)) lagMonitor.Hold(WorldSettle);
        if (foreground && !lagForeground) lagMonitor.Hold(ForegroundSettle);
        lagInWorld = inWorld; lagForeground = foreground; lagStarbase = scene?.ActiveStarbaseId;
        lagMonitor.FrameRateCap = CurrentFrameRate;
        lagMonitor.Frame(begin, handled, drained, ticked, rendered, foreground && _benchmark is null);
    }

    private void TickLagReports()
    {
        if (worldScreen is not { } world || _client?.SelectedFixedSessionId is not uint) return;
        if (lagChatQueue.Count > 0) { var waiting = lagChatQueue.ToArray(); lagChatQueue.Clear(); foreach (string line in waiting) LagChat(line); }
        if (!lagPendingChecked) { lagPendingChecked = true; dispatch.Observe(SendPendingReportsAsync()); }
        if (lagManualDue >= 0 && Environment.TickCount64 >= lagManualDue)
        {
            lagManualDue = -1;
            dispatch.Observe(SendLagReportAsync(new(EnbLagKind.Manual, "a report sent from the game", DateTime.UtcNow), automatic: false));
        }
        if (lagMonitor.TakeDetection() is not { } found) return;
        AppendDiagnostic("LAG: " + found.Kind + ", " + found.Summary);
        var options = EffectiveQualityOfLife;
        if (!options.LagReports) return;
        if (options.LagReportsAutomatic)
        {
            LagChat("EnbNext noticed " + found.Summary + " and is sending a report to the developer.");
            dispatch.Observe(SendLagReportAsync(found, automatic: true));
            return;
        }
        lagOffered = found;
        string offer = "EnbNext noticed " + found.Summary + (options.ModernUi
            ? ". Click here to send a report to the developer."
            : ". Type /report to send a report to the developer.");
        LagChat(offer);
        if (options.ModernUi) world.Owner.Chat.AddModernLink(offer, () => SendOfferedReport(found));
    }

    private void SendOfferedReport(EnbLagDetection found)
    {
        if (lagReportSending) return;
        lagOffered = null;
        dispatch.Observe(SendLagReportAsync(found, automatic: false));
    }

    private bool TryReportCommand(string text)
    {
        if (!string.Equals(text.Trim(), ReportCommand, StringComparison.OrdinalIgnoreCase)) return false;
        RequestDeveloperReport();
        return true;
    }

    /// <summary>Help > Send a report, or /report: the offered report at once, otherwise thirty more seconds recorded first.</summary>
    private void RequestDeveloperReport()
    {
        if (lagReportSending || lagManualDue >= 0) { LagChat("A report to the developer is already on its way."); return; }
        if (lagOffered is { } offered) { SendOfferedReport(offered); return; }
        lagManualDue = Environment.TickCount64 + ManualRecordMilliseconds;
        LagChat("Recording 30 seconds of performance details for a report to the developer. Keep playing as usual.");
    }

    private void LagChat(string text)
    {
        if (worldScreen is not null && _client?.SelectedFixedSessionId is uint character) _client.ReportLocalChatMessage(character, 19, text);
        else if (lagChatQueue.Count < 8) lagChatQueue.Add(text);
    }

    private async Task SendLagReportAsync(EnbLagDetection detection, bool automatic)
    {
        if (lagReportSending) return;
        lagReportSending = true;
        try
        {
            var (summary, files, names) = GatherLagReport(detection, automatic);
            byte[] package = await Task.Run(() => EnbLagReport.Package(summary, files, names));
            var (number, retry) = await UploadReportAsync(package);
            if (number is long sent)
            {
                AppendDiagnostic("LAG REPORT: sent as report " + sent.ToString(CultureInfo.InvariantCulture));
                LagChat("Report #" + sent.ToString(CultureInfo.InvariantCulture) + " was sent to the developer. Thank you!");
            }
            else if (retry && SavePendingReport(package)) LagChat("The report could not be sent right now. EnbNext sends it the next time it starts.");
            else LagChat("The report could not be sent.");
        }
        catch (Exception ex)
        {
            // A report is a convenience: whatever fails here is logged and the game carries on.
            AppendDiagnostic("LAG REPORT: could not be prepared: " + ex);
            LagChat("The report could not be prepared.");
        }
        finally { lagReportSending = false; }
    }

    /// <summary>Reports a previous session could not send, oldest first; one that fails again waits for the next start.</summary>
    private async Task SendPendingReportsAsync()
    {
        string[] waiting;
        try
        {
            if (!Directory.Exists(PendingReportDirectory)) return;
            waiting = [.. Directory.GetFiles(PendingReportDirectory, "report-*.zip").Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        foreach (string path in waiting)
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > PendingReportAge) { File.Delete(path); continue; }
                var (number, retry) = await UploadReportAsync(await File.ReadAllBytesAsync(path));
                if (number is null && retry) return;
                File.Delete(path);
                if (number is long sent) LagChat("An earlier report was sent to the developer as report #" + sent.ToString(CultureInfo.InvariantCulture) + ". Thank you!");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        }
    }

    /// <summary>The Forge's report number; Retry when the Forge could not be reached or asked to come back later.</summary>
    private static async Task<(long? Number, bool Retry)> UploadReportAsync(byte[] package)
    {
        try
        {
            using var content = new ByteArrayContent(package);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await reportHttp.PostAsync(reportAddress, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, (int)response.StatusCode is 408 or 429 or >= 500);
            return (EnbLagReport.ReportNumber(await response.Content.ReadAsStringAsync().ConfigureAwait(false)), false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { return (null, true); }
    }

    private static bool SavePendingReport(byte[] package)
    {
        try
        {
            Directory.CreateDirectory(PendingReportDirectory);
            var kept = Directory.GetFiles(PendingReportDirectory, "report-*.zip").Order(StringComparer.Ordinal).ToList();
            while (kept.Count >= PendingReportsKept) { File.Delete(kept[0]); kept.RemoveAt(0); }
            File.WriteAllBytes(Path.Combine(PendingReportDirectory,
                "report-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".zip"), package);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string ClientVersion =>
        typeof(SdlApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    private static string KindName(EnbLagKind kind) => kind switch
    {
        EnbLagKind.LowFrameRate => "low-frame-rate", EnbLagKind.Stutter => "stutter", EnbLagKind.Freeze => "freeze", _ => "manual",
    };

    /// <summary>The report's summary and files, taken now on the interface thread; the names to take out of them.</summary>
    private (JsonObject Summary, List<(string Name, string Text)> Files, List<string?> Names) GatherLagReport(EnbLagDetection detection, bool automatic)
    {
        var options = EffectiveQualityOfLife;
        var scene = _client?.LatestPresentationSnapshot;
        var (fps, hitches, worst) = lagMonitor.LastMinute();
        DateTime started;
        using (var process = Process.GetCurrentProcess()) started = process.StartTime.ToUniversalTime();
        var summary = new JsonObject
        {
            ["format"] = 1,
            ["createdAtUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["client"] = new JsonObject
            {
                ["version"] = ClientVersion,
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["process"] = RuntimeInformation.ProcessArchitecture.ToString(),
                ["uptimeSeconds"] = (long)(DateTime.UtcNow - started).TotalSeconds,
                ["diagnosticFiles"] = DiagnosticFiles.Enabled,
            },
            ["trigger"] = new JsonObject
            {
                ["kind"] = KindName(detection.Kind),
                ["summary"] = detection.Summary,
                ["automatic"] = automatic,
                ["detectedAtUtc"] = detection.DetectedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            },
            ["game"] = new JsonObject
            {
                ["sector"] = scene?.LocationTransition?.DestinationSectorName ?? _presenceSession.SettledLocation ?? _selectedCharacter?.SectorName,
                ["docked"] = scene?.ActiveStarbaseId is not null,
                ["profession"] = _selectedCharacter?.ClassName,
                ["level"] = _selectedCharacter?.OverallLevel,
                ["interface"] = options.ModernUi ? "modern" : "classic",
                ["server"] = _preferences.Server,
                ["multiboxClients"] = multibox?.Peers.Length ?? 1,
            },
            ["performance"] = new JsonObject
            {
                ["lastMinuteFramesPerSecond"] = Math.Round(fps, 1),
                ["lastMinuteHitches"] = hitches,
                ["lastMinuteWorstFrameMs"] = Math.Round(worst, 1),
                ["frameRateCap"] = CurrentFrameRate,
                ["lastWorldRenderMs"] = worldScreen is { } world ? Math.Round(world.LastWorldRenderMs, 2) : null,
                ["lastInterfaceRenderMs"] = worldScreen is { } shown ? Math.Round(shown.LastInterfaceRenderMs, 2) : null,
                ["managedHeapMegabytes"] = GC.GetTotalMemory(false) / (1024 * 1024),
                ["workingSetMegabytes"] = Environment.WorkingSet / (1024 * 1024),
                ["garbageCollections"] = new JsonArray(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)),
            },
            ["system"] = SystemSummary(),
            ["settings"] = JsonSerializer.SerializeToNode(_preferences with { SoundSettingsOwner = null }),
        };
        string transcript;
        lock (_diagnosticsSync) transcript = _diagnostics.ToString();
        var files = new List<(string Name, string Text)>
        {
            ("timeline.csv", lagMonitor.TimelineCsv()),
            ("frames.csv", lagMonitor.FramesCsv()),
            ("session.log", transcript),
            ("issues.txt", _runtimeIssues.Describe(false)),
        };
        var names = new List<string?> { _frontendUsername, _selectedCharacter?.FullName, _selectedCharacter?.FirstName, _selectedCharacter?.LastName,
            Environment.UserName, Environment.MachineName };
        foreach (var peer in multibox?.Peers ?? []) names.Add(peer.Name);
        return (summary, files, names);
    }

    /// <summary>The computer: system, processor, memory, the graphics cards (the one in use marked), displays and window.</summary>
    private JsonObject SystemSummary()
    {
        var adapters = new JsonArray();
        string? active = null;
        try
        {
            if (OperatingSystem.IsWindows())
                foreach (var card in WindowsGraphicsAdapters.List())
                {
                    bool used = window.AdapterLuid == card.Luid;
                    if (used) active = card.Name;
                    adapters.Add(Adapter(card, used, null));
                }
            else if (OperatingSystem.IsLinux())
                foreach (var card in LinuxGraphicsAdapters.List())
                {
                    bool used = activeGraphicsAdapter is null ? card.Primary : card.Adapter.Key == activeGraphicsAdapter;
                    if (used) active = card.Adapter.Name;
                    adapters.Add(Adapter(card.Adapter, used, card.Driver));
                }
        }
        catch (Exception ex) { adapters.Add("unavailable: " + ex.Message); }
        var displays = new JsonArray();
        try
        {
            var current = SdlDisplays.Current(window).Id;
            foreach (var screen in SdlDisplays.All())
                displays.Add(new JsonObject
                {
                    ["width"] = screen.PixelWidth, ["height"] = screen.PixelHeight, ["scale"] = Math.Round(screen.Scale, 2),
                    ["refreshRate"] = RefreshRate(screen.Id), ["game"] = screen.Id == current,
                });
        }
        catch (Exception ex) { displays.Add("unavailable: " + ex.Message); }
        return new JsonObject
        {
            ["os"] = RuntimeInformation.OSDescription,
            ["cpu"] = ProcessorName(),
            ["logicalProcessors"] = Environment.ProcessorCount,
            ["memoryMegabytes"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            ["graphics"] = active ?? window.ContextDescription,
            ["graphicsContext"] = window.ContextDescription,
            ["adapters"] = adapters,
            ["displays"] = displays,
            ["window"] = new JsonObject
            {
                ["width"] = window.PixelWidth, ["height"] = window.PixelHeight, ["scale"] = Math.Round(window.Scale, 2),
                ["fullscreen"] = window.Fullscreen, ["maximized"] = window.Maximized,
            },
        };
    }

    private static JsonObject Adapter(GraphicsAdapter card, bool used, string? driver) => new()
    {
        ["name"] = card.Name,
        ["vendor"] = card.Vendor.ToString("X4", CultureInfo.InvariantCulture),
        ["device"] = card.Device.ToString("X4", CultureInfo.InvariantCulture),
        ["videoMemoryMegabytes"] = (long)(card.VideoMemory / (1024 * 1024)),
        ["driver"] = driver,
        ["used"] = used,
    };

    private static unsafe float? RefreshRate(SDL.SDL_DisplayID id)
    {
        var mode = SDL_GetDesktopDisplayMode(id);
        return mode == null ? null : mode->refresh_rate;
    }

    private static string? ProcessorName()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim();
            }
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal) && line.IndexOf(':') is int colon and > 0) return line[(colon + 1)..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return null;
    }
}
