using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Social;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7DirectAuditCrossCategoryIntegrationTests
{
    private const uint LabId = 0x55667788;

    [Fact]
    public void SharedOpcodeOwnershipKeepsCrossCategoryMeaningsExplicit()
    {
        Assert.Equal(
            new[] { "INV-04", "INV-05", "ECON-01", "ECON-02" },
            Net7GameplayProvenance.GetOwners(0x0027).ToArray());
        Assert.Equal(
            new[] { "MOVE-07", "MOVE-08", "MOVE-11", "ECON-02", "SOCIAL-05" },
            Net7GameplayProvenance.GetOwners(0x002C).ToArray());
        Assert.Equal(
            new[] { "INV-05", "MISSION-01", "FULL-06" },
            Net7GameplayProvenance.GetOwners(0x0065).ToArray());

        Dictionary<ushort, string> manufacturing = new()
        {
            [0x0079] = "MFG-01",
            [0x007A] = "MFG-02",
            [0x007B] = "MFG-03",
            [0x007C] = "MFG-04",
            [0x007E] = "MFG-05",
            [0x007F] = "MFG-06",
            [0x0080] = "MFG-07",
        };
        foreach ((ushort opcode, string owner) in manufacturing)
            Assert.Equal(new[] { owner }, Net7GameplayProvenance.GetOwners(opcode).ToArray());

        // 0x007D is factory-only residue with no supported current
        // direction. It must not acquire an outbound owner or integrated codec.
        Assert.Empty(Net7GameplayProvenance.GetOwners(0x007D));

        // This is the one local-data service with a server opcode owner.
        // GalaxyMap and presentation catalogs remain local semantic assets and
        // must not acquire synthetic gameplay opcode ownership.
        Assert.Equal(
            new[] { "ASSET-01", "WORLD-12" },
            Net7GameplayProvenance.GetOwners(0x2025).ToArray());
    }

    [InstalledRetailFact]
    public void LocalAssetReadsAndPresentationResolutionDoNotMutateAuthoritativeGameplayState()
    {
        var gameplay = new Net7GameplayState();
        gameplay.SetLocalSelectedTarget(0x11111111);
        Assert.True(gameplay.TradeDomain.ApplyCoordinator(new Net7TradeState(0x22222222, 0)));
        gameplay.ManufacturingDomain.SetActiveObject(0x33333333);

        var world = new Net7WorldState();
        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.Money", U64(424242)));
        Net7WorldObject objectBefore = world.MaterializeCreate(new Net7CreateUpdate(
            0x44444444, 1f, 77, 0x04, 0, 0, 0));

        // Reading pinned local data and resolving presentation metadata are
        // deliberately local operations. They must not become authors of
        // server-owned target/trade/MFG/AuxData/object identity state.
        Assert.NotEmpty(Net7LocalDataAssets.CurrentGalaxyMap.Systems);
        Assert.True(Net7LocalDataAssets.CurrentCData.RecordCount > 0);

        var presentation = new Net7PresentationCatalogs();
        presentation.LoadBaseAssetsText("""
            [BASE-77]
            RSLID=707
            """);
        Net7BaseAssetDefinition localDescriptor = presentation.ResolveBaseAsset(77);
        Assert.Equal(Net7BaseAssetDefinitionKind.Rsl, localDescriptor.Kind);

        var player = new Net7PlayerSemanticDomain(world);
        Assert.Equal(424242UL, player.GetAuthoritativeMoney());
        Assert.Equal(0x11111111u, gameplay.LocalSelectedTargetId);
        Assert.Equal(0x22222222u, gameplay.TradeDomain.ActivePartnerId);
        Assert.Equal(0x33333333u, gameplay.ManufacturingDomain.ActiveObjectId);

        Net7WorldObject objectAfter = Assert.IsType<Net7WorldObject>(world.Objects.FindFirst(0x44444444));
        Assert.Same(objectBefore, objectAfter);
        Assert.Equal(0x44444444u, objectAfter.GameId);
        Assert.Equal(77, objectAfter.BaseAsset);
    }

    [Fact]
    public void ClientLocalTargetTradePartnerAndManufacturingObjectRemainIndependent()
    {
        var gameplay = new Net7GameplayState();

        gameplay.SetLocalSelectedTarget(0x11111111);
        Assert.True(gameplay.TradeDomain.ApplyCoordinator(new Net7TradeState(0x22222222, 0)));
        gameplay.ManufacturingDomain.SetActiveObject(0x33333333);

        Assert.Equal(0x11111111u, gameplay.LocalSelectedTargetId);
        Assert.Equal(0x22222222u, gameplay.TradeDomain.ActivePartnerId);
        Assert.Equal(0x33333333u, gameplay.ManufacturingDomain.ActiveObjectId);

        gameplay.ResetForServerHandoff();
        Assert.Null(gameplay.LocalSelectedTargetId);
        Assert.Null(gameplay.TradeDomain.ActivePartnerId);
        Assert.Equal(0u, gameplay.ManufacturingDomain.ActiveObjectId);
    }

    [Fact]
    public void LocalPlayerAndManufacturingObjectAuxDataRemainSeparateAuthorities()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        Net7AuxDataSchema labSchema = world.Objects.FindFirst(LabId)!.AuxData!;

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Money", U64(123456789)),
            ("LocalPlayerAuxData.Missions.0.Name", Lp("Cross-category mission")),
            ("LocalPlayerAuxData.Missions.0.Stage", I32(1)),
            ("LocalPlayerAuxData.Missions.0.Stages.0.Text", Lp("Stage one")));
        Apply(labSchema,
            ("ManufacturingLab.KnownFormula.0.ItemName", Lp("Widget")),
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(7001)),
            ("ManufacturingLab.KnownFormula.0.TechLevel", I32(5)),
            ("ManufacturingLab.NegotiatedCost", U64(4444)));

        var player = new Net7PlayerSemanticDomain(world);
        var manufacturing = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        manufacturing.SetActiveObject(LabId);

        Assert.Equal(123456789UL, player.GetAuthoritativeMoney());
        Assert.Equal("Stage one", player.GetMission(0, 0).CurrentStageText);
        Assert.Equal(4444UL, manufacturing.CurrentLab!.NegotiatedCost);
        Assert.True(manufacturing.CurrentLab.IsMapped(7001));

        // Object-targeted MFG state must not disturb player ECON/MISSION state.
        Apply(labSchema, ("ManufacturingLab.NegotiatedCost", U64(9999)));
        Assert.Equal(9999UL, manufacturing.CurrentLab!.NegotiatedCost);
        Assert.Equal(123456789UL, player.GetAuthoritativeMoney());
        Assert.Equal("Stage one", player.GetMission(0, 0).CurrentStageText);

        // Player-targeted state must not rewrite the active ManufacturingLab.
        Apply(world.LocalPlayerAuxData, ("LocalPlayerAuxData.Money", U64(987654321)));
        Assert.Equal(987654321UL, player.GetAuthoritativeMoney());
        Assert.Equal(9999UL, manufacturing.CurrentLab!.NegotiatedCost);
        Assert.True(manufacturing.CurrentLab.IsMapped(7001));
    }

    [Fact]
    public async Task SharedActionOpcodeConsumesAllRequestSubtypes()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        var delivered = new List<Net7PlayerAction>();
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x002C)
                delivered.Add((Net7PlayerAction)Net7GameplayWire.DecodeAction(packet.Payload.Span).Action);
        };

        await DeliverCore(session, Packet.ForOpcode(
            0x002C,
            Net7GameplayWire.BuildAction(1, Net7PlayerAction.TradeRequest, 2, 0)));
        await DeliverCore(session, Packet.ForOpcode(
            0x002C,
            Net7GameplayWire.BuildAction(1, Net7PlayerAction.FireAllWeapons, 2, 0)));

        Assert.Empty(delivered);
    }

    [Fact]
    public async Task SharedUiTriggerRemainsOutboundOnlyWhileActiveManufacturingObjectRemainsInboundState()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        int uiTriggerDeliveries = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x0065)
                uiTriggerDeliveries++;
        };

        await DeliverCore(session, Packet.ForOpcode(0x0065, Net7GameplayWire.BuildUiTrigger(0x11223344, 8)));
        Assert.Equal(0, uiTriggerDeliveries);

        await DeliverCore(session, Packet.ForOpcode(0x007F, Convert.FromHexString("55667788")));
        Assert.Equal(LabId, session.Gameplay.ManufacturingDomain.ActiveObjectId);
    }


    [Fact]
    public void SocialOwnershipKeepsSharedOpcodesAndAdjacentDomainsExplicit()
    {
        Assert.Equal(new[] { "SOCIAL-03", "FULL-06" }, Net7GameplayProvenance.GetOwners(0x001D).ToArray());
        Assert.Equal(new[] { "SOCIAL-05" }, Net7GameplayProvenance.GetOwners(0x001E).ToArray());
        Assert.Equal(new[] { "ECON-02" }, Net7GameplayProvenance.GetOwners(0x001F).ToArray());
        Assert.Equal(
            new[] { "MOVE-07", "MOVE-08", "MOVE-11", "ECON-02", "SOCIAL-05" },
            Net7GameplayProvenance.GetOwners(0x002C).ToArray());
        Assert.Equal(new[] { "SOCIAL-01" }, Net7GameplayProvenance.GetOwners(0x0033).ToArray());
        Assert.Equal(new[] { "SOCIAL-07" }, Net7GameplayProvenance.GetOwners(0x0053).ToArray());
        Assert.Equal(
            new[] { "SOCIAL-02", "SOCIAL-04", "SOCIAL-07", "ENTRY-09" },
            Net7GameplayProvenance.GetOwners(0x00A3).ToArray());
        Assert.Equal(new[] { "SOCIAL-05" }, Net7GameplayProvenance.GetOwners(0x00BC).ToArray());
        Assert.Equal(new[] { "SOCIAL-05" }, Net7GameplayProvenance.GetOwners(0x00BD).ToArray());
        Assert.Equal(new[] { "SOCIAL-06" }, Net7GameplayProvenance.GetOwners(0x00D0).ToArray());
    }

    [Fact]
    public void SocialGroupGuildInventoryMissionEconomyAndManufacturingAuthoritiesStayOrthogonal()
    {
        const uint playerId = 0x10203040;
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(playerId, 1f, 0, 0, 0, 0, 0));
        world.MaterializeCreate(new Net7CreateUpdate(LabId, 1f, 0, 0x27, 0, 0, 0));
        world.SetLocalPlayerId(playerId);

        Net7AuxDataSchema ship = world.Objects.FindFirst(playerId)!.AuxData!;
        Net7AuxDataSchema lab = world.Objects.FindFirst(LabId)!.AuxData!;
        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Money", U64(123456)),
            ("LocalPlayerAuxData.SecureInventory.0.StackCount", I32(3)),
            ("LocalPlayerAuxData.Missions.0.Name", Lp("Social seam mission")),
            ("LocalPlayerAuxData.Missions.0.Stage", I32(1)),
            ("LocalPlayerAuxData.Missions.0.Stages.0.Text", Lp("Still independent")),
            ("LocalPlayerAuxData.GroupInfo.IsGroupLeader", U8(true)),
            ("LocalPlayerAuxData.GroupInfo.Members.0.Name", Lp("Wingmate")),
            ("LocalPlayerAuxData.GroupInfo.Members.0.GameID", I32(0x01020304)));
        Apply(ship,
            ("ShipAuxData.GuildName", Lp("Builders")),
            ("ShipAuxData.GuildRank", I32(0)),
            ("ShipAuxData.GuildRankName", Lp("Leader")));
        Apply(lab,
            ("ManufacturingLab.NegotiatedCost", U64(4444)),
            ("ManufacturingLab.KnownFormula.0.ItemName", Lp("Widget")),
            ("ManufacturingLab.KnownFormula.0.ItemID", I32(7001)),
            ("ManufacturingLab.KnownFormula.0.TechLevel", I32(5)));

        var player = new Net7PlayerSemanticDomain(world);
        var manufacturing = new Net7ManufacturingDomain(id => world.Objects.FindFirst(id));
        manufacturing.SetActiveObject(LabId);

        Assert.Equal(123456UL, player.GetAuthoritativeMoney());
        Assert.Equal(3, Assert.IsType<int>(Assert.Single(
            world.LocalPlayerAuxData.Properties,
            p => p.Path == "LocalPlayerAuxData.SecureInventory.0.StackCount").Value));
        Assert.Equal("Still independent", player.GetMission(0, 0).CurrentStageText);
        Assert.True(player.GetGroupState().IsInGroup);
        Assert.True(player.GetGroupState().IsGroupLeader);
        Assert.True(player.GetGuildMembership().IsInGuild);
        Assert.Equal("Builders", player.GetGuildMembership().GuildName);
        Assert.Equal(4444UL, manufacturing.CurrentLab!.NegotiatedCost);
        Assert.True(manufacturing.CurrentLab.IsMapped(7001));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.GroupInfo.IsGroupLeader", U8(false)),
            ("LocalPlayerAuxData.GroupInfo.Members.0.Name", Lp("Wingmate2")));
        Apply(ship,
            ("ShipAuxData.GuildName", Lp("Renamed")),
            ("ShipAuxData.GuildRankName", Lp("Officer")));

        Assert.False(player.GetGroupState().IsGroupLeader);
        Assert.Equal("Wingmate2", player.GetGroupState().Members[0].Name);
        Assert.Equal("Renamed", player.GetGuildMembership().GuildName);
        Assert.Equal("Officer", player.GetGuildMembership().GuildRankName);
        Assert.Equal(123456UL, player.GetAuthoritativeMoney());
        Assert.Equal(3, Assert.IsType<int>(Assert.Single(
            world.LocalPlayerAuxData.Properties,
            p => p.Path == "LocalPlayerAuxData.SecureInventory.0.StackCount").Value));
        Assert.Equal("Still independent", player.GetMission(0, 0).CurrentStageText);
        Assert.Equal(4444UL, manufacturing.CurrentLab!.NegotiatedCost);
        Assert.True(manufacturing.CurrentLab.IsMapped(7001));

        Apply(world.LocalPlayerAuxData,
            ("LocalPlayerAuxData.Money", U64(654321)),
            ("LocalPlayerAuxData.SecureInventory.0.StackCount", I32(9)));
        Apply(lab, ("ManufacturingLab.NegotiatedCost", U64(9999)));

        Assert.Equal(654321UL, player.GetAuthoritativeMoney());
        Assert.Equal(9, Assert.IsType<int>(Assert.Single(
            world.LocalPlayerAuxData.Properties,
            p => p.Path == "LocalPlayerAuxData.SecureInventory.0.StackCount").Value));
        Assert.True(player.GetGroupState().IsInGroup);
        Assert.False(player.GetGroupState().IsGroupLeader);
        Assert.True(player.GetGuildMembership().IsInGuild);
        Assert.Equal("Renamed", player.GetGuildMembership().GuildName);
        Assert.Equal(9999UL, manufacturing.CurrentLab!.NegotiatedCost);
    }

    [Fact]
    public void SocialTargetRecruitmentAndTradeKeepSeparateClientAndServerAuthorities()
    {
        var gameplay = new Net7GameplayState();
        gameplay.SetLocalSelectedTarget(0x11111111);
        Assert.True(gameplay.TradeDomain.ApplyCoordinator(new Net7TradeState(0x22222222, 0)));
        gameplay.Social.ApplyFindMember([
            new Net7FindMemberRecord(0x33333333, 7, 0, 0),
        ]);

        Net7GroupRequestPlan invite = Net7SocialGroupPolicy.PlanTargetAction(
            0x44444444,
            gameplay.LocalSelectedTargetId,
            Net7PlayerAction.GroupInvite);
        Assert.True(invite.ShouldSend);
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32LittleEndian(invite.Payload.AsSpan(8, 4)));
        Assert.Equal(0x11111111u, gameplay.LocalSelectedTargetId);
        Assert.Equal(0x22222222u, gameplay.TradeDomain.ActivePartnerId);
        Assert.Equal(0x33333333u, Assert.Single(gameplay.Social.GroupFinderCandidates).ObjectId);

        Assert.True(gameplay.TradeDomain.ApplyCoordinator(new Net7TradeState(0x55555555, 0)));
        Assert.Equal(0x11111111u, gameplay.LocalSelectedTargetId);
        Assert.Equal(0x55555555u, gameplay.TradeDomain.ActivePartnerId);
        Assert.Equal(0x33333333u, Assert.Single(gameplay.Social.GroupFinderCandidates).ObjectId);
    }

    [Fact]
    public void CharacterSelectionResetPreservesPresencePreferenceButClearsPerCharacterCrossDomainState()
    {
        var gameplay = new Net7GameplayState();
        Assert.Equal(28u, gameplay.Social.SelectPresenceRequestCode(true));
        gameplay.SetLocalSelectedTarget(0x11111111);
        Assert.True(gameplay.TradeDomain.ApplyCoordinator(new Net7TradeState(0x22222222, 0)));
        gameplay.Social.ApplyFindMember([new Net7FindMemberRecord(0x33333333, 7, 0, 0)]);
        gameplay.Social.ApplyGroupAction(new Net7GroupActionUpdate(1, [(byte)'P', 0]) { InviteText = "P" });

        gameplay.ResetForCharacterSelection();

        Assert.True(gameplay.Social.RestrictedStatus);
        Assert.Null(gameplay.LocalSelectedTargetId);
        Assert.Null(gameplay.TradeDomain.ActivePartnerId);
        Assert.Empty(gameplay.Social.GroupFinderCandidates);
        Assert.Null(gameplay.Social.GroupInvite);
    }

    private static async Task DeliverCore(Net7Session session, Packet packet)
    {
        MethodInfo method = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(Net7Session), "DeliverGameplayPacketCoreAsync");
        await ((Task)(method.Invoke(session, [packet, "cross-category-integration", false, CancellationToken.None])
            ?? throw new InvalidOperationException("DeliverGameplayPacketCoreAsync returned null."))).ConfigureAwait(false);
    }

    private static void Apply(Net7AuxDataSchema schema, params (string Path, byte[] Wire)[] values)
    {
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((string path, byte[] wire) in values)
        {
            Net7AuxDataProperty property = Assert.Single(schema.Properties, p => p.Path == path);
            WriteU32(delta, checked((uint)property.Id));
            delta.Write(wire);
        }
        WriteU32(delta, 0);
        WriteU32(delta, 0);
        new Net7AuxDataCodec().Apply(schema, delta.ToArray());
    }

    private static byte[] I32(int value)
    {
        byte[] result = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(result, value);
        return result;
    }

    private static byte[] U8(bool value) => [value ? (byte)1 : (byte)0];

    private static byte[] U64(ulong value)
    {
        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(result, value);
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
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }
}
