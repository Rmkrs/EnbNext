using System.Diagnostics;
using System.Globalization;

namespace EnbNext.Engine;

/// <summary>
/// Diagnostics for the galaxy map's first openings (it could freeze the first time it opens): how long its
/// first few presentations took on the interface thread, and in which parts. Shown in the session's presentation
/// receipt; nothing changes behaviour.
/// </summary>
internal sealed partial class EnbNavigationMapUi
{
    private const int GalaxyTimedPublishes = 3;
    private int _galaxyTimedPublishes;
    private double _galaxyLoadMs, _galaxyGlyphMs, _galaxyTextureMs, _galaxyLabelMs;
    private string _galaxyTiming = "";

    /// <summary>The first galaxy presentations' times (empty until the galaxy map was first shown).</summary>
    internal string GalaxyOpenDiagnostics => _galaxyTiming;

    private bool TimingGalaxy => _galaxyTimedPublishes < GalaxyTimedPublishes;

    private static double Since(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    private void PublishGalaxyTimed()
    {
        if (!TimingGalaxy) { PublishGalaxy(); return; }
        _galaxyLoadMs = _galaxyGlyphMs = _galaxyTextureMs = _galaxyLabelMs = 0;
        long start = Stopwatch.GetTimestamp();
        try { PublishGalaxy(); }
        finally
        {
            _galaxyTimedPublishes++;
            _galaxyTiming += string.Format(CultureInfo.InvariantCulture,
                " · galaxy publish {0}: {1:0.0} ms (data {2:0.0}, models {3:0.0}, textures {4:0.0}, labels {5:0.0})",
                _galaxyTimedPublishes, Since(start), _galaxyLoadMs, _galaxyGlyphMs, _galaxyTextureMs, _galaxyLabelMs);
        }
    }
}
