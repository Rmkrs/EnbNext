using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class Net7GalaxyMapUpdatesTests
{
    internal static byte[] Packet(uint command, byte[] blob)
    {
        byte[] packet = new byte[8 + blob.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet, command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)blob.Length);
        blob.CopyTo(packet, 8);
        return packet;
    }

    [Fact]
    public void LocalFramesUseTheSameAdmissionAsIncomingUpdates()
    {
        using var record = new MemoryStream();
        using var writer = new BinaryWriter(record, Encoding.Latin1, true);
        writer.Write(3u); writer.Write(100u); writer.Write(999u);
        writer.Write(Encoding.Latin1.GetBytes("Sector\0"));
        writer.Write(6u); writer.Write(1f); writer.Write(2f); writer.Write(3f);
        byte[] sector = Packet(11, [..record.ToArray(), 0xAA]);
        byte[][] packets = [Packet(3, [1]), sector, sector, Packet(100, []), Packet(3, [2]),
            Packet(9, [7,0,0,0, 0xFF,0,0,0, 0xFE,0,0,0])];
        using var file = new MemoryStream();
        using var frames = new BinaryWriter(file, Encoding.Latin1, true);
        var incoming = new Net7GalaxyMapData([], [], [], []);
        foreach (byte[] packet in packets)
        {
            frames.Write((ushort)(packet.Length + 4)); frames.Write((ushort)0x97); frames.Write(packet);
            incoming = Net7GalaxyMapUpdates.Apply(incoming, packet);
        }
        var local = Net7GalaxyMapCodec.DecodeCurrentGeometry(file.ToArray());
        Assert.Equal(incoming.AuxiliaryBlob, local.AuxiliaryBlob);
        Assert.Single(local.Sectors); Assert.Single(local.Gates);
        Assert.Equal(11u, local.Sectors[0].CommandType);
        Assert.Equal(6u, local.Sectors[0].Subtype);
        Assert.Equal(999u, local.Sectors[0].Field2);
        Assert.Equal(incoming.RecordCount, local.RecordCount);
        Assert.Empty(Net7GalaxyMapCodec.DecodeCurrentGeometry([]).AuxiliaryBlob);
    }

    [Fact]
    public void ShortCommandHeadersUseTheInitializedCommandAndEmptyView()
    {
        // 00870FD0: Command -1 and Length 0 before bounded reads.
        var map = new Net7GalaxyMapData([], [], [], []) { RecordCount = 20 };
        Assert.Same(map, Net7GalaxyMapUpdates.Apply(map, new byte[] { 9, 0 }));
        Assert.Same(map, Net7GalaxyMapUpdates.Apply(map, ReadOnlySpan<byte>.Empty));
        Assert.Equal(new Net7GalaxyGateRecord(20, 0, 0), Assert.Single(Net7GalaxyMapUpdates.Apply(map, new byte[] { 9, 0, 0, 0 }).Gates));
        Assert.Empty(Net7GalaxyMapUpdates.Apply(map with { AuxiliaryBlob = [1, 2] }, new byte[] { 3, 0, 0, 0, 0 }).AuxiliaryBlob);
    }

    [Fact]
    public void GateShortReadsKeepTheInitializedFieldBytes()
    {
        var map = new Net7GalaxyMapData([], [], [], []) { RecordCount = 20 };
        var empty = Net7GalaxyMapUpdates.Apply(map, Packet(9, []));
        Assert.Equal(new Net7GalaxyGateRecord(20, 0, 0), Assert.Single(empty.Gates));
        var partial = Net7GalaxyMapUpdates.Apply(map, Packet(9, [7,0,0,0, 0x34,0x12]));
        Assert.Equal(new Net7GalaxyGateRecord(7, 0x1234, 0), Assert.Single(partial.Gates));
    }

    [Fact]
    public void SystemFinalFieldRetainsAllFourBytesAndItsUnreadDefault()
    {
        using var record = new MemoryStream();
        using var writer = new BinaryWriter(record, Encoding.Latin1, true);
        writer.Write(1u); writer.Write(100u); writer.Write((byte)0);
        for (int i = 0; i < 6; i++) writer.Write(0f);
        var map = new Net7GalaxyMapData([], [], [], []);
        var defaults = Net7GalaxyMapUpdates.Apply(map, Packet(5, record.ToArray()));
        Assert.Equal(1f, defaults.Systems[0].Scalar);
        Assert.Equal(3u, defaults.Systems[0].TailValue);
        writer.Write(2f); writer.Write(0x100u);
        var full = Net7GalaxyMapUpdates.Apply(map, Packet(5, record.ToArray()));
        Assert.Equal(0x100u, full.Systems[0].TailValue);
    }

    [Fact]
    public void ReplacingAnIndexRetainsOldHashButRemovalOnlyRemovesItsCurrentOwner()
    {
        var system = new Net7GalaxySystemRecord(7, 100, "Old", [0,0,0], [0,0,0], 1, 0);
        var map = new Net7GalaxyMapData([], [system], [], []) { RecordCount = 20 };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, true);
        writer.Write(7u); writer.Write(200u); writer.Write(100u);
        writer.Write(Encoding.Latin1.GetBytes("New sector\0"));
        writer.Write(6u); writer.Write(0f); writer.Write(0f); writer.Write(0f);
        var added = Net7GalaxyMapUpdates.Apply(map, Packet(6, stream.ToArray()));
        Assert.Single(added.Systems); Assert.Single(added.Sectors);
        var removed = Net7GalaxyMapUpdates.Apply(added, Packet(2, BitConverter.GetBytes(7u)));
        Assert.Single(removed.Systems); Assert.Empty(removed.Sectors);
        Assert.Equal(20u, removed.RecordCount);
        var empty = new Net7GalaxyMapData([], [], [], []);
        var location = new Net7GalaxyMapLocation(1, "", "", "", 20);
        Assert.Equal(Net7GalaxyMapLocationWire.BuildPresenceReply(location, empty),
            Net7GalaxyMapLocationWire.BuildPresenceReply(location, removed));
        var repeated = Net7GalaxyMapUpdates.Apply(removed, Packet(2, BitConverter.GetBytes(7u)));
        Assert.Single(repeated.Systems);
        var restored = Net7GalaxyMapUpdates.Apply(repeated, Packet(6, stream.ToArray()));
        Assert.Single(restored.Systems); Assert.Single(restored.Sectors);
    }

    [Theory]
    [InlineData(0u)] [InlineData(1u)] [InlineData(10u)] [InlineData(uint.MaxValue)]
    public void UnknownCommandsLeaveTheMapIntact(uint command)
    {
        var map = new Net7GalaxyMapData([1], [], [], []);
        Assert.Same(map, Net7GalaxyMapUpdates.Apply(map, Packet(command, [2, 3, 4])));
        var next = Net7GalaxyMapUpdates.Apply(map, Packet(3, [5]));
        Assert.Equal(new byte[] { 5 }, next.AuxiliaryBlob);
    }

    [Fact]
    public void PersistedWholeMapFlagIsIndependentOfRecordPresenceAndKnownBits()
    {
        Assert.False(EnbRetailPlayerOptionsCatalog.ParseText("").GalaxyMapShowAll);
        Assert.True(EnbRetailPlayerOptionsCatalog.ParseText("[BOOL-001]\nOptionName=Galaxymap_Show_All\nOptionValue=yes\n").GalaxyMapShowAll);
        var location=new Net7GalaxyMapLocation(1,"","","",504);
        var map=Net7LocalDataAssets.CurrentGalaxyMap;
        var knownReply=Net7GalaxyMapLocationWire.BuildPresenceReply(location,map);
        var wholeReply=Net7GalaxyMapLocationWire.BuildPresenceReply(location,map,true);
        Assert.Equal(knownReply[..^1],wholeReply[..^1]);
        Assert.Equal(1,wholeReply[^1]);
    }

    [Fact]
    public void CommandThreeReplacesKnownBitsWithoutRemovingRecordsOrChangingPresenceReply()
    {
        var map = Net7LocalDataAssets.CurrentGalaxyMap;
        Assert.All(map.Sectors, s => Assert.True(map.IsKnown(s)));
        var unknown = Net7GalaxyMapUpdates.Apply(map, Packet(3, new byte[63]));
        Assert.All(unknown.Sectors, s => Assert.False(unknown.IsKnown(s)));
        Assert.Equal(map.Sectors, EnbGalaxyMapPresentation.VisibleSectors(unknown));
        Assert.Equal(Net7GalaxyMapLocationWire.BuildPresenceReply(new(1,"","","",504),map),
            Net7GalaxyMapLocationWire.BuildPresenceReply(new(1,"","","",504),unknown));
        var sector = map.Sectors.First(s => s.Field0 % 8 == 7);
        byte[] bits = new byte[63]; bits[sector.Field0 / 8] = 128;
        var known = Net7GalaxyMapUpdates.Apply(unknown, Packet(3,bits));
        bits.AsSpan().Clear(); // Network payload ownership must not leak into the snapshot.
        Assert.Single(known.Sectors, known.IsKnown);
        Assert.True(known.IsKnown(sector));
        Assert.All(unknown.Sectors,s=>Assert.False(unknown.IsKnown(s)));
        Assert.False(Net7GalaxyMapUpdates.Apply(known,Packet(3,[])).IsKnown(sector));
    }

    [Fact]
    public void KnownDestinationControlsDirectedLinkColorAndHomeUsesInverse()
    {
        var system = new Net7GalaxySystemRecord(0,100,"System",[.25f,.5f,.75f],[0,0,0],1,0);
        var a = new Net7GalaxySectorRecord(6,7,1000,100,"A",6,[0,0,0]);
        var b = a with { Field0=8,SectorHashId=1001,Name="B" };
        var map = new Net7GalaxyMapData([128,0],[system],[a,b],[]);
        Assert.Equal(new EnbRgb(.4f,.6f,.8f),EnbGalaxyMapPresentation.SectorColor(map,a));
        Assert.Equal(new EnbRgb(.35f,.35f,.35f),EnbGalaxyMapPresentation.SectorColor(map,b));
        Assert.Equal(new EnbRgb(.65f,.65f,.65f),EnbGalaxyMapPresentation.LinkColor(map,a,b));
        var inverse = EnbGalaxyMapPresentation.SectorColor(map,a,true);
        Assert.Equal(inverse,EnbGalaxyMapPresentation.LinkColor(map,b,a));
        Assert.Equal(EnbGalaxyMapPresentation.SectorColor(map,a),EnbGalaxyMapPresentation.LinkColor(map,b with { Field2=200 },a));
    }

    [Fact]
    public void RemovingAParentSuppressesItsSectorPresentationsWithoutDeletingSourceChildren()
    {
        var map = Net7LocalDataAssets.CurrentGalaxyMap;
        var parent = map.Systems.First();
        var next = Net7GalaxyMapUpdates.Apply(map,Packet(2,BitConverter.GetBytes(parent.Field0)));
        Assert.Equal(map.Sectors,next.Sectors);
        Assert.DoesNotContain(EnbGalaxyMapPresentation.VisibleSectors(next),s=>s.Field2==parent.SystemHashId);
        Assert.Equal(map.Systems.Count-1,next.Systems.Count);
    }

    [Fact]
    public void MissingParentAndUnsupportedSubtypeHaveNoInventedPresentation()
    {
        var map=Net7LocalDataAssets.CurrentGalaxyMap;
        var sector=map.Sectors[0];
        var absent=map with { Sectors=[sector with { Field2=uint.MaxValue },sector with { Subtype=10 }] };
        Assert.Empty(EnbGalaxyMapPresentation.VisibleSectors(absent));
        Assert.Equal(new EnbRgb(.35f,.35f,.35f),EnbGalaxyMapPresentation.SectorColor(absent,absent.Sectors[0]));
        var view=Packet(3,[0x80]);
        Assert.Equal(new byte[]{0x80},Net7GalaxyMapUpdates.Apply(map,[..view,0xFF]).AuxiliaryBlob);
        Assert.Equal(map.Systems,Net7GalaxyMapUpdates.Apply(map,Packet(2,[0xFF,0xFF,0xFF])).Systems);
    }

    [Fact]
    public void LoginReplayRestoresLocalBitsAndMissingRecordsButPreservesSuppliedNamesAndExtraRecords()
    {
        var asset=Net7LocalDataAssets.CurrentGalaxyMap;
        Assert.Same(asset,Net7GalaxyMapUpdates.ReplayLocalAsset(asset,asset));
        var first=asset.Sectors[0];
        var map=asset with { AuxiliaryBlob=[], Sectors=[..asset.Sectors.Skip(1).Select(s=>s with { Name="Server "+s.Name }), first with { Field0=1000,SectorHashId=100000 }] };
        var restored=Net7GalaxyMapUpdates.ReplayLocalAsset(map,asset);
        Assert.Equal(asset.AuxiliaryBlob,restored.AuxiliaryBlob);
        Assert.Equal(asset.Sectors.Count+1,restored.Sectors.Count);
        Assert.Contains(restored.Sectors,s=>s.SectorHashId==first.SectorHashId && s.Name==first.Name);
        Assert.Contains(restored.Sectors,s=>s.Name.StartsWith("Server ",StringComparison.Ordinal));
        Assert.Contains(restored.Sectors,s=>s.SectorHashId==100000);
        Assert.Same(restored,Net7GalaxyMapUpdates.ReplayLocalAsset(restored,asset));
    }

    [Theory]
    [InlineData(5u)] [InlineData(6u)] [InlineData(7u)] [InlineData(8u)] [InlineData(9u)] [InlineData(11u)]
    public void RetainedRecordsCanBeRemovedAndReadmittedAndDuplicateHashesDoNotOverwrite(uint command)
    {
        var map = new Net7GalaxyMapData([255,255,255],[],[],[]) { RecordCount=20 };
        using var stream=new MemoryStream(); using var writer=new BinaryWriter(stream,Encoding.Latin1,true);
        writer.Write(7u);
        writer.Write(100u);
        if(command!=5) writer.Write(200u);
        if(command!=9)
        {
            writer.Write(Encoding.Latin1.GetBytes("Authored name\0"));
            if(command==5) { writer.Write(.2f);writer.Write(.3f);writer.Write(.4f); }
            else writer.Write(command);
            writer.Write(1f);writer.Write(2f);writer.Write(3f);
            if(command==5) { writer.Write(1f);writer.Write((byte)2); }
        }
        byte[] payload=Packet(command,stream.ToArray());
        var added=Net7GalaxyMapUpdates.Apply(map,payload);
        Assert.Same(added,Net7GalaxyMapUpdates.Apply(added,payload));
        var removed=Net7GalaxyMapUpdates.Apply(added,Packet(2,BitConverter.GetBytes(7u)));
        Assert.Empty(removed.Systems); Assert.Empty(removed.Sectors); Assert.Empty(removed.Gates);
        var restored=Net7GalaxyMapUpdates.Apply(removed,payload);
        Assert.Single(restored.Systems.Cast<object>().Concat(restored.Sectors).Concat(restored.Gates));
        // A declared view longer than the packet is pool garbage (safety rule).
        // Shorter headers use 00870FD0's initialized Command/Length instead.
        for(int length=8;length<payload.Length;length++)
            Assert.Throws<InvalidDataException>(()=>Net7GalaxyMapUpdates.Apply(map,payload.AsSpan(0,length)));
        // Unassigned record uses the retained record count, not list length.
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8),uint.MaxValue);
        var assigned=Net7GalaxyMapUpdates.Apply(map,payload);
        Assert.Equal(21u,assigned.RecordCount);
    }
}
