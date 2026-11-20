using EnbNext.Assets;
using EnbNext.Net7.Gameplay;

namespace EnbNext.Net7.Tests;

public sealed class ChatStartupTests
{
    [Fact]
    public async Task OocSubscriptionUsesFullChannelNameRatherThanSlashAlias()
    {
        var options = EnbRetailPlayerOptionsCatalog.ParseText("""
            [BOOL-000]
            OptionName=Channel_Newbie
            OptionValue=no
            [BOOL-001]
            OptionName=Channel_OOC
            OptionValue=yes
            """);
        Assert.Equal(new[] { "Out of Context" }, options.MonitoredChatChannels);
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 7u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var sent = new List<byte[]>();
        channel.FrameSent += (frame, _) => sent.Add(frame.Payload.ToArray());
        Assert.True(await session.InitializeChatAsync(options.MonitoredChatChannels, false));
        Assert.Equal(2, sent.Count);
        Assert.Equal(Convert.FromHexString("070000000600000000000E004F7574206F6620436F6E7465787401002000000000"), sent[0]);
    }

    [Fact]
    public void SavedNamedSubscriptionsOverrideDefaultsWithoutJoiningLocalOrPrivateFilters()
    {
        Assert.Equal(new[] { "New Players" }, EnbRetailPlayerOptionsCatalog.ParseText("").MonitoredChatChannels);
        var options = EnbRetailPlayerOptionsCatalog.ParseText("""
            [BOOL-000]
            OptionName=Channel_Newbie
            OptionValue=no
            [BOOL-001]
            OptionName=Channel_Market
            OptionValue=yes
            [BOOL-002]
            OptionName=Channel_Traders
            OptionValue=yes
            [BOOL-003]
            OptionName=Channel_Private
            OptionValue=yes
            [BOOL-004]
            OptionName=Channel_Local
            OptionValue=yes
            """);
        Assert.Equal(new[] { "Market", "Tradesmen" }, options.MonitoredChatChannels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupSendsDefaultNewPlayersJoinBeforePrivacy(bool restricted)
    {
        await using var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        typeof(Net7Session).GetProperty("PlayerId")!.SetValue(session, 7u);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        var sent = new List<byte[]>();
        channel.FrameSent += (frame, _) => { Assert.Equal((ushort)0xA3, frame.Opcode); sent.Add(frame.Payload.ToArray()); };
        Assert.True(await session.InitializeChatAsync(EnbRetailPlayerOptionsCatalog.ParseText("").MonitoredChatChannels, restricted));
        Assert.Equal(2, sent.Count);
        // Same payload as passive retail entry, with the offline Start ID substituted.
        Assert.Equal(Convert.FromHexString("070000000600000000000B004E657720506C617965727301002000000000"), sent[0]);
        Assert.Equal(Net7GameplayWire.BuildClientChatRequest(7, restricted ? 28u : 29u, "", "", "", []), sent[1]);
    }
}
