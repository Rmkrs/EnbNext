using System.Numerics;
using EnbNext.Rendering;
using Xunit;

namespace EnbNext.Net7.Tests;

public sealed class EnbR6PStaticSortFrameSubmissionTests
{
    [Fact]
    public void ImmediateTraversalPrecedesDescendingDeferredBucketsWithFifoInsideEachBucket()
    {
        EnbW3dDrawTask[] traversal =
        [
            Packet(1, EnbW3dStaticSortSemantics.FromAuthoredValue(0)),
            Packet(2, EnbW3dStaticSortSemantics.FromAuthoredValue(10)),
            Packet(3, EnbW3dStaticSortSemantics.FromAuthoredValue(0)),
            Packet(4, EnbW3dStaticSortSemantics.FromAuthoredValue(32)),
            Packet(5, EnbW3dStaticSortSemantics.FromAuthoredValue(10)),
            Packet(6, EnbW3dStaticSortSemantics.FromAuthoredValue(1)),
        ];

        Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(
            EnbRenderPassKind.SharedScene,
            traversal,
            out EnbW3dFrameSubmissionOrder order,
            out string? diagnostic), diagnostic);

        Assert.Equal(new uint[] { 1, 3, 4, 2, 5, 6 }, order.Packets.Select(static packet => packet.GameId).ToArray());
        Assert.Equal(2, order.ImmediatePacketCount);
        Assert.Equal(4, order.DeferredPacketCount);
        Assert.Equal(6, order.PacketCount);
    }

    [Fact]
    public void ShaderDerivedR4nSortLevelUsesTheSameApplicationBucketQueue()
    {
        EnbW3dStaticSortSemantics synthesized = EnbW3dStaticSortSemantics.ResolveCurrentRetail(
            authoredValue: 0,
            new EnbW3dGeometrySortSemantics(
                Requested: true,
                PassZeroShaderClassMask: 0x10,
                AllPassShaderClassMask: 0x10));
        Assert.Equal((sbyte)20, synthesized.RuntimeLevel);

        EnbW3dDrawTask[] traversal =
        [
            Packet(10, synthesized),
            Packet(11, EnbW3dStaticSortSemantics.FromAuthoredValue(32)),
            Packet(12, EnbW3dStaticSortSemantics.FromAuthoredValue(0)),
        ];

        Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(
            EnbRenderPassKind.SharedScene,
            traversal,
            out EnbW3dFrameSubmissionOrder order,
            out string? diagnostic), diagnostic);

        Assert.Equal(new uint[] { 12, 11, 10 }, order.Packets.Select(static packet => packet.GameId).ToArray());
    }

    [Theory]
    [InlineData(33u)]
    [InlineData(255u)]
    public void CurrentRetailRejectedSortLevelIsNotQueuedAndTheFrameContinues(uint authoredSortLevel)
    {
        // 0097D060 accepts only levels 1..32; any other level returns without queueing.
        EnbW3dDrawTask[] traversal =
        [
            Packet(19, EnbW3dStaticSortSemantics.FromAuthoredValue(0)),
            Packet(20, EnbW3dStaticSortSemantics.FromAuthoredValue(authoredSortLevel)),
            Packet(21, EnbW3dStaticSortSemantics.FromAuthoredValue(5)),
        ];

        Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(
            EnbRenderPassKind.SharedScene,
            traversal,
            out EnbW3dFrameSubmissionOrder order,
            out string? diagnostic), diagnostic);
        Assert.Equal(new uint[] { 19, 21 }, order.Packets.Select(static packet => packet.GameId).ToArray());
    }

    [Fact]
    public void SceneSortQueueDoesNotLeakAcrossRenderPassBoundaries()
    {
        EnbW3dDrawTask[] traversal =
        [
            Packet(30, EnbW3dStaticSortSemantics.FromAuthoredValue(0), EnbRenderPassKind.SharedScene),
            Packet(31, EnbW3dStaticSortSemantics.FromAuthoredValue(10), EnbRenderPassKind.SpecializedDepthRange),
        ];

        Assert.False(EnbW3dFrameSubmissionOrderCompiler.TryCompile(
            EnbRenderPassKind.SharedScene,
            traversal,
            out _,
            out string? diagnostic));
        Assert.Contains("not requested pass", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void RetainedWorkspacePreservesAllBucketsAndDrainsFailuresWithoutChangingOldViews()
    {
        var workspace=new EnbW3dFrameSubmissionWorkspace();
        var traversal=Enumerable.Range(0,132).Select(i=>Packet((uint)i,
            EnbW3dStaticSortSemantics.FromAuthoredValue((uint)(i%33)))).ToArray();
        var expected=traversal.Where(p=>p.OrderKey.StaticSort.RuntimeLevel==0)
            .Concat(traversal.Where(p=>p.OrderKey.StaticSort.RuntimeLevel!=0)
                .OrderByDescending(p=>p.OrderKey.StaticSort.RuntimeLevel)).ToArray();
        Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.SharedScene,
            traversal,out var first,out var error,workspace),error);
        for(int frame=0;frame<100;frame++)
        {
            Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.SharedScene,
                traversal,out var current,out error,workspace),error);
            Assert.Equal(expected,current.Packets.ToArray());
        }
        Assert.False(EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.SharedScene,
            [traversal[1],traversal[0],Packet(999,EnbW3dStaticSortSemantics.FromAuthoredValue(5),EnbRenderPassKind.SpecializedDepthRange)],out _,out _,workspace));
        Assert.True(EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.SharedScene,
            [],out var empty,out error,workspace),error);
        Assert.Empty(empty.Packets);
        Assert.Equal(expected,first.Packets.ToArray());
        Assert.Empty(workspace.Immediate);
        Assert.Empty(workspace.Active);
        Assert.All(workspace.Deferred,bucket=>Assert.True(bucket is null||bucket.Count==0));
    }

    private static EnbW3dDrawTask Packet(
        uint gameId,
        EnbW3dStaticSortSemantics staticSort,
        EnbRenderPassKind renderPass = EnbRenderPassKind.SharedScene)
    {
        var key = new EnbW3dDrawOrderKey(
            renderPass,
            EnbW3dDrawBlendClass.MixedOrUnspecified,
            staticSort,
            new EnbW3dRenderResourceKey("fixture.w3d", "fixture.mix", 0, 1),
            MeshOrdinal: 0,
            MaterialPassOrdinal: 0,
            RenderIdentity: gameId,
            BindingOrdinal: 0);

        return new EnbW3dDrawTask(
            key,
            RenderIdentity: gameId,
            VisualGeneration: 1,
            GameId: gameId,
            WorldTransform: Matrix4x4.Identity,
            Bounds: new EnbW3dWorldBounds(Vector3.Zero, 1f),
            Mesh: null!,
            DeformedGeometry: null,
            Material: null!,
            LightEnvironment: null!,
            FrameTimeMilliseconds: 0,
            HierarchyName: null,
            PivotIndex: 0,
            RenderObjectName: $"fixture-{gameId}");
    }
}
