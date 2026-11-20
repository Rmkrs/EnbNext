using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Synchronous render/bounds access to a particle object's owned state.
/// Consumers copy invocation values into their tasks; they do not retain this input.</summary>
public interface IEnbParticleRenderOwner
{
    string RenderObjectName { get; }
    string EmitterResourceName { get; }
    uint GameId { get; }
    EnbRenderPassKind RenderPass { get; }
    EnbCurrentRetailParticleEmitterDefinition Definition { get; }
    EnbW3dRenderTextureLocation? TextureLocation { get; }
    Matrix4x4 WorldTransform { get; }
    string HierarchyName { get; }
    int PivotIndex { get; }
    int BindingOrdinal { get; }
    string? RuntimeTextureName { get; }
    EnbResolvedTextureResource? RetainedTexture { get; }
}
