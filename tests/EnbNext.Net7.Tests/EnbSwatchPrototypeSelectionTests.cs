using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbSwatchPrototypeSelectionTests
{
    [Theory]
    [InlineData(false,false)] [InlineData(true,false)]
    [InlineData(false,true)] [InlineData(true,true)]
    public void AggregateSwatchSelectsCreatedBaseAndPublishesDiscardedChild(bool preloaded,bool nestedChild)
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Swatch",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"fixture.w3d");
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] mesh=File.ReadAllBytes(path);
            byte[] bytes=[..EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("SW733","SHIPROOT.SHIPMESH",
                [("ABSENT733","ROOT"),(nestedChild?"NEST733":"CHILD733","ROOT")],0,0),..mesh];
            byte[] child=new byte[56];BinaryPrimitives.WriteUInt32LittleEndian(child,0x750);
            BinaryPrimitives.WriteUInt32LittleEndian(child.AsSpan(4),48);"CHILD733"u8.CopyTo(child.AsSpan(24));
            File.WriteAllBytes(Path.Combine(directory,"CHILD733.w3d"),child);
            if(nestedChild)File.WriteAllBytes(Path.Combine(directory,"NEST733.w3d"),
                EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("NEST733","NULL",[("CHILD733","ROOT")],0,0));
            var resources=WritePackage(directory,"SW733.w3d",bytes);
            using var prototypes=new EnbW3dRenderResourceCache();
            if(preloaded)
            {
                Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var publication,out var error),error);
                Assert.True(prototypes.TryLoadFile(new("SW733.w3d","SW733",new("SW733","fixture",0,bytes.Length),publication.Model,[])
                    {PrototypeFile=publication},out _,out error),error);
            }
            var definitions=EnbDecalDefinitionCatalog.ParseText("","[Decal00]\nSwatch=SW733\n","");
            using var catalog=EnbCurrentRetailSwatchCatalog.Load(definitions,resources,123,prototypes,new(directory,resources));
            Assert.True(catalog.TryGet(-1,out var descriptor,out var diagnostic),diagnostic);
            Assert.True(prototypes.TryFindPrototypeSource("SW733",out var wrapper));Assert.NotNull(wrapper.AggregatePrototype);
            Assert.True(prototypes.TryFindPrototypeSource("CHILD733",out _));
            Assert.True(prototypes.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out var selected));
            Assert.True(prototypes.TryGetRegisteredPrototype(selected,out var table));
            Assert.Same(table.Meshes[0].MaterialPasses[0],Assert.Single(descriptor.Passes));
            using var waterCache=new EnbW3dRenderResourceCache();
            Assert.True(EnbRetailTerrainWater.TryAcquireSwatch(waterCache,new(directory,resources),resources,"SW733",123,out var water,out diagnostic),diagnostic);
            Assert.True(waterCache.TryFindPrototypeSource("CHILD733",out _));
            Assert.True(waterCache.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out var waterSource));
            Assert.True(waterCache.TryGetRegisteredPrototype(waterSource,out var waterTable));
            Assert.Same(waterTable.Meshes[0],water);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void FailedAggregateCreationRetriesExplicitFileBeforeSelectingDescriptor()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Swatch",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"fixture.w3d");
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] mesh=File.ReadAllBytes(path);
            byte[] wrapper=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("SW733.AGG","SHIPROOT.SHIPMESH",[],0,0);
            var resources=WritePackage(directory,"SW733.w3d",wrapper);
            File.WriteAllBytes(Path.Combine(directory,"SW733.AGG.w3d"),mesh);
            var definitions=EnbDecalDefinitionCatalog.ParseText("","[Decal00]\nSwatch=SW733.AGG\n","");
            using var prototypes=new EnbW3dRenderResourceCache();
            Assert.False(EnbRetailTerrainWater.TryAcquireSwatch(prototypes,new(directory,resources),resources,"SW733.AGG",123,out _,out _));
            Assert.False(prototypes.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out _));
            using var catalog=EnbCurrentRetailSwatchCatalog.Load(definitions,resources,123,prototypes,new(directory,resources));
            Assert.True(catalog.TryGet(-1,out var descriptor,out var diagnostic),diagnostic);
            Assert.True(prototypes.TryFindPrototypeSource("SW733.AGG",out var retained));Assert.NotNull(retained.AggregatePrototype);
            Assert.True(prototypes.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out var selected));
            Assert.True(prototypes.TryGetRegisteredPrototype(selected,out var table));
            Assert.Same(table.Meshes[0].MaterialPasses[0],Assert.Single(descriptor.Passes));
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DescriptorComesFromTheSelectedPrototypeRatherThanAShortMeshName(bool preloaded, bool nonMesh)
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext.Swatch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "fixture.w3d");
            var source = EnbR4W3dRenderResourceTests.BuildResolvedResource(0, writePath: path);
            byte[] bytes = File.ReadAllBytes(path);
            string token = nonMesh ? "SHIPMESH" : "SHIPROOT.SHIPMESH";
            if (nonMesh)
            {
                byte[] empty = new byte[56];
                BinaryPrimitives.WriteUInt32LittleEndian(empty, 0x750);
                BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(4), 48);
                Encoding.ASCII.GetBytes(token).CopyTo(empty, 24);
                bytes = [..empty, ..bytes];
            }
            string name = (nonMesh ? "SHIPMESH" : "SHIPROOT") + ".w3d";
            var resources = WritePackage(directory,name,bytes);
            Assert.NotNull(resources);
            Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
            source = source with { Model = model };
            using var prototypes = new EnbW3dRenderResourceCache();
            if (preloaded) Assert.True(prototypes.TryLoadFile(source, out _, out var error), error);
            var definitions = EnbDecalDefinitionCatalog.ParseText("", $"[Decal00]\nSwatch={token}\n", "");
            using var catalog = EnbCurrentRetailSwatchCatalog.Load(definitions, resources, 123, prototypes);
            Assert.True(catalog.TryGet(-1, out var descriptor, out var diagnostic), diagnostic);
            Assert.True(prototypes.TryFindPrototypeSource(token, out var selected));
            Assert.True(prototypes.TryGetRegisteredPrototype(selected, out var table));
            if (nonMesh)
            {
                Assert.Equal(EnbW3dPrototypeKind.NullObject, selected.RegisteredPrototype!.Value.Kind);
                Assert.NotSame(table.Meshes[0].MaterialPasses[0], descriptor.Passes[0]);
                Assert.Equal(EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor().CurrentRetailUniformShader,
                    descriptor.Passes[0].CurrentRetailUniformShader);
            }
            else
            {
                Assert.Equal(EnbW3dPrototypeKind.Mesh, selected.RegisteredPrototype!.Value.Kind);
                Assert.Same(table.Meshes[0].MaterialPasses[0], Assert.Single(descriptor.Passes));
                Assert.True(prototypes.RemovePrototype(token));
                using var reloaded = EnbCurrentRetailSwatchCatalog.Load(definitions, resources, 456, prototypes);
                Assert.True(reloaded.TryGet(-1, out var replacement, out diagnostic), diagnostic);
                Assert.True(prototypes.TryFindPrototypeSource(token, out var fresh));
                Assert.NotEqual(selected.PrototypeRegistration, fresh.PrototypeRegistration);
                Assert.True(prototypes.TryGetRegisteredPrototype(fresh, out var freshTable));
                Assert.Same(freshTable.Meshes[0].MaterialPasses[0], Assert.Single(replacement.Passes));
                Assert.NotSame(descriptor.Passes[0], replacement.Passes[0]);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public void FailedNameExplicitFileAndFreshNameRetainEveryConstruction(int explicitFileKind,int loads)
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Swatch",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"fixture.w3d");
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] bytes=File.ReadAllBytes(path);
            var resources=WritePackage(directory,"SHIPROOT.w3d",bytes);
            if(explicitFileKind!=0)File.WriteAllBytes(Path.Combine(directory,"SHIPROOT.UNKNOWN.w3d"),explicitFileKind==1?[]:bytes);
            var constructions=new List<string>();
            using var prototypes=new EnbW3dRenderResourceCache(definition=>
            {
                constructions.Add(definition.LogicalName);
                return (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0));
            });
            var definitions=EnbDecalDefinitionCatalog.ParseText("","[Decal00]\nSwatch=SHIPROOT.UNKNOWN\n","");
            using var catalog=EnbCurrentRetailSwatchCatalog.Load(definitions,resources,123,prototypes,new(directory,resources));
            Assert.True(catalog.TryGet(-1,out var descriptor,out var error),error);
            Assert.Equal(Enumerable.Repeat("PAINT.TGA",loads),constructions);
            Assert.Equal(EnbCurrentRetailShipDecalRuntime.CreateSwatchDefaultDescriptor().CurrentRetailUniformShader,
                Assert.Single(descriptor.Passes).CurrentRetailUniformShader);
            Assert.True(prototypes.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out var retained));
            Assert.False(prototypes.TryFindPrototypeSource("SHIPROOT.UNKNOWN",out _));
            Assert.Equal(1,retained.PrototypeRegistration);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(0x500u)] [InlineData(0x741u)] [InlineData(0x742u)]
    public void UndecodedPublicationDoesNotSelectTheMissingSwatchDefault(uint kind)
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext.Swatch",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"fixture.w3d");EnbR4W3dRenderResourceTests.BuildResolvedResource(0,writePath:path);
            byte[] unknown=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(unknown,kind);
            var resources=WritePackage(directory,"UNDECODED.w3d",[..File.ReadAllBytes(path),..unknown]);
            using var cache=new EnbW3dRenderResourceCache();
            var definitions=EnbDecalDefinitionCatalog.ParseText("","[Decal00]\nSwatch=UNDECODED\n","");
            using var catalog=EnbCurrentRetailSwatchCatalog.Load(definitions,resources,123,cache,new(directory,resources));
            Assert.False(catalog.TryGet(-1,out _,out var diagnostic));Assert.Contains("unproven",diagnostic);
            Assert.False(EnbRetailTerrainWater.TryAcquireSwatch(cache,new(directory,resources),resources,"UNDECODED",123,out _,out diagnostic));
            Assert.Contains("unproven",diagnostic);
        }
        finally {Directory.Delete(directory,true);}
    }

    private static EnbMixResourceCatalog WritePackage(string directory,string name,byte[] bytes)
    {
        using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"mixfile_art_01.mix"))))
        {
            writer.Write(0x3158494Du);writer.Write((uint)(16+bytes.Length));
            writer.Write((uint)(32+bytes.Length));writer.Write(0u);writer.Write(bytes);
            writer.Write(1u);writer.Write(0u);writer.Write(16u);writer.Write((uint)bytes.Length);
            writer.Write(1u);byte[] encoded=Encoding.ASCII.GetBytes(name+"\0");
            writer.Write((byte)encoded.Length);writer.Write(encoded);
        }
        return EnbMixResourceCatalog.Load(directory)!;
    }
}
