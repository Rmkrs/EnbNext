using System.Numerics;
using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbShakePresentationTests
{
    [Fact]
    public void FrameSamplesFinalFieldsAndIndependentRevisions()
    {
        var world = CreateWorld();
        Apply(world, (44, 2), (45, 3));
        Assert.Null(Snapshot(world).Force);
        Apply(world, (43, 1), (46, 20));
        var detached = Snapshot(world);
        Assert.Equal(new Vector3(1, 2, 3), detached.Force);
        Assert.Equal(20f, detached.Damage);
        Apply(world, (43, 5), (46, 80));
        Apply(world, (44, 7)); // Same-frame Y update participates in the pending X impulse.
        var current = Snapshot(world);
        Assert.Equal(new Vector3(5, 7, 3), current.Force);
        Assert.Equal(80f, current.Damage);
        Assert.Equal(2ul, current.ForceRevision);
        Assert.Equal(2ul, current.DamageRevision);
        Assert.Equal(new Vector3(1, 2, 3), detached.Force);
        Apply(world, (46, 80));
        Assert.Equal(current.ForceRevision, Snapshot(world).ForceRevision);
        Assert.Equal(3ul, Snapshot(world).DamageRevision);
    }

    [Fact]
    public void StateZeroCancelsEarlierSameFrameShake()
    {
        var world = CreateWorld();
        Apply(world, (43, 5), (46, 80));
        using var delta = new MemoryStream();
        using (var writer = new BinaryWriter(delta, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)0); writer.Write(0u); writer.Write(0u);
            writer.Write(2u); writer.Write(43u); writer.Write(46u);
        }
        ApplyDelta(world, delta.ToArray());
        Assert.Null(Snapshot(world).Force);
        Assert.Null(Snapshot(world).Damage);
        Apply(world, (43, 5));
        Assert.Equal(2ul, Snapshot(world).ForceRevision);
        Assert.Equal(new Vector3(5, 0, 0), Snapshot(world).Force);
    }

    [Fact]
    public void ShortDeltaRetainsCompletedForceWithoutInventingDamage()
    {
        var world = CreateWorld();
        Apply(world, (43, 1));
        var before = Snapshot(world);
        using var delta = new MemoryStream();
        using (var writer = new BinaryWriter(delta, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)0); writer.Write(2u);
            writer.Write(43u); writer.Write(99f);
            writer.Write(46u); writer.Write((ushort)0);
        }
        ApplyDelta(world, delta.ToArray());
        Assert.Equal(before.ForceRevision+1,Snapshot(world).ForceRevision);
        Assert.Equal(new Vector3(99,0,0),Snapshot(world).Force);
        Assert.Null(world.Objects.FindFirst(23)!.AuxData!.Resolve(46).Value);
        Apply(world, (44, 8));
        Assert.Equal(before.ForceRevision+1, Snapshot(world).ForceRevision);
        Assert.Equal(new Vector3(99, 8, 0), Snapshot(world).Force);
    }

    private static Net7WorldState CreateWorld()
    {
        var world = new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23, 1, 10, 1, 0, 0, 0));
        return world;
    }

    private static EnbSceneShakeSnapshot Snapshot(Net7WorldState world) =>
        new EnbPresentationSnapshotBuilder().Build(world, null, 0).Objects[0].ShipPresentation!.Shake;

    private static void Apply(Net7WorldState world, params (uint Id, float Value)[] values)
    {
        using var delta = new MemoryStream();
        using (var writer = new BinaryWriter(delta, System.Text.Encoding.UTF8, true))
        {
            writer.Write((byte)0); writer.Write((uint)values.Length);
            foreach (var (id, value) in values) { writer.Write(id); writer.Write(value); }
            writer.Write(0u); writer.Write(0u);
        }
        ApplyDelta(world, delta.ToArray());
    }

    private static void ApplyDelta(Net7WorldState world, byte[] delta)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, true))
        {
            writer.Write(23u); writer.Write((ushort)delta.Length); writer.Write(delta);
        }
        world.Apply(Packet.ForOpcode(0x001B, payload.ToArray()), 0);
    }
}
