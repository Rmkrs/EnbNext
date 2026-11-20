using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>
/// One retail in-space camera mode. Angular values are normalized to radians at
/// the asset boundary; Distance remains the authored configuration scalar and
/// is multiplied by the proven retail chase distance scale by presentation.
/// </summary>
public sealed record EnbRetailCameraMode(
    int Index,
    float HeadingRadians,
    float Distance,
    float TiltRadians,
    float HorizontalFieldOfViewRadians,
    float ElevationOffsetRadians);

/// <summary>
/// Retail spacecam/cam&lt;session&gt;.ini camera-mode table. Current retail reads
/// Mode00, Mode01, ... and stops at the first missing section.
/// </summary>
public sealed class EnbRetailCameraModeCatalog
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private readonly List<EnbRetailCameraMode> _modes = [];

    public IReadOnlyList<EnbRetailCameraMode> Modes => _modes;

    public static EnbRetailCameraModeCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailCameraModeCatalog Parse(EnbRetailIniDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var result = new EnbRetailCameraModeCatalog();
        for (int index = 0; ; index++)
        {
            string sectionName = $"Mode{index:00}";
            if (!document.TryGetSection(sectionName, out IReadOnlyDictionary<string, string> values))
                break;

            float heading = ReadFloat(values, "Heading", 0f) * DegreesToRadians;
            float distance = ReadFloat(values, "Distance", 0f);
            float tilt = ReadFloat(values, "Tilt", 0f) * DegreesToRadians;
            float fov = ReadFloat(values, "FOV", 90f) * DegreesToRadians;
            float elevationOffset = ReadFloat(values, "ElevationOffset", 0f) * DegreesToRadians;

            result._modes.Add(new EnbRetailCameraMode(
                index,
                heading,
                distance,
                tilt,
                fov,
                elevationOffset));
        }

        return result;
    }

    /// <summary>
    /// Current retail formats cam%d.ini from the signed x86 dword value of the
    /// network-order fixed/session ID bytes, i.e. signed bswap32(fixedSessionId).
    /// </summary>
    public static int GetSessionOverrideSelector(uint fixedSessionId)
        => unchecked((int)BinaryPrimitives.ReverseEndianness(fixedSessionId));

    private static float ReadFloat(
        IReadOnlyDictionary<string, string> values,
        string key,
        float defaultValue)
    {
        if (!EnbRetailIniDocument.TryReadFloat(values, key, out float parsed) || !float.IsFinite(parsed))
            return defaultValue;
        return parsed;
    }
}

public sealed record EnbRetailCameraModeSelection(
    EnbRetailCameraModeCatalog Catalog,
    EnbRetailDataProvenance Provenance,
    bool UsesSessionOverride,
    int? SessionOverrideSelector);
