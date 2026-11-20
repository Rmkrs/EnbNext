using EnbNext.Assets;
using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>00458C10/00459120 Environment material and0093F620's ordinary
/// extra-pass draw. The generated coordinates do not replace target streams.</summary>
public static class EnbCurrentRetailRslEffectPassMaterial
{
    /// <summary>00458DD0 creates a new pass/VM/mapper and retains the source
    /// TextureClass. Unlike00458C10, copying does not rewrite its address words.</summary>
    public static EnbW3dRenderMaterialPass Copy(EnbW3dRenderMaterialPass prototype)
    {
        var source = prototype.CurrentRetailUniformMaterial
            ?? throw new InvalidDataException("RSL effect-pass prototype has no retained vertex material.");
        var material = source.Material with
        {
            Mapper0 = source.Material.Mapper0 is {} mapper0 ? mapper0 with { } : null,
            Mapper1 = source.Material.Mapper1 is {} mapper1 ? mapper1 with { } : null,
        };
        var binding = source with { Material = material };
        return (prototype).Copy().SetVertexMaterials([binding]).SetCurrentRetailUniformMaterial(binding).SetTriangleBindings(prototype.TriangleBindings.Select(triangle => triangle with
                { VertexMaterial = binding }).ToArray());
    }

    public static EnbW3dRenderMaterialPass Create(EnbCurrentRetailRslEffectPassDefinition definition,
        EnbW3dRenderTextureBinding texture)
    {
        if (!definition.ConstructsUsablePass)
            throw new InvalidDataException("Retail does not construct a usable pass for this effect-pass type.");
        if (texture.CurrentRetailTextureState is not {} owner)
            throw new InvalidDataException("RSL effect-pass texture requires its shared TextureClass owner.");
        owner.ApplyRslEffectPassAddressing();
        //0092E8D0's D3DMATERIAL8: white diffuse/ambient, black specular/
        //emissive, power zero. +60 remains false and color-source fields zero.
        //00935660's half-bias matrix and00934FB0's reflection TCI match Mode01.
        var mapper = new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode01,
            string.Empty, 1, 1, 0, 0, 0, 0, false);
        var white = new EnbRenderPackedRgb8(255, 255, 255, 0);
        var black = new EnbRenderPackedRgb8(0, 0, 0, 0);
        var material = new EnbW3dRenderVertexMaterial(-1, "RSL Environment", 0,
            white, white, black, black, 0, 1, 0, mapper, null);
        var lighting = new EnbW3dVertexLightingSemantics(false,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material, EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(definition.PackedShader,
            material, 1, lighting, out var semantics, out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var binding = new EnbW3dRenderVertexMaterialBinding(uint.MaxValue, material)
        {
            SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
            CurrentRetailRuntimeCoordinates = new(0, 1),
        };
        var shader = new EnbW3dRenderShaderBinding(definition.PackedShader, default, semantics.FixedFunction)
            { SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState };
        var stage = new EnbW3dRenderTextureStage(0, [texture], [], EnbW3dRenderBindingCardinality.Uniform)
            { CurrentRetailUniformTexture = texture, EffectiveTextureCoordinateSetIndex = 0 };
        return new(0, [binding], EnbW3dRenderBindingCardinality.Uniform,
            [shader], EnbW3dRenderBindingCardinality.Uniform, [], [], [], [stage], [], semantics)
        {
            CurrentRetailUniformMaterial = binding,
            CurrentRetailUniformLighting = lighting,
            CurrentRetailUniformShader = definition.PackedShader,
        };
    }

    public static EnbW3dRenderMaterialPass BindToMesh(EnbW3dRenderMaterialPass pass, EnbW3dRenderMesh mesh)
    {
        //Generated reflection coordinates have no dependency on an authored
        //UV array. Keep the target's actual slot when it exists.
        var uv = mesh.MaterialPasses.SelectMany(pass => pass.TextureStages)
            .FirstOrDefault(stage => stage.EffectiveTextureCoordinateSetIndex == 0)?.EffectiveRuntimeTexCoords
            ?? Array.Empty<Vector2>();
        var stage = (pass.TextureStages[0]).Copy().SetRuntimeTexCoords(uv).SetEffectiveRuntimeTexCoords(uv);
        var texture = stage.Textures[0];
        var triangleStates = new EnbW3dTriangleBindingPool();
        var triangles = Enumerable.Range(0, mesh.Indices.Count / 3).Select(i =>
            triangleStates.Create(i, (int)mesh.Indices[i * 3], pass.CurrentRetailUniformMaterial,
                pass.Shaders[0], [texture], pass.SingleBindingSemantics)).ToArray();
        return (pass).Copy().SetTextureStages([stage]).SetTriangleBindings(triangles);
    }
}
