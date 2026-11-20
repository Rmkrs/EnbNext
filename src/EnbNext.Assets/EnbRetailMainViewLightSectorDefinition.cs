using System.Numerics;

namespace EnbNext.Assets;

/// <summary>
/// One authored entry in current retail's [LIGHTS] MainView rig. RGB and
/// intensity are kept separate because LightClass stores them separately and
/// multiplies them only while emitting the D3DLIGHT8 record.
/// </summary>
public readonly record struct EnbRetailMainViewLightEntry(
    Vector3 Rgb,
    float Intensity,
    Vector3 DirectionOffset)
{
    public bool HasColorOverride =>
        Rgb.LengthSquared() + (Intensity * Intensity) > 0.0001f;
}

/// <summary>
/// Installed light_sectorNNNN.ini data consumed by OrbitalManager/MainView.
/// Missing keys use the current executable's zero defaults.
/// </summary>
public sealed record EnbRetailMainViewLightSectorDefinition(
    EnbRetailMainViewLightEntry Key,
    EnbRetailMainViewLightEntry ReverseKey,
    EnbRetailMainViewLightEntry Camera,
    EnbRetailMainViewLightEntry ReverseCamera,
    EnbRetailMainViewLightEntry Ambient)
{
    public static EnbRetailMainViewLightSectorDefinition Empty { get; } = new(
        default,
        default,
        default,
        default,
        default);

    public static EnbRetailMainViewLightSectorDefinition Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Parse(EnbRetailIniDocument.Load(path));
    }

    public static EnbRetailMainViewLightSectorDefinition Parse(EnbRetailIniDocument ini)
    {
        ArgumentNullException.ThrowIfNull(ini);
        if (!ini.TryGetSection("LIGHTS", out IReadOnlyDictionary<string, string> lights))
            return Empty;

        return new EnbRetailMainViewLightSectorDefinition(
            ReadEntry(lights, "KEY"),
            ReadEntry(lights, "RKEY"),
            ReadEntry(lights, "CAMERA"),
            ReadEntry(lights, "RCAMERA"),
            ReadEntry(lights, "AMBIENT"));
    }

    private static EnbRetailMainViewLightEntry ReadEntry(
        IReadOnlyDictionary<string, string> values,
        string prefix)
        => new(
            new Vector3(
                ReadFloat(values, $"{prefix}R"),
                ReadFloat(values, $"{prefix}G"),
                ReadFloat(values, $"{prefix}B")),
            ReadFloat(values, $"{prefix}I"),
            new Vector3(
                ReadFloat(values, $"{prefix}X"),
                ReadFloat(values, $"{prefix}Y"),
                ReadFloat(values, $"{prefix}Z")));

    private static float ReadFloat(IReadOnlyDictionary<string, string> values, string key)
        => EnbRetailIniDocument.TryReadFloat(values, key, out float value) ? value : 0f;
}
