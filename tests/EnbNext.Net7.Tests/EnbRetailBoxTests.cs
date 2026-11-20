using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailBoxTests
{
    [InstalledRetailTheory]
    [InlineData(0u)] [InlineData(1u)]
    public void AggregateBoxCreatesChildrenWithoutRetainingThem(uint attributes)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        byte[] payload=Convert.FromHexString(Payload);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),attributes);
        Assert.True(EnbW3dModel.TryDecode(Chunk(payload),out var model));
        using var cache=new EnbW3dRenderResourceCache();
        const string baseName="V_DECEPT01A.OBJECT015";
        Assert.True(cache.TryLoadFile(new("box-fixture.w3d",baseName,new("box","box",0,payload.Length+8),model,[]),out _,out var error,0),error);
        string file=Path.Combine(Path.GetTempPath(),"EnbNext-BoxAggregate-"+Guid.NewGuid().ToString("N")+".w3d");
        try
        {
            File.WriteAllBytes(file,EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("BOX724",baseName,[("MS03A","ROOT")],25,1));
            assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-31000]\nFILE={file}\nNAME=BOX724\n");
            var selected=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,Vector3.Zero,registeredPrototypes:cache.CapturePrototypeSources(out _));
            Assert.Empty(selected.ModelChildren);
            Assert.Contains(selected.PrototypePublications,p=>p.Model.HierarchyModels.Any(m=>m.Name=="MS03A"));
            File.Delete(file);
            var built=selected.Build();
            Assert.Equal("BOX724",built.StandaloneBox!.Name);Assert.Equal(attributes,built.StandaloneBox.Attributes);
            Assert.True(built.ForceSubObjectLod);Assert.Empty(built.ModelAttachments);
            Assert.False(built.IsNullObject);Assert.Empty(built.Resource!.Meshes);
        }
        finally {File.Delete(file);}
    }

    [InstalledRetailTheory]
    [InlineData(0u)]
    [InlineData(1u)]
    public void EffectSelectsOnlyTheNamedBoxPrototype(uint attributes)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var siblings,out var location,out _));
        byte[] payload=Convert.FromHexString(Payload);BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4),attributes);
        Assert.True(EnbW3dModel.TryDecode([..siblings,..Chunk(payload)],out var model));
        var asset=EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,new EnbResolvedW3dResource("box-fixture.w3d","V_DECEPT01A.OBJECT015",location!,model,[]),Vector3.Zero);
        Assert.False(asset.IsNullObject);Assert.Null(asset.Animation);Assert.Null(asset.Sequence);
        Assert.Empty(asset.Resource!.Meshes);Assert.Empty(asset.Resource.HierarchyModels);Assert.Empty(asset.Resource.HierarchicalLods);
        var box=Assert.Single(asset.Resource.ExternalRenderObjects).BoxDefinition!;
        Assert.Equal(attributes,box.Attributes);Assert.Equal(12,box.IntegerRenderCost);
        Assert.Equal(Assert.Single(model.Boxes).Center,box.Center);
        Assert.Equal(Assert.Single(model.Boxes).Extents,box.Extents);
    }

    [InstalledRetailFact]
    public void BoxEffectCannotBorrowItsContainingHlodAnimation()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var states=EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nID=0\nTYPE=object\nOBJECT=V_DECEPT01A.OBJECT015\nLINK=1\n"+
            "[0\\STATE1]\nID=1\nTYPE=anim\nANIM=SK_SDEST03A.SK_SDEST03A-1\nNEXT=2\nRS_ACTIVATE_NEXT=true\n"+
            "[0\\STATE2]\nID=2\nTYPE=anim\nSUFFIX=-1\n");
        var catalog=new EnbRetailPresentationCatalog();catalog.LoadBaseAssetsText("[BASE-31000]\nRSLID=0\nRSID=0\n");
        var resolved=new EnbRetailBaseAssetResolver(catalog,assets.MixResources,states,assets.RslResources).Resolve(31000);
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,resolved);
        Assert.NotNull(Assert.Single(asset.Resource!.ExternalRenderObjects).BoxDefinition);
        Assert.Empty(asset.Resource.Meshes);Assert.Null(asset.Animation);Assert.Null(asset.Sequence);
        Assert.Empty(asset.Sample(0,100000));
    }

    private const string Payload = "0000010001000000565F4445434550543031412E4F424A4543543031350000000000000000000000E5A6D700A0EF863E7491DF3D0000B0B676EF863E8CE1223FA6D33E3F";

    [Theory]
    [InlineData(0u, 0x1a)]
    [InlineData(1u, 0x1b)]
    [InlineData(0xffffffffu, 0x1b)]
    public void PrototypePreservesPayloadAndParticipatesInHlod(uint attributes, int classId)
    {
        byte[] payload = Convert.FromHexString(Payload);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), attributes);
        var bytes = Chunk(payload);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var box = Assert.Single(model.Boxes);
        Assert.Equal(0x10000u, box.Version);
        Assert.Equal(attributes, box.Attributes);
        Assert.Equal(classId, box.RetailClassId);
        Assert.Equal("V_DECEPT01A.OBJECT015", box.Name);
        Assert.Equal(0x00d7a6e5u, box.Color);
        var location = new EnbMixResourceLocation("box.w3d", "test.mix", 0, bytes.Length);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(new("box.w3d", box.Name, location, model, []), out var resource, out var error), error);
        Assert.Same(box, Assert.Single(resource.ExternalRenderObjects).BoxDefinition);
        var hierarchy = new EnbW3dRenderHierarchy(0x30000, "TREE", Vector3.Zero, false,
            [new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity)], []);
        var hlod = new EnbW3dRenderHlod("BOX", "TREE", [new(0, float.MaxValue, 0x7f7fffff, [new(0, box.Name)])])
            { Aggregate = new(0, [new(0, box.Name)]) };
        resource = resource.Copy().SetHierarchies([hierarchy]).SetHierarchicalLods([hlod]);
        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource, hlod, Matrix4x4.Identity, null, 0, out var bounds, out error), error);
        Assert.Equal(box.Center.X, bounds.LocalBox.Center.X, 6);
        Assert.Equal(box.Center.Y, bounds.LocalBox.Center.Y, 6);
        Assert.Equal(box.Center.Z, bounds.LocalBox.Center.Z, 6);
        Assert.Equal(box.Extents.X, bounds.LocalBox.Extents.X, 6);
        Assert.Equal(box.Extents.Y, bounds.LocalBox.Extents.Y, 6);
        Assert.Equal(box.Extents.Z, bounds.LocalBox.Extents.Z, 6);
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(resource, hlod, _ => true, out var costs, out error), error);
        Assert.Equal(12, Assert.Single(costs));
        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(resource, hlod, _ => true, out var cost, out error), error);
        Assert.Equal(12f, cost);
        Assert.Empty(resource.Meshes);
    }

    [Fact]
    public void ShortPayloadIsContainedWithoutInventingVersionOrTailValidation()
    {
        byte[] payload = Convert.FromHexString(Payload);
        for (int length = 0; length < payload.Length; length++)
            Assert.False(EnbW3dModel.TryDecode(Chunk(payload[..length]), out _));
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 0x12345678);
        Assert.True(EnbW3dModel.TryDecode(Chunk([..payload, 0xff]), out var model));
        Assert.Equal(0x12345678u, Assert.Single(model.Boxes).Version);
    }

    private static byte[] Chunk(byte[] payload)
    {
        var bytes = new byte[payload.Length + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x740);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }
}
