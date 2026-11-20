using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// Current retail's user-facing TextureFilter setting after registry clamping.
/// On a capable device the three values resolve to bilinear, trilinear and
/// anisotropic filtering respectively. The numeric identities are retained so
/// settings migration does not depend on UI labels.
/// </summary>
public enum EnbRenderTextureFilterMode : byte
{
    Bilinear = 0,
    Trilinear = 1,
    Anisotropic = 2,
}

public enum EnbRenderTextureMinMagFilter : byte
{
    Point = 1,
    Linear = 2,
    Anisotropic = 3,
}

public enum EnbRenderTextureMipFilter : byte
{
    None = 0,
    Point = 1,
    Linear = 2,
}

/// <summary>
/// Runtime texture settings owned by the selected user/install rather than by
/// immutable W3D data. Current retail clamps TextureReduction to 0..3 and
/// TextureFilter to 0..2 when reading its graphics settings.
/// </summary>
public readonly record struct EnbRenderTextureRuntimeSettings(
    int TextureReduction,
    EnbRenderTextureFilterMode TextureFilter)
{
    public static EnbRenderTextureRuntimeSettings RetailDefaults =>
        new(0, EnbRenderTextureFilterMode.Bilinear);

    public static EnbRenderTextureRuntimeSettings FromRetailValues(
        int textureReduction,
        int textureFilter)
        => new(
            Math.Clamp(textureReduction, 0, 3),
            (EnbRenderTextureFilterMode)Math.Clamp(textureFilter, 0, 2));
}

/// <summary>
/// Small capability seam retained because current retail degrades its
/// anisotropic mode when the device lacks that capability. Point/linear
/// filtering is a required baseline for the modern backend.
/// </summary>
public readonly record struct EnbRenderTextureBackendCapabilities(
    bool SupportsAnisotropicFiltering)
{
    public static EnbRenderTextureBackendCapabilities ModernDefault => new(true);
}

/// <summary>
/// Exact sampler state required for one W3D texture binding after combining
/// authored TextureInfo, the retail '+' filename override and the runtime
/// TextureFilter setting.
/// </summary>
public readonly record struct EnbW3dTextureSamplerSemantics(
    EnbRenderTextureAddressMode AddressU,
    EnbRenderTextureAddressMode AddressV,
    EnbRenderTextureMinMagFilter MinFilter,
    EnbRenderTextureMinMagFilter MagFilter,
    EnbRenderTextureMipFilter MipFilter,
    int MaximumAnisotropy,
    bool UsesPlusNameFilterOverride);

public static class EnbW3dTextureSamplerCompiler
{
    public static EnbW3dTextureSamplerSemantics ApplySceneAnisotropy(
        EnbW3dRenderTextureBinding binding, EnbW3dTextureSamplerSemantics sampler,
        int mipCount, bool blended, int deviceLimit)
    {
        if(deviceLimit<=1 || blended || mipCount<2 || sampler.UsesPlusNameFilterOverride ||
            sampler.MipFilter==EnbRenderTextureMipFilter.None || sampler.MinFilter==EnbRenderTextureMinMagFilter.Point ||
            binding.Sampling.CurrentRetailMinMagOverride.HasValue || binding.Sampling.CurrentRetailMipOverride.HasValue)
            return sampler;
        return sampler with {MaximumAnisotropy=Math.Min(16,deviceLimit),MipFilter=EnbRenderTextureMipFilter.Linear};
    }
    public static EnbW3dTextureSamplerSemantics Compile(
        EnbW3dRenderTextureBinding binding,
        EnbRenderTextureRuntimeSettings settings,
        EnbRenderTextureBackendCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(binding);
        settings = EnbRenderTextureRuntimeSettings.FromRetailValues(
            settings.TextureReduction,
            (int)settings.TextureFilter);

        bool plusOverride = binding.LogicalName.Contains('+');
        EnbRenderTextureMinMagFilter minMag;
        EnbRenderTextureMipFilter mip;

        if (binding.Sampling.CurrentRetailMinMagOverride is EnbRenderTextureMinMagFilter runtimeMinMag)
        {
            minMag = runtimeMinMag;
            mip = settings.TextureFilter == EnbRenderTextureFilterMode.Bilinear
                ? EnbRenderTextureMipFilter.Point
                : EnbRenderTextureMipFilter.Linear;
        }
        else if (plusOverride)
        {
            // Current retail forces object selector 1 for MIN/MAG when '+' is
            // present. Selector 1 is fixed LINEAR/LINEAR/POINT and does not
            // follow the global TextureFilter table.
            minMag = EnbRenderTextureMinMagFilter.Linear;
            mip = EnbRenderTextureMipFilter.Point;
        }
        else
        {
            minMag = settings.TextureFilter == EnbRenderTextureFilterMode.Anisotropic &&
                     capabilities.SupportsAnisotropicFiltering
                ? EnbRenderTextureMinMagFilter.Anisotropic
                : EnbRenderTextureMinMagFilter.Linear;

            mip = settings.TextureFilter == EnbRenderTextureFilterMode.Bilinear
                ? EnbRenderTextureMipFilter.Point
                : EnbRenderTextureMipFilter.Linear;
        }

        // Current W3D texture construction sets the mip selector to NONE when
        // RetailCreationSelector == 1. The '+' path does not override that
        // special case.
        if (binding.Sampling.RetailCreationSelector == 1)
            mip = EnbRenderTextureMipFilter.None;
        if (binding.Sampling.CurrentRetailMipOverride is {} runtimeMip)
            mip = runtimeMip;

        int maximumAnisotropy = minMag == EnbRenderTextureMinMagFilter.Anisotropic ? 2 : 1;
        return new EnbW3dTextureSamplerSemantics(
            binding.Sampling.AddressU,
            binding.Sampling.AddressV,
            minMag,
            minMag,
            mip,
            maximumAnisotropy,
            plusOverride);
    }
}

/// <summary>
/// Runtime upload plan corresponding to current retail's TextureReduction
/// behavior for a decoded source texture. FirstSourceMip is the authored source
/// level used as the uploaded base image. UploadMipCount excludes the two
/// sub-4x4 tail levels current retail omits from compressed DDS chains.
/// </summary>
public readonly record struct EnbTextureUploadMipPlan(
    int FirstSourceMip,
    int UploadMipCount,
    int Width,
    int Height)
{
    public bool UsesReducedBase => FirstSourceMip != 0;
}

public static class EnbTextureUploadMipPlanner
{
    public static EnbTextureUploadMipPlan Compile(
        EnbTextureImage image,
        EnbRenderTextureRuntimeSettings settings,
        bool applyLegacyLargeTextureCapabilityFallback = false,
        byte retailCreationSelector = 0,
        uint? retailCreationMipLevels = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        settings = EnbRenderTextureRuntimeSettings.FromRetailValues(
            settings.TextureReduction,
            (int)settings.TextureFilter);

        int sourceMipCount = image.MipLevels.Count;
        if (sourceMipCount == 0)
            throw new InvalidDataException("Decoded texture has no mip levels.");

        int firstMip = 0;
        uint creationMipLevels=retailCreationMipLevels??retailCreationSelector;
        if (image.Format is not (EnbTextureFormat.CurrentRetailRuntimeRgb8 or EnbTextureFormat.CurrentRetailMissingArgb8 or EnbTextureFormat.CurrentRetailPackedSurface) &&
            sourceMipCount != 1 && image.Width > 32 && image.Height > 32)
        {
            //0094BC20 bypasses reduction for the one-level creation path.
            firstMip = EnbCurrentRetailTextureSurfaceRequirements.SelectReduction(
                image.Width,image.Height,unchecked((int)creationMipLevels),
                settings.TextureReduction,applyLegacyLargeTextureCapabilityFallback);

            // Deliberate correction of retail's partial-chain over-reduction:
            //94A890 shifts metadata by the request, while9A0870 uses request-1
            //when it reaches the source count.9A0D30 can then leave the surface
            //unwritten or read beyond its offset table. Use the last authored
            //decoded mip instead; the literal source model retains that behavior.
            firstMip = Math.Min(firstMip, sourceMipCount - 1);
        }

        int remaining = sourceMipCount - firstMip;
        bool compressedRetailDds = image.Format is EnbTextureFormat.DdsDxt1 or EnbTextureFormat.DdsDxt2 or EnbTextureFormat.DdsDxt3 or EnbTextureFormat.DdsDxt4 or EnbTextureFormat.DdsDxt5;
        int uploadMipCount = compressedRetailDds
            ? (remaining < 3 ? 1 : remaining - 2)
            : remaining;
        EnbTextureMipLevel baseLevel = image.MipLevels[firstMip];
        //0094A833..866 caps every creation count and the shorter block
        // dimension, not only selector1. Already allocated surfaces bypass it.
        if(compressedRetailDds)uploadMipCount=checked((int)
            EnbCurrentRetailTextureSurfaceRequirements.SelectCompressedMipCount(
                (uint)baseLevel.Width,(uint)baseLevel.Height,(uint)uploadMipCount,creationMipLevels));
        return new EnbTextureUploadMipPlan(
            firstMip,
            uploadMipCount,
            baseLevel.Width,
            baseLevel.Height);
    }
}
