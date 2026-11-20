using EnbNext.App;

namespace EnbNext.Net7.Tests;

public sealed class SessionDiagnosticLogTests
{
    [Fact]
    public async Task OwnerMutationAfterCaptureCannotReachDiagnosticTimer()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-owner-scene-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new SessionDiagnosticLog(directory);
            var world=EnbR11FlightTests.FlightWorld();
            var publisher=new EnbNext.Presentation.EnbPresentationSnapshotPublisher();publisher.Publish(world,null,1000);
            var frame=publisher.AcquireLiveFrame(world,1000,null)!;
            var owner=frame.MutableObject(0);owner.Name="captured";
            Assert.True(EnbNext.Rendering.EnbResolvedRenderCamera.TryCreate(new(100,200,300),System.Numerics.Quaternion.Identity,
                new(800,600,1),Math.PI/2,new(1,1000000),out var camera));
            log.PerformanceScene(new(frame,camera,1000,"profile"));
            owner.Name="later";frame.Release();publisher.Clear();
            await Task.Run(log.Flush);
            Assert.Null(log.Error);
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(log.Path+".scene-00.json"));
            Assert.Equal("captured",json.RootElement.GetProperty("Scene").GetProperty("Objects")[0].GetProperty("Name").GetString());
        }
        finally{if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public async Task PerformanceScenesSurviveTranscriptFlushAndRotateWithExactCamera()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-scenes-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new SessionDiagnosticLog(directory);
            var scene=new EnbNext.Presentation.EnbSceneSnapshot(1,123,null,null,null,false,null,null!,[],null!,null!,[],[],null,null,[],[]);
            Assert.True(EnbNext.Rendering.EnbResolvedRenderCamera.TryCreate(new(100,200,300),System.Numerics.Quaternion.Identity,
                new(1700,950,1.25),Math.PI/2,new(1,1000000),out var camera));
            var origin=DateTimeOffset.UtcNow;
            var settings=new EnbNext.Rendering.EnbRenderDetailRuntimeSettings(false,1.2f,39680,2000,39680,7,new(0,0));
            for(int i=0;i<14;i++)
            {
                log.PerformanceScene(new(scene,camera,123,"profile"){CapturedUtc=origin.AddSeconds(i*11),DetailSettings=settings,FixedSessionId=1073758730});
                await Task.Run(log.Flush);
            }
            Assert.Null(log.Error);
            Assert.Equal(12,Directory.GetFiles(directory,"*.scene-*.json").Length);
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(log.Path+".scene-01.json"));
            var options=new System.Text.Json.JsonSerializerOptions{IncludeFields=true,
                NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals};
            Assert.Equal(camera,System.Text.Json.JsonSerializer.Deserialize<EnbNext.Rendering.EnbResolvedRenderCamera>(json.RootElement.GetProperty("Camera"),options));
            Assert.Equal(settings,System.Text.Json.JsonSerializer.Deserialize<EnbNext.Rendering.EnbRenderDetailRuntimeSettings>(json.RootElement.GetProperty("DetailSettings"),options));
            Assert.Equal(1073758730u,json.RootElement.GetProperty("FixedSessionId").GetUInt32());
            Assert.Equal(origin.AddSeconds(13*11),json.RootElement.GetProperty("CapturedUtc").GetDateTimeOffset());
            Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }
    [Fact]
    public void DockingPreservesPriorSpaceFrameTimingWithoutCopyingBulkPresentation()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-session-"+Guid.NewGuid().ToString("N"));
        try
        {
            using var log=new SessionDiagnosticLog(directory);
            log.Presentation("space\nframe loop: slow\nslow frames (history):\n  age 1s · render profile: space hitch\nlarge geometry receipt");log.Flush();
            log.Presentation("station\nframe loop: settled\nrender profile: station");log.Flush();
            string history=File.ReadAllText(log.Path+".frames.txt");
            Assert.Contains("space hitch",history);
            Assert.Contains("station",history);
            Assert.DoesNotContain("large geometry receipt",history);
            Assert.StartsWith("station",File.ReadAllText(log.Path+".presentation.txt"));
            File.WriteAllText(log.Path+".frames.txt",new string('x',8*1024*1024));
            log.Presentation("frame loop: after rotation");log.Flush();
            Assert.True(File.Exists(log.Path+".frames.txt.previous"));
            Assert.Contains("after rotation",File.ReadAllText(log.Path+".frames.txt"));
            Assert.Null(log.Error);
        }
        finally {if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public void FlushPreservesTranscriptAndReplacesPresentation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext-session-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            using (var log = new SessionDiagnosticLog(directory))
            {
                path = log.Path!;
                log.Append("login failed\n"); log.Presentation("login"); log.Flush();
                log.Append("login accepted\n"); log.Presentation("station");
            }
            Assert.Equal("login failed\nlogin accepted\n", File.ReadAllText(path));
            Assert.Equal("station", File.ReadAllText(path + ".presentation.txt"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void WriteFailureRetainsPendingDiagnosticsForRetry()
    {
        string directory = Path.Combine(Path.GetTempPath(), "EnbNext-session-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(directory, "blocked");
        try
        {
            using var log = new SessionDiagnosticLog(directory);
            log.Append("important receipt\n"); log.Presentation("world"); log.Flush();
            Assert.NotNull(log.Error);
            File.Delete(directory); log.Flush();
            Assert.Null(log.Error);
            Assert.Equal("important receipt\n", File.ReadAllText(log.Path!));
            Assert.Equal("world", File.ReadAllText(log.Path + ".presentation.txt"));
        }
        finally
        {
            if (File.Exists(directory)) File.Delete(directory);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
