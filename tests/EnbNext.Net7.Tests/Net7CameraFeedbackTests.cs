using System.Reflection;

namespace EnbNext.Net7.Tests;

public sealed class Net7CameraFeedbackTests
{
    [Fact]
    public async Task CameraFeedbackIsLocalType23AndRejectsStaleCharacterAndLifecycle()
    {
        await using var client=new Client.EnbClient(new());
        var session=new Net7Session(new Net7ServerOptions{Host="127.0.0.1"});
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;
        typeof(Client.EnbClient).GetField("_net7",flags)!.SetValue(client,session);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client,Client.ClientLifecycleState.InWorld);
        var (_,channel,receiver)=await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime=receiver;
        int packets=0;channel.FrameSent+=(_,_)=>packets++;
        client.ReportCameraMessage(8,"stale character");
        Assert.Empty(session.Gameplay.Social.Messages);
        client.ReportCameraMessage(7,"Camera set to 1st person mode.");
        var message=Assert.Single(session.Gameplay.Social.Messages);
        Assert.Equal(23,message.Type);
        Assert.Equal("Camera set to 1st person mode.",message.Text);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client,Client.ClientLifecycleState.Disconnected);
        client.ReportCameraMessage(7,"disconnected");
        Assert.Single(session.Gameplay.Social.Messages);
        Assert.Equal(0,packets);
    }

    [Fact]
    public async Task ChatMenuFeedbackUsesRetailComputerAndSystemTypesWithoutSendingPackets()
    {
        await using var client = new Client.EnbClient(new());
        var session = new Net7Session(new Net7ServerOptions { Host = "127.0.0.1" });
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(Client.EnbClient).GetField("_net7", flags)!.SetValue(client, session);
        typeof(Client.EnbClient).GetProperty("State")!.SetValue(client, Client.ClientLifecycleState.InWorld);
        var (_, channel, receiver) = await Net7R11FlightWireTests.AttachLoopback(session);
        using var receiverLifetime = receiver;
        int packets = 0; channel.FrameSent += (_, _) => packets++;

        client.ReportLocalChatMessage(7, 17, "Unable to reply - no one to reply to.");
        client.ReportLocalChatMessage(7, 19, "Setting chat channel to New Players.");
        client.ReportLocalChatMessage(7, 17, "Friend not online.");
        client.ReportLocalChatMessage(8, 19, "stale");

        Assert.Collection(session.Gameplay.Social.Messages,
            message => { Assert.Equal(17, message.Type); Assert.Equal("COMPUTER: Unable to reply - no one to reply to.", message.Text); },
            message => { Assert.Equal(19, message.Type); Assert.Equal("SYSTEM: Setting chat channel to New Players.", message.Text); },
            message => { Assert.Equal(17, message.Type); Assert.Equal("COMPUTER: Friend not online.", message.Text); });
        Assert.Equal(0, packets);
    }
}
