namespace EnbNext.Assets;

/// <summary>Speculative immutable asset decoding only. No renderer publication,
/// callback, mapper construction or clock advancement happens on this queue.</summary>
public sealed class EnbBaseAssetPredecode(Func<int,EnbBaseAssetResolution> decode) : IDisposable
{
    private readonly object gate=new();
    private readonly Dictionary<int,Task<EnbBaseAssetResolution?>> pending=[];
    private Task tail=Task.CompletedTask;
    private bool stopped;
    public void Request(int selector)
    {
        lock(gate)
        {
            if(stopped || pending.ContainsKey(selector))return;
            var preceding=tail;
            var task=Task.Run(async ()=>
            {
                await preceding.ConfigureAwait(false);
                lock(gate)if(stopped)return null;
                // Speculation cannot make a recoverable asset failure fatal.
                // The ordered demand path retries and reports its own result.
                try{return decode(selector);}catch(Exception){return null;}
            });
            pending.Add(selector,task);tail=task;
        }
    }
    public bool TryGetCompleted(int selector,out EnbBaseAssetResolution result)
    {
        lock(gate)
        {
            if(!stopped && pending.TryGetValue(selector,out var task) && task.IsCompletedSuccessfully && task.Result is {} ready)
            {result=ready;return true;}
        }
        result=null!;return false;
    }
    public Task Completion {get {lock(gate)return tail;}}
    public void Dispose(){lock(gate){stopped=true;pending.Clear();}}
}
