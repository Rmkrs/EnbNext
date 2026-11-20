using System.Buffers.Binary;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.LocalData;
using EnbNext.Net7.Protocol;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class LauncherDataFormatTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(128)]
    public void AuxiliarySizeAndFrameInventoryAreNotReleasePins(int size)
    {
        byte[] map = new byte[12 + size];
        BinaryPrimitives.WriteUInt16LittleEndian(map, (ushort)map.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(map.AsSpan(2), 0x97);
        BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(map.AsSpan(8), (uint)size);
        Assert.Equal(size, Net7GalaxyMapCodec.DecodeCurrentGeometry(map).AuxiliaryBlob.Length);
        Assert.Throws<InvalidDataException>(() => Net7GalaxyMapCodec.DecodeCurrentGeometry(map[..^1]));
        map[2] = 0;
        Assert.Throws<InvalidDataException>(() => Net7GalaxyMapCodec.DecodeCurrentGeometry(map));
    }

    [Fact]
    public void LauncherFolderLoadsChangedFilesAndReportsTheFailingPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "EnbNext-launcher-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        Directory.CreateDirectory(Path.Combine(root, "database"));
        try
        {
            // Two independently encoded zero fields form a valid empty cdata catalog.
            byte[] cdata = new byte[8];
            for (int field = 0; field < 2; field++)
                for (int i = 0; i < 4; i++) cdata[field * 4 + 3 - i] = unchecked((byte)(-i - 0x6A));
            File.WriteAllBytes(Path.Combine(root, "bin", "cdata.dat"), cdata);
            byte[] galaxy = new byte[13];
            BinaryPrimitives.WriteUInt16LittleEndian(galaxy, 13);
            BinaryPrimitives.WriteUInt16LittleEndian(galaxy.AsSpan(2), 0x97);
            BinaryPrimitives.WriteUInt32LittleEndian(galaxy.AsSpan(4), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(galaxy.AsSpan(8), 1);
            string mapPath = Path.Combine(root, "database", "GalaxyMap.dat");
            File.WriteAllBytes(mapPath, galaxy);
            var data = Net7LocalDataAssets.LoadFromFolder(root);
            Assert.Equal(0, data.CData.RecordCount);
            Assert.Single(data.GalaxyMap.AuxiliaryBlob);
            Assert.Equal(0, Net7CDataCatalog.DecodePinnedCurrentAsset(cdata).RecordCount);
            File.WriteAllBytes(mapPath, [0]);
            var error = Assert.Throws<Net7LocalDataException>(() => Net7LocalDataAssets.LoadFromFolder(root));
            Assert.Contains(mapPath, error.Message);
            Assert.IsType<InvalidDataException>(error.InnerException);
            var feedback = EnbNext.App.LoginFailureFeedback.Report(error, _ => { }, _ => Assert.Fail("Installation errors are expected player feedback."));
            Assert.True(feedback.Expected);
            Assert.Contains("Net-7", feedback.Message);
            File.Delete(mapPath);
            Assert.Contains(mapPath, Assert.Throws<Net7LocalDataException>(() => Net7LocalDataAssets.LoadFromFolder(root)).Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
