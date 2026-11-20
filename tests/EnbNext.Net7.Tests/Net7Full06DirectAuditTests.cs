using System.Buffers.Binary;
using System.Numerics;
using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Presentation;
using EnbNext.Net7.Protocol;
using EnbNext.Net7.World;
using EnbNext.Protocol.Net;

namespace EnbNext.Net7.Tests;

public sealed class Net7Full06DirectAuditTests
{
    [Fact]
    public void DurationLinkedEffectUsesCurrentRawCodecAndZeroNormalDuration()
    {
        byte[] payload = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(0x01020304);
            w.U32(0x11121314);
            w.U32(0x21222324);
            w.CString("ROOT");
            w.U32(0x31323334);
            w.I16(-9);
            w.U16(15);
            w.Vector3(new Vector3(1.25f, 2.5f, 3.75f));
            w.I16(-7);
            w.I16(8);
            w.U8(1);
            w.F32(4.5f);
            w.Vector3(new Vector3(.1f, .2f, .3f));
            w.F32(.5f);
        });

        Net7ObjectToObjectDurationLinkedEffectUpdate decoded =
            Net7EffectsRenderWire.DecodeObjectToObjectDurationLinkedEffect(payload);

        Assert.Equal(0x01020304u, decoded.EffectId);
        Assert.Equal(0x11121314u, decoded.Timestamp);
        Assert.Equal(0x21222324u, decoded.SourceGameId);
        Assert.Equal("ROOT", decoded.SourceBoneChain);
        Assert.Equal(0x31323334u, decoded.TargetGameId);
        Assert.Equal((short)-9, decoded.DurationLink);
        Assert.Equal((ushort)15, decoded.EffectDefinitionId);
        Assert.Equal(new Vector3(1.25f, 2.5f, 3.75f), decoded.SpatialOffset);
        Assert.Equal((short)-7, decoded.EndpointShortA);
        Assert.Equal((short)8, decoded.EndpointShortB);
        Assert.Equal((byte)1, decoded.DescriptorFlag);
        Assert.Equal(4.5f, decoded.Scale);
        Assert.Equal(new Vector3(.1f, .2f, .3f), decoded.HsvShift);
        Assert.Equal(.5f, decoded.Speedup);

        var state = new Net7GameplayState(convertServerTimestamp: timestamp => timestamp + 3);
        Assert.True(state.Apply(Packet.ForOpcode(0x000E, payload)));
        Net7EffectState effect = state.Effects[0x01020304];
        Assert.Equal(0, effect.DurationMilliseconds);
        Assert.Equal((short)-9, effect.DurationLink);
        Assert.Equal(0x11121317u, effect.Timestamp);
        Assert.Equal(new Vector3(1.25f, 2.5f, 3.75f), effect.SpatialOffset);
        Assert.Equal(unchecked((ushort)-7), effect.EndpointShortA);
        Assert.Equal((ushort)8, effect.EndpointShortB);
        Assert.True(effect.DescriptorFlag);
    }

    [Fact]
    public void AllOrdinaryEffectFamiliesUseClientTimeCorrection()
    {
        var session = new Net7Session(new Net7ServerOptions());
        byte[] setTime = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(setTime.AsSpan(0, 4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(setTime.AsSpan(4, 4), 120);
        BinaryPrimitives.WriteUInt32LittleEndian(setTime.AsSpan(8, 4), 125);
        session.World.Time.ApplySetClientTime(setTime, 150); // correction = -2, so server 1000 -> client 1002

        byte[] objectEffect = new byte[15];
        objectEffect[0] = 0x03;
        Net7EffectsRenderWireTests.WriteU32(objectEffect, 1, 1);
        Net7EffectsRenderWireTests.WriteU16(objectEffect, 5, 10);
        Net7EffectsRenderWireTests.WriteU32(objectEffect, 7, 101);
        Net7EffectsRenderWireTests.WriteU32(objectEffect, 11, 1000);
        session.Gameplay.Apply(Packet.ForOpcode(0x0009, objectEffect));

        byte[] point = new byte[40];
        Net7EffectsRenderWireTests.WriteU32(point, 0, 102);
        Net7EffectsRenderWireTests.WriteU32(point, 4, 1000);
        Net7EffectsRenderWireTests.WriteU16(point, 22, 11);
        Net7EffectsRenderWireTests.WriteF32(point, 24, 1);
        session.Gameplay.Apply(Packet.ForOpcode(0x000A, point));

        byte[] objectToObject = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U16(0x0003); w.U32(1); w.U32(2); w.U16(12); w.CString(""); w.U32(103); w.U32(1000);
        });
        session.Gameplay.Apply(Packet.ForOpcode(0x000B, objectToObject));

        byte[] objectToEffect = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(104); w.U32(1000); w.U32(1); w.CString(""); w.U32(2); w.I16(0); w.U16(13);
            w.Vector3(Vector3.Zero); w.I16(0); w.I16(0); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        session.Gameplay.Apply(Packet.ForOpcode(0x000C, objectToEffect));

        byte[] objectToPoint = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(105); w.U32(1000); w.U32(1); w.CString(""); w.Vector3(Vector3.Zero); w.I16(0); w.U16(14);
            w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        session.Gameplay.Apply(Packet.ForOpcode(0x000D, objectToPoint));

        byte[] durationLinked = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(106); w.U32(1000); w.U32(1); w.CString(""); w.U32(2); w.I16(0); w.U16(15);
            w.Vector3(Vector3.Zero); w.I16(0); w.I16(0); w.U8(0); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        session.Gameplay.Apply(Packet.ForOpcode(0x000E, durationLinked));

        foreach (uint effectId in new uint[] { 101, 102, 103, 104, 105, 106 })
            Assert.Equal(1002u, session.Gameplay.Effects[effectId].Timestamp);
    }

    [Fact]
    public void PointAndFixedEffectFamiliesKeepEffectIdZeroAsARealRegistryKey()
    {
        var state = new Net7GameplayState();
        byte[] removeZero = new byte[4];

        byte[] point = new byte[40];
        Net7EffectsRenderWireTests.WriteU16(point, 22, 10);
        Net7EffectsRenderWireTests.WriteF32(point, 24, 1);
        Assert.True(state.Apply(Packet.ForOpcode(0x000A, point)));
        Assert.Contains(0u, state.Effects.Keys);
        state.Apply(Packet.ForOpcode(0x000F, removeZero));

        byte[] objectToEffect = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(0); w.U32(0); w.U32(1); w.CString(""); w.U32(2); w.I16(0); w.U16(11);
            w.Vector3(Vector3.Zero); w.I16(0); w.I16(0); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000C, objectToEffect)));
        Assert.Contains(0u, state.Effects.Keys);
        state.Apply(Packet.ForOpcode(0x000F, removeZero));

        byte[] objectToPoint = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(0); w.U32(0); w.U32(1); w.CString(""); w.Vector3(Vector3.Zero); w.I16(0); w.U16(12);
            w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000D, objectToPoint)));
        Assert.Contains(0u, state.Effects.Keys);
        state.Apply(Packet.ForOpcode(0x000F, removeZero));

        byte[] linked = Net7EffectsRenderWireTests.Build(w =>
        {
            w.U32(0); w.U32(0); w.U32(1); w.CString(""); w.U32(2); w.I16(0); w.U16(13);
            w.Vector3(Vector3.Zero); w.I16(0); w.I16(0); w.U8(0); w.F32(1); w.Vector3(Vector3.Zero); w.F32(1);
        });
        Assert.True(state.Apply(Packet.ForOpcode(0x000E, linked)));
        Assert.Contains(0u, state.Effects.Keys);
    }

    [Fact]
    public void ClickIndicatorAndNotifyEmoteRequireAResolvedWorldObject()
    {
        var world = new Net7WorldState();
        var state = new Net7GameplayState(resolveWorldObject: world.Objects.FindFirst);
        const uint gameId = 0x11223344;

        byte[] click = new byte[5];
        click[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(click.AsSpan(1, 4), gameId);
        byte[] emote = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(emote.AsSpan(0, 4), gameId);
        BinaryPrimitives.WriteUInt32LittleEndian(emote.AsSpan(4, 4), 7);

        state.Apply(Packet.ForOpcode(0x0069, click));
        state.Apply(Packet.ForOpcode(0x00A2, emote));
        Assert.Empty(state.ClickIndicators);
        Assert.Empty(state.Emotes);

        world.MaterializeCreate(new Net7CreateUpdate(gameId, 1, 0, 0, 0, 0, 0));
        state.Apply(Packet.ForOpcode(0x0069, click));
        state.Apply(Packet.ForOpcode(0x00A2, emote));
        Assert.True(state.ClickIndicators[gameId].IsOn);
        Assert.Equal(7u, state.Emotes[gameId]);
    }

    [Fact]
    public void AvatarDescriptionCachesBeforeCreateAndDoesNotReplaceAnExistingGameId()
    {
        var world = new Net7WorldState();
        const uint gameId = 0x10203040;

        byte[] first = BuildAvatarDescription(gameId, "FIRST");
        byte[] second = BuildAvatarDescription(gameId, "SECOND");
        Assert.True(world.Apply(Packet.ForOpcode(0x0061, first), 0).PacketMaterialized);
        Assert.True(world.Apply(Packet.ForOpcode(0x0061, second), 0).PacketMaterialized);
        Assert.Equal("FIRST", world.AvatarDescriptions[gameId].Skeleton);

        Net7WorldObject created = world.MaterializeCreate(new Net7CreateUpdate(gameId, 1, 0, 0, 0, 0, 0));
        Assert.Equal("FIRST", created.AvatarDescription!.Skeleton);

        byte[] handoff = BuildServerHandoffPayload(Net7SectorJoinClientData.CreateSuccessful(0x11223344, 2000, 2));
        Assert.NotNull(world.Apply(Packet.ForOpcode(0x003A, handoff), 0).ServerHandoff);
        Assert.Equal("FIRST", world.AvatarDescriptions[gameId].Skeleton);
        Net7WorldObject recreated = world.MaterializeCreate(new Net7CreateUpdate(gameId, 1, 0, 0, 0, 0, 0));
        Assert.Equal("FIRST", recreated.AvatarDescription!.Skeleton);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpaceRemoveRetiresAvatarCacheEvenWithoutWorldObject(bool materialized)
    {
        var world = new Net7WorldState();
        const uint id = 42;
        world.Apply(Packet.ForOpcode(0x0061, BuildAvatarDescription(id, "FIRST")), 0);
        ulong firstLifetime=world.AvatarCacheLifetimes[id];
        if (materialized) world.MaterializeCreate(new Net7CreateUpdate(id, 1, 0, 0, 0, 0, 0));
        byte[] remove = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, id);
        world.Apply(Packet.ForOpcode(0x0007, remove), 0);
        Assert.False(world.AvatarDescriptions.ContainsKey(id));
        Assert.Null(world.MaterializeCreate(new Net7CreateUpdate(id, 1, 0, 0, 0, 0, 0)).AvatarDescription);
        world.Apply(Packet.ForOpcode(0x0061, BuildAvatarDescription(id, "SECOND")), 0);
        Assert.Equal("SECOND", world.Objects.FindFirst(id)!.AvatarDescription!.Skeleton);
        Assert.True(world.AvatarCacheLifetimes[id]>firstLifetime);
    }

    [Fact]
    public void RemoveUsesActiveAvatarViewRatherThanRetainedStationIdentity()
    {
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const uint id = 42;
        session.World.Apply(Packet.ForOpcode(0x0061, BuildAvatarDescription(id, "FIRST")), 0);
        session.PlayerSemantics.ObserveStarbaseSet(new(7, 0, 0));
        Assert.True(session.PlayerSemantics.IsAvatarViewActive);
        byte[] remove = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(remove, id);
        session.World.Apply(Packet.ForOpcode(0x0007, remove), 0);
        Assert.Equal("FIRST", session.World.AvatarDescriptions[id].Skeleton);
        session.World.Apply(Packet.ForOpcode(0x0061, BuildAvatarDescription(id, "IGNORED")), 0);
        Assert.Equal("FIRST", session.World.AvatarDescriptions[id].Skeleton);
        session.PlayerSemantics.ObserveClientType(0);
        Assert.False(session.PlayerSemantics.IsAvatarViewActive);
        Assert.Equal(7u, session.PlayerSemantics.ActiveStarbaseId);
        session.World.Apply(Packet.ForOpcode(0x0007, remove), 0);
        Assert.False(session.World.AvatarDescriptions.ContainsKey(id));
    }

    [Fact]
    public void SpeechAdmissionUsesAvatarCacheBeforeWorldObjectAndCapturesItsLifetime()
    {
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        const uint id=42;
        session.World.Apply(Packet.ForOpcode(0x0061,BuildAvatarDescription(id,"FIRST")),0);
        session.Gameplay.Social.ApplyMessageChatstream(new(0,4,id,[new(0,2,[],null),new(2,17,[],null)]));
        var first=Assert.Single(session.Gameplay.Social.AvatarSpeech);
        Assert.False(first.WorldObjectPresent);Assert.Equal(0ul,first.CreationOrdinal);
        Assert.Equal(session.World.AvatarCacheLifetimes[id],first.CacheLifetime);
        session.World.MaterializeCreate(new Net7CreateUpdate(id,1,0,0,0,0,0));
        session.Gameplay.Social.ApplyMessageChatstream(new(0,4,id,[new(0,1,[],null)]));
        var second=session.Gameplay.Social.AvatarSpeech.Last();
        Assert.True(second.WorldObjectPresent);Assert.Equal(first.CacheLifetime,second.CacheLifetime);
        Assert.Equal(session.World.Objects.FindFirst(id)!.CreationOrdinal,second.CreationOrdinal);
        byte[] remove=new byte[4];BinaryPrimitives.WriteUInt32LittleEndian(remove,id);
        session.World.Apply(Packet.ForOpcode(0x0007,remove),0);
        session.Gameplay.Social.ApplyMessageChatstream(new(0,4,id,[new(0,3,[],null)]));
        Assert.Equal(2,session.Gameplay.Social.AvatarSpeech.Count);
        session.World.Apply(Packet.ForOpcode(0x0061,BuildAvatarDescription(id,"SECOND")),0);
        session.Gameplay.Social.ApplyMessageChatstream(new(0,4,id,[new(0,3,[],null)]));
        Assert.NotEqual(first.CacheLifetime,session.Gameplay.Social.AvatarSpeech.Last().CacheLifetime);
    }

    private static byte[] BuildServerHandoffPayload(Net7SectorJoinClientData join)
    {
        using var stream = new MemoryStream();
        stream.Write(join.ToWireBytes());
        byte[] length = new byte[2];
        foreach (string value in new[] { "SourceSector", "SourceSystem", "DestSector", "DestSystem" })
        {
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(value);
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
            stream.Write(length);
            stream.Write(bytes);
        }
        return stream.ToArray();
    }

    private static byte[] BuildAvatarDescription(uint gameId, string skeleton)
    {
        byte[] avatar = Net7PresentationWireTests.BuildAvatarData(skeleton);
        byte[] payload = new byte[4 + avatar.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0, 4), gameId);
        avatar.CopyTo(payload, 4);
        return payload;
    }
}
