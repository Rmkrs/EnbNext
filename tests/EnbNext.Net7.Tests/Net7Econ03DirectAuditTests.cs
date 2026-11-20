using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using EnbNext.Net7;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Econ03DirectAuditTests
{
    [Fact]
    public void EconomyAndFactionPropertyIdsMatchCurrentSchemaExactly()
    {
        var catalog = new Net7AuxDataSchemaCatalog();
        Net7AuxDataSchema local = catalog.LocalPlayer;
        Net7AuxDataSchema ship = catalog.Ship;

        AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerMoney,
            "LocalPlayerAuxData.Money", Net7AuxDataValueKind.UInt64LittleEndian);

        AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationRoot,
            "LocalPlayerAuxData.Reputation", Net7AuxDataValueKind.Aggregate);
        AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationFactionAggregate,
            "LocalPlayerAuxData.Reputation.Faction", Net7AuxDataValueKind.Aggregate);

        for (int i = 0; i < Net7AuxDataPropertyIds.LocalPlayerReputationFactionCount; i++)
        {
            int offset = i * Net7AuxDataPropertyIds.LocalPlayerReputationFactionStride;
            AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Root + offset,
                $"LocalPlayerAuxData.Reputation.Faction.{i}", Net7AuxDataValueKind.Aggregate);
            AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Name + offset,
                $"LocalPlayerAuxData.Reputation.Faction.{i}.Name", Net7AuxDataValueKind.LengthPrefixedString);
            AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction + offset,
                $"LocalPlayerAuxData.Reputation.Faction.{i}.Reaction", Net7AuxDataValueKind.Float32LittleEndian);
            AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Order + offset,
                $"LocalPlayerAuxData.Reputation.Faction.{i}.Order", Net7AuxDataValueKind.Int32LittleEndian);
        }

        AssertProperty(local, Net7AuxDataPropertyIds.LocalPlayerReputationAffiliation,
            "LocalPlayerAuxData.Reputation.Affiliation", Net7AuxDataValueKind.LengthPrefixedString);
        AssertProperty(ship, Net7AuxDataPropertyIds.ShipFactionIdentifier,
            "ShipAuxData.FactionIdentifier", Net7AuxDataValueKind.LengthPrefixedString);
    }

    [Fact]
    public void SemanticDomainKeepsMoneyAffiliationStandingsAndObjectFactionSeparate()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(90, 1, 0, 0, 0, 0, 0));
        Net7AuxDataSchema ship = world.Objects.FindFirst(90)!.AuxData!;

        Apply(world.LocalPlayerAuxData,
            (Net7AuxDataPropertyIds.LocalPlayerMoney, U64(0x1122334455667788)),
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Name, Lp("EarthCorps")),
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction, F32(4.5f)),
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Order, I32(7)),
            (Net7AuxDataPropertyIds.LocalPlayerReputationAffiliation, Lp("Progen")));
        Apply(ship, (Net7AuxDataPropertyIds.ShipFactionIdentifier, Lp("EarthCorps")));

        var domain = new Net7PlayerSemanticDomain(world);
        Assert.Equal(0x1122334455667788UL, domain.GetAuthoritativeMoney());
        Assert.Equal("Progen", domain.GetAffiliation());
        Net7FactionStanding only = Assert.Single(domain.GetFactionStandings());
        Assert.Equal("EarthCorps", only.FactionKey);
        Assert.Equal(4.5f, only.Reaction);
        Assert.Equal(7, only.Order);
        Assert.Equal(only, Assert.IsType<Net7FactionStanding>(domain.GetStandingForObject(90)));

        // Exact key identity: player Affiliation is not the object faction key,
        // and a differently-cased object key does not alias the standing entry.
        Apply(ship, (Net7AuxDataPropertyIds.ShipFactionIdentifier, Lp("earthcorps")));
        Assert.Null(domain.GetStandingForObject(90));
        Assert.Equal("Progen", domain.GetAffiliation());
    }

    [Fact]
    public void RelationshipBatchCannotMutatePlayerReputationStanding()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(90, 1, 0, 0, 0, 0, 0));
        Apply(world.LocalPlayerAuxData,
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Name, Lp("EarthCorps")),
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction, F32(4.5f)),
            (Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Order, I32(7)));

        byte[] payload = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), 90);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), (uint)Net7Reaction.Shun);
        Assert.True(world.Apply(Packet.ForOpcode(0x008A, payload), 0).PacketMaterialized);

        Assert.Equal((uint)Net7Reaction.Shun, world.Objects.FindFirst(90)!.RelationshipRaw);
        Assert.Equal(4.5f, Assert.IsType<float>(world.LocalPlayerAuxData.Resolve(
            Net7AuxDataPropertyIds.LocalPlayerReputationFaction0Reaction).Value));
    }

    [Fact]
    public async Task DormantGuildDeductFeeCannotMasqueradeAsMoneyMaterialization()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        Apply(session.World.LocalPlayerAuxData,
            (Net7AuxDataPropertyIds.LocalPlayerMoney, U64(123456789)));

        int delivered = 0;
        session.GameplayPacketReceived += packet =>
        {
            if (packet.Header.Opcode == 0x00DC)
                delivered++;
        };

        MethodInfo dispatch = typeof(Net7Session).GetMethod(
            "DeliverGameplayPacketCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)dispatch.Invoke(session,
            [Packet.ForOpcode(0x00DC, Convert.FromHexString("0102030405060708")), "decoded-inner", false, CancellationToken.None])!;
        await task;

        Assert.Equal(0, delivered);
        Assert.Equal(123456789UL, session.PlayerSemantics.GetAuthoritativeMoney());
        Assert.False(session.Gameplay.Apply(Packet.ForOpcode(0x00DC, Array.Empty<byte>())));
    }


    private static void AssertProperty(
        Net7AuxDataSchema schema,
        int propertyId,
        string expectedPath,
        Net7AuxDataValueKind expectedKind)
    {
        Net7AuxDataProperty property = schema.Resolve(propertyId);
        Assert.Equal(expectedPath, property.Path);
        Assert.Equal(expectedKind, property.Kind);
    }

    private static void Apply(Net7AuxDataSchema schema, params (int PropertyId, byte[] Wire)[] values)
    {
        using var delta = new MemoryStream();
        delta.WriteByte(0);
        WriteU32(delta, checked((uint)values.Length));
        foreach ((int propertyId, byte[] wire) in values)
        {
            WriteU32(delta, checked((uint)propertyId));
            delta.Write(wire);
        }
        WriteU32(delta, 0);
        WriteU32(delta, 0);
        new Net7AuxDataCodec().Apply(schema, delta.ToArray());
    }

    private static byte[] U64(ulong value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] I32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] F32(float value) => I32(BitConverter.SingleToInt32Bits(value));

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
