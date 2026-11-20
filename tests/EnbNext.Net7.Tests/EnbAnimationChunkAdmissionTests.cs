using System.Buffers.Binary;
using System.Text;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbAnimationChunkAdmissionTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(AnimationCurveFixture.LargeCount)]
    public void SparseChannelsUseCompleteDeclaredKeysAndIgnoreUnreadTails(int count)
    {
        var header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x40002);
        Encoding.ASCII.GetBytes("MOVE").CopyTo(header, 4);
        Encoding.ASCII.GetBytes("TREE").CopyTo(header, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), (uint)count);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), 30);
        byte[] scalar = AnimationCurveFixture.Scalar(0, 10, 20, count);
        byte[] visibility = AnimationCurveFixture.Visibility(0, count);
        byte[] Build() => Chunk(0x280, [..Chunk(0x281, header, false),
            ..Chunk(0x282, scalar, false), ..Chunk(0x283, visibility, false)], true);
        byte[] bytes = Build();
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var animation = Assert.Single(model.Animations);
        Assert.Equal(count, Assert.Single(animation.CompressedChannels).TimeCodes.Count);
        Assert.Equal(count, Assert.Single(animation.CompressedBitChannels).TimeCodes.Count);
        var source = new EnbResolvedW3dResource("test.w3d", "TREE", new("test.w3d", "test.mix", 0, bytes.Length), model, []);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var compiled, out var error), error);
        var clip = Assert.Single(compiled.Animations);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip, 0, .5f, out var first, out error), error);
        Assert.Equal(15f, first.Translation.X); Assert.True(first.Visible);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip, 0, count - 1, out var last, out error), error);
        Assert.Equal(20f, last.Translation.X); Assert.False(last.Visible);
        BinaryPrimitives.WriteUInt32LittleEndian(scalar, uint.MaxValue);
        Assert.False(EnbW3dModel.TryDecode(Build(), out _));
        BinaryPrimitives.WriteUInt32LittleEndian(scalar, (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(visibility, uint.MaxValue);
        Assert.False(EnbW3dModel.TryDecode(Build(), out _));
    }

    [Theory]
    [InlineData(false, 1_000_001u, 100_001u)]
    [InlineData(false, uint.MaxValue, uint.MaxValue)]
    [InlineData(true, 1_000_001u, 30u)]
    [InlineData(true, uint.MaxValue, 65_535u)]
    public void HeaderDurationDoesNotAllocateOrLimitChannelStorage(bool compressed, uint frames, uint rate)
    {
        uint root = compressed ? 0x280u : 0x200u;
        byte[] header = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x40001);
        Encoding.ASCII.GetBytes("MOVE").CopyTo(header, 4);
        Encoding.ASCII.GetBytes("TREE").CopyTo(header, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), frames);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), rate);
        byte[] channel = new byte[compressed ? 24 : 20];
        if (compressed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(channel, 2);
            channel[6] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(12), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(channel.AsSpan(16), 1_000_000);
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(20), 20);
        }
        else
        {
            channel[2] = 1; channel[4] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(12), 10);
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(16), 20);
        }
        byte[] bytes = Chunk(root, [..Chunk(root + 1, header, false), ..Chunk(root + 2, channel, false)], true);
        Assert.True(EnbW3dModel.TryDecode(bytes, out var model));
        var animation = Assert.Single(model.Animations);
        Assert.Equal(frames, animation.NumFrames);
        Assert.Equal(rate, animation.FrameRate);
        var source = new EnbResolvedW3dResource("test.w3d", "TREE", new("test.w3d", "test.mix", 0, bytes.Length), model, []);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var compiled, out var error), error);
        var clip = Assert.Single(compiled.Animations);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip, 0, compressed ? 500_000f : .5f, out var midpoint, out error), error);
        //Classic009C3356 uses a signed frame-count comparison for the next
        //sample. A DWORD above INT_MAX wraps that next sample to frame zero.
        Assert.Equal(!compressed && frames > int.MaxValue ? 10f : 15f, midpoint.Translation.X);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(clip, 0, 1_000_000f, out var tail, out error), error);
        //Classic channels return zero outside their own ushort range;
        //compressed sparse channels retain their authored final key.
        Assert.Equal(compressed ? 20f : 0f, tail.Translation.X);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void FirstHeaderAndChannelsIgnoreHintsAndUnknownLaterRecords(bool compressed, bool hint)
    {
        uint root = compressed ? 0x280u : 0x200u;
        byte[] header = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x40001);
        Encoding.ASCII.GetBytes(" MOVE ").CopyTo(header, 4);
        Encoding.ASCII.GetBytes(" TREE ").CopyTo(header, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), 30);
        header[44] = 255;
        byte[] channel = new byte[compressed ? 24 : 20];
        if (compressed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(channel, 2);
            channel[6] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(12), 10);
            BinaryPrimitives.WriteUInt32LittleEndian(channel.AsSpan(16), 1);
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(20), 20);
        }
        else
        {
            channel[2] = 1; channel[4] = 1;
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(12), 10);
            BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(16), 20);
        }
        byte[] first = Chunk(root + 1, header, hint);
        byte[] children = [..first, ..Chunk(0x7fff0101, [255, 255, 255], hint),
            ..Chunk(root + 1, [], hint), ..Chunk(root + 2, channel, hint)];
        Assert.True(EnbW3dModel.TryDecode(Chunk(root, children, !hint), out var model));
        var animation = Assert.Single(model.Animations);
        Assert.Equal(" MOVE ", animation.Name);
        Assert.Equal(" TREE ", animation.HierarchyName);
        var source = new EnbResolvedW3dResource("test.w3d", "TREE", new("test.w3d", "test.mix", 0, children.Length), model, []);
        Assert.True(EnbW3dRenderResourceCompiler.TryCompile(source, out var compiled, out var error), error);
        Assert.True(EnbW3dAnimationSampler.TrySamplePivot(Assert.Single(compiled.Animations), 0, .5f,
            out var sample, out error), error);
        Assert.Equal(15f, sample.Translation.X);
        Assert.False(EnbW3dModel.TryDecode(Chunk(root, [..Chunk(0x7fff0101, [], false), ..first], true), out _));
        Assert.False(EnbW3dModel.TryDecode(Chunk(root, Chunk(root + 1, header[..43], false), true), out _));
    }

    private static byte[] Chunk(uint type, byte[] payload, bool hint)
    {
        var result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, type);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)payload.Length | (hint ? 0x80000000u : 0));
        payload.CopyTo(result, 8);
        return result;
    }
}
