using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Configured RenderObj identity from the shared native asset formatter.</summary>
public static class EnbCurrentRetailPrototypeVariantName
{
    public static bool ChangesScale(float scale) => EnbCurrentRetailAssetVariantName.ChangesScale(scale);
    public static bool ChangesHsv(Vector3 hsv) => EnbCurrentRetailAssetVariantName.ChangesHsv(hsv);
    public static string Create(string name, float scale, Vector3 hsv)
        => EnbCurrentRetailAssetVariantName.Create(name, scale, hsv);
}
