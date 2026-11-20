using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbLegacyParticlePublicationTests
{
    [Fact]
    public async Task EarlierMissingHlodMemberIsNotResurrectedByLaterFilePublication()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var meshFile,out _,out _));
        string early="E"+Guid.NewGuid().ToString("N")[..8],late="F"+Guid.NewGuid().ToString("N")[..8];
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-Occurrence743-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var provider=typeof(EnbRetailAssetBundle).GetField("<FileResources>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        byte[] Mesh(string name)
        {
            int offset=0;while(BinaryPrimitives.ReadUInt32LittleEndian(meshFile.AsSpan(offset))!=0)
                offset+=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(meshFile.AsSpan(offset+4))&0x7fffffffu);
            int length=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(meshFile.AsSpan(offset+4))&0x7fffffffu);
            byte[] mesh=meshFile.AsSpan(offset,length).ToArray();int child=8;
            while(BinaryPrimitives.ReadUInt32LittleEndian(mesh.AsSpan(child))!=0x1f)
                child+=8+(int)(BinaryPrimitives.ReadUInt32LittleEndian(mesh.AsSpan(child+4))&0x7fffffffu);
            mesh.AsSpan(child+16,32).Clear();Encoding.ASCII.GetBytes(name).CopyTo(mesh,child+16);return mesh;
        }
        try
        {
            File.WriteAllBytes(Path.Combine(directory,late+".w3d"),[..Mesh(early),..Mesh(late)]);
            provider.SetValue(assets,new EnbRetailFileResolver(directory,null));
            using var cache=Cache();var context=cache.CreatePublicationContext();
            context.Publish(Source([..Hierarchy(),..Composite(2,"ROOT743",[early,late])]));
            Assert.True(cache.TryFindPrototypeSource("ROOT743",out var root));
            assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=unused743.w3d\nNAME=ROOT743\n");
            var prepared=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,
                registeredFactorySource:root,registeredPrototypes:cache.CapturePrototypeSources(out _),publicationContext:context);
            Assert.True(cache.TryFindPrototypeSource(early,out _));Assert.True(cache.TryFindPrototypeSource(late,out var selected));
            File.Delete(Path.Combine(directory,late+".w3d"));
            var built=await Task.Run(prepared.Build);
            Assert.Equal(late,Assert.Single(Assert.Single(Assert.Single(built.Resource!.HierarchicalLods).Levels).Members).Name);
            Assert.Equal(late,Assert.Single(built.Resource.Meshes).Name);
            Assert.Equal(selected.PrototypeRegistration,Assert.Single(built.RegisteredBaseMeshSources).PrototypeRegistration);
        }
        finally{Directory.Delete(directory,true);}
    }

    private static byte[] Hierarchy()
    {
        byte[] header=new byte[36];BinaryPrimitives.WriteUInt32LittleEndian(header,0x40001);Encoding.ASCII.GetBytes("TREE743").CopyTo(header,4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20),1);
        byte[] pivot=new byte[60];Encoding.ASCII.GetBytes("ROOT").CopyTo(pivot,0);BinaryPrimitives.WriteUInt32LittleEndian(pivot.AsSpan(16),uint.MaxValue);
        BinaryPrimitives.WriteSingleLittleEndian(pivot.AsSpan(56),1);
        return Chunk(0x100,[..Chunk(0x101,header),..Chunk(0x102,pivot)]);
    }
    [Fact]
    public async Task RetainedModelChildBuildUsesPreparedDescendantsAfterFilesAndProviderDisappear()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var bytes,out _,out _));
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-Retained743-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var provider=typeof(EnbRetailAssetBundle).GetField("<FileResources>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            File.WriteAllBytes(Path.Combine(directory,"MS03A.w3d"),bytes);
            using var cache=Cache();var context=cache.CreatePublicationContext();
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(assets.FileResources,assets.RetailMixResources,"MISSILE",out var missile,out var error),error);
            context.Publish(missile);
            context.Publish(Source(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("OUT743","MISSILE",[("MS03A","ROOT")],25,0)));
            Assert.True(cache.TryFindPrototypeSource("OUT743",out var root));
            provider.SetValue(assets,new EnbRetailFileResolver(directory,assets.RetailMixResources));
            assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=unused743.w3d\nNAME=OUT743\n");
            var preparation=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,
                registeredFactorySource:root,registeredPrototypes:cache.CapturePrototypeSources(out _),publicationContext:context);
            var selected=Assert.Single(preparation.ModelChildren).Value;
            var retainedMeshes=cache.CapturePrototypeSources(out _).Where(pair=>pair.Key.StartsWith("MS03A.",StringComparison.OrdinalIgnoreCase)).ToDictionary(pair=>pair.Key,pair=>pair.Value);
            Assert.NotEmpty(retainedMeshes);
            File.Delete(Path.Combine(directory,"MS03A.w3d"));provider.SetValue(assets,new EnbRetailFileResolver(directory,null));
            int count=context.PublicationCount;
            var built=await Task.Run(preparation.Build);var child=Assert.Single(built.ModelAttachments).Asset;
            Assert.Equal(selected.PrototypeRegistration,child.FactoryPrototypeRegistration);
            Assert.NotEmpty(child.Resource!.Meshes);
            Assert.All(child.RegisteredBaseMeshSources,value=>Assert.Equal(retainedMeshes[value.RequestedObjectName].PrototypeRegistration,value.PrototypeRegistration));
            Assert.Equal(count,context.PublicationCount);
        }
        finally{Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false,0)] [InlineData(true,0)]
    [InlineData(false,1)] [InlineData(true,1)]
    [InlineData(false,2)] [InlineData(true,2)]
    [InlineData(false,3)] [InlineData(true,3)]
    public async Task NestedDiscardedAggregatePublishesBeforeNextSiblingAndNeverDuringBuild(bool nestedFirst,int composite)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var meshBytes,out _,out _));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(Source(meshBytes),out var meshTable,out var error),error);
        var texture=meshTable.Meshes.SelectMany(mesh=>mesh.TextureAcquisitions).First(value=>value.Info is not null).LogicalName;
        string name="L"+Guid.NewGuid().ToString("N")[..8];
        string path=Path.Combine(Environment.CurrentDirectory,name+".w3d");
        typeof(EnbRetailAssetBundle).GetField("<FileResources>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .SetValue(assets,new EnbRetailFileResolver(Environment.CurrentDirectory,assets.RetailMixResources));
        var bytes=Legacy(texture);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes,20);
        File.WriteAllBytes(path,bytes);
        try
        {
            int publications=0;
            using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
                new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),definition.Info is null?0u:7u)));
            var context=cache.CreatePublicationContext();
            if(composite==1)context.Publish(Source(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("NEST743.MS03A","NULL",[("MS03A","ROOT")],25,0)));
            var nested=Source(composite==0?EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("NEST743","NULL",[("MS03A","ROOT")],25,0)
                :Composite(composite,"NEST743",["MS03A"]));
            var attachments=nestedFirst?new[]{("NEST743","ROOT"),(name,"ROOT")}:new[]{(name,"ROOT"),("NEST743","ROOT")};
            var outer=Source(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("OUT743","NULL",attachments,25,0));
            context.Publish(nested);context.Publish(outer);
            Assert.True(cache.TryFindPrototypeSource("OUT743",out var registered));
            assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=unused743.w3d\nNAME=OUT743\n");
            var prepared=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,
                registeredFactorySource:registered,registeredPrototypes:cache.CapturePrototypeSources(out _),publicationContext:context);
            if(composite==1)
            {
                Assert.True(cache.TryFindPrototypeSource("NEST743",out var compositeSource));
                Assert.Equal("NEST743.MS03A",Assert.Single(Assert.Single(compositeSource.Model.HierarchyModels).Nodes).RenderObjectName);
                Assert.True(cache.TryFindPrototypeSource("MS03A",out _),string.Join(";",prepared.PrototypePublications.Select(value=>value.RequestedObjectName)));
            }
            Assert.True(cache.TryFindPrototypeSource(name,out var legacy));
            Assert.Equal(nestedFirst?(byte)5:(byte)1,Assert.Single(legacy.Model.ParticleEmitters).Shader.DestinationBlend);
            publications=context.PublicationCount;
            File.Delete(path);
            Assert.True((await Task.Run(prepared.Build)).IsNullObject);
            var retained=await Task.Run(()=>EnbCurrentRetailRObjEffectAsset.LoadPrototype(assets,legacy,System.Numerics.Vector3.Zero));
            Assert.NotNull(retained.Particle);
            Assert.Equal(nestedFirst?(byte)5:(byte)1,retained.Particle.Definition.Shader.DestinationBlend);
            Assert.Equal(publications,context.PublicationCount);
        }
        finally{File.Delete(path);}
    }

    private static byte[] Composite(int kind,string name,string[] members)
    {
        if(kind==3)
        {
            byte[] header=new byte[32];Encoding.ASCII.GetBytes(name).CopyTo(header,4);
            return Chunk(0x420,[..Chunk(0x421,header),..members.SelectMany(member=>Chunk(0x422,Encoding.ASCII.GetBytes(member+"\0")))]);
        }
        byte[] info=new byte[40];BinaryPrimitives.WriteUInt32LittleEndian(info,0x10000);
        if(kind==1)
        {
            Encoding.ASCII.GetBytes("TREE743").CopyTo(info,20);
            Encoding.ASCII.GetBytes(name).CopyTo(info,4);BinaryPrimitives.WriteUInt16LittleEndian(info.AsSpan(36),(ushort)members.Length);
            return Chunk(0x300,[..Chunk(0x301,info),..members.SelectMany(member=>
            {byte[] node=new byte[18];Encoding.ASCII.GetBytes(member).CopyTo(node,0);return Chunk(0x302,node);})]);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(4),1);Encoding.ASCII.GetBytes(name).CopyTo(info,8);
        Encoding.ASCII.GetBytes("TREE743").CopyTo(info,24);
        byte[] level=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(level,(uint)members.Length);
        return Chunk(0x700,[..Chunk(0x701,info),..Chunk(0x702,[..Chunk(0x703,level),..members.SelectMany(member=>
        {byte[] node=new byte[36];Encoding.ASCII.GetBytes(member).CopyTo(node,4);return Chunk(0x704,node);})])]);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ReadableWrongNameMemberPreservesFileEffectsAndNativeContinuation(int kind)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        string name="M"+Guid.NewGuid().ToString("N")[..8];
        string path=Path.Combine(Environment.CurrentDirectory,(kind==1?"NEST743":name)+".w3d");
        typeof(EnbRetailAssetBundle).GetField("<FileResources>k__BackingField",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .SetValue(assets,new EnbRetailFileResolver(Environment.CurrentDirectory,assets.RetailMixResources));
        File.WriteAllBytes(path,Legacy());
        try
        {
            int acquired=0;
            using var cache=Cache(()=>acquired++);var context=cache.CreatePublicationContext();
            context.Publish(Source(Composite(kind,"NEST743",[name])));
            context.Publish(Source(EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("OUT743","NULL",
                [("NEST743","ROOT"),("NEST743","ROOT"),("MS03A","ROOT")],25,0)));
            Assert.True(cache.TryFindPrototypeSource("OUT743",out var registered));
            assets.PresentationCatalog!.LoadBaseAssetsText("[BASE-31000]\nFILE=unused743.w3d\nNAME=OUT743\n");
            EnbCurrentRetailRObjEffectAsset.FactoryPreparation Prepare()=>EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,
                registeredFactorySource:registered,registeredPrototypes:cache.CapturePrototypeSources(out _),publicationContext:context);
            if(kind==3)
            {
                Assert.Throws<InvalidDataException>(Prepare);
                Assert.False(cache.TryFindPrototypeSource("MS03A",out _));
                Assert.Equal(1,acquired);
            }
            else
            {
                var prepared=Prepare();
                Assert.Equal(2,prepared.PrototypePublications.Count(source=>source.Model.ParticleEmitters.Count!=0));
                Assert.True(cache.TryFindPrototypeSource("MS03A",out _));
                File.Delete(path);Assert.True(prepared.Build().IsNullObject);
            }
            Assert.True(cache.TryFindPrototypeSource("LEGACY743",out _));
            Assert.False(cache.TryFindPrototypeSource(name,out _));
        }
        finally{File.Delete(path);}
    }

    [Fact]
    public void LazyAggregateRetainsActualWrapperAndBaseRegistrations()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-LazyAggregate743-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var wrapper=EnbR6Z11CurrentRetailAggregateTests.BuildAggregate("WRAP743","LEGACY743",[],25,0);
            File.WriteAllBytes(Path.Combine(directory,"WRAP743.w3d"),[..wrapper,..Legacy()]);
            using var cache=Cache();var context=cache.CreatePublicationContext();
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(new(directory,null),null,"WRAP743",
                out var source,out var error,context.CreateNamespace()),error);
            Assert.True(cache.TryFindPrototypeSource("WRAP743",out var registeredWrapper));
            Assert.True(cache.TryFindPrototypeSource("LEGACY743",out var registeredBase));
            Assert.Equal(registeredWrapper.PrototypeRegistration,source.PrototypeRegistration);
            Assert.Equal(registeredBase.PrototypeRegistration,source.CurrentRetailAggregate!.BasePrototypeRegistration);
            Assert.Equal(registeredBase.RegisteredPrototype,source.CurrentRetailAggregate.BaseRegisteredPrototype);
            Assert.Equal(1,context.PublicationCount);
        }
        finally{Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ConfiguredFactoryPublishesAuthoredFileOnceBeforeSelectionAndBuild(bool missingName)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-Legacy743-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"authored.w3d");File.WriteAllBytes(path,Legacy());
            assets.PresentationCatalog!.LoadBaseAssetsText($"[BASE-31000]\nFILE={path}\nNAME={(missingName?"ABSENT743":"LEGACY743")}\n");
            int acquired=0;
            using var cache=new EnbW3dRenderResourceCache(definition=>
            {
                if(definition.LogicalName=="legacy.tga")acquired++;
                return(definition.LogicalName.ToLowerInvariant(),new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0));
            });
            var context=cache.CreatePublicationContext();
            var prepared=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,31000,System.Numerics.Vector3.Zero,
                registeredPrototypes:cache.CapturePrototypeSources(out _),publicationContext:context);
            Assert.True(prepared.PublicationsApplied);Assert.Equal(1,acquired);
            Assert.True(cache.TryFindPrototypeSource("LEGACY743",out var retained));
            File.Delete(path);
            var built=prepared.Build();
            Assert.Equal(1,acquired);Assert.Equal(missingName,built.IsMissingFactoryObject);
            if(!missingName)
            {
                Assert.Equal(retained.PrototypeRegistration,prepared.Source!.PrototypeRegistration);
                Assert.NotNull(built.Particle);
            }
        }
        finally{Directory.Delete(directory,true);}
    }

    [Fact]
    public void LazyResolverBindsLegacyBeforeTestingNamePresence()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-LazyLegacy743-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path=Path.Combine(directory,"LEGACY743.w3d");File.WriteAllBytes(path,Legacy());
            using var cache=Cache();var context=cache.CreatePublicationContext();
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(new(directory,null),null,"LEGACY743",
                out var source,out var error,context.CreateNamespace()),error);
            Assert.Equal(1,context.PublicationCount);Assert.Single(source.Model.ParticleEmitters);
            Assert.True(source.PrototypeRegistration>0);File.Delete(path);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(new(directory,null),null,source,
                cache.CapturePrototypeSources(out _),out var retained,out error),error);
            Assert.Same(source.Model,retained.Model);
        }
        finally{Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(0,false)] [InlineData(1,false)] [InlineData(2,true)] [InlineData(3,false)]
    [InlineData(6,true)] [InlineData(7,true)] [InlineData(9,true)] [InlineData(10,true)]
    [InlineData(12,true)] [InlineData(15,true)] [InlineData(16,true)] [InlineData(20,false)]
    [InlineData(21,true)] [InlineData(22,true)] [InlineData(23,true)] [InlineData(24,true)]
    public void FileShaderCapturesCurrentFormatAndDoesNotFollowLaterSurfaceCompletion(uint format,bool alpha)
    {
        using var cache=Cache();
        using var texture=cache.AcquireNamedTexture("legacy.tga");
        texture.State.PublishSurfaceFormat(format,true);
        var source=Source(Legacy());
        Assert.True(source.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        Assert.Empty(source.Model.ParticleEmitters);
        var bound=cache.CreatePublicationContext().Publish(source);
        var definition=Assert.Single(bound.Model.ParticleEmitters);
        Assert.False(bound.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        Assert.Equal(alpha?(byte)5:(byte)1,definition.Shader.DestinationBlend);
        Assert.Equal(alpha?(byte)2:(byte)1,definition.Shader.SourceBlend);
        texture.State.PublishSurfaceFormat(alpha?0u:7u,true);
        Assert.True(cache.TryFindPrototypeSource("LEGACY743",out var retained));
        Assert.Same(definition,Assert.Single(retained.Model.ParticleEmitters));
        Assert.Equal(alpha?(byte)5:(byte)1,definition.Shader.DestinationBlend);
    }

    [Fact]
    public void OperationReplayDoesNotConstructAgainButFreshFileReloadDoes()
    {
        int acquisitions=0,cold=0;
        using var cache=Cache(()=>acquisitions++);
        var context=cache.CreatePublicationContext(initializeColdTexture:(_,state)=>{cold++;state.PublishSurfaceFormat(7,true);});
        var source=Source(Legacy());var bound=context.Publish(source);
        Assert.Same(bound,context.Publish(source));Assert.Same(bound,context.Publish(bound));
        Assert.Equal(1,context.PublicationCount);Assert.Equal(1,acquisitions);Assert.Equal(1,cold);
        Assert.True(cache.TryFindPrototypeSource("LEGACY743",out var first));
        var again=context.Publish(Source(Legacy()));
        Assert.Equal(2,context.PublicationCount);Assert.Equal(2,acquisitions);Assert.Equal(1,cold);
        Assert.True(cache.TryFindPrototypeSource("LEGACY743",out var same));
        Assert.Equal(first.PrototypeRegistration,same.PrototypeRegistration);
        Assert.Single(again.Model.ParticleEmitters);
    }

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("legacy.tga")]
    public void OptionalFailureStillAcquiresNonemptyNameAndDoesNotPublishPrototype(string texture)
    {
        int acquisitions=0;
        using var cache=Cache(()=>acquisitions++);
        var source=Source(Legacy(texture,Chunk(0x509,[1])));
        Assert.Single(source.PrototypeFile!.LegacyTextureConstructions);
        var bound=cache.CreatePublicationContext().Publish(source);
        Assert.Equal(texture.Length==0?0:1,acquisitions);
        Assert.True(bound.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        Assert.Empty(bound.Model.ParticleEmitters);
        Assert.False(cache.TryFindPrototypeSource("LEGACY743",out _));
    }

    [Fact]
    public void ContextlessPublicationKeepsLegacyUnresolved()
    {
        var source=Source(Legacy());using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryPublishFile(source,out var unchanged,out _,out var error),error);
        Assert.True(unchanged.PrototypeFile!.HasUnrepresentedRenderPrototypes);
        Assert.False(cache.TryFindPrototypeSource("LEGACY743",out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MeshAndLegacyAcquisitionsFollowFileRootOrder(bool meshFirst)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.True(assets.FileResources.TryRead("MS03A.w3d",out var meshBytes,out _,out _));
        var meshSource=Source(meshBytes);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(meshSource,out var meshTable,out var error),error);
        var meshTexture=meshTable.Meshes.SelectMany(mesh=>mesh.TextureAcquisitions).First(value=>value.Info is not null);
        byte[] particle=Legacy(meshTexture.LogicalName);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName.ToLowerInvariant(),
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),definition.Info is null?0u:7u)));
        var bound=cache.CreatePublicationContext().Publish(Source(meshFirst?[..meshBytes,..particle]:[..particle,..meshBytes]));
        var legacy=Assert.Single(bound.Model.ParticleEmitters,p=>p.Name=="LEGACY743");
        Assert.Equal(meshFirst?(byte)5:(byte)1,legacy.Shader.DestinationBlend);
    }

    private static EnbW3dRenderResourceCache Cache(Action? acquired=null)
        =>new(definition=>{acquired?.Invoke();return(definition.LogicalName.ToLowerInvariant(),
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0));});
    private static EnbResolvedW3dResource Source(byte[] bytes)
    {
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
        return new("legacy743.w3d","LEGACY743",new("legacy743.w3d","absent743",0,bytes.Length),file.Model,[]){PrototypeFile=file};
    }
    private static byte[] Legacy(string texture="legacy.tga",params byte[][] extra)
    {
        byte[] header=new byte[20];BinaryPrimitives.WriteUInt32LittleEndian(header,0x10000);
        Encoding.ASCII.GetBytes("LEGACY743").CopyTo(header,4);
        byte[] info=new byte[332];Encoding.ASCII.GetBytes(texture).CopyTo(info,0);
        foreach(var (offset,value) in new[]{(260,4f),(264,99f),(268,1f),(272,10f),(280,7f),(284,2000f),(288,.5f)})
            BinaryPrimitives.WriteSingleLittleEndian(info.AsSpan(offset),value);
        new byte[]{10,20,30,255,40,50,60,0}.CopyTo(info,324);
        return Chunk(0x500,new[]{Chunk(0x501,header),Chunk(0x502,new byte[12]),Chunk(0x503,info)}.Concat(extra).SelectMany(x=>x).ToArray());
    }
    private static byte[] Chunk(uint type,byte[] payload)
    {
        var bytes=new byte[8+payload.Length];BinaryPrimitives.WriteUInt32LittleEndian(bytes,type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4),(uint)payload.Length);payload.CopyTo(bytes,8);return bytes;
    }
}
