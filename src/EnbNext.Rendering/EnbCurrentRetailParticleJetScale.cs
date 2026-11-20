using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public static class EnbCurrentRetailParticleJetScale
{
    public static float Read(EnbCurrentRetailEffectOwner node)
        =>Read(node.Definition,node.Root.Scale);

    public static float Read(EnbEffectDefinition definition,float incomingScale)
    {
        float authored=1;
        if(definition.Properties.ContainsKey("RObjScale") &&
            !EnbRetailIniDocument.TryReadFloat(definition.Properties,"RObjScale",out authored))
            throw new InvalidDataException("ParticleJet authored scale unavailable");
        float scale=authored*incomingScale;
        if(!float.IsFinite(scale)||scale<=0)throw new InvalidDataException("ParticleJet requires finite positive factory scale");
        return MathF.Abs(scale-1)<=.01f?1:scale;
    }

    // 0097DE70 scales emitter randomizers, velocity and outward velocity.
    // Buffer0095E8B0 scales size curves/randomness and acceleration;
    // line009C2B50 scales width and subdivision noise. Apply before jet
    // activation chooses its lifetime from the scaled forward velocity.
    public static EnbCurrentRetailParticleEmitterDefinition Apply(EnbCurrentRetailParticleEmitterDefinition d,float scale)
    {
        if(!float.IsFinite(scale)||scale<=0)throw new InvalidDataException("Invalid particle factory scale");
        if(scale==1)return d;
        var words=d.LinePropertyWords;
        if(words.Length>2)
        {
            var copy=words.ToArray();
            copy[2]=BitConverter.SingleToUInt32Bits(BitConverter.UInt32BitsToSingle(copy[2])*scale);
            words=copy.ToImmutableArray();
        }
        return d.Copy().SetCreationRandomizer(d.CreationRandomizer with {Values=d.CreationRandomizer.Values*scale}).SetVelocityRandomizer(d.VelocityRandomizer with {Values=d.VelocityRandomizer.Values*scale}).SetVelocityPerSecond(d.VelocityPerSecond*scale).SetOutwardVelocityPerSecond(d.OutwardVelocityPerSecond*scale).SetAccelerationPerSecondSquared(d.AccelerationPerSecondSquared*scale).SetSize(d.Size with {Random=d.Size.Random*scale,Keys=d.Size.Keys.Select(k=>k with {Value=k.Value*scale}).ToArray()}).SetLinePropertyWords(words);
    }
}
