using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Render-prepared data owned by one particle emitter definition (
/// option A). Formerly four ConditionalWeakTables keyed by the definition: sprite
/// topology, compiled property curves (native ms times/slopes, compiled once as
/// 0095EF40/0095F550 do at construction), retained material and line bindings.
/// One instance per definition object; a copied definition starts without it.</summary>
internal sealed class EnbPreparedParticleEmitter : EnbRenderPrepared
{
    internal EnbCurrentRetailParticleEmitterRuntime.SpriteTopology? SpriteTopology;
    internal EnbCurrentRetailParticleEmitterRuntime.CompiledProperties? PropertyCurves;
    internal EnbCurrentRetailParticleEmitterRuntime.RetainedMaterial? Material;
    internal EnbCurrentRetailParticleEmitterRuntime.RetainedLineBindings? LineBindings;

    internal static EnbPreparedParticleEmitter Of(EnbCurrentRetailParticleEmitterDefinition definition)
        => (EnbPreparedParticleEmitter)OwnerCache.Get(ref definition.RenderPrepared, definition,
            static _ => (EnbRenderPrepared)new EnbPreparedParticleEmitter());
}
