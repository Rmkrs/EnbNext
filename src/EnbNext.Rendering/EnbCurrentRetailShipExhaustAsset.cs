using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbCurrentRetailShipExhaustAsset(EnbW3dRenderResourceKey Key,
    EnbCurrentRetailParticleEmitterDefinition Definition, EnbW3dRenderTextureLocation? Texture,
    string? DeferredReason, string? Error)
{
    public string? RuntimeTextureName { get; init; }
    public EnbResolvedTextureResource? RetainedTexture { get; init; }
    public bool IsReady => DeferredReason is null && Error is null &&
        (Definition.TextureFileName.Length == 0 || Texture is not null || RuntimeTextureName is not null);

    public EnbCurrentRetailShipExhaustAsset PrepareTexture(EnbRetailAssetBundle assets)
    {
        try
        {
            var prepared = new EnbCurrentRetailRObjParticleAsset(Key, null, Definition).PrepareTexture(assets);
            return this with { Texture = prepared.Texture, RuntimeTextureName = prepared.RuntimeTextureName, RetainedTexture=prepared.RetainedTexture, Error = null };
        }
        catch (InvalidDataException ex) { return this with { Texture = null, RuntimeTextureName = null, RetainedTexture=null, Error = ex.Message }; }
    }

    public static EnbCurrentRetailShipExhaustAsset Load(EnbRetailAssetBundle assets, string name, bool allowInheritedMotion = false)
    {
        if (!assets.RetailMixResources!.TryReadResource(name + ".w3d", out var bytes, out var location) ||
            !EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes, out var d, out _, assets.Textures) ||
            !d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Exhaust emitter unavailable: {name}");
        var key = EnbW3dRenderResourceKey.From(location);
        //0097D180 retains finite and unlimited allowances; an exhaust bone does not change admission.
        try { EnbRetailParticleAdmission.Validate(d); }
        catch (InvalidDataException e) { return new(key, d, null, e.Message, null); }
        return new EnbCurrentRetailShipExhaustAsset(key, d, null, null, null).PrepareTexture(assets);
    }
}
