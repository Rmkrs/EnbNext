using System.Buffers.Binary;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.LocalData;

namespace EnbNext.Net7.Tests;

public sealed class Net7GalaxyMapLocationTests
{
    public const string Captured = "040000001F000000BBA301005369726975730053776F6F70696E67204561676C65000077010000";

    [InstalledRetailFact]
    public void CapturedLocationHasSeparateChannelAndRecordCount()
    {
        var location = Net7GalaxyMapLocationWire.Decode(Convert.FromHexString(Captured));
        Assert.Equal(new(0x1A3BB, "Sirius", "Swooping Eagle", "", 375), location);
        byte[] reply = Net7GalaxyMapLocationWire.BuildPresenceReply(location, Net7LocalDataAssets.CurrentGalaxyMap);
        Assert.Equal(64, reply.Length); // 13 header/tail + 4 inner length + ceil(375/8).
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(reply));
        Assert.Equal(51u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(4)));
        Assert.Equal(47u, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(8)));
        Assert.Equal(0x1A3BBu, BinaryPrimitives.ReadUInt32LittleEndian(reply.AsSpan(59)));
        Assert.Equal(0, reply[63]);
        // Check the presence bits against the original local frame records,
        // independently of the typed model's property names.
        byte[] raw = File.ReadAllBytes(Path.Combine(EnbNext.Installation.EnbInstallDiscovery.RequireLauncher(), "database", "GalaxyMap.dat"));
        var ids = new HashSet<uint>();
        for (int offset = 0; offset < raw.Length; offset += BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset)))
            if (BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 4)) != 3)
                ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 12)));
        for (uint id = 0; id < location.RecordCount; id++)
            Assert.Equal(ids.Contains(id), (reply[12 + (int)(id >> 3)] & (1 << (int)(id & 7))) != 0);
    }

    [Fact]
    public void SparsePresenceBitsUseRecordIndexesRatherThanSectorIdentifiers()
    {
        var map = new Net7GalaxyMapData([], [new(0, 999, "", [], [], 0, 0)],
            [new(6, 7, 4120, 0, "", 6, []), new(6, 9, 4515, 0, "", 6, [])],
            [new(8, 4120, 4515), new(20, 4120, 4515)]);
        byte[] reply = Net7GalaxyMapLocationWire.BuildPresenceReply(new(0x12345678, "", "", "", 10), map);
        Assert.Equal("00000000060000000200000081037856341200", Convert.ToHexString(reply));
        byte[] empty = Net7GalaxyMapLocationWire.BuildPresenceReply(new(7, "", "", "", 0), map);
        Assert.Equal("0000000004000000000000000700000000", Convert.ToHexString(empty));
    }

    [Fact]
    public void SmallerLocationCountKeepsHigherSlots()
    {
        var map = new Net7GalaxyMapData([], [new(0, 999, "", [], [], 0, 0)],
            [new(6, 7, 4120, 0, "", 6, []), new(6, 9, 4515, 0, "", 6, [])],
            [new(8, 4120, 4515), new(20, 4120, 4515)]);
        // 008365C0 raw 00836604/00836606: a smaller count is stored, AllData is not shrunk (MAP-D01).
        var resized = Net7GalaxyMapUpdates.ResizeAllData(map, 8);
        Assert.Equal(8u, resized.RecordCount);
        Assert.Equal(Net7GalaxyMapUpdates.ResizeAllData(map, 30).AllData!.Keys.Order().ToArray(), resized.AllData!.Keys.Order().ToArray());
        Assert.Contains(20u, resized.AllData!.Keys);
        Assert.Equal(map.Gates, resized.Gates);
        Assert.Equal(resized.AllData!.Keys.Order().ToArray(), Net7GalaxyMapUpdates.ResizeAllData(resized, 21).AllData!.Keys.Order().ToArray());
        // A later 00836B70 removal of a slot above the count still finds its owner.
        byte[] remove = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(remove.AsSpan(4), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(remove.AsSpan(8), 20);
        Assert.Single(Net7GalaxyMapUpdates.Apply(resized, remove).Gates);
    }

    [InstalledRetailFact]
    public void TruncationsAreRejectedAndOversizedCountsSendNoReply()
    {
        byte[] packet = Convert.FromHexString(Captured);
        // 00870FD0 initializes Command to -1: fewer than four bytes is not command 4
        // and reaches 0073AC60's default arm as an unchanged map.
        for (int length = 0; length < 4; length++)
            Assert.NotEqual(4u, Net7GalaxyMapUpdates.Command(packet.AsSpan(0, length), out _));
        for (int length = 4; length < packet.Length; length++)
            Assert.Throws<InvalidDataException>(() => Net7GalaxyMapLocationWire.Decode(packet.AsSpan(0, length)));
        // Bytes beyond the declared view must not satisfy its missing count.
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 27);
        Assert.Throws<InvalidDataException>(() => Net7GalaxyMapLocationWire.Decode(packet));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 31);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(packet.Length - 4), uint.MaxValue);
        // 008365C0 stores any count; only the presence datagram cannot carry it.
        var oversized = Net7GalaxyMapLocationWire.Decode(packet);
        Assert.Equal(uint.MaxValue, oversized.RecordCount);
        Assert.Throws<InvalidDataException>(() => Net7GalaxyMapLocationWire.BuildPresenceReply(oversized, Net7LocalDataAssets.CurrentGalaxyMap));
        packet.AsSpan(12).Fill(0x41);
        Assert.Throws<InvalidDataException>(() => Net7GalaxyMapLocationWire.Decode(packet));
    }
}
