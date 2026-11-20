using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using EnbNext.Client.Models;

namespace EnbNext.App;

internal sealed class GroupLaunchSession(SavedLoginGroup group) : IDisposable
{
    internal sealed class Member(SavedGroupMember member)
    {
        public SavedGroupMember Definition { get; }=member;
        public string State { get; internal set; }="Waiting";
        public string Error { get; internal set; }="";
        internal Process? Process;
        internal int Attempt;
        internal CancellationTokenSource? ReaderStop;
        internal Task Reader=Task.CompletedTask;
    }
    public SavedLoginGroup Group { get; }=group;
    public Member[] Members { get; }=group.Members.Select(m=>new Member(m)).ToArray();
    public bool Paused { get; private set; }
    public bool Stopped { get; private set; }
    public int Revision { get; private set; }
    private readonly CancellationTokenSource stop=new();
    private readonly ConcurrentQueue<(int Index,int Attempt,string State,string Error)> notifications=new();
    private readonly List<Task> readers=[];
    internal Task ReadersCompleted=>Task.WhenAll(readers);
    // Owner revision 20 Nov: members no longer wait for the previous one to enter the game.
    public int Next => Paused||Stopped?-1:Array.FindIndex(Members,m=>m.State=="Waiting");
    public bool Begin(int index)
    {
        var member=Members[index];if(member.State is "Starting" or "Online" || member.Process is {} process && !process.HasExited)return false;
        member.ReaderStop?.Cancel();member.ReaderStop?.Dispose();member.ReaderStop=null;member.Process?.Dispose();member.Process=null;member.Attempt++;member.State="Starting";member.Error="";Revision++;return true;
    }
    public void Report(int index,string state,string error="")
    {
        var m=Members[index];if(m.State=="Failed"&&state=="Offline")return;if(m.State==state&&m.Error==error)return;
        m.State=state;m.Error=error;if(state is "Failed" or "Offline")Paused=true;Revision++;
    }
    public void Continue() { Paused=false;Stopped=false;Revision++; }
    public void Stop() { Stopped=true;Revision++; }
    public void Skip()
    {
        int failed=Array.FindIndex(Members,m=>m.State=="Failed");
        if(failed>=0)Report(failed,"Offline",Members[failed].Error);
        Continue();
    }
    public string Listen(int index)
    {
        string name="EnbNext-launch-"+Guid.NewGuid().ToString("N");int attempt=Members[index].Attempt;
        var pipe=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);Members[index].ReaderStop=cancel;var token=cancel.Token;
        readers.RemoveAll(task=>task.IsCompleted);var task=ReadAsync();Members[index].Reader=task;readers.Add(task);return name;
        async Task ReadAsync()
        {
            try
            {
                using(pipe)
                {
                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var reader=new StreamReader(pipe);
                    while(await reader.ReadLineAsync(token).ConfigureAwait(false) is {} line)
                    {
                        string[] fields=line.Split('|',3);
                        if(fields.Length>=2 && fields[0]==Members[index].Definition.Id.ToString("N") && fields[1] is "Online" or "Offline" or "Failed")
                            notifications.Enqueue((index,attempt,fields[1],fields.Length>2?fields[2]:""));
                    }
                    notifications.Enqueue((index,attempt,"Offline","Launch connection closed."));
                }
            }
            catch(OperationCanceledException) { }
            catch(IOException) { notifications.Enqueue((index,attempt,"Offline","Launch connection lost.")); }

        }
    }
    public void Attach(int index,Process process) { Members[index].Process=process; }
    public void Tick()
    {
        while(notifications.TryDequeue(out var n))if(Members[n.Index].Attempt==n.Attempt)Report(n.Index,n.State,n.Error);
        for(int i=1;i<Members.Length;i++)
            if(Members[i].Process is {} process && process.HasExited && Members[i].State is "Starting" or "Online")
                {Report(i,"Offline","Client exited. Launch it again when ready.");Members[i].ReaderStop?.Cancel();}
    }
    public void Dispose()
    {
        stop.Cancel();foreach(var member in Members)member.Process?.Dispose();
        // Releasing Process handles leaves surviving client processes running.
        _=ReadersCompleted.ContinueWith(_=>{foreach(var member in Members)member.ReaderStop?.Dispose();stop.Dispose();},TaskScheduler.Default);
    }
}

internal sealed class GroupLaunchReporter(string pipeName,Guid member) : IAsyncDisposable
{
    private readonly NamedPipeClientStream pipe=new(".",pipeName,PipeDirection.Out,PipeOptions.Asynchronous);
    private StreamWriter? writer;
    public async Task ConnectAsync() { await pipe.ConnectAsync(10000);writer=new(pipe){AutoFlush=true}; }
    public async Task SendAsync(string state,string error="")
    {
        if(writer is null)return;
        try { await writer.WriteLineAsync(member.ToString("N")+"|"+state+"|"+error.Replace('\n',' ').Replace('\r',' ')); }
        catch(IOException) { writer=null; }
    }
    public async ValueTask DisposeAsync() { if(writer is not null)await writer.DisposeAsync();await pipe.DisposeAsync(); }
}
