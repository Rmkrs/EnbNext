using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbRetailRslResourceGraphTests
{
    [Fact]
    public void LibraryTemplatesRetainIdentityUntilCatalogDefinitionIsReplaced()
    {
        const string text = "[0\\STATE0]\nTYPE=object\nOBJECT=child\n";
        var catalog = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(catalog.TryGetLibrary(0, out var library));
        var resolver = new EnbRetailRslResourceResolver(null);
        var first = resolver.Resolve(library);
        var second = resolver.Resolve(library);
        Assert.Same(first, second);
        Assert.Same(first.States[0], second.States[0]);
        var replacement = EnbRetailRenderStateCatalog.ParseText(text);
        Assert.True(replacement.TryGetLibrary(0, out var other));
        var replaced = resolver.Resolve(other);
        Assert.NotSame(first, replaced);
        Assert.NotSame(first.States[0], replaced.States[0]);
        Assert.Same(replaced, resolver.Resolve(other));
    }

    [Theory]
    [InlineData("object", "RS_PRECACHED", true)]
    [InlineData("object", "RS_ACTIVATE_NEXT", false)]
    [InlineData("bone", "RS_PRECACHED", false)]
    [InlineData("bone", "RS_ACTIVATE_NEXT", true)]
    [InlineData("anim", "", true)]
    [InlineData("link", "RS_PRECACHED", false)]
    public void FilePreloadUsesEachNativeFactoryFlag(string type, string flag, bool expected)
    {
        var catalog = EnbRetailRenderStateCatalog.ParseText($"[0\\STATE0]\nTYPE={type}\nOBJECT=child\n{flag}=1\n");
        Assert.True(catalog.TryGetLibrary(0, out var library));
        Assert.Equal(expected, EnbCurrentRetailRslEffectPassLibrary.PreloadsFile(library.States[0]));
    }

    [Fact]
    public void EffectPassLibraryPreservesInterleavedConstructionAndCopiesWithoutSamplerWrites()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                ("first.ini", Encoding.ASCII.GetBytes("[Effect_Pass_Description]\nEFFECT_PASS_TYPE=Environment\nTEXTURE=env.tga\nSHADER=2\nHIDDEN=T\n")),
                ("second.ini", Encoding.ASCII.GetBytes("[Effect_Pass_Description]\nEFFECT_PASS_TYPE=Environment\nTEXTURE=other.tga\nSHADER=3\nTWO_SIDED=T\n")));
            var catalog = EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nTYPE=effect_pass\nFILE=first.ini\n[0\\STATE1]\nTYPE=object\nOBJECT=child\n[0\\STATE2]\nTYPE=effect_pass\nFILE=second.ini\n");
            Assert.True(catalog.TryGetLibrary(0, out var definition));
            var graph = new EnbRetailRslResourceResolver(EnbMixResourceCatalog.Load(directory)).Resolve(definition);
            using var resources = new EnbW3dRenderResourceCache(texture =>
                (texture.LogicalName, new EnbW3dTextureCreationRequest(EnbW3dTextureSamplingSemantics.RetailDefault, 0)));
            int children = 0;
            using var template = EnbCurrentRetailRslEffectPassLibrary.Create(graph, resources, state =>
            {
                Assert.Equal(1, state.Definition.StateId);
                using var texture = resources.AcquireRslEffectPassTexture("env.tga");
                Assert.Equal(EnbRenderTextureAddressMode.Clamp, texture.State.Sampling.AddressU);
                texture.State.ApplyW3dInfo(new EnbW3dTextureInfo(0, 0, 0));
                children++;
            });
            Assert.Equal(1, children);
            Assert.Equal(new[] { 2, 0 }, template.States.Select(state => state.StateId));
            using var instance = template.Copy();
            Assert.Equal(new[] { 0, 2 }, instance.States.Select(state => state.StateId));
            var first = instance.States[0].Owner;
            var second = instance.States[1].Owner;
            Assert.Equal(EnbRenderTextureAddressMode.Wrap,
                first.Material.TextureStages[0].Textures[0].CurrentRetailTextureState!.Sampling.AddressU);
            Assert.NotSame(template.States[1].Owner.Material.VertexMaterials[0].Material,
                first.Material.VertexMaterials[0].Material);
            template.Dispose();
            var context = instance.CreateContext([2, 0]);
            Assert.Same(second.Material, context.Passes[0]);
            Assert.Same(first.Material, context.Passes[1]);
            Assert.Equal(7u, context.Flags);
            Assert.False(context.AdmitsBaseMesh(0));
            context.Restore();
            context.Restore();
            Assert.Empty(context.Passes);
            Assert.Equal(0u, context.Flags);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ObjectLazyAndAuthoredFilesUseLooseProviderBeforeCompetingPackages()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string release = Path.Combine(directory, "release");
            Directory.CreateDirectory(release);
            string authored = Path.Combine(release, "authored.w3d");
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0, writePath: authored,
                hierarchyCenter: new(7, 8, 9));
            var looseBytes = File.ReadAllBytes(authored);
            string lazy = Path.Combine(release, "SHIPROOT.w3d");
            EnbR4W3dRenderResourceTests.BuildResolvedResource(0, writePath: lazy);
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                ("authored.w3d", File.ReadAllBytes(lazy)), ("SHIPROOT.w3d", looseBytes));
            var packages = EnbMixResourceCatalog.Load(directory)!;
            var files = new EnbRetailFileResolver(release, packages);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, packages,
                "SHIPROOT", out var source, out var error), error);
            Assert.Equal(lazy, source.LooseFilePath);
            Assert.Equal(System.Numerics.Vector3.Zero, source.Model.Hierarchies[0].Center);
            var catalog = EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nTYPE=object\nOBJECT=SHIPROOT\nFILE=authored.w3d\n");
            Assert.True(catalog.TryGetLibrary(0, out var library));
            var dependency = new EnbRetailRslResourceResolver(packages, packages, files).Resolve(library).States[0].FileDependency;
            Assert.True(dependency.IsResolved, dependency.Diagnostic);
            Assert.Equal(authored, dependency.LooseFilePath);
            Assert.Equal(new System.Numerics.Vector3(7, 8, 9), dependency.Model!.Hierarchies[0].Center);
            File.Delete(lazy);
            Assert.True(EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(files, packages,
                "SHIPROOT", out source, out error), error);
            Assert.Null(source.LooseFilePath);
            Assert.Equal(new System.Numerics.Vector3(7, 8, 9), source.Model.Hierarchies[0].Center);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void EffectPassUsesNativeLooseSearchBeforePackages()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string release=Path.Combine(directory,"release");
            string art=Path.Combine(directory,"data","client","art");
            string ini=Path.Combine(directory,"data","client","ini");
            Directory.CreateDirectory(release); Directory.CreateDirectory(art); Directory.CreateDirectory(ini);
            string Text(int shader)=>$"[Effect_Pass_Description]\nEFFECT_PASS_TYPE=Environment\nTEXTURE=env.tga\nSHADER={shader}\n";
            WriteMix1(Path.Combine(directory,"mixfile_art_01.mix"),("environment.ini",Encoding.ASCII.GetBytes(Text(2))),
                ("env.dds",[0x44,0x44,0x53,0x20]));
            var packages=EnbMixResourceCatalog.Load(directory)!;
            File.WriteAllText(Path.Combine(release,"environment.ini"),Text(3));
            File.WriteAllText(Path.Combine(art,"environment.ini"),Text(4));
            File.WriteAllText(Path.Combine(ini,"environment.ini"),Text(5));
            var catalog=EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nID=0\nTYPE=effect_pass\nFILE=environment.ini\n");
            Assert.True(catalog.TryGetLibrary(0,out var library));
            EnbResolvedRslFileDependency Resolve()
                => new EnbRetailRslResourceResolver(packages,packages,new EnbRetailFileResolver(release,packages))
                    .Resolve(library).States[0].FileDependency;
            foreach(var expected in new[] {(Directory:release,Shader:3),(Directory:art,Shader:4),(Directory:ini,Shader:5)})
            {
                var dependency=Resolve();
                Assert.Equal(EnbCurrentRetailRslEffectPassDefinition.ShaderPreset(expected.Shader),dependency.EffectPass!.PackedShader);
                Assert.Equal(Path.Combine(expected.Directory,"environment.ini"),dependency.LooseFilePath);
                Assert.Null(dependency.ResourceLocation);
                File.Delete(Path.Combine(expected.Directory,"environment.ini"));
            }
            var packaged=Resolve();
            Assert.Equal(EnbCurrentRetailRslEffectPassDefinition.ShaderPreset(2),packaged.EffectPass!.PackedShader);
            Assert.Null(packaged.LooseFilePath); Assert.NotNull(packaged.ResourceLocation);
        }
        finally {Directory.Delete(directory,true);}
    }

    [Fact]
    public void EffectPassFileResolvesAsIniWithIndependentTextureAndStateEdges()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(Path.Combine(directory, "mixfile_art_01.mix"),
                ("environment.ini", Encoding.ASCII.GetBytes("[Effect_Pass_Description]\nEFFECT_PASS_TYPE=Environment\nTEXTURE=env.tga\nSHADER=2\nHIDDEN=T\n")),
                ("env.dds", [0x44, 0x44, 0x53, 0x20]));
            var resources = EnbMixResourceCatalog.Load(directory)!;
            var catalog = EnbRetailRenderStateCatalog.ParseText("[0\\STATE0]\nID=0\nTYPE=effect_pass\nFILE=environment.ini\nNEXT=1\nLINK=1\n[0\\STATE1]\nID=1\nTYPE=link\n");
            Assert.True(catalog.TryGetLibrary(0, out var library));
            var graph = new EnbRetailRslResourceResolver(resources).Resolve(library);
            Assert.True(graph.IsComplete);
            Assert.True(graph.TryFindState(0, out var state));
            Assert.Equal(EnbRslStateType.EffectPass, state.Definition.Type);
            Assert.Equal(EnbRslFileDependencyStatus.ResolvedEffectPass, state.FileDependency.Status);
            Assert.Null(state.FileDependency.Model);
            Assert.Equal(0xd401bu, state.FileDependency.EffectPass!.PackedShader);
            Assert.True(state.FileDependency.EffectPass.Hidden);
            Assert.Equal("env.dds", Assert.Single(state.FileDependency.Textures!).Location!.Name);
            Assert.Equal([1], state.ReferencedStateIds);
            var traversal = EnbCurrentRetailRslLinkActivationRuntime.Resolve(graph, 0);
            var plan = EnbCurrentRetailRslClassActivationRuntime.Resolve(graph, traversal);
            Assert.True(plan.IsExecutableWithKnownCurrentObject);
            Assert.Equal(EnbCurrentRetailRslClassActivationEffect.MaterialPass, plan.Steps[0].Effect);
            Assert.Null(plan.FinalCurrentObjectStateId);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ResourceGraphKeepsFileObjectAnimationAndSoundAsDistinctAuthoredInputs()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("ship.w3d", BuildMinimalW3d("SHIP", "paint.tga")),
                ("anim.w3d", BuildMinimalAnimationW3d("SHIP.IDLE")),
                ("paint.dds", [0x44, 0x44, 0x53, 0x20]));

            EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
                """
                [0\STATE0]
                ID=0
                TYPE=object
                FILE=ship.w3d
                OBJECT=SHIP
                LINK=1
                RS_PRECACHED=T

                [0\STATE1]
                ID=1
                TYPE=anim
                FILE=anim.w3d
                ANIM=SHIP.IDLE
                SUFFIX=-2
                NEXT=2

                [0\STATE2]
                ID=2
                TYPE=sound
                SOUND=GATE ACTIVE IDLE
                LINK=3

                [0\STATE3]
                ID=3
                TYPE=object
                OBJECT=GLOBAL_ONLY
                """);

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));

            var resolver = new EnbRetailRslResourceResolver(resources);
            EnbResolvedRslLibraryResourceGraph graph = resolver.Resolve(library);

            Assert.True(graph.IsComplete);
            Assert.Equal(4, graph.States.Count);
            Assert.Empty(graph.MissingReferencedStateIds);

            Assert.True(graph.TryFindState(0, out EnbResolvedRslStateResource objectState));
            Assert.Equal("SHIP", objectState.ObjectPrototypeName);
            Assert.Null(objectState.AnimationName);
            Assert.True(objectState.FileDependency.IsResolved);
            Assert.Equal("ship.w3d", objectState.FileDependency.LogicalFileName);
            Assert.Equal("ship.w3d", objectState.FileDependency.ResourceLocation!.Name);
            Assert.Single(objectState.FileDependency.Model!.Meshes);
            EnbResolvedTextureReference texture = Assert.Single(objectState.FileDependency.Textures!);
            Assert.Equal("paint.tga", texture.LogicalName);
            Assert.Equal("paint.dds", texture.Location!.Name);
            Assert.Equal(new[] { 1 }, objectState.ReferencedStateIds);

            Assert.True(graph.TryFindState(1, out EnbResolvedRslStateResource animState));
            Assert.Equal("SHIP.IDLE", animState.AnimationName);
            Assert.Equal("-2", animState.AnimationSuffix);
            Assert.True(animState.FileDependency.IsResolved);
            Assert.Single(animState.FileDependency.Model!.Animations);
            Assert.Equal(new[] { 2 }, animState.ReferencedStateIds);

            Assert.True(graph.TryFindState(2, out EnbResolvedRslStateResource soundState));
            Assert.Equal("GATE ACTIVE IDLE", soundState.SoundName);
            Assert.Equal(EnbRslFileDependencyStatus.NotAuthored, soundState.FileDependency.Status);
            Assert.Equal(new[] { 3 }, soundState.ReferencedStateIds);

            Assert.True(graph.TryFindState(3, out EnbResolvedRslStateResource globalObjectState));
            Assert.Equal("GLOBAL_ONLY", globalObjectState.ObjectPrototypeName);
            Assert.Equal(EnbRslFileDependencyStatus.NotAuthored, globalObjectState.FileDependency.Status);
            Assert.Empty(globalObjectState.ReferencedStateIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ResourceGraphReportsMissingInvalidFilesAndBrokenStateReferencesWithoutInventingFallbacks()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("broken.w3d", [0x01, 0x02, 0x03, 0x04]));

            EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
                """
                [0\STATE0]
                ID=0
                TYPE=object
                FILE=missing.w3d
                OBJECT=MISSING
                NEXT=8

                [0\STATE1]
                ID=1
                TYPE=object
                FILE=broken.w3d
                OBJECT=BROKEN
                LINK=9
                """);

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));

            EnbResolvedRslLibraryResourceGraph graph =
                new EnbRetailRslResourceResolver(resources).Resolve(library);

            Assert.False(graph.IsComplete);
            Assert.Equal(1, graph.MissingFileCount);
            Assert.Equal(1, graph.InvalidW3dCount);
            Assert.Equal(new[] { 8, 9 }, graph.MissingReferencedStateIds);

            Assert.True(graph.TryFindState(0, out EnbResolvedRslStateResource missing));
            Assert.Equal(EnbRslFileDependencyStatus.MissingResource, missing.FileDependency.Status);
            Assert.Equal(new[] { 8 }, missing.MissingReferencedStateIds);

            Assert.True(graph.TryFindState(1, out EnbResolvedRslStateResource invalid));
            Assert.Equal(EnbRslFileDependencyStatus.InvalidW3d, invalid.FileDependency.Status);
            Assert.Equal("broken.w3d", invalid.FileDependency.ResourceLocation!.Name);
            Assert.Equal(new[] { 9 }, invalid.MissingReferencedStateIds);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BaseAssetRslResolutionCarriesTheResolvedInstalledResourceGraph()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("station.w3d", BuildMinimalW3d("STATION", "station.tga")),
                ("station.dds", [0x44, 0x44, 0x53, 0x20]));

            var presentation = new EnbRetailPresentationCatalog();
            presentation.LoadBaseAssetsText(
                """
                [BASE-47]
                RSLID=0
                RSID=0
                """);

            EnbRetailRenderStateCatalog renderStates = EnbRetailRenderStateCatalog.ParseText(
                """
                [0\STATE0]
                ID=0
                TYPE=object
                FILE=station.w3d
                OBJECT=STATION
                """);

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            var rslResources = new EnbRetailRslResourceResolver(resources);
            var resolver = new EnbRetailBaseAssetResolver(
                presentation,
                resources,
                renderStates,
                rslResources);

            EnbBaseAssetResolution resolution = resolver.Resolve(47);

            Assert.True(resolution.IsResolved);
            EnbResolvedRslReference rsl = Assert.IsType<EnbResolvedRslReference>(resolution.Rsl);
            EnbResolvedRslLibraryResourceGraph graph = Assert.IsType<EnbResolvedRslLibraryResourceGraph>(rsl.ResourceGraph);
            Assert.True(graph.IsComplete);
            Assert.True(graph.TryFindState(0, out EnbResolvedRslStateResource state));
            Assert.Equal("STATION", state.ObjectPrototypeName);
            Assert.True(state.FileDependency.IsResolved);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SuffixIsPreservedAsAuthoredTextBecauseRetailConsumesAString()
    {
        EnbRetailRenderStateCatalog catalog = EnbRetailRenderStateCatalog.ParseText(
            """
            [0\STATE0]
            ID=0
            TYPE=anim
            ANIM=TURN
            SUFFIX=-13
            """);

        Assert.True(catalog.TryGetLibrary(0, out EnbRslLibraryDefinition library));
        EnbRslStateDefinition state = Assert.Single(library.States);
        Assert.Equal("-13", state.Suffix);
    }

    private static byte[] BuildMinimalW3d(string meshName, string textureName)
    {
        byte[] header = new byte[116];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(8, 16), meshName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), 1u);
        BinaryPrimitives.WriteInt32LittleEndian(
            header.AsSpan(112, 4),
            BitConverter.SingleToInt32Bits(1.0f));

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

    private static byte[] BuildMinimalAnimationW3d(string name)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0004_0002u);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), "SHIP");
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), 1u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), 30u);
        return ContainerChunk(0x0200, LeafChunk(0x0201, header));
    }

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
        => BinaryPrimitives.WriteInt32LittleEndian(
            destination,
            BitConverter.SingleToInt32Bits(value));

    private static void WriteMix1(
        string path,
        params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;

        uint dataLength = checked((uint)resources.Sum(resource => resource.Payload.Length));
        uint directoryOffset = checked(headerLength + dataLength);
        uint filenameTableOffset = checked(
            directoryOffset + 4u + ((uint)resources.Length * 12u));

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
        foreach (var (name, _) in resources)
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
