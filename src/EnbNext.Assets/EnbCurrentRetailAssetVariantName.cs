using System.Numerics;

namespace EnbNext.Assets;

/// <summary>0048B220's named variant identity, including the native float32
/// promotion / CRT17-digit conversion / six-digit %g rounding path.</summary>
public static class EnbCurrentRetailAssetVariantName
{
    public static bool ChangesScale(float scale) => MathF.Abs(scale - 1) > .01f;
    public static bool ChangesHsv(Vector3 hsv)
        => MathF.Abs(hsv.X) > 1 || MathF.Abs(hsv.Y) > .01f || MathF.Abs(hsv.Z) > .01f;

    public static string Create(string name, float scale, Vector3 hsv)
        => $"#{Fold(name)}!{Format(scale)}H{Format(hsv.X)}S{Format(hsv.Y)}V{Format(hsv.Z)}";

    /// <summary>0048B7B0/0048CB40, literal00B71F54.</summary>
    public static string CreateTexture(string name, Vector3 hsv)
        => $"#{Fold(name)}!H{Format(hsv.X)}S{Format(hsv.Y)}V{Format(hsv.Z)}";

    private static string Fold(string name)
    {
        //00A27F25's C-locale branch folds ASCII only, preserving other bytes.
        var folded = name.ToCharArray();
        for (int i = 0; i < folded.Length; i++)
            if (folded[i] is >= 'A' and <= 'Z') folded[i] += (char)32;
        return new string(folded);
    }

    //0048B220 formats each field with "%g" through the static CRT sprintf.
    public static string Format(float value) => EnbRetailCrtFormat.Format("%g", value)!;
}
