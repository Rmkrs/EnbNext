using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// LightClass types, not D3DLIGHT8 types (953600, 985430).
public enum EnbRetailStationLightKind { Point, Directional, Spot }

public sealed record EnbRetailStationLight(int Ordinal, EnbRetailStationLightKind Kind,
    Matrix4x4 Transform, Vector3 Ambient, Vector3 Diffuse, float Intensity,
    float Near, float Far, Vector3 SpotAxis, float SpotThreshold);

public sealed record EnbRetailStationLightSelection(Vector3 Ambient,
    ImmutableArray<int> RankedOrdinals, EnbW3dLightEnvironment Environment);

/// <summary>Station scene 762520 and LightEnvironment 985430/985840/926460.
/// Immutable room inputs; every registered object is evaluated at its current root translation.</summary>
public sealed record EnbRetailStationLighting(Vector3 Ambient, float Opposing,
    ImmutableArray<EnbRetailStationLight> Lights)
{
    public bool ReplacesSceneAmbient { get; init; } = true;
    // 761C80 initializes ambient=(1,1,1), then accumulates the ambient-only
    // million-unit light once. 926460 packs 2*255 without component masks.
    public static EnbW3dLightEnvironment UnregisteredEnvironment { get; } =
        EnbW3dLightEnvironment.CreateCurrentRetail(PackAmbient(new(2)), []);

    private readonly record struct Evaluated(int Ordinal, Vector3 Direction, Vector3 Diffuse);

    public EnbRetailStationLightSelection Select(Vector3 position)
    {
        var ambient = Ambient;
        var ranked = new List<Evaluated>(4);
        foreach (var light in Lights)
        {
            Vector3 direction;
            float attenuation = 1;
            bool ambientOnly = false;
            if (light.Kind == EnbRetailStationLightKind.Directional)
                direction = -Vector3.TransformNormal(Vector3.UnitZ, light.Transform);
            else
            {
                direction = light.Transform.Translation - position;
                float distance = direction.Length();
                if (distance > 0) direction *= 1 / distance;
                attenuation = 1 - (distance - light.Near) / (light.Far - light.Near);
                if (attenuation < 0) attenuation = 0;
                else if (attenuation > 1) attenuation = 1;
                if (light.Kind == EnbRetailStationLightKind.Spot)
                {
                    float cone = (Vector3.Dot(-Vector3.TransformNormal(light.SpotAxis, light.Transform), direction)
                        - light.SpotThreshold) / (1 - light.SpotThreshold) * attenuation;
                    attenuation = cone >= 0 ? (cone > 1 ? 1 : cone) : 0;
                }
                ambientOnly = light.Diffuse.LengthSquared() <= 0;
            }
            ambient += light.Ambient * attenuation;
            if (ambientOnly)
            {
                ambient += light.Diffuse * attenuation; // B12F50=1
                continue;
            }
            var diffuse = light.Diffuse * attenuation;
            if (Opposing != 0) diffuse *= light.Intensity;
            var candidate = new Evaluated(light.Ordinal, direction, diffuse);
            int index = 0;
            // Strict greater-than preserves native scene-list order on ties.
            while (index < ranked.Count && !(diffuse.LengthSquared() > ranked[index].Diffuse.LengthSquared())) index++;
            if (index < 4)
            {
                ranked.Insert(index, candidate);
                if (ranked.Count > 4) ranked.RemoveAt(4);
            }
        }
        var ordinals = ranked.Select(l => l.Ordinal).ToImmutableArray();
        if (ranked.Count != 0 && Opposing != 0)
        {
            var direction = ranked[0].Direction;
            var rgb = ranked[0].Diffuse;
            for (int i = 1; i < Math.Min(ranked.Count, 3); i++)
            {
                float weight = ranked[i].Diffuse.LengthSquared() / ranked[0].Diffuse.LengthSquared();
                direction += ranked[i].Direction * weight;
                rgb += ranked[i].Diffuse * weight;
            }
            if (direction.LengthSquared() != 0) direction = Vector3.Normalize(direction);
            var opposing = new Evaluated(-1, -direction, Complement(rgb, Opposing));
            if (ranked.Count == 4) ranked[3] = opposing;
            else ranked.Add(opposing);
        }
        // 926460 consumes world-space records, not 985660's camera-space compact array.
        var slots = ranked.Select(l => new EnbW3dLightSlot(true, new(
            EnbW3dLightType.Directional, l.Diffuse, Vector3.Zero, Vector3.Zero,
            Vector3.Zero, -l.Direction, 0, 0, 0, 0, 0, 0, 0)));
        return new(ambient, ordinals, EnbW3dLightEnvironment.CreateCurrentRetail(PackAmbient(ambient), slots)
            with {HasNativeStationArithmetic=true,
                SpecialMaterialObjectLights=new(ambient,ranked.Select(l=>(l.Direction,l.Diffuse)).ToArray())});
    }

    private static Vector3 Complement(Vector3 rgb, float opposing)
    {
        float value = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z));
        float delta = value - MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float saturation = value == 0 ? 0 : delta / value;
        if (saturation == 0) return new(value * opposing);
        float hue = rgb.X == value ? (rgb.Y - rgb.Z) / delta :
            rgb.Y == value ? (rgb.Z - rgb.X) / delta + 2 : (rgb.X - rgb.Y) / delta + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        hue += 180;
        if (hue > 360) hue -= 360;
        if (hue == 360) hue = 0;
        value *= opposing;
        float sector = hue / 60;
        int index = unchecked((int)sector);
        float fraction = sector - index;
        float p = value * (1 - saturation), q = value * (1 - saturation * fraction),
            t = value * (1 - saturation * (1 - fraction));
        return index switch { 0 => new(value,t,p), 1 => new(q,value,p), 2 => new(p,value,t),
            3 => new(p,q,value), 4 => new(t,p,value), 5 => new(value,p,q), _ => new(float.NaN) };
    }

    public static uint PackAmbient(Vector3 rgb)
        => unchecked((uint)(((int)(rgb.X * 255) << 16) | ((int)(rgb.Y * 255) << 8) | (int)(rgb.Z * 255)));

    public static EnbRetailStationLighting Load(EnbRetailAssetBundle assets, uint definition, uint roomType,
        uint flags, EnbW3dRenderResource resource)
    {
        string path = Path.Combine(assets.Paths.ClientIniDirectory, "starbase_lights.ini");
        var ini = EnbNext.Installation.EnbPathCase.FileExists(path) ? EnbRetailIniDocument.Load(path) : EnbRetailIniDocument.Parse("");
        return FromAuthored(ini, definition, roomType, flags, resource.Hierarchies.Single());
    }

    public static EnbRetailStationLighting FromAuthored(EnbRetailIniDocument ini, uint definition, uint roomType,
        uint flags, EnbW3dRenderHierarchy hierarchy)
    {
        // 640810 uses room flags even if the display-resource lookup fell back.
        int variant = (flags & 0x80) != 0 ? 3 : (flags & 0x40) != 0 ? 2 : 1;
        bool found = ini.TryGetSection($"STARBASE-{definition}-{roomType}-{variant}", out var section);
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out var pose, out var error))
            throw new InvalidDataException(error);
        var lights = ImmutableArray.CreateBuilder<EnbRetailStationLight>();
        for (int i = 0; ; i++)
        {
            var pivot = hierarchy.Pivots.FirstOrDefault(p => p.Name.Equals($"D_LIGHT{i:00}", StringComparison.OrdinalIgnoreCase));
            if (pivot is null || pivot.Ordinal == 0) break;
            lights.Add(new(i, EnbRetailStationLightKind.Point, pose[pivot.Ordinal],
                ReadRgb($"AmbientRGB{i}", Vector3.Zero), ReadRgb($"LightRGB{i}", Vector3.One),
                found ? ReadFloat($"Intensity{i}", 0) : 1,
                found ? ReadFloat($"Near{i}", 0) : 50, found ? ReadFloat($"Far{i}", 50) : 100,
                Vector3.UnitZ, BitConverter.Int32BitsToSingle(0x3f34fdf4)));
        }
        if (lights.Count == 0)
            return new(Vector3.Zero, .7f, [new(0, EnbRetailStationLightKind.Point,
                Matrix4x4.CreateTranslation(100, 50, 10), Vector3.Zero, new(.8f, .6f, .4f), 1, 0, 500,
                Vector3.UnitZ, BitConverter.Int32BitsToSingle(0x3f34fdf4))]) {ReplacesSceneAmbient=false};
        return new(ReadRgb("Ambient", Vector3.Zero), ReadFloat("Fill", .75f), lights.ToImmutable());

        float ReadFloat(string key, float fallback)
            => found && EnbRetailIniDocument.TryReadFloat(section, key, out float value) ? value : fallback;
        Vector3 ReadRgb(string key, Vector3 fallback)
        {
            if (!found || !section.TryGetValue(key, out var text)) return fallback;
            if (string.IsNullOrWhiteSpace(text)) return Vector3.Zero;
            var parts = text.Split(',');
            if (parts.Length != 3) throw new InvalidDataException($"Invalid station light RGB {key}.");
            return new(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>640810 replaces room lights; its no-pivot branch retains scene ambient.
/// Scene destruction (761F70) retires all state.</summary>
public sealed class EnbRetailStationLightingState
{
    private EnbRetailStationLighting? _source;
    public EnbRetailStationLighting Current { get; private set; } = new(Vector3.Zero,.5f,[]);
    public void Enter(EnbRetailStationLighting room)
    {
        if (ReferenceEquals(room,_source)) return;
        Current=room.ReplacesSceneAmbient ? room : room with {Ambient=Current.Ambient};
        _source=room;
    }
    public void Reset() { _source=null;Current=new(Vector3.Zero,.5f,[]); }
}
