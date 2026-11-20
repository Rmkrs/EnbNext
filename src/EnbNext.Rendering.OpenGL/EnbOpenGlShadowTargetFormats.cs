using System.Collections.Frozen;

namespace EnbNext.Rendering.OpenGL;

/// <summary>Format mappings implemented by the GLES 3 core capture backend.
/// ES 3.0 tables 3.2/3.13 require RGB565 and RGBA4 to be filterable,
/// color-renderable textures with these upload formats. No allocation probes
/// or source-target retries are involved. Other API profiles remain unresolved.</summary>
public static class EnbOpenGlShadowTargetFormats
{
    private static readonly IReadOnlyDictionary<EnbCurrentRetailShadowTargetFormat, (int Internal, int Pixel)> Es3 =
        new Dictionary<EnbCurrentRetailShadowTargetFormat, (int, int)>
        {
            [EnbCurrentRetailShadowTargetFormat.R5G6B5] = (0x8D62, 0x1907),
            [EnbCurrentRetailShadowTargetFormat.A4R4G4B4] = (0x8056, 0x1908),
        }.ToFrozenDictionary();

    // RGB5_A1 is not X1R5G5B5: its stored alpha has different semantics.
    // R3G3B2 and RGB5 have no GLES 3 core sized texture formats.
    public static IReadOnlyDictionary<EnbCurrentRetailShadowTargetFormat, (int Internal, int Pixel)>?
        ResolveCoreMappings(GlVersion version)
        => version.Type == GlProfileType.OpenGLES && version.Major >= 3 ? Es3 : null;
}
