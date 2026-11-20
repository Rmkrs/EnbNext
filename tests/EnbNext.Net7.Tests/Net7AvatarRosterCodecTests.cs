using System.Buffers.Binary;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7AvatarRosterCodecTests
{
    [Fact]
    public void CurrentFixedEmptySkeletonGeometry_DecodesFiveSlotsAndOneDeclaredRoutingRecord()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster();
        Assert.Equal(Net7AvatarRosterCodec.CurrentFixedPayloadSize, payload.Length);

        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(payload);

        Assert.Equal(5, roster.Slots.Count);
        Assert.Single(roster.RoutingRecords);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, roster.RoutingHeaderRaw);
        Assert.False(roster.Slots[0].IsEmpty);
        Assert.True(roster.Slots[1].IsEmpty);
        Net7AvatarSlotMeta meta = roster.Slots[0].Meta;
        Assert.Equal(0u, meta.Slot);
        Assert.Equal(1079u, meta.Sector);
        Assert.Equal(7u, meta.ShardId);
        Assert.Equal(12u, meta.SaveCount);
        Assert.Equal(0x0102030405060708UL, meta.AvatarId);
        Assert.Equal(0x1112131415161718UL, meta.AccountId);
        Assert.Equal(2u, meta.AdminLevel);
        Assert.Equal(3u, meta.GmFlag);
        Assert.Equal(10u, meta.CombatLevel);
        Assert.Equal(11u, meta.ExploreLevel);
        Assert.Equal(12u, meta.TradeLevel);
        Assert.Equal("Test Sector", meta.SectorName);
        Assert.Equal("Kestralion", roster.Slots[0].Avatar.FirstName);
        Assert.Equal(0xAB, roster.Slots[0].Avatar.UnknownU8);
        Assert.Equal(1.25f, roster.Slots[0].Avatar.TattooOffset[0]);
        Assert.Equal(-2.5f, roster.Slots[0].Avatar.TattooOffset[1]);
        Assert.Equal(3.75f, roster.Slots[0].Avatar.TattooOffset[2]);
        Assert.Equal(100u, roster.Slots[0].Avatar.BodyWeight[0]);
        Assert.Equal(204u, roster.Slots[0].Avatar.HeadWeight[4]);
        Assert.Equal((ushort)3802, roster.RoutingRecords[0].RoutingPortRaw);
    }

    [Fact]
    public void EmptySkeletonFixedGeometry_PlacesRoute0At0752AndLegacyPortAt079AWithoutMutation()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster(routePortRaw: 4242);

        Assert.Equal(0x752, 5 * (Net7AvatarRosterCodec.SlotMetaSize + Net7AvatarRosterCodec.AvatarDataBaseSize) + Net7AvatarRosterCodec.RoutingHeaderSize);
        Assert.Equal(4242, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0x79A, 2)));

        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(payload);
        Assert.Equal((ushort)4242, roster.RoutingRecords[0].RoutingPortRaw);
        Assert.Equal(3u, roster.RoutingRecords[0].CurrentLoad);
        Assert.Equal(100u, roster.RoutingRecords[0].Capacity);
    }

    [Fact]
    public void NonEmptySkeleton_ExtendsAvatarDataAndShiftsFollowingGeometry()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster(slot0Skeleton: "HUMAN_MALE");
        Assert.Equal(Net7AvatarRosterCodec.CurrentFixedPayloadSize + "HUMAN_MALE".Length, payload.Length);

        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(payload);

        Assert.Equal("HUMAN_MALE", roster.Slots[0].Avatar.Skeleton);
        Assert.Equal("", roster.Slots[1].Avatar.Skeleton);
        Assert.Equal("Andromeda", roster.RoutingRecords[0].GalaxyName);
        Assert.Equal(104u, roster.Slots[0].Avatar.BodyWeight[4]);
        Assert.Equal(200u, roster.Slots[0].Avatar.HeadWeight[0]);
    }

    [Fact]
    public void NegativeRoutingCountProducesNoRoutes()
    {
        byte[] rawHeader = [0xDE, 0xAD, 0xBE, 0xEF];
        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(
            TestProtocolFixtures.BuildRoster(routingHeader: rawHeader));

        Assert.Equal(rawHeader, roster.RoutingHeaderRaw);
        Assert.Empty(roster.RoutingRecords);
    }

    [Fact]
    public void Fixed07FaExtent_WithDynamicSkeletonIgnoresUnusedTail()
    {
        byte[] dynamic = TestProtocolFixtures.BuildRoster(slot0Skeleton: "SKELETON");
        byte[] forcedFixedExtent = dynamic[..Net7AvatarRosterCodec.CurrentFixedPayloadSize];

        Assert.Equal("Andromeda", Assert.Single(Net7AvatarRosterCodec.Decode(forcedFixedExtent).RoutingRecords).GalaxyName);
    }

    [Fact]
    public void UnusedRoutingTailNeedNotBeAligned()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster();
        Assert.Single(Net7AvatarRosterCodec.Decode(payload.AsSpan(0, payload.Length - 1)).RoutingRecords);
    }

    [Fact]
    public void RoutingCountControlsSelectionAndShortRecordsRetainPreviousFields()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster(routingHeader: [0, 0, 0, 2]);
        const int firstRoute = 0x752;
        // A second route for the same shard overrides the address while all
        // fields after this short prefix retain the first route's values.
        byte[] shortRoutes = payload[..(firstRoute + 84 + 72)];
        payload.AsSpan(firstRoute, 84).CopyTo(payload.AsSpan(firstRoute + 84));
        payload.AsSpan(firstRoute + 84, 72).CopyTo(shortRoutes.AsSpan(firstRoute + 84));
        BinaryPrimitives.WriteUInt32LittleEndian(shortRoutes.AsSpan(firstRoute + 84 + 68), 0x01020304);
        Net7AvatarRoster roster = Net7AvatarRosterCodec.Decode(shortRoutes);
        Assert.Equal(2, roster.RoutingRecords.Count);
        Assert.Equal(roster.RoutingRecords[0].RoutingPortRaw, roster.RoutingRecords[1].RoutingPortRaw);
        Assert.Equal(0x01020304u, roster.ResolveUsableRouteForSlot(0).MasterIpv4Raw);
        shortRoutes[0x751] = 1;
        roster = Net7AvatarRosterCodec.Decode(shortRoutes);
        Assert.Single(roster.RoutingRecords);
        Assert.NotEqual(0x01020304u, roster.ResolveUsableRouteForSlot(0).MasterIpv4Raw);
    }

    [Fact]
    public void AvatarStringAndWeightsKeepDecodedPrefixOnShortRead()
    {
        byte[] payload = TestProtocolFixtures.BuildRoster();
        int start = Net7AvatarRosterCodec.SlotMetaSize;
        byte[] avatar = payload.AsSpan(start, Net7AvatarRosterCodec.AvatarDataBaseSize).ToArray();
        avatar[Net7AvatarRosterCodec.AvatarSkeletonOffset] = (byte)'A';
        var unterminated = Net7AvatarRosterCodec.DecodeAvatarData(avatar.AsSpan(0, 201));
        Assert.Equal("A", unterminated.Avatar.Skeleton);
        Assert.Equal(201, unterminated.Consumed);
        Assert.Equal(new uint[] { 0x3f800000, 0, 0, 0, 0 }, unterminated.Avatar.BodyWeight);
        avatar[200] = 0;
        avatar[201] = 0x12;
        avatar[202] = 0x34;
        var partial = Net7AvatarRosterCodec.DecodeAvatarData(avatar.AsSpan(0, 203));
        Assert.Equal(0x3f803412u, partial.Avatar.BodyWeight[0]);
        Assert.Equal(0x3f800000u, partial.Avatar.HeadWeight[0]);
        Assert.Equal(203, partial.Consumed);
        Assert.Equal("Kestralion", partial.Avatar.FirstName);
    }

    [Fact]
    public void RouteResolution_UsesMatchingShardRegardlessOfGalaxyListGate()
    {
        Net7AvatarRoster good = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster());
        Net7GalaxyRoutingRecord route = good.ResolveUsableRouteForSlot(0);
        Assert.Equal(7u, route.ShardId);

        Net7AvatarRoster disabled = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster(routeGate: 0));
        Assert.Equal(route.MasterIpv4Raw, disabled.ResolveUsableRouteForSlot(0).MasterIpv4Raw);

        Net7AvatarRoster wrongShard = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster(routeShard: 8));
        Assert.Throws<InvalidOperationException>(() => wrongShard.ResolveUsableRouteForSlot(0));

        Net7AvatarRoster zeroAddress = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster(routeMasterIpv4Raw: 0));
        Assert.Throws<InvalidOperationException>(() => zeroAddress.ResolveUsableRouteForSlot(0));
        // 00769400 shows its unavailable-galaxy notice for both; the probe sends nothing.
        Assert.Null(wrongShard.TryResolveUsableRouteForSlot(0));
        Assert.Null(zeroAddress.TryResolveUsableRouteForSlot(0));

        Net7AvatarRoster empty = Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster(slot0Occupied: false));
        Assert.Throws<InvalidOperationException>(() => empty.GetSelectableSlot(0));
    }
}
