using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z5CurrentRetailParticleEmitterRenderObjectTests
{
    [Fact]
    public void DirectNamedPrototypeRequiresExactParticleEmitterNameHeader()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] emitterHeader = new byte[0x14];
            Encoding.ASCII.GetBytes("PWEAPEXHAUST\0").CopyTo(emitterHeader.AsSpan(4));
            WriteMix1(
                Path.Combine(directory, "mixfile_art_01.mix"),
                ("PWEAPEXHAUST.W3D", ContainerChunk(0x0500u, LeafChunk(0x0501u, emitterHeader))),
                ("NOTEMITTER.W3D", LeafChunk(0x0700u, Encoding.ASCII.GetBytes("NOTEMITTER\0"))));

            EnbMixResourceCatalog catalog = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX1 catalog was not loaded.");

            Assert.True(EnbInstalledW3dPrototypeTokenAudit.TryResolveDirectParticleEmitterPrototype(
                catalog,
                "pweapexhaust",
                out EnbMixResourceLocation location));
            Assert.Equal("PWEAPEXHAUST.W3D", location.Name);
            Assert.False(EnbInstalledW3dPrototypeTokenAudit.TryResolveDirectParticleEmitterPrototype(
                catalog,
                "NOTEMITTER",
                out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ParticleEmitterMemberUsesZeroBoundsZeroCostAndNoOpDraw()
    {
        EnbW3dRenderResource resource = ParticleEmitterResource(includeUnknown: false);
        EnbW3dRenderHlod source = Assert.Single(resource.HierarchicalLods);
        EnbW3dRenderHlod hlod = source with
        {
            Aggregate = new EnbW3dRenderHlodAggregate(
                0u,
                [new EnbW3dRenderHlodMember(0u, "PWEAPEXHAUST")]),
        };

        Assert.True(EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.Identity,
            animation: null,
            animationFrame: 0f,
            out EnbW3dHlodBoundsOwner bounds,
            out string? diagnostic), diagnostic);
        Assert.Equal(Vector3.Zero, bounds.LocalBox.Center);
        Assert.Equal(Vector3.Zero, bounds.LocalBox.Extents);
        Assert.Equal(0f, bounds.LocalSphere.Radius);

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dLevelRenderCosts(
            resource,
            hlod,
            static _ => true,
            out ImmutableArray<int> levelCosts,
            out diagnostic), diagnostic);
        Assert.Equal(new[] { 0 }, levelCosts);

        Assert.True(EnbW3dHlodBudgetCompiler.TryComputeW3dAggregateFixedRenderCost(
            resource,
            hlod,
            static _ => true,
            out float aggregateCost,
            out diagnostic), diagnostic);
        Assert.Equal(EnbW3dHlodBudgetCompiler.CurrentRetailZeroCostFallback, aggregateCost);

        Assert.True(EnbW3dDrawPacketCompiler.TryBuild(
            SceneObject(),
            resource,
            frameTimeMilliseconds: 0,
            renderPass: EnbRenderPassKind.OrdinaryWorld,
            hlodSelection: new EnbW3dHlodSelection(hlod.Name, 0),
            lightEnvironment: EnbW3dLightEnvironment.BlackNoLights,
            out EnbW3dDrawInvocation drawList,
            out diagnostic), diagnostic);
        Assert.Empty(drawList.Packets);
    }

    [Fact]
    public void UnboundUnknownNonMeshMemberRemainsFailClosed()
    {
        EnbW3dRenderResource resource = ParticleEmitterResource(includeUnknown: true);
        EnbW3dRenderHlod hlod = Assert.Single(resource.HierarchicalLods);

        Assert.False(EnbW3dHlodCompositeBoundsCompiler.TryCompute(
            resource,
            hlod,
            Matrix4x4.Identity,
            animation: null,
            animationFrame: 0f,
            out _,
            out string? diagnostic));
        Assert.Contains("other retail render-object classes remain fail-closed", diagnostic, StringComparison.Ordinal);
    }

    private static EnbW3dRenderResource ParticleEmitterResource(bool includeUnknown)
    {
        var hierarchy = new EnbW3dRenderHierarchy(
            0x30000u,
            "ROOT",
            Vector3.Zero,
            HasSyntheticRetailRoot: false,
            [
                new EnbW3dRenderPivot(
                    0,
                    "ROOT",
                    uint.MaxValue,
                    Vector3.Zero,
                    Vector3.Zero,
                    Quaternion.Identity,
                    Matrix4x4.Identity),
            ],
            Array.Empty<EnbW3dRenderPivotFixup>());

        var members = new List<EnbW3dRenderHlodMember>
        {
            new(0u, "PWEAPEXHAUST"),
        };
        if (includeUnknown)
            members.Add(new EnbW3dRenderHlodMember(0u, "NOT_A_PROVEN_RENDER_OBJECT"));

        var hlod = new EnbW3dRenderHlod(
            "ROOT",
            "ROOT",
            [new EnbW3dRenderHlodLevel(0, float.MaxValue, BitConverter.SingleToUInt32Bits(float.MaxValue), members)]);

        return new EnbW3dRenderResource(
            new EnbW3dRenderResourceKey("PSS04.W3D", "test.mix", 0, 0),
            "PSS04.W3D",
            Array.Empty<EnbW3dRenderMesh>(),
            [hierarchy],
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            [hlod])
        {
            ExternalRenderObjects =
            [
                new EnbW3dExternalRenderObject(
                    "PWEAPEXHAUST",
                    EnbW3dExternalRenderObjectKind.ParticleEmitter,
                    "pweapexhaust.w3d",
                    "mixfile_art_17.mix"),
            ],
        };
    }

    private static EnbSceneObjectSnapshot SceneObject()
        => new(
            RenderIdentity: 0x5566_7788_99AA_BBCCUL,
            VisualGeneration: 1,
            GameId: 0x1549,
            IsLocalShip: false,
            IsCurrentTarget: false,
            ObjectType: 0x25,
            BaseAsset: 1549,
            Scale: 1f,
            HsvH: 0,
            HsvS: 0,
            HsvV: 0,
            RelationshipRaw: 0,
            IsAttacking: false,
            Name: "R6Z5 ParticleEmitter fixture",
            Radar: new EnbSceneRadarSnapshot(false, 0, null),
            Spatial: new EnbSceneSpatialSnapshot(
                EnbSceneSpatialProviderKind.Fixed,
                new EnbSceneTransformSnapshot(
                    ClientTime: 100,
                    Position: Vector3.Zero,
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

    private static void WriteMix1(string path, params (string Name, byte[] Payload)[] resources)
    {
        const uint headerLength = 16;
        uint cursor = headerLength;
        var dataOffsets = new uint[resources.Length];
        for (int index = 0; index < resources.Length; index++)
        {
            dataOffsets[index] = cursor;
            cursor = checked(cursor + (uint)resources[index].Payload.Length);
        }

        uint directoryOffset = cursor;
        uint filenameTableOffset = checked(directoryOffset + 4u + (uint)(resources.Length * 12));
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(0x3158494Du);
        writer.Write(directoryOffset);
        writer.Write(filenameTableOffset);
        writer.Write(0u);
        foreach (var resource in resources)
            writer.Write(resource.Payload);
        writer.Write((uint)resources.Length);
        for (int index = 0; index < resources.Length; index++)
        {
            writer.Write(unchecked(0xB500_0000u + (uint)index));
            writer.Write(dataOffsets[index]);
            writer.Write((uint)resources[index].Payload.Length);
        }
        writer.Write((uint)resources.Length);
        foreach (var resource in resources)
        {
            byte[] nameBytes = Encoding.ASCII.GetBytes(resource.Name + "\0");
            writer.Write((byte)nameBytes.Length);
            writer.Write(nameBytes);
        }
    }

    private static byte[] LeafChunk(uint type, byte[] payload)
        => Chunk(type, payload, container: false);

    private static byte[] ContainerChunk(uint type, params byte[][] children)
        => Chunk(type, children.SelectMany(static bytes => bytes).ToArray(), container: true);

    private static byte[] Chunk(uint type, byte[] payload, bool container)
    {
        byte[] bytes = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4, 4),
            checked((uint)payload.Length) | (container ? 0x8000_0000u : 0u));
        payload.CopyTo(bytes, 8);
        return bytes;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "enbnext-r6z5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
