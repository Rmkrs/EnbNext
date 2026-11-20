using System.Buffers.Binary;
using EnbNext.Assets;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbLayeredBackdropTests
{
    private static byte[]? Installed()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return null;
        var assets = EnbRetailAssetBundle.Load(paths);
        string file = assets.BaseAssetResolver!.Resolve(1186).Definition.File!;
        Assert.True(assets.RetailMixResources!.TryReadResource(file, out var bytes, out _));
        return bytes;
    }

    [Fact]
    public void DarkBackdropRetainsAllAuthoredMembersAndMapperScale()
    {
        if (Installed() is not {} bytes) return;
        Assert.True(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out var sky));
        Assert.Equal("SPHERE01", sky.Mesh.Name);
        Assert.Equal(new[] { "MESH02", "MESH02+" }, sky.AdditiveLayers.Select(l => l.Mesh.Name));
        Assert.Equal(192, sky.Mesh.Triangles.Count + sky.AdditiveLayers.Sum(l => l.Mesh.Triangles.Count));
        foreach (var layer in new[] { sky }.Concat(sky.AdditiveLayers))
        {
            Assert.Equal(6, layer.Mesh.VertexMaterials[0].Info.Mapper1TypeSelector);
            Assert.Equal(2, layer.Mesh.VertexMaterials[0].Mapper1!.UScale);
        }
    }

    [Fact]
    public void MissingHlodDoesNotInventLayerMembership()
    {
        if (Installed() is not {} bytes) return;
        using var stream = new MemoryStream();
        for (int offset = 0; offset < bytes.Length;)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            int length = checked(8 + (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4)) & 0x7fffffff));
            if (type != 0x700) stream.Write(bytes, offset, length);
            offset += length;
        }
        Assert.True(EnbW3dModel.TryDecode(stream.ToArray(), out var model));
        Assert.Empty(model.HierarchicalLods);
        Assert.False(EnbW3dEnvironmentBackdrop.TryDecode(stream.ToArray(), out _));
    }

    [Fact]
    public void AdditionalOpaqueMeshesAreNotSilentlyTreatedAsAdditive()
    {
        if (Installed() is not {} bytes) return;
        byte[] shader = Convert.FromHexString("07000001020100010102000200020002");
        int changed = 0;
        for (int i = 0; i <= bytes.Length - shader.Length; i++)
            if (bytes.AsSpan(i, shader.Length).SequenceEqual(shader)) { bytes[i + 3] = 0; changed++; }
        Assert.Equal(2, changed);
        Assert.True(EnbW3dModel.TryDecode(bytes, out _));
        Assert.False(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out _));
    }

    [Fact]
    public void InstalledInfluenceSkyBaseAssetDecodesAsTheBoundedBackdrop()
    {
        // Production calls TryDecode only for BaseAsset 0x137 (EnbWorldController
        // TryCreateInfluenceEnvironmentRenderer). The installed resource must take the
        // single-sphere path, so the layered and rejecting branches are unreached.
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        string file = assets.BaseAssetResolver!.Resolve(EnbNext.Rendering.EnbCurrentRetailInfluenceNebulaRuntime.SpecialEnvironmentBaseAsset).Definition.File!;
        Assert.True(assets.RetailMixResources!.TryReadResource(file, out var bytes, out _));
        Assert.True(EnbW3dEnvironmentBackdrop.TryDecode(bytes, out var sky));
        Assert.False(sky.IsLayered);
        Assert.Equal("SPHERE01", sky.Mesh.Name, ignoreCase: true);
    }
}
