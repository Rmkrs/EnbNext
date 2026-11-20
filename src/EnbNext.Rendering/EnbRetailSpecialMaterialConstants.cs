using System.Numerics;

namespace EnbNext.Rendering;

public sealed record EnbRetailSpecialMaterialObjectLights(Vector3 Ambient,
    IReadOnlyList<(Vector3 Direction, Vector3 Diffuse)> Lights);

/// <summary>A0F610's shader register bank survives between members in a flush.</summary>
public sealed class EnbRetailSpecialMaterialConstants
{
    public Vector4[] Registers { get; } = new Vector4[34];

    public void SetDraw(EnbW3dDrawTask packet, EnbResolvedRenderCamera camera,
        EnbW3dLightEnvironment globals, Vector3 globalAmbient, int mode, Matrix4x4 projection)
    {
        var draw = packet.SpecialMaterial ?? throw new ArgumentException("Not a special-material draw.");
        var resource = draw.Resource;
        var c = Registers;
        c[0] = new(0, 1, .5f, 2);
        EnbRenderCameraMatrixCompiler.TryCreateCanonicalClipFromModel(camera, packet.WorldTransform, out var clip);
        Matrix(1, clip); Matrix(12, packet.WorldTransform); Matrix(30, projection);
        var environment = packet.LightEnvironment.SpecialMaterialObjectLights;
        Vector3 ambient;
        if (environment is { Lights.Count: > 0 })
        {
            ambient = environment.Ambient;
            for (int i = 0; i < 4; i++)
            {
                if (i < environment.Lights.Count)
                {
                    c[16+i] = new(environment.Lights[i].Direction, 1);
                    c[20+i] = new(environment.Lights[i].Diffuse, 1);
                }
                else c[20+i] = Vector4.Zero;
            }
        }
        else
        {
            ambient = globalAmbient;
            for (int i = 0; i < 4; i++)
            {
                var slot = globals.Lights[i];
                if (!slot.Enabled) { c[20+i] = Vector4.Zero; continue; }
                var direction = slot.Light.Type == EnbW3dLightType.Directional ? -slot.Light.Direction : slot.Light.Position;
                if (direction.LengthSquared() != 0) direction = Vector3.Normalize(direction);
                c[16+i] = new(direction, 0); c[20+i] = new(slot.Light.Diffuse, 1);
            }
        }
        float w = resource.Selector == 4 && mode == 3 ? 0 : 1;
        // A0F610: ambient alpha belongs to the light-environment branch,
        // independently of the material diffuse alpha supplied by the caller.
        c[24] = new(ambient * resource.Ambient, environment is { Lights.Count: > 0 } ? 0 : 1);
        c[25] = new(resource.Diffuse, w);
        c[26] = new(resource.Specular, 1);
        c[27] = new(resource.Parameter.X, resource.Parameter.Y, 0, 0);
        c[28] = new(camera.Position, 1);
    }

    private void Matrix(int at, Matrix4x4 m)
    {
        Registers[at] = new(m.M11,m.M21,m.M31,m.M41);
        Registers[at+1] = new(m.M12,m.M22,m.M32,m.M42);
        Registers[at+2] = new(m.M13,m.M23,m.M33,m.M43);
        Registers[at+3] = new(m.M14,m.M24,m.M34,m.M44);
    }
}
