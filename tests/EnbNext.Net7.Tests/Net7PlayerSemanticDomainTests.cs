using System.Buffers.Binary;
using System.Text;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7PlayerSemanticDomainTests
{
    [Fact]
    public void MissionViewUsesNonEmptyNameDescendingStartTimeAndStageTimerPrecedence()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.0.Name", Lp("Older")),
            ("LocalPlayerAuxData.Missions.0.StartTime", I32(100)),
            ("LocalPlayerAuxData.Missions.0.Stage", I32(1)),
            ("LocalPlayerAuxData.Missions.0.Stages.0.Text", Lp("Stage A")),
            ("LocalPlayerAuxData.Missions.0.Stages.0.IsTimed", U8(1)),
            ("LocalPlayerAuxData.Missions.0.StageExpirationTime", I32(3500)),
            ("LocalPlayerAuxData.Missions.0.IsTimed", U8(1)),
            ("LocalPlayerAuxData.Missions.0.ExpirationTime", I32(9000)),
            ("LocalPlayerAuxData.Missions.1.Name", Lp("Newer")),
            ("LocalPlayerAuxData.Missions.1.StartTime", I32(200)),
            ("LocalPlayerAuxData.Missions.1.IsTimed", U8(1)),
            ("LocalPlayerAuxData.Missions.1.ExpirationTime", I32(6000)));

        IReadOnlyList<Net7MissionView> visible = domain.GetVisibleMissions(1000);

        Assert.Equal(2, visible.Count);
        Assert.Equal("Newer", visible[0].Name);
        Assert.Equal(5000L, visible[0].RemainingMilliseconds!.Value);
        Assert.Equal("Older", visible[1].Name);
        Assert.Equal("Stage A", visible[1].CurrentStageText);
        Assert.Equal(2500L, visible[1].RemainingMilliseconds!.Value);
    }

    [Fact]
    public void MissionForfeitRequiresActiveForfeitableStateAndExplicitConfirmationWhileClearUsesTerminalState()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Missions.2.ID", I32(44)),
            ("LocalPlayerAuxData.Missions.2.Name", Lp("Mission")),
            ("LocalPlayerAuxData.Missions.2.IsForfeitable", U8(1)));

        Assert.Throws<InvalidOperationException>(() => domain.RequireForfeitMissionId(2, false, 0));
        Assert.Equal(44u, domain.RequireForfeitMissionId(2, true, 0));
        Assert.Throws<InvalidOperationException>(() => domain.RequireClearMissionId(2, 0));

        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.Missions.2.IsComplete", U8(1)));
        Assert.Throws<InvalidOperationException>(() => domain.RequireForfeitMissionId(2, true, 0));
        Assert.Equal(44u, domain.RequireClearMissionId(2, 0));
    }

    [Fact]
    public void MissionNotificationsSuppressFirstBatchThenUseCurrentStatusPrecedence()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,
            [("LocalPlayerAuxData.Missions.0.Name", Lp("A")), ("LocalPlayerAuxData.Missions.0.Stage", I32(1))],
            stateTwoPaths: ["LocalPlayerAuxData.Missions"]);

        Assert.Empty(domain.ConsumeMissionNotifications(0));
        Assert.Equal(1, Require(world.LocalPlayerAuxData, "LocalPlayerAuxData.Missions").State);

        Apply(world.LocalPlayerAuxData,
            [("LocalPlayerAuxData.Missions.0.Stage", I32(2))],
            stateTwoPaths: ["LocalPlayerAuxData.Missions"]);
        Net7MissionNotification stage = Assert.Single(domain.ConsumeMissionNotifications(0));
        Assert.Equal(Net7MissionNotificationKind.StageCompleted, stage.Kind);

        Apply(world.LocalPlayerAuxData,
            [("LocalPlayerAuxData.Missions.0.IsFailed", U8(1)), ("LocalPlayerAuxData.Missions.0.IsComplete", U8(1))],
            stateTwoPaths: ["LocalPlayerAuxData.Missions"]);
        Net7MissionNotification terminal = Assert.Single(domain.ConsumeMissionNotifications(0));
        Assert.Equal(Net7MissionNotificationKind.Completed, terminal.Kind);
    }

    [Fact]
    public async Task MissionFramePublishesBothStageLinesAndSoundOnceWithoutChatHistory()
    {
        await using var session = new Net7Session(new Net7ServerOptions());
        var schema = session.World.LocalPlayerAuxData;
        Apply(schema, ("LocalPlayerAuxData.Missions.0.Name", Lp("A <tag>mission</tag>")),
            ("LocalPlayerAuxData.Missions.0.Stage", I32(1)));
        session.AdvanceLocalMissionPresentation();
        Assert.Empty(session.Gameplay.Social.PresentationEvents);
        int publications = 0;
        session.PresentationStateChanged += _ => publications++;
        Apply(schema, ("LocalPlayerAuxData.Missions.0.Stage", I32(2)),
            ("LocalPlayerAuxData.Missions.0.StageCount", I32(3)),
            ("LocalPlayerAuxData.Missions.0.Stages.1.Text", Lp("Go to <color>Arx</color>.")));
        session.AdvanceLocalMissionPresentation();
        var events = session.Gameplay.Social.PresentationEvents;
        Assert.Equal(new[] { "Completed Stage 1 of A mission.", "(2/3) Go to Arx.", "Mission_Updated" }, events.Select(e => e.Text));
        Assert.Equal(new uint[] { 3, 7 }, events.Where(e => e.ScreenLine is not null).Select(e => e.ScreenLine!.DisplayPriority));
        Assert.All(events.Where(e => e.ScreenLine is not null), e => Assert.Equal(5000u, e.ScreenLine!.TimeMilliseconds));
        Assert.Equal(EnbNext.Net7.Social.Net7ChatPresentationKind.InterfaceSound, events[2].Kind);
        Assert.Empty(session.Gameplay.Social.Messages);
        Assert.Equal(1, publications);
        session.AdvanceLocalMissionPresentation();
        Assert.Equal(3, events.Count);
        Assert.Equal(1, publications);
        // No mutation of the authoritative mission text while consuming dirtiness.
        Assert.Equal("A <tag>mission</tag>", session.PlayerSemantics.GetMission(0, 0).Name);
    }

    [Theory]
    [InlineData(1, false, "Mission_New")]
    [InlineData(1, true, "Mission_Updated")]
    [InlineData(2, false, "Mission_Accomplished")]
    public void MissionStageAndCompletionCoexistAndStageSoundWins(int stage, bool messaged, string sound)
    {
        var world = new Net7WorldState(); var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.Missions.0.Name", Lp("Mission")));
        Assert.Empty(domain.ConsumeMissionNotifications(0));
        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.Missions.0.Stage", I32(stage)),
            ("LocalPlayerAuxData.Missions.0.HasGivenNewMissionMessage", U8((byte)(messaged ? 1 : 0))),
            ("LocalPlayerAuxData.Missions.0.IsComplete", U8(1)),
            ("LocalPlayerAuxData.Missions.0.IsExpired", U8(1)),
            ("LocalPlayerAuxData.Missions.0.IsFailed", U8(1)));
        var notification = Assert.Single(domain.ConsumeMissionNotifications(0));
        Assert.Equal(Net7MissionNotificationKind.Completed, notification.Kind);
        Assert.Equal(sound, notification.Sound);
        Assert.Equal(stage == 2 ? new[] { "Completed Stage 1 of Mission.", "Mission Completed!" }
            : new[] { "Mission Completed!" }, notification.Lines.Select(l => l.Text));
    }

    [Theory]
    [InlineData("a <b>x</b>", "a x")]
    [InlineData("a <unfinished", "a <unfinished")]
    [InlineData("a <b>x<unfinished", "a x")]
    [InlineData("a > b", "a > b")]
    public void MissionMarkupMatchesRetailCompletedSpanRule(string input, string expected)
        => Assert.Equal(expected, Net7PlayerSemanticDomain.StripMissionMarkup(input));

    [Fact]
    public void SectorResetSuppressesOnlyItsFirstMissionBatch()
    {
        var world=new Net7WorldState();var domain=new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,("LocalPlayerAuxData.Missions.0.Name",Lp("A")));
        Assert.Empty(domain.ConsumeMissionNotifications(0));
        domain.ResetForSectorBoundary();
        Apply(world.LocalPlayerAuxData,("LocalPlayerAuxData.Missions.0.Stage",I32(2)));
        Assert.Empty(domain.ConsumeMissionNotifications(0));
        Apply(world.LocalPlayerAuxData,("LocalPlayerAuxData.Missions.0.Stage",I32(3)));
        Assert.Single(domain.ConsumeMissionNotifications(0));
    }

    [Fact]
    public void JournalProjectionUsesAuthoredCountFirstEmptyStageAndStatusPrecedence()
    {
        var world=new Net7WorldState();var schema=world.LocalPlayerAuxData;
        Apply(schema,("LocalPlayerAuxData.Missions.0.IsForfeitable",U8(1)),
            ("LocalPlayerAuxData.Missions.0.Name",Lp("Mission")),
            ("LocalPlayerAuxData.Missions.0.Summary",Lp("A <tag>summary</tag>")),
            ("LocalPlayerAuxData.Missions.0.Reward",Lp("100 credits")),
            ("LocalPlayerAuxData.Missions.0.FailureConsequence",Lp("Lost reputation")),
            ("LocalPlayerAuxData.Missions.0.Stage",I32(1)),
            ("LocalPlayerAuxData.Missions.0.StageCount",I32(4)),
            ("LocalPlayerAuxData.Missions.0.Stages.0.Text",Lp("First")),
            ("LocalPlayerAuxData.Missions.0.Stages.1.Text",Lp("Second")),
            ("LocalPlayerAuxData.Missions.0.Stages.3.Text",Lp("Must not skip the empty third")),
            ("LocalPlayerAuxData.Missions.0.Stages.0.IsTimed",U8(1)),
            ("LocalPlayerAuxData.Missions.0.StageExpirationTime",I32(65000)),
            ("LocalPlayerAuxData.Missions.0.ExpirationTime",I32(90000)));
        EnbNext.Presentation.EnbCharacterMissionSnapshot Project()=>Assert.Single(EnbNext.Presentation.EnbSceneInventorySnapshotBuilder.Build(null,schema,null,null).Missions);
        var mission=Project();
        Assert.Equal("Reward: 100 credits\n\nFailure: Lost reputation\n\nA summary\n\nMission Steps:\n1. First\n2. Second\n",mission.Description);
        Assert.Equal("(1/4) First",mission.CurrentStep);Assert.True(mission.CanForfeit);Assert.False(mission.CanClear);
        Assert.Equal(65000L,mission.ExpirationTime);
        Apply(schema,("LocalPlayerAuxData.Missions.0.StageCount",I32(1)));
        Assert.DoesNotContain("Second",Project().Description);
        Apply(schema,("LocalPlayerAuxData.Missions.0.StageCount",I32(0)));
        Assert.Equal("(1) First",Project().CurrentStep);Assert.EndsWith("Mission Steps:\n",Project().Description);
        Apply(schema,("LocalPlayerAuxData.Missions.0.IsExpired",U8(1)));
        Assert.Equal("Mission Expired!",Project().CurrentStep);
        Apply(schema,("LocalPlayerAuxData.Missions.0.IsFailed",U8(1)));
        Assert.Equal("Mission Failed!",Project().CurrentStep);
        Apply(schema,("LocalPlayerAuxData.Missions.0.IsComplete",U8(1)));
        mission=Project();Assert.Equal("Mission Complete!",mission.CurrentStep);Assert.True(mission.CanClear);Assert.False(mission.CanForfeit);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "0 secs")]
    [InlineData(59999, "59 secs")]
    [InlineData(60000, "0:01")]
    [InlineData(3599999, "0:59")]
    [InlineData(3600000, "1:00")]
    public void MissionTimerUsesRetailTruncationAndHourMinuteFormat(int remaining,string expected)
    {
        var mission=new EnbNext.Presentation.EnbCharacterMissionSnapshot("A", ""){ExpirationTime=1000L+remaining};
        Assert.Equal(expected,mission.TimerText(1000));
        Assert.Equal("",mission.TimerText((uint)(1001+remaining)));
    }

    [Fact]
    public void SkillProjectionUsesStableIndexAndExactAvailabilityPredicate()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.RPGInfo.Skills.7.Level", I32(2)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.RecycleTime", I32(5000)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.LastActivation", I32(1000)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.Availability", Raw16(1, 0, 10, 3)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.QuestOnlyLevels", I32(1)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.MaxSkillLevels", I32(3)),
            ("LocalPlayerAuxData.RPGInfo.Skills.7.Affiliations", Lp("A")),
            ("LocalPlayerAuxData.RPGInfo.SkillPoints", I32(9)));

        Net7SkillView skill = domain.GetSkill(7);
        Assert.Equal(7, skill.SkillIndex);
        Assert.False(skill.CanTrain); // 00548DBB compares against QuestOnlyLevels, not MaxSkillLevels.
        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.RPGInfo.Skills.7.QuestOnlyLevels", I32(3)));
        Assert.True(domain.GetSkill(7).CanTrain);
        Assert.Equal(10, skill.Availability.RequiredLevel);
        Assert.Equal(3, skill.Availability.UpgradeCost);
        Assert.Equal(9, skill.SkillPoints);
        Assert.Equal(3000L, skill.RechargeRemaining(3000));

        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.RPGInfo.Skills.7.Availability", Raw16(1, 1, 10, 3)));
        Assert.False(domain.GetSkill(7).CanTrain);
    }

    [Fact]
    public void AbilityPresentationPreservesReadinessInputsWithoutTurningThemIntoSendAdmission()
    {
        var world = new Net7WorldState();
        var domain = new Net7PlayerSemanticDomain(world);
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.HasAbility", U8(0)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.Usage", U32(0x01020304)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.Targets", U32(0x11223344)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.Range", I32(750)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.Radius", I32(50)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.Validity", U32(0xDEADBEEF)),
            ("LocalPlayerAuxData.RPGInfo.SkillAbilities.5.CanBeUsedWhileIncapacitated", U8(1)));

        Net7SkillAbilityPresentation view = domain.GetSkillAbilityPresentation(5, 0, 1000);
        Assert.False(view.HasAbility);
        Assert.Equal(0x11223344u, view.TargetsRaw);
        Assert.Equal(750, view.Range);
        Assert.Equal(0xDEADBEEFu, view.ValidityRaw);
        Assert.True(view.CanBeUsedWhileIncapacitated);
    }

    [Fact]
    public void ExpiredNonPermanentBuffIsHiddenOnlyInPresentation()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(1, 1, 0, 0, 0, 0, 0));
        world.SetLocalPlayerId(1);
        Net7AuxDataSchema ship = world.Objects.FindFirst(1)!.AuxData!;
        Apply(ship,
            ("ShipAuxData.BuffArray.0.BuffType", Lp("Buff")),
            ("ShipAuxData.BuffArray.0.ScrubTypeName", Lp("Type")),
            ("ShipAuxData.BuffArray.0.IsPermanent", U8(0)),
            ("ShipAuxData.BuffArray.0.BuffRemovalTime", I32(500)));
        var domain = new Net7PlayerSemanticDomain(world);

        Net7BuffPresentation buff = Assert.Single(domain.GetBuffPresentation(1000));
        Assert.False(buff.IsVisible);
        Assert.Equal(500L, ((Net7AuxDataTime)Require(ship, "ShipAuxData.BuffArray.0.BuffRemovalTime").Value!).RelativeValue);
    }

    [Fact]
    public void BuffPresentationUsesRetailOccupancyExpiryAndElementOrdering()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(1, 1, 0, 0, 0, 0, 0));
        world.SetLocalPlayerId(1);
        Net7AuxDataSchema ship = world.Objects.FindFirst(1)!.AuxData!;
        Assert.Equal(15, Require(ship, "ShipAuxData.BuffArray").Children.Count);
        for (int slot = 0; slot < 15; slot++)
            Assert.Equal(5, Require(ship, $"ShipAuxData.BuffArray.{slot}.Elements").Children.Count);

        Apply(ship,
            ("ShipAuxData.BuffArray.0.BuffType", Lp("Ordered")),
            ("ShipAuxData.BuffArray.0.IsPermanent", U8(0)),
            ("ShipAuxData.BuffArray.0.BuffRemovalTime", I32(1000)),
            ("ShipAuxData.BuffArray.0.Elements.0.SourceEntity", Lp("Inactive")),
            ("ShipAuxData.BuffArray.0.Elements.0.SourceObject", Lp("z")),
            ("ShipAuxData.BuffArray.0.Elements.0.Magnitude", I32(99)),
            ("ShipAuxData.BuffArray.0.Elements.0.IsActive", U8(0)),
            ("ShipAuxData.BuffArray.0.Elements.0.ExpirationTime", I32(999)),
            ("ShipAuxData.BuffArray.0.Elements.1.SourceEntity", Lp("Beta")),
            ("ShipAuxData.BuffArray.0.Elements.1.SourceObject", Lp("z")),
            ("ShipAuxData.BuffArray.0.Elements.1.Magnitude", I32(20)),
            ("ShipAuxData.BuffArray.0.Elements.1.IsActive", U8(1)),
            ("ShipAuxData.BuffArray.0.Elements.1.ExpirationTime", I32(900)),
            ("ShipAuxData.BuffArray.0.Elements.2.SourceEntity", Lp("Alpha")),
            ("ShipAuxData.BuffArray.0.Elements.2.SourceObject", Lp("z")),
            ("ShipAuxData.BuffArray.0.Elements.2.Magnitude", I32(20)),
            ("ShipAuxData.BuffArray.0.Elements.2.IsActive", U8(1)),
            ("ShipAuxData.BuffArray.0.Elements.2.ExpirationTime", I32(900)),
            ("ShipAuxData.BuffArray.0.Elements.3.SourceEntity", Lp("Later")),
            ("ShipAuxData.BuffArray.0.Elements.3.SourceObject", Lp("z")),
            ("ShipAuxData.BuffArray.0.Elements.3.Magnitude", I32(20)),
            ("ShipAuxData.BuffArray.0.Elements.3.IsActive", U8(1)),
            ("ShipAuxData.BuffArray.0.Elements.3.ExpirationTime", I32(700)),
            ("ShipAuxData.BuffArray.0.Elements.4.SourceEntity", Lp("Small")),
            ("ShipAuxData.BuffArray.0.Elements.4.SourceObject", Lp("z")),
            ("ShipAuxData.BuffArray.0.Elements.4.Magnitude", I32(10)),
            ("ShipAuxData.BuffArray.0.Elements.4.IsActive", U8(1)),
            ("ShipAuxData.BuffArray.0.Elements.4.ExpirationTime", I32(1200)),
            ("ShipAuxData.BuffArray.1.BuffType", Lp("Zero")),
            ("ShipAuxData.BuffArray.1.BuffRemovalTime", I32(0)),
            ("ShipAuxData.BuffArray.2.BuffType", Lp("Negative")),
            ("ShipAuxData.BuffArray.2.BuffRemovalTime", I32(-1)),
            ("ShipAuxData.BuffArray.3.BuffType", Lp("Future")),
            ("ShipAuxData.BuffArray.3.BuffRemovalTime", I32(1001)),
            ("ShipAuxData.BuffArray.4.BuffType", Lp("Permanent")),
            ("ShipAuxData.BuffArray.4.IsPermanent", U8(1)),
            ("ShipAuxData.BuffArray.4.BuffRemovalTime", I32(-1)),
            ("ShipAuxData.BuffArray.5.ScrubTypeName", Lp("orphan scrub")),
            ("ShipAuxData.BuffArray.6.BuffType", Lp("EmptyElement")),
            ("ShipAuxData.BuffArray.6.BuffRemovalTime", I32(1001)),
            ("ShipAuxData.BuffArray.6.Elements.0.Magnitude", I32(0)));

        var domain = new Net7PlayerSemanticDomain(world);
        IReadOnlyList<Net7BuffPresentation> buffs = domain.GetBuffPresentation(1000);

        Assert.DoesNotContain(buffs, buff => buff.SlotIndex == 5);
        Net7BuffPresentation ordered = Assert.Single(buffs, buff => buff.SlotIndex == 0);
        Assert.True(ordered.IsVisible); // equality is not yet "earlier than" current time
        Assert.Equal(new[] { "Alpha", "Beta", "Later", "Small", "Inactive" }, ordered.Elements.Select(element => element.SourceEntity).ToArray());
        Assert.All(ordered.Elements, element => Assert.True(element.IsSourceObjectMagnitudeConsistent));
        Assert.False(Assert.Single(buffs, buff => buff.SlotIndex == 1).IsVisible);
        Assert.False(Assert.Single(buffs, buff => buff.SlotIndex == 2).IsVisible);
        Assert.True(Assert.Single(buffs, buff => buff.SlotIndex == 3).IsVisible);
        Assert.True(Assert.Single(buffs, buff => buff.SlotIndex == 4).IsVisible);
        Assert.Empty(Assert.Single(buffs, buff => buff.SlotIndex == 6).Elements);
        Assert.False(new Net7BuffElementPresentation("", "", 5, true, 0).IsSourceObjectMagnitudeConsistent);

        // Presentation decisions never rewrite the server-authored BuffArray.
        Assert.Equal(new Net7AuxDataTime(0), Assert.IsType<Net7AuxDataTime>(Require(ship, "ShipAuxData.BuffArray.1.BuffRemovalTime").Value));
        Assert.Equal(-1L, Assert.IsType<Net7AuxDataTime>(Require(ship, "ShipAuxData.BuffArray.2.BuffRemovalTime").Value).RelativeValue);
    }

    [Fact]
    public void NavigationProjectionPreservesUnknownNavTypeAndHugeVisitedSemantics()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(77, 1, 0, 3, 0, 0, 0));
        byte[] nav = new byte[14];
        BinaryPrimitives.WriteUInt32LittleEndian(nav.AsSpan(0, 4), 77);
        BinaryPrimitives.WriteInt32LittleEndian(nav.AsSpan(4, 4), BitConverter.SingleToInt32Bits(123.5f));
        nav[8] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(nav.AsSpan(9, 4), 99);
        nav[13] = 1;
        _ = world.Apply(Packet.ForOpcode(0x0099, nav), 0);
        var domain = new Net7PlayerSemanticDomain(world);

        Net7NavigationSemantics view = Assert.IsType<Net7NavigationSemantics>(domain.GetNavigationSemantics(77));
        Assert.Equal(99u, view.NavType);
        Assert.False(view.IsOrdinaryRouteCandidate);
        Assert.Null(view.GetOrdinaryRouteCandidateSpatialValue());
        Assert.False(view.IsRevealed);
        Assert.True(view.UsesUnknownStarMapRepresentation);
        Assert.False(view.UsesNormalTypeSpecificStarMapRepresentation);
        Assert.True(view.IsHugeObject);
        Assert.True(view.UsesAlternateMapGraphics);
        Assert.True(view.BypassesNormalNavigationRangeGate);
        Assert.False(view.UsesNormalSignatureDistanceTargetability);
        Assert.Null(view.GetNormalRadarTargetabilityGeometry(500f));
        Assert.Equal(173.5f, view.AddSignatureToDestinationSpatialRadius(50f));

        nav[8] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(nav.AsSpan(9, 4), 1);
        nav[13] = 0;
        _ = world.Apply(Packet.ForOpcode(0x0099, nav), 0);
        view = Assert.IsType<Net7NavigationSemantics>(domain.GetNavigationSemantics(77));
        Assert.True(view.IsOrdinaryRouteCandidate);
        Assert.Equal(123.5f, view.GetOrdinaryRouteCandidateSpatialValue()!.Value);
        Assert.True(view.IsRevealed);
        Assert.False(view.UsesUnknownStarMapRepresentation);
        Assert.True(view.UsesNormalTypeSpecificStarMapRepresentation);
        Assert.False(view.IsHugeObject);
        Assert.False(view.UsesAlternateMapGraphics);
        Assert.False(view.BypassesNormalNavigationRangeGate);
        Assert.True(view.UsesNormalSignatureDistanceTargetability);
        Assert.Equal(623.5f, view.GetNormalRadarTargetabilityGeometry(500f)!.Value);
    }

    [Fact]
    public void WarpRouteSelectionSnapshotsRouteAndClearsAtBoundary()
    {
        var domain = new Net7PlayerSemanticDomain(new Net7WorldState());
        var route = new List<uint> { 10, 20, 30 };
        Assert.True(domain.SelectWarpRoute(route));
        route[0] = 999;
        Assert.Equal(new uint[] { 10, 20, 30 }, domain.RequireSelectedWarpRoute());
        domain.ResetForSectorBoundary();
        Assert.Throws<InvalidOperationException>(() => domain.RequireSelectedWarpRoute());
        Assert.False(domain.SelectWarpRoute(Array.Empty<uint>()));
    }

    [Fact]
    public void FactionStandingJoinsObjectFactionIdentifierToPlayerFactionKeyExactly()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(90, 1, 0, 0, 0, 0, 0));
        Apply(world.Objects.FindFirst(90)!.AuxData!, ("ShipAuxData.FactionIdentifier", Lp("earthcorps")));
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Reputation.Faction.3.Name", Lp("earthcorps")),
            ("LocalPlayerAuxData.Reputation.Faction.3.Reaction", F32(4.5f)),
            ("LocalPlayerAuxData.Reputation.Faction.3.Order", I32(7)));
        var domain = new Net7PlayerSemanticDomain(world);

        Net7FactionStanding standing = Assert.IsType<Net7FactionStanding>(domain.GetStandingForObject(90));
        Assert.Equal(4.5f, standing.Reaction);
        Assert.Equal(7, standing.Order);

        Apply(world.Objects.FindFirst(90)!.AuxData!, ("ShipAuxData.FactionIdentifier", Lp("EarthCorps")));
        Assert.Null(domain.GetStandingForObject(90));
    }

    [Fact]
    public void TransitionCommitUsesServerTargetForDockGateButRequestedPlanetForLand()
    {
        var domain = new Net7PlayerSemanticDomain(new Net7WorldState());
        domain.BeginTransition(Net7TransitionKind.Dock, 10);
        Assert.Equal(0u, domain.RequireCommitTarget(Net7TransitionKind.Dock));
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Dock, 20);
        Assert.Equal(20u, domain.RequireCommitTarget(Net7TransitionKind.Dock));

        domain.BeginTransition(Net7TransitionKind.Land, 30);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Land, 40);
        Assert.Equal(30u, domain.RequireCommitTarget(Net7TransitionKind.Land));

        domain.BeginTransition(Net7TransitionKind.Gate, 50);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Gate, 60);
        Assert.Equal(60u, domain.RequireCommitTarget(Net7TransitionKind.Gate));
    }

    [Fact]
    public void ServerTransitionTargetsSurviveOtherRequestsAndClearIndependently()
    {
        var domain = new Net7PlayerSemanticDomain(new Net7WorldState());

        domain.BeginTransition(Net7TransitionKind.Dock, 10);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Gate, 99);
        Assert.Equal(0u, domain.RequireCommitTarget(Net7TransitionKind.Dock));
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Dock, 20);
        Assert.Equal(20u, domain.RequireCommitTarget(Net7TransitionKind.Dock));

        domain.BeginTransition(Net7TransitionKind.Land, 30);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Dock, 40);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Land, 50);
        Assert.Equal(30u, domain.RequireCommitTarget(Net7TransitionKind.Land));

        domain.BeginTransition(Net7TransitionKind.Gate, 60);
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Dock, 70);
        Assert.Equal(99u, domain.RequireCommitTarget(Net7TransitionKind.Gate));
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Gate, 80);
        Assert.Equal(80u, domain.RequireCommitTarget(Net7TransitionKind.Gate));
        Assert.Equal(70u, domain.RequireCommitTarget(Net7TransitionKind.Dock));
        domain.CompleteTransition(Net7TransitionKind.Dock);
        Assert.Equal(uint.MaxValue, domain.RequireCommitTarget(Net7TransitionKind.Dock));
        Assert.Equal(80u, domain.RequireCommitTarget(Net7TransitionKind.Gate));
        domain.ResetForSectorBoundary();
        Assert.Equal(80u, domain.RequireCommitTarget(Net7TransitionKind.Gate));
        domain.ObserveServerTransitionTarget(Net7TransitionKind.Dock, 90);
        Assert.Equal(90u, domain.RequireCommitTarget(Net7TransitionKind.Dock));
    }

    [Fact]
    public void ActiveStarbaseFollowsStarbaseSetActionNotExitMode()
    {
        var domain = new Net7PlayerSemanticDomain(new Net7WorldState());

        // Action 0 opens/materializes even when ExitMode is nonzero; the
        // current processor ignores ExitMode on this branch.
        domain.ObserveStarbaseSet(new Net7StarbaseSetUpdate(0x11223344, 0, 2));
        Assert.Equal(0x11223344u, domain.RequireActiveStarbaseId());

        // Action 1 closes even when ExitMode is zero.
        domain.ObserveStarbaseSet(new Net7StarbaseSetUpdate(0x11223344, 1, 0));
        Assert.Throws<InvalidOperationException>(() => domain.RequireActiveStarbaseId());

        domain.ObserveStarbaseTow(new Net7StarbaseTowUpdate([
            new Net7StarbaseTowOption(0x55667788, 100),
            new Net7StarbaseTowOption(0x11111111, 200),
            new Net7StarbaseTowOption(0x22222222, 300),
        ]));
        Assert.Equal(0x55667788u, domain.RequireActiveStarbaseId());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x11223344u)]
    public void ExistingStationOwnerRejectsReplacementUntilMatchingClose(uint original)
    {
        // 7381C0/738330 -> 74B680(false): existing view skips the entire open.
        var domain = new Net7PlayerSemanticDomain(new Net7WorldState());
        domain.ObserveStarbaseSet(new(original, 0, 2));
        domain.ObserveStarbaseSet(new(99, 0, 0));
        domain.ObserveStarbaseTow(new([new(100, 200)]));
        Assert.Equal(original, domain.RequireActiveStarbaseId());
        domain.ObserveStarbaseSet(new(99, 1, 1));
        domain.ObserveStarbaseSet(new(original, 2, 0));
        Assert.Equal(original, domain.RequireActiveStarbaseId());
        domain.ObserveStarbaseSet(new(original, 1, 2));
        Assert.Null(domain.ActiveStarbaseId);
        domain.ObserveStarbaseTow(new([new(100, 200)]));
        Assert.Equal(100u, domain.RequireActiveStarbaseId());
    }

    [Fact]
    public void SceneSnapshotCarriesLocalBuffsAndRemainsDetachedAfterRemoval()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(1, 1, 0, 0, 0, 0, 0));
        world.SetLocalPlayerId(1);
        var ship = world.Objects.FindFirst(1)!.AuxData!;
        Apply(ship,("ShipAuxData.BuffArray.14.BuffType",Lp("Cloak")),
            ("ShipAuxData.BuffArray.14.IsPermanent",U8(1)),
            ("ShipAuxData.BuffArray.14.Elements.0.SourceObject",Lp("Cloak")));
        var builder = new EnbNext.Presentation.EnbPresentationSnapshotBuilder();
        var first = builder.Build(world,null,1000).Hud.Buffs;
        Assert.Equal(14,Assert.Single(first).Slot);
        Apply(ship,("ShipAuxData.BuffArray.14.BuffType",Lp("")));
        Assert.Empty(builder.Build(world,null,1001).Hud.Buffs);
        Assert.Equal("Cloak",Assert.Single(first).Type);
        Assert.Equal("Cloak",Assert.Single(first[0].Elements).SourceObject);
    }

    private static void Apply(Net7AuxDataSchema schema, params (string Path, byte[] Wire)[] values)
        => Apply(schema, values, Array.Empty<string>());

    private static void Apply(
        Net7AuxDataSchema schema,
        IReadOnlyList<(string Path, byte[] Wire)> values,
        IReadOnlyList<string> stateTwoPaths)
    {
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Count));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Require(schema, path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, checked((uint)stateTwoPaths.Count));
        foreach (string path in stateTwoPaths)
            WriteU32(delta, checked((uint)Require(schema, path).Id));
        WriteU32(delta, 0);
        new Net7AuxDataCodec().Apply(schema, delta.ToArray());
    }

    private static Net7AuxDataProperty Require(Net7AuxDataSchema schema, string path)
        => Assert.Single(schema.Properties, p => p.Path == path);

    private static byte[] U8(byte value) => [value];

    private static byte[] U32(uint value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] I32(int value)
    {
        byte[] b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    private static byte[] F32(float value) => I32(BitConverter.SingleToInt32Bits(value));

    private static byte[] Raw16(int a, int b, int c, int d)
    {
        byte[] result = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(0, 4), a);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), b);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8, 4), c);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12, 4), d);
        return result;
    }

    private static byte[] Lp(string value)
    {
        byte[] text = Encoding.Latin1.GetBytes(value);
        byte[] result = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(result, checked((ushort)text.Length));
        text.CopyTo(result, 2);
        return result;
    }

    private static void WriteU32(Stream stream, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        stream.Write(b);
    }
}
