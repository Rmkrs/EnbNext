using EnbNext.Net7.Social;

namespace EnbNext.Net7.Tests;

public sealed class Net7SecureSelectionTests
{
    [Theory]
    [InlineData(Net7ClientChatRequestKind.Friend,8u)]
    [InlineData(Net7ClientChatRequestKind.Ignore,10u)]
    public async Task TargetSocialRequestPreservesMaterializedEmptyRecipient(Net7ClientChatRequestKind kind,uint code)
    {
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        session.World.SetLocalPlayerId(42);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,42u);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        var packets=new List<(ushort Opcode,byte[] Payload)>();
        channel.FrameSent+=(frame,_)=>packets.Add((frame.Opcode,frame.Payload.ToArray()));
        Assert.True(await session.SendChatControlRequestAsync(kind,player:""));
        var packet=Assert.Single(packets);
        Assert.Equal((ushort)0xa3,packet.Opcode);
        // 878DC0: start-session, code, three u16-length strings, u32 zero blob.
        Assert.Equal(18,packet.Payload.Length);
        Assert.Equal(code,System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(packet.Payload.AsSpan(4)));
        Assert.All(packet.Payload.Skip(8),value=>Assert.Equal((byte)0,value));
        Assert.False(await session.SendChatControlRequestAsync(kind));
        Assert.Single(packets);
    }

    [Fact]
    public async Task TellWithoutAvatarStillSelectsRecipientWithoutSendingOrEchoing()
    {
        await using var session=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        session.World.SetLocalPlayerId(42);
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session,42u);
        // Deliberately no transport: this native branch queues no packet.
        Assert.True(await session.SubmitChatAsync(new("/tell Peer hello"){AvatarProducer=_=>null}));
        Assert.Equal("Peer",Assert.Single(session.Gameplay.Social.SecureTargets));
        Assert.Empty(session.Gameplay.Social.Messages);
        var value=Assert.Single(session.Gameplay.Social.PresentationEvents,e=>e.Kind==Net7ChatPresentationKind.SelectSecureRecipient);
        Assert.Equal("Peer",value.Text);
        // 00749590: a tell without a message is a handled usage notice (type 6), not a failed send (CHAT-D35).
        Assert.True(await session.SubmitChatAsync(new("/tell Missing"){AvatarProducer=_=>null}));
        Assert.Contains(session.Gameplay.Social.Messages,m=>m.Type==6&&m.Text.Contains("Usage: /tell",StringComparison.Ordinal));
        Assert.Equal("Peer",Assert.Single(session.Gameplay.Social.SecureTargets));
    }

    [Fact]
    public void ExistingSecureSlotIsSelectedWithoutReorderingAndNewEntryEvictsLast()
    {
        var state=new Net7SocialState();
        foreach(string name in new[]{"One","Two","Three","One"})state.SelectSecureRecipient(name);
        Assert.Equal(new[]{"Three","Two","One"},state.SecureTargets);
        Assert.Equal("One",state.PresentationEvents.Last().Text);
        state.SelectSecureRecipient("one");
        Assert.Equal(new[]{"one","Three","Two"},state.SecureTargets);
    }

    [Fact]
    public void WhitespaceNameIsStoredLikeAnyNameAndEmptyNameSelectsAnEmptySlot()
    {
        // 005658B0 rejects no name; an empty name matches an empty slot ("").
        var state=new Net7SocialState();
        state.PromoteSecureTarget("");
        Assert.Empty(state.SecureTargets);
        state.PromoteSecureTarget(" ");
        Assert.Equal(new[]{" "},state.SecureTargets);
    }
}
