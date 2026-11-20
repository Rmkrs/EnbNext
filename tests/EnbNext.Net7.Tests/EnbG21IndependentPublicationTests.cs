using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbG21IndependentPublicationTests
{
    private static byte[] Root(uint kind,byte[] payload)
    {
        byte[] bytes=new byte[payload.Length+8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes,kind);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)payload.Length);
        payload.CopyTo(bytes,8);return bytes;
    }
    private static byte[] Null(string name)
    {
        byte[] payload=new byte[48];Encoding.ASCII.GetBytes(name).CopyTo(payload,16);return Root(0x750,payload);
    }
    private static EnbResolvedW3dResource Decode(byte[] bytes)
    {
        Assert.True(EnbRetailW3dFileResolver.TryDecodeFile("fixture.w3d","FIRST",bytes,null,"fixture.w3d",null,out var source,out var why),why);
        return source;
    }

    [Theory]
    [InlineData(0u)] [InlineData(0x100u)] [InlineData(0x200u)] [InlineData(0x280u)]
    [InlineData(0x300u)] [InlineData(0x400u)] [InlineData(0x420u)] [InlineData(0x600u)]
    [InlineData(0x700u)] [InlineData(0x740u)] [InlineData(0x750u)] [InlineData(0x900u)]
    public void BoundedFailureRetainsBothNamesAndRegistryLookupAfterFileRemoval(uint failedKind)
    {
        var directory=Path.Combine(Path.GetTempPath(),"enb-g21-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            byte[] bytes=[..Null("FIRST"),..Root(failedKind,failedKind==0x600?Root(0x601,[]):[]),..Null("LAST")];
            File.WriteAllBytes(Path.Combine(directory,"FIRST.w3d"),bytes);
            var files=new EnbRetailFileResolver(directory,null);
            using var cache=new EnbW3dRenderResourceCache();
            var context=cache.CreatePublicationContext();var names=context.CreateNamespace();
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"FIRST",out var first,out var why,out var publications,names),why);
            var publication=Assert.Single(publications);
            Assert.Equal(2,publication.Model.NullObjects.Count);
            Assert.True(cache.TryLoadFile(publication,out var loaded,out why),why);
            Assert.True(cache.TryFindPrototypeSource("FIRST",out var before));
            Assert.True(cache.TryFindPrototypeSource("LAST",out var last));
            File.Delete(Path.Combine(directory,"FIRST.w3d"));
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"LAST",out var contextual,out why,names),why);
            Assert.Equal(last.PrototypeRegistration,contextual.PrototypeRegistration);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"LAST",out var retained,out why,cache.CapturePrototypeSources(out _)),why);
            Assert.Equal(last.PrototypeRegistration,retained.PrototypeRegistration);
            Assert.True(cache.TryLoadFile(publication,out var repeat,out why),why);Assert.Same(loaded,repeat);
            Assert.True(cache.TryFindPrototypeSource("FIRST",out var after));Assert.Equal(before.PrototypeRegistration,after.PrototypeRegistration);
            Assert.True(cache.RemovePrototype("FIRST"));Assert.True(cache.TryLoadFile(publication,out _,out why),why);
            Assert.True(cache.TryFindPrototypeSource("FIRST",out var replacement));Assert.NotEqual(before.PrototypeRegistration,replacement.PrototypeRegistration);
            Assert.Equal("FIRST",before.Model.NullObjects[before.RegisteredPrototype!.Value.Ordinal].Name);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UntrustedOuterExtentStopsAtVerifiedPrefix(bool truncatedHeader)
    {
        var boundary=truncatedHeader?new byte[]{1,2,3}:new byte[]{0,0,0,0,255,255,255,127};
        byte[] bytes=truncatedHeader?[..Null("FIRST"),..boundary]:[..Null("FIRST"),..boundary,..Null("LAST")];
        var source=Decode(bytes);Assert.Single(source.Model.NullObjects);
        Assert.Equal(!truncatedHeader,source.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        using var cache=new EnbW3dRenderResourceCache();Assert.True(cache.TryLoadFile(source,out _,out var why),why);
        Assert.True(cache.TryFindPrototypeSource("FIRST",out _));Assert.False(cache.TryFindPrototypeSource("LAST",out _));
    }

    [Fact]
    public void AggregateBeforeBadBoundaryRemainsRegisteredWithoutCreatingMissingBase()
    {
        byte[] bytes=[..EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAPPER","MISSING",[],25,0),..new byte[]{1,2,3}];
        var source=Decode(bytes);using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source,out _,out var why),why);
        Assert.True(cache.TryFindPrototypeSource("WRAPPER",out var retained));Assert.Equal("MISSING",retained.AggregatePrototype!.Aggregate!.BaseModelName);
    }

    [Fact]
    public void MalformedNestedExtentCannotPublishAnEmbeddedNameOrHideTheNextRoot()
    {
        byte[] nested=[0x1f,0,0,0,255,255,255,127,..Null("EMBEDDED")];
        var source=Decode([..Null("FIRST"),..Root(0,nested),..Null("LAST")]);
        using var cache=new EnbW3dRenderResourceCache();Assert.True(cache.TryLoadFile(source,out _,out var why),why);
        Assert.True(cache.TryFindPrototypeSource("FIRST",out _));Assert.True(cache.TryFindPrototypeSource("LAST",out _));
        Assert.False(cache.TryFindPrototypeSource("EMBEDDED",out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CompilationAndConstructionFailuresDoNotDiscardSiblingMeshes(bool constructionFailure)
    {
        string path=Path.GetTempFileName();
        try
        {
            byte[] Mesh(string name,uint shader)
            {
                EnbR4W3dRenderResourceTests.BuildResolvedResource(shader,meshName:name,writePath:path);
                var data=File.ReadAllBytes(path);int size=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))&0x7fffffff);return data[..size];
            }
            byte[] bytes=[..Mesh("FIRST",0),..Mesh("BAD",constructionFailure?0u:3u),..Mesh("LAST",0)];
            var source=Decode(bytes);int acquisitions=0;
            using var cache=new EnbW3dRenderResourceCache(definition=>
            {
                if(++acquisitions==2 && constructionFailure)throw new IOException("fixture construction failure");
                return (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault,0));
            });
            Assert.True(cache.TryPublishFile(source,out var bound,out var compiled,out var why),why);
            Assert.Equal(new[]{"FIRST","LAST"},compiled.Meshes.Select(m=>m.Name));
            Assert.Equal(new[]{0,1},compiled.Meshes.Select(m=>m.Ordinal));
            Assert.True(bound.PrototypeFile!.HasUnrepresentedRenderPrototypes);
            Assert.False(cache.TryFindPrototypeSource("SHIPROOT.BAD",out _));
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.LAST",out var last));
            Assert.Equal(1,last.RegisteredPrototype!.Value.Ordinal);
            using var owner=cache.CreateModelOwner(compiled.Meshes[1],[]);using var instance=owner.CloneRenderObject();
            cache.Dispose();Assert.NotNull(instance.TextureIdentity(0));
        }
        finally {File.Delete(path);}
    }

    [Fact]
    public void SameFileDuplicatesConstructInOrderAndReleaseDiscardedTextureOwners()
    {
        string path=Path.GetTempFileName();
        try
        {
            byte[] Mesh(string name,string texture)
            {
                EnbR4W3dRenderResourceTests.BuildResolvedResource(0,meshName:name,writePath:path);
                var data=File.ReadAllBytes(path);int size=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))&0x7fffffff);
                data=data[..size];Encoding.ASCII.GetBytes(texture).CopyTo(data,data.AsSpan().IndexOf("PAINT"u8));return data;
            }
            var source=Decode([..Mesh("FIRST","FIRST"),..Root(0x600,[]),..Mesh("FIRST","OTHER"),..Mesh("LAST","LASTX")]);
            var acquisitions=new List<string>();
            using var cache=new EnbW3dRenderResourceCache(definition=>
            {
                acquisitions.Add(definition.LogicalName);
                return (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault,0));
            });
            Assert.True(cache.TryLoadFile(source,out var compiled,out var why),why);
            Assert.Equal(new[]{"FIRST.TGA","OTHER.TGA","LASTX.TGA"},acquisitions);
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.FIRST",out var first));Assert.Equal(0,first.RegisteredPrototype!.Value.Ordinal);
            using var owner=cache.CreateModelOwner(compiled.Meshes[0],[]);
            Assert.Throws<ArgumentException>(()=>cache.CreateModelOwner(compiled.Meshes[1],[]));
            Assert.True(cache.TryLoadFile(source,out _,out why),why);Assert.Equal(6,acquisitions.Count);
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.FIRST",out var repeated));Assert.Equal(first.PrototypeRegistration,repeated.PrototypeRegistration);
            Assert.True(cache.RemovePrototype("SHIPROOT.FIRST"));
            Assert.True(cache.TryLoadFile(source,out _,out why),why);
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.FIRST",out var replacement));Assert.NotEqual(first.PrototypeRegistration,replacement.PrototypeRegistration);
            Assert.True(cache.TryGetRegisteredPrototype(replacement,out var replaced));
            using var freshOwner=cache.CreateModelOwner(replaced.Meshes[0],[]);
            cache.Dispose();Assert.NotNull(owner.TextureIdentity(0));Assert.NotNull(freshOwner.TextureIdentity(0));
        }
        finally {File.Delete(path);}
    }

    [Fact]
    public void FailedHierarchyRootRetainsLaterTreeWithRemappedOrdinal()
    {
        string path=Path.GetTempFileName();
        try
        {
            byte[] Tree(string name,float center)
            {
                EnbR4W3dRenderResourceTests.BuildResolvedResource(0,hierarchyName:name,hierarchyCenter:new(0,0,0),writePath:path);
                var bytes=File.ReadAllBytes(path);
                int offset=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4))&0x7fffffff);
                int size=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset+4))&0x7fffffff);
                var tree=bytes.AsSpan(offset,size).ToArray();
                BinaryPrimitives.WriteSingleLittleEndian(tree.AsSpan(40),center);return tree;
            }
            var source=Decode([..Tree("FIRST",1),..Tree("BAD",float.NaN),..Tree("LAST",3)]);
            using var cache=new EnbW3dRenderResourceCache();
            Assert.True(cache.TryPublishFile(source,out var bound,out var compiled,out var why),why);
            Assert.Equal(new[]{"FIRST","LAST"},compiled.Hierarchies.Select(h=>h.Name));
            Assert.Equal(new[]{0,1},bound.Model.TopLevelHierarchyOrdinals);
            Assert.True(cache.TryFindHierarchy("LAST",out var last));Assert.Equal(3,last.Center.X);
            Assert.False(cache.TryFindHierarchy("BAD",out _));
        }
        finally {File.Delete(path);}
    }
}
