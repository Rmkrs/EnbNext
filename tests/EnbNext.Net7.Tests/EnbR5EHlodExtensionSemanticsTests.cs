using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR5EHlodExtensionSemanticsTests
{
    [Fact]
    public void RenderResourceCompilerCarriesTyped0705And0706Definitions()
    {
        EnbResolvedW3dResource source = EnbR4W3dRenderResourceTests.BuildResolvedResource(
            shaderId: 0,
            includeHlodAggregate: true,
            includeHlodProxyArray: true);

        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            source,
            out EnbW3dRenderResource resource,
            out string? diagnostic), diagnostic);

        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);
        EnbW3dRenderHlodAggregate aggregate = Assert.IsType<EnbW3dRenderHlodAggregate>(hlod.Aggregate);
        Assert.Equal(0x1122_3344u, aggregate.RawValue);
        EnbW3dRenderHlodMember aggregateMember = Assert.Single(aggregate.Members);
        Assert.Equal((uint)77, aggregateMember.ReferenceIndex);
        Assert.Equal("SHIPMESH", aggregateMember.Name);

        EnbW3dRenderHlodProxyArray proxyArray = Assert.IsType<EnbW3dRenderHlodProxyArray>(hlod.ProxyArray);
        Assert.Equal(0x5566_7788u, proxyArray.RawHeaderValue);
        EnbW3dRenderHlodMember proxyMember = Assert.Single(proxyArray.Members);
        Assert.Equal((uint)1, proxyMember.ReferenceIndex);
        Assert.Equal("SHIPMESH", proxyMember.Name);
    }

    [Fact]
    public void HlodDecoderUsesLast0705And0706LikeCurrentRetailRuntimeSlots()
    {
        byte[] header = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), 0x0001_0000u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 1u);
        WriteFixedAscii(header.AsSpan(8, 16), "LASTWINS");
        WriteFixedAscii(header.AsSpan(24, 16), "HIER");

        byte[] bytes = ContainerChunk(
            0x0700,
            LeafChunk(0x0701, header),
            HlodMemberContainer(0x0702, BitConverter.SingleToUInt32Bits(float.MaxValue), (0u, "BASE")),
            HlodMemberContainer(0x0705, 0x1111_1111u, (1u, "AGG_OLD")),
            HlodMemberContainer(0x0706, 0x2222_2222u, (2u, "PROXY_OLD")),
            HlodMemberContainer(0x0705, 0x3333_3333u, (3u, "AGG_NEW")),
            HlodMemberContainer(0x0706, 0x4444_4444u, (4u, "PROXY_NEW")));

        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dHierarchicalLod hlod = Assert.Single(model.HierarchicalLods);

        Assert.Equal(2, hlod.AggregateExtensions.Count);
        Assert.Equal(2, hlod.ProxyArrayExtensions.Count);

        EnbW3dHlodAggregate aggregate = Assert.IsType<EnbW3dHlodAggregate>(hlod.Aggregate);
        Assert.Equal(0x3333_3333u, aggregate.RawValue);
        Assert.Equal("AGG_NEW", Assert.Single(aggregate.Members).Name);
        Assert.Equal((uint)3, Assert.Single(aggregate.Members).ReferenceIndex);

        EnbW3dHlodProxyArray proxyArray = Assert.IsType<EnbW3dHlodProxyArray>(hlod.ProxyArray);
        Assert.Equal(0x4444_4444u, proxyArray.RawHeaderValue);
        Assert.Equal("PROXY_NEW", Assert.Single(proxyArray.Members).Name);
        Assert.Equal((uint)4, Assert.Single(proxyArray.Members).ReferenceIndex);
    }

    [Fact]
    public void AggregateChildrenExtendCompositeBoundsWhileProxyArrayDoesNot()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = ExtensionResource();

        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.CreateTranslation(100f, 0f, 0f),
            animation: null,
            animationFrame: 0f,
            out EnbW3dHlodBoundsOwner bounds,
            out string? diagnostic), diagnostic);

        // Ordinary levels cover A@0 and B@10. The 0x0705 aggregate adds C@20,
        // extending the AABB to [-1,21]. The 0x0706 proxy points at B@100 and
        // must not enter bounds; if it did, max X would become 101.
        Assert.Equal(new Vector3(10f, 0f, 0f), bounds.LocalBox.Center);
        Assert.Equal(new Vector3(11f, 1f, 1f), bounds.LocalBox.Extents);
        Assert.Equal(new Vector3(110f, 0f, 0f), bounds.WorldSphere.Center);
        Assert.Equal(MathF.Sqrt(123f), bounds.WorldSphere.Radius, 5);
    }

    [Fact]
    public void AggregateChildrenContributeFixedCostIndependentlyOfSelectableLevels()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = ExtensionResource();
        Dictionary<EnbW3dHlodMemberKey, EnbCurrentRetailRenderObjectState> states = ActiveMemberStates(hlod);

        Assert.True(EnbW3dHlodSceneGatherer.TryGather(
            Camera(),
            [new EnbW3dHlodSceneInstance(
                resource,
                hlod,
                0,
                Matrix4x4.CreateTranslation(100f, 0f, 0f),
                ActiveRoot(),
                states)],
            lodScale: 1f,
            out EnbW3dHlodSceneGatherResult gathered,
            out string? diagnostic), diagnostic);

        EnbHlodDetailOwner candidate = Assert.Single(gathered.Candidates);
        Assert.Equal(new[] { 1f, 1f }, candidate.LevelCosts);
        Assert.Equal(2f, gathered.FixedRenderCost);

        EnbW3dRenderHlodMember aggregateMember = Assert.Single(hlod.Aggregate!.Members);
        states[EnbW3dHlodMemberKey.From(aggregateMember)] = new EnbCurrentRetailRenderObjectState(0);

        Assert.True(EnbW3dHlodSceneGatherer.TryGather(
            Camera(),
            [new EnbW3dHlodSceneInstance(
                resource,
                hlod,
                0,
                Matrix4x4.CreateTranslation(100f, 0f, 0f),
                ActiveRoot(),
                states)],
            lodScale: 1f,
            out EnbW3dHlodSceneGatherResult inactiveAggregate,
            out diagnostic), diagnostic);

        Assert.Single(inactiveAggregate.Candidates);
        Assert.Equal(0f, inactiveAggregate.FixedRenderCost);
    }

    [Fact]
    public void AggregateMembersRemainDrawableForEverySelectedLevelAndProxyEntriesDoNot()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = ExtensionResource();

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection(hlod.Name, 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out string? diagnostic), diagnostic);

        string[] names = drawList.Packets
            .Select(packet => packet.RenderObjectName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(new[] { "A", "C" }, names);
        Assert.DoesNotContain(drawList.Packets, packet =>
            string.Equals(packet.RenderObjectName, "B", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProxyResolverReturnsNameAndCurrentHierarchyStateTransform()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = ExtensionResource();
        Assert.Equal(1, EnbW3dHlodProxyResolver.GetCount(hlod));

        Assert.True(EnbW3dHlodProxyResolver.TryResolve(
            resource,
            hlod,
            proxyOrdinal: 0,
            Matrix4x4.CreateTranslation(100f, 0f, 0f),
            animation: null,
            animationFrame: 0f,
            out EnbW3dHlodProxySample proxy,
            out string? diagnostic), diagnostic);

        Assert.Equal("B", proxy.Name);
        Assert.Equal(new Vector3(200f, 0f, 0f), Translation(proxy.WorldTransform));

        EnbW3dRenderHlod withoutProxy = hlod with { ProxyArray = null };
        Assert.Equal(0, EnbW3dHlodProxyResolver.GetCount(withoutProxy));
        Assert.False(EnbW3dHlodProxyResolver.TryResolve(
            resource,
            withoutProxy,
            0,
            Matrix4x4.Identity,
            null,
            0f,
            out EnbW3dHlodProxySample missing,
            out diagnostic));
        Assert.Equal(string.Empty, missing.Name);
        Assert.Equal(Matrix4x4.Identity, missing.WorldTransform);
        Assert.Contains("no current-retail 0x0706 proxy array", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProxyResolverUsesRawIndicesWithAnInsertedLegacyRoot()
    {
        (EnbW3dRenderResource resource, EnbW3dRenderHlod hlod) = ExtensionResource();
        EnbW3dRenderHierarchy hierarchy = Assert.Single(resource.Hierarchies);
        resource = resource.Copy().SetHierarchies([hierarchy.Copy().SetVersion(0x20000).SetHasSyntheticRetailRoot(true)]);

        Assert.True(EnbW3dHlodProxyResolver.TryResolve(
            resource,
            hlod,
            0,
            Matrix4x4.Identity,
            null,
            0f,
            out var proxy,
            out string? diagnostic),diagnostic);
        Assert.Equal("B",proxy.Name);
        Assert.Equal(new Vector3(100,0,0),Translation(proxy.WorldTransform));
    }

    [Fact]
    public void R5EImplementationCarriesCurrentRetailExtensionEvidenceAndStaysBackendNeutral()
    {
        string root = FindRepositoryRoot();
        string model = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Assets", "EnbW3dModel.cs"));
        string runtime = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbW3dHlodSceneRuntime.cs"));
        string budget = File.ReadAllText(Path.Combine(root, "src", "EnbNext.Rendering", "EnbRenderDetailBudgetAllocator.cs"));

        Assert.Contains("FUN_0098B7D0", model, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0098DAA0", runtime, StringComparison.Ordinal);
        Assert.Contains("0098DAC0", runtime, StringComparison.Ordinal);
        Assert.Contains("00942020", budget, StringComparison.Ordinal);
        Assert.Contains("0x0705", budget, StringComparison.OrdinalIgnoreCase);

        string combined = model + runtime + budget;
        Assert.DoesNotContain("Direct3D", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OpenGL", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vulkan", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Avalonia", combined, StringComparison.OrdinalIgnoreCase);
    }

    private static (EnbW3dRenderResource Resource, EnbW3dRenderHlod Hlod) ExtensionResource()
    {
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(
            EnbR4W3dRenderResourceTests.BuildResolvedResource(shaderId: 0),
            out EnbW3dRenderResource compiled,
            out string? diagnostic), diagnostic);

        EnbW3dRenderMesh template = Assert.Single(compiled.Meshes);
        EnbW3dRenderMesh a = (template).Copy().SetOrdinal(0).SetName("A").SetBoundingBoxMin(new Vector3(-1f, -1f, -1f)).SetBoundingBoxMax(new Vector3(1f, 1f, 1f)).SetBoundingSphereCenter(Vector3.Zero).SetBoundingRadius(MathF.Sqrt(3f)).SetCurrentRetailRenderPassCount(1);
        EnbW3dRenderMesh b = (template).Copy().SetOrdinal(1).SetName("B").SetBoundingBoxMin(new Vector3(-1f, -1f, -1f)).SetBoundingBoxMax(new Vector3(1f, 1f, 1f)).SetBoundingSphereCenter(Vector3.Zero).SetBoundingRadius(MathF.Sqrt(3f)).SetCurrentRetailRenderPassCount(1);
        EnbW3dRenderMesh c = (template).Copy().SetOrdinal(2).SetName("C").SetBoundingBoxMin(new Vector3(-1f, -1f, -1f)).SetBoundingBoxMax(new Vector3(1f, 1f, 1f)).SetBoundingSphereCenter(Vector3.Zero).SetBoundingRadius(MathF.Sqrt(3f)).SetCurrentRetailRenderPassCount(2);

        var hierarchy = new EnbW3dRenderHierarchy(
            0x0003_0000u,
            "HIER",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            new EnbW3dRenderPivot[]
            {
                new(0, "ROOT", uint.MaxValue, Vector3.Zero, Vector3.Zero, Quaternion.Identity, Matrix4x4.Identity),
                new(1, "P10", 0, new Vector3(10f, 0f, 0f), Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(10f, 0f, 0f)),
                new(2, "P20", 0, new Vector3(20f, 0f, 0f), Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(20f, 0f, 0f)),
                new(3, "P100", 0, new Vector3(100f, 0f, 0f), Vector3.Zero, Quaternion.Identity, Matrix4x4.CreateTranslation(100f, 0f, 0f)),
            },
            Array.Empty<EnbW3dRenderPivotFixup>());

        var hlod = new EnbW3dRenderHlod(
            "EXTLOD",
            "HIER",
            new EnbW3dRenderHlodLevel[]
            {
                new(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), [new EnbW3dRenderHlodMember(0, "A")]),
                new(1, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), [new EnbW3dRenderHlodMember(1, "B")]),
            })
        {
            Aggregate = new EnbW3dRenderHlodAggregate(
                0x1122_3344u,
                [new EnbW3dRenderHlodMember(2, "C")]),
            ProxyArray = new EnbW3dRenderHlodProxyArray(
                0x5566_7788u,
                [new EnbW3dRenderHlodMember(3, "B")]),
        };

        return (compiled.Copy().SetMeshes([a, b, c]).SetHierarchies([hierarchy]).SetHierarchyModels(Array.Empty<EnbW3dRenderHierarchyModel>()).SetHierarchicalLods([hlod]), hlod);
    }

    private static Dictionary<EnbW3dHlodMemberKey, EnbCurrentRetailRenderObjectState> ActiveMemberStates(
        EnbW3dRenderHlod hlod)
    {
        IEnumerable<EnbW3dRenderHlodMember> members = hlod.Levels.SelectMany(level => level.Members);
        if (hlod.Aggregate is EnbW3dRenderHlodAggregate aggregate)
            members = members.Concat(aggregate.Members);

        return members
            .Distinct()
            .ToDictionary(
                EnbW3dHlodMemberKey.From,
                _ => new EnbCurrentRetailRenderObjectState(EnbCurrentRetailRenderObjectState.ActivityMask));
    }

    private static EnbCurrentRetailRenderObjectState ActiveRoot()
        => new(EnbCurrentRetailRenderObjectState.ActivityMask);

    private static EnbResolvedRenderCamera Camera()
    {
        Assert.True(EnbRenderViewport.TryFromLogicalSize(1000, 500, 1, out EnbRenderViewport viewport));
        Assert.True(EnbResolvedRenderCamera.TryCreate(
            Vector3.Zero,
            Quaternion.Identity,
            viewport,
            Math.PI / 2,
            new EnbRenderClipRange(1f, 10_000f),
            out EnbResolvedRenderCamera camera));
        return camera;
    }

    private static EnbSceneObjectSnapshot SceneObject()
        => new(
            RenderIdentity: 0x1122_3344_5566_7788UL,
            VisualGeneration: 7,
            GameId: 0x1234,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 1,
            BaseAsset: 42,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R5E fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: new Vector3(100f, 0f, 0f),
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

    private static Vector3 Translation(Matrix4x4 matrix)
        => new(matrix.M41, matrix.M42, matrix.M43);

    private static byte[] HlodMemberContainer(uint type, uint rawSecondHeaderValue, params (uint ReferenceIndex, string Name)[] members)
    {
        byte[] header = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), checked((uint)members.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), rawSecondHeaderValue);

        var children = new List<byte[]> { LeafChunk(0x0703, header) };
        foreach ((uint referenceIndex, string name) in members)
        {
            byte[] member = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(member.AsSpan(0, 4), referenceIndex);
            WriteFixedAscii(member.AsSpan(4, 32), name);
            children.Add(LeafChunk(0x0704, member));
        }
        return ContainerChunk(type, children.ToArray());
    }

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        bytes.AsSpan(0, Math.Min(bytes.Length, destination.Length - 1)).CopyTo(destination);
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static child => child).ToArray(), container: true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        uint size = checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), size);
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src")) &&
                Directory.Exists(Path.Combine(current.FullName, "tests")))
                return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
