using System.Text.Json;
using EnbNext.App;
namespace EnbNext.Net7.Tests;

[CollectionDefinition("Developer diagnostics", DisableParallelization=true)]
public sealed class DeveloperDiagnosticsCollection { }

[Collection("Developer diagnostics")]
public sealed class DeveloperRenderDiagnosticsTests
{
    [Fact]
    public async Task ClosingRestoresDefaultsButCannotLaunderDiagnosticReceipts()
    {
        Assert.False(DeveloperRenderDiagnostics.Open);
        Assert.False(DeveloperRenderDiagnostics.SessionUsed);
        Assert.Null(FullClientBenchmarkMeter.Active);
        Assert.False(FrameIntervalClock.IsObserved);
        for(int i=0;i<100;i++) using(FullClientBenchmarkMeter.Enter("world",true)) { }
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) using(FullClientBenchmarkMeter.Enter("world",true)) { }
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        DeveloperRenderDiagnostics.OpenSession();
        DeveloperRenderDiagnostics.Dazzles=DeveloperRenderDiagnostics.Particles=DeveloperRenderDiagnostics.Effects=DeveloperRenderDiagnostics.Scanner=false;
        DeveloperRenderDiagnostics.Hud=DeveloperRenderDiagnostics.Backdrop=DeveloperRenderDiagnostics.Shadows=false;
        DeveloperRenderDiagnostics.Gutter=DeveloperRenderDiagnostics.Audio=false;
        DeveloperRenderDiagnostics.NotifyChanged();
        var altered=JsonSerializer.SerializeToElement(DeveloperRenderDiagnostics.Receipt());
        Assert.False(altered.GetProperty("EligibleForParityOrPerformanceAcceptance").GetBoolean());
        Assert.False(altered.GetProperty("Dazzles").GetBoolean());
        DeveloperRenderDiagnostics.CloseSession();
        Assert.False(DeveloperRenderDiagnostics.Open);
        Assert.True(DeveloperRenderDiagnostics.Dazzles && DeveloperRenderDiagnostics.Particles && DeveloperRenderDiagnostics.Effects && DeveloperRenderDiagnostics.Scanner);
        Assert.True(DeveloperRenderDiagnostics.Hud && DeveloperRenderDiagnostics.Backdrop && DeveloperRenderDiagnostics.Shadows);
        Assert.True(DeveloperRenderDiagnostics.Gutter && DeveloperRenderDiagnostics.Audio);
        Assert.Contains("DIAGNOSTIC",DeveloperRenderDiagnostics.TitlePrefix);
        var meter=new FullClientBenchmarkMeter();
        var receipt=JsonSerializer.SerializeToElement(meter.Finish());
        Assert.True(receipt.GetProperty("DeveloperDiagnostics").GetProperty("DiagnosticSession").GetBoolean());
        Assert.False(receipt.GetProperty("DeveloperDiagnostics").GetProperty("EligibleForParityOrPerformanceAcceptance").GetBoolean());
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-developer-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new SessionDiagnosticLog(directory);
            var scene=new EnbNext.Presentation.EnbSceneSnapshot(1,123,null,null,null,false,null,null!,[],null!,null!,[],[],null,null,[],[]);
            log.PerformanceScene(new(scene,default,123,"diagnostic"));
            await Task.Run(log.Flush);
            Assert.Null(log.Error);
            using var saved=JsonDocument.Parse(File.ReadAllText(log.Path+".scene-00.json"));
            Assert.True(saved.RootElement.GetProperty("DeveloperDiagnostics").GetProperty("DiagnosticSession").GetBoolean());
        }
        finally { if(Directory.Exists(directory))Directory.Delete(directory,true); }

        Assert.Null(FullClientBenchmarkMeter.Active);
        Assert.False(FrameIntervalClock.IsObserved);
    }
}
