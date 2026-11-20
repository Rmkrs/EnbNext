using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Current-retail graphics-settings values that exist before per-player options
/// are applied. A Windows migration layer may populate these from the installed
/// retail registry; other platforms may supply the migrated values. Rendering
/// never reads the Windows registry directly.
/// </summary>
public readonly record struct EnbRetailRenderBudgetSourceSettings(
    int PolyBudget,
    int StarBudget,
    int LandPolyBudget,
    int LandLod,
    int TextureReduction,
    int TextureFilter)
{
    public static EnbRetailRenderBudgetSourceSettings RetailExecutableDefaults =>
        new(
            PolyBudget: 10_000,
            StarBudget: 2_000,
            LandPolyBudget: 10_000,
            LandLod: 1,
            TextureReduction: 0,
            TextureFilter: 0);

    public EnbRetailRenderBudgetSourceSettings ClampLikeRetail()
        => new(
            Math.Clamp(PolyBudget, 8_000, 40_000),
            Math.Clamp(StarBudget, 500, 2_000),
            Math.Clamp(LandPolyBudget, 8_000, 40_000),
            Math.Clamp(LandLod, 0, 7),
            Math.Clamp(TextureReduction, 0, 3),
            Math.Clamp(TextureFilter, 0, 2));
}

/// <summary>
/// Effective current-retail presentation settings after applying the selected
/// per-player options. This only materializes the proven runtime inputs.
/// owns the shared projected-detail/HLOD allocator that consumes PolyBudget and
/// LandPolyBudget.
/// </summary>
public readonly record struct EnbRenderDetailRuntimeSettings(
    bool UsesRetailDefaultGraphics,
    float GammaFactor,
    int PolyBudget,
    int StarBudget,
    int LandPolyBudget,
    int LandLod,
    EnbRenderTextureRuntimeSettings Texture)
{
    private const float RetailDefaultGammaFactor = 1.2f;
    private const float RetailDefaultGammaOptionPercent = 10f;

    public static EnbRenderDetailRuntimeSettings FromRetailInputs(
        EnbRetailRenderBudgetSourceSettings sourceSettings,
        EnbRetailPlayerOptions playerOptions)
    {
        ArgumentNullException.ThrowIfNull(playerOptions);
        EnbRetailRenderBudgetSourceSettings source = sourceSettings.ClampLikeRetail();

        if (playerOptions.GraphicsDefault)
        {
            return new EnbRenderDetailRuntimeSettings(
                UsesRetailDefaultGraphics: true,
                GammaFactor: RetailDefaultGammaFactor,
                PolyBudget: source.PolyBudget,
                StarBudget: source.StarBudget,
                LandPolyBudget: source.LandPolyBudget,
                LandLod: source.LandLod,
                Texture: EnbRenderTextureRuntimeSettings.FromRetailValues(
                    source.TextureReduction,
                    source.TextureFilter));
        }

        // ConstructOptionsManager derives the numeric option defaults from the
        // current graphics-settings object before loading player*_options.ini.
        // Missing persisted FLOAT sections therefore retain these percentages.
        float gammaPercent = playerOptions.GraphicsGamma ?? RetailDefaultGammaOptionPercent;
        float landPolyPercent = playerOptions.GraphicsLandPoly ??
            NormalizeIntegerToPercent(source.LandPolyBudget, 8_000, 40_000);
        float landDetailPercent = playerOptions.GraphicsLandDetail ??
            NormalizeIntegerToPercent(source.LandLod, 0, 8);
        float spacePolyPercent = playerOptions.GraphicsSpacePoly ??
            NormalizeIntegerToPercent(source.PolyBudget, 8_000, 40_000);
        float textureSizePercent = playerOptions.GraphicsTextureSize ??
            100f - NormalizeIntegerToPercent(source.TextureReduction, 0, 3);

        // Preserve current x87 operation order. This matters at integer
        // conversion boundaries; e.g. 64.578125% land/space poly becomes
        // 28664 with the retail p*0.01*32000+8000 order, not 28665.
        float gammaFactor = Math.Clamp(((gammaPercent * 0.01f) * 2f) + 1f, 1f, 3f);
        int landPolyBudget = Math.Clamp(
            (int)(((landPolyPercent * 0.01f) * 32_000f) + 8_000f),
            8_000,
            40_000);
        int landLod = Math.Clamp(
            (int)((landDetailPercent * 0.01f) * 8f),
            0,
            7);
        int polyBudget = Math.Clamp(
            (int)(((spacePolyPercent * 0.01f) * 32_000f) + 8_000f),
            8_000,
            40_000);
        int textureReduction = Math.Clamp(
            (int)(((100f - textureSizePercent) * 0.01f) * 3f),
            0,
            3);

        return new EnbRenderDetailRuntimeSettings(
            UsesRetailDefaultGraphics: false,
            GammaFactor: gammaFactor,
            PolyBudget: polyBudget,
            StarBudget: source.StarBudget,
            LandPolyBudget: landPolyBudget,
            LandLod: landLod,
            Texture: EnbRenderTextureRuntimeSettings.FromRetailValues(
                textureReduction,
                source.TextureFilter));
    }

    /// <summary>
    /// Current MainView subtracts the cost already consumed by earlier scene
    /// layers before invoking the shared LOD allocator. This helper preserves
    /// that bounded arithmetic without implementing the allocator itself.
    /// </summary>
    public float RemainingSpacePolyBudget(float earlierLayerCost)
        => Math.Max(0f, PolyBudget - Math.Max(0f, earlierLayerCost));

    private static float NormalizeIntegerToPercent(int value, int minimum, int maximum)
    {
        if (maximum == minimum)
            return 0f;

        return (value - minimum) * 100f / (maximum - minimum);
    }
}
