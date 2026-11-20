using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbW3dDazzlePrototypeAdmissionTests
{
    [InstalledRetailFact]
    public void AggregateDazzlePublishesChildrenWithoutRetainingTheirModels()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache();
        var source=Source("dazzle-fixture.w3d",Dazzle("SUN01.LF","SUN"));
        Assert.True(cache.TryLoadFile(source,out _,out var error,0),error);
        string path=Path.Combine(Path.GetTempPath(),"EnbNext-DazzleAggregate-"+Guid.NewGuid().ToString("N")+".w3d");
        try
        {
            File.WriteAllBytes(path,EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("DAZZLE725","SUN01.LF",[("MS03A","ROOT")],25,1));
            assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-31000]\nFILE={path}\nNAME=DAZZLE725\n");
            var selected=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,registeredPrototypes:cache.CapturePrototypeSources(out _));
            Assert.Empty(selected.ModelChildren);
            Assert.Contains(selected.PrototypePublications,p=>p.Model.HierarchyModels.Any(m=>m.Name=="MS03A"));
            File.Delete(path);
            var built=selected.Build();var dazzle=Assert.Single(built.Resource!.ExternalRenderObjects);
            Assert.Equal("SUN01.LF",dazzle.Name);Assert.Equal("SUN",dazzle.DazzleTypeName);
            Assert.NotNull(dazzle.DazzleDefinition);Assert.True(built.ForceSubObjectLod);
            Assert.Empty(built.ModelAttachments);Assert.Empty(built.Resource.Meshes);
        }
        finally {File.Delete(path);}
    }

    private static byte[] Chunk(uint kind, byte[] payload)
    {
        var bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
    private static byte[] Dazzle(string name, string type) => Chunk(0x900,
        [..Chunk(0x901, Encoding.ASCII.GetBytes(name + "\0")), ..Chunk(0x902, Encoding.ASCII.GetBytes(type + "\0"))]);
    private static EnbResolvedW3dResource Source(string file, byte[] bytes)
    {
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        return new(file, "SUN01.LF", new(file, "offline.mix", 0, bytes.Length), model, []);
    }

    [Fact]
    public void DazzleUsesFirstNamedPrototypeAcrossDuplicateChunksFilesAndReload()
    {
        byte[] first = Dazzle("SUN01.LF", "SUN");
        var source = Source("sun01.w3d", [..first, ..Dazzle("sun01.lf", "DEFAULT"), ..Dazzle("other", "INTERIOR01")]);
        Assert.Equal(new[] { 0, first.Length, first.Length + Dazzle("sun01.lf", "DEFAULT").Length }, source.Model.PrototypeEntries.Select(e => e.ChunkOffset));
        Assert.All(source.Model.PrototypeEntries, entry => Assert.Equal(EnbW3dPrototypeKind.Dazzle, entry.Kind));
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source, out var compiled, out var error), error);
        Assert.Equal(3, compiled.ExternalRenderObjects.Count);
        Assert.True(cache.TryFindPrototypeSource("sun01.lf", out var original));
        Assert.Equal(0, original.RegisteredPrototype!.Value.Ordinal);
        Assert.True(cache.TryGetRegisteredPrototype(original, out var registered));
        Assert.Equal("SUN", registered.ExternalRenderObjects[original.RegisteredPrototype.Value.Ordinal].DazzleTypeName);
        Assert.True(cache.TryLoadFile(Source("later.w3d", Dazzle("SUN01.LF", "DEFAULT")), out _, out error), error);
        Assert.True(cache.TryLoadFile(source, out _, out error), error);
        Assert.True(cache.TryFindPrototypeSource("SUN01.LF", out var unchanged));
        Assert.Equal(original.PrototypeRegistration, unchanged.PrototypeRegistration);
        Assert.True(cache.RemovePrototype("SUN01.LF"));
        Assert.True(cache.TryLoadFile(source, out _, out error), error);
        Assert.True(cache.TryFindPrototypeSource("SUN01.LF", out var recreated));
        Assert.NotEqual(original.PrototypeRegistration, recreated.PrototypeRegistration);
        Assert.False(cache.TryGetRegisteredPrototype(original, out _));
        Assert.True(cache.TryGetRegisteredPrototype(recreated, out registered));
        Assert.Equal("SUN", registered.ExternalRenderObjects[recreated.RegisteredPrototype!.Value.Ordinal].DazzleTypeName);
    }
}
