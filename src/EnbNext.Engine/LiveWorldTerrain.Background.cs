using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbResolvedRenderCamera? AppendPlanetBackground(LiveFrame frame, uint rgb, List<EnbW3dDrawTask> packets)
    {
        if (!_useMainViewEnvironment || frame.Snapshot.Environment is not { } environment ||
            !EnbCurrentRetailMainViewPassRuntime.RendersPlanetFogBackground(frame.Snapshot.IsTransitioning, environment.PresentationMode,
                environment.SwapBackdrop, _terrain is { HasSea: true } terrain && frame.Camera.Position.Z < terrain.SeaLevel)) return null;
        if (!EnbResolvedRenderCamera.TryCreateOrthographic(Vector3.Zero, Quaternion.Identity,
                frame.Camera.Viewport, 1, 1, new(.1f,2), out var camera)) return null;
        var white = new EnbRenderPackedRgb8(255,255,255,0);
        var material = new EnbW3dRenderVertexMaterial(0,"planet fog background",0,white,white,white,new(0,0,0,0),1,1,0,null,null);
        var lighting = new EnbW3dVertexLightingSemantics(true,EnbW3dMaterialColorSource.Material,
            EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material,EnbW3dMaterialColorSource.Material);
        // 00510D40 uses 00C01134 (0093B300 initializes 84433), with depth compare Always.
        if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(0x84437,material,0,lighting,out var semantics,out var reason))
            throw new InvalidDataException(reason);
        semantics=semantics with {Uniforms=semantics.Uniforms with {Ambient=Vector3.Zero,Diffuse=Vector3.Zero,
            Specular=Vector3.Zero,Emissive=new((rgb>>16&255)/255f,(rgb>>8&255)/255f,(rgb&255)/255f)}};
        Vector3[] positions=[new(1,-1,1),new(1,-1,-1),new(1,1,-1),new(1,1,1)];
        Vector3[] normals=Enumerable.Repeat(-Vector3.UnitX,4).ToArray();
        uint[] indices=[0,2,1,0,3,2];
        var bindings=Enumerable.Range(0,2).Select(i=>new EnbW3dRenderTriangleBinding(i,(int)indices[i*3],null,null,[],semantics)
            {UsesImplicitVertexMaterial=true,UsesImplicitShader=true}).ToArray();
        var pass=new EnbW3dRenderMaterialPass(0,[],EnbW3dRenderBindingCardinality.None,[],EnbW3dRenderBindingCardinality.None,[],[],[],[],bindings,semantics);
        var mesh=new EnbW3dRenderMesh(0,"planet fog background","planet fog background",2,positions,normals,[],indices,
            new EnbW3dRenderTriangleMetadata[2],[material],[pass]);
        var order=new EnbW3dDrawOrderKey(EnbRenderPassKind.OrdinaryWorld,EnbW3dDrawBlendClass.Blended,
            EnbW3dStaticSortSemantics.FromAuthoredValue(0),new("planet fog background","",0,0),0,0,PlanetBackgroundIdentity,0);
        packets.Add(new(order,PlanetBackgroundIdentity,(ulong)_assetGeneration,0,Matrix4x4.Identity,new(new(1,0,0),2),mesh,
            new(positions.ToImmutableArray(),normals.ToImmutableArray()),new(pass,semantics),EnbW3dLightEnvironment.BlackNoLights,
            frame.CurrentClientTime,null,0,"planet fog background"));
        return camera;
    }
}
