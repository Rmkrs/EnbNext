using System.Net;
using System.Net.Sockets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class ServerStatusTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(-1)]
    public async Task LauncherPacketsAndSignedCountAreExact(short count)
    {
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var check=Net7ServerStatusClient.CheckEndpointAsync((IPEndPoint)server.Client.LocalEndPoint!,CancellationToken.None);
        var status=await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("10000010000000000000000000000040",Convert.ToHexString(status.Buffer));
        await server.SendAsync(new byte[]{4,0,1,16},status.RemoteEndPoint);
        var players=await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("1000005000000000000000002C010000",Convert.ToHexString(players.Buffer));
        byte[] reply=new byte[14];System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(reply.AsSpan(12),count);
        await server.SendAsync(reply,players.RemoteEndPoint);
        var result=await check;Assert.True(result.Online);Assert.False(result.Starting);
        Assert.Equal(count==-1?(short?)null:count,result.Players);
    }
    [Theory]
    [InlineData("04000B10",true)]
    [InlineData("04000210",false)]
    [InlineData("040001",false)]
    public async Task NonOnlineReplyDoesNotRequestCount(string hex,bool starting)
    {
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        var check=Net7ServerStatusClient.CheckEndpointAsync((IPEndPoint)server.Client.LocalEndPoint!,CancellationToken.None);
        var request=await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await server.SendAsync(Convert.FromHexString(hex),request.RemoteEndPoint);
        var result=await check;Assert.False(result.Online);Assert.Equal(starting,result.Starting);Assert.Null(result.Players);
        Assert.Equal(0,server.Available);
    }
    [Fact]
    public async Task CancellationClosesPendingQuery()
    {
        using var server=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));using var stop=new CancellationTokenSource();
        var check=Net7ServerStatusClient.CheckEndpointAsync((IPEndPoint)server.Client.LocalEndPoint!,stop.Token);
        await server.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(2));stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>check);
    }
    [Fact]
    public async Task LatestSelectionWinsEvenWhenOldQueryIgnoresCancellation()
    {
        var first=new TaskCompletionSource<Net7ServerStatus>();var second=new TaskCompletionSource<Net7ServerStatus>();
        CancellationToken oldToken=default;int calls=0;
        using var owner=new EnbFrontendServerStatus((host,token)=>{calls++;if(host=="old"){oldToken=token;return first.Task;}return second.Task;});
        var old=owner.RefreshAsync("old");Assert.Equal("CHECKING...",owner.Text);
        var current=owner.RefreshAsync("new");Assert.True(oldToken.IsCancellationRequested);
        second.SetResult(new(true,0));await current;Assert.Equal("ONLINE - 0 PLAYERS",owner.Text);
        first.SetResult(new(true,99));await old;Assert.Equal("ONLINE - 0 PLAYERS",owner.Text);
        await owner.RefreshAsync("new");Assert.Equal(3,calls);
    }
    [Fact]
    public async Task DisposalRejectsLateCompletionAndUnexpectedErrorsRemainDiagnostic()
    {
        var completion=new TaskCompletionSource<Net7ServerStatus>();
        var owner=new EnbFrontendServerStatus((_,_)=>completion.Task);var check=owner.RefreshAsync("host");owner.Dispose();
        completion.SetResult(new(true,1));await check;Assert.False(owner.Result.Online);Assert.False(owner.Checking);
        using var broken=new EnbFrontendServerStatus((_,_)=>throw new InvalidOperationException("fixture"));
        var diagnostics=new List<string>();broken.Diagnostic=diagnostics.Add;await broken.RefreshAsync("host");Assert.Single(diagnostics);
    }
}
