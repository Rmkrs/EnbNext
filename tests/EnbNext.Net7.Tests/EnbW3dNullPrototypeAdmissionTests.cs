using System.Buffers.Binary;
using System.Numerics;
using System.Collections.Immutable;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbW3dNullPrototypeAdmissionTests
{
    private static byte[] Chunk(string name,byte marker)
    {
        byte[] bytes=new byte[56];BinaryPrimitives.WriteUInt32LittleEndian(bytes,0x750);BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),48);
        bytes[8]=marker;Encoding.Latin1.GetBytes(name).CopyTo(bytes,24);return bytes;
    }
    [Fact]
    public void ConfiguredNullPrototypesKeepRawPayloadAndFirstRegisteredIdentityAcrossReload()
    {
        byte[] bytes=[..Chunk("configured",17),..Chunk("CONFIGURED",99),..Chunk("other",31)];
        Assert.True(EnbW3dModel.TryDecode(bytes,out var model));Assert.Equal(3,model.NullObjects.Count);
        Assert.Equal(new[]{0,56,112},model.PrototypeEntries.Select(value=>value.ChunkOffset));
        Assert.All(model.PrototypeEntries,value=>Assert.Equal(EnbW3dPrototypeKind.NullObject,value.Kind));
        Assert.Equal((byte)17,model.NullObjects[0].Payload[0]);Assert.Equal("configured",model.NullObjects[0].Name);
        var source=new EnbResolvedW3dResource("nulls.w3d","configured",new("nulls.w3d","offline.mix",0,bytes.Length),model,[]);
        using var cache=new EnbW3dRenderResourceCache();Assert.True(cache.TryLoadFile(source,out var resource,out var error),error);
        Assert.Equal(3,resource.ExternalRenderObjects.Count);Assert.True(cache.TryFindPrototypeSource("CONFIGURED",out var first));
        Assert.Equal(0,first.RegisteredPrototype!.Value.Ordinal);Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("configured",out var unchanged));Assert.Equal(first.PrototypeRegistration,unchanged.PrototypeRegistration);
        Assert.True(cache.RemovePrototype("configured"));Assert.True(cache.TryLoadFile(source,out _,out error),error);
        Assert.True(cache.TryFindPrototypeSource("CONFIGURED",out var replaced));Assert.NotEqual(first.PrototypeRegistration,replaced.PrototypeRegistration);
        Assert.True(cache.TryGetRegisteredPrototype(replaced,out _));Assert.False(cache.TryGetRegisteredPrototype(first,out _));
    }
    [Fact]
    public void ConfiguredNullHlodChildHasProvenBoundsAndCostAndProducesAnEmptyDrawList()
    {
        byte[] bytes=Chunk("configured",17);Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
        var source=new EnbResolvedW3dResource("nulls.w3d","configured",new("nulls.w3d","offline.mix",0,bytes.Length),model,[]);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var nullResource,out var error),error);
        var basis=EnbR4W3dRenderResourceTests.BuildResolvedResource(0);Assert.True(EnbW3dRenderResourceCompiler.TryCompile(basis,out var resource,out error),error);
        var hlod=Assert.Single(resource.HierarchicalLods) with {Levels=[new(0,1,0,[new(0,"configured")])],Aggregate=null,ProxyArray=null};
        resource=resource.Copy().SetHierarchicalLods([hlod]).SetExternalRenderObjects(nullResource.ExternalRenderObjects);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource,hlod,_=>true,out var costs,out error),error);
        Assert.Equal(0f,Assert.Single(costs));
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,Matrix4x4.Identity,null,0,out var bounds,out error),error);
        Assert.Equal(Vector3.Zero,bounds.LocalBox.Center);Assert.Equal(new Vector3(.1f),bounds.LocalBox.Extents);
        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(SceneObject(1),resource,0,EnbRenderPassKind.OrdinaryWorld,
            new EnbW3dHlodSelection(hlod.Name,0),EnbW3dLightEnvironment.BlackNoLights,out var draw,out error),error);
        Assert.Empty(draw.Packets);Assert.Empty(draw.ParticleEmitters);Assert.Empty(draw.Dazzles);
        resource=resource.Copy().SetExternalRenderObjects([]);Assert.False(EnbW3dDrawPacketCompiler.TryBuild(SceneObject(1),resource,0,
            EnbRenderPassKind.OrdinaryWorld,new EnbW3dHlodSelection(hlod.Name,0),EnbW3dLightEnvironment.BlackNoLights,out _,out _));
    }
    private static EnbSceneObjectSnapshot SceneObject(float scale, Quaternion? orientation = null)
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 7,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: scale,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R4C fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100, 200, 300),
                    Orientation: orientation ?? Quaternion.Identity,
                    Velocity: Vector3.Zero,
                    Speed: 0,
                    Rotation: Vector3.Zero,
                    OrientationColumn0: Vector3.UnitX),
                Planet: null),
            BaseAssetReference: null,
            ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: null,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0);
}
