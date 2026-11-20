using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR9AtCurrentRetailRadarRenderRangeTests
{
    [Fact]
    public void DirectOwnerRadiiMatchPublishedDictionaryIncludingInvalidValuesAndBoundary()
    {
        var observer = Object(1, 0, Vector3.Zero, 1, 10, false, 0, null, true);
        float?[] values = [null, 0, -1, 5, 10, float.NaN, float.PositiveInfinity, float.NegativeInfinity];
        foreach (float? observerRadius in values)
        foreach (float? objectRadius in values)
        foreach (float distance in new[] { 0f, 119.999f, 120f, 124.999f, 125f, 129.999f, 130f, 134.999f, 135f, 1000f })
        {
            var target = Object(2, 0x0C, new Vector3(distance, 0, 0), 1, 5, true, 0,
                new EnbSceneNavigationSnapshot(100, false, 0, false));
            var published = new Dictionary<ulong, float>();
            if (observerRadius is {} a) published.Add(observer.RenderIdentity, a);
            if (objectRadius is {} b) published.Add(target.RenderIdentity, b);
            bool expected = EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, target, 20, published);
            bool actual = EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, target, 20,
                observerRadius: observerRadius, objectRadius: objectRadius);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void MarsCaptureDetachesRemusButKeepsRomulusAttached()
    {
        EnbSceneObjectSnapshot observer = Object(
            gameId: 1,
            objectType: 0x00,
            position: new Vector3(-7_228f, 81_627f, 41f),
            scale: 1f,
            assetRadius: 650f,
            appearsInRadar: false,
            radarRange: 0f,
            navigation: null,
            isLocalShip: true);
        EnbSceneObjectSnapshot remus = Object(
            gameId: 0x0001_86B7,
            objectType: 0x25,
            position: new Vector3(-129_000f, 1_000f, 0f),
            scale: 5f,
            assetRadius: 6_450.082f,
            appearsInRadar: true,
            radarRange: 100_000f,
            navigation: new EnbSceneNavigationSnapshot(100_000f, false, 0, false));
        EnbSceneObjectSnapshot romulus = Object(
            gameId: 0x0001_86BC,
            objectType: 0x25,
            position: new Vector3(130_000f, 1_000f, 0f),
            scale: 5f,
            assetRadius: 6_450.082f,
            appearsInRadar: true,
            radarRange: 200_000f,
            navigation: new EnbSceneNavigationSnapshot(200_000f, false, 0, false));

        Assert.False(RetailTestShims.ShouldAttachDecorationRenderObject(observer, remus));
        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, romulus));
    }

    [Fact]
    public void TargetableRangeUsesStrictCombinedRadiusBoundary()
    {
        Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.IsWithinTargetableRange(
            Vector3.Zero, 10f, new Vector3(34.999f, 0f, 0f), 5f, 20f));
        Assert.False(EnbCurrentRetailRadarRenderRangeRuntime.IsWithinTargetableRange(
            Vector3.Zero, 10f, new Vector3(35f, 0f, 0f), 5f, 20f));
    }

    [Fact]
    public void FixedNavigationRenderObjectsUsePlayerScanRangeGate()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, 1f, 10f, false, 0f, null, isLocalShip: true);
        EnbSceneNavigationSnapshot navigation = new(100f, false, 0, false);
        EnbSceneObjectSnapshot inside = Object(
            2, 0x0C, new Vector3(134.999f, 0f, 0f), 1f, 5f, true, 0f, navigation);
        EnbSceneObjectSnapshot boundary = Object(
            3, 0x0C, new Vector3(135f, 0f, 0f), 1f, 5f, true, 0f, navigation);
        EnbSceneObjectSnapshot unknownScanRange = Object(
            4, 0x0C, new Vector3(135f, 0f, 0f), 1f, 5f, true, 0f, navigation);
        EnbSceneObjectSnapshot huge = Object(
            5, 0x0C, new Vector3(1_000_000f, 0f, 0f), 1f, 5f, true, 0f, navigation with { IsHuge = true });

        Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, inside, 20));
        Assert.False(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, boundary, 20));
        Assert.False(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, unknownScanRange, null));
        Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, huge, 20));
    }

    [Fact]
    public void NavigationGateDoesNotDependOnSpatialProviderAndPreservesMissingMetadata()
    {
        var observer = Object(1, 0, Vector3.Zero, 1, 10, false, 0, null, true);
        var target = Object(2, 0, new Vector3(1000, 0, 0), 1, 5, true, 0,
            new EnbSceneNavigationSnapshot(100, false, 0, false));
        foreach (var provider in Enum.GetValues<EnbSceneSpatialProviderKind>())
        {
            var obj = target with { Spatial = target.Spatial! with { ProviderKind = provider } };
            Assert.False(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, obj, 20));
            Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer,
                obj with { Radar = obj.Radar with { Navigation = null } }, 20));
            Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer,
                obj with { ObjectType = 8 }, 20));
        }
    }

    [Fact]
    public void MaxRangeSentinelAndNonParticipatingObjectsRemainAttached()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, 1f, 1f, false, 0f, null, isLocalShip: true);
        EnbSceneNavigationSnapshot navigation = new(10f, false, 0, false);
        EnbSceneObjectSnapshot sentinel = Object(
            2, 0x25, new Vector3(1_000_000f, 0f, 0f), 1f, 1f, true, float.MaxValue, navigation);
        EnbSceneObjectSnapshot noNavigationButRadarEligible = Object(
            3, 0x25, new Vector3(1_000_000f, 0f, 0f), 1f, 1f, true, 10f, null);
        EnbSceneObjectSnapshot notRadarEligible = Object(
            4, 0x25, new Vector3(1_000_000f, 0f, 0f), 1f, 1f, false, 10f, navigation);
        EnbSceneObjectSnapshot otherType = Object(
            5, 0x26, new Vector3(1_000_000f, 0f, 0f), 1f, 1f, true, 10f, navigation);

        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, sentinel));
        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, noNavigationButRadarEligible));
        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, notRadarEligible));
        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, otherType));
    }

    [Fact]
    public void DecorationRenderObjectGateRequiresNavigationMetadata()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, 1f, 10f, false, 0f, null, isLocalShip: true);
        EnbSceneObjectSnapshot noNavigationInside = Object(
            2, 0x25, new Vector3(34.999f, 0f, 0f), 1f, 5f, true, 20f, null);
        EnbSceneObjectSnapshot navigationBoundary = Object(
            3, 0x25, new Vector3(35f, 0f, 0f), 1f, 5f, true, 20f, new EnbSceneNavigationSnapshot(20f, false, 0, false));

        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, noNavigationInside));
        Assert.False(RetailTestShims.ShouldAttachDecorationRenderObject(observer, navigationBoundary));
    }

    [Fact]
    public void MarsReceiptKeepsNoNavigationHuntingGroundsButDetachesRemus()
    {
        EnbSceneObjectSnapshot observer = Object(
            gameId: 1,
            objectType: 0x00,
            position: new Vector3(-7_253f, 81_645f, 40f),
            scale: 1f,
            assetRadius: 650f,
            appearsInRadar: false,
            radarRange: 0f,
            navigation: null,
            isLocalShip: true);
        EnbSceneObjectSnapshot kraken = Object(
            gameId: 0x0001_86C1,
            objectType: 0x25,
            position: new Vector3(-25_000f, 35_000f, 0f),
            scale: 1f,
            assetRadius: 2_887.541f,
            appearsInRadar: true,
            radarRange: 30_000f,
            navigation: null);
        EnbSceneObjectSnapshot dzurai = Object(
            gameId: 0x0001_86C0,
            objectType: 0x25,
            position: new Vector3(1_000f, 20_000f, 0f),
            scale: 1f,
            assetRadius: 2_887.541f,
            appearsInRadar: true,
            radarRange: 30_000f,
            navigation: null);
        EnbSceneObjectSnapshot remus = Object(
            gameId: 0x0001_86B7,
            objectType: 0x25,
            position: new Vector3(-129_000f, 1_000f, 0f),
            scale: 5f,
            assetRadius: 6_450.082f,
            appearsInRadar: true,
            radarRange: 100_000f,
            navigation: new EnbSceneNavigationSnapshot(100_000f, false, 0, false));

        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, kraken));
        Assert.True(RetailTestShims.ShouldAttachDecorationRenderObject(observer, dzurai));
        Assert.False(RetailTestShims.ShouldAttachDecorationRenderObject(observer, remus));
    }

    private static EnbSceneObjectSnapshot Object(
        uint gameId,
        byte objectType,
        Vector3 position,
        float scale,
        float assetRadius,
        bool appearsInRadar,
        float radarRange,
        EnbSceneNavigationSnapshot? navigation,
        bool isLocalShip = false)
        => new(
            RenderIdentity: gameId,
            VisualGeneration: 1,
            GameId: gameId,
            IsLocalShip: isLocalShip,
            IsCurrentTarget: false,
            ObjectType: objectType,
            BaseAsset: 1549,
            Scale: scale,
            HsvH: 0f,
            HsvS: 0f,
            HsvV: 0f,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: null,
            Radar: new EnbSceneRadarSnapshot(appearsInRadar, radarRange, navigation),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 0,
                    Position: position,
                    Orientation: Quaternion.Identity,
                    Velocity: Vector3.Zero,
                    Speed: 0f,
                    Rotation: Vector3.Zero,
                    OrientationColumn0: Vector3.UnitX),
                Planet: null),
            BaseAssetReference: null,
            ClientBaseAssetReference: new EnbPresentationClientBaseAssetReference(
                Selector: 1549,
                Radius: assetRadius,
                RadiusAdjust: 0f,
                Provenance: new EnbPresentationProvenance("fixture")),
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
            SpecialTargetTransition: 0f);
}
