using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

[Trait("Category", "Fast")]
public sealed class TextureOverrideTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "texture-pack-" + Guid.NewGuid().ToString("N"));
    public TextureOverrideTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    [Fact]
    public void PairChangesOrdinaryCompressedAndHsvSourcesTogether()
    {
        Pair(8);
        var pack = new EnbTextureOverridePack(directory);
        var resolver = new EnbRetailTextureResolver(null, overrides: pack);
        var thumbnail = Assert.IsType<EnbCurrentRetailTextureThumbnail>(pack.ResolveThumbnail("SHIP_ALL.tga"));
        Assert.Equal(8u, thumbnail.Width);
        Assert.Equal(8, resolver.Resolve("SHIP_ALL.tga").Image!.Width);
        Assert.True(resolver.TryReadCurrentRetailSource("ship_all.tga", true, out var dds, out var selected));
        Assert.Equal("SHIP_ALL.dds", Path.GetFileName(selected.ArchivePath));
        var shifted = resolver.RecolorCurrentRetailTexture("ship_all.tga", new Vector3(120, 0, 0));
        Assert.Equal(8, shifted.Image!.Width);
        Assert.NotEqual(resolver.Resolve("ship_all.tga").Image!.GetPixel(0, 0), shifted.Image.GetPixel(0, 0));
        var state = new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault, 0) { CurrentRetailThumbnail = thumbnail });
        var owner = new EnbCurrentRetailOrdinaryTextureOwner(new(4096, 4096, uint.MaxValue, false), 32, 32, 0);
        var surface = owner.Load(resolver, state, "ship_all.tga", Vector3.Zero);
        Assert.NotNull(surface?.Image);
        Assert.Equal(8, surface.Image.Width);
        Assert.StartsWith(directory, surface.ResourceLocation!.ArchivePath);
        Assert.Equal(1, pack.LoadedTextureCount);
        File.WriteAllBytes(Path.Combine(directory, "SHIP_ALL.dds"), [1, 2, 3]);
        Assert.True(resolver.TryReadCurrentRetailSource("ship_all.tga", true, out var retained, out _));
        Assert.Same(dds, retained); // No metadata/source mixture after an in-session edit.
        Assert.Empty(pack.Diagnostics);
    }
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("dimensions")]
    public void InvalidPairFallsBackAsAWhole(string failure)
    {
        Pair(8);
        string path = Path.Combine(directory, "SHIP_ALL.dds");
        if (failure == "missing") File.Delete(path);
        if (failure == "corrupt") File.WriteAllBytes(path, [1, 2, 3]);
        if (failure == "dimensions") File.WriteAllBytes(path, Dds(4));
        var pack = new EnbTextureOverridePack(directory);
        Assert.Null(pack.ResolveThumbnail("ship_all.tga"));
        Assert.False(pack.TryRead("ship_all.tga", false, out _, out _));
        Assert.False(pack.TryRead("ship_all.tga", true, out _, out _));
        Assert.NotEmpty(pack.Diagnostics);
        if (!OperatingSystem.IsWindows()) return;
        string original = Path.Combine(directory, "original"); Directory.CreateDirectory(original);
        var baseline = Tga(4); File.WriteAllBytes(Path.Combine(original, "ship_all.tga"), baseline);
        Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(original, original, out var source));
        var resolver = new EnbRetailTextureResolver(null, source, pack);
        Assert.True(resolver.TryReadCurrentRetailSource("ship_all.tga", false, out var actual, out var location));
        Assert.Equal(baseline, actual); Assert.StartsWith(original, location.ArchivePath);
    }
    [Fact]
    public void CompressedSourceNameFollowsTheNativeThreeByteOverwrite()
    {
        // 009A0870 writes "dds" over the three bytes before the copied name's terminator:
        // "ab" opens "ds", "a" opens "s"; a 256-byte name has no terminator (no source).
        if (!OperatingSystem.IsWindows()) return;
        string original = Path.Combine(directory, "native"); Directory.CreateDirectory(original);
        File.WriteAllBytes(Path.Combine(original, "ds"), Dds(4));
        File.WriteAllBytes(Path.Combine(original, "s"), Dds(8));
        Assert.True(EnbCurrentRetailThumbnailFileSource.TryLoad(original, original, out var source));
        var resolver = new EnbRetailTextureResolver(null, source);
        Assert.True(resolver.TryReadCurrentRetailSource("ab", true, out var two, out var twoLocation));
        Assert.Equal("ds", Path.GetFileName(twoLocation.ArchivePath));
        Assert.True(resolver.TryReadCurrentRetailSource("a", true, out _, out var oneLocation));
        Assert.Equal("s", Path.GetFileName(oneLocation.ArchivePath));
        Assert.False(resolver.TryReadCurrentRetailSource(new string('x', 252) + ".tga", true, out _, out _));
    }
    [Theory]
    [InlineData("../ship_all.tga")]
    [InlineData("folder\\ship_all.tga")]
    [InlineData("C:ship_all.tga")]
    [InlineData("ship_all.png")]
    public void LogicalNamesCannotSelectPaths(string name)
    {
        Pair(4); var pack = new EnbTextureOverridePack(directory);
        Assert.False(pack.TryRead(name, false, out _, out _));
    }
    [Fact]
    public void NoFolderIsClassicAndRequiresNoInstallation()
    {
        var pack = new EnbTextureOverridePack(Path.Combine(directory, "absent"));
        Assert.False(pack.TryRead("ship_all.tga", false, out _, out _));
        Assert.Null(pack.ResolveThumbnail("ship_all.tga")); Assert.Empty(pack.Diagnostics);
    }
    [Fact]
    public void NamedPacksReplaceMetadataAndSourcesAndRejectEscapes()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} installed)return;
        foreach(var (name,size) in new[]{("A",4),("B",8)})
        {
            string folder=Path.Combine(directory,name);Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder,"Ship_all.tga"),Tga(size));
            File.WriteAllBytes(Path.Combine(folder,"Ship_all.dds"),Dds(size));
        }
        var assets=EnbRetailAssetBundle.Load(installed,directory);
        Assert.Equal(new string?[]{"","A","B"},assets.GetTexturePackNames());
        foreach(var (name,size) in new[]{("A",4),("B",8),("A",4)})
        {
            assets.SelectTexturePack(name);
            Assert.Equal((uint)size,assets.ResolveTextureThumbnail("ship_all.tga")!.Width);
            Assert.Equal(size,assets.Textures.Resolve("ship_all.tga").Image!.Width);
            var resolver=assets.Textures;long revision=assets.TextureRevision;
            assets.SelectTexturePack(name);Assert.Same(resolver,assets.Textures);Assert.Equal(revision,assets.TextureRevision);
        }
        Assert.Throws<ArgumentException>(()=>assets.SelectTexturePack("../A"));
        Assert.Equal("A",assets.SelectedTexturePack);
        assets.SelectTexturePack("missing");Assert.Equal("",assets.SelectedTexturePack);
        Assert.Null(assets.TextureOverrides);Assert.False(assets.TextureOverridesEnabled);
        Assert.NotEqual(4u,assets.ResolveTextureThumbnail("ship_all.tga")!.Width);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SourceMetadataIgnoresDiskThumbnailCaches(bool compressed)
    {
        string release=Path.Combine(directory,"release");
        string ini=Path.Combine(directory,"Data","client","ini");
        string mix=Path.Combine(directory,"Data","client","mixfiles");
        Directory.CreateDirectory(release);Directory.CreateDirectory(ini);Directory.CreateDirectory(mix);
        File.WriteAllBytes(Path.Combine(release,"client.exe"),[]);
        File.WriteAllText(Path.Combine(ini,"basset.ini"),"");
        File.WriteAllBytes(Path.Combine(release,"ship_all.tga"),Tga(4));
        if(compressed)File.WriteAllBytes(Path.Combine(release,"ship_all.dds"),Dds(8));
        // A valid global table deliberately disagrees with the real source.
        byte[] name=System.Text.Encoding.ASCII.GetBytes("ship_all.tga\0");
        byte[] table=new byte[52+name.Length];
        void W(int offset,uint value)=>BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(offset),value);
        W(0,0x36554854);W(4,1);W(24,1);W(28,1);W(32,128);W(36,128);W(40,1);W(44,21);W(48,(uint)name.Length);
        name.CopyTo(table,52);
        string global=Path.Combine(mix,"global.th6"),bad=Path.Combine(mix,"broken.th6");
        File.WriteAllBytes(global,table);File.WriteAllBytes(bad,[1,2,3]);
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryResolveRoot(directory)!);
        var metadata=Assert.IsType<EnbCurrentRetailTextureThumbnail>(assets.ResolveTextureThumbnail("ship_all.tga"));
        int expected=compressed?8:4;
        Assert.Equal((uint)expected,metadata.Width);Assert.Equal((uint)expected,metadata.Height);
        Assert.Same(metadata,assets.ResolveTextureThumbnail("SHIP_ALL.TGA"));
        var state=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault,0){CurrentRetailThumbnail=metadata});
        var owner=new EnbCurrentRetailOrdinaryTextureOwner(new(4096,4096,uint.MaxValue,false),32,32,0);
        var surface=owner.Load(assets.Textures,state,"ship_all.tga",Vector3.Zero);
        Assert.NotNull(surface?.Image);Assert.Equal(expected,surface.Image.Width);Assert.Equal(expected,surface.Image.Height);
        Assert.Equal(table,File.ReadAllBytes(global));Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(bad));
        Assert.Equal(2,Directory.GetFiles(mix,"*.th6").Length);
        File.Delete(global);File.Delete(bad);
        assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryResolveRoot(directory)!);
        Assert.Equal((uint)expected,assets.ResolveTextureThumbnail("ship_all.tga")!.Width);
        Assert.Empty(Directory.GetFiles(mix,"*.th6"));
    }
    private void Pair(int size)
    {
        File.WriteAllBytes(Path.Combine(directory, "ship_all.TGA"), Tga(size));
        File.WriteAllBytes(Path.Combine(directory, "SHIP_ALL.dds"), Dds(size));
    }
    private static byte[] Tga(int size)
    {
        var b = new byte[18 + size * size * 4]; b[2] = 2; b[12] = (byte)size; b[14] = (byte)size; b[16] = 32; b[17] = 40;
        for (int i = 18; i < b.Length; i += 4) { b[i + 2] = 255; b[i + 3] = 255; }
        return b;
    }
    private static byte[] Dds(int size)
    {
        var b = new byte[128 + size * size / 2];
        void W(int i, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i), v);
        W(0, 0x20534444); W(4, 124); W(8, 0x81007); W(12, (uint)size); W(16, (uint)size); W(28, 1);
        W(76, 32); W(80, 4); W(84, 0x31545844); W(108, 0x1000);
        for (int i = 128; i < b.Length; i += 8) { b[i + 1] = 0xf8; }
        return b;
    }
}
