using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4SFixedFunctionLightingTests
{
    [Fact]
    public void CurrentRetailPipelinePinsColorVertexLocalViewerNoNormalNormalizationAndFourLights()
    {
        EnbW3dFixedFunctionLightingPipeline pipeline = EnbW3dFixedFunctionLightingPipeline.CurrentRetail;

        Assert.True(pipeline.ColorVertexEnabled);
        Assert.True(pipeline.LocalViewerEnabled);
        Assert.False(pipeline.NormalizeNormals);
        Assert.Equal(EnbW3dMaterialColorSource.Material, pipeline.SpecularMaterialSource);
        Assert.Equal(4, pipeline.MaximumSnapshottedLights);
    }

    [Fact]
    public void LightEnvironmentOwnsExactlyFourImmutableRetailSlots()
    {
        var source = new List<EnbW3dLightSlot>
        {
            new(true, DirectionalLight(diffuse: Vector3.One)),
        };

        EnbW3dLightEnvironment environment = EnbW3dLightEnvironment.CreateCurrentRetail(0x12_34_56u, source);
        source[0] = default;

        Assert.Equal(0x12_34_56u, environment.GlobalAmbientRgb24);
        Assert.Equal(4, environment.Lights.Length);
        Assert.True(environment.Lights[0].Enabled);
        Assert.Equal(EnbW3dLightType.Directional, environment.Lights[0].Light.Type);
        Assert.All(environment.Lights.Skip(1), slot => Assert.False(slot.Enabled));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EnbW3dLightEnvironment.CreateCurrentRetail(
                0,
                Enumerable.Repeat(default(EnbW3dLightSlot), 5)));
    }

    [Fact]
    public void DrawPacketCapturesExplicitImmutableLightEnvironment()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dLightEnvironment environment = EnbW3dLightEnvironment.CreateCurrentRetail(
            0x20_40_80u,
            [new EnbW3dLightSlot(true, DirectionalLight(diffuse: new Vector3(0.25f, 0.5f, 1.0f)))]);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 5,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: environment,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Same(environment, packet.LightEnvironment);
        Assert.Equal(0x20_40_80u, packet.LightEnvironment.GlobalAmbientRgb24);
        Assert.Equal(4, packet.LightEnvironment.Lights.Length);
    }








    private static EnbW3dCompiledLight DirectionalLight(
        Vector3? diffuse = null,
        Vector3? specular = null,
        Vector3? ambient = null)
        => new(
            EnbW3dLightType.Directional,
            Diffuse: diffuse ?? Vector3.Zero,
            Specular: specular ?? Vector3.Zero,
            Ambient: ambient ?? Vector3.Zero,
            Position: Vector3.Zero,
            Direction: -Vector3.UnitZ,
            Range: 0f,
            Falloff: 0f,
            Attenuation0: 1f,
            Attenuation1: 0f,
            Attenuation2: 0f,
            Theta: 0f,
            Phi: 0f);

    private static EnbW3dMaterialUniforms Material(
        Vector3 ambient,
        Vector3 diffuse,
        Vector3 specular,
        Vector3 emissive,
        float power,
        float alpha)
        => new(
            ambient,
            diffuse,
            specular,
            emissive,
            power,
            alpha,
            EnbW3dMapperUniforms.Identity,
            EnbW3dMapperUniforms.Identity);

    private static EnbSceneObjectSnapshot SceneObject()
        => new(
            RenderIdentity: 0x7788UL,
            VisualGeneration: 3,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R4S fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 0,
                    Position: Vector3.Zero,
                    Orientation: Quaternion.Identity,
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

    private static Vector3 Clamp01(Vector3 value)
        => new(
            Math.Clamp(value.X, 0f, 1f),
            Math.Clamp(value.Y, 0f, 1f),
            Math.Clamp(value.Z, 0f, 1f));

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    private static void AssertVector4(Vector4 expected, Vector4 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
        Assert.Equal(expected.W, actual.W, 4);
    }
}
