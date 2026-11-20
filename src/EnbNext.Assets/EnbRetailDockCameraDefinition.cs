using System.Globalization;
using System.Numerics;
namespace EnbNext.Assets;

public sealed record EnbRetailDockCameraDefinition(string Bone, string? SubObject,
    Vector3 StartOffset, Vector3 EndOffset, float Speed, float DurationMilliseconds,
    float AngleRadians, float Radius, float EnterLead, float ExitLead)
{
    // 00792750: section is the RenderObject name, not the station label.
    public static EnbRetailDockCameraDefinition? Resolve(EnbRetailIniDocument document, string name)
    {
        if (!document.TryGetSection(name, out var section) ||
            !section.TryGetValue("Bone", out var bone) || string.IsNullOrEmpty(bone)) return null;
        float F(string key, float fallback) => section.TryGetValue(key, out var value) &&
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && float.IsFinite(parsed)
            ? parsed : fallback;
        return new(bone, section.GetValueOrDefault("SubObject"), new(F("SX",0),F("SY",0),F("SZ",0)),
            new(F("EX",0),F("EY",0),F("EZ",0)), F("Speed",.4f), F("Duration",4)*1000,
            F("Angle",90)*MathF.PI/180, F("Radius",1.5f), F("EnterLead",1000), F("ExitLead",1000));
    }
}
