using EnbNext.Assets;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailPresentationDataCatalogTests
{
    [Fact]
    public void SwatchesRetainEmptySlotsAndSeparatePrototypeTokenFromFilename()
    {
        var catalog = EnbDecalDefinitionCatalog.ParseText("", """
            [Decal00]
            Swatch=decal00
            [Decal01]
            Swatch=
            [Decal02]
            Swatch=already.w3d
            [Decal04]
            Swatch=after_gap
            """, "");
        Assert.Equal(3, catalog.Swatches.Count);
        Assert.True(catalog.TryResolve(-1, out var first));
        Assert.Equal("decal00", first.SwatchPrototypeName);
        Assert.Equal("decal00.w3d", first.Resource);
        Assert.True(catalog.TryResolve(-2, out var empty));
        Assert.Null(empty.SwatchPrototypeName);
        Assert.Equal("", empty.Resource);
        Assert.True(catalog.TryResolve(-3, out var suffixed));
        Assert.Equal("already.w3d", suffixed.SwatchPrototypeName);
        Assert.Equal("already.w3d.w3d", suffixed.Resource);
        Assert.False(catalog.TryResolve(-4, out _));
        Assert.False(catalog.TryResolve(-5, out _));
    }
    [Fact]
    public void EnvironmentMapCatalogPreservesSequentialProceduralAndBackFallbackRules()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText("""
            [PROCTEX-00]
            BaseTexture=grid_env01.tga
            [PROCTEX-01]
            BaseTexture=grid_env02.tga
            [PROCTEX-03]
            BaseTexture=must_not_load.tga
            [BACK-DEFAULT]
            Texture=sky_default.tga
            [BACK-49]
            Texture=sky_49.tga
            Axis=Y
            [BACK-50]
            Axis=X
            [BACK-51]
            Axis=bogus
            """);

        Assert.Equal(2, catalog.ProceduralTextures.Count);
        Assert.Equal("grid_env02.tga", catalog.ProceduralTextures[1].BaseTexture);

        Assert.True(catalog.TryResolveReflection(49, out EnbEnvironmentReflectionDefinition explicitBack));
        Assert.Equal("sky_49.tga", explicitBack.Texture);
        Assert.Equal(EnbEnvironmentMapAxis.Y, explicitBack.Axis);
        Assert.False(explicitBack.UsesDefaultTexture);

        Assert.True(catalog.TryResolveReflection(50, out EnbEnvironmentReflectionDefinition fallbackBack));
        Assert.Equal("sky_default.tga", fallbackBack.Texture);
        Assert.Equal(EnbEnvironmentMapAxis.X, fallbackBack.Axis);
        Assert.True(fallbackBack.UsesDefaultTexture);

        Assert.True(catalog.TryResolveReflection(999, out EnbEnvironmentReflectionDefinition missingBack));
        Assert.Equal(EnbEnvironmentMapAxis.Z, missingBack.Axis);
        Assert.True(missingBack.UsesDefaultTexture);
    }

    [Fact]
    public void DecalCatalogPreservesSequentialFamiliesAndSignedNamespace()
    {
        EnbDecalDefinitionCatalog catalog = EnbDecalDefinitionCatalog.ParseText(
            """
            [Decal0000]
            File=one.tga
            TextureWrap=FALSE
            [Decal0001]
            File=two.tga
            [Decal0003]
            File=must_not_load.tga
            """,
            """
            [Decal00]
            Swatch=paint_red
            """,
            """
            [Decal0124]
            File=orphan_secret.tga
            """);

        Assert.Equal(2, catalog.NormalTextures.Count);
        Assert.Single(catalog.Swatches);
        Assert.Empty(catalog.SecretTextures); // retail stops at missing Decal0000

        Assert.True(catalog.TryResolve(0, out EnbDecalDefinition normal));
        Assert.Equal("one.tga", normal.Resource);
        Assert.False(normal.TextureWrap);
        Assert.True(catalog.TryResolve(-1, out EnbDecalDefinition swatch));
        Assert.Equal("paint_red.w3d", swatch.Resource);
        Assert.False(catalog.TryResolve(1000, out _));
    }

    [Fact]
    public void GutterAndMetalCatalogsParseInstalledRgbFormsWithoutHardcodingValues()
    {
        EnbGutterColorCatalog gutter = EnbGutterColorCatalog.ParseText("""
            [Neutral]
            Target1=0.99,0.99,0.99 ; White
            NotTarget1=0.43,0.65,0.69 ; Dark Silver
            """);
        Assert.True(gutter.TryResolve("Neutral", "NotTarget1", out EnbRgb neutral));
        Assert.Equal(new EnbRgb(0.43f, 0.65f, 0.69f), neutral);

        EnbMetalEquivalentRgbCatalog metals = EnbMetalEquivalentRgbCatalog.ParseText("""
            [Metal00]
            R=1.0
            G=1.0
            B=1.0
            [Metal02]
            R=0.7765
            G=0.3843
            B=0.1922
            """);
        Assert.True(metals.TryResolve(2, out EnbRgb copper));
        Assert.Equal(new EnbRgb(0.7765f, 0.3843f, 0.1922f), copper);
        Assert.False(metals.TryResolve(1, out _));
    }

    [Fact]
    public void MetalRgbDefaultsEachComponentAndUsesRetailNumericPrefixAndPercent()
    {
        var metals = EnbMetalEquivalentRgbCatalog.ParseText("""
            [Metal02]
            R=25% trailing
            B=invalid
            [Metal03]
            R=invalid%
            G=0.5 authored suffix
            B=1e50
            """);
        Assert.Equal(new EnbRgb(.25f, 1, 1), metals.ResolveOrDefault(2));
        var overflow = metals.ResolveOrDefault(3);
        Assert.Equal(.01f, overflow.R);
        Assert.Equal(.5f, overflow.G);
        Assert.Equal(float.PositiveInfinity, overflow.B);
        Assert.Equal(new EnbRgb(1, 1, 1), metals.ResolveOrDefault(99));
    }

    [Fact]
    public void FontCatalogAppliesTemplatesThenSelectsLastProfileThatFits()
    {
        EnbFontSystemCatalog fonts = EnbFontSystemCatalog.ParseText("""
            [INIT-0]
            ResWidth=640
            ResHeight=480
            Font0=font_tiny5.tga
            FontID0=FONT_TINY
            Scale0=1.0
            Spacing0=-1

            [INIT-1]
            ResWidth=800
            ResHeight=600
            Template0=INIT-0
            Font0=font_small9.tga
            Spacing0=0

            [INIT-2]
            ResWidth=1024
            ResHeight=768
            Template0=INIT-1
            Font0=font_small11.tga
            """);

        Assert.True(fonts.TrySelectProfile(900, 700, out EnbFontProfile selected));
        Assert.Equal(1, selected.Index);
        EnbFontDefinition tiny = Assert.Single(selected.Fonts);
        Assert.Equal("FONT_TINY", tiny.FontId);
        Assert.Equal("font_small9.tga", tiny.Resource);
        Assert.Equal(0, tiny.Spacing);

        Assert.True(fonts.TrySelectProfile(1920, 1080, out selected));
        Assert.Equal(2, selected.Index);
        Assert.Equal("FONT_TINY", Assert.Single(selected.Fonts).FontId);
    }

    [Fact]
    public void InterfaceLayoutCatalogResolvesRadarStringIdThroughTemplateInheritance()
    {
        EnbInterfaceLayoutCatalog layout = EnbInterfaceLayoutCatalog.ParseText("""
            [TEMPLATE_RADAR]
            Color=YELLOW
            FontType=FONT_TINY
            BlendMode=ADDITIVE
            Offset=0.0,0.0

            [STR-0]
            StringID=RADAR_NAME
            Template0=TEMPLATE_RADAR
            Justify=BOTTOM,CENTER

            [STR-1]
            StringID=RADAR_PLAYER
            Template0=TEMPLATE_RADAR
            Color=BLUE
            """);

        Assert.True(layout.TryResolve("RADAR_NAME", out EnbInterfaceTextDefinition radarName));
        Assert.Equal(EnbInterfaceDefinitionKind.String, radarName.Kind);
        Assert.Equal("FONT_TINY", radarName.Properties["FontType"]);
        Assert.Equal("YELLOW", radarName.Properties["Color"]);
        Assert.Equal("BOTTOM,CENTER", radarName.Properties["Justify"]);

        Assert.True(layout.TryResolve("RADAR_PLAYER", out EnbInterfaceTextDefinition player));
        Assert.Equal("BLUE", player.Properties["Color"]);
        Assert.Equal("ADDITIVE", player.Properties["BlendMode"]);
    }
}
