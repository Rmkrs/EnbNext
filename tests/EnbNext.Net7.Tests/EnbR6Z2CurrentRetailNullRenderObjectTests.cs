using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z2CurrentRetailNullRenderObjectTests
{
    [Fact]
    public void HlodNullMemberUsesProvenRetailAabbWithoutInventingMeshGeometry()
    {
        EnbW3dRenderResource resource = NullResource(includeUnknown: false);
        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);

        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.Identity,
            animation: null,
            animationFrame: 0f,
            out EnbW3dHlodBoundsOwner bounds,
            out string? diagnostic), diagnostic);

        // ReferenceIndex 0 selects hierarchy state record 0. Current retail
        // overwrites that root record with the owning HLOD transform, so the
        // authored ROOT translation is deliberately absent from local bounds.
        Assert.Equal(Vector3.Zero, bounds.LocalBox.Center);
        Assert.Equal(new Vector3(0.1f, 0.1f, 0.1f), bounds.LocalBox.Extents);
        Assert.Equal(Vector3.Zero, bounds.LocalSphere.Center);
        Assert.Equal(MathF.Sqrt(0.03f), bounds.LocalSphere.Radius, 5);
        Assert.Equal(bounds.LocalSphere, bounds.WorldSphere);
        Assert.Empty(resource.Meshes);
    }

    [Fact]
    public void HlodNullMemberUsesZeroIntegerCostAndRetailFloatFallbackForAggregate()
    {
        EnbW3dRenderResource resource = NullResource(includeUnknown: false);
        EnbW3dRenderHlod source = Assert.Single(resource.HierarchicalLods);
        EnbW3dRenderHlod hlod = source with
        {
            Aggregate = new EnbW3dRenderHlodAggregate(
                0u,
                [new EnbW3dRenderHlodMember(0u, "NULL")]),
        };

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            hlod,
            static _ => true,
            out ImmutableArray<int> levelCosts,
            out string? diagnostic), diagnostic);
        Assert.Equal(new[] { 0 }, levelCosts);

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(
            resource,
            hlod,
            static _ => true,
            out float aggregateCost,
            out diagnostic), diagnostic);
        Assert.Equal(EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback, aggregateCost);
    }


    [Fact]
    public void NullOnlySelectedHlodLevelIsAValidEmptyDrawResult()
    {
        EnbW3dRenderResource resource = NullResource(includeUnknown: false);
        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection(hlod.Name, 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        Assert.Empty(drawList.Packets);
    }

    [Fact]
    public void UnknownNonMeshHlodMemberStillFailsClosed()
    {
        EnbW3dRenderResource resource = NullResource(includeUnknown: true);
        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);

        Assert.False(EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.Identity,
            animation: null,
            animationFrame: 0f,
            out _,
            out string? boundsDiagnostic));
        Assert.Contains("other retail render-object classes remain fail-closed", boundsDiagnostic, StringComparison.Ordinal);

        Assert.False(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            hlod,
            static _ => true,
            out _,
            out string? costDiagnostic));
        Assert.Contains("other render-object classes remains fail-closed", costDiagnostic, StringComparison.Ordinal);
    }


    private static EnbSceneObjectSnapshot SceneObject()
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 1,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 0x25,
            BaseAsset: 245,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R6Z2 NULL fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
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

    private static EnbW3dRenderResource NullResource(bool includeUnknown)
    {
        Matrix4x4 pivotTransform = Matrix4x4.CreateTranslation(2f, 3f, 4f);
        var hierarchy = new EnbW3dRenderHierarchy(
            0x30000u,
            "ROOT",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            [
                new EnbW3dRenderPivot(
                    0,
                    "ROOT",
                    uint.MaxValue,
                    new Vector3(2f, 3f, 4f),
                    Vector3.Zero,
                    Quaternion.Identity,
                    pivotTransform),
            ],
            Array.Empty<EnbW3dRenderPivotFixup>());

        var members = new List<EnbW3dRenderHlodMember>
        {
            new(0u, "NULL"),
        };
        if (includeUnknown)
            members.Add(new EnbW3dRenderHlodMember(0u, "NOT_A_PROVEN_RENDER_OBJECT"));

        var hlod = new EnbW3dRenderHlod(
            "ROOT",
            "ROOT",
            [new EnbW3dRenderHlodLevel(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), members)]);

        return new EnbW3dRenderResource(
            new EnbW3dRenderResourceKey("NULLTEST.W3D", "test.mix", 0, 0),
            "NULLTEST.W3D",
            Array.Empty<EnbW3dRenderMesh>(),
            [hierarchy],
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            [hlod]);
    }
}
