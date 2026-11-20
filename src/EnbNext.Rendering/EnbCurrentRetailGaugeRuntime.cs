namespace EnbNext.Rendering;

/// <summary>Current client 00669C70, constant 00AFA27C; UVController 004A13A0.
/// Gauges translate a texture mask, not geometry or screen-space clipping.</summary>
public static class EnbCurrentRetailGaugeRuntime
{
    public static float TextureOffset(float ratio)
        => (float.IsFinite(ratio) ? Math.Clamp(ratio,0f,1f) : 0f) * 0.46f;

}
