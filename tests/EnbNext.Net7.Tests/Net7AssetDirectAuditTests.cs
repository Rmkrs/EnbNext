using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

public sealed class Net7AssetDirectAuditTests
{
    private const int GalaxyMapSize = 12_647;
    private const int GalaxyMapFrames = 305;
    private const string GalaxyMapSha256 = "3F2441D7DE271ABF0F3E9D0732E0BE7BEBB4E1F35760A69233219D4F21E98B42";

    [Fact]
    public void Asset02GalaxyMapPinnedAssetAndNativeProjectionMatchDirectAuthority()
    {
        byte[] raw = ReadEmbeddedCurrentAsset("GalaxyMap.dat");
        Assert.Equal(GalaxyMapSize, raw.Length);
        Assert.Equal(GalaxyMapSha256, Convert.ToHexString(SHA256.HashData(raw)));

        var commandCounts = new Dictionary<uint, int>();
        byte[]? rawAuxiliary = null;
        int offset = 0;
        int frameCount = 0;
        while (offset < raw.Length)
        {
            Assert.True(raw.Length - offset >= 12);
            ushort frameLength = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset, 2));
            ushort opcode = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 2, 2));
            uint command = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 4, 4));
            uint blobLength = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 8, 4));

            Assert.Equal((ushort)0x0097, opcode);
            Assert.Equal(frameLength, 12 + checked((int)blobLength));
            Assert.True(offset + frameLength <= raw.Length);

            commandCounts[command] = commandCounts.GetValueOrDefault(command) + 1;
            if (command == 3)
                rawAuxiliary = raw.AsSpan(offset + 12, checked((int)blobLength)).ToArray();

            offset += frameLength;
            frameCount++;
        }

        Assert.Equal(raw.Length, offset);
        Assert.Equal(GalaxyMapFrames, frameCount);
        Assert.Equal(1, commandCounts[3]);
        Assert.Equal(19, commandCounts[5]);
        Assert.Equal(75, commandCounts[6]);
        Assert.Equal(7, commandCounts[7]);
        Assert.Equal(4, commandCounts[8]);
        Assert.Equal(199, commandCounts[9]);
        Assert.Equal(6, commandCounts.Count);

        byte[] auxiliary = Assert.IsType<byte[]>(rawAuxiliary);
        Assert.Equal(63, auxiliary.Length);
        Assert.All(auxiliary, value => Assert.Equal((byte)0xFF, value));

        Net7GalaxyMapData map = Net7GalaxyMapCodec.DecodePinnedCurrentAsset(raw);
        Assert.Equal(auxiliary, map.AuxiliaryBlob);
        Assert.Equal(19, map.Systems.Count);
        Assert.Equal(19, map.Systems.Select(record => record.SystemHashId).Distinct().Count());
        Assert.Equal(86, map.Sectors.Count);
        Assert.Equal(86, map.Sectors.Select(record => record.SectorHashId).Distinct().Count());
        Assert.Equal(75, map.Sectors.Count(record => record.CommandType == 6));
        Assert.Equal(7, map.Sectors.Count(record => record.CommandType == 7));
        Assert.Equal(4, map.Sectors.Count(record => record.CommandType == 8));
        Assert.All(map.Sectors, record => Assert.Equal(record.CommandType, record.Subtype));
        Assert.Equal(199, map.Gates.Count);
        Assert.Equal(199, map.Gates.Select(record => record.GateHashId).Distinct().Count());

        HashSet<uint> sectorIds = map.Sectors.Select(record => record.SectorHashId).ToHashSet();
        Assert.All(map.Gates, gate =>
        {
            Assert.Contains(gate.SectorReferenceA, sectorIds);
            Assert.Contains(gate.SectorReferenceB, sectorIds);
        });

        // The integrated client owns a pinned local semantic model. Production
        // sector entry cannot accept a caller-supplied GalaxyMap model merely to
        // recreate the retail proxy's local 0x0097 self-injection seam.
        MethodInfo[] publicEntryMethods = typeof(Net7Session)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.Name == nameof(Net7Session.EnterSelectedCharacterAsync))
            .ToArray();
        Assert.Single(publicEntryMethods);
        Assert.DoesNotContain(
            publicEntryMethods[0].GetParameters(),
            parameter => parameter.ParameterType == typeof(Net7GalaxyMapData));

        Assert.Same(Net7LocalDataAssets.CurrentGalaxyMap, Net7LocalDataAssets.CurrentGalaxyMap);
        Assert.Equal(
            map.Systems.Select(record => record.SystemHashId),
            Net7LocalDataAssets.CurrentGalaxyMap.Systems.Select(record => record.SystemHashId));
        Assert.Equal(
            map.Sectors.Select(record => record.SectorHashId),
            Net7LocalDataAssets.CurrentGalaxyMap.Sectors.Select(record => record.SectorHashId));
        Assert.Equal(
            map.Gates.Select(record => record.GateHashId),
            Net7LocalDataAssets.CurrentGalaxyMap.Gates.Select(record => record.GateHashId));
    }

    [Fact]
    public void InstalledLauncherCDataDecodesEveryIndexedRecord()
    {
        // The container checks in Net7CDataCatalog.Decode are file-format
        // invariants of the installed launcher asset; the current file passes all of them.
        var catalog = EnbNext.Net7.Gameplay.Net7CDataCatalog.Decode(ReadEmbeddedCurrentAsset("cdata.dat"));
        Assert.True(catalog.RecordCount > 0);
    }

    private static byte[] ReadEmbeddedCurrentAsset(string fileName)
    {
        string root = EnbNext.Installation.EnbInstallDiscovery.RequireLauncher();
        return File.ReadAllBytes(Path.Combine(root, fileName == "cdata.dat" ? "bin" : "database", fileName));
    }
}
