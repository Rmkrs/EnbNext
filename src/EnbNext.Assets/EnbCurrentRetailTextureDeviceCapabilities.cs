namespace EnbNext.Assets;

/// <summary>009AFEF0's active SDK8 caps and009B18C0/009B2540's finalized
/// texture support. The caller supplies the proven vendor classification and
/// actual CheckDeviceFormat results for its adapter/backbuffer format.</summary>
public readonly record struct EnbCurrentRetailTextureDeviceCapabilities(
    uint MaximumWidth,uint MaximumHeight,uint SupportedFormatBits,bool SupportsCompressed)
{
}
