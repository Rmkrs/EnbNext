using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AgCurrentRetailEnvironmentMapSelectionTests
{
    [Fact]
    public void SceneEnvironmentBaseAssetSelectsExplicitRetailBackSection()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [PROCTEX-00]
            BaseTexture=grid_env01.tga

            [BACK-DEFAULT]
            Texture=sky_env01.tga

            [BACK-378]
            Texture=sky_env15.tga
            Axis=Y
            """);

        bool resolved = EnbCurrentRetailEnvironmentMapRuntime.TryResolveReflection(
            catalog,
            Environment(baseAsset: 378),
            out EnbEnvironmentReflectionDefinition reflection,
            out string? diagnostic);

        Assert.True(resolved, diagnostic);
        Assert.Equal(378, reflection.BaseAssetId);
        Assert.Equal("sky_env15.tga", reflection.Texture);
        Assert.Equal(EnbEnvironmentMapAxis.Y, reflection.Axis);
        Assert.False(reflection.UsesDefaultTexture);
    }

    [Fact]
    public void MissingSceneEnvironmentFailsClosedInsteadOfSelectingAnObjectAsset()
    {
        EnbEnvironmentMapCatalog catalog = EnbEnvironmentMapCatalog.ParseText(
            """
            [BACK-DEFAULT]
            Texture=sky_env01.tga
            """);

        bool resolved = EnbCurrentRetailEnvironmentMapRuntime.TryResolveReflection(
            catalog,
            environment: null,
            out _,
            out string? diagnostic);

        Assert.False(resolved);
        Assert.Equal("scene environment is unavailable", diagnostic);
    }

    private static EnbSceneEnvironmentSnapshot Environment(short baseAsset)
        => new(
            BaseAsset: baseAsset,
            SwapBackdrop: false,
            FinalRawValue: 1065,
            RawServerParameters: ImmutableArray<byte>.Empty,
            BaseAssetReference: null,
            FogNear: 100000f,
            FogFar: 100000f,
            LightBackdrop: false,
            FogBackdrop: false,
            BackdropFogNear: 0f,
            BackdropFogFar: 0f,
            PresentationMode: 0,
            SplineUpdateCountAtLoad: 0);
}
