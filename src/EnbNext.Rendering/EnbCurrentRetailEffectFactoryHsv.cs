using System.Numerics;
using System.Globalization;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailEffectFactoryHsv
{
    public static bool TryRead(EnbCurrentRetailEffectOwner node, out Vector3 hsv, out string? error, bool applyPrototypeThreshold = true)
        =>TryRead(node.Definition.Properties,node.Root.HsvShift+node.Root.EffectOffset,out hsv,out error,applyPrototypeThreshold);

    public static bool TryRead(IReadOnlyDictionary<string,string> p,Vector3 incoming,out Vector3 hsv,out string? error,
        bool applyPrototypeThreshold=true)
    {
        error = null;
        // 00734EB2 / 00735345 pass the 0009/000B optional vector to
        // 006E9490, where 006E9642..57 adds it to RObjHSV. The legacy
        // transport name EffectOffset does not describe a spatial offset.
        // Other packet families already publish this input as HsvShift.
        hsv = incoming;
        if (p.TryGetValue("RObjHSV", out var authoredHsv) && !string.IsNullOrWhiteSpace(authoredHsv))
        {
            string[] parts = authoredHsv.Split(',');
            if (parts.Length != 3 ||
                !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float h) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float s) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
            { error = "Effect authored HSV unavailable"; return false; }
            // 006E9642 / 006E964C / 006E9657: add the incoming root vector.
            hsv += new Vector3(h, s, v);
        }
        if (!float.IsFinite(hsv.X) || !float.IsFinite(hsv.Y) || !float.IsFinite(hsv.Z))
        { error = "Effect HSV must be finite"; return false; }
        //006E9642 preserves the full sum.004904B0 wraps hue once and leaves
        //RGB unchanged for an unhandled sector; do not reject or normalize it.
        // 0048B220 tests the complete vector before dispatching its adjustment.
        // Procedural SegLine colours bypass this prototype gate; their texture
        // lookup (0048B7B0) applies the same threshold independently.
        if (applyPrototypeThreshold && !EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv)) hsv = Vector3.Zero;
        return true;
    }
}
