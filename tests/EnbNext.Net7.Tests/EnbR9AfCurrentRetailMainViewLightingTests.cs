using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AfCurrentRetailMainViewLightingTests
{
    [Fact]
    public void PlanetUsesOnlyDominantOrbitalLightAndRetainsAmbient()
    {
        var dominant = new EnbRetailTerrainLightState(new(.1f,.7f,.2f), new(0,0,2), 0, .1f, Vector3.Zero, true);
        var lighting = EnbRetailMainViewLightingEvaluator.EvaluateModeOne(0x102030, dominant);
        Assert.Equal(0x102030u, lighting.GlobalAmbientRgb24);
        var light = Assert.Single(lighting.Lights.Where(slot => slot.Enabled)).Light;
        Assert.Equal(dominant.Diffuse, light.Diffuse);
        Assert.Equal(-Vector3.UnitZ, light.Direction);
        Assert.DoesNotContain(EnbRetailMainViewLightingEvaluator.EvaluateModeOne(0, dominant with { Enabled = false }).Lights,
            slot => slot.Enabled);
    }

    [Fact]
    public void SectorLightParserRetainsTheFourAuthoredRigEntriesAndAmbient()
    {
        EnbRetailMainViewLightSectorDefinition definition = ParseSelectedSectorDefinition();

        AssertClose(new Vector3(0.725490f, 0.858824f, 0.972549f), definition.Key.Rgb);
        Assert.Equal(0.953492f, definition.Key.Intensity);
        AssertClose(new Vector3(-0.160294f, -0.121324f, 0.307353f), definition.Key.DirectionOffset);
        Assert.Equal(0f, definition.Camera.Intensity);
        Assert.True(definition.Camera.HasColorOverride);
        Assert.Equal(2.787132f, definition.ReverseCamera.Intensity);
        Assert.True(definition.Ambient.HasColorOverride);
        Assert.Equal(0f, definition.Ambient.Intensity);
    }

    [Fact]
    public void ModeZeroBuildsRetailFourDirectionalSlotsAndBlackAmbientOverride()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.CreateEmpty();
        AddRgbConstant(splines, "LightColor.Test", new Vector3(0.2f, 0.3f, 0.4f));
        AddRgbConstant(splines, "LightIntensity.Test", new Vector3(0.5f, 0.5f, 0.5f));
        var scene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Test",
            "SkyColor.Test",
            "SkyEmissive.Test",
            [
                new EnbRetailOrbitalDefinition(
                    0,
                    1,
                    1f,
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    0f,
                    100f,
                    0f,
                    0f,
                    true,
                    "LightIntensity.Test",
                    "SkyIntensity.Test",
                    0f,
                    false),
            ]);

        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            new EnbRenderViewport(640, 480, 1.0),
            Math.PI / 2.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        // The production caller supplies the dominant orbital light (color x intensity, clamped).
        Assert.True(EnbRetailOrbitalFogEvaluator.TryBuildOrbitalWorldTransform(scene.Orbitals[0], 0, camera.Position,
            out _, out Vector3 dominantDirection, out _, out _));
        var dominant = new EnbRetailTerrainLightState(Vector3.Clamp(new Vector3(0.2f, 0.3f, 0.4f) * 0.5f, Vector3.Zero, Vector3.One),
            dominantDirection, 0, 0, Vector3.Zero, true);
        Assert.True(EnbRetailMainViewLightingEvaluator.TryEvaluateModeZero(
            camera,
            ParseSelectedSectorDefinition(),
            0x5A1919u,
            dominant,
            out EnbRetailMainViewLightingEvaluation evaluation,
            out string? diagnostic), diagnostic);

        Assert.Equal(4, evaluation.DirectionalLights.Length);
        Assert.Equal(4, evaluation.EnabledDirectionalLightCount);
        Assert.True(evaluation.AmbientOverridden);
        Assert.Equal(0u, evaluation.GlobalAmbientRgb24);

        EnbW3dLightSlot key = evaluation.DirectionalLights[0];
        EnbW3dLightSlot reverseKey = evaluation.DirectionalLights[1];
        EnbW3dLightSlot cameraLight = evaluation.DirectionalLights[2];
        EnbW3dLightSlot reverseCamera = evaluation.DirectionalLights[3];
        Assert.All(evaluation.DirectionalLights, static slot => Assert.Equal(EnbW3dLightType.Directional, slot.Light.Type));
        AssertClose(new Vector3(0.725490f, 0.858824f, 0.972549f) * 0.953492f, key.Light.Diffuse);
        AssertClose(new Vector3(0.949020f, 0.447059f, 0.376471f) * 0.220036f, reverseKey.Light.Diffuse);
        Assert.Equal(Vector3.Zero, cameraLight.Light.Diffuse);
        AssertClose(new Vector3(0.839216f, 0.972549f, 0.937255f) * 2.787132f, reverseCamera.Light.Diffuse);

        AssertClose(-Vector3.Normalize(new Vector3(0.121324f, 0.307353f, -0.839706f)), key.Light.Direction);
        AssertClose(-Vector3.Normalize(new Vector3(0.033088f, 0.329412f, 1.126471f)), reverseKey.Light.Direction);
        AssertClose(Vector3.UnitX, cameraLight.Light.Direction);
        AssertClose(Vector3.Normalize(new Vector3(-0.933823f, -0.077206f, -0.055147f)), reverseCamera.Light.Direction);
    }

    [Fact]
    public void CameraEntriesUseRetailCameraPositiveZRatherThanCanonicalViewingForward()
    {
        EnbRetailScalarSplineDatabase splines = EnbRetailScalarSplineDatabase.CreateEmpty();
        var scene = new EnbRetailOrbitalSceneDefinition(
            "LightColor.Unused",
            "SkyColor.Unused",
            "SkyEmissive.Unused",
            []);
        var authored = new EnbRetailMainViewLightSectorDefinition(
            default,
            default,
            new EnbRetailMainViewLightEntry(Vector3.One, 1f, Vector3.Zero),
            new EnbRetailMainViewLightEntry(Vector3.One, 1f, Vector3.Zero),
            default);

        Quaternion orientation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            orientation,
            new EnbRenderViewport(640, 480, 1.0),
            Math.PI / 2.0,
            new EnbRenderClipRange(6f, 600000f),
            out EnbResolvedRenderCamera camera));

        Assert.True(EnbRetailMainViewLightingEvaluator.TryEvaluateModeZero(
            camera,
            authored,
            0,
            null,
            out EnbRetailMainViewLightingEvaluation evaluation,
            out string? diagnostic), diagnostic);

        AssertClose(-camera.Basis.Forward, -evaluation.DirectionalLights[2].Light.Direction);
        AssertClose(camera.Basis.Forward, -evaluation.DirectionalLights[3].Light.Direction);
    }

    [Fact]
    public void LightSectorUsesCurrentRetailLooseResourcePrecedence()
    {
        string root = Path.Combine(Path.GetTempPath(), $"enbnext-r9af-light-resource-{Guid.NewGuid():N}");
        try
        {
            string release = Path.Combine(root, "release");
            string ini = Path.Combine(root, "Data", "client", "ini");
            string mixfiles = Path.Combine(root, "Data", "client", "mixfiles");
            string common = Path.Combine(root, "Data", "common");
            Directory.CreateDirectory(release);
            Directory.CreateDirectory(ini);
            Directory.CreateDirectory(mixfiles);
            Directory.CreateDirectory(common);
            File.WriteAllBytes(Path.Combine(release, "client.exe"), [0x4D, 0x5A]);
            File.WriteAllText(Path.Combine(ini, "basset.ini"), string.Empty);

            string releaseLight = Path.Combine(release, "light_sector1065.ini");
            File.WriteAllText(releaseLight, "[LIGHTS]\nKEYR=0.25\n");
            File.WriteAllText(Path.Combine(ini, "light_sector1065.ini"), "[LIGHTS]\nKEYR=0.75\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");

            Assert.True(paths.TryResolveMainViewLightSectorIniPath(1065, out string resolved));
            Assert.Equal(releaseLight, resolved);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static EnbRetailMainViewLightSectorDefinition ParseSelectedSectorDefinition()
        => EnbRetailMainViewLightSectorDefinition.Parse(EnbRetailIniDocument.Parse("""
            [LIGHTS]
            KEYR=0.725490
            KEYG=0.858824
            KEYB=0.972549
            KEYI=0.953492
            KEYX=-0.160294
            KEYY=-0.121324
            KEYZ=0.307353
            RKEYR=0.949020
            RKEYG=0.447059
            RKEYB=0.376471
            RKEYI=0.220036
            RKEYX=0.126471
            RKEYY=0.033088
            RKEYZ=0.329412
            CAMERAR=0.929412
            CAMERAG=0.980392
            CAMERAB=0.874510
            CAMERAI=0.000000
            CAMERAX=0.000000
            CAMERAY=0.000000
            CAMERAZ=0.000000
            RCAMERAR=0.839216
            RCAMERAG=0.972549
            RCAMERAB=0.937255
            RCAMERAI=2.787132
            RCAMERAX=-0.066177
            RCAMERAY=0.077206
            RCAMERAZ=0.055147
            AMBIENTR=0.929412
            AMBIENTG=0.980392
            AMBIENTB=0.874510
            AMBIENTI=0.000000
            """));

    private static void AddRgbConstant(
        EnbRetailScalarSplineDatabase splines,
        string baseName,
        Vector3 rgb)
    {
        splines.ApplyRuntimeUpdate(BuildRuntimeConstant($"{baseName}_R", rgb.X));
        splines.ApplyRuntimeUpdate(BuildRuntimeConstant($"{baseName}_G", rgb.Y));
        splines.ApplyRuntimeUpdate(BuildRuntimeConstant($"{baseName}_B", rgb.Z));
    }

    private static byte[] BuildRuntimeConstant(string name, float value)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        writer.Write((ushort)1);
        writer.Write(EnbRetailScalarSplineDatabase.ComputeCurrentRetailCrc32(name));
        writer.Write((byte)3);
        writer.Write((byte)0);
        writer.Write((uint)1);
        writer.Write(value);
        writer.Write(0.5f);
        writer.Flush();
        return stream.ToArray();
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float epsilon = 1e-5f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, epsilon);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, epsilon);
    }
}
