using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6Z21CurrentRetailCompressedHAnimFlavor0Tests
{
    [Fact]
    public void PersistentRegistryRetainsFirstCompressedClipAcrossDifferentFiles()
    {
        EnbResolvedRslFileDependency File(string file,float endpoint)
        {
            var bytes=BuildCompressedAnimationW3d(0x40002,"A","RIG",11,30,0,
                BuildChannel(1,EnbW3dAnimationChannelKind.TranslationX,1,(0u,new[]{0f}),(10u,new[]{endpoint})));
            Assert.True(EnbW3dModel.TryDecode(bytes,out var model));
            return new(file,EnbRslFileDependencyStatus.ResolvedW3d,Model:model);
        }
        var registry=new EnbCurrentRetailRslHAnimRegistryRuntime();
        var first=File("first.w3d",10);var second=File("second.w3d",90);
        Assert.True(registry.TryResolve(EmptyResource("root"),first,null,null,null,"RIG.A",out var original,out var error),error);
        Assert.True(registry.TryResolve(EmptyResource("other-root"),second,null,null,null,"RIG.A",out var duplicate,out error),error);
        Assert.Same(original.Animation,duplicate.Animation);
        Assert.Equal("FILE:first.w3d",duplicate.Source);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(duplicate.Animation,1,5,out var sample,out error),error);
        Assert.Equal(5,sample.Translation.X);
        registry.Clear();
        Assert.True(registry.TryResolve(EmptyResource("root"),second,null,null,null,"RIG.A",out var replacement,out error),error);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(replacement.Animation,1,5,out sample,out error),error);
        Assert.Equal(45,sample.Translation.X);
    }

    [Fact]
    public void Flavor0SparseScalarUsesNextKeyStepBitAndDoesNotApplyClassicPivotShift()
    {
        byte[] bytes = BuildCompressedAnimationW3d(
            version: 0x0002_0000u,
            name: "TSG02A-1",
            hierarchyName: "TSG02A",
            numFrames: 31,
            frameRate: 30,
            flavor: 0,
            BuildChannel(
                pivotIndex: 1,
                kind: EnbW3dAnimationChannelKind.TranslationX,
                vectorLength: 1,
                (0u, new float[] { 0f }),
                (10u, new float[] { 10f }),
                (0x8000_0014u, new float[] { 20f }),
                (30u, new float[] { 30f })));

        EnbW3dRenderHierarchyAnimation animation = DecodeAndCompileSingle(bytes);
        Assert.Equal(EnbW3dHAnimFamily.Compressed0280, animation.Family);
        Assert.Equal("TSG02A-1", animation.Name);
        Assert.Equal("TSG02A", animation.HierarchyName);
        Assert.Equal(31u, animation.NumFrames);
        Assert.Equal(30u, animation.FrameRate);
        Assert.Equal((ushort?)0, animation.CompressionFlavor);
        Assert.False(animation.HasLegacySyntheticRootChannelShift);
        EnbW3dRenderCompressedAnimationChannel channel = Assert.Single(animation.CompressedChannels);
        Assert.Equal((ushort)1, channel.PivotIndex);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 5f, out EnbW3dPivotAnimationSample at5, out string? diagnostic5), diagnostic5);
        Assert.Equal(5f, at5.Translation.X, 4);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 15f, out EnbW3dPivotAnimationSample at15, out string? diagnostic15), diagnostic15);
        Assert.Equal(10f, at15.Translation.X, 4);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 25f, out EnbW3dPivotAnimationSample at25, out string? diagnostic25), diagnostic25);
        Assert.Equal(25f, at25.Translation.X, 4);

        // Native __ftol truncates a negative fractional entry to integer key 0,
        // retaining the signed interpolation fraction; -1 wraps unsigned and
        // selects the final sparse key (009C6D60).
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, -.75f, out var signed, out var signedDiagnostic), signedDiagnostic);
        Assert.Equal(-.75f, signed.Translation.X, 4);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, -1f, out var wrapped, out var wrappedDiagnostic), wrappedDiagnostic);
        Assert.Equal(30f, wrapped.Translation.X, 4);
    }

    [Fact]
    public void Flavor0QuaternionUsesRetailShortestPathInterpolation()
    {
        byte[] bytes = BuildCompressedAnimationW3d(
            version: 0x0004_0002u,
            name: "TURN",
            hierarchyName: "DOOR",
            numFrames: 11,
            frameRate: 30,
            flavor: 0,
            BuildChannel(
                pivotIndex: 1,
                kind: EnbW3dAnimationChannelKind.Quaternion,
                vectorLength: 4,
                (0u, new float[] { 0f, 0f, 0f, 1f }),
                (10u, new float[] { 0f, 0f, 1f, 0f })));

        EnbW3dRenderHierarchyAnimation animation = DecodeAndCompileSingle(bytes);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 5f, out EnbW3dPivotAnimationSample sample, out string? diagnostic), diagnostic);

        float rootHalf = MathF.Sqrt(0.5f);
        Assert.Equal(0f, sample.Rotation.X, 4);
        Assert.Equal(0f, sample.Rotation.Y, 4);
        Assert.Equal(rootHalf, sample.Rotation.Z, 4);
        Assert.Equal(rootHalf, sample.Rotation.W, 4);
    }

    [Fact]
    public void Flavor0VisibilityUsesLatestSparseKeyHighBit()
    {
        byte[] bytes = BuildCompressedAnimationW3d(
            version: 0x0004_0002u,
            name: "VIS",
            hierarchyName: "DOOR",
            numFrames: 31,
            frameRate: 30,
            flavor: 0,
            BuildBitChannel(
                pivotIndex: 1,
                kind: EnbW3dAnimationBitChannelKind.Visibility,
                0x8000_0000u,
                10u,
                0x8000_0014u));

        EnbW3dRenderHierarchyAnimation animation = DecodeAndCompileSingle(bytes);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 5.9f, out EnbW3dPivotAnimationSample beforeOff, out string? beforeDiagnostic), beforeDiagnostic);
        Assert.True(beforeOff.Visible);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 15.9f, out EnbW3dPivotAnimationSample off, out string? offDiagnostic), offDiagnostic);
        Assert.False(off.Visible);

        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(animation, 1, 25.9f, out EnbW3dPivotAnimationSample onAgain, out string? onDiagnostic), onDiagnostic);
        Assert.True(onAgain.Visible);
    }

    [Fact]
    public void LazyResolverPromotesFlavor0CompressedAnimationIntoExecutableDrawResource()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            byte[] bytes = BuildCompressedAnimationW3d(
                version: 0x0004_0002u,
                name: "TSG02A-1",
                hierarchyName: "TSG02A",
                numFrames: 31,
                frameRate: 30,
                flavor: 0,
                BuildChannel(
                    pivotIndex: 1,
                    kind: EnbW3dAnimationChannelKind.TranslationX,
                    vectorLength: 1,
                    (0u, new float[] { 0f }),
                    (30u, new float[] { 30f })));
            WriteMix1(Path.Combine(directory, "mixfile_art_11.mix"), ("TSG02A-1.W3D", bytes));

            EnbMixResourceCatalog resources = EnbMixResourceCatalog.Load(directory)
                ?? throw new Xunit.Sdk.XunitException("Synthetic MIX catalog was not loaded.");
            var noAuthoredFile = new EnbResolvedRslFileDependency(
                string.Empty,
                EnbRslFileDependencyStatus.NotAuthored);

            Assert.True(EnbCurrentRetailRslHAnimResolver.TryResolve(
                EmptyResource("TSG02A.W3D"),
                noAuthoredFile,
                resources,
                "TSG02A.TSG02A-1",
                out EnbCurrentRetailRslHAnimResolution resolution,
                out string? diagnostic), diagnostic);

            Assert.Equal(EnbW3dHAnimFamily.Compressed0280, resolution.Animation.Family);
            Assert.Equal((ushort?)0, resolution.Animation.CompressionFlavor);
            Assert.Contains(
                "lazy:TSG02A-1.w3d@MIX:mixfile_art_11.mix/TSG02A-1.W3D",
                resolution.Source,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("lazy-header:", resolution.Source, StringComparison.OrdinalIgnoreCase);
            EnbW3dRenderHierarchyAnimation appended = Assert.Single(resolution.DrawResource.Animations);
            Assert.Same(resolution.Animation, appended);
            Assert.True(EnbW3dAnimationSampler.TrySamplePivot(
                resolution.Animation,
                1,
                7.5f,
                out EnbW3dPivotAnimationSample sample,
                out string? sampleDiagnostic), sampleDiagnostic);
            Assert.Equal(7.5f, sample.Translation.X, 4);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Flavor1RemainsHeaderVisibleButOutsideR6Z21ExecutionBoundary()
    {
        byte[] bytes = BuildCompressedAnimationW3d(
            version: 0x0004_0002u,
            name: "ALT",
            hierarchyName: "DOOR",
            numFrames: 12,
            frameRate: 24,
            flavor: 1);

        Assert.True(EnbW3dHAnimHeaderProbe.TryInspect(bytes, out IReadOnlyList<EnbW3dHAnimHeaderInfo> headers, out string? diagnostic), diagnostic);
        EnbW3dHAnimHeaderInfo header = Assert.Single(headers);
        Assert.Equal((ushort?)1, header.CompressionFlavor);
        Assert.False(EnbW3dModel.TryDecode(bytes, out _));
    }

    private static EnbW3dRenderHierarchyAnimation DecodeAndCompileSingle(byte[] bytes)
    {
        Assert.True(EnbW3dModel.TryDecode(bytes, out EnbW3dModel model));
        EnbW3dHierarchyAnimation decoded = Assert.Single(model.Animations);
        Assert.Equal(decoded.HierarchyName + "." + decoded.Name, EnbCurrentRetailRslHAnimResolver.BuildRegistryName(decoded.Name, decoded.HierarchyName));
        Assert.True(EnbW3dRenderResourceCompiler.TryCompileAnimation(0, decoded, out EnbW3dRenderHierarchyAnimation compiled, out string? diagnostic), diagnostic);
        return compiled;
    }

    private static byte[] BuildCompressedAnimationW3d(
        uint version,
        string name,
        string hierarchyName,
        uint numFrames,
        ushort frameRate,
        ushort flavor,
        params byte[][] channels)
    {
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0, 4), version);
        WriteFixedAscii(header.AsSpan(4, 16), name);
        WriteFixedAscii(header.AsSpan(20, 16), hierarchyName);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36, 4), numFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40, 2), frameRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42, 2), flavor);

        var children = new byte[channels.Length + 1][];
        children[0] = LeafChunk(0x0281, header);
        Array.Copy(channels, 0, children, 1, channels.Length);
        return ContainerChunk(0x0280, children);
    }

    private static byte[] BuildChannel(
        ushort pivotIndex,
        EnbW3dAnimationChannelKind kind,
        byte vectorLength,
        params (uint TimeCode, float[] Values)[] keys)
    {
        Assert.NotEmpty(keys);
        byte[] payload = new byte[checked(8 + (keys.Length * (1 + vectorLength) * 4))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)keys.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), pivotIndex);
        payload[6] = vectorLength;
        payload[7] = checked((byte)kind);

        int cursor = 8;
        foreach ((uint timeCode, float[] values) in keys)
        {
            Assert.Equal((int)vectorLength, values.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(cursor, 4), timeCode);
            cursor += 4;
            foreach (float value in values)
            {
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(cursor, 4), value);
                cursor += 4;
            }
        }

        return LeafChunk(0x0282, payload);
    }

    private static byte[] BuildBitChannel(
        ushort pivotIndex,
        EnbW3dAnimationBitChannelKind kind,
        params uint[] timeCodes)
    {
        Assert.NotEmpty(timeCodes);
        byte[] payload = new byte[checked(8 + (timeCodes.Length * 4))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), checked((uint)timeCodes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), pivotIndex);
        payload[6] = checked((byte)kind);
        payload[7] = 0;
        for (int index = 0; index < timeCodes.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8 + (index * 4), 4), timeCodes[index]);
        return LeafChunk(0x0283, payload);
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

    private static EnbW3dRenderResource EmptyResource(string logicalName)
        => new(
            new EnbW3dRenderResourceKey(logicalName, "test.mix", 0, 0),
            logicalName,
            Array.Empty<EnbW3dRenderMesh>(),
            Array.Empty<EnbW3dRenderHierarchy>(),
            Array.Empty<EnbW3dRenderHierarchyAnimation>(),
            Array.Empty<EnbW3dRenderHierarchyModel>(),
            Array.Empty<EnbW3dRenderHlod>());

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

    private static void WriteFixedAscii(Span<byte> destination, string value)
    {
        destination.Clear();
        byte[] encoded = Encoding.ASCII.GetBytes(value);
        Assert.True(encoded.Length < destination.Length);
        encoded.CopyTo(destination);
    }
}
