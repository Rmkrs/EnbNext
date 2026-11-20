using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR10RadarCompatibilityAdapterTests
{
    [Fact]
    public void SelectedGutterContactBypassesRangeButNotEligibilityOrFinitePosition()
    {
        var self=Object(1,1,Vector3.Zero,isLocalShip:true);
        var selected=Object(2,1,new(10000,0,0),navigation:new(10,false,0,false));
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[self,selected],100).Contacts);
        var retained=EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[self,selected],100,retainedTargetId:2);
        Assert.Equal(2u,Assert.Single(retained.Contacts).GameId);
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[selected with{ObjectType=5}],100,retainedTargetId:2).Contacts);
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[selected with{Spatial=Spatial(new(float.NaN,0,0))}],100,retainedTargetId:2).Contacts);
        Assert.True(Assert.Single(EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[self,selected],100,retainedTargetId:1).Contacts).IsLocalShip);
    }
    [Fact]
    public void GutterCanAdmitSelfWithoutChangingOrdinaryRadarPopulation()
    {
        var self=Object(1,1,Vector3.Zero,isLocalShip:true);
        var other=Object(2,1,new(100,0,0));
        var ordinary=EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[self,other],100);
        Assert.Equal(2u,Assert.Single(ordinary.Contacts).GameId);
        var gutter=EnbCurrentRetailRadarCompatibilityAdapter.Build(self,[self,other],100,includeLocal:true);
        Assert.True(gutter.Contacts.Single(c=>c.GameId==1).IsLocalShip);
        Assert.False(gutter.Contacts.Single(c=>c.GameId==2).IsLocalShip);
        Assert.Equal(0,gutter.Contacts.Single(c=>c.GameId==1).SurfaceDistance);
    }
    [Fact]
    public void ComposedRadiiParticipateBeforeContactAdmissionAndKeepStrictBoundary()
    {
        var observer=Object(1,1,Vector3.Zero,assetRadius:1,isLocalShip:true);
        var contact=Object(2,1,new(39,0,0),assetRadius:1,navigation:new(10,false,0,false));
        var radii=new Dictionary<ulong,float>{{observer.RenderIdentity,20},{contact.RenderIdentity,10}};
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,[contact],0).Contacts);
        var frame=EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,[contact],0,distanceRadii:radii);
        Assert.Equal(20,frame.ObserverWorldRadius);
        var admitted=Assert.Single(frame.Contacts);
        Assert.Equal(10,admitted.WorldRadius);
        Assert.Equal(9,admitted.SurfaceDistance);
        Assert.True(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer, contact, 0, radii));
        Assert.False(EnbCurrentRetailRadarRenderRangeRuntime.ShouldAttachRadarGatedRenderObject(observer,
            contact with {Spatial=Spatial(new(40,0,0))}, 0, radii));
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,
            [contact with {Spatial=Spatial(new(40,0,0))}],0,distanceRadii:radii).Contacts);
        // A smaller current radius must remove a previously admissible contact.
        radii[observer.RenderIdentity]=0;radii[contact.RenderIdentity]=0;
        Assert.Empty(EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,[contact],0,distanceRadii:radii).Contacts);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x18)]
    [InlineData(0x19)]
    [InlineData(0x22)]
    [InlineData(0x23)]
    [InlineData(0x26)]
    public void CurrentRetailAlwaysEligibleObjectTypesAreAdmitted(byte objectType)
    {
        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.CanClientObjectBeTargeted(
            Object(2, objectType, Vector3.Zero)));
    }

    [Fact]
    public void DecorationEligibilityUsesAppearsInRadarAndUnknownTypeIsRejected()
    {
        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.CanClientObjectBeTargeted(
            Object(2, 0x25, Vector3.Zero, appearsInRadar: true)));
        Assert.False(EnbCurrentRetailRadarCompatibilityAdapter.CanClientObjectBeTargeted(
            Object(2, 0x25, Vector3.Zero, appearsInRadar: false)));
        Assert.False(EnbCurrentRetailRadarCompatibilityAdapter.CanClientObjectBeTargeted(
            Object(2, 0x05, Vector3.Zero)));
    }

    [Fact]
    public void NavigationTargetabilityUsesSignatureScanRangeAndStrictCombinedRadii()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, assetRadius: 10f, isLocalShip: true);
        EnbSceneNavigationSnapshot navigation = new(100f, false, 0, false);
        EnbSceneObjectSnapshot inside = Object(
            2, 0x26, new Vector3(134.999f, 0f, 0f), assetRadius: 5f, navigation: navigation);
        EnbSceneObjectSnapshot boundary = Object(
            3, 0x26, new Vector3(135f, 0f, 0f), assetRadius: 5f, navigation: navigation);
        EnbSceneObjectSnapshot huge = Object(
            4, 0x26, new Vector3(1_000_000f, 0f, 0f), navigation: navigation with { IsHuge = true });

        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, inside, 20));
        Assert.False(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, boundary, 20));
        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, huge, 20));
    }

    [Fact]
    public void DecorationUsesItsOwnRadarRangeInsteadOfPlayerScanRange()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, assetRadius: 10f, isLocalShip: true);
        EnbSceneNavigationSnapshot navigation = new(9_000f, false, 0, false);
        EnbSceneObjectSnapshot decoration = Object(
            2,
            0x25,
            new Vector3(34.999f, 0f, 0f),
            assetRadius: 5f,
            appearsInRadar: true,
            radarRange: 20f,
            navigation: navigation);

        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, decoration, 0));
        Assert.True(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(observer, decoration, 100_000));
        Assert.False(EnbCurrentRetailRadarCompatibilityAdapter.IsTargetable(
            observer,
            decoration with
            {
                Spatial = Spatial(new Vector3(35f, 0f, 0f)),
            },
            100_000));
    }

    [Fact]
    public void FrameUsesShipLocalAxesAndContainsOnlyTargetableContacts()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, isLocalShip: true);
        EnbSceneObjectSnapshot contact = Object(
            2,
            0x26,
            new Vector3(30f, -40f, 5f),
            isCurrentTarget: true,
            name: "Target");
        EnbSceneObjectSnapshot outOfRange = Object(
            3,
            0x26,
            new Vector3(500f, 0f, 0f),
            navigation: new EnbSceneNavigationSnapshot(0f, false, 0, false));

        EnbRadarFrame frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(
            observer,
            new[] { observer, contact, outOfRange },
            scanRange: 100);

        Assert.True(frame.IsAvailable);
        Assert.Equal(100, frame.ScanRange);
        EnbRadarContact captured = Assert.Single(frame.Contacts);
        Assert.Equal("Target", captured.Name);
        Assert.Equal("g_resrce.tga", captured.IconAssetName);
        Assert.Equal(new Vector3(30f, -40f, 5f), captured.WorldPosition);
        Assert.Equal(0f, captured.WorldRadius);
        Assert.True(captured.IsCurrentTarget);
        Assert.Equal(30f, captured.RelativePosition.Forward, 3);
        Assert.Equal(40f, captured.RelativePosition.Right, 3);
        Assert.Equal(5f, captured.RelativePosition.Up, 3);
        Assert.Equal(MathF.Sqrt(2525f), captured.CenterDistance, 3);
    }

    [Fact]
    public void RenderAttachmentRadiusOverrideReproducesObservedMarsSurfaceDistance()
    {
        EnbSceneObjectSnapshot observer = Object(
            1,
            0x01,
            Vector3.Zero,
            assetRadius: 49.1692f,
            isLocalShip: true);
        EnbSceneObjectSnapshot mars = Object(
            2,
            0x03,
            new Vector3(139_391.709f, 0f, 0f),
            assetRadius: 70_519.125f,
            name: "Mars");

        EnbRadarFrame original = EnbCurrentRetailRadarCompatibilityAdapter.Build(
            observer,
            new[] { observer, mars },
            scanRange: 0);
        EnbRadarContact originalMars = Assert.Single(original.Contacts);
        Assert.Equal(68_823.414f, originalMars.SurfaceDistance, 2);

        EnbRadarFrame adjusted = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(
            original,
            new Dictionary<ulong, float>
            {
                [observer.RenderIdentity] = 92.584f,
            });

        Assert.Equal(92.584f, adjusted.ObserverWorldRadius, 3);
        Assert.Equal(68_780f, Assert.Single(adjusted.Contacts).SurfaceDistance, 2);
    }

    [Fact]
    public void AttachedRadiusUsesRetailRecursiveParentChildExpansion()
    {
        EnbCurrentRetailAttachedRadiusNode[] nodes =
        [
            new("root", Vector3.Zero, 3f),
            new("lego/~02", new Vector3(5f, 0f, 0f), 10f),
            new("lego/~02/~03_01", new Vector3(5f, 12f, 0f), 7f),
        ];

        Assert.True(EnbCurrentRetailAttachedRadiusRuntime.TryResolve(nodes, out float radius));
        Assert.Equal(24f, radius);
    }

    [Fact]
    public void AttachedRadiusRejectsAnOrphanedControllerPath()
    {
        EnbCurrentRetailAttachedRadiusNode[] nodes =
        [
            new("root", Vector3.Zero, 3f),
            new("lego/~02/~03_01", new Vector3(5f, 12f, 0f), 7f),
        ];

        Assert.False(EnbCurrentRetailAttachedRadiusRuntime.TryResolve(nodes, out _));
    }

    [Fact]
    public void PlayerContactUsesRetailUnknownWhenNamePropertiesAreAbsent()
    {
        EnbSceneObjectSnapshot observer = Object(
            1, 0x00, Vector3.Zero, isLocalShip: true);
        EnbSceneObjectSnapshot player = Object(
            2,
            0x01,
            new Vector3(20f, 0f, 0f),
            avatar: Avatar("Passing", "Pilot"));

        EnbRadarContact contact = Assert.Single(
            EnbCurrentRetailRadarCompatibilityAdapter.Build(
                observer,
                new[] { observer, player },
                scanRange: 100).Contacts);

        // 6FF7A0 consults materialized Owner/Name, never avatar identity.
        Assert.Equal("Unknown", contact.Name);
    }

    [Fact]
    public void PlayerContactUsesRetailTitleOwnerAndSameGuildTagPresentation()
    {
        EnbScenePlayerPresentationSnapshot observerPlayer = PlayerPresentation(
            owner: "Observer",
            guildName: "Star Wardens",
            guildRankName: "Officer");
        EnbSceneObjectSnapshot observer = Object(
            1, 0x01, Vector3.Zero, isLocalShip: true, playerPresentation: observerPlayer);
        EnbScenePlayerPresentationSnapshot remotePlayer = PlayerPresentation(
            owner: "Kestralion",
            title: "Consul",
            guildName: "Star Wardens",
            guildRankName: "Master Chief",
            sameGuildColor: new Vector3(0f, 1f, 0f));
        EnbSceneObjectSnapshot player = Object(
            2,
            0x01,
            new Vector3(20f, 0f, 0f),
            name: "ship-internal-name",
            playerPresentation: remotePlayer);

        EnbRadarContact contact = Assert.Single(
            EnbCurrentRetailRadarCompatibilityAdapter.Build(
                observer,
                new[] { observer, player },
                scanRange: 100).Contacts);

        Assert.Equal("Consul Kestralion", contact.Name);
        Assert.Equal("[Star Wardens Master Chief]", contact.GuildTag);
        Assert.Equal(new Vector3(0f, 1f, 0f), contact.GuildTagColor);
    }

    [Theory]
    [InlineData(0x00, "g_npc.tga")]
    [InlineData(0x01, "g_pc.tga")]
    [InlineData(0x02, "g_cap.tga")]
    [InlineData(0x03, "g_planet.tga")]
    [InlineData(0x04, "g_item.tga")]
    [InlineData(0x09, "g_misile.tga")]
    [InlineData(0x0A, "g_nebula.tga")]
    [InlineData(0x0B, "g_gate.tga")]
    [InlineData(0x0C, "g_sbase.tga")]
    [InlineData(0x18, "g_build.tga")]
    [InlineData(0x19, "g_dead.tga")]
    [InlineData(0x1B, "g_mine.tga")]
    [InlineData(0x22, "g_resrce.tga")]
    [InlineData(0x23, "g_drone.tga")]
    [InlineData(0x25, "g_deco.tga")]
    [InlineData(0x26, "g_resrce.tga")]
    [InlineData(0xFF, "g_dead.tga")]
    public void CurrentRetailObjectTypesSelectAuthoredGutterAssets(byte objectType, string expected)
        => Assert.Equal(expected, EnbCurrentRetailRadarCompatibilityAdapter.ResolveIconAssetName(objectType));

    [Theory]
    [InlineData(0x03)]
    [InlineData(0x04)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x18)]
    [InlineData(0x19)]
    [InlineData(0x1B)]
    [InlineData(0x22)]
    [InlineData(0x23)]
    [InlineData(0x25)]
    [InlineData(0x26)]
    [InlineData(0xFF)]
    public void CurrentRetailReactionlessObjectTypesAlwaysUseNeutralGutterPalette(byte objectType)
    {
        Assert.False(EnbCurrentRetailGutterColorRuntime.UsesReactionPalette(objectType));
        Assert.Equal(
            "Neutral",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(objectType, relationshipRaw: 2, isAttacking: true));
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x0B)]
    [InlineData(0x0C)]
    [InlineData(0x1C)]
    [InlineData(0x21)]
    public void CurrentRetailReactionCapableTypesBypassNeutralPalette(byte objectType)
        => Assert.True(EnbCurrentRetailGutterColorRuntime.UsesReactionPalette(objectType));

    [Theory]
    [InlineData(0, "KOS")]
    [InlineData(1, "Shun")]
    [InlineData(2, "Friendly")]
    [InlineData(3, "Adore")]
    [InlineData(99, "Neutral")]
    public void GateAndStationGutterColorsFollowFactionReaction(uint relationshipRaw, string expected)
    {
        Assert.Equal(
            expected,
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(0x0B, relationshipRaw, isAttacking: false));
        Assert.Equal(
            expected,
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(0x0C, relationshipRaw, isAttacking: false));
    }

    [Fact]
    public void AttackingAndPlayerPalettesRetainRetailPrecedence()
    {
        Assert.Equal(
            "Attacking",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(0x01, relationshipRaw: 2, isAttacking: true));
        Assert.Equal(
            "Player",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(0x01, relationshipRaw: 2, isAttacking: false));
        Assert.Equal(
            "Neutral",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(0x03, relationshipRaw: 0, isAttacking: true));
    }

    [Fact]
    public void PlayerPaletteUsesAttackingGroupPvpAndPlayerPrecedence()
    {
        Assert.Equal(
            "Attacking",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(
                0x01, relationshipRaw: 2, isAttacking: true, isGroupMember: true, isPvpAttackable: true));
        Assert.Equal(
            "Group",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(
                0x01, relationshipRaw: 2, isAttacking: false, isGroupMember: true, isPvpAttackable: true));
        Assert.Equal(
            "PVPAttackable",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(
                0x01, relationshipRaw: 2, isAttacking: false, isGroupMember: false, isPvpAttackable: true));
        Assert.Equal(
            "Player",
            EnbCurrentRetailGutterColorRuntime.ResolveCategory(
                0x01, relationshipRaw: 2, isAttacking: false));
    }

    [Fact]
    public void PlayerContactCarriesGroupAndMutualPvpState()
    {
        EnbScenePlayerPresentationSnapshot localPresentation = PlayerPresentation("Observer") with
        {
            IsInPvp = true,
        };
        EnbSceneObjectSnapshot observer = Object(
            1, 0x01, Vector3.Zero, isLocalShip: true, playerPresentation: localPresentation);
        EnbSceneObjectSnapshot groupedPlayer = Object(
            2,
            0x01,
            new Vector3(20f, 0f, 0f),
            playerPresentation: PlayerPresentation("Grouped") with { IsInPvp = true });
        EnbSceneObjectSnapshot pvpPlayer = Object(
            3,
            0x01,
            new Vector3(30f, 0f, 0f),
            playerPresentation: PlayerPresentation("Pvp") with { IsInPvp = true });
        EnbSceneObjectSnapshot ordinaryPlayer = Object(
            4,
            0x01,
            new Vector3(40f, 0f, 0f),
            playerPresentation: PlayerPresentation("Ordinary"));

        EnbRadarFrame frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(
            observer,
            new[] { observer, groupedPlayer, pvpPlayer, ordinaryPlayer },
            scanRange: 100,
            groupMemberIds: new[] { groupedPlayer.GameId });

        EnbRadarContact grouped = Assert.Single(frame.Contacts, static contact => contact.GameId == 2);
        Assert.True(grouped.IsGroupMember);
        Assert.False(grouped.IsPvpAttackable);
        EnbRadarContact pvp = Assert.Single(frame.Contacts, static contact => contact.GameId == 3);
        Assert.False(pvp.IsGroupMember);
        Assert.True(pvp.IsPvpAttackable);
        EnbRadarContact ordinary = Assert.Single(frame.Contacts, static contact => contact.GameId == 4);
        Assert.False(ordinary.IsGroupMember);
        Assert.False(ordinary.IsPvpAttackable);
    }

    [Fact]
    public void TargetCycleUsesSurfaceDistanceAndWrapsInBothDirections()
    {
        EnbSceneObjectSnapshot observer = Object(1, 0x00, Vector3.Zero, isLocalShip: true);
        EnbSceneObjectSnapshot near = Object(2, 0x03, new Vector3(20f, 0f, 0f));
        EnbSceneObjectSnapshot middle = Object(3, 0x03, new Vector3(40f, 0f, 0f));
        EnbSceneObjectSnapshot far = Object(4, 0x03, new Vector3(60f, 0f, 0f));
        EnbRadarFrame frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(
            observer,
            new[] { observer, far, near, middle },
            scanRange: 100);

        Assert.Equal(3u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, near.GameId, previous: false, preserveCurrentCategory: false));
        Assert.Equal(2u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, far.GameId, previous: false, preserveCurrentCategory: false));
        Assert.Equal(4u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, near.GameId, previous: true, preserveCurrentCategory: false));
        Assert.Equal(2u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, currentTargetId: null, previous: false, preserveCurrentCategory: false));
        Assert.Equal(4u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, currentTargetId: null, previous: true, preserveCurrentCategory: false));
    }

    [Fact]
    public void NearestEnemyUsesAttackableCategoryAndSurfaceDistanceAndClearsWhenEmpty()
    {
        var observer = Object(1, 0, Vector3.Zero, isLocalShip: true);
        var near = Object(2, 0, new Vector3(40, 0, 0));
        var large = Object(3, 0, new Vector3(60, 0, 0), assetRadius: 30);
        var friendly = Object(4, 0, new Vector3(10, 0, 0)) with { RelationshipRaw = 2 };
        var debris = Object(5, 3, new Vector3(5, 0, 0));
        var frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,
            new[] { observer, near, large, friendly, debris }, scanRange: 100);
        Assert.Equal(3u, EnbCurrentRetailTargetingRuntime.SelectNearest(frame, EnbRetailTargetCategory.Attackable));
        frame = EnbCurrentRetailRadarCompatibilityAdapter.ApplyDistanceRadiusOverrides(frame,
            new Dictionary<ulong, float> { [near.RenderIdentity] = 35 });
        Assert.Equal(2u, EnbCurrentRetailTargetingRuntime.SelectNearest(frame, EnbRetailTargetCategory.Attackable));
        var empty = EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,
            new[] { observer, friendly, debris }, scanRange: 100);
        Assert.Equal(0u, EnbCurrentRetailTargetingRuntime.SelectNearest(empty, EnbRetailTargetCategory.Attackable));
    }

    [Theory]
    [InlineData(EnbRetailTargetCategory.Friendly, 2u)]
    [InlineData(EnbRetailTargetCategory.Navigation, 3u)]
    [InlineData(EnbRetailTargetCategory.Object, 4u)]
    public void NearestCategoryIgnoresCloserOtherCategoriesAndClearsWhenMissing(EnbRetailTargetCategory category, uint expected)
    {
        var observer = Object(1, 0, Vector3.Zero, isLocalShip: true);
        var friendly = Object(2, 0, new Vector3(40, 0, 0)) with { RelationshipRaw = 2 };
        var navigation = Object(3, 0x26, new Vector3(20, 0, 0), navigation: new(0, false, 0, true));
        var debris = Object(4, 3, new Vector3(10, 0, 0));
        var hostile = Object(5, 0, new Vector3(5, 0, 0));
        var objects = new[] { observer, friendly, navigation, debris, hostile };
        var frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(observer, objects, scanRange: 100);
        Assert.Equal(expected, EnbCurrentRetailTargetingRuntime.SelectNearest(frame, category));
        var missing = EnbCurrentRetailRadarCompatibilityAdapter.Build(observer, objects.Where(o => o.GameId != expected).ToArray(), scanRange: 100);
        Assert.Equal(0u, EnbCurrentRetailTargetingRuntime.SelectNearest(missing, category));
    }

    [Fact]
    public void NearestAttackerUsesAttackStateIndependentlyOfReactionAndContextRetainsIt()
    {
        var observer = Object(1, 0, Vector3.Zero, isLocalShip: true);
        var hostileIdle = Object(2, 0, new Vector3(5, 0, 0));
        var friendlyAttacker = Object(3, 0, new Vector3(20, 0, 0)) with { RelationshipRaw = 2, IsAttacking = true };
        var another = Object(4, 0, new Vector3(40, 0, 0)) with { RelationshipRaw = 2, IsAttacking = true };
        var friendlyIdle = Object(5, 0, new Vector3(30, 0, 0)) with { RelationshipRaw = 2 };
        var frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(observer,
            new[] { observer, hostileIdle, friendlyAttacker, another, friendlyIdle }, scanRange: 100);
        Assert.Equal(3u, EnbCurrentRetailTargetingRuntime.SelectNearest(frame, EnbRetailTargetCategory.Attacking));
        Assert.Equal(4u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(frame, 3, false, true));
        frame = frame with { Contacts = frame.Contacts.Select(c => c with { IsAttacking = false }).ToImmutableArray() };
        Assert.Equal(0u, EnbCurrentRetailTargetingRuntime.SelectNearest(frame, EnbRetailTargetCategory.Attacking));
    }

    [Fact]
    public void GroupKeysPreserveSlotIndexAndDistinguishUnavailableFromAbsentObject()
    {
        var group = new EnbSceneGroupSnapshot(false, "", -1,
            [new(4, "", 9, -1), new(0, "remote", 8, -1), new(2, "empty", 0, -1)], null, false, []) { AuxDataState = 2 };
        var objects = new[] { Object(9, 1, new Vector3(100000, 0, 0)) };
        Assert.Equal(9u, EnbCurrentRetailTargetingRuntime.SelectGroupMember(group, objects, 4));
        Assert.Equal(0u, EnbCurrentRetailTargetingRuntime.SelectGroupMember(group, objects, 0));
        Assert.Null(EnbCurrentRetailTargetingRuntime.SelectGroupMember(group, objects, 2));
        Assert.Null(EnbCurrentRetailTargetingRuntime.SelectGroupMember(group, objects, 5));
        Assert.Null(EnbCurrentRetailTargetingRuntime.SelectGroupMember(group with { AuxDataState = 0 }, objects, 4));
    }

    [Fact]
    public void NearestPlayerRestoresSelfOnlyForSelectionAndAdvancesTwice()
    {
        var self = Object(1, 1, Vector3.Zero, isLocalShip: true);
        var near = Object(2, 1, new Vector3(20, 0, 0));
        var far = Object(3, 1, new Vector3(40, 0, 0));
        var mob = Object(4, 0, new Vector3(5, 0, 0));
        var frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(self, new[] { self, far, near, mob }, 100);
        Assert.Equal(2u, EnbCurrentRetailTargetingRuntime.SelectNearestPlayer(frame, self));
        Assert.DoesNotContain(frame.Contacts, c => c.GameId == self.GameId);
        frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(self, new[] { self, mob }, 100);
        Assert.Equal(1u, EnbCurrentRetailTargetingRuntime.SelectNearestPlayer(frame, self));
        Assert.Equal(0u, EnbCurrentRetailTargetingRuntime.SelectNearestPlayer(EnbRadarFrame.Unavailable(), self));
    }

    [Fact]
    public void ContextCycleRetainsTheSelectedRetailCategory()
    {
        EnbSceneObjectSnapshot observer = Object(1, 0x00, Vector3.Zero, isLocalShip: true);
        EnbSceneObjectSnapshot player = Object(2, 0x01, new Vector3(10f, 0f, 0f));
        EnbSceneObjectSnapshot navA = Object(
            3, 0x26, new Vector3(20f, 0f, 0f),
            navigation: new EnbSceneNavigationSnapshot(0f, false, 0, true));
        EnbSceneObjectSnapshot objectTarget = Object(4, 0x03, new Vector3(30f, 0f, 0f));
        EnbSceneObjectSnapshot navB = Object(
            5, 0x26, new Vector3(40f, 0f, 0f),
            navigation: new EnbSceneNavigationSnapshot(0f, false, 0, true));
        EnbRadarFrame frame = EnbCurrentRetailRadarCompatibilityAdapter.Build(
            observer,
            new[] { observer, player, navA, objectTarget, navB },
            scanRange: 100);

        Assert.Equal(5u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, navA.GameId, previous: false, preserveCurrentCategory: true));
        Assert.Equal(3u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, navB.GameId, previous: false, preserveCurrentCategory: true));
        Assert.Equal(2u, EnbCurrentRetailTargetingRuntime.SelectAdjacent(
            frame, player.GameId, previous: false, preserveCurrentCategory: true));
    }

    [Fact]
    public void VerbTreeProjectsIntoRetailLeftToRightButtonSlots()
    {
        uint[] ascending =
        [
            0x00040000, // Group
            0x00050000, // Message
            0x00060000, // Trade
            0x000D0000, // Follow
        ];

        Assert.Equal(
            new uint[] { 0x000D0000, 0x00060000, 0x00050000, 0x00040000 },
            EnbCurrentRetailTargetingRuntime.OrderVerbButtonsLeftToRight(ascending));
        Assert.Equal(
            new uint[] { 0x000B0000, 0x00080000 },
            EnbCurrentRetailTargetingRuntime.OrderVerbButtonsLeftToRight(
                [0x00080000, 0x000B0000]));
        Assert.Equal(
            new uint[] { 5, 4, 3, 2, 1 },
            EnbCurrentRetailTargetingRuntime.OrderVerbButtonsLeftToRight([1, 2, 3, 4, 5, 6]));
    }

    private static EnbSceneObjectSnapshot Object(
        uint gameId,
        byte objectType,
        Vector3 position,
        float assetRadius = 0f,
        bool appearsInRadar = false,
        float radarRange = 0f,
        EnbSceneNavigationSnapshot? navigation = null,
        bool isLocalShip = false,
        bool isCurrentTarget = false,
        string? name = null,
        EnbSceneAvatarSnapshot? avatar = null,
        EnbScenePlayerPresentationSnapshot? playerPresentation = null)
        => new EnbSceneObjectSnapshot(
            RenderIdentity: gameId,
            VisualGeneration: 1,
            GameId: gameId,
            IsLocalShip: isLocalShip,
            IsCurrentTarget: isCurrentTarget,
            ObjectType: objectType,
            BaseAsset: 1549,
            Scale: 1f,
            HsvH: 0f,
            HsvS: 0f,
            HsvV: 0f,
            RelationshipRaw: 1,
            IsAttacking: false,
            Name: name,
            Radar: new EnbSceneRadarSnapshot(appearsInRadar, radarRange, navigation),
            Spatial: Spatial(position),
            BaseAssetReference: null,
            ClientBaseAssetReference: assetRadius > 0f
                ? new EnbPresentationClientBaseAssetReference(
                    Selector: 1549,
                    Radius: assetRadius,
                    RadiusAdjust: 0f,
                    Provenance: new EnbPresentationProvenance("fixture"))
                : null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: avatar,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0f)
        {
            PlayerPresentation = playerPresentation,
        };

    private static EnbScenePlayerPresentationSnapshot PlayerPresentation(
        string owner,
        string? title = null,
        string? guildName = null,
        string? guildRankName = null,
        Vector3? sameGuildColor = null)
        => new(
            Owner: owner,
            Title: title,
            Rank: null,
            CombatLevel: null,
            ShieldPercent: 1f,
            GuildName: guildName,
            GuildRank: string.IsNullOrEmpty(guildName) ? -1 : 0,
            GuildRankName: guildRankName,
            SameGuildTagColor: sameGuildColor,
            OtherGuildTagColor: null);

    private static EnbSceneAvatarSnapshot Avatar(string firstName, string lastName)
        => new(
            FirstName: firstName,
            LastName: lastName,
            AvatarType: 0,
            UnknownU8: 0,
            AvatarVersion: 0,
            Race: 0,
            Profession: 0,
            Gender: 0,
            MoodType: 0,
            Personality: 0,
            Nlp: 0,
            BodyType: 0,
            PantsType: 0,
            HeadType: 0,
            HairNum: 0,
            EarNum: 0,
            GoggleNum: 0,
            BeardNum: 0,
            WeaponHipNum: 0,
            WeaponUniqueNum: 0,
            WeaponBackNum: 0,
            HeadTextureNum: 0,
            TattooTextureNum: 0,
            TattooOffset: ImmutableArray<float>.Empty,
            HairColor: ImmutableArray<float>.Empty,
            BeardColor: ImmutableArray<float>.Empty,
            EyeColor: ImmutableArray<float>.Empty,
            SkinColor: ImmutableArray<float>.Empty,
            ShirtPrimaryColor: ImmutableArray<float>.Empty,
            ShirtSecondaryColor: ImmutableArray<float>.Empty,
            PantsPrimaryColor: ImmutableArray<float>.Empty,
            PantsSecondaryColor: ImmutableArray<float>.Empty,
            ShirtPrimaryMetal: 0,
            ShirtSecondaryMetal: 0,
            PantsPrimaryMetal: 0,
            PantsSecondaryMetal: 0,
            Skeleton: string.Empty,
            BodyWeight: ImmutableArray<uint>.Empty,
            HeadWeight: ImmutableArray<uint>.Empty);

    private static EnbSceneSpatialSnapshot Spatial(Vector3 position)
        => new(
            EnbSceneSpatialProviderKind.Fixed,
            new EnbSceneTransformSnapshot(
                ClientTime: 0,
                Position: position,
                Orientation: Quaternion.Identity,
                Velocity: Vector3.Zero,
                Speed: 0f,
                Rotation: Vector3.Zero,
                OrientationColumn0: Vector3.UnitX),
            Planet: null);
}
