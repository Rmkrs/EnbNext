using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public readonly record struct EnbRetailSpecialMaterialTransform(Matrix4x4 RigidWorld,float Scale);

public sealed record EnbRetailSpecialMaterialDraw(EnbRetailSpecialMaterialInstance Instance,
    int MemberOrdinal, EnbRetailSpecialMaterialGeometry Geometry, EnbCurrentRetailSpecialMaterialResource Resource)
{
    public bool BindRetainedInstance { get; init; }
    public string? RetainedOwnerPath { get; init; }
    public ulong? RetainedRslGeneration { get; init; }
    public object? RetainedPhysicalFactory { get; init; }
}

public static class EnbRetailSpecialMaterialDrawPacket
{
    public static ImmutableArray<EnbW3dDrawTask> Build(EnbRetailSpecialMaterialInstance instance,
        EnbW3dRenderResourceKey key, Matrix4x4 world, EnbW3dLightEnvironment lights, long clock,
        ulong identity, ulong generation, uint gameId, out IReadOnlyList<string> diagnostics)
    {
        var errors = new List<string>(); diagnostics = errors;
        var result = ImmutableArray.CreateBuilder<EnbW3dDrawTask>();
        var root = instance.Definition;
        var white = new EnbRenderPackedRgb8(255,255,255,0);
        var material = new EnbW3dRenderVertexMaterial(0,"SpecialMaterial",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var lighting = new EnbW3dVertexLightingSemantics(false,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(EnbW3dMaterialCompiler.RetailW3dShaderBaseline,
            material,0,lighting,out var semantics,out var error)) throw new InvalidOperationException(error);
        for (int ordinal = 0; ordinal < root.Members.Length; ordinal++)
        {
            if (!instance.TryGetGeometry(ordinal,out var geometry,out error)) { errors.Add($"{root.Name}/{ordinal}: {error}"); continue; }
            if (geometry.Indices.IsEmpty) continue;
            var triangleStates = new EnbW3dTriangleBindingPool();
            var bindings = Enumerable.Range(0,geometry.Indices.Length/3).Select(i=>triangleStates.Create(
                i,geometry.Indices[i*3],null,null,[],semantics) with{UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
            var pass = new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,
                [],[],[],[],bindings,semantics);
            var mesh = new EnbW3dRenderMesh(int.MinValue+ordinal,root.Name,root.Name,root.SphereRadius,
                geometry.Positions,geometry.Normals,[],geometry.Indices.Select(i=>(uint)i).ToArray(),
                Enumerable.Range(0,geometry.Indices.Length/3).Select(_=>new EnbW3dRenderTriangleMetadata(0,Vector3.Zero,0)).ToArray(),[],[pass])
            {BoundingBoxMin=root.Minimum,BoundingBoxMax=root.Maximum,BoundingSphereCenter=root.SphereCenter};
            var order = new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,EnbW3dDrawBlendClass.Opaque,
                EnbW3dStaticSortSemantics.ResolveCurrentRetail(0,new(false,0,0)),key,mesh.Ordinal,0,identity,ordinal);
            result.Add(new(order,identity,generation,gameId,world,new(Vector3.Transform(root.SphereCenter,world),root.SphereRadius),
                mesh,null,new(pass,semantics),lights,clock,null,0,root.Name)
            {SpecialMaterial=new(instance,ordinal,geometry,root.Members[ordinal].Resource!)});
        }
        return result.ToImmutable();
    }
}
