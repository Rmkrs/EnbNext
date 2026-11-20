using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5ARuntimeGraphicsBudgetTests
{
    [Fact]
    public void PlayerOptionsParserReadsCurrentBoolAndFloatGraphicsSections()
    {
        EnbRetailPlayerOptions options = EnbRetailPlayerOptionsCatalog.ParseText("""
            [BOOL-000]
            OptionName=Bridge_Mode
            OptionValue=yes

            [BOOL-026]
            OptionName=Graphics_Default
            OptionValue=no

            [FLOAT-000]
            OptionName=Graphics_Space_Poly
            OptionValue=99.000000

            [FLOAT-001]
            OptionName=Graphics_Texture_Size
            OptionValue=88.500000

            [FLOAT-003]
            OptionName=Graphics_Land_Poly
            OptionValue=77.250000

            [FLOAT-005]
            OptionName=Graphics_Land_Detail
            OptionValue=66.125000

            [FLOAT-016]
            OptionName=Graphics_Gamma
            OptionValue=10.000002

            [FLOAT-017]
            OptionName=Radar_Gutter_Opacity
            OptionValue=0.625

            [FLOAT-999]
            OptionName=Unrelated_Value
            OptionValue=123.000000
            """);

        Assert.True(options.BridgeMode);
        Assert.False(options.GraphicsDefault);
        Assert.Equal(10.000002f, options.GraphicsGamma!.Value);
        Assert.Equal(77.25f, options.GraphicsLandPoly!.Value);
        Assert.Equal(66.125f, options.GraphicsLandDetail!.Value);
        Assert.Equal(99f, options.GraphicsSpacePoly!.Value);
        Assert.Equal(88.5f, options.GraphicsTextureSize!.Value);
        Assert.Equal(0.625f, options.RadarGutterOpacity);
        Assert.Equal(
            0.75f,
            EnbRetailPlayerOptionsCatalog.ParseText(string.Empty).RadarGutterOpacity);
    }

    [Fact]
    public void CustomGraphicsConvertsPercentOptionsWithCurrentRetailTruncationAndClamps()
    {
        var source = new EnbRetailRenderBudgetSourceSettings(
            PolyBudget: 12_345,
            StarBudget: 1_234,
            LandPolyBudget: 23_456,
            LandLod: 4,
            TextureReduction: 2,
            TextureFilter: 1);
        var options = new EnbRetailPlayerOptions(
            BridgeMode: false,
            GraphicsDefault: false,
            GraphicsGamma: 10.000002f,
            GraphicsLandPoly: 99f,
            GraphicsLandDetail: 99f,
            GraphicsSpacePoly: 99f,
            GraphicsTextureSize: 99f);

        EnbRenderDetailRuntimeSettings actual =
            EnbRenderDetailRuntimeSettings.FromRetailInputs(source, options);

        Assert.False(actual.UsesRetailDefaultGraphics);
        Assert.Equal(1.20000005f, actual.GammaFactor, 6);
        Assert.Equal(39_680, actual.LandPolyBudget);
        Assert.Equal(7, actual.LandLod);
        Assert.Equal(39_680, actual.PolyBudget);
        Assert.Equal(1_234, actual.StarBudget);
        Assert.Equal(0, actual.Texture.TextureReduction);
        Assert.Equal(EnbRenderTextureFilterMode.Trilinear, actual.Texture.TextureFilter);
    }

    [Fact]
    public void CustomPolyConversionPreservesRetailMultiplyOrderAtTruncationBoundary()
    {
        var options = new EnbRetailPlayerOptions(
            BridgeMode: false,
            GraphicsDefault: false,
            GraphicsLandPoly: 64.578125f,
            GraphicsSpacePoly: 64.578125f);

        EnbRenderDetailRuntimeSettings actual =
            EnbRenderDetailRuntimeSettings.FromRetailInputs(
                EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults,
                options);

        // Current retail performs p * 0.01, then * 32000, then + 8000
        // before __ftol truncation. Reassociating 32000 * p * 0.01 yields
        // 28665 for this exact float and is therefore observably wrong.
        Assert.Equal(28_664, actual.LandPolyBudget);
        Assert.Equal(28_664, actual.PolyBudget);
    }

    [Fact]
    public void DefaultGraphicsUsesClampedRetailSourceAndExecutableGamma()
    {
        var source = new EnbRetailRenderBudgetSourceSettings(
            PolyBudget: 99_999,
            StarBudget: 1,
            LandPolyBudget: 7_000,
            LandLod: 99,
            TextureReduction: 99,
            TextureFilter: 99);

        EnbRenderDetailRuntimeSettings actual =
            EnbRenderDetailRuntimeSettings.FromRetailInputs(
                source,
                new EnbRetailPlayerOptions(BridgeMode: false, GraphicsDefault: true));

        Assert.True(actual.UsesRetailDefaultGraphics);
        Assert.Equal(1.2f, actual.GammaFactor);
        Assert.Equal(40_000, actual.PolyBudget);
        Assert.Equal(500, actual.StarBudget);
        Assert.Equal(8_000, actual.LandPolyBudget);
        Assert.Equal(7, actual.LandLod);
        Assert.Equal(3, actual.Texture.TextureReduction);
        Assert.Equal(EnbRenderTextureFilterMode.Anisotropic, actual.Texture.TextureFilter);
    }

    [Fact]
    public void MissingCustomFloatsRetainConstructOptionsManagerDefaultsDerivedFromSource()
    {
        EnbRetailRenderBudgetSourceSettings source =
            EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults;
        var options = new EnbRetailPlayerOptions(
            BridgeMode: false,
            GraphicsDefault: false);

        EnbRenderDetailRuntimeSettings actual =
            EnbRenderDetailRuntimeSettings.FromRetailInputs(source, options);

        Assert.False(actual.UsesRetailDefaultGraphics);
        Assert.Equal(1.2f, actual.GammaFactor, 6);
        Assert.Equal(10_000, actual.PolyBudget);
        Assert.Equal(2_000, actual.StarBudget);
        Assert.Equal(10_000, actual.LandPolyBudget);
        Assert.Equal(1, actual.LandLod);
        Assert.Equal(0, actual.Texture.TextureReduction);
        Assert.Equal(EnbRenderTextureFilterMode.Bilinear, actual.Texture.TextureFilter);
    }



    [Fact]
    public void GraphicsSaveIsCharacterScopedAtomicAndPreservesOtherOptions()
    {
        var installed = EnbInstalledAssetLocator.TryLocate();
        if (installed is null) return;
        string temporary = Path.Combine(Path.GetTempPath(), "EnbNext-graphics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var assets = EnbRetailAssetBundle.Load(installed with { ClientOutputDirectory = temporary });
            const uint character = 12345;
            string path = Path.Combine(temporary,
                $"player{EnbRetailPlayerOptionsCatalog.GetCurrentCompatibilitySelector(character)}_options.ini");
            File.WriteAllText(path, "; preserve me\n[FLOAT-012]\nOptionName=Sound_SFX\nOptionValue=42\n");

            assets.SaveGraphicsOptions(character, new(false, 17.5f, -5f, 50f, 101f, 75.25f));

            EnbRetailPlayerOptions options = assets.ResolvePlayerOptions(character).Options;
            Assert.False(options.GraphicsDefault);
            Assert.Equal(17.5f, options.GraphicsGamma);
            Assert.Equal(0f, options.GraphicsLandPoly);
            Assert.Equal(50f, options.GraphicsLandDetail);
            Assert.Equal(100f, options.GraphicsSpacePoly);
            Assert.Equal(75.25f, options.GraphicsTextureSize);
            Assert.Equal(42f, options.SoundSfx);
            Assert.Contains("; preserve me", File.ReadAllText(path));
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Fact]
    public void GraphicsPreviewChangesEffectiveSettingsWithoutWritingUntilCommit()
    {
        var installed = EnbInstalledAssetLocator.TryLocate();
        if (installed is null) return;
        string temporary = Path.Combine(Path.GetTempPath(), "EnbNext-graphics-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var assets = EnbRetailAssetBundle.Load(installed with { ClientOutputDirectory = temporary });
            const uint character = 12345;
            var baseline = assets.ResolvePlayerOptions(character).Options;
            var preview = new EnbRetailGraphicsOptions(false, 75f, 50f, 25f, 80f, 30f);
            assets.SetGraphicsPreview(character, preview);

            var current = assets.ResolvePlayerOptions(character).Options;
            Assert.False(current.GraphicsDefault);
            Assert.Equal(75f, current.GraphicsGamma);
            Assert.Equal(80f, current.GraphicsSpacePoly);
            Assert.Equal(30f, current.GraphicsTextureSize);
            Assert.NotEqual(
                EnbRenderDetailRuntimeSettings.FromRetailInputs(EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults, baseline),
                EnbRenderDetailRuntimeSettings.FromRetailInputs(EnbRetailRenderBudgetSourceSettings.RetailExecutableDefaults, current));
            Assert.Empty(Directory.GetFiles(temporary));

            assets.SetGraphicsPreview(character, null);
            Assert.Equal(baseline, assets.ResolvePlayerOptions(character).Options);
            Assert.Empty(Directory.GetFiles(temporary));

            assets.SetGraphicsPreview(character, preview);
            assets.SaveGraphicsOptions(character, preview);
            assets.SetGraphicsPreview(character, null);
            Assert.Equal(75f, assets.ResolvePlayerOptions(character).Options.GraphicsGamma);
            Assert.Single(Directory.GetFiles(temporary));
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Fact]
    public void R5ARemainsCrossPlatformAndDoesNotSelectHlodYet()
    {
        string root = FindRepositoryRoot();
        string detailSettings = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Rendering",
            "EnbRenderDetailRuntimeSettings.cs"));
        string playerOptions = File.ReadAllText(Path.Combine(
            root,
            "src",
            "EnbNext.Assets",
            "EnbRetailPlayerOptionsCatalog.cs"));

        Assert.DoesNotContain("Microsoft.Win32", detailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry", detailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("EnbW3dHlodSelection", detailSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("Vulkan", detailSettings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", detailSettings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Direct3D", detailSettings, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FLOAT-", playerOptions, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EnbNext.slnx")))
                return current.FullName;
            current = current.Parent;
        }

        throw new Xunit.Sdk.XunitException("Could not locate EnbNext repository root.");
    }
}
