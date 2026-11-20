using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z11CurrentRetailAggregateTests
{
    [Fact]
    public void HeaderOnlyAggregateRetainsPublicationAfterUnknownChunkSeeksPastEnd()
    {
        byte[] bytes = new byte[45];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x600);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0x80000024);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x601);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 20);
        Encoding.ASCII.GetBytes("WRAPPER").CopyTo(bytes, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 0x60200);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 107520);
        Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(bytes,out var file,out var error),error);
        Assert.False(file.HasOtherUnrepresentedRenderPrototypes);
        var aggregate=Assert.Single(file.Entries).Aggregate!;
        Assert.Equal("WRAPPER",aggregate.Name);
        Assert.Equal(string.Empty,aggregate.BaseModelName);
        Assert.Empty(aggregate.Attachments);
        Assert.Equal(25u,aggregate.OriginalClassId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 0x602);
        Assert.False(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes.AsSpan(0,44),out _,out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateModelChildUsesFullDottedFileAfterOrdinaryLookupFails(bool readablePrimary)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            if(readablePrimary)File.WriteAllBytes(Path.Combine(directory,"MODEL.w3d"),BuildMinimalW3d("UNRELATED","wrong.tga"));
            string path=Path.Combine(directory,"MODEL.CHILD.w3d");
            File.WriteAllBytes(path,BuildMinimalW3d("MODEL.CHILD","right.tga"));
            var files=new EnbRetailFileResolver(directory,null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,"MODEL.CHILD",out var child,out var error),error);
            Assert.Equal(path,child.LooseFilePath);
            Assert.Equal("right.tga",Assert.Single(child.Model.Meshes.Single().TextureNames));
            bool ordinary=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"MODEL.CHILD",out var primary,out _);
            Assert.Equal(readablePrimary,ordinary);
            if(ordinary)Assert.Equal("UNRELATED",primary.Model.Meshes.Single().Name);
            var registered=new Dictionary<string,EnbResolvedW3dResource>(StringComparer.OrdinalIgnoreCase)
            { ["MODEL.CHILD"]=child with {PrototypeRegistration=42} };
            File.Delete(path);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,"model.child",out var retained,out error,registered),error);
            Assert.Equal(42,retained.PrototypeRegistration);
            Assert.Equal("model.child",retained.RequestedObjectName);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void AggregateBaseUsesCapturedRegistryBeforeItsOwnFileAndRetainsExactRegistration()
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            Assert.True(EnbW3dModel.TryDecode([..BuildMinimalW3d("UNRELATED","unused.tga"),..BuildMinimalW3d("BASE","first.tga")],out var model));
            var location=new EnbMixResourceLocation("ORIGIN.w3d",Path.Combine(directory,"ORIGIN.w3d"),0,100);
            var source=new EnbResolvedW3dResource("ORIGIN.w3d","UNRELATED",location,model,[]);
            using var cache=new EnbW3dRenderResourceCache();
            Assert.True(cache.TryLoadFile(source,out _,out var error),error);
            var first=cache.CapturePrototypeSources(out long firstVersion);
            Assert.Same(first,cache.CapturePrototypeSources(out _));
            Assert.Equal("BASE",first["BASE"].RequestedObjectName);
            string root=Path.Combine(directory,"ROOT.w3d");
            File.WriteAllBytes(root,[..BuildAggregate("ROOT","BASE",[],25,0),..BuildMinimalW3d("BASE","local.tga")]);
            var files=new EnbRetailFileResolver(directory,null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",out var resolved,out error,first),error);
            Assert.Equal(location,resolved.CurrentRetailAggregate!.BaseResourceLocation);
            Assert.Equal(first["BASE"].RegisteredPrototype,resolved.CurrentRetailAggregate.BaseRegisteredPrototype);
            Assert.Equal(first["BASE"].PrototypeRegistration,resolved.CurrentRetailAggregate.BasePrototypeRegistration);
            Assert.Equal("first.tga",Assert.Single(resolved.Model.Meshes[1].TextureNames));
            Assert.True(cache.RemovePrototype("BASE"));
            var removed=cache.CapturePrototypeSources(out long removedVersion);
            Assert.True(removedVersion>firstVersion);Assert.False(removed.ContainsKey("BASE"));Assert.True(first.ContainsKey("BASE"));
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",out var local,out error,removed),error);
            Assert.Equal(root,local.CurrentRetailAggregate!.BaseResourceLocation.ArchivePath);
            Assert.Equal("local.tga",Assert.Single(local.Model.Meshes.Single().TextureNames));
            Assert.True(cache.TryLoadFile(source,out _,out error),error);
            var replacement=cache.CapturePrototypeSources(out long replacementVersion);
            Assert.True(replacementVersion>removedVersion);
            Assert.NotEqual(first["BASE"].PrototypeRegistration,replacement["BASE"].PrototypeRegistration);
            Assert.Equal(first["UNRELATED"].PrototypeRegistration,replacement["UNRELATED"].PrototypeRegistration);
            Assert.True(cache.TryLoadFile(source,out _,out error),error);
            Assert.Same(replacement,cache.CapturePrototypeSources(out long duplicateVersion));
            Assert.Equal(replacementVersion,duplicateVersion);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public void NestedAggregateLoadsRetainEarlierFilePublications(bool aggregateBase,bool duplicate)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            byte[] original=aggregateBase
                ?[..BuildAggregate("BASE","LEAF",[],25,0),..BuildMinimalW3d("LEAF","original.tga")]
                :BuildMinimalW3d("BASE","original.tga");
            string first=Path.Combine(directory,"ROOT.w3d");
            File.WriteAllBytes(first,[..BuildAggregate("ROOT","INNER",[],25,0),..original]);
            byte[] later=duplicate?BuildMinimalW3d("BASE","replacement.tga"):[];
            File.WriteAllBytes(Path.Combine(directory,"INNER.w3d"),[..BuildAggregate("INNER","BASE",[],25,0),..later]);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(new(directory,null),null,"ROOT",out var source,out var error),error);
            Assert.Equal(aggregateBase?"LEAF":"BASE",source.CurrentRetailAggregate!.BaseRequestedObjectName);
            Assert.Equal(first,source.CurrentRetailAggregate.BaseResourceLocation.ArchivePath);
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out error),error);
            Assert.Equal(aggregateBase?"LEAF":"BASE",Assert.Single(compiled.Meshes).Name);
            Assert.Contains(source.Model.Meshes.Single().TextureNames,name=>name=="original.tga");
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateBasesUseEarlierOrLaterPrototypesInTheSameFile(bool baseFirst)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            byte[] wrappers=[..BuildAggregate("ROOT","INNER",[],25,0),..BuildAggregate("INNER","BASE",[],25,1)];
            byte[] model=BuildMinimalW3d("BASE","base.tga");
            string path=Path.Combine(directory,"ROOT.w3d");
            File.WriteAllBytes(path,baseFirst?[..model,..wrappers]:[..wrappers,..model]);
            var files=new EnbRetailFileResolver(directory,null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",out var source,out var error),error);
            Assert.Equal("BASE",source.CurrentRetailAggregate!.BaseRequestedObjectName);
            Assert.Equal(path,source.CurrentRetailAggregate.BaseResourceLocation.ArchivePath);
            Assert.Equal("INNER",source.CurrentRetailAggregate.InheritedAggregate!.Definition.Name);
            Assert.False(source.CurrentRetailAggregate.RenderDefinition.ForceSubObjectLod);
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source,out var compiled,out error),error);
            Assert.Equal("BASE",Assert.Single(compiled.Meshes).Name);
            File.WriteAllBytes(path,[..BuildAggregate("ROOT","INNER",[],25,0),..BuildAggregate("INNER","ROOT",[],25,0)]);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",out _,out error));
            Assert.Contains("recursive",error);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateAndOrdinaryDuplicateNamesRetainFirstFileRegistration(bool aggregateFirst)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            byte[] aggregate=BuildAggregate("ROOT","BASE",[],25,0);
            byte[] ordinary=BuildMinimalW3d("ROOT","ordinary.tga");
            byte[] model=BuildMinimalW3d("BASE","base.tga");
            File.WriteAllBytes(Path.Combine(directory,"ROOT.w3d"),aggregateFirst
                ?[..aggregate,..ordinary,..model]:[..ordinary,..aggregate,..model]);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(new(directory,null),null,"ROOT",out var source,out var error),error);
            Assert.Equal(aggregateFirst,source.CurrentRetailAggregate is not null);
            if(aggregateFirst)Assert.Equal("BASE",source.CurrentRetailAggregate!.BaseRequestedObjectName);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthoredBaseAssetUsesLooseProviderForRootAndAggregateBase(bool packagesAvailable)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            if (packagesAvailable)
                WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                    ("AUTHORED.w3d", BuildMinimalW3d("PACKAGED", "wrong.tga")));
            var packages = packagesAvailable ? EnbMixResourceCatalog.Load(directory) : null;
            string wrapper = Path.Combine(directory, "AUTHORED.w3d");
            File.WriteAllBytes(wrapper, BuildAggregate("ROOT", "BASE.PART", [], 25, 0));
            File.WriteAllBytes(Path.Combine(directory, "BASE.w3d"), BuildMinimalW3d("OTHER", "wrong.tga", "BASE"));
            File.WriteAllBytes(Path.Combine(directory, "BASE.PART.w3d"), BuildMinimalW3d("PART", "right.tga", "BASE"));
            var catalog = new EnbRetailPresentationCatalog();
            catalog.LoadBaseAssetsText("[BASE-42]\nFILE=AUTHORED.w3d\nNAME=ROOT\n");
            var resolver = new EnbRetailBaseAssetResolver(catalog, packages,
                files: new EnbRetailFileResolver(directory, packages));
            var result = resolver.Resolve(42);
            Assert.Equal(EnbBaseAssetResolutionStatus.W3d, result.Status);
            Assert.Equal(wrapper, result.W3d!.LooseFilePath);
            Assert.Equal(wrapper, result.W3d.ResourceLocation.ArchivePath);
            Assert.Equal(Path.Combine(directory, "BASE.PART.w3d"), result.W3d.CurrentRetailAggregate!.BaseResourceLocation.ArchivePath);
            Assert.Equal(Path.Combine(directory, "BASE.PART.w3d"), result.W3d.CurrentRetailAggregate.BaseLooseFilePath);
            Assert.Equal("PART", Assert.Single(result.W3d.Model.Meshes).Name);
            //A readable malformed loose file shadows the valid package too.
            File.WriteAllBytes(wrapper, [1, 2, 3]);
            Assert.Equal(EnbBaseAssetResolutionStatus.InvalidW3d, resolver.Resolve(42).Status);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("mesh")]
    [InlineData("aggregate")]
    [InlineData("undecodable")]
    public void AggregateBaseRetriesAfterReadableFileDoesNotPublishRequestedPrototype(string kind)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "ROOT.w3d"), BuildAggregate("ROOT", "BASE.PART", [], 25, 0));
            File.WriteAllBytes(Path.Combine(directory, "BASE.w3d"), kind switch
            {
                "mesh" => BuildMinimalW3d("OTHER", "wrong.tga", "BASE"),
                "aggregate" => BuildAggregate("UNRELATED", "MISSING", [], 25, 0),
                _ => Chunk(0x7ffffffe, [1, 2, 3], false),
            });
            File.WriteAllBytes(Path.Combine(directory, "BASE.PART.w3d"), BuildMinimalW3d("PART", "right.tga", "BASE"));
            var files = new EnbRetailFileResolver(directory, null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "ROOT", out var source, out var error), error);
            Assert.Equal(Path.Combine(directory, "BASE.PART.w3d"), source.CurrentRetailAggregate!.BaseResourceLocation.ArchivePath);
            Assert.Equal("PART", Assert.Single(source.Model.Meshes).Name);
            Assert.Equal("right.tga", Assert.Single(Assert.Single(source.Model.Meshes).TextureNames));
            Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var compiled, out error), error);
            Assert.Equal("BASE.PART", EnbW3dRenderObjectNameSemantics.RegisteredMeshName(Assert.Single(compiled.Meshes)));
            //The ordinary manager still does not use this aggregate-only path.
            bool ordinary = EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "BASE.PART", out var ordinarySource, out _);
            if (ordinary) Assert.Equal("OTHER", Assert.Single(ordinarySource.Model.Meshes).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AggregateBaseRetriesFullDottedWorkingDirectoryFileAfterLazyMiss()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "ROOT.w3d"), BuildAggregate("ROOT", "BASE.PART", [], 25, 0));
            File.WriteAllBytes(Path.Combine(directory, "BASE.PART.w3d"), BuildMinimalW3d("PART", "base.tga", "BASE"));
            var files = new EnbRetailFileResolver(directory, null);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null, "BASE.PART", out _, out _));
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null, "ROOT", out var source, out var error), error);
            Assert.Equal(Path.Combine(directory, "BASE.PART.w3d"), source.CurrentRetailAggregate!.BaseResourceLocation.ArchivePath);
            Assert.Equal("PART", Assert.Single(source.Model.Meshes).Name);
            File.WriteAllBytes(Path.Combine(directory, "BASE.w3d"), BuildMinimalW3d("PART", "ordinary.tga", "BASE"));
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null, "ROOT", out source, out error), error);
            Assert.Equal(Path.Combine(directory, "BASE.w3d"), source.CurrentRetailAggregate!.BaseResourceLocation.ArchivePath);
            Assert.Equal("ordinary.tga", Assert.Single(Assert.Single(source.Model.Meshes).TextureNames));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AggregateCannotCreateFromReadableFilesWithoutItsBasePrototype(bool fullNameFile)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory,"ROOT.w3d"),BuildAggregate("ROOT","BASE.PART",[],25,0));
            File.WriteAllBytes(Path.Combine(directory,"BASE.w3d"),BuildMinimalW3d("OTHER","first.tga","BASE"));
            if(fullNameFile)File.WriteAllBytes(Path.Combine(directory,"BASE.PART.w3d"),BuildMinimalW3d("UNRELATED","second.tga","BASE"));
            var files=new EnbRetailFileResolver(directory,null);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",
                out _,out var error,out var publications));
            Assert.Contains("BASE.PART",error);
            Assert.Equal(fullNameFile?4:2,publications.Count);
            if(fullNameFile)Assert.Equal(new[]{"ROOT.w3d","BASE.w3d","BASE.PART.w3d","BASE.w3d"},
                publications.Select(value=>Path.GetFileName(value.LooseFilePath)));
            var cache=new EnbW3dRenderResourceCache();
            foreach(var publication in publications)Assert.True(cache.TryLoadFile(publication,out _,out var why,0),why);
            Assert.True(cache.TryFindPrototypeSource("ROOT",out _));
            Assert.True(cache.TryFindPrototypeSource("BASE.OTHER",out _));
            Assert.False(cache.TryFindPrototypeSource("BASE.PART",out _));
            Assert.Equal(fullNameFile,cache.TryFindPrototypeSource("BASE.UNRELATED",out _));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData(false,true)] [InlineData(true,true)]
    [InlineData(false,false)] [InlineData(true,false)]
    public void AggregateRetriesParentBaseAfterNestedCreationFailure(bool registeredMiddle,bool suppliesLeaf)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory,"ROOT.w3d"),BuildAggregate("ROOT","MID.PART",[],25,0));
            byte[] middle=BuildAggregate("MID.PART","LEAF",[],25,0);
            File.WriteAllBytes(Path.Combine(directory,"MID.w3d"),middle);
            File.WriteAllBytes(Path.Combine(directory,"MID.PART.w3d"),[
                ..BuildAggregate("MID.PART","WRONG",[],25,0),
                ..BuildMinimalW3d(suppliesLeaf?"LEAF":"UNRELATED","leaf.tga")]);
            IReadOnlyDictionary<string,EnbResolvedW3dResource>? names=null;
            if(registeredMiddle)
            {
                Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(middle,out var file,out var why),why);
                names=new Dictionary<string,EnbResolvedW3dResource>(StringComparer.OrdinalIgnoreCase)
                { ["MID.PART"]=new("retained.w3d","MID.PART",new("retained","fixture",0,middle.Length),file.Model,[])
                    {PrototypeFile=file,AggregatePrototype=Assert.Single(file.Entries),PrototypeRegistration=720} };
            }
            var files=new EnbRetailFileResolver(directory,null);
            bool found=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",
                out var source,out var error,out var publications,names);
            Assert.Equal(suppliesLeaf,found);
            Assert.Equal(registeredMiddle?2:3,publications.Count);
            Assert.Equal(Path.Combine(directory,"MID.PART.w3d"),publications[^1].LooseFilePath);
            if(found)
            {
                Assert.Equal("LEAF",source.CurrentRetailAggregate!.BaseRequestedObjectName);
                Assert.Equal("leaf.tga",Assert.Single(Assert.Single(source.Model.Meshes).TextureNames));
                Assert.Equal("MID.PART",source.CurrentRetailAggregate.InheritedAggregate!.Definition.Name);
            }
            else Assert.NotNull(error);
            bool childFound=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,"MID.PART",out var child,out var childError,names);
            Assert.Equal(suppliesLeaf,childFound);
            if(childFound)Assert.Equal("LEAF",child.CurrentRetailAggregate!.BaseRequestedObjectName);
            else Assert.NotNull(childError);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData("NULL")] [InlineData("NuLl")]
    public void AggregateBuiltinNullBasePrecedesFilesAndRegisteredNames(string baseName)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            byte[] wrong=BuildMinimalW3d("NULL","wrong.tga");
            Assert.True(EnbW3dModel.TryDecode(wrong,out var wrongModel));
            var names=new Dictionary<string,EnbResolvedW3dResource>(StringComparer.OrdinalIgnoreCase)
            { ["NULL"]=new("wrong.w3d","NULL",new("wrong","fixture",0,wrong.Length),wrongModel,[])
                {RegisteredPrototype=Assert.Single(wrongModel.PrototypeEntries),PrototypeRegistration=721} };
            File.WriteAllBytes(Path.Combine(directory,"ROOT.w3d"),BuildAggregate("ROOT",baseName,[("CHILD","ROOT")],25,1));
            File.WriteAllBytes(Path.Combine(directory,"NULL.w3d"),wrong);
            var files=new EnbRetailFileResolver(directory,null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"ROOT",out var source,out var error,out var publications,names),error);
            Assert.Empty(source.Model.Meshes);
            Assert.Equal("NULL",Assert.Single(source.Model.NullObjects).Name);
            Assert.Equal("NULL",source.CurrentRetailAggregate!.BaseRequestedObjectName);
            Assert.Equal(EnbW3dPrototypeKind.NullObject,source.CurrentRetailAggregate.BaseRegisteredPrototype?.Kind);
            Assert.Equal(0,source.CurrentRetailAggregate.BasePrototypeRegistration);
            Assert.Single(publications);
            Assert.True(source.CurrentRetailAggregate.RenderDefinition.ForceSubObjectLod);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,baseName,out var child,out error,out var childPublications,names),error);
            Assert.Equal("NULL",Assert.Single(child.Model.NullObjects).Name);Assert.Empty(childPublications);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void PriorPartialPublicationDoesNotProveLaterNameMissing(bool acquisitionMetadata)
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            byte[] mixed=[..BuildMinimalW3d("KNOWN","known.tga"),0,5,0,0,0,0,0,128];
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(mixed,out var publication,out var error),error);
            var loaded=new EnbResolvedW3dResource("mixed.w3d","KNOWN",new("mixed","fixture",0,mixed.Length),publication.Model,[]){PrototypeFile=publication};
            var source=acquisitionMetadata?loaded with {PrototypeFile=null,AcquisitionPublications=[loaded]}:loaded;
            using var cache=new EnbW3dRenderResourceCache();
            Assert.True(cache.TryLoadFile(source,out _,out error,0),error);
            var names=cache.CapturePrototypeSources(out _);var files=new EnbRetailFileResolver(directory,null);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"MISSING",out _,out error,out _,out bool missing,names));
            Assert.False(missing);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,"MISSING",out _,out error,out _,out missing,names));
            Assert.False(missing);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAggregateChild(files,null,"KNOWN",out _,out error,names),error);
            byte[] wrapper=BuildAggregate("WRAPPER","MISSING",[],25,0);
            Assert.True(EnbCurrentRetailW3dPrototypeFile.TryDecode(wrapper,out var wrapperFile,out error),error);
            Assert.True(cache.TryLoadFile(new("wrapper.w3d","WRAPPER",new("wrapper","fixture",0,wrapper.Length),wrapperFile.Model,[])
                {PrototypeFile=wrapperFile},out _,out error,0),error);
            Assert.True(cache.TryFindPrototypeSource("WRAPPER",out var registered));
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveRegisteredSource(files,null,registered,cache.CapturePrototypeSources(out _),
                out _,out error,out _,out missing));
            Assert.False(missing);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void AuthoredResourceAcquisitionPreservesExactPrototypeName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] wrapper = BuildAggregate(" WRAPPER ", "BASE", [], 25, 0);
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                ("wrapper.w3d", wrapper), ("BASE.w3d", BuildMinimalW3d("BASE", "base.tga")));
            var catalog = EnbMixResourceCatalog.Load(directory)!;
            Assert.True(catalog.TryReadResource("wrapper.w3d", out var bytes, out var location));
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAuthoredResource(catalog,
                " wrapper.w3d ", " wrapper ", bytes, location, out var source, out var error), error);
            Assert.Equal(" wrapper.w3d ", source.LogicalFileName);
            Assert.Equal(" wrapper ", source.RequestedObjectName);
            Assert.Equal(" WRAPPER ", source.CurrentRetailAggregate!.Definition.Name);
            Assert.Equal("BASE", Assert.Single(source.Model.Meshes).Name);
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveAuthoredResource(catalog,
                "wrapper.w3d", "WRAPPER", bytes, location, out _, out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownTopLevelChunksDoNotPublishEmbeddedModelsOrParseTheirPayload(bool hint)
    {
        var visible = BuildMinimalW3d("VISIBLE", "visible.tga");
        var hidden = BuildMinimalW3d("HIDDEN", "hidden.tga");
        byte[] bytes = [..Chunk(0x7fff0101, hidden, hint), ..visible,
            ..Chunk(0x7fff0102, [0xff, 0xff, 0xff], hint)];
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        Assert.Equal("VISIBLE", Assert.Single(model.Meshes).Name);
        Assert.Single(model.PrototypeEntries);
        Assert.False(EnbW3dModel.TryDecode(Chunk(0x7fff0101, hidden, hint), out _));
        Assert.False(EnbW3dModel.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _));
    }

    [Fact]
    public void AggregateAttachmentCountIsBoundedByPayloadRatherThanAnArbitraryCeiling()
    {
        var attachments = Enumerable.Range(0, 65_537).Select(index => ($"CHILD{index}", "ROOT")).ToArray();
        var bytes = BuildAggregate("ROOT", "BASE", attachments, 25, 0);
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes, out var aggregate, out var error), error);
        Assert.Equal(65_537, aggregate.Attachments.Count);
        Assert.Equal("CHILD0", aggregate.Attachments[0].SubobjectName);
        Assert.Equal("CHILD65536", aggregate.Attachments[^1].SubobjectName);
        const int countOffset = 8 + 8 + 20 + 8 + 32;
        foreach (uint invalidCount in new[] { 65_538u, uint.MaxValue })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(countOffset), invalidCount);
            Assert.False(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes, out _, out error));
            Assert.NotNull(error);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AggregateRetainsEmptyAndSpaceBoneNamesForTheRenderConsumer(string boneName)
    {
        var bytes = BuildAggregate("ROOT", "BASE", [("CHILD", boneName)], 25, 0);
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes, out var aggregate, out var error), error);
        Assert.Equal(boneName, Assert.Single(aggregate.Attachments).BoneName);
        EnbW3dRenderPivot Pivot(int index, string name) => new(index, name, uint.MaxValue,
            default, default, Quaternion.Identity, Matrix4x4.Identity);
        var hierarchy = new EnbW3dRenderHierarchy(0, "tree", default, false,
            [Pivot(0, "ROOT"), Pivot(1, boneName), Pivot(2, boneName)], []);
        Assert.Equal(1, EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, boneName));
        hierarchy = hierarchy.Copy().SetPivots([Pivot(0, "ROOT"), Pivot(1, "OTHER")]);
        Assert.Equal(0, EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, boneName));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(256)]
    public void DeepAggregateBasesPreserveEveryWrapperAndContainCycles(int depth)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "BASE.w3d"), BuildMinimalW3d("BASE", "base.tga"));
            for (int index = 0; index < depth; index++)
                File.WriteAllBytes(Path.Combine(directory, $"W{index}.w3d"),
                    BuildAggregate($"W{index}", index + 1 == depth ? "BASE" : $"W{index + 1}",
                        [($"C{index}", "MOUNT")], 25, index == 0 ? 0u : 1u));
            var files = new EnbRetailFileResolver(directory, null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "W0", out var source, out var error), error);
            var root = Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(source.CurrentRetailAggregate);
            Assert.Equal("BASE", root.BaseRequestedObjectName);
            Assert.Equal(Path.Combine(directory, "BASE.w3d"), root.BaseResourceLocation.ArchivePath);
            Assert.Equal(Path.Combine(directory, "BASE.w3d"), root.BaseLooseFilePath);
            Assert.Equal(Path.Combine(directory, "W0.w3d"), source.LooseFilePath);
            Assert.False(root.RenderDefinition.ForceSubObjectLod);
            Assert.Equal(Enumerable.Range(0, depth).Reverse().Select(index => $"C{index}"),
                root.RenderDefinition.Attachments.Select(attachment => attachment.SubobjectName));
            var current = root;
            for (int index = 0; index < depth; index++)
            {
                Assert.NotNull(current);
                Assert.Equal($"W{index}", current.Definition.Name);
                current = current.InheritedAggregate;
            }
            Assert.Null(current);
            File.WriteAllBytes(Path.Combine(directory, $"W{depth - 1}.w3d"),
                BuildAggregate($"W{depth - 1}", "W0", [], 25, 0));
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "W0", out _, out error));
            Assert.Contains("recursive", error);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NestedBasePreservesAuthoredDefinitionsAndAppendsChildrenWithOuterFlags()
    {
        string directory=CreateTemporaryDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(directory,"BASE.w3d"),BuildMinimalW3d("BASE","base.tga"));
            File.WriteAllBytes(Path.Combine(directory,"INNER.w3d"),BuildAggregate("INNER","BASE",[("FIRST","A")],0,1));
            File.WriteAllBytes(Path.Combine(directory,"OUTER.w3d"),BuildAggregate("OUTER","INNER",[("SECOND","B")],0,0));
            var files=new EnbRetailFileResolver(directory,null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"OUTER",out var source,out var error),error);
            var owner=Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(source.CurrentRetailAggregate);
            Assert.Equal("INNER",owner.Definition.BaseModelName);
            Assert.Equal("SECOND",Assert.Single(owner.Definition.Attachments).SubobjectName);
            Assert.Equal(new[]{"FIRST","SECOND"},owner.RenderDefinition.Attachments.Select(a=>a.SubobjectName));
            Assert.False(owner.RenderDefinition.ForceSubObjectLod);
            Assert.True(owner.InheritedAggregate!.Definition.ForceSubObjectLod);
            Assert.Equal("BASE",owner.BaseRequestedObjectName);
            Assert.Equal(Path.Combine(directory,"BASE.w3d"),owner.BaseResourceLocation.ArchivePath);
            Assert.Equal(Path.Combine(directory,"OUTER.w3d"),source.LooseFilePath);
            File.WriteAllBytes(Path.Combine(directory,"INNER.w3d"),BuildAggregate("INNER","OUTER",[],0,1));
            Assert.False(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files,null,"OUTER",out _,out error));
            Assert.Contains("recursive",error);
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void LooseAggregateAndItsBaseRetainTheirOwnProviderLocations()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string release = Path.Combine(directory, "release");
            Directory.CreateDirectory(release);
            string wrapper = Path.Combine(release, "AGGROOT.w3d");
            string child = Path.Combine(directory, "BASEROOT.w3d");
            File.WriteAllBytes(wrapper, BuildAggregate("AGGROOT", "BASEROOT.CHILD", [], 0u, 0u));
            File.WriteAllBytes(child, BuildMinimalW3d("CHILD", "base.tga", "BASEROOT"));
            var files = new EnbRetailFileResolver(release, null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "AGGROOT", out var resolved, out var error), error);
            Assert.Equal(wrapper, resolved.LooseFilePath);
            Assert.Equal(wrapper, resolved.ResourceLocation.ArchivePath);
            var aggregate = Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(resolved.CurrentRetailAggregate);
            Assert.Equal("..\\BASEROOT.w3d", aggregate.BaseLogicalFileName);
            Assert.Equal(child, aggregate.BaseResourceLocation.ArchivePath);
            Assert.Equal("BASEROOT.CHILD", aggregate.BaseRequestedObjectName);
            Assert.Equal("CHILD", Assert.Single(resolved.Model.Meshes).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void AggregateBaseUsesTheSameFirstDotFilenameAndFullPrototypeName()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                ("AGGROOT.w3d", BuildAggregate("AGGROOT", "BASEROOT.CHILD", [], 0u, 0u)),
                ("BASEROOT.w3d", BuildMinimalW3d("CHILD", "base.tga", "BASEROOT")));
            var resources = EnbMixResourceCatalog.Load(directory)!;
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources, "AGGROOT", out var resolved, out var diagnostic), diagnostic);
            var aggregate = Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(resolved.CurrentRetailAggregate);
            Assert.Equal("BASEROOT.w3d", aggregate.BaseLogicalFileName);
            Assert.Equal("BASEROOT.CHILD", aggregate.BaseRequestedObjectName);
            Assert.Equal("CHILD", Assert.Single(resolved.Model.Meshes).Name);
            Assert.Equal("BASEROOT", Assert.Single(resolved.Model.Meshes).ContainerName);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void AggregateWithoutContainerHintStillDecodesAndKeepsLengthValidation()
    {
        var bytes = BuildAggregate("SH_FLSH1", "SH_ROOT", [("FLASH", "MOUNT")], 0u, 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes, out var decoded, out var error), error);
        Assert.Equal("SH_ROOT", decoded.BaseModelName);
        Assert.Equal("FLASH", Assert.Single(decoded.Attachments).SubobjectName);
        Assert.False(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
    }

    [Fact]
    public void AggregateDecoderPreservesExactHeaderBaseAttachmentsAndClassInfo()
    {
        byte[] bytes = BuildAggregate(
            "AGGROOT",
            "BASEROOT",
            [("CHILD_A", "BONE_A"), ("CHILD_B", "BONE_B")],
            originalClassId: 0x1234u,
            flags: 0u);

        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(
            bytes,
            out EnbCurrentRetailW3dAggregateDefinition aggregate,
            out string? diagnostic), diagnostic);
        Assert.Equal(0x0004_0000u, aggregate.Version);
        Assert.Equal("AGGROOT", aggregate.Name);
        Assert.Equal("BASEROOT", aggregate.BaseModelName);
        Assert.Equal(0x1234u, aggregate.OriginalClassId);
        Assert.Equal(0u, aggregate.Flags);
        Assert.False(aggregate.HasTextureReplacerInfo);
        Assert.Collection(
            aggregate.Attachments,
            first =>
            {
                Assert.Equal("CHILD_A", first.SubobjectName);
                Assert.Equal("BONE_A", first.BoneName);
            },
            second =>
            {
                Assert.Equal("CHILD_B", second.SubobjectName);
                Assert.Equal("BONE_B", second.BoneName);
            });
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void AggregateSkipsUnknownChildrenAndKnownTrailingBytes(bool trailing,bool containerHints)
    {
        var original=BuildAggregate("AGGROOT","BASEROOT",[("CHILD","MOUNT")],25,1);
        var children=new List<byte[]> {Chunk(0x5ff,[0xff,0xff,0xff],containerHints)};
        for(int offset=8;offset<original.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset));
            int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset+4))&0x7fffffff);
            byte[] payload=original.AsSpan(offset+8,length).ToArray();
            children.Add(Chunk(type,trailing?[..payload,0xaa,0xbb,0xcc]:payload,containerHints));
            children.Add(Chunk(0x605,[],containerHints));offset+=8+length;
        }
        var altered=ContainerChunk(0x600,children.ToArray());
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(altered,out var actual,out var error),error);
        Assert.Equal("AGGROOT",actual.Name);Assert.Equal("BASEROOT",actual.BaseModelName);
        Assert.Equal(25u,actual.OriginalClassId);Assert.True(actual.ForceSubObjectLod);
        Assert.Equal(new EnbCurrentRetailW3dAggregateAttachment("CHILD","MOUNT"),Assert.Single(actual.Attachments));
        Assert.False(EnbCurrentRetailW3dAggregateDecoder.TryDecode(altered.AsSpan(0,altered.Length-1),out _,out _));
    }

    [Fact]
    public void RepeatedAggregateRecordsOverwriteScalarsButAppendAttachmentPairs()
    {
        var first=BuildAggregate("FIRST","OLD",[("A","P1")],25,1);
        var second=BuildAggregate("LAST","NEW",[("B","P2")],17,0);
        var joined=Chunk(0x600,[..first.AsSpan(8).ToArray(),..second.AsSpan(8).ToArray()],true);
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(joined,out var actual,out var error),error);
        Assert.Equal("LAST",actual.Name);Assert.Equal("NEW",actual.BaseModelName);
        Assert.Equal(17u,actual.OriginalClassId);Assert.False(actual.ForceSubObjectLod);
        Assert.Equal(new[]{new EnbCurrentRetailW3dAggregateAttachment("A","P1"),new EnbCurrentRetailW3dAggregateAttachment("B","P2")},actual.Attachments);
        var noClass=Chunk(0x600,first.AsSpan(8,first.Length-8-28).ToArray(),true);
        Assert.True(EnbCurrentRetailW3dAggregateDecoder.TryDecode(noClass,out var defaults,out error),error);
        Assert.Equal(25u,defaults.OriginalClassId);Assert.Equal(0u,defaults.Flags);
    }

    [Theory]
    [InlineData(0x601u)] [InlineData(0x602u)] [InlineData(0x604u)]
    public void KnownAggregateRecordsStillRequireEveryRequestedByte(uint shortType)
    {
        var original=BuildAggregate("ROOT","BASE",[("CHILD","BONE")],25,0);
        var children=new List<byte[]>();
        for(int offset=8;offset<original.Length;)
        {
            uint type=BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset));
            int length=(int)(BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(offset+4))&0x7fffffff);
            children.Add(LeafChunk(type,original.AsSpan(offset+8,length-(type==shortType?1:0)).ToArray()));offset+=8+length;
        }
        Assert.False(EnbCurrentRetailW3dAggregateDecoder.TryDecode(ContainerChunk(0x600,children.ToArray()),out _,out _));
    }

    [Fact]
    public void LazyPrototypeResolverResolvesAggregateThroughAuthoredBaseAndRetainsWrapperMetadata()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("AGGROOT.w3d", BuildAggregate("AGGROOT", "BASEROOT", [("CHILD", "MOUNT")], 0u, 0u)),
                ("BASEROOT.w3d", BuildMinimalW3d("BASEROOT", "base.tga")),
                ("BASE.dds", [0x44, 0x44, 0x53, 0x20]));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");

            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources,
                "AGGROOT",
                out EnbResolvedW3dResource resolved,
                out string? diagnostic), diagnostic);

            Assert.Equal("AGGROOT.w3d", resolved.LogicalFileName, ignoreCase: true);
            Assert.Equal("AGGROOT", resolved.RequestedObjectName);
            Assert.Equal("BASEROOT", Assert.Single(resolved.Model.Meshes).Name);
            EnbCurrentRetailW3dAggregateResolution aggregate = Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(resolved.CurrentRetailAggregate);
            Assert.Equal("AGGROOT", aggregate.Definition.Name);
            Assert.Equal("BASEROOT", aggregate.BaseRequestedObjectName);
            Assert.Equal("BASEROOT.w3d", aggregate.BaseLogicalFileName, ignoreCase: true);
            EnbCurrentRetailW3dAggregateAttachment attachment = Assert.Single(aggregate.Definition.Attachments);
            Assert.Equal("CHILD", attachment.SubobjectName);
            Assert.Equal("MOUNT", attachment.BoneName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AggregateTextureReplacerIsRetailIgnoredWhileForceSubObjectLodIsRetained()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("TEXREP.w3d", BuildAggregate("TEXREP", "BASE", [], 0u, 0u, includeTextureReplacer: true)),
                ("FORCELOD.w3d", BuildAggregate("FORCELOD", "BASE", [], 0u, 1u)),
                ("BASE.w3d", BuildMinimalW3d("BASE", "base.tga")));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");

            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources,
                "TEXREP",
                out EnbResolvedW3dResource textureResolved,
                out string? textureDiagnostic), textureDiagnostic);
            Assert.True(Assert.IsType<EnbCurrentRetailW3dAggregateResolution>(textureResolved.CurrentRetailAggregate)
                .Definition.HasTextureReplacerInfo);

            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
                resources,
                "FORCELOD",
                out var lodResolved,
                out string? lodDiagnostic),lodDiagnostic);
            Assert.True(lodResolved.CurrentRetailAggregate!.Definition.ForceSubObjectLod);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DrawCompilerAcceptsExplicitAttachedWorldTransformWithoutReapplyingSceneTransform()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? compileDiagnostic), compileDiagnostic);
        EnbSceneObjectSnapshot sceneObject = SceneObject(scale: 9f);
        Matrix4x4 attachedWorld = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(7f, 8f, 9f);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(
            sceneObject,
            resource,
            attachedWorld,
            frameTimeMilliseconds: 123,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: null,
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        EnbW3dDrawTask packet = Assert.Single(drawList.Packets);
        Assert.Equal(new Vector3(27f, 48f, 69f), new Vector3(packet.WorldTransform.M41, packet.WorldTransform.M42, packet.WorldTransform.M43));
        Assert.NotEqual(new Vector3(120f, 240f, 360f), new Vector3(packet.WorldTransform.M41, packet.WorldTransform.M42, packet.WorldTransform.M43));
    }

    internal static byte[] BuildAggregate(
        string name,
        string baseModel,
        (string Subobject, string Bone)[] attachments,
        uint originalClassId,
        uint flags,
        bool includeTextureReplacer = false)
    {
        byte[] header = new byte[0x14];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0000u);
        WriteFixedAscii(header.AsSpan(4, 16), name);

        byte[] info = new byte[0x24 + (attachments.Length * 0x40)];
        WriteFixedAscii(info.AsSpan(0, 32), baseModel);
        BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(32, 4), checked((uint)attachments.Length));
        for (int index = 0; index < attachments.Length; index++)
        {
            int offset = 0x24 + (index * 0x40);
            WriteFixedAscii(info.AsSpan(offset, 32), attachments[index].Subobject);
            WriteFixedAscii(info.AsSpan(offset + 32, 32), attachments[index].Bone);
        }

        byte[] classInfo = new byte[0x14];
        BinaryPrimitives.WriteUInt32LittleEndian(classInfo.AsSpan(0, 4), originalClassId);
        BinaryPrimitives.WriteUInt32LittleEndian(classInfo.AsSpan(4, 4), flags);

        var children = new List<byte[]>
        {
            LeafChunk(0x0601, header),
            LeafChunk(0x0602, info),
        };
        if (includeTextureReplacer)
            children.Add(LeafChunk(0x0603, [1, 0, 0, 0, 0xA5, 0x5A, 0xC3, 0x3C]));
        children.Add(LeafChunk(0x0604, classInfo));
        return ContainerChunk(0x0600, children.ToArray());
    }

    private static byte[] BuildMinimalW3d(string meshName, string textureName, string containerName = "")
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), meshName);
        WriteFixedAscii(header.AsSpan(24, 16), containerName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1u);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(112, 4), BitConverter.SingleToInt32Bits(1.0f));

        byte[] vertex = new byte[12];
        WriteSingle(vertex.AsSpan(0, 4), 1.0f);
        WriteSingle(vertex.AsSpan(4, 4), 2.0f);
        WriteSingle(vertex.AsSpan(8, 4), 3.0f);

        return ContainerChunk(
            0x0000,
            LeafChunk(0x001F, header),
            LeafChunk(0x0002, vertex),
            ContainerChunk(
                0x0030,
                ContainerChunk(
                    0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes(textureName + "\0")))));
    }

    private static EnbSceneObjectSnapshot SceneObject(float scale)
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 7,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: scale,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R6Z11 fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100, 200, 300),
                    Orientation: Quaternion.Identity,
                    Velocity: Vector3.Zero,
                    Speed: 0,
                    Rotation: Vector3.Zero,
                    OrientationColumn0: Vector3.UnitX),
                Planet: null),
            BaseAssetReference: null,
            ClientBaseAssetReference: null,
            Decals: ImmutableArray<EnbSceneDecalSnapshot>.Empty,
            ColorizationEntries: ImmutableArray<EnbSceneColorizationSnapshot>.Empty,
            RenderStateLinks: ImmutableArray<EnbSceneRenderStateLinkSnapshot>.Empty,
            ActiveRenderStates: ImmutableArray<uint>.Empty,
            RenderStateActions: ImmutableArray<EnbSceneRenderStateActionSnapshot>.Empty,
            Attachments: ImmutableArray<EnbSceneAttachmentSnapshot>.Empty,
            NameDecal: null,
            SubParts: ImmutableArray<EnbSceneSubPartSnapshot>.Empty,
            Avatar: null,
            SpecialTargetEndpointId: 0,
            SpecialTargetTransition: 0);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), container: true);

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length);
        if (container)
            size |= 0x8000_0000u;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), size);
        payload.CopyTo(result, 8);
        return result;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void WriteMix1(string path, params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;
        uint dataLength = checked((uint)resources.Sum(resource => resource.Payload.Length));
        uint directoryOffset = checked(headerLength + dataLength);
        uint filenameTableOffset = checked(directoryOffset + 4u + ((uint)resources.Length * 12u));

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x3158494Du);
        writer.Write(directoryOffset);
        writer.Write(filenameTableOffset);
        writer.Write(0u);

        var offsets = new uint[resources.Length];
        uint cursor = headerLength;
        for (int index = 0; index < resources.Length; index++)
        {
            offsets[index] = cursor;
            writer.Write(resources[index].Payload);
            cursor = checked(cursor + (uint)resources[index].Payload.Length);
        }

        writer.Write((uint)resources.Length);
        for (int index = 0; index < resources.Length; index++)
        {
            writer.Write(0x1000_0000u + (uint)index);
            writer.Write(offsets[index]);
            writer.Write((uint)resources[index].Payload.Length);
        }

        writer.Write((uint)resources.Length);
        foreach ((string name, _) in resources)
        {
            byte[] storedName = Encoding.ASCII.GetBytes(name + "\0");
            writer.Write((byte)storedName.Length);
            writer.Write(storedName);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "EnbNext.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
