using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>00458C10/00458DD0/00458F10: one state owns its pass and
/// vertex material; copies retain the texture but copy the material/mapper.</summary>
public sealed class EnbCurrentRetailRslEffectPassOwner : IDisposable
{
    private EnbCurrentRetailTextureRegistry.Lease? _texture;
    public EnbCurrentRetailRslEffectPassDefinition Definition { get; }
    public EnbW3dRenderMaterialPass Material { get; }

    private EnbCurrentRetailRslEffectPassOwner(EnbCurrentRetailRslEffectPassDefinition definition,
        EnbCurrentRetailTextureRegistry.Lease texture, EnbW3dRenderMaterialPass material)
    {
        Definition = definition;
        _texture = texture;
        Material = material;
    }

    public static EnbCurrentRetailRslEffectPassOwner Create(EnbW3dRenderResourceCache resources,
        EnbCurrentRetailRslEffectPassDefinition definition)
    {
        if (!definition.ConstructsUsablePass)
            throw new InvalidDataException("Retail does not construct this effect-pass state.");
        var texture = resources.AcquireRslEffectPassTexture(definition.Texture);
        try
        {
            var material = EnbCurrentRetailRslEffectPassMaterial.Create(definition,
                new(0, definition.Texture, null)
                { CurrentRetailTextureState = texture.State, ResolveLogicalNameAtRuntime = true });
            return new(definition, texture, material);
        }
        catch { texture.Dispose(); throw; }
    }

    public EnbCurrentRetailRslEffectPassOwner Copy()
    {
        var source = _texture ?? throw new ObjectDisposedException(nameof(EnbCurrentRetailRslEffectPassOwner));
        var texture = source.Retain();
        try { return new(Definition, texture, EnbCurrentRetailRslEffectPassMaterial.Copy(Material)); }
        catch { texture.Dispose(); throw; }
    }

    public void Dispose()
    {
        _texture?.Dispose();
        _texture = null;
    }
}
