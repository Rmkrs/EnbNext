using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>006ECC50/006ED130 lifetime selection; does not admit or render a jet.</summary>
public static class EnbCurrentRetailParticleJetLifetime
{
    public static float Select(float originalLifetime, float currentLifetime, float minimumLifetime,
        float capturedForwardVelocity, float distance)
    {
        if (!float.IsFinite(originalLifetime) || originalLifetime <= 0 ||
            !float.IsFinite(currentLifetime) || currentLifetime <= 0 ||
            !float.IsFinite(minimumLifetime) || !float.IsFinite(capturedForwardVelocity) ||
            !float.IsFinite(distance) || distance < 0)
            throw new ArgumentOutOfRangeException(nameof(distance), "Finite positive lifetimes and a nonnegative distance are required.");
        float minimum = minimumLifetime < 0 ? originalLifetime * 0.3333333f : minimumLifetime;
        float flight = distance / Math.Max(capturedForwardVelocity, .01f);
        float desired = minimum;
        // Preserve native branch order, including an explicit minimum above original.
        if (minimum <= flight) desired = Math.Min(flight, originalLifetime);
        return currentLifetime < desired * .8f || currentLifetime > desired * 1.2f ? desired : currentLifetime;
    }

    public static EnbCurrentRetailParticleEmitterDefinition Retime(
        EnbCurrentRetailParticleEmitterDefinition definition, float currentLifetime, float selectedLifetime)
    {
        if (!float.IsFinite(currentLifetime) || currentLifetime <= 0 ||
            !float.IsFinite(selectedLifetime) || selectedLifetime < 0)
            throw new ArgumentOutOfRangeException(nameof(selectedLifetime));
        float ratio = selectedLifetime / currentLifetime;
        EnbCurrentRetailParticleScalarProperty Scale(EnbCurrentRetailParticleScalarProperty property) =>
            property with { Keys = property.Keys.Select(k => k with { TimeSeconds = k.TimeSeconds * ratio }).ToArray() };
        return definition.Copy().SetLifetimeSeconds(selectedLifetime).SetColor(definition.Color with { Keys = definition.Color.Keys.Select(k => k with { TimeSeconds = k.TimeSeconds * ratio }).ToArray() }).SetOpacity(Scale(definition.Opacity)).SetSize(Scale(definition.Size)).SetRotation(Scale(definition.Rotation)).SetFrame(Scale(definition.Frame)).SetBlurTime(definition.BlurTime is {} blur ? Scale(blur) : null);
    }
}
