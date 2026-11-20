namespace EnbNext.Rendering;

/// <summary>Optional machine-wide scene effects; Classic is the default.</summary>
public readonly record struct EnbEnhancedGraphicsOptions(bool Enabled, bool Bloom, bool Hdr = false, bool Supersampling = false, bool AnisotropicFiltering = false)
{
    public static EnbEnhancedGraphicsOptions Default => new(false, true);
    public bool AnisotropicFilteringActive => Enabled && AnisotropicFiltering;
    public bool SupersamplingActive => Enabled && Supersampling;
    public bool HdrActive => Enabled && Hdr;
    public bool BloomActive => Enabled && Bloom;
}
