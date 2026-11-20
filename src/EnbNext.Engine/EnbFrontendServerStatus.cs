using EnbNext.Net7;

namespace EnbNext.Engine;

internal sealed class EnbFrontendServerStatus(Func<string,CancellationToken,Task<Net7ServerStatus>>? query = null) : IDisposable
{
    private readonly Func<string,CancellationToken,Task<Net7ServerStatus>> query=query??Net7ServerStatusClient.CheckAsync;
    private CancellationTokenSource? pending;
    private bool disposed;
    public bool Enabled { get; private set; }
    public bool Checking => pending is not null;
    public Net7ServerStatus Result { get; private set; }
    public Action<string>? Diagnostic { get; set; }
    public string Text {get;private set;}="OFFLINE";
    public Task RefreshAsync(string host)
    {
        if(disposed)return Task.CompletedTask;
        Enabled=true;pending?.Cancel();Result=default;Text="CHECKING...";
        var operation=new CancellationTokenSource();pending=operation;
        return CompleteAsync(host,operation);
    }
    private async Task CompleteAsync(string host,CancellationTokenSource operation)
    {
        try
        {
            var result=await query(host,operation.Token);
            if(ReferenceEquals(pending,operation))
            {
                Result=result;
                Text=result.Starting?"STARTING":!result.Online?"OFFLINE":result.Players is {} count?$"ONLINE - {count} PLAYERS":"ONLINE";
            }
        }
        catch(OperationCanceledException) when(operation.IsCancellationRequested) { }
        catch(Exception ex)
        {
            if(ReferenceEquals(pending,operation))Diagnostic?.Invoke("Server status check failed: "+ex);
        }
        finally
        {
            if(ReferenceEquals(pending,operation)){pending=null;if(Text=="CHECKING...")Text="OFFLINE";}
            operation.Dispose();
        }
    }
    public void Dispose(){disposed=true;var old=pending;pending=null;old?.Cancel();}
}
