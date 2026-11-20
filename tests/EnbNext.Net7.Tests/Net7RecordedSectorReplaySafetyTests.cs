using System.Net;
using System.Text.Json;
using EnbNext.Net7.Diagnostics;
using EnbNext.Net7.Protocol;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class Net7RecordedSectorReplaySafetyTests
{
    [Fact]
    public async Task ReplayClockStartsAtRecordedOriginOnlyOnce()
    {
        var clock=new Net7WireReplayClock(12345);
        await Task.Delay(20);
        Assert.Equal(12345u,clock.GetMilliseconds());
        clock.Start();
        await Task.Delay(20);
        Assert.True(clock.GetMilliseconds()>=12365);
        Assert.Throws<InvalidOperationException>(clock.Start);
    }
    [Fact]
    public async Task RemotePeerAndNonLoopbackSessionAreRejectedBeforeEntry()
    {
        var bootstrap=CreateBootstrap();
        await using var local=new Net7Session(new Net7ServerOptions {Host="127.0.0.1"});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>local.BeginRecordedSectorAsync(bootstrap,new(IPAddress.Parse("192.0.2.1"),3806),new(IPAddress.Loopback,3501)));
        Assert.Equal(Net7LoginState.Created,local.LoginState);
        await using var remote=new Net7Session(new Net7ServerOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(()=>remote.BeginRecordedSectorAsync(bootstrap,new(IPAddress.Loopback,3806),new(IPAddress.Loopback,3501)));
        Assert.Equal(Net7LoginState.Created,remote.LoginState);
    }
    [Fact]
    public void BootstrapRoundTripRetainsExactJoinWireAndCharacter()
    {
        var bootstrap=CreateBootstrap();
        var wire=bootstrap.JoinData.ToWireBytes();wire[1]=0x71;
        bootstrap=bootstrap with {JoinWire=wire};
        var decoded=JsonSerializer.Deserialize<Net7RecordedSectorBootstrap>(JsonSerializer.Serialize(bootstrap))!;
        Assert.Equal(wire,decoded.JoinWire);
        Assert.Equal(bootstrap.Selection.Character.Meta,decoded.Selection.Character.Meta);
        Assert.Equal(bootstrap.Selection.Character.Avatar.FirstName,decoded.Selection.Character.Avatar.FirstName);
        Assert.Equal(bootstrap.ClientTime,decoded.ClientTime);
    }
    private static Net7RecordedSectorBootstrap CreateBootstrap()
    {
        var roster=Net7AvatarRosterCodec.Decode(TestProtocolFixtures.BuildRoster());
        var character=roster.Slots.First(s=>!s.IsEmpty);
        var join=Net7SectorJoinClientData.CreateSuccessful(123,1079,0);
        var selection=new Net7CharacterSelectionResult(0,character,new("fixture",1,0,0,0,1,1),123,0x2009,join);
        return new(selection,join,0,1,12345);
    }
}
