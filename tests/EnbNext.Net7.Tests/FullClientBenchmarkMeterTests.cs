using System.Text.Json;
using EnbNext.App;
namespace EnbNext.Net7.Tests;

public sealed class FullClientBenchmarkMeterTests
{
    [Fact]
    public void NestedScannerIsInclusiveButNotDoubleCountedInOuterCpuEstimate()
    {
        var before=DateTimeOffset.UtcNow;
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            using(FullClientBenchmarkMeter.Enter("world",true))
            {
                Thread.SpinWait(10000);
                using(FullClientBenchmarkMeter.Enter("scanner"))Thread.SpinWait(10000);
            }
        }
        finally{FullClientBenchmarkMeter.Active=null;}
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        Assert.Equal(1,result.GetProperty("Frames").GetInt64());
        var started=result.GetProperty("StartedUtc").GetDateTimeOffset();
        var ended=result.GetProperty("EndedUtc").GetDateTimeOffset();
        Assert.InRange(started,before,ended);
        Assert.InRange(ended,started,DateTimeOffset.UtcNow);
        var parts=result.GetProperty("InclusiveComponents");
        var world=parts.GetProperty("world");var scanner=parts.GetProperty("scanner");
        Assert.True(world.GetProperty("CyclesPerFrame").GetDouble()>=scanner.GetProperty("CyclesPerFrame").GetDouble());
        Assert.True(world.GetProperty("WallMsPerFrame").GetDouble()>=scanner.GetProperty("WallMsPerFrame").GetDouble());
        Assert.Equal(world.GetProperty("CpuEstimateMsPerFrame").GetDouble(),result.GetProperty("InstrumentedCpuEstimateMsPerFrame").GetDouble(),8);
        Assert.Equal(1,world.GetProperty("Calls").GetInt64());
    }
    [Fact]
    public void ProcessGcPauseIsElapsedTimeNotAdditionalAttributedCpu()
    {
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        var before=GC.GetTotalPauseDuration();
        try
        {
            using(FullClientBenchmarkMeter.Enter("world",true))GC.Collect(2,GCCollectionMode.Forced,blocking:true,compacting:false);
            using(FullClientBenchmarkMeter.Enter("world",true))Thread.SpinWait(1000);
        }
        finally{FullClientBenchmarkMeter.Active=null;}
        var observedMinimum=(GC.GetTotalPauseDuration()-before).TotalMilliseconds;
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        Assert.True(result.GetProperty("GcPauseMs").GetDouble()>=observedMinimum);
        Assert.Equal(result.GetProperty("GcPauseMs").GetDouble()/2,result.GetProperty("GcPauseMsPerFrame").GetDouble(),8);
        var parts=result.GetProperty("InclusiveComponents");
        Assert.Single(parts.EnumerateObject());
        Assert.Equal(parts.GetProperty("world").GetProperty("CpuEstimateMsPerFrame").GetDouble(),result.GetProperty("InstrumentedCpuEstimateMsPerFrame").GetDouble(),8);
    }

    [Fact]
    public void FreshTrialHasNoPriorFrameIntervalAndDisabledScopeIsNoop()
    {
        FullClientBenchmarkMeter.Active=null;using(FullClientBenchmarkMeter.Enter("world",true)){}
        for(int i=0;i<2;i++)
        {
            var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
            try{using(FullClientBenchmarkMeter.Enter("world",true))Thread.SpinWait(1000);}
            finally{FullClientBenchmarkMeter.Active=null;}
            var result=JsonSerializer.SerializeToElement(meter.Finish());
            Assert.Empty(result.GetProperty("FrameIntervalsMs").EnumerateArray());
        }
    }
    [Fact]
    public void SequentialStagesCloseOnFailureWithoutDoubleCountingOuterCpu()
    {
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            using(FullClientBenchmarkMeter.Enter("world",true))
            {
                using var stages=FullClientBenchmarkMeter.BeginStages(true,"phase.first");
                Assert.NotNull(stages);
                Thread.SpinWait(10000);
                stages.Next("phase.second");
                Thread.SpinWait(10000);
                stages.Dispose(); // Using cleanup must not record the last stage twice.
                Assert.Throws<ObjectDisposedException>(()=>stages.Next("phase.invalid"));
            }
            // A subsequent outer scope must not inherit a leaked nesting depth.
            using(FullClientBenchmarkMeter.Enter("after"))Thread.SpinWait(10000);
        }
        finally{FullClientBenchmarkMeter.Active=null;}
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        var parts=result.GetProperty("InclusiveComponents");
        var world=parts.GetProperty("world");
        var first=parts.GetProperty("phase.first");var second=parts.GetProperty("phase.second");
        Assert.Equal(1,first.GetProperty("Calls").GetInt64());
        Assert.Equal(1,second.GetProperty("Calls").GetInt64());
        Assert.False(parts.TryGetProperty("phase.invalid",out _));
        foreach(var property in new[]{"WallMsPerFrame","CyclesPerFrame","BytesPerFrame"})
            Assert.True(first.GetProperty(property).GetDouble()+second.GetProperty(property).GetDouble()<=world.GetProperty(property).GetDouble());
        Assert.Equal(world.GetProperty("CpuEstimateMsPerFrame").GetDouble()+parts.GetProperty("after").GetProperty("CpuEstimateMsPerFrame").GetDouble(),
            result.GetProperty("InstrumentedCpuEstimateMsPerFrame").GetDouble(),8);
    }
    [Fact]
    public void SequentialStagesRequireBothOptInAndAnActiveMeter()
    {
        FullClientBenchmarkMeter.Active=null;
        Assert.Null(FullClientBenchmarkMeter.BeginStages(true,"unused"));
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try { Assert.Null(FullClientBenchmarkMeter.BeginStages(false,"unused")); }
        finally { FullClientBenchmarkMeter.Active=null; }
        Assert.Empty(JsonSerializer.SerializeToElement(meter.Finish()).GetProperty("InclusiveComponents").EnumerateObject());
    }

    [Fact]
    public void AggregateWallObservationsDoNotBecomeCpuOrFrameCounts()
    {
        FullClientBenchmarkMeter.Active=null;
        FullClientBenchmarkMeter.RecordObservedMilliseconds("unused",1);
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.draw",1.25);
            FullClientBenchmarkMeter.RecordObservedMilliseconds("submission.draw",2.5);
        }
        finally{FullClientBenchmarkMeter.Active=null;}
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        Assert.Equal(0,result.GetProperty("Frames").GetInt64());
        Assert.Empty(result.GetProperty("InclusiveComponents").EnumerateObject());
        Assert.Equal(0,result.GetProperty("InstrumentedCpuEstimateMsPerFrame").GetDouble());
        Assert.Equal(3.75,result.GetProperty("ObservedWallOnlyMsPerFrame").GetProperty("submission.draw").GetDouble(),5);
    }

    [Fact]
    public void WorkCountsUseRenderedFramesAndDoNotLeakAcrossTrials()
    {
        FullClientBenchmarkMeter.Active=null;
        FullClientBenchmarkMeter.RecordObservedCount("unused",100);
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            using(FullClientBenchmarkMeter.Enter("world",true))
                FullClientBenchmarkMeter.RecordObservedCount("batches",4);
            using(FullClientBenchmarkMeter.Enter("world",true))
                FullClientBenchmarkMeter.RecordObservedCount("batches",8);
        }
        finally {FullClientBenchmarkMeter.Active=null;}
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        Assert.Equal(2,result.GetProperty("Frames").GetInt64());
        var counts=result.GetProperty("ObservedCounts");
        Assert.False(counts.TryGetProperty("unused",out _));
        Assert.Equal(12,counts.GetProperty("batches").GetProperty("Total").GetInt64());
        Assert.Equal(6,counts.GetProperty("batches").GetProperty("PerFrame").GetDouble());
        Assert.Empty(JsonSerializer.SerializeToElement(new FullClientBenchmarkMeter().Finish()).GetProperty("ObservedCounts").EnumerateObject());
    }

    [Fact]
    public void TitleObserverAndBenchmarkReceiveIdenticalFrameStartClock()
    {
        var timestamps=new List<long>();
        void Observe(long stamp)=>timestamps.Add(stamp);
        FrameIntervalClock.FrameStarted+=Observe;
        var meter=new FullClientBenchmarkMeter();FullClientBenchmarkMeter.Active=meter;
        try
        {
            using(FullClientBenchmarkMeter.Enter("world",true)){}
            using(FullClientBenchmarkMeter.Enter("scanner")){}
            using(FullClientBenchmarkMeter.Enter("world",true)){}
        }
        finally{FullClientBenchmarkMeter.Active=null;FrameIntervalClock.FrameStarted-=Observe;}
        Assert.Equal(2,timestamps.Count);
        var result=JsonSerializer.SerializeToElement(meter.Finish());
        double interval=result.GetProperty("FrameIntervalsMs")[0].GetDouble();
        Assert.Equal(System.Diagnostics.Stopwatch.GetElapsedTime(timestamps[0],timestamps[1]).TotalMilliseconds,interval);
    }
}
