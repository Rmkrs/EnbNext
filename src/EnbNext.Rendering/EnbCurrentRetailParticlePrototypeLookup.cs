using EnbNext.Assets;

namespace EnbNext.Rendering;

internal static class EnbCurrentRetailParticlePrototypeLookup
{
    //009A3AB0 creates by global name before considering an implicit FILE.
    //A retained name of another class must not fall through to a particle file.
    internal static bool TryResolve(EnbRetailAssetBundle assets,string name,
        IReadOnlyDictionary<string,EnbResolvedW3dResource>? prototypes,
        out EnbCurrentRetailParticleEmitterDefinition definition,out EnbMixResourceLocation location)
    {
        definition=default!;location=default!;
        if(prototypes?.TryGetValue(name,out var source)==true)
        {
            if(source.CurrentRetailAggregate is not null || source.RegisteredPrototype is not
                {Kind:EnbW3dPrototypeKind.ParticleEmitter} entry)return false;
            definition=source.Model.ParticleEmitters[entry.Ordinal];
            location=source.ResourceLocation;
            return true;
        }
        return assets.RetailMixResources!.TryReadResource(name+".w3d",out var bytes,out location) &&
            EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out definition,out _,assets.Textures) &&
            definition.Name.Equals(name,StringComparison.OrdinalIgnoreCase);
    }
}
