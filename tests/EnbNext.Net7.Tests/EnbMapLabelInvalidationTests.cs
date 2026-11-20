using EnbNext.Net7.World;
using EnbNext.Presentation;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class EnbMapLabelInvalidationTests
{
    [Fact]
    public void PvpInvalidationCarriesDirtyStateRatherThanBooleanValue()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23,1,10,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();
        void Apply(bool clear)
        {
            using var delta=new MemoryStream();
            using(var writer=new BinaryWriter(delta,System.Text.Encoding.UTF8,true))
            {
                writer.Write((byte)0);writer.Write(clear?0u:1u);
                if(!clear){writer.Write(30u);writer.Write((byte)0);}
                writer.Write(0u);writer.Write(clear?1u:0u);if(clear)writer.Write(30u);
            }
            using var payload=new MemoryStream();
            using(var writer=new BinaryWriter(payload,System.Text.Encoding.UTF8,true))
            {writer.Write(23u);writer.Write((ushort)delta.Length);writer.Write(delta.ToArray());}
            world.Apply(Packet.ForOpcode(0x001B,payload.ToArray()),0);
        }
        Apply(false);
        var before=builder.Build(world,null,0).Objects[0];
        Assert.True(before.PvpUpdated);Assert.Equal(1ul,before.PvpUpdateRevision);
        Apply(false);
        Assert.Equal(2ul,builder.Build(world,null,0).Objects[0].PvpUpdateRevision);
        Apply(true);
        Assert.False(builder.Build(world,null,0).Objects[0].PvpUpdated);
        Assert.True(before.PvpUpdated);
    }

    [Fact]
    public void BatchedNotificationsStopAtMissingObjectAndPreserveRepeatedValues()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23,1,10,1,0,0,0));
        world.MaterializeCreate(new Net7CreateUpdate(25,1,10,1,0,0,0));
        byte[] reaction=new byte[28];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reaction,3);
        for(int i=0;i<3;i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(reaction.AsSpan(4+i*8),(uint)(23+i));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(reaction.AsSpan(8+i*8),0);
        }
        world.Apply(Packet.ForOpcode(0x008A,reaction),0);
        world.Apply(Packet.ForOpcode(0x008A,reaction),0);
        byte[] attacker=new byte[17];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(attacker.AsSpan(1),3);
        for(int i=0;i<3;i++)System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(attacker.AsSpan(5+i*4),(uint)(23+i));
        world.Apply(Packet.ForOpcode(0x008B,attacker),0);
        Assert.Equal(3ul,world.Objects.FindFirst(23)!.RelationshipNotificationRevision);
        Assert.Equal(0ul,world.Objects.FindFirst(25)!.RelationshipNotificationRevision);
    }

    [Fact]
    public void TransientCensusAndIdenticalRelationshipStillInvalidateDetachedLabels()
    {
        var world=new Net7WorldState();
        world.MaterializeCreate(new Net7CreateUpdate(23,1,10,1,0,0,0));
        var builder=new EnbPresentationSnapshotBuilder();
        var before=builder.Build(world,null,0);
        world.MaterializeCreate(new Net7CreateUpdate(24,1,10,1,0,0,0));
        world.MaterializeRemove(24);
        var census=builder.Build(world,null,0);
        Assert.Equal(before.Objects.Select(o=>o.GameId),census.Objects.Select(o=>o.GameId));
        Assert.Equal(before.WorldRegistryRevision+2,census.WorldRegistryRevision);
        var obj=world.Objects.FindFirst(23)!;
        var reaction=new Net7RelationshipUpdate(23,obj.RelationshipRaw,obj.IsAttacking);
        world.MaterializeRelationship(reaction);
        world.MaterializeRelationship(reaction);
        var after=builder.Build(world,null,0);
        Assert.Equal(before.Objects[0].RelationshipNotificationRevision+2,after.Objects[0].RelationshipNotificationRevision);
        Assert.Equal(before.Objects[0].RelationshipRaw,after.Objects[0].RelationshipRaw);
        Assert.Equal(0ul,before.Objects[0].RelationshipNotificationRevision);
        world.MaterializeRelationship(reaction with {ObjectId=999});
        world.MaterializeRemove(999);
        Assert.Equal(after.Objects[0].RelationshipNotificationRevision,obj.RelationshipNotificationRevision);
        Assert.Equal(census.WorldRegistryRevision,world.Objects.MembershipRevision);
    }
}
