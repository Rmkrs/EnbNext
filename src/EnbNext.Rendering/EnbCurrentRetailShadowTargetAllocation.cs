namespace EnbNext.Rendering;

/// <summary>Native texture format identifiers, not API-specific GPU enums.</summary>
public enum EnbCurrentRetailShadowTargetFormat
{
    Display = 0,
    R5G6B5 = 4,
    X1R5G5B5 = 5,
    A4R4G4B4 = 7,
    R3G3B2 = 8,
}

/// <summary>00498120 selects by device capabilities before allocating once.
/// A null allocation is handled by 00493D80's shadow.tga branch; it must not
/// cause another format to be tried. Display delegates to 00926790's current
/// display-format resolution. Capability discovery belongs to the GPU device.</summary>
public static class EnbCurrentRetailShadowTargetAllocation
{
    /// <summary>00493D80 retains either the capture target or the shared blob.
    /// Failure does not change the source's authored SimpleShadow flag.
    /// A missing optional blob leaves this source without a texture.</summary>
    public static EnbCurrentRetailShadowSourceTexture<TTarget, TBlob> CreateSource<TTarget, TBlob>(
        IReadOnlySet<EnbCurrentRetailShadowTargetFormat> supported,
        Func<EnbCurrentRetailShadowTargetFormat, TTarget?> allocate, Func<TBlob?> resolveBlob)
        where TTarget : class where TBlob : class
    {
        ArgumentNullException.ThrowIfNull(resolveBlob);
        var target = Create(supported, allocate);
        return new(target, target is null ? resolveBlob() : null);
    }

    public static T? Create<T>(IReadOnlySet<EnbCurrentRetailShadowTargetFormat> supported,
        Func<EnbCurrentRetailShadowTargetFormat, T?> allocate) where T : class
    {
        ArgumentNullException.ThrowIfNull(supported);
        ArgumentNullException.ThrowIfNull(allocate);
        var format = supported.Contains(EnbCurrentRetailShadowTargetFormat.R3G3B2)
            ? EnbCurrentRetailShadowTargetFormat.R3G3B2
            : supported.Contains(EnbCurrentRetailShadowTargetFormat.R5G6B5)
                ? EnbCurrentRetailShadowTargetFormat.R5G6B5
                : supported.Contains(EnbCurrentRetailShadowTargetFormat.A4R4G4B4)
                    ? EnbCurrentRetailShadowTargetFormat.A4R4G4B4
                    : supported.Contains(EnbCurrentRetailShadowTargetFormat.X1R5G5B5)
                        ? EnbCurrentRetailShadowTargetFormat.X1R5G5B5
                        : EnbCurrentRetailShadowTargetFormat.Display;
        return allocate(format);
    }
}

/// <summary>Target presence controls 0093DF00 capture; a blob never becomes a target.
/// Resource disposal belongs to the owning GPU device/texture cache.</summary>
public sealed record EnbCurrentRetailShadowSourceTexture<TTarget, TBlob>(TTarget? CaptureTarget, TBlob? Blob)
    where TTarget : class where TBlob : class;
