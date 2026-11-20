using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public sealed record EnbMapMarker(EnbMapGlyph Glyph, Vector2 Position, float Heading, EnbRgb Color)
{
    public Vector2 Scale { get; init; } = Vector2.One;
    public bool PreserveMaterial { get; init; }
    public Matrix4x4? WorldTransform { get; init; }
}

public sealed record EnbMapGlyph(EnbW3dRenderResource Resource, ImmutableArray<EnbW3dDrawTask> Packets)
{
    public static EnbMapGlyph Load(EnbRetailAssetBundle assets, string name, float uvScale=1)
    {
        var resources = assets.RetailMixResources!;
        if (!resources.TryReadResource(name,out var bytes,out var location) || !EnbW3dModel.TryDecode(bytes,out var model) ||
            !EnbAuthoredUiAsset.TryCompile(resources,location,model,model.HierarchicalLods.FirstOrDefault()?.Name ?? model.Meshes[0].Name,out var resource))
            throw new InvalidDataException("Map marker unavailable: " + name);
        var packets = ImmutableArray.CreateBuilder<EnbW3dDrawTask>();
        EnbW3dHierarchyPoseOwner? pose=null;
        if (resource.Hierarchies.FirstOrDefault() is { } hierarchy &&
            !EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,null,0,out pose,out var error))
            throw new InvalidDataException(error);
        foreach (var mesh in resource.Meshes.OrderByDescending(m=>m.StaticSort.RuntimeLevel))
        {
            int pivot=resource.Hierarchies.FirstOrDefault()?.Pivots.FirstOrDefault(p=>p.Name.Equals(mesh.Name,StringComparison.OrdinalIgnoreCase))?.Ordinal ?? 0;
            var transform=pose?.WorldTransforms[pivot] ?? Matrix4x4.Identity;
            foreach (var original in mesh.MaterialPasses)
            {
                var pass=uvScale==1 ? original : (original).Copy().SetTextureStages(original.TextureStages.Select(s=>(s).Copy().SetRuntimeTexCoords(s.RuntimeTexCoords.Select(v=>v*uvScale).ToArray()).SetEffectiveRuntimeTexCoords(s.EffectiveRuntimeTexCoords.Select(v=>v*uvScale).ToArray())).ToArray());
                packets.Add(new(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,
                    mesh.StaticSort,resource.Key,mesh.Ordinal,pass.Ordinal,1,0),1,1,0,transform,
                    new(Vector3.Zero,mesh.BoundingRadius),mesh,null,new(pass,pass.SingleBindingSemantics),
                    EnbW3dLightEnvironment.BlackNoLights,0,null,0,mesh.Name));
            }
        }
        return new(resource,packets.ToImmutable());
    }

    public static EnbW3dDrawTask Tint(EnbW3dDrawTask packet, EnbRgb color)
    {
        var rgb = new Vector3(color.R,color.G,color.B);
        EnbW3dCompiledMaterialSemantics Change(EnbW3dCompiledMaterialSemantics s)
            => s with { Uniforms = s.Uniforms with { Emissive = rgb } };
        var source = packet.Material.Source;
        var pass = (source).Copy().SetSingleBindingSemantics(source.SingleBindingSemantics is { } single ? Change(single) : null).SetTriangleBindings(source.TriangleBindings.Select(t=>t with { Semantics=t.Semantics is { } s ? Change(s) : null }).ToArray());
        return (packet).ForkTask().SetMaterial(new(pass,pass.SingleBindingSemantics));
    }
}
