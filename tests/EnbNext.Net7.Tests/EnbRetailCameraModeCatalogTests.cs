using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailCameraModeCatalogTests
{
    [Fact]
    public void CameraModeCatalogUsesRetailDefaultsConvertsAnglesAndStopsAtFirstMissingMode()
    {
        EnbRetailCameraModeCatalog catalog = EnbRetailCameraModeCatalog.ParseText("""
            [Mode00]
            Heading=180
            Distance=1.5
            Tilt=-90
            FOV=60
            ElevationOffset=45

            [Mode02]
            Heading=99
            """);

        EnbRetailCameraMode mode = Assert.Single(catalog.Modes);
        Assert.Equal(0, mode.Index);
        Assert.Equal(MathF.PI, mode.HeadingRadians, 5);
        Assert.Equal(1.5f, mode.Distance);
        Assert.Equal(-MathF.PI / 2f, mode.TiltRadians, 5);
        Assert.Equal(MathF.PI / 3f, mode.HorizontalFieldOfViewRadians, 5);
        Assert.Equal(MathF.PI / 4f, mode.ElevationOffsetRadians, 5);

        EnbRetailCameraModeCatalog defaults = EnbRetailCameraModeCatalog.ParseText("""
            [Mode00]
            """);
        EnbRetailCameraMode defaultMode = Assert.Single(defaults.Modes);
        Assert.Equal(0f, defaultMode.HeadingRadians);
        Assert.Equal(0f, defaultMode.Distance);
        Assert.Equal(0f, defaultMode.TiltRadians);
        Assert.Equal(MathF.PI / 2f, defaultMode.HorizontalFieldOfViewRadians, 5);
        Assert.Equal(0f, defaultMode.ElevationOffsetRadians);
    }

    [Fact]
    public void AssetBundleUsesSessionCameraOverrideThenFallsBackToStockSpacecam()
    {
        string root = CreateRetailRoot();
        try
        {
            string ini = Path.Combine(root, "Data", "client", "ini");
            string output = Path.Combine(root, "Data", "client", "output");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(ini, "spacecam.ini"), "[Mode00]\nFOV=80\n");

            uint fixedSessionId = 0x01000000;
            Assert.Equal(1, EnbRetailCameraModeCatalog.GetSessionOverrideSelector(fixedSessionId));
            string overridePath = Path.Combine(output, "cam1.ini");
            File.WriteAllText(overridePath, "[Mode00]\nFOV=70\n");

            EnbInstalledAssetPaths paths = EnbInstalledAssetLocator.TryResolveRoot(root)
                ?? throw new Xunit.Sdk.XunitException("Synthetic retail root was not resolved.");
            EnbRetailAssetBundle bundle = EnbRetailAssetBundle.Load(paths);

            EnbRetailCameraModeSelection selected = Assert.IsType<EnbRetailCameraModeSelection>(
                bundle.ResolveCameraModes(fixedSessionId));
            Assert.True(selected.UsesSessionOverride);
            Assert.Equal(1, selected.SessionOverrideSelector);
            Assert.Equal(overridePath, selected.Provenance.Path);
            Assert.Equal(70f * MathF.PI / 180f, Assert.Single(selected.Catalog.Modes).HorizontalFieldOfViewRadians, 5);

            EnbRetailCameraModeSelection fallback = Assert.IsType<EnbRetailCameraModeSelection>(
                bundle.ResolveCameraModes(0x02000000));
            Assert.False(fallback.UsesSessionOverride);
            Assert.Equal(2, fallback.SessionOverrideSelector);
            Assert.Equal(paths.SpaceCameraIniPath, fallback.Provenance.Path);
            Assert.Equal(80f * MathF.PI / 180f, Assert.Single(fallback.Catalog.Modes).HorizontalFieldOfViewRadians, 5);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRetailRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "enbnext-camera-" + Guid.NewGuid().ToString("N"));
        string ini = Path.Combine(root, "Data", "client", "ini");
        Directory.CreateDirectory(Path.Combine(root, "release"));
        Directory.CreateDirectory(ini);
        Directory.CreateDirectory(Path.Combine(root, "Data", "client", "mixfiles"));
        File.WriteAllBytes(Path.Combine(root, "release", "client.exe"), [0x4D, 0x5A]);
        File.WriteAllText(Path.Combine(ini, "basset.ini"), "[BASE-1]\nNULLOBJECT\n");
        return root;
    }
}
