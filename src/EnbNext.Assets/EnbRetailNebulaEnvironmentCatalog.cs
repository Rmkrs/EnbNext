using System.Numerics;

namespace EnbNext.Assets;

/// <summary>
/// Current-retail influence/nebula environment inputs loaded from the selected
/// installation's nebula.ini. The executable reads only Room and Between here;
/// ordinary sector backdrop/fog data remains owned by the normal environment path.
/// </summary>
public sealed record EnbRetailNebulaEnvironmentState(
    Vector3 FogColor,
    float FogNearRange,
    float FogFarRange,
    Vector3? SkyBoxColor = null)
{
    public uint FogColorRgb24 => PackRgb24(FogColor);

    private static uint PackRgb24(Vector3 color)
    {
        static uint ToByte(float value)
        {
            if (!float.IsFinite(value))
                return 0u;
            return (uint)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
        }

        uint r = ToByte(color.X);
        uint g = ToByte(color.Y);
        uint b = ToByte(color.Z);
        return (r << 16) | (g << 8) | b;
    }
}

public sealed class EnbRetailNebulaEnvironmentCatalog
{
    private const float ByteToUnit = 1f / 255f;

    private EnbRetailNebulaEnvironmentCatalog(
        EnbRetailNebulaEnvironmentState room,
        EnbRetailNebulaEnvironmentState between)
    {
        Room = room;
        Between = between;
    }

    public EnbRetailNebulaEnvironmentState Room { get; }

    public EnbRetailNebulaEnvironmentState Between { get; }

    public static EnbRetailNebulaEnvironmentCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailNebulaEnvironmentCatalog Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Parse(EnbRetailIniDocument.Parse(text));
    }

    internal static EnbRetailNebulaEnvironmentCatalog Parse(EnbRetailIniDocument ini)
    {
        ArgumentNullException.ThrowIfNull(ini);

        EnbRetailNebulaEnvironmentState room = ReadState(ini, "Room", includeSkyBoxColor: false);
        EnbRetailNebulaEnvironmentState between = ReadState(ini, "Between", includeSkyBoxColor: true);
        return new EnbRetailNebulaEnvironmentCatalog(room, between);
    }

    private static EnbRetailNebulaEnvironmentState ReadState(
        EnbRetailIniDocument ini,
        string sectionName,
        bool includeSkyBoxColor)
    {
        IReadOnlyDictionary<string, string> values = ini.TryGetSection(sectionName, out var section)
            ? section
            : EmptySection;

        Vector3 fogColor = new(
            ReadInteger(values, "FogColorR", 0) * ByteToUnit,
            ReadInteger(values, "FogColorG", 0) * ByteToUnit,
            ReadInteger(values, "FogColorB", 0) * ByteToUnit);
        float fogNear = ReadFloat(values, "FogNearRange", 0f);
        float fogFar = ReadFloat(values, "FogFarRange", 1f);

        Vector3? skyBoxColor = includeSkyBoxColor
            ? new Vector3(
                ReadInteger(values, "SkyBoxColorR", 0) * ByteToUnit,
                ReadInteger(values, "SkyBoxColorG", 0) * ByteToUnit,
                ReadInteger(values, "SkyBoxColorB", 0) * ByteToUnit)
            : null;

        return new EnbRetailNebulaEnvironmentState(fogColor, fogNear, fogFar, skyBoxColor);
    }

    private static int ReadInteger(IReadOnlyDictionary<string, string> values, string key, int fallback)
        => EnbRetailIniDocument.TryReadInt(values, key, out int value) ? value : fallback;

    private static float ReadFloat(IReadOnlyDictionary<string, string> values, string key, float fallback)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) ? value : fallback;

    private static IReadOnlyDictionary<string, string> EmptySection { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
