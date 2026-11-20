using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>
/// One current-retail bump-source mip after the retail RGB->DU/DV/L conversion
/// has been made explicit. R/G are a modern unsigned encoding of the signed
/// 8-bit DU/DV values (signedByte + 128), B is the retail luminance byte, and
/// A is opaque. The affine R/G encoding keeps filtered perturbations linear on
/// GL backends that do not expose the legacy D3D bump formats.
/// </summary>
public sealed record EnbW3dBumpSemanticMip(
    int SourceMipIndex,
    int Width,
    int Height,
    ImmutableArray<EnbColor32> Pixels);

public sealed record EnbW3dBumpSemanticTexture(
    ImmutableArray<EnbW3dBumpSemanticMip> Mips);

/// <summary>
/// Reproduces the current client.exe ConvertTextureSurfacePixels bump-format
/// source conversion for the authored/upload-selected mip chain. Interior and
/// cross-row horizontal neighbors follow the current tight linear source walk.
/// The current binary can read one pixel outside the source allocation at the
/// absolute first/last pixel; EnbNext deliberately clamps only those two unsafe
/// allocation-edge reads to the current pixel instead of reproducing undefined
/// adjacent-allocation residue.
/// </summary>
public static class EnbW3dBumpTextureCompiler
{
    private const int BlueLumaWeight = 0x1275;
    private const int GreenLumaWeight = 0xB725;
    private const int RedLumaWeight = 0x3666;

    public static bool TryCompile(
        EnbTextureImage image,
        EnbTextureUploadMipPlan uploadMipPlan,
        out EnbW3dBumpSemanticTexture texture,
        out string? diagnostic)
    {
        ArgumentNullException.ThrowIfNull(image);
        texture = default!;
        diagnostic = null;

        if (uploadMipPlan.UploadMipCount <= 0 || uploadMipPlan.FirstSourceMip < 0)
        {
            diagnostic = "Bump texture has an invalid R4J upload mip plan.";
            return false;
        }
        if (uploadMipPlan.FirstSourceMip + uploadMipPlan.UploadMipCount > image.MipLevels.Count)
        {
            diagnostic = "Bump texture upload mip plan exceeds its decoded authored mip chain.";
            return false;
        }

        var mips = ImmutableArray.CreateBuilder<EnbW3dBumpSemanticMip>(uploadMipPlan.UploadMipCount);
        for (int uploadLevel = 0; uploadLevel < uploadMipPlan.UploadMipCount; uploadLevel++)
        {
            int sourceMipIndex = uploadMipPlan.FirstSourceMip + uploadLevel;
            EnbTextureMipLevel source = image.MipLevels[sourceMipIndex];
            if (source.Width <= 0 || source.Height <= 0)
            {
                diagnostic = $"Bump source mip {sourceMipIndex} has invalid dimensions {source.Width}x{source.Height}.";
                return false;
            }

            ReadOnlySpan<EnbColor32> pixels = source.Pixels.Span;
            int pixelCount = checked(source.Width * source.Height);
            if (pixels.Length != pixelCount)
            {
                diagnostic = $"Bump source mip {sourceMipIndex} has {pixels.Length} pixels for {source.Width}x{source.Height}.";
                return false;
            }

            var encoded = ImmutableArray.CreateBuilder<EnbColor32>(pixelCount);
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    int index = checked(y * source.Width + x);
                    int current = ComputeCurrentRetailLuma(pixels[index]);

                    // Current client.exe walks the horizontal neighbors as
                    // sourcePointer +/- bytesPerPixel. On a tight source this
                    // crosses row boundaries at x=0/width-1. Preserve that
                    // observable walk wherever the address remains in-bounds.
                    int leftIndex = index > 0 ? index - 1 : index;
                    int rightIndex = index + 1 < pixelCount ? index + 1 : index;
                    int previousRowIndex = y > 0 ? index - source.Width : index;
                    int nextRowIndex = y + 1 < source.Height ? index + source.Width : index;

                    int left = ComputeCurrentRetailLuma(pixels[leftIndex]);
                    int right = ComputeCurrentRetailLuma(pixels[rightIndex]);
                    int previousRow = ComputeCurrentRetailLuma(pixels[previousRowIndex]);
                    int nextRow = ComputeCurrentRetailLuma(pixels[nextRowIndex]);

                    int du = left - right;
                    if (current < left && current < right)
                        du = left - current;
                    int dv = nextRow - previousRow;
                    byte luminance = current <= 1 ? (byte)127 : (byte)63;

                    encoded.Add(new EnbColor32(
                        EncodeSignedDeltaAsUnorm(du),
                        EncodeSignedDeltaAsUnorm(dv),
                        luminance,
                        255));
                }
            }

            mips.Add(new EnbW3dBumpSemanticMip(
                sourceMipIndex,
                source.Width,
                source.Height,
                encoded.ToImmutable()));
        }

        texture = new EnbW3dBumpSemanticTexture(mips.ToImmutable());
        return true;
    }

    public static int ComputeCurrentRetailLuma(EnbColor32 pixel)
        => checked((pixel.B * BlueLumaWeight + pixel.G * GreenLumaWeight + pixel.R * RedLumaWeight) >> 16);

    public static byte EncodeSignedDeltaAsUnorm(int delta)
    {
        sbyte wrapped = unchecked((sbyte)delta);
        return checked((byte)(wrapped + 128));
    }
}
