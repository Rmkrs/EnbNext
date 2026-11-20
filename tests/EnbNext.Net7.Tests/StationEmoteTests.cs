using EnbNext.Net7.Gameplay;
using EnbNext.Net7.Social;
using EnbNext.Presentation;

namespace EnbNext.Net7.Tests;

public sealed class StationEmoteTests
{
    [Fact]
    public async Task AssignedAvatarIdentityDrivesStationPacketsWithoutChangingStartIdentity()
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const uint start = 0x420A, avatar = 0x4000420A;
        session.World.SetLocalPlayerId(start);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, start);
        session.World.Apply(EnbNext.Protocol.Net.Packet.ForOpcode(0x0037, Convert.FromHexString("0A420040")), 0);
        session.PlayerSemantics.ObserveStarbaseSet(new(10651, 0, 0));
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var packets = new List<(ushort Opcode, byte[] Payload)>();
        channel.FrameSent += (frame, _) => packets.Add((frame.Opcode, frame.Payload.ToArray()));
        Assert.True(await session.SubmitChatAsync(new("/celebrate") { AvatarRecords = [new(2, 18)] }));
        Assert.True(await session.SendStarbaseAvatarChangeAsync(0, 0, System.Numerics.Vector3.Zero, 1));
        Assert.True(await session.SendStarbaseRoomChangeAsync(0, uint.MaxValue));
        Assert.Equal(new ushort[] { 0x005E, 0x009D, 0x009F }, packets.Select(p => p.Opcode));
        Assert.All(packets, p => Assert.Equal(avatar, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(p.Payload)));
        Assert.Equal(Convert.FromHexString("0A42004001080002120000080000000000"), packets[0].Payload);
        Assert.Equal(start, session.World.LocalPlayerId);
        Assert.Equal(start, session.PlayerId);
    }

    [Fact]
    public void AnimationSoundFramesPreserveOverlapsAndIgnoreCommentedCues()
    {
        var ini = EnbNext.Assets.EnbRetailIniDocument.Parse("[SKEL.CLIP]\nSound0=12, voice\nSound1=3, cloth,2D\nSound2=12, clap\n;Sound3=1, disabled\n");
        var cues = EnbNext.Assets.EnbRetailAnimationSounds.Read(ini, "skel.clip", 30);
        Assert.Equal(new[] { .1, .4, .4 }, cues.Select(c => c.Seconds));
        Assert.Equal(new[] { "cloth", "voice", "clap" }, cues.Select(c => c.Preset));
        Assert.True(cues[0].Flat);
        Assert.False(cues[1].Flat);
        Assert.Empty(EnbNext.Assets.EnbRetailAnimationSounds.Read(ini, "missing", 30));
    }
    [Fact]
    public void GestureHasNoChatTextAndReceivedSelfGestureSurvivesPrivateSuppression()
    {
        var result=Net7ChatInput.Plan(new("/bow"){AvatarRecords=[new(2,17)]},new(42,Net7SelectedChatChannel.Local,false,false),99,null,true);
        var packet=Assert.Single(result.Packets);
        Assert.Equal((ushort)0x5e,packet.Opcode);
        Assert.Equal(new byte[]{42,0,0,0,1,8,0,2,17,0,0,8,0,0,0,0,0},packet.Payload);
        var state=new Net7SocialState(localPlayerId:()=>42,resolveAvatarCache:_=>1);
        state.ApplyMessageChatstream(new(8,3,42,[new(2,17,[],null),new(8,0,[],null)]));
        Assert.Empty(state.Messages);
        var first=EnbSceneChatSnapshot.From(state);
        Assert.Equal((byte)17,Assert.Single(Assert.Single(first.AvatarEmotes).Animations));
        Assert.Same(first,EnbSceneChatSnapshot.From(state));
        state.ApplyMessageChatstream(new(8,1,42,[new(2,17,[],null),new(8,0,[],null)]));
        Assert.Equal(2,EnbSceneChatSnapshot.From(state).AvatarEmotes.Length);
        Assert.Single(first.AvatarEmotes);
        state.ResetForHandoff();Assert.Empty(EnbSceneChatSnapshot.From(state).AvatarEmotes);
    }

    [Fact]
    public void StationStreamPreservesTextMoodAndEmptyReplacementWithoutSpaceObject()
    {
        var state=new Net7SocialState(resolveAvatarCache:_=>1);
        state.ApplyMessageChatstream(new(0,1,42,[new(3,2,[],null),new(1,17,[],"hello"),new(8,0,[],null)]));
        Assert.False(Assert.Single(state.AvatarSpeech).WorldObjectPresent);
        var first=Assert.Single(state.AvatarEmotes);
        Assert.Empty(first.Animations);
        Assert.Equal(new byte[]{3,1,8},first.Commands.Select(c=>c.Type));
        Assert.Equal("hello",first.Commands[1].Text);
        state.ApplyMessageChatstream(new(0,1,42,[new(8,0,[],null)]));
        Assert.Equal(2,state.AvatarEmotes.Count);
        Assert.Equal((byte)8,Assert.Single(state.AvatarEmotes[1].Commands).Type);
    }
}
