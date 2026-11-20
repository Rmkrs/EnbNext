using EnbNext.Presentation;
using System.IO.Pipes;
namespace EnbNext.App;
internal sealed partial class MultiboxPeer
{
    private readonly object peerFeedbackGate=new();
    private long sentShortcutRevision=-1;
    internal MultiboxState Current=>Volatile.Read(ref current);
    internal static MultiboxState MergeShortcutFeedback(MultiboxState known,MultiboxState? update)
        =>update is not null && update.Instance==known.Instance && update.Account==known.Account &&
          update.Character==known.Character && update.Avatar==known.Avatar && update.Server==known.Server &&
          update.Context==known.Context && update.ShortcutRevision>known.ShortcutRevision &&
          update.Shortcuts is {Length:<=12} slots && slots.All(s=>s is not null && s.Slot is >=0 and <12)
          ?known with {Shortcuts=slots,ShortcutRevision=update.ShortcutRevision}:known;
    private void AcceptShortcutFeedback(MultiboxState update)
    {
        lock(peerFeedbackGate)
        {
            var old=peers;int index=Array.FindIndex(old,p=>p.Instance==update.Instance);
            if(index<0)return;
            var merged=MergeShortcutFeedback(old[index],update);
            if(ReferenceEquals(merged,old[index]))return;
            var next=(MultiboxState[])old.Clone();next[index]=merged;Volatile.Write(ref peers,next);
        }
    }
    public async Task PublishShortcutsAsync(EnbRemoteShortcut[] slots)
    {
        var state=Current;
        if(!state.Shortcuts.SequenceEqual(slots)){Publish(state with {Shortcuts=slots});state=Current;}
        if(state.ShortcutRevision==sentShortcutRevision)return;
        sentShortcutRevision=state.ShortcutRevision;
        foreach(var peer in Peers)
        {
            if(!peer.Leader || peer.Server!=state.Server || !peer.GroupMembers.Contains(state.Avatar) ||
                peer.Context is not {} context || state.Context?.Sector!=context.Sector)continue;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stop.Token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
                await using var pipe=new NamedPipeClientStream(".",Pipe(peer.Instance),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token);
                await WriteAsync(pipe,new(Version,"feedback",Instance,Guid.Empty,"",state),deadline.Token);
            }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or ObjectDisposedException){}
        }
    }
}
