using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Engine;
using EnbNext.Net7.World;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class EnbNullSpatialProviderTests
{
    // 6F2820 installs NullSpatialStateProvider (00B006B8) before any
    // positional packet; MainView+80 (514270) admits the RenderObj at identity.
    [Fact]
    public void CreatedObjectWithoutPositionPresentsTheNullProviderUntilAPositionArrives()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(123,1,1,4,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();
        var before=builder.Build(world,null,0).Objects.Single(o=>o.GameId==123);
        Assert.NotNull(before.Spatial);
        Assert.Equal(EnbSceneSpatialProviderKind.Null,before.Spatial!.ProviderKind);
        Assert.Equal(Vector3.Zero,before.Spatial.Transform.Position);
        Assert.Equal(Quaternion.Identity,before.Spatial.Transform.Orientation);
        Assert.Equal(Vector3.Zero,before.Spatial.Transform.Velocity);
        Assert.Equal(Matrix4x4.Identity,before.Spatial.Transform.ProviderTransform);

        byte[] packet=new byte[48];
        BinaryPrimitives.WriteUInt32LittleEndian(packet,123);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8),42);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32),1);
        world.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0008,packet),0);
        var after=builder.Build(world,null,0).Objects.Single(o=>o.GameId==123);
        Assert.Equal(EnbSceneSpatialProviderKind.Simple,after.Spatial!.ProviderKind);
    }

    [Fact]
    public void DuplicateGameIdKeepsItsOwnNullProvider()
    {
        // 7274D0 chains a second object into the same hash bucket; both remain
        // ClientGameObjects with their own provider.
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(77,1,1,4,0,0,0));
        world.MaterializeCreate(new Net7CreateUpdate(77,1,1,4,0,0,0));
        var objects=new EnbPresentationSnapshotBuilder().Build(world,null,0).Objects.Where(o=>o.GameId==77).ToArray();
        Assert.Equal(2,objects.Length);
        Assert.All(objects,o=>Assert.Equal(EnbSceneSpatialProviderKind.Null,o.Spatial!.ProviderKind));
    }

    [Theory]
    [InlineData((byte)0x1C,false)]
    [InlineData((byte)0x1D,false)]
    [InlineData((byte)0x00,true)]
    [InlineData((byte)0x08,true)]
    [InlineData((byte)0x29,true)]
    public void MainViewSceneAdmissionSkipsOnlyTypes1CAnd1D(byte objectType,bool admitted)
        => Assert.Equal(admitted,EnbWorldRenderer.MainViewAdmitsRenderObject(objectType));
}
