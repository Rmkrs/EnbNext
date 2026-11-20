using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Xunit.Trait("Category", "Heavy")] // retail/hardware verification, >=5 s; run with --filter Category=Heavy
public sealed class EnbRObjMeshMapperOwnerTests
{
    [Theory]
    [InlineData(1f)]
    [InlineData(1.7f)]
    public void CollectionBlocksHsvButCopiesGeometryWhenScaleRequiresIt(float scale)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName,
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(assets.BaseAssetResolver!.Resolve(1989).W3d!,out var source,out var error),error);
        var mesh=source.Meshes.First(m=>m.CurrentRetailLoadedGeometry?.Tree is not null);
        using var original=new EnbCurrentRetailRObjMeshMappers(source.Copy().SetMeshes([mesh]),100,null,cache,[mesh]);
        using var configured=original.ConfigureCollectionTree(200,assets.Textures,new(47,.2f,.1f),scale,[false]);
        var actual=Assert.Single(configured.Resource.Meshes);
        var oldOwner=original.CreateAppearance(mesh,[]).Owner;
        var owner=configured.CreateAppearance(actual,[]).Owner;
        if(scale==1)Assert.Same(oldOwner.ModelIdentity,owner.ModelIdentity);
        else Assert.NotSame(oldOwner.ModelIdentity,owner.ModelIdentity);
        var expected=mesh.CurrentRetailLoadedGeometry!.CopyModel();uint seed=123;
        if(scale!=1)expected.Scale(new(scale),ref seed);
        Assert.Equal(expected.Positions,actual.CurrentRetailLoadedGeometry!.Positions);
        Assert.Equal(mesh.Positions,mesh.CurrentRetailLoadedGeometry.Positions);
        Assert.Equal(scale,actual.CurrentRetailLoadedGeometryScale);
        for(int i=0;i<mesh.CurrentRetailLoadedMaterials.Count;i++)
        {
            var before=mesh.CurrentRetailLoadedMaterials[i];var after=actual.CurrentRetailLoadedMaterials[i];
            if(before is null){Assert.Null(after);continue;}
            Assert.Equal(before.Diffuse,after.Diffuse);
            Assert.Equal(EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(before),EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(after));
        }
    }

    [Fact]
    public void MixedCollectionCopiesDeferredMappersAfterHlodPreparationWalk()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var mapper=new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode10,"FPS=5\nUPerSec=.3\nVPerSec=.2",1,1,0,0,0,0,false);
        var meshes=Enumerable.Range(0,3).Select(i=>
        {
            var material=new EnbW3dRenderVertexMaterial(0,"random"+i,0,default,default,default,default,0,1,0,mapper,null);
            return new EnbW3dRenderMesh(0,"child"+i,"parent",1,[],[],[],[],[],[material],[]){CurrentRetailLoadedMaterials=[material]};
        }).ToArray();
        using var source=new EnbCurrentRetailRObjMeshMappers(new(default,"parent",meshes,[],[],[],[]),100);
        EnbW3dRetailMapperRandomSource.Process.Reseed(0x1105);
        var expected=new EnbCurrentRetailRObjMeshMappers[3];
        try
        {
            //48BE60 skips the middle Collection, preparing slots0/2 first.
            //991220->93EDD0 later forces the shared model copy for slot1.
            foreach(int index in new[]{0,2,1})
            {
                using var selected=source.RetainRenderObjects(source.Resource.Copy().SetMeshes([meshes[index]]));
                expected[index]=selected.Localize(200,false);
            }
            EnbW3dRetailMapperRandomSource.Process.Reseed(0x1105);
            using var actual=source.ConfigureCollectionTree(200,assets.Textures,default,1.7f,[true,false,true]);
            for(int i=0;i<3;i++)
                Assert.Equal(expected[i].Mappers.Values.Single().Stage0.Evaluate(201),
                    actual.Mappers[actual.Resource.Meshes[i].CurrentRetailLoadedMaterials[0]].Stage0.Evaluate(201));
        }
        finally{foreach(var item in expected)item?.Dispose();}
    }

    [Theory]
    [InlineData(1884)]
    [InlineData(1038)]
    [InlineData(1544)]
    [InlineData(1545)]
    [InlineData(2037)]
    [InlineData(2038)]
    [InlineData(2039)]
    public void AttachedFactoryRetainsRequestedWrapperName(int baseId)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var prepared=EnbCurrentRetailRObjEffectAsset.PrepareFactory(assets,baseId,default);
        var built=prepared.Build();
        Assert.NotEmpty(built.ModelAttachments);
        Assert.Equal(prepared.Source!.RequestedObjectName,built.FactoryObjectName,ignoreCase:true);
    }

    [Fact]
    public void AttachedFactoryCopiesAllRandomMappersBeforeResetLocalization()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var mapper=new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode10,"FPS=5\nUPerSec=.3\nVPerSec=.2",1,1,0,0,0,0,false);
        var sources=Enumerable.Range(0,3).Select(i=>
        {
            var material=new EnbW3dRenderVertexMaterial(0,"random"+i,0,default,default,default,default,0,1,0,mapper,null);
            var mesh=new EnbW3dRenderMesh(0,"child"+i,"parent",1,[],[],[],[],[],[material],[]){CurrentRetailLoadedMaterials=[material]};
            var hlod=new EnbW3dRenderHlod("parent"+i,"unused",[new(0,0,0,[new(0,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh))])]);
            return new EnbCurrentRetailRObjMeshMappers(new(default,"parent",[mesh],[],[],[],[hlod]),100);
        }).ToArray();
        try
        {
            EnbW3dRetailMapperRandomSource.Process.Reseed(0x1105);
            //48BE60 visits every child before006EA830 resets/localizes the
            //completed parent. Retain those individual reference copies.
            var reference=sources.Select(s=>s.Localize(200,false)).ToArray();
            var expected=reference.Select(s=>s.Localize(200)).ToArray();
            try
            {
                EnbW3dRetailMapperRandomSource.Process.Reseed(0x1105);
                using(var firstFactory=sources[0].Localize(200,false))
                using(var prematureReset=firstFactory.Localize(200))
                    Assert.NotEqual(expected[0].Mappers.Values.Single().Stage0.Evaluate(201),prematureReset.Mappers.Values.Single().Stage0.Evaluate(201));
                EnbW3dRetailMapperRandomSource.Process.Reseed(0x1105);
                var configured=EnbCurrentRetailRObjMeshMappers.ConfigureCompositeGroup(sources,200,assets.Textures,default,1.7f);
                try
                {
                    for(int i=0;i<configured.Count;i++)
                    {
                        using var local=configured[i].Localize(200);
                        Assert.Equal(expected[i].Mappers.Values.Single().Stage0.Evaluate(201),local.Mappers.Values.Single().Stage0.Evaluate(201));
                        Assert.NotSame(sources[i].CreateAppearance(sources[i].Resource.Meshes[0],[]).Owner.ModelIdentity,
                            configured[i].CreateAppearance(configured[i].Resource.Meshes[0],[]).Owner.ModelIdentity);
                    }
                }
                finally {foreach(var owner in configured)owner.Dispose();}
            }
            finally {foreach(var owner in reference.Concat(expected))owner.Dispose();}
        }
        finally {foreach(var owner in sources)owner.Dispose();}
    }

    [Fact]
    public void RetainedChildSubsetPreservesActualModelsAcrossParentRelease()
    {
        var material=new EnbW3dRenderVertexMaterial(0,"child",0,default,default,default,default,0,1,0,null,null);
        var first=new EnbW3dRenderMesh(0,"first","parent",1,[],[],[],[],[],[material],[])
            {CurrentRetailLoadedMaterials=[material]};
        var second=(first).Copy().SetName("second");
        var resource=new EnbW3dRenderResource(default,"parent",[first,second],[],[],[],[]);
        var parent=new EnbCurrentRetailRObjMeshMappers(resource,100);
        var identity=parent.CreateAppearance(second,[]).Owner.ModelIdentity;
        using var child=parent.RetainRenderObjects(resource.Copy().SetMeshes([second]));
        parent.Dispose();
        Assert.Same(identity,child.CreateAppearance(second,[]).Owner.ModelIdentity);
        Assert.Throws<ArgumentException>(()=>child.CreateAppearance(first,[]));
        using var localized=child.Localize(200);
        Assert.NotSame(identity,localized.CreateAppearance(localized.Resource.Meshes[0],[]).Owner.ModelIdentity);
        Assert.Throws<ArgumentException>(()=>child.RetainRenderObjects(resource));
    }

    [Fact]
    public void CompositeResetLocalizesRepeatedChildrenBeforeDelayedActivation()
    {
        var mapper=new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode11,"VStart=.35\nVPerSec=.2",1,1,0,0,0,0,false);
        var material=new EnbW3dRenderVertexMaterial(0,"phase",0,default,default,default,default,0,1,0,mapper,null);
        var mesh=new EnbW3dRenderMesh(0,"child","parent",1,[],[],[],[],[],[material],[])
            {CurrentRetailLoadedMaterials=[material]};
        var name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh);
        var hlod=new EnbW3dRenderHlod("parent","unused",[new(0,0,0,[new(0,name),new(1,name)])])
            {Aggregate=new(0,[new(2,name)])};
        var resource=new EnbW3dRenderResource(default,"parent",[mesh],[],[],[],[hlod]);
        using var source=new EnbCurrentRetailRObjMeshMappers(resource,100);
        var before=source.Mappers[material].Stage0.Evaluate(300);
        using var first=source.Localize(400,compositeOccurrences:true);
        using var second=source.Localize(500,compositeOccurrences:true);
        Assert.Equal(3,first.Resource.Meshes.Count);
        Assert.Equal(3,first.Mappers.Count);
        Assert.Equal(3,first.Mappers.Values.Select(p=>p.Stage0).Distinct().Count());
        Assert.All(first.Mappers.Values,p=>Assert.Equal(before,p.Stage0.Evaluate(400)));
        var expected=first.Mappers.Values.First().Stage0.CloneForMaterial(400);
        expected.Reset(900);
        first.Activate(900);
        Assert.All(first.Mappers.Values,p=>Assert.Equal(expected.Evaluate(1000),p.Stage0.Evaluate(1000)));
        Assert.All(second.Mappers.Values,p=>Assert.Equal(500u,p.Stage0.InitialMilliseconds));
        Assert.Equal(100u,source.Mappers[material].Stage0.InitialMilliseconds);
        Assert.All(first.Resource.Meshes,m=>Assert.NotEqual(0u,
            EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(m.CurrentRetailLoadedMaterials[0])));
    }

    [Fact]
    public void ReconfiguringRetainedMeshScalesItsExistingGeometryAndPreservesSource()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName,
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(assets.BaseAssetResolver!.Resolve(1989).W3d!,out var source,out var error),error);
        var mesh=source.Meshes.First(m=>m.CurrentRetailLoadedGeometry?.Tree is not null);
        var resource=source.Copy().SetMeshes([mesh]).SetHierarchicalLods([]).SetHierarchyModels([]);
        using var original=new EnbCurrentRetailRObjMeshMappers(resource,100,null,cache,[mesh]);
        using var first=original.Configure(200,assets.Textures,System.Numerics.Vector3.Zero,2);
        var firstPositions=first.Resource.Meshes[0].CurrentRetailLoadedGeometry!.Positions.ToArray();
        using var second=first.Configure(300,assets.Textures,System.Numerics.Vector3.Zero,3);
        var expected=mesh.CurrentRetailLoadedGeometry!.CopyModel();uint seed=123;
        expected.Scale(new(2),ref seed);expected.Scale(new(3),ref seed);
        var secondMesh=Assert.Single(second.Resource.Meshes);
        Assert.Equal(expected.Positions,secondMesh.CurrentRetailLoadedGeometry!.Positions);
        Assert.Equal(6,secondMesh.CurrentRetailLoadedGeometryScale);
        Assert.Equal(firstPositions,first.Resource.Meshes[0].CurrentRetailLoadedGeometry!.Positions);
        Assert.Equal(2,first.Resource.Meshes[0].CurrentRetailLoadedGeometryScale);
        Assert.Equal(mesh.Positions,mesh.CurrentRetailLoadedGeometry.Positions);
    }

    [Theory]
    [InlineData(1.7f, 0f)]
    [InlineData(1f, 47f)]
    [InlineData(1.7f, 47f)]
    public void ConfiguredCompositeCopiesEveryRepeatedMeshOccurrence(float scale, float hue)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName,
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(assets.BaseAssetResolver!.Resolve(1989).W3d!,out var source,out var error),error);
        var mesh=source.Meshes.First(m=>m.CurrentRetailLoadedGeometry?.Tree is not null);
        var name=EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh);
        var hlod=new EnbW3dRenderHlod("occurrences","unused",
            [new(0,0,0,[new(0,name),new(1,name)]),new(1,0,0,[new(0,name)])])
            {Aggregate=new(0,[new(0,name)])};
        var resource=source.Copy().SetMeshes([mesh]).SetHierarchicalLods([hlod]).SetHierarchyModels([]);
        using var original=new EnbCurrentRetailRObjMeshMappers(resource,100,null,cache,[mesh]);
        using var configured=original.Configure(200,assets.Textures,new(hue,0,0),scale,compositeOccurrences:true);
        Assert.Equal(4,configured.Resource.Meshes.Count);
        var identities=new HashSet<object>(ReferenceEqualityComparer.Instance);
        var geometries=new HashSet<object>(ReferenceEqualityComparer.Instance);
        uint seed=123;
        var expected=mesh.CurrentRetailLoadedGeometry!.CopyModel();
        if(scale!=1)expected.Scale(new(scale),ref seed);
        foreach(var occurrence in configured.Resource.Meshes)
        {
            var owner=configured.CreateAppearance(occurrence,[]).Owner;
            Assert.True(identities.Add(owner.ModelIdentity));
            Assert.True(geometries.Add(owner.Geometry!));
            Assert.NotSame(mesh.CurrentRetailLoadedGeometry,owner.Geometry);
            Assert.Same(owner.Geometry,occurrence.CurrentRetailLoadedGeometry);
            Assert.Equal(expected.Positions,owner.Geometry!.Positions);
            Assert.Equal(expected.Tree!.Nodes,owner.Geometry.Tree!.Nodes);
            Assert.Equal(expected.Tree.PolygonIndices,owner.Geometry.Tree.PolygonIndices);
            Assert.Equal(scale,occurrence.CurrentRetailLoadedGeometryScale);
        }
        var configuredHlod=Assert.Single(configured.Resource.HierarchicalLods);
        Assert.Equal(configured.Resource.Meshes.Select(EnbW3dRenderObjectNameSemantics.RegisteredMeshName),
            configuredHlod.Levels.SelectMany(level=>level.Members).Concat(configuredHlod.Aggregate!.Members).Select(m=>m.Name));
        Assert.Equal(mesh.Positions,mesh.CurrentRetailLoadedGeometry.Positions);
        Assert.Equal(name,hlod.Levels[0].Members[0].Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalizedAndHsvOwnersRetainCopiedLoadedGeometry(bool batched)
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        using var cache=new EnbW3dRenderResourceCache(definition=>(definition.LogicalName,
            new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(assets.BaseAssetResolver!.Resolve(1989).W3d!,out var resource,out var error),error);
        using var original=new EnbCurrentRetailRObjMeshMappers(resource,100,
            batched?new EnbCurrentRetailMeshBatchState():null,cache,resource.Meshes.Cast<EnbW3dRenderMesh?>().ToArray());
        using var local=original.Localize(200);
        using var hsv=original.Configure(300,assets.Textures,new(47,-.1f,-.2f));
        for(int index=0;index<resource.Meshes.Count;index++)
        {
            var source=resource.Meshes[index];var geometry=source.CurrentRetailLoadedGeometry!;
            Assert.NotNull(geometry.Tree);
            Assert.Same(geometry,original.CreateAppearance(source,[]).Owner.Geometry);
            foreach(var owner in new[]{local,hsv})
            {
                var mesh=owner.Resource.Meshes[index];
                var copied=owner.CreateAppearance(mesh,[]).Owner.Geometry!;
                Assert.NotNull(copied);Assert.NotSame(geometry,copied);
                Assert.Same(copied,mesh.CurrentRetailLoadedGeometry);
                Assert.NotSame(geometry.Tree,copied.Tree);
                Assert.Equal(geometry.Tree.Nodes,copied.Tree!.Nodes);
                Assert.Equal(geometry.Tree.PolygonIndices,copied.Tree.PolygonIndices);
                uint seed=123;
                copied.Scale(new(2),ref seed);
                Assert.Equal(123u,seed);
                Assert.Equal(source.Positions,geometry.Positions);
                Assert.Equal(source.Positions.Select(p=>p*2),copied.Positions);
            }
        }
    }

    [Fact]
    public void PendingPreparationCloneRetainsModelAndMapperUntilLastConsumerReleases()
    {
        var mapper=new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode04,"VPerSec=.2",1,1,0,0,.2f,0,false);
        var material=new EnbW3dRenderVertexMaterial(0,"pending",0,default,default,default,default,0,1,0,mapper,null);
        var mesh=new EnbW3dRenderMesh(0,"pending","pending",1,[],[],[],[],[],[material],[])
            {CurrentRetailLoadedMaterials=[material]};
        var resource=new EnbW3dRenderResource(default,"pending",[mesh],[],[],[],[]);
        var batches=new EnbCurrentRetailMeshBatchState();
        using var acquisition=new EnbCurrentRetailRObjMeshMappers(resource,100,batches);
        var identity=acquisition.CreateAppearance(mesh,[]).Owner.ModelIdentity;
        var container=batches.Containers.Register(identity,0x112,false,false,3,3);
        batches.Descriptors.GetOrAdd(identity,container,0,null,0,null,null);
        using var consumer=acquisition.RetainRenderObjects(resource.Copy().SetLogicalFileName("prepared"));
        Assert.Same(identity,consumer.CreateAppearance(mesh,[]).Owner.ModelIdentity);
        Assert.Same(acquisition.Mappers[material].Stage0,consumer.Mappers[material].Stage0);
        Assert.Equal(100u,consumer.Mappers[material].Stage0.InitialMilliseconds);
        acquisition.Dispose();
        Assert.True(batches.Descriptors.HasModelLinks(identity));
        Assert.Same(identity,consumer.CreateAppearance(mesh,[]).Owner.ModelIdentity);
        consumer.Dispose();
        Assert.False(batches.Descriptors.HasModelLinks(identity));
    }

    [Fact]
    public void PreparedRslRetainsTheWholeUnconfiguredLazyFileForSessionPublication()
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        int id=assets.PresentationCatalog!.FindEffect(369)!.BaseAssetId!.Value;
        var asset=EnbCurrentRetailRObjEffectAsset.Load(assets,id,new(47,-.1f,-.2f));
        var source=Assert.IsType<EnbResolvedW3dResource>(asset.LazyPrototypeSource);
        Assert.Null(source.CurrentRetailAggregate);
        using var cache=new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source,out var published,out var error,123),error);
        Assert.True(cache.TryFindPrototypeSource(source.RequestedObjectName,out _));
        foreach(var mesh in asset.UnconfiguredMeshResource!.Meshes)
        {
            Assert.True(cache.TryFindPrototypeSource(EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh),out _));
            var original=published.Meshes.Single(m=>m.Name==mesh.Name && m.ContainerName==mesh.ContainerName);
            Assert.All(original.CurrentRetailLoadedMaterials,m=>Assert.Null(m.CurrentRetailUniformOverride));
        }
    }

    [Theory]
    [InlineData(EnbW3dMapperTypeSelector.Mode04)]
    [InlineData(EnbW3dMapperTypeSelector.Mode10)]
    [InlineData(EnbW3dMapperTypeSelector.Mode11)]
    public void HsvConfigurationRetainsMappersOutsideAnEmptyLoadedTable(EnbW3dMapperTypeSelector selector)
    {
        if(EnbInstalledAssetLocator.TryLocate() is not {} paths)return;
        var assets=EnbRetailAssetBundle.Load(paths);
        var mapper=new EnbW3dRenderMapper(selector,"VStart=.35\nVPerSec=.2",1,1,.25f,0,.2f,0,false);
        var material=new EnbW3dRenderVertexMaterial(0,"descriptor",0,default,default,default,default,0,1,0,mapper,null);
        var pass=new EnbW3dRenderMaterialPass(0,[new(0,material)],EnbW3dRenderBindingCardinality.Uniform,[],EnbW3dRenderBindingCardinality.Uniform,[],[],[],[],[],null)
            {CurrentRetailUniformMaterial=new(0,material)};
        var mesh=new EnbW3dRenderMesh(0,"descriptor","descriptor",1,[],[],[],[],[],[material],[pass]);
        var resource=new EnbW3dRenderResource(default,"descriptor",[mesh],[],[],[],[]);
        using var source=new EnbCurrentRetailRObjMeshMappers(resource,100);
        var original=source.Mappers[material];original.Stage0.Evaluate(600);
        using var variant=source.Configure(900,assets.Textures,new(47,-.1f,-.2f));
        var adjusted=Assert.Single(variant.Resource.Meshes);
        Assert.Empty(adjusted.CurrentRetailLoadedMaterials);
        Assert.Same(material,Assert.Single(Assert.Single(adjusted.MaterialPasses).VertexMaterials).Material);
        Assert.Same(original.Stage0,variant.Mappers[material].Stage0);
        Assert.Same(original.Stage1,variant.Mappers[material].Stage1);
        Assert.Equal(100u,variant.Mappers[material].Stage0.InitialMilliseconds);
        variant.Activate(1200);
        Assert.Equal(100u,original.Stage0.InitialMilliseconds); // Reset scans only the empty loaded table.
    }

    [Fact]
    public void AppearanceUsesRetainedModelAndLocalizedRetirementRemovesOnlyItsBatchLinks()
    {
        var mesh = new EnbW3dRenderMesh(0, "owner", "owner", 1, [], [], [], [], [], [], []);
        var resource = new EnbW3dRenderResource(default, "owner", [mesh], [], [], [], []);
        var batches = new EnbCurrentRetailMeshBatchState();
        using var source = new EnbCurrentRetailRObjMeshMappers(resource, 10, batches);
        using var local = source.Localize(20);
        var first = source.CreateAppearance(mesh, []);
        Assert.Same(first.Owner, source.CreateAppearance(mesh, []).Owner);
        var copied = local.CreateAppearance(local.Resource.Meshes[0], []);
        Assert.NotSame(first.Owner.ModelIdentity, copied.Owner.ModelIdentity);
        object sourceId = first.Owner.ModelIdentity, localId = copied.Owner.ModelIdentity;
        foreach (var identity in new[] { sourceId, localId })
        {
            var container = batches.Containers.Register(identity, 0x112, false, false, 3, 3);
            batches.Descriptors.GetOrAdd(identity, container, 0, null, 0, null, null);
        }
        local.Dispose();
        Assert.False(batches.Descriptors.HasModelLinks(localId));
        Assert.True(batches.Descriptors.HasModelLinks(sourceId));
        batches.ResetBuffers();
        Assert.Same(sourceId, source.CreateAppearance(mesh, []).Owner.ModelIdentity);
        Assert.Throws<ArgumentException>(() => source.CreateAppearance((mesh).Copy().SetName("foreign"), []));
    }

    [Fact]
    public void HsvConfigurationCopiesDuplicateLoadedEntriesBeforeColorWrites()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var mapper = new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode04, "", 1, 1, .25f, 0, .2f, 0, false);
        var material = new EnbW3dRenderVertexMaterial(0, "color", 0, new(255, 0, 0, 0), new(255, 0, 0, 0), default, default, 0, 1, 0, mapper, null);
        var mesh = new EnbW3dRenderMesh(0, "color", "color", 1, [], [], [], [], [], [material], [])
        { CurrentRetailLoadedMaterials = [material, material] };
        var resource = new EnbW3dRenderResource(default, "color", [mesh], [], [], [], []);
        using var source = new EnbCurrentRetailRObjMeshMappers(resource, 100);
        var shift = new System.Numerics.Vector3(47, -.1f, -.2f);
        using var variant = source.Configure(900, assets.Textures, shift);
        var loaded = variant.Resource.Meshes[0].CurrentRetailLoadedMaterials;
        Assert.NotSame(loaded[0], loaded[1]);
        Assert.NotSame(variant.Mappers[loaded[0]].Stage0, variant.Mappers[loaded[1]].Stage0);
        var expected = EnbCurrentRetailHsvRuntime.ShiftRgb(System.Numerics.Vector3.UnitX, shift);
        Assert.All(loaded, entry => Assert.Equal(expected, entry.CurrentRetailUniformOverride!.Value.Diffuse));
        Assert.NotEqual(EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(loaded[0]), EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(loaded[1]));
        Assert.Null(material.CurrentRetailUniformOverride);
        Assert.Equal(100u, source.Mappers[material].Stage0.InitialMilliseconds);
    }

    [Fact]
    public void ScaleConfigurationCopiesTheCurrentEnvironmentPhaseWithoutMarkingMaterialLocal()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var mapper = new EnbW3dRenderMapper(EnbW3dMapperTypeSelector.Mode11, "VStart=.35\nVPerSec=.2", 1, 1, 0, 0, 0, 0, false);
        var material = new EnbW3dRenderVertexMaterial(0, "phase", 0, default, default, default, default, 0, 1, 0, mapper, null);
        var mesh = new EnbW3dRenderMesh(0, "phase", "phase", 1, [], [], [], [], [], [material], [])
        { CurrentRetailLoadedMaterials = [material] };
        var resource = new EnbW3dRenderResource(default, "phase", [mesh], [], [], [], []);
        using var source = new EnbCurrentRetailRObjMeshMappers(resource, 100);
        var original = source.Mappers[material].Stage0;
        var advanced = original.Evaluate(600);
        using var variant = source.Configure(900, assets.Textures, System.Numerics.Vector3.Zero);
        var copiedMaterial = variant.Resource.Meshes[0].CurrentRetailLoadedMaterials[0];
        Assert.NotSame(material, copiedMaterial);
        var copied = variant.Mappers[copiedMaterial].Stage0;
        Assert.NotSame(original, copied);
        Assert.Equal(advanced, copied.Evaluate(900));
        Assert.Equal(0u, EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64(copiedMaterial));
        Assert.Equal(original.Evaluate(800), copied.Evaluate(1100));
    }

    [Fact]
    public void LocalizedInstalledMeshKeepsCopiedDescriptorAndLoadedMapperIdentitiesTogether()
    {
        if (EnbInstalledAssetLocator.TryLocate() is not {} paths) return;
        var assets = EnbRetailAssetBundle.Load(paths);
        var definition = assets.PresentationCatalog!.FindEffect(369)!;
        var asset = EnbCurrentRetailRObjEffectAsset.Load(assets, definition.BaseAssetId!.Value);
        using var prototype = new EnbCurrentRetailRObjMeshMappers(asset.Resource!, 100);
        using var instance = prototype.Localize(400);
        Assert.NotEmpty(instance.Mappers);
        var localWords = instance.Resource.Meshes.SelectMany(mesh => mesh.CurrentRetailLoadedMaterials)
            .Select(EnbCurrentRetailMeshModelOwner.MaterialRuntimeWord64).ToArray();
        Assert.All(localWords, word => Assert.NotEqual(0u, word));
        Assert.Equal(localWords.Length, localWords.Distinct().Count());
        for (int i = 0; i < prototype.Resource.Meshes.Count; i++)
        {
            var source = prototype.Resource.Meshes[i];
            var mesh = instance.Resource.Meshes[i];
            Assert.Equal(source.CurrentRetailLoadedMaterials.Count, mesh.CurrentRetailLoadedMaterials.Count);
            for (int j = 0; j < mesh.CurrentRetailLoadedMaterials.Count; j++)
            {
                var oldMaterial = source.CurrentRetailLoadedMaterials[j];
                var material = mesh.CurrentRetailLoadedMaterials[j];
                Assert.NotSame(oldMaterial, material);
                if (material.Mapper0 is not null)
                    Assert.NotSame(prototype.Mappers[oldMaterial].Stage0, instance.Mappers[material].Stage0);
                if (material.Mapper1 is not null)
                    Assert.NotSame(prototype.Mappers[oldMaterial].Stage1, instance.Mappers[material].Stage1);
                Assert.Same(material, mesh.CurrentRetailLoadedMaterialRuntime[j].Material);
            }
            foreach (var pass in mesh.MaterialPasses)
                foreach (var binding in pass.VertexMaterials.Where(b => !b.IsUnbound))
                    Assert.True(instance.Mappers.ContainsKey(binding.Material));
        }
        var animated = instance.Mappers.Values.SelectMany(pair => new[] { pair.Stage0, pair.Stage1 })
            .Where(state => state.Program.UsesElapsedTime).Distinct().ToArray();
        Assert.NotEmpty(animated);
        var controls = animated.Select(state => state.CloneForMaterial(400)).ToArray();
        foreach (var state in animated) state.Evaluate(700);
        instance.Activate(900);
        for (int i = 0; i < animated.Length; i++)
        {
            controls[i].Reset(900);
            Assert.Equal(controls[i].Evaluate(1000), animated[i].Evaluate(1000));
        }
    }
}
