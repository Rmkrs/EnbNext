using System.Text.Json;
using EnbNext.Net7.Diagnostics;

namespace EnbNext.Net7.Tests;

public sealed class Net7WireReplayRecordingTests
{
    [Fact]
    public async Task FixedSegmentDeliversPrefixInOrderAndNeverAgesIntoTail()
    {
        using var fixture = new Recording([(1, 0), (2, 10), (3, 20), (4, 30), (5, 40)], 50);
        var recording = Net7WireReplayRecording.Load(fixture.Path);
        var trial = recording.CreateTrial(20, 40);
        var seen = new List<long>();
        ValueTask Deliver(Net7WireCaptureFrame f, CancellationToken _) { seen.Add(f.Index); return ValueTask.CompletedTask; }
        await trial.AdvanceAsync(19, Deliver);
        Assert.False(trial.IsMeasuring);
        Assert.Equal(new long[]{1,2}, seen);
        await trial.AdvanceAsync(20, Deliver);
        Assert.True(trial.IsMeasuring);
        await trial.AdvanceAsync(50, Deliver);
        Assert.True(trial.IsComplete);
        Assert.False(trial.IsMeasuring);
        Assert.Equal(new long[]{1,2,3,4}, seen);
        await Assert.ThrowsAsync<InvalidOperationException>(() => trial.AdvanceAsync(0, Deliver).AsTask());
        var repeat = recording.CreateTrial(20, 40);
        var repeated = new List<long>();
        await repeat.AdvanceAsync(40, (f, _) => { repeated.Add(f.Index); return ValueTask.CompletedTask; });
        Assert.Equal(seen, repeated);
    }

    [Fact]
    public async Task RenderingCadenceDoesNotChangePacketSequence()
    {
        using var fixture = new Recording([(1,0),(2,7),(3,19),(4,19),(5,25),(6,99)], 100);
        var recording = Net7WireReplayRecording.Load(fixture.Path);
        async Task<List<long>> Replay(int quantum)
        {
            var trial = recording.CreateTrial(20, 100);
            var seen = new List<long>();
            for(int now=0; now<100; now+=quantum)
                await trial.AdvanceAsync(now, (f, _) => {seen.Add(f.Index);return ValueTask.CompletedTask;});
            await trial.AdvanceAsync(100, (f, _) => {seen.Add(f.Index);return ValueTask.CompletedTask;});
            return seen;
        }
        Assert.Equal(await Replay(1), await Replay(17));
    }

    [Fact]
    public async Task ConcurrentTimestampRegressionKeepsCaptureIndexOrder()
    {
        using var fixture = new Recording([(2,15),(1,20),(3,30)], 40);
        var trial = Net7WireReplayRecording.Load(fixture.Path).CreateTrial(0,40);
        var seen = new List<long>();
        await trial.AdvanceAsync(19, (f,_)=>{seen.Add(f.Index);return ValueTask.CompletedTask;});
        Assert.Empty(seen);
        await trial.AdvanceAsync(20, (f,_)=>{seen.Add(f.Index);return ValueTask.CompletedTask;});
        Assert.Equal(new long[]{1,2},seen);
    }

    [Fact]
    public void IncompleteRecordingOrOutOfRangeWindowIsRejected()
    {
        using var incomplete = new Recording([(1,0)], null);
        Assert.Throws<InvalidDataException>(()=>Net7WireReplayRecording.Load(incomplete.Path));
        using var complete = new Recording([(1,0)], 100);
        var recording=Net7WireReplayRecording.Load(complete.Path);
        Assert.Throws<ArgumentOutOfRangeException>(()=>recording.CreateTrial(0,101));
        Assert.Throws<ArgumentOutOfRangeException>(()=>recording.CreateTrial(20,20));
    }

    [Fact]
    public void DuplicateIndexOrDataAfterStopIsRejected()
    {
        using var duplicate = new Recording([(1,0),(1,1)], 100);
        Assert.Throws<InvalidDataException>(()=>Net7WireReplayRecording.Load(duplicate.Path));
        using var beyond = new Recording([(1,101)], 100);
        Assert.Throws<InvalidDataException>(()=>Net7WireReplayRecording.Load(beyond.Path));
    }

    [Fact]
    public async Task OutboundRemainsDistinctAndFailedDeliveryIsNotConsumed()
    {
        using var fixture=new Recording([(1,0)],10);
        var trial=Net7WireReplayRecording.Load(fixture.Path).CreateTrial(0,10);
        await Assert.ThrowsAsync<IOException>(()=>trial.AdvanceAsync(0,(f,_)=>throw new IOException()).AsTask());
        Assert.Equal(0,trial.DeliveredFrames);
        await trial.AdvanceAsync(0,(f,_)=>{Assert.Equal(Net7WireDirection.Outbound,f.Direction);Assert.Equal(new byte[]{4,0,1,0},f.WireBytes);return ValueTask.CompletedTask;});
        Assert.Equal(1,trial.DeliveredFrames);
    }

    private sealed class Recording : IDisposable
    {
        public string Path {get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),Guid.NewGuid()+".enbcap.jsonl");
        public Recording((long Index,long Time)[] frames,long? stop)
        {
            var lines=new List<string>{JsonSerializer.Serialize(new {type="header",format="enbnext-wire-capture",version=1})};
            foreach(var f in frames)lines.Add(JsonSerializer.Serialize(new {type="frame",index=f.Index,elapsedMicroseconds=f.Time,timestampUtc=DateTimeOffset.UnixEpoch,direction="out",transport="tcp",channel="account-tcp",localEndPoint="127.0.0.1:1",remoteEndPoint="127.0.0.1:2",wireBase64=Convert.ToBase64String([4,0,1,0])}));
            if(stop.HasValue)lines.Add(JsonSerializer.Serialize(new {type="marker",name="capture-stop",elapsedMicroseconds=stop.Value}));
            File.WriteAllLines(Path,lines);
        }
        public void Dispose()=>File.Delete(Path);
    }
}
