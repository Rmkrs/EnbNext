using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR4W3dRenderResourceTests
{
    [Fact]
    public void MapperLookupDoesNotOwnRetiredReplacementMaterials()
    {
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(BuildResolvedResource(0), out var resource, out var error), error);
        var material = resource.Meshes[0].VertexMaterials[0];
        Assert.True(cache.TryRetainMaterialMappers([material], 123, out var live, out error), error);
        var retired = RetainTemporaryReplacement(cache, material);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(retired.Material.IsAlive);
        Assert.False(retired.Mappers.IsAlive);
        Assert.True(cache.TryRetainMaterialMappers([material], 999, out var again, out error), error);
        Assert.Same(live[material], again[material]);
        Assert.Equal(123u, again[material].Stage0.InitialMilliseconds);
        GC.KeepAlive(cache);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (WeakReference Material, WeakReference Mappers) RetainTemporaryReplacement(
        EnbW3dRenderResourceCache cache, EnbW3dRenderVertexMaterial prototype)
    {
        var material = prototype with { Name = "retired replacement" };
        Assert.True(cache.TryRetainMaterialMappers([material], 456, out var retained, out var error), error);
        return (new(material), new(retained[material]));
    }

    [Theory]
    [InlineData(" SHIPROOT.SHIPMESH ", " SHIPROOT.w3d")]
    [InlineData("SHIPROOT .SHIPMESH", "SHIPROOT .w3d")]
    [InlineData(" ", " .w3d")]
    public void ObjectLazyProviderPreservesLiteralNameAndFirstDotPrefix(string objectName, string fileName)
    {
        //0091D4DB..0091D57D: the same EBP name reaches registry/hash,
        //strchr and strncpy. No trimmed-name alias precedes that path.
        string fixture = Path.Combine(Path.GetTempPath(), "enb-literal-object-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            BuildResolvedResource(0, writePath: Path.Combine(fixture, fileName));
            var files = new EnbRetailFileResolver(fixture, null);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                objectName, out var source, out var error), error);
            Assert.Equal(objectName, source.RequestedObjectName);
            Assert.Equal(fileName, source.LogicalFileName);
            Assert.Equal(Path.Combine(fixture, fileName), source.LooseFilePath);
            using var cache = new EnbW3dRenderResourceCache();
            Assert.True(cache.TryLoadFile(source, out _, out error), error);
            Assert.False(cache.TryFindPrototypeSource(objectName, out _));
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out _));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Fact]
    public void EmptyHierarchyNameConstructsPrivatelyWithoutLoadingDotW3d()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "enb-empty-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            BuildResolvedResource(0, writePath: Path.Combine(fixture, ".w3d"));
            using var cache = new EnbW3dRenderResourceCache();
            var files = new EnbRetailW3dFileResolver(new EnbRetailFileResolver(fixture, null), null);
            Assert.True(cache.TryResolveHierarchy(files, "", true, out var tree, out var error), error);
            Assert.Equal("", tree.RuntimeName);
            Assert.Equal("RootTransform", Assert.Single(tree.Pivots).Name);
            Assert.False(cache.TryFindHierarchy("SHIP_TREE", out _));
            Assert.Equal(0, cache.Count);
            Assert.True(EnbW3dHierarchyPose.TryEvaluate(tree, Matrix4x4.CreateTranslation(7, 8, 9), out var pose, out error), error);
            Assert.Equal(Matrix4x4.CreateTranslation(7, 8, 9), Assert.Single(pose));
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ObjectLazyProviderUsesSecondaryOnlyAfterPrimaryOpenFailure(bool openedEmptyPrimary)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "enb-object-file-" + Guid.NewGuid().ToString("N"));
        string working = Path.Combine(fixture, "release");
        Directory.CreateDirectory(working);
        try
        {
            BuildResolvedResource(0, writePath: Path.Combine(fixture, "SHIPROOT.w3d"));
            if (openedEmptyPrimary) File.WriteAllBytes(Path.Combine(working, "SHIPROOT.w3d"), []);
            var files = new EnbRetailFileResolver(working, null);
            bool resolved = EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, null,
                "SHIPROOT.SHIPMESH", out var source, out var error);
            Assert.Equal(!openedEmptyPrimary, resolved);
            if (resolved)
            {
                Assert.Equal("..\\SHIPROOT.w3d", source.LogicalFileName);
                Assert.Equal("SHIPROOT.SHIPMESH", source.RequestedObjectName);
                Assert.Equal(Path.Combine(fixture, "SHIPROOT.w3d"), source.LooseFilePath);
            }
            else Assert.Contains("not decoded", error);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void HierarchyLazyLoadUsesLiteralPrimaryThenSecondaryOnlyOnFileFailure(bool primaryExists, bool primaryHasTree, bool emptyPrimary)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "enb-hierarchy-" + Guid.NewGuid().ToString("N"));
        string working = Path.Combine(fixture, "release");
        Directory.CreateDirectory(working);
        try
        {
            BuildResolvedResource(0, writePath: Path.Combine(fixture, "SHIP_TREE.w3d"), hierarchyCenter: new(7, 8, 9));
            if (primaryHasTree) BuildResolvedResource(0, writePath: Path.Combine(working, "SHIP_TREE.w3d"));
            else if (primaryExists) File.WriteAllBytes(Path.Combine(working, "SHIP_TREE.w3d"), emptyPrimary ? [] : LeafChunk(0x7fffffff, []));
            var files = new EnbRetailW3dFileResolver(new EnbRetailFileResolver(working, null), null);
            using var cache = new EnbW3dRenderResourceCache();
            Assert.True(cache.TryResolveHierarchy(files, "SHIP_TREE", false, out var disabled, out var error), error);
            Assert.Equal(string.Empty, disabled.RuntimeName);
            Assert.False(cache.TryFindHierarchy("SHIP_TREE", out _));
            Assert.True(cache.TryResolveHierarchy(files, "SHIP_TREE", true, out var tree, out error), error);
            if (primaryExists && !primaryHasTree)
            {
                Assert.Equal(string.Empty, tree.RuntimeName);
                Assert.False(cache.TryFindHierarchy("SHIP_TREE", out _));
                Assert.Equal("RootTransform", Assert.Single(tree.Pivots).Name);
                Assert.Equal(Matrix4x4.Identity, tree.Pivots[0].LocalTransform);
                Assert.True(tree.Pivots[0].IsRoot);
                Assert.False(tree.HasSyntheticRetailRoot);
                BuildResolvedResource(0, writePath: Path.Combine(working, "SHIP_TREE.w3d"));
                Assert.True(cache.TryResolveHierarchy(files, "SHIP_TREE", true, out tree, out error), error);
                Assert.Equal("SHIP_TREE", tree.RuntimeName);
            }
            else Assert.Equal(primaryHasTree ? Vector3.Zero : new(7, 8, 9), tree.Center);
        }
        finally { Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HierarchyFilenamePreservesEntireNameAndAlwaysAppendsExtension(bool emptyPrimary)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "enb-hierarchy-" + Guid.NewGuid().ToString("N"));
        string working = Path.Combine(fixture, "release");
        Directory.CreateDirectory(working);
        try
        {
            //A competing sibling filename must not satisfy a dotted tree name.
            BuildResolvedResource(0, writePath: Path.Combine(working, "SHIP_TREE.w3d"));
            var bytes = LeafChunk(0x7fffffff, []);
            File.WriteAllBytes(Path.Combine(working, "SHIP_TREE.w3d.w3d"), emptyPrimary ? [] : bytes);
            var files = new EnbRetailW3dFileResolver(new EnbRetailFileResolver(working, null), null);
            Assert.True(files.TryResolve("SHIP_TREE.w3d", "SHIPROOT", out var source, out var error), error);
            Assert.Equal(Path.Combine(working, "SHIP_TREE.w3d"), source.LooseFilePath);
            Assert.Equal(0, source.ResourceLocation.DataOffset);
            Assert.Equal(new FileInfo(source.LooseFilePath!).Length, source.ResourceLocation.DataLength);
            files.TryResolve("SHIP_TREE.w3d.w3d", "SHIPROOT", out _, out _, out bool nativeLoaded);
            Assert.True(nativeLoaded);
            using var cache = new EnbW3dRenderResourceCache();
            Assert.True(cache.TryResolveHierarchy(files, "SHIP_TREE.w3d", true, out var tree, out error), error);
            Assert.Equal(string.Empty, tree.RuntimeName);
            Assert.False(cache.TryFindHierarchy("SHIP_TREE", out _));
        }
        finally { Directory.Delete(fixture, true); }
    }
    [Fact]
    public void HierarchyRegistryKeepsTheFirstLoadedTreeAcrossPrototypeRemovalAndFileReload()
    {
        var source = BuildResolvedResource(0);
        var other = BuildResolvedResource(0, hierarchyCenter: new(1, 2, 3)) with
        {
            LogicalFileName = "OTHER.W3D",
            ResourceLocation = source.ResourceLocation with { Name = "OTHER.W3D", DataOffset = 100000 },
        };
        using var cache = new EnbW3dRenderResourceCache();
        Assert.False(cache.TryFindHierarchy("SHIP_TREE", out _));
        Assert.True(cache.TryLoadFile(source, out _, out var error), error);
        Assert.True(cache.TryFindHierarchy("ship_tree", out var first));
        Assert.Equal(Vector3.Zero, first.Center);
        Assert.True(cache.TryLoadFile(other, out var loadedOther, out error), error);
        Assert.Equal(new Vector3(1, 2, 3), loadedOther.Hierarchies[0].Center);
        Assert.True(cache.TryFindHierarchy("SHIP_TREE", out var retained));
        Assert.Same(first, retained);
        Assert.True(cache.RemovePrototype("SHIPROOT"));
        Assert.True(cache.TryLoadFile(other, out _, out error), error);
        Assert.True(cache.TryFindHierarchy("SHIP_TREE", out retained));
        Assert.Same(first, retained);
        cache.Dispose();
        Assert.Equal(Vector3.Zero, first.Center);
        Assert.Throws<ObjectDisposedException>(() => cache.TryFindHierarchy("SHIP_TREE", out _));
    }

    [Theory]
    [InlineData(false, EnbW3dPrototypeKind.HierarchyModel)]
    [InlineData(true, EnbW3dPrototypeKind.HierarchicalLod)]
    public void PrototypeRegistrationUsesTopLevelFileOrder(bool hlodFirst, EnbW3dPrototypeKind winner)
    {
        var source = BuildResolvedResource(0, hlodBeforeHierarchyModel: hlodFirst);
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source, out _, out var error), error);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT", out var selected));
        Assert.Equal(winner, selected.RegisteredPrototype!.Value.Kind);
        Assert.True(selected.PrototypeRegistration > 0);
        Assert.True(source.Model.PrototypeEntries.Zip(source.Model.PrototypeEntries.Skip(1))
            .All(pair => pair.First.ChunkOffset < pair.Second.ChunkOffset));
    }

    [Fact]
    public void NamedRemovalAndExplicitReloadCreateANewPrototypeWhileOldModelOwnersSurvive()
    {
        var source = BuildResolvedResource(0);
        using var cache = new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName.ToLowerInvariant(), new EnbW3dTextureCreationRequest(
                EnbW3dTextureSemanticCompiler.Compile(definition.Info), 0)));
        Assert.True(cache.TryLoadFile(source, out var original, out var error, 123), error);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out var oldSource));
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT", out var sibling));
        var oldMesh = Assert.Single(original.Meshes);
        using var childModel = cache.CreateModelOwner(oldMesh, oldMesh.VertexMaterials.Select(material =>
            ((object)material, (object?)material.Mapper0, (object?)material.Mapper1)));
        Assert.True(cache.RemovePrototype("shiproot.shipmesh"));
        Assert.False(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out _));
        Assert.False(cache.TryGetRegisteredPrototype(oldSource, out _));
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT", out var retainedSibling));
        Assert.Equal(sibling.PrototypeRegistration, retainedSibling.PrototypeRegistration);
        Assert.True(cache.TryGetOrCompile(source, out var cached, out error), error);
        Assert.Same(original, cached);
        Assert.False(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out _));
        Assert.True(cache.TryLoadFile(source, out _, out error, 456), error);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out var freshSource));
        Assert.NotEqual(oldSource.PrototypeRegistration, freshSource.PrototypeRegistration);
        Assert.True(cache.TryGetRegisteredPrototype(freshSource, out var fresh));
        var freshMesh = Assert.Single(fresh.Meshes);
        Assert.NotSame(oldMesh, freshMesh);
        Assert.NotSame(oldMesh.VertexMaterials[0], freshMesh.VertexMaterials[0]);
        Assert.Same(oldMesh.MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState,
            freshMesh.MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState);
        Assert.True(cache.TryRetainMaterialMappers([oldMesh.VertexMaterials[0], freshMesh.VertexMaterials[0]],
            789, out var mappers, out error), error);
        Assert.Equal(123u, mappers[oldMesh.VertexMaterials[0]].Stage0.InitialMilliseconds);
        Assert.Equal(456u, mappers[freshMesh.VertexMaterials[0]].Stage0.InitialMilliseconds);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT", out retainedSibling));
        Assert.Equal(sibling.PrototypeRegistration, retainedSibling.PrototypeRegistration);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.RemovePrototype("NULL"));
        cache.Dispose();
        Assert.NotNull(childModel.ModelIdentity);
    }

    [Fact]
    public void RegisteredMeshPrototypeRetainsSharedModelUntilRemovalAndLastChildRelease()
    {
        using var cache=new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName.ToLowerInvariant(),new EnbW3dTextureCreationRequest(
                EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        var source=BuildResolvedResource(0);
        Assert.True(cache.TryLoadFile(source,out var resource,out var error,123),error);
        var mesh=Assert.Single(resource.Meshes);
        var retired=new List<object>();
        Assert.True(cache.TryCreateModelOwner(mesh,mesh.CurrentRetailLoadedMaterials,456,out var first,out error,retired.Add),error);
        var identity=first.ModelIdentity;
        first.Dispose();
        Assert.Empty(retired);
        Assert.True(cache.TryCreateModelOwner(mesh,mesh.CurrentRetailLoadedMaterials,789,out var child,out error,retired.Add),error);
        using(child)
        {
            Assert.Same(identity,child.ModelIdentity);
            using(var unique=child.CloneRenderObject())
            {
                unique.EnsureUniqueModel(900);
                Assert.NotSame(identity,unique.ModelIdentity);
                Assert.NotSame(child.MapperIdentityAt(0,0),unique.MapperIdentityAt(0,0));
            }
            Assert.Single(retired); // The private copy, not the registered template.
            Assert.True(cache.RemovePrototype("SHIPROOT.SHIPMESH"));
            Assert.Single(retired);
            Assert.True(cache.TryLoadFile(source,out _,out error,1000),error);
            Assert.True(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH",out var freshSource));
            Assert.True(cache.TryGetRegisteredPrototype(freshSource,out var fresh));
            var freshMesh=Assert.Single(fresh.Meshes);
            Assert.True(cache.TryCreateModelOwner(freshMesh,freshMesh.CurrentRetailLoadedMaterials,1100,out var freshChild,out error,retired.Add),error);
            using(freshChild)
            {
                Assert.NotSame(identity,freshChild.ModelIdentity);
                cache.Dispose();
                Assert.Single(retired); // Both child owners survive registry teardown.
            }
            Assert.Equal(2,retired.Count);
        }
        Assert.Equal(3,retired.Count);
        Assert.Same(identity,retired[^1]);
    }

    [Fact]
    public void MutatedLoadedTableDoesNotAliasTheRegisteredPrototypeModel()
    {
        using var cache=new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(BuildResolvedResource(0),out var resource,out var error,123),error);
        var mesh=Assert.Single(resource.Meshes);
        Assert.True(cache.TryCreateModelOwner(mesh,mesh.CurrentRetailLoadedMaterials,200,out var original,out error),error);
        using(original)
        {
            var replacement=mesh.CurrentRetailLoadedMaterials[0] with { };
            Assert.True(cache.TryCreateModelOwner(mesh,[replacement],300,out var changed,out error),error);
            using(changed)
            {
                Assert.NotSame(original.ModelIdentity,changed.ModelIdentity);
                Assert.Same(replacement,changed.MaterialIdentityAt(0));
                Assert.Same(mesh.CurrentRetailLoadedMaterials[0],original.MaterialIdentityAt(0));
            }
        }
    }

    [Theory]
    [InlineData(0x0004_0400u)]
    [InlineData(0x0010_0000u)]
    public void RObjCanBorrowPublishedModelsAndMappersBeforeLocalizingItsOwnCopy(uint mapperAttributes)
    {
        var random=new EnbW3dRetailMapperRandomSource();
        using var cache=new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)),random);
        Assert.True(cache.TryLoadFile(BuildResolvedResource(0,mapperAttributes:mapperAttributes),out var resource,out var error,123),error);
        var mesh=Assert.Single(resource.Meshes);
        var batches=new EnbCurrentRetailMeshBatchState();
        Assert.True(batches.TryCreateModel(cache,mesh,mesh.CurrentRetailLoadedMaterials,200,out var ordinary,out error),error);
        using(ordinary)
        using(var effect=new EnbCurrentRetailRObjMeshMappers(resource,300,batches,cache,resource.Meshes))
        {
            var appearance=effect.CreateAppearance(mesh,mesh.MaterialPasses.ToArray());
            Assert.Same(ordinary.ModelIdentity,appearance.Owner.ModelIdentity);
            Assert.Same(ordinary.MapperIdentityAt(0,0),effect.Mappers[mesh.CurrentRetailLoadedMaterials[0]].Stage0);
            Assert.Equal(123u,effect.Mappers[mesh.CurrentRetailLoadedMaterials[0]].Stage0.InitialMilliseconds);
            using var local=effect.Localize(400);
            var localMesh=Assert.Single(local.Resource.Meshes);
            Assert.NotSame(ordinary.ModelIdentity,local.CreateAppearance(localMesh,[]).Owner.ModelIdentity);
            Assert.NotSame(ordinary.MapperIdentityAt(0,0),local.Mappers[localMesh.CurrentRetailLoadedMaterials[0]].Stage0);
            cache.Dispose();
            Assert.Same(ordinary.TextureIdentity(0),appearance.Owner.TextureIdentity(0));
        }
        if(mapperAttributes==0x0010_0000)
        {
            var expected=new EnbW3dRetailMapperRandomSource();
            for(int i=0;i<6;i++)expected.NextUInt32(); // Source constructor and private copy only.
            Assert.Equal(expected.NextUInt32(),random.NextUInt32());
        }
    }

    [Fact]
    public void DescriptorMapperRetentionUsesExactReferencesAndPreservesPublishedEpoch()
    {
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(cache.TryGetOrCompile(BuildResolvedResource(0), out var resource, out var error, 123), error);
        var material = Assert.Single(Assert.Single(resource.Meshes).VertexMaterials);
        var replacement = material with { };
        Assert.True(cache.TryRetainMaterialMappers([material, replacement, material], 456, out var first, out error), error);
        Assert.Equal(2, first.Count);
        Assert.Equal(123u, first[material].Stage0.InitialMilliseconds);
        Assert.Equal(456u, first[replacement].Stage0.InitialMilliseconds);
        Assert.NotSame(first[material].Stage0, first[replacement].Stage0);
        Assert.True(cache.TryRetainMaterialMappers([replacement, material], 789, out var again, out error), error);
        Assert.Same(first[material], again[material]);
        Assert.Same(first[replacement], again[replacement]);
        cache.Dispose();
        Assert.Equal(456u, first[replacement].Stage0.InitialMilliseconds);
        Assert.Throws<ObjectDisposedException>(() => cache.TryRetainMaterialMappers([material], 900, out _, out _));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RObjBorrowsEachPublishedMeshIndependentlyOfAnUnregisteredSibling(bool registeredFirst)
    {
        using var cache=new EnbW3dRenderResourceCache(definition=>
            (definition.LogicalName,new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info),0)));
        Assert.True(cache.TryLoadFile(BuildResolvedResource(0,mapperAttributes:0x0004_0400u),out var resource,out var error,123),error);
        var mesh=Assert.Single(resource.Meshes);
        var privateMesh=new EnbW3dRenderMesh(1,"unregistered","unregistered",1,[],[],[],[],[],[],[])
            {CurrentRetailLoadedMaterials=mesh.CurrentRetailLoadedMaterials};
        int index=registeredFirst?0:1;
        var mixed=resource.Copy().SetMeshes(registeredFirst?[mesh,privateMesh]:[privateMesh,mesh]);
        EnbW3dRenderMesh?[] registered=registeredFirst?[mesh,null]:[null,mesh];
        var batches=new EnbCurrentRetailMeshBatchState();
        Assert.True(batches.TryCreateModel(cache,mesh,mesh.CurrentRetailLoadedMaterials,200,out var ordinary,out error),error);
        using(ordinary)
        using(var effect=new EnbCurrentRetailRObjMeshMappers(mixed,300,batches,cache,registered))
        {
            var appearance=effect.CreateAppearance(mesh,[]);
            Assert.Same(ordinary.ModelIdentity,appearance.Owner.ModelIdentity);
            Assert.Equal(123u,effect.Mappers[mesh.CurrentRetailLoadedMaterials[0]].Stage0.InitialMilliseconds);
            Assert.Same(ordinary.MapperIdentityAt(0,0),effect.CreateAppearance(privateMesh,[]).Owner.MapperIdentityAt(0,0));
            Assert.NotSame(ordinary.ModelIdentity,effect.CreateAppearance(privateMesh,[]).Owner.ModelIdentity);
            using var local=effect.Localize(400);
            Assert.NotSame(ordinary.ModelIdentity,local.CreateAppearance(local.Resource.Meshes[index],[]).Owner.ModelIdentity);
            Assert.NotSame(effect.CreateAppearance(privateMesh,[]).Owner.ModelIdentity,
                local.CreateAppearance(local.Resource.Meshes[1-index],[]).Owner.ModelIdentity);
            cache.Dispose();
            Assert.Same(ordinary.TextureIdentity(0),appearance.Owner.TextureIdentity(0));
        }
    }

    [Fact]
    public void ModelOwnersRetainPrototypeMapperEpochAndSeparateOnlyOnUniqueCopy()
    {
        using var cache = new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName.ToLowerInvariant(),
                new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info), 0)));
        Assert.True(cache.TryGetOrCompile(BuildResolvedResource(0), out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        var material = Assert.Single(mesh.VertexMaterials);
        Assert.True(cache.TryRetainDescriptor(mesh.MaterialPasses, 123, out _, out error), error);
        var generated=new EnbCurrentRetailTextureObjectState(new EnbW3dTextureCreationRequest(
            EnbW3dTextureSamplingSemantics.RetailDefault with {RetailCreationSelector=1},7));
        var authored=Assert.Single(mesh.CurrentRetailLoadedTextureStates);
        Assert.True(cache.TryCreateModelOwner(mesh,mesh.CurrentRetailLoadedMaterials,0,out var replaced,out error,
            loadedTextures:[generated,authored,authored]),error);
        using(replaced)
        using(var replacementCopy=replaced.CloneRenderObject())
        {
            replacementCopy.EnsureUniqueModel(1);
            Assert.Equal(3,replacementCopy.LoadedTextureCount);
            Assert.Same(generated,replacementCopy.TextureIdentity(0));
            Assert.Same(authored,replacementCopy.TextureIdentity(1));
            Assert.Same(authored,replacementCopy.TextureIdentity(2));
        }
        // Descriptor acquisition happens first, so later model owners must
        // retain its mapper epoch even with a different construction clock.
        var geometry=new EnbCurrentRetailMeshGeometryState([new(1,2,3)],[Vector3.UnitZ],[],[],
            Vector3.Zero,Vector3.One,Vector3.Zero,1,0);
        Assert.True(cache.TryCreateModelOwner(mesh, mesh.VertexMaterials, 456, out var first, out error,geometry:geometry), error);
        using (first)
        {
            Assert.True(cache.TryCreateModelOwner(mesh, mesh.VertexMaterials, 789, out var second, out error), error);
            using (second)
            using (var clone = first.CloneRenderObject())
            {
                var mapper = Assert.IsType<EnbW3dMapperRuntimeState>(first.MapperIdentity(material, 0));
                Assert.Equal(123u, mapper.InitialMilliseconds);
                Assert.Same(mapper, second.MapperIdentity(material, 0));
                Assert.Same(first.MapperIdentity(material, 1), second.MapperIdentity(material, 1));
                Assert.Same(first.ModelIdentity, clone.ModelIdentity);
                Assert.Same(geometry,clone.Geometry);
                Assert.NotNull(second.Geometry);
                Assert.NotSame(geometry, second.Geometry);
                Assert.Same(mesh.CurrentRetailLoadedGeometry!.Positions, second.Geometry.Positions);
                Assert.Equal(mesh.CurrentRetailLoadedGeometry.TriangleIndices.ToArray(), second.Geometry.TriangleIndices.ToArray());
                clone.EnsureUniqueModel(900);
                Assert.NotSame(geometry,clone.Geometry);
                Assert.Same(geometry.Positions,clone.Geometry!.Positions);
                uint seed=1;clone.Geometry.Scale(new(2),ref seed);
                Assert.Equal(new Vector3(2,4,6),clone.Geometry.Positions[0]);
                Assert.Equal(new Vector3(1,2,3),geometry.Positions[0]);
                Assert.NotSame(mapper, clone.MapperIdentity(material, 0));
                Assert.NotSame(material, clone.MaterialIdentity(material));
                cache.Dispose();
                Assert.Same(mapper, second.MapperIdentity(material, 0));
                Assert.Same(first.TextureIdentity(0), clone.TextureIdentity(0));
                Assert.Equal(123u, mapper.InitialMilliseconds);
            }
        }
    }

    [Fact]
    public void PrototypeCacheSerializesTextureAcquisitionsAndDoesNotReplayCacheHits()
    {
        var source = BuildResolvedResource(0);
        int acquisitions = 0;
        using var cache = new EnbW3dRenderResourceCache(definition =>
        {
            acquisitions++;
            return (definition.LogicalName.ToLowerInvariant(),
                new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info), 0));
        });
        var results = new EnbW3dRenderResource[16];
        Parallel.For(0, results.Length, index =>
        {
            Assert.True(cache.TryGetOrCompile(source, out var compiled, out var error), error);
            results[index] = compiled;
        });
        Assert.Equal(1, acquisitions);
        Assert.All(results, result => Assert.Same(results[0], result));
        var firstState = results[0].Meshes[0].MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState;
        Assert.NotNull(firstState);
        var second = source with { ResourceLocation = source.ResourceLocation with { DataOffset = source.ResourceLocation.DataOffset + 1000 } };
        Assert.True(cache.TryGetOrCompile(second, out var other, out var diagnostic), diagnostic);
        Assert.Equal(2, acquisitions);
        Assert.Same(firstState, other.Meshes[0].MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState);
        using var model = cache.CreateModelOwner(results[0].Meshes[0], []);
        using var instance = model.CloneRenderObject();
        Assert.Same(firstState, model.TextureIdentity(0));
        cache.Dispose();
        model.Dispose();
        instance.EnsureUniqueModel(10, force: true);
        Assert.Same(firstState, instance.TextureIdentity(0));
        Assert.Equal(0, cache.Count);
        Assert.Throws<ObjectDisposedException>(() => cache.TryGetOrCompile(source, out _, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitFileReloadReplaysTextureInfoBeforeRejectingDuplicatePrototype(bool hasInfo)
    {
        var source = BuildResolvedResource(0, textureInfo: hasInfo ? new(0, 1, 0) : null);
        int acquisitions = 0;
        using var cache = new EnbW3dRenderResourceCache(definition =>
        {
            acquisitions++;
            return (definition.LogicalName.ToLowerInvariant(),
                new EnbW3dTextureCreationRequest(EnbW3dTextureSemanticCompiler.Compile(definition.Info), 0));
        });
        Assert.True(cache.TryLoadFile(source, out var first, out var diagnostic, 123), diagnostic);
        var mesh = Assert.Single(first.Meshes);
        var material = Assert.Single(mesh.VertexMaterials);
        var state = mesh.MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState!;
        using var passTexture = cache.AcquireRslEffectPassTexture("paint.tga");
        Assert.Same(state, passTexture.State);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, state.Sampling.AddressU);
        int beforeReload = acquisitions;
        Assert.True(cache.TryLoadFile(source, out var repeated, out diagnostic, 456), diagnostic);
        Assert.Same(first, repeated);
        Assert.Equal(beforeReload + 1, acquisitions);
        var expectedAddress = hasInfo ? EnbRenderTextureAddressMode.Wrap : EnbRenderTextureAddressMode.Clamp;
        Assert.Equal(expectedAddress, state.Sampling.AddressU);
        Assert.Equal(expectedAddress, state.Sampling.AddressV);
        Assert.True(cache.TryRetainMaterialMappers([material], 789, out var mappers, out diagnostic), diagnostic);
        Assert.Equal(123u, mappers[material].Stage0.InitialMilliseconds);
        state.ApplyRslEffectPassAddressing();
        Assert.True(cache.TryGetOrCompile(source, out var lookup, out diagnostic), diagnostic);
        Assert.Same(first, lookup);
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, state.Sampling.AddressU);
        Assert.Equal(beforeReload + 1, acquisitions);
    }

    [Fact]
    public void DuplicateFileLoadConstructsRandomMappersButKeepsPublishedMapperIdentity()
    {
        var source = BuildResolvedResource(0, mapperAttributes: 0x0010_0000);
        var random = new EnbW3dRetailMapperRandomSource();
        var expected = new EnbW3dRetailMapperRandomSource();
        using var cache = new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName, new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault, 0)), random);
        Assert.True(cache.TryLoadFile(source, out var original, out var error, 123), error);
        var material = Assert.Single(Assert.Single(original.Meshes).CurrentRetailLoadedMaterials);
        Assert.True(cache.TryRetainMaterialMappers([material], 123, out var before, out error), error);
        var phase = before[material].Stage0.Evaluate(123);
        Assert.True(cache.TryLoadFile(source, out var duplicate, out error, 456), error);
        Assert.Same(original, duplicate);
        Assert.True(cache.TryRetainMaterialMappers([material], 789, out var after, out error), error);
        Assert.Same(before[material].Stage0, after[material].Stage0);
        Assert.Equal(phase, after[material].Stage0.Evaluate(123));
        //00938D50 consumes three words per constructed mapper, even when
        //0091D370 subsequently discards the duplicate prototype.
        for (int i = 0; i < 6; i++) expected.NextUInt32();
        Assert.Equal(expected.NextUInt32(), random.NextUInt32());
    }

    [Fact]
    public void DuplicateFileLoadReplaysIncomingTextureInfoRatherThanCachedFileInfo()
    {
        var source = BuildResolvedResource(0, textureInfo: new(8, 1, 0));
        using var cache = new EnbW3dRenderResourceCache(definition =>
            (definition.LogicalName.ToLowerInvariant(), new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault, 0)));
        Assert.True(cache.TryLoadFile(source, out var original, out var error), error);
        using var texture = cache.AcquireRslEffectPassTexture("paint.tga");
        Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.State.Sampling.AddressU);
        var incoming = BuildResolvedResource(0, textureInfo: new(0, 1, 0));
        Assert.Equal(source.ResourceLocation, incoming.ResourceLocation);
        Assert.True(cache.TryLoadFile(incoming, out var duplicate, out error), error);
        Assert.Same(original, duplicate);
        Assert.Equal(EnbRenderTextureAddressMode.Wrap, texture.State.Sampling.AddressU);
    }

    [Fact]
    public void ExplicitReloadRegistersIncomingNamesAtTheSameFileLocation()
    {
        var source = BuildResolvedResource(0);
        var incoming = BuildResolvedResource(0, meshName: "NEXTMESH");
        Assert.Equal(source.ResourceLocation, incoming.ResourceLocation);
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source, out var original, out var error), error);
        Assert.True(cache.TryLoadFile(incoming, out var retained, out error), error);
        Assert.Same(original, retained);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT.NEXTMESH", out var registered));
        Assert.True(cache.TryGetRegisteredPrototype(registered, out var constructed));
        Assert.Equal("NEXTMESH", Assert.Single(constructed.Meshes).Name);
    }

    [Fact]
    public void DuplicatePrototypeReloadStillRegistersNewHierarchyNames()
    {
        var source = BuildResolvedResource(0);
        var incoming = BuildResolvedResource(0, hierarchyName: "NEXT_TREE", hierarchyCenter: new(7, 8, 9));
        Assert.Equal(source.ResourceLocation, incoming.ResourceLocation);
        using var cache = new EnbW3dRenderResourceCache();
        Assert.True(cache.TryLoadFile(source, out var original, out var error), error);
        Assert.True(cache.TryFindHierarchy("SHIP_TREE", out var oldTree));
        Assert.True(cache.TryLoadFile(incoming, out var retained, out error), error);
        Assert.Same(original, retained);
        Assert.True(cache.TryFindHierarchy("NEXT_TREE", out var newTree));
        Assert.Equal(new Vector3(7, 8, 9), newTree.Center);
        Assert.True(cache.TryFindHierarchy("SHIP_TREE", out var oldTreeAfter));
        Assert.Same(oldTree, oldTreeAfter);
    }

    [Fact]
    public void TextureAcquisitionOrderPreservesFirstCreationAndDropsDiscardedLeases()
    {
        var source = BuildResolvedResource(shaderId: 0, includeUnusedTexture: true);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        mesh = (mesh).Copy().SetTextureAcquisitions(new EnbW3dTextureAcquisition[]
            {
                new("paint.tga", new(0x48, 1, 0)),
                new("DISCARDED.TGA", null),
            }.Concat(mesh.TextureAcquisitions).ToArray());
        var registry = new EnbCurrentRetailTextureRegistry();
        var acquired = new List<string>();
        using var owner = new EnbCurrentRetailCompiledTextureOwner(mesh, registry, definition =>
        {
            acquired.Add(definition.LogicalName);
            return (definition.LogicalName.ToLowerInvariant(), EnbW3dTextureSemanticCompiler.Compile(definition.Info));
        });
        Assert.Equal(new[] { "paint.tga", "DISCARDED.TGA", "PAINT.TGA", "UNUSED.TGA" }, acquired);
        Assert.Equal(2, owner.LoadedTextures.Count);
        Assert.Equal(2,owner.Mesh.CurrentRetailLoadedTextureStates.Count);
        Assert.Same(owner.LoadedTextures[0].State,owner.Mesh.CurrentRetailLoadedTextureStates[0]);
        Assert.Same(owner.LoadedTextures[1].State,owner.Mesh.CurrentRetailLoadedTextureStates[1]);
        Assert.Equal(EnbW3dTextureSemanticCompiler.Compile(new(0x48, 1, 0)).RetailCreationSelector,
            owner.LoadedTextures[0].State.Sampling.RetailCreationSelector);
        Assert.Equal(EnbW3dTextureSemanticCompiler.Compile(new(0x48, 1, 0)).AddressU,
            owner.LoadedTextures[0].State.Sampling.AddressU);
        Assert.Same(owner.LoadedTextures[0].State,
            owner.Mesh.MaterialPasses[0].TriangleBindings[0].Textures[0]!.CurrentRetailTextureState);
        Assert.Equal(1, registry.CollectUnused());
        owner.Dispose();
        Assert.Equal(2, registry.CollectUnused());
    }

    [Fact]
    public void CompiledTextureDefinitionsPreserveUnusedEntriesAndAbsentInfo()
    {
        var source = BuildResolvedResource(shaderId: 0, includeUnusedTexture: true);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error), error);
        var mesh = Assert.Single(resource.Meshes);
        Assert.Equal(2, mesh.ModernTextureDefinitions.Count);
        Assert.Equal(new[] { "PAINT.TGA", "UNUSED.TGA" }, mesh.TextureAcquisitions.Select(value => value.LogicalName));
        Assert.Null(mesh.TextureAcquisitions[0].Info);
        Assert.Equal(new EnbW3dTextureInfo(4, 1, 0), mesh.TextureAcquisitions[1].Info);
        Assert.Equal(new EnbW3dRenderTextureDefinition(0, "PAINT.TGA", null), mesh.ModernTextureDefinitions[0]);
        Assert.Equal(new EnbW3dRenderTextureDefinition(1, "UNUSED.TGA", new(4, 1, 0)), mesh.ModernTextureDefinitions[1]);
        Assert.Single(Assert.Single(mesh.MaterialPasses).TextureStages[0].Textures);
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<EnbW3dRenderTextureDefinition>>(mesh.ModernTextureDefinitions);
        var registry = new EnbCurrentRetailTextureRegistry();
        using var owner = new EnbCurrentRetailCompiledTextureOwner(mesh, registry,
            definition => (definition.LogicalName, EnbW3dTextureSemanticCompiler.Compile(definition.Info)));
        Assert.Equal(2, owner.LoadedTextures.Count);
        var pass = Assert.Single(owner.Mesh.MaterialPasses);
        var texture = Assert.Single(Assert.Single(pass.TextureStages).Textures);
        Assert.Same(owner.LoadedTextures[0].State, texture.CurrentRetailTextureState);
        Assert.Same(texture.CurrentRetailTextureState, pass.TextureStages[0].CurrentRetailUniformTexture!.CurrentRetailTextureState);
        Assert.Same(texture.CurrentRetailTextureState, pass.TriangleBindings[0].Textures[0]!.CurrentRetailTextureState);
        Assert.Null(mesh.MaterialPasses[0].TextureStages[0].Textures[0].CurrentRetailTextureState);
        using var model = new EnbCurrentRetailMeshModelOwner([], loadedTextures: owner.LoadedTextures);
        owner.Dispose();
        Assert.Equal(0, registry.CollectUnused());
        model.Dispose();
        Assert.Equal(2, registry.CollectUnused());
    }

    [Fact]
    public void R4ACompilerBuildsImmutableBufferReadyMeshAndAuthoredBindings()
    {
        EnbResolvedW3dResource source = BuildResolvedResource(shaderId: 0);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out EnbW3dRenderResource resource, out string? diagnostic), diagnostic);
        Assert.Equal("SHIP.W3D", resource.LogicalFileName);
        Assert.Equal("SHIP.W3D", resource.Key.ResourceName);
        Assert.Equal("mixfile_art_07.mix", Path.GetFileName(resource.Key.ArchivePath));
        EnbW3dRenderMesh mesh = Assert.Single(resource.Meshes);
        Assert.Equal("SHIPMESH", mesh.Name);
        Assert.Equal(3, mesh.Positions.Count);
        Assert.Equal(3, mesh.Normals.Count);
        Assert.Equal([0u, 1u, 2u], mesh.Indices);
        Assert.Equal(5.5f, mesh.BoundingRadius, 3);

        EnbW3dRenderTriangleMetadata triangle = Assert.Single(mesh.TriangleMetadata);
        Assert.Equal(0x1234u, triangle.Attributes);
        Assert.Equal(Vector3.UnitZ, triangle.Normal);
        Assert.Equal(-0.25f, triangle.Distance, 4);

        EnbW3dRenderVertexMaterial material = Assert.Single(mesh.VertexMaterials);
        Assert.Equal("R4 MATERIAL", material.Name);
        Assert.Equal(new EnbRenderPackedRgb8(4, 5, 6, 0), material.Diffuse);
        Assert.Equal(1.5f, material.Power, 4);
        Assert.Equal(0.75f, material.Opacity, 4);
        Assert.NotNull(material.Mapper0);
        Assert.Equal(EnbW3dMapperTypeSelector.Mode04, material.Mapper0!.TypeSelector);
        Assert.Equal(6.0f, material.Mapper0.UScale, 4);
        Assert.Equal(0.006f, material.Mapper0.UPerSecond, 6);
        Assert.NotNull(material.Mapper1);
        Assert.Equal(EnbW3dMapperTypeSelector.Mode04, material.Mapper1!.TypeSelector);
        Assert.Equal(1.0f, material.Mapper1.UScale, 4);
        Assert.Equal(0.0f, material.Mapper1.UPerSecond, 4);

        EnbW3dRenderMaterialPass pass = Assert.Single(mesh.MaterialPasses);
        Assert.Equal((uint)0, Assert.Single(pass.VertexMaterials).SourceId);
        EnbW3dRenderShaderRecord shader = Assert.Single(pass.Shaders).Shader;
        Assert.Equal(0x0000_0103u, shader.Word0);
        Assert.Equal(0x0100_0102u, shader.Word1);
        Assert.Equal(0x0200_0201u, shader.Word2);
        Assert.Equal(0x0200_0200u, shader.Word3);
        EnbW3dFixedFunctionState fixedFunction = Assert.Single(pass.Shaders).FixedFunction;
        Assert.Equal(EnbRenderDepthCompare.LessOrEqual, fixedFunction.DepthCompare);
        Assert.True(fixedFunction.DepthWriteEnabled);
        Assert.Equal(EnbRenderCullMode.Clockwise, fixedFunction.CullMode);
        Assert.Equal(EnbRenderBlendFactor.One, fixedFunction.SourceBlend);
        Assert.Equal(EnbRenderBlendFactor.Zero, fixedFunction.DestinationBlend);
        Assert.False(fixedFunction.BlendEnabled);
        Assert.Equal(new EnbRenderColor32(255, 0, 0, 255), pass.DiffuseColors[0]);

        EnbW3dRenderTextureStage stage = Assert.Single(pass.TextureStages);
        EnbW3dRenderTextureBinding texture = Assert.Single(stage.Textures);
        Assert.Equal("PAINT.TGA", texture.LogicalName);
        Assert.False(texture.IsMissing);
        Assert.NotNull(texture.Location);
        Assert.Equal("PAINT.DDS", texture.Location!.Name);
        Assert.Equal(0.10f, stage.RuntimeTexCoords[0].X, 4);
        Assert.Equal(0.80f, stage.RuntimeTexCoords[0].Y, 4);

        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);
        Assert.Equal("SHIPROOT", hlod.Name);
        EnbW3dRenderHlodLevel level = Assert.Single(hlod.Levels);
        Assert.Equal(float.MaxValue, level.Threshold);
        Assert.Equal("SHIPMESH", Assert.Single(level.Members).Name);

        Assert.IsType<System.Collections.Immutable.ImmutableArray<Vector3>>(mesh.Positions);
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<uint>>(mesh.Indices);

        EnbW3dMesh sourceMesh = Assert.Single(source.Model.Meshes);
        Assert.Same(sourceMesh.Vertices, mesh.Positions);
        var immutableSourcePositions = Assert.IsAssignableFrom<IList<Vector3>>(sourceMesh.Vertices);
        Assert.Throws<NotSupportedException>(() => immutableSourcePositions[0] = new Vector3(99, 98, 97));
        Assert.Equal(new Vector3(1, 0, 0), mesh.Positions[0]);
    }

    [Fact]
    public void R4ACompilerRejectsPassReferenceOutsideDecodedResourceTables()
    {
        EnbResolvedW3dResource source = BuildResolvedResource(shaderId: 3);

        Assert.False(EnbW3dRenderResourceCompiler.TryCompile(source, out _, out string? diagnostic));
        string actualDiagnostic = diagnostic
            ?? throw new Xunit.Sdk.XunitException("Expected the renderer compiler to explain the invalid shader reference.");
        Assert.Contains("missing shader 3", actualDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilerRetainsAuthoredJoinMapWithoutReplacingAbsentMaps()
    {
        byte[] treePayload = [9, 8, 7, 6];
        var source = BuildResolvedResource(shaderId: 0, vertexJoinMap: [2, 2, 2], aabTreePayload: treePayload);
        Assert.Equal(new uint[] { 2, 2, 2 }, Assert.Single(source.Model.Meshes).AuthoredVertexJoinMap);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var compiled, out var error), error);
        var map = Assert.Single(compiled.Meshes).AuthoredVertexJoinMap;
        Assert.Equal(new uint[] { 2, 2, 2 }, map);
        Assert.NotSame(Assert.Single(source.Model.Meshes).AuthoredVertexJoinMap, map);
        Assert.IsAssignableFrom<System.Collections.ObjectModel.ReadOnlyCollection<uint>>(map);
        Assert.Equal(treePayload, Assert.Single(compiled.Meshes).AuthoredAabTreePayload);
        Assert.NotSame(Assert.Single(source.Model.Meshes).AuthoredAabTreePayload,
            Assert.Single(compiled.Meshes).AuthoredAabTreePayload);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(BuildResolvedResource(0), out var absent, out error), error);
        Assert.Null(Assert.Single(absent.Meshes).AuthoredVertexJoinMap);
        Assert.Null(Assert.Single(absent.Meshes).AuthoredAabTreePayload);
    }

    [Fact]
    public void R4AResourceCacheUsesInstalledMixEntryIdentityNotBaseAssetRequestName()
    {
        EnbResolvedW3dResource first = BuildResolvedResource(shaderId: 0);
        EnbResolvedW3dResource second = first with { RequestedObjectName = "ALTERNATE_ROOT" };
        var cache = new EnbW3dRenderResourceCache();

        Assert.True(cache.TryGetOrCompile(first, out EnbW3dRenderResource firstCompiled, out string? firstDiagnostic), firstDiagnostic);
        Assert.True(cache.TryGetOrCompile(second, out EnbW3dRenderResource secondCompiled, out string? secondDiagnostic), secondDiagnostic);

        Assert.Same(firstCompiled, secondCompiled);
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryFindPrototype("shipmesh", out _));
        Assert.True(cache.TryFindPrototype("shiproot.shipmesh", out var prototype));
        Assert.Same(firstCompiled, prototype);
        var duplicate = first with { ResourceLocation = first.ResourceLocation with { DataOffset = first.ResourceLocation.DataOffset + 1000 } };
        Assert.True(cache.TryGetOrCompile(duplicate, out var duplicateResource, out var error), error);
        Assert.NotSame(firstCompiled, duplicateResource);
        Assert.True(cache.TryFindPrototype("SHIPROOT.SHIPMESH", out prototype));
        Assert.Same(firstCompiled, prototype);
        Assert.True(cache.TryFindPrototypeSource("SHIPROOT.SHIPMESH", out var registered));
        Assert.Equal(first.ResourceLocation, registered.ResourceLocation);
        Assert.Equal(first.LogicalFileName, registered.LogicalFileName);
        Assert.Same(first.Model, registered.Model);
        Assert.Same(first.Textures, registered.Textures);
        Assert.Equal("SHIPROOT.SHIPMESH", registered.RequestedObjectName);
        Assert.False(cache.TryFindPrototype("unregistered", out _));
        Assert.False(cache.TryFindPrototypeSource("unregistered", out _));
    }

    [Fact]
    public void R4ARenderingProjectConsumesAssetsWithoutReopeningNet7OrGpuDependencies()
    {
        string root = FindRepositoryRoot();
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "EnbNext.Rendering", "EnbNext.Rendering.csproj"));
        string[] dependencies = project.Descendants().Where(e=>e.Name.LocalName is "ProjectReference" or "PackageReference")
            .Select(e=>(string?)e.Attribute("Include")??"").ToArray();
        Assert.Contains(dependencies,d=>d.Contains("EnbNext.Assets",StringComparison.Ordinal));
        Assert.Contains(dependencies,d=>d.Contains("EnbNext.Presentation",StringComparison.Ordinal));
        Assert.DoesNotContain(dependencies,d=>d.Contains("EnbNext.Net7",StringComparison.OrdinalIgnoreCase));
        var assemblies=typeof(EnbW3dRenderResourceCompiler).Assembly.GetReferencedAssemblies().Select(a=>a.Name??"");
        foreach(string forbidden in new[]{"Avalonia","Veldrid","Silk.NET","Direct3D","OpenGL","Vulkan"})
            Assert.DoesNotContain(dependencies.Concat(assemblies),d=>d.Contains(forbidden,StringComparison.OrdinalIgnoreCase));
    }

    internal static EnbResolvedW3dResource BuildResolvedResource(
        uint shaderId,
        uint hierarchyVersion = 0x0003_0000u,
        uint hierarchyModelVersion = 0x0003_0000u,
        uint authoredSortLevel = 0,
        Vector3? boundingBoxMin = null,
        Vector3? boundingBoxMax = null,
        Vector3? boundingSphereCenter = null,
        bool includeNormals = true,
        bool includeDiffuseColor = true,
        uint meshVersion = 0x0004_0002u,
        ushort[]? vertexInfluences = null,
        bool includeHlodAggregate = false,
        bool includeHlodProxyArray = false,
        bool includeUnusedTexture = false,
        EnbW3dTextureInfo? textureInfo = null,
        bool hlodBeforeHierarchyModel = false,
        Vector3? hierarchyCenter = null,
        string? writePath = null,
        uint[]? vertexJoinMap = null,
        byte[]? aabTreePayload = null,
        uint mapperAttributes = 0x0004_0400u,
        string meshName = "SHIPMESH",
        string hierarchyName = "SHIP_TREE")
    {
        Vector3[] vertices =
        [
            new(1, 0, 0),
            new(0, 1, 0),
            new(0, 0, 1),
        ];
        Vector3[] normals = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
        Vector2[] sourceUvs =
        [
            new(0.10f, 0.20f),
            new(0.50f, 0.75f),
            new(0.90f, 1.25f),
        ];

        byte[] vertexMaterialInfo = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(vertexMaterialInfo.AsSpan(0, 4), mapperAttributes);
        new byte[] { 1, 2, 3, 0 }.CopyTo(vertexMaterialInfo, 4);
        new byte[] { 4, 5, 6, 0 }.CopyTo(vertexMaterialInfo, 8);
        new byte[] { 7, 8, 9, 0 }.CopyTo(vertexMaterialInfo, 12);
        new byte[] { 10, 11, 12, 0 }.CopyTo(vertexMaterialInfo, 16);
        WriteSingle(vertexMaterialInfo.AsSpan(20, 4), 1.5f);
        WriteSingle(vertexMaterialInfo.AsSpan(24, 4), 0.75f);
        WriteSingle(vertexMaterialInfo.AsSpan(28, 4), 0.25f);

        byte[] shader = [3, 1, 0, 0, 2, 1, 0, 1, 1, 2, 0, 2, 0, 2, 0, 2];
        byte[] diffuse =
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
        ];

        byte[] mesh = ContainerChunk(
            0x0000,
            LeafChunk(
                0x001F,
                BuildMeshHeader(
                    meshName,
                    "SHIPROOT",
                    1,
                    3,
                    5.5f,
                    authoredSortLevel,
                    boundingBoxMin ?? Vector3.Zero,
                    boundingBoxMax ?? Vector3.Zero,
                    boundingSphereCenter ?? Vector3.Zero,
                    meshVersion,
                    vertexInfluences is null
                        ? EnbW3dVertexChannels.Location | EnbW3dVertexChannels.Normal
                        : EnbW3dVertexChannels.Location | EnbW3dVertexChannels.Normal | EnbW3dVertexChannels.BoneId)),
            LeafChunk(0x0002, Vector3Payload(vertices)),
            vertexJoinMap is null ? Array.Empty<byte>() : LeafChunk(0x0022, UInt32Payload(vertexJoinMap)),
            aabTreePayload is null ? Array.Empty<byte>() : LeafChunk(0x0090, aabTreePayload),
            includeNormals ? LeafChunk(0x0003, Vector3Payload(normals)) : Array.Empty<byte>(),
            vertexInfluences is null ? Array.Empty<byte>() : LeafChunk(0x000E, VertexInfluencePayload(vertexInfluences)),
            LeafChunk(0x0020, TrianglePayload(0, 1, 2, 0x1234u, Vector3.UnitZ, -0.25f)),
            LeafChunk(0x0028, UInt32Payload(1, 1, 1, includeUnusedTexture ? 2u : 1u)),
            LeafChunk(0x0029, shader),
            ContainerChunk(
                0x002A,
                ContainerChunk(
                    0x002B,
                    LeafChunk(0x002C, Encoding.ASCII.GetBytes("R4 MATERIAL\0")),
                    LeafChunk(0x002D, vertexMaterialInfo),
                    LeafChunk(0x002E, Encoding.ASCII.GetBytes("UPerSec=.006\r\nUScale=6\0")))),
            ContainerChunk(
                0x0030,
                ContainerChunk(0x0031, LeafChunk(0x0032, Encoding.ASCII.GetBytes("PAINT.TGA\0")),
                    textureInfo is {} info ? LeafChunk(0x0033, UInt32Payload(info.Attributes, info.Word1, info.Word2)) : Array.Empty<byte>()),
                includeUnusedTexture ? ContainerChunk(0x0031,
                    LeafChunk(0x0032, Encoding.ASCII.GetBytes("UNUSED.TGA\0")),
                    LeafChunk(0x0033, UInt32Payload(4, 1, 0))) : Array.Empty<byte>()),
            ContainerChunk(
                0x0038,
                LeafChunk(0x0039, UInt32Payload(0)),
                LeafChunk(0x003A, UInt32Payload(shaderId)),
                includeDiffuseColor ? LeafChunk(0x003B, diffuse) : Array.Empty<byte>(),
                ContainerChunk(
                    0x0048,
                    LeafChunk(0x0049, UInt32Payload(0)),
                    LeafChunk(0x004A, Vector2Payload(sourceUvs)))));

        byte[] hierarchy = ContainerChunk(
            0x0100,
            LeafChunk(0x0101, HierarchyHeader(hierarchyVersion, hierarchyName, 2, hierarchyCenter ?? Vector3.Zero)),
            LeafChunk(
                0x0102,
                Concat(
                    Pivot("ROOT", uint.MaxValue, new Vector3(500, 600, 700), Vector3.Zero, 0, 0, 0, 1),
                    Pivot(
                        "MOUNT",
                        0,
                        new Vector3(10, 20, 30),
                        Vector3.Zero,
                        0,
                        0,
                        MathF.Sin(MathF.PI / 4f),
                        MathF.Cos(MathF.PI / 4f)))));

        byte[] hierarchyModel = ContainerChunk(
            0x0300,
            LeafChunk(0x0301, HierarchyModelHeader(hierarchyModelVersion, "SHIPROOT", "SHIP_TREE", 1)),
            LeafChunk(0x0302, HierarchyModelNode("SHIPMESH", 1)));

        byte[] hlodHeader = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(hlodHeader.AsSpan(0, 4), 0x0001_0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(hlodHeader.AsSpan(4, 4), 1u);
        WriteFixedAscii(hlodHeader.AsSpan(8, 16), "SHIPROOT");
        WriteFixedAscii(hlodHeader.AsSpan(24, 16), "SHIP_TREE");

        byte[] levelHeader = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(levelHeader.AsSpan(0, 4), 1u);
        WriteSingle(levelHeader.AsSpan(4, 4), float.MaxValue);
        byte[] member = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(member.AsSpan(0, 4), 77u);
        WriteFixedAscii(member.AsSpan(4, 32), "SHIPMESH");

        var hlodChildren = new List<byte[]>
        {
            LeafChunk(0x0701, hlodHeader),
            ContainerChunk(
                0x0702,
                LeafChunk(0x0703, levelHeader),
                LeafChunk(0x0704, member)),
        };
        if (includeHlodAggregate)
        {
            byte[] aggregateHeader = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(aggregateHeader.AsSpan(0, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(aggregateHeader.AsSpan(4, 4), 0x1122_3344u);
            hlodChildren.Add(ContainerChunk(
                0x0705,
                LeafChunk(0x0703, aggregateHeader),
                LeafChunk(0x0704, member)));
        }
        if (includeHlodProxyArray)
        {
            byte[] proxyHeader = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(proxyHeader.AsSpan(0, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(proxyHeader.AsSpan(4, 4), 0x5566_7788u);
            byte[] proxyMember = (byte[])member.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(proxyMember.AsSpan(0, 4), 1u);
            hlodChildren.Add(ContainerChunk(
                0x0706,
                LeafChunk(0x0703, proxyHeader),
                LeafChunk(0x0704, proxyMember)));
        }

        byte[] hlod = ContainerChunk(0x0700, hlodChildren.ToArray());

        byte[] bytes = hlodBeforeHierarchyModel ? Concat(mesh, hierarchy, hlod, hierarchyModel) :
            Concat(mesh, hierarchy, hierarchyModel, hlod);
        if (writePath is not null) File.WriteAllBytes(writePath, bytes);
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));

        string archivePath = Path.Combine(Path.GetTempPath(), "mixfile_art_07.mix");
        var w3dLocation = new EnbMixResourceLocation("SHIP.W3D", archivePath, 4096, bytes.Length);
        var textureLocation = new EnbMixResourceLocation("PAINT.DDS", archivePath, 8192, 256);
        return new EnbResolvedW3dResource(
            "SHIP.W3D",
            "SHIPROOT",
            w3dLocation,
            model,
            [new EnbResolvedTextureReference("PAINT.TGA", textureLocation)]);
    }

    private static byte[] HierarchyHeader(uint version, string name, uint pivotCount, Vector3 center)
    {
        byte[] payload = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), pivotCount);
        WriteSingle(payload.AsSpan(24, 4), center.X);
        WriteSingle(payload.AsSpan(28, 4), center.Y);
        WriteSingle(payload.AsSpan(32, 4), center.Z);
        return payload;
    }

    private static byte[] Pivot(
        string name,
        uint parentIndex,
        Vector3 translation,
        Vector3 eulerAngles,
        float q0,
        float q1,
        float q2,
        float q3)
    {
        byte[] payload = new byte[60];
        WriteFixedAscii(payload.AsSpan(0, 16), name);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(16, 4), parentIndex);
        WriteSingle(payload.AsSpan(20, 4), translation.X);
        WriteSingle(payload.AsSpan(24, 4), translation.Y);
        WriteSingle(payload.AsSpan(28, 4), translation.Z);
        WriteSingle(payload.AsSpan(32, 4), eulerAngles.X);
        WriteSingle(payload.AsSpan(36, 4), eulerAngles.Y);
        WriteSingle(payload.AsSpan(40, 4), eulerAngles.Z);
        WriteSingle(payload.AsSpan(44, 4), q0);
        WriteSingle(payload.AsSpan(48, 4), q1);
        WriteSingle(payload.AsSpan(52, 4), q2);
        WriteSingle(payload.AsSpan(56, 4), q3);
        return payload;
    }

    private static byte[] HierarchyModelHeader(
        uint version,
        string name,
        string hierarchyName,
        ushort connections)
    {
        byte[] payload = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), version);
        WriteFixedAscii(payload.AsSpan(4, 16), name);
        WriteFixedAscii(payload.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(36, 2), connections);
        return payload;
    }

    private static byte[] HierarchyModelNode(string name, ushort pivotIndex)
    {
        byte[] payload = new byte[18];
        WriteFixedAscii(payload.AsSpan(0, 16), name);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16, 2), pivotIndex);
        return payload;
    }

    private static byte[] BuildMeshHeader(
        string name,
        string container,
        uint triangleCount,
        uint vertexCount,
        float radius,
        uint authoredSortLevel,
        Vector3 boundingBoxMin,
        Vector3 boundingBoxMax,
        Vector3 boundingSphereCenter,
        uint version,
        uint vertexChannels)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), version);
        WriteFixedAscii(header.AsSpan(8, 16), name);
        WriteFixedAscii(header.AsSpan(24, 16), container);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), triangleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), authoredSortLevel);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), vertexChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(72, 4), EnbW3dFaceChannels.Face);
        WriteVector3(header.AsSpan(76, 12), boundingBoxMin);
        WriteVector3(header.AsSpan(88, 12), boundingBoxMax);
        WriteVector3(header.AsSpan(100, 12), boundingSphereCenter);
        WriteSingle(header.AsSpan(112, 4), radius);
        return header;
    }

    private static byte[] TrianglePayload(uint v0, uint v1, uint v2, uint attributes, Vector3 normal, float distance)
    {
        byte[] bytes = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), v0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), v1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), v2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), attributes);
        WriteSingle(bytes.AsSpan(16, 4), normal.X);
        WriteSingle(bytes.AsSpan(20, 4), normal.Y);
        WriteSingle(bytes.AsSpan(24, 4), normal.Z);
        WriteSingle(bytes.AsSpan(28, 4), distance);
        return bytes;
    }

    private static byte[] VertexInfluencePayload(IEnumerable<ushort> boneIndices)
    {
        ushort[] snapshot = boneIndices.ToArray();
        byte[] bytes = new byte[snapshot.Length * 8];
        for (int index = 0; index < snapshot.Length; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(index * 8, 2), snapshot[index]);
        return bytes;
    }

    private static byte[] UInt32Payload(params uint[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
        return bytes;
    }

    private static byte[] Vector2Payload(IEnumerable<Vector2> values)
    {
        Vector2[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 8];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan(index * 8, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 8) + 4, 4), snapshot[index].Y);
        }
        return bytes;
    }

    private static byte[] Vector3Payload(IEnumerable<Vector3> values)
    {
        Vector3[] snapshot = values.ToArray();
        byte[] bytes = new byte[snapshot.Length * 12];
        for (int index = 0; index < snapshot.Length; index++)
        {
            WriteSingle(bytes.AsSpan(index * 12, 4), snapshot[index].X);
            WriteSingle(bytes.AsSpan((index * 12) + 4, 4), snapshot[index].Y);
            WriteSingle(bytes.AsSpan((index * 12) + 8, 4), snapshot[index].Z);
        }
        return bytes;
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] ContainerChunk(uint type, params byte[][] children)
    {
        byte[] payload = Concat(children);
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), (uint)payload.Length | 0x8000_0000u);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] Concat(params byte[][] values)
    {
        byte[] result = new byte[values.Sum(static value => value.Length)];
        int offset = 0;
        foreach (byte[] value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static void WriteVector3(Span<byte> destination, Vector3 value)
    {
        WriteSingle(destination.Slice(0, 4), value.X);
        WriteSingle(destination.Slice(4, 4), value.Y);
        WriteSingle(destination.Slice(8, 4), value.Z);
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EnbNext.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate EnbNext repository root.");
    }
}
