using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbRetailSeekEffectConstruction
{
    public static EnbRetailSeekEffectPath? Spline(EnbEffectDefinition definition, uint duration)
    {
        if (definition.EffectClassKind != EnbEffectClassKind.SplineRandomSeekEffect) return null;
        var p = definition.Properties;
        float seconds = 0, distance = 0;
        if (p.ContainsKey("ChangeTime") && !EnbRetailIniDocument.TryReadFloat(p, "ChangeTime", out seconds) ||
            p.ContainsKey("Distance") && !EnbRetailIniDocument.TryReadFloat(p, "Distance", out distance) ||
            !float.IsFinite(seconds))
            throw new InvalidDataException("Invalid seek parameters");
        //006F0FD0 builds before006EB330 writes OneChange;006F1590 consumes BE6CC8.
        //006EB405..431 clamps seconds to[0,4000000] before the x87 conversion.
        return new(duration, (uint)(Math.Clamp(seconds, 0, 4000000) * 1000d), distance, false, EnbRetailSeekRandomStreams.Process.Spline);
    }

    public static EnbRetailLinearSeekEffectPath? Linear(EnbEffectDefinition definition, uint duration)
    {
        if (definition.EffectClassKind != EnbEffectClassKind.LinearRandomSeekEffect) return null;
        var p = definition.Properties;
        float change = 0, transition = 0, distance = 0;
        if (p.ContainsKey("ChangeTime") && !EnbRetailIniDocument.TryReadFloat(p, "ChangeTime", out change) ||
            p.ContainsKey("TimeToChange") && !EnbRetailIniDocument.TryReadFloat(p, "TimeToChange", out transition) ||
            p.ContainsKey("Distance") && !EnbRetailIniDocument.TryReadFloat(p, "Distance", out distance) ||
            !float.IsFinite(change) || !float.IsFinite(transition))
            throw new InvalidDataException("Invalid linear seek parameters");
        return new(duration, (uint)(Math.Clamp(change, 0, 4000000) * 1000d),
            (uint)(Math.Clamp(transition, 0, 4000000) * 1000d), distance, EnbRetailSeekRandomStreams.Process.Linear);
    }
}
