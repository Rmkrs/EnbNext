using EnbNext.Engine;
using EnbNext.App;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class LiveIssueLogTests
{
    [Fact]
    public async Task SavesInBackgroundAndPreservesPreviousSessionWhenCleared()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-issues-"+Guid.NewGuid().ToString("N"));
        var log=new LiveIssueLog(directory);
        try
        {
            log.ObserveDiagnostic("ordinary session progress");
            Assert.Null(log.ReportPath);Assert.False(Directory.Exists(directory));
            log.Report("Presentation","effects","E10008 failed to load · é");
            string first=Assert.IsType<string>(log.ReportPath);
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!File.Exists(first) && DateTime.UtcNow<deadline)await Task.Delay(20);
            Assert.Contains("E10008 failed to load · é",File.ReadAllText(first, System.Text.Encoding.UTF8));
            Assert.Contains("module ",File.ReadAllText(first));
            log.Report("Presentation","effects","Updated failure",true);
            log.Clear();
            Assert.Equal(0,log.Count);Assert.Null(log.ReportPath);
            Assert.Contains("Updated failure",File.ReadAllText(first));
            Assert.Contains("2 report(s)",File.ReadAllText(first));
            log.Report("Assets","station","Station error");log.Flush();
            Assert.NotEqual(first,log.ReportPath);
            Assert.DoesNotContain("Updated failure",File.ReadAllText(log.ReportPath!));
            Assert.Equal(2,Directory.GetFiles(directory,"*.issues.txt").Length);
            Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
        }
        finally{log.Flush();if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public void FailedSaveKeepsIssuesAndRetriesWithoutRecursiveErrors()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-issues-"+Guid.NewGuid().ToString("N"));
        File.WriteAllText(directory,"blocks directory creation");
        var log=new LiveIssueLog(directory);
        try
        {
            log.Report("Assets","missing","Missing texture");log.Flush();
            Assert.NotNull(log.SaveError);Assert.Equal(1,log.Count);
            Assert.Contains("Automatic save failed",log.Describe());
            File.Delete(directory);
            log.Flush();
            Assert.Null(log.SaveError);
            Assert.Contains("Missing texture",File.ReadAllText(log.ReportPath!));
            Assert.DoesNotContain("Automatic save failed",File.ReadAllText(log.ReportPath!));
        }
        finally{log.Flush();if(File.Exists(directory))File.Delete(directory);if(Directory.Exists(directory))Directory.Delete(directory,true);}
    }

    [Fact]
    public void RetentionAndConcurrentReportsKeepBoundedCompleteSnapshots()
    {
        string directory=Path.Combine(Path.GetTempPath(),"EnbNext-issues-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var log=new LiveIssueLog(directory);
        try
        {
            for(int i=0;i<35;i++)
            {
                string path=Path.Combine(directory,$"session-old-{i}.issues.txt");
                File.WriteAllText(path,"old");File.SetLastWriteTimeUtc(path,DateTime.UtcNow.AddDays(-1));
            }
            File.WriteAllText(Path.Combine(directory,"unrelated.txt"),"keep");
            Parallel.For(0,120,i=>log.Report("Assets",i.ToString(),$"Failure {i}"));
            log.Flush();
            string text=File.ReadAllText(log.ReportPath!);
            Assert.Equal(100,log.Count);Assert.Contains("20 older entries omitted",text);
            Assert.Equal(100,text.Split("report(s)").Length-1);
            Assert.Equal(30,Directory.GetFiles(directory,"session-*.issues.txt").Length);
            Assert.Equal("keep",File.ReadAllText(Path.Combine(directory,"unrelated.txt")));
        }
        finally{log.Flush();Directory.Delete(directory,true);}
    }
    [Theory]
    [InlineData("name decal target/applied/unresolved 3/3/0", false)]
    [InlineData("packets target/applied/excluded/unresolved 20/19/1/0", false)]
    [InlineData("color entries 8 packets target/applied/excluded/pending/unresolved 19/0/0/19/0", false)]
    [InlineData("color entries 2 packets target/applied/excluded/pending/unresolved 19/18/0/0/1", true)]
    [InlineData("decals target/applied/tinted/unassigned/unresolved 3/2/0/1/0", false)]
    [InlineData("name decal target/applied/unresolved 3/2/1", true)]
    [InlineData("roots ready/fail 0/12", true)]
    [InlineData("render roots: compiled roots 62 · preorder fail 0 · HLOD fail 0 · HAnim audit pending 3 [BASE 1530, BASE 1531, BASE 1532] · root [none]", false)]
    [InlineData("render roots: compiled roots 62 · preorder fail 1 · HLOD fail 0 · HAnim audit pending 2 [BASE 1531, BASE 1532]", true)]
    [InlineData("effect objects draws 75 pending 0 blocked 31", true)]
    [InlineData("effect objects draws 75 pending 0 blocked 0", false)]
    [InlineData("render animation: retained emitters 0 rejected 0 · warp plume attached 0 active 0", false)]
    [InlineData("target renderer: render animation: retained emitters 0 rejected 0 · none active", false)]
    [InlineData("retained emitters 0 rejected 1", true)]
    [InlineData("retained/rejected 12/0", false)]
    [InlineData("retained/rejected 0/12", true)]
    [InlineData("animation rejected: invalid channel", true)]
    [InlineData("rejected 0 · render failure: invalid framebuffer", true)]
    [InlineData("effect visuals passes 20 hidden 0 pending 0 blocked 0 recovered 1", true)]
    [InlineData("effect visuals passes 20 hidden 0 pending 0 blocked 0 recovered 0", false)]
    [InlineData("E20315: radius-triggered SFX is not implemented", true)]
    [InlineData("effect objects draws 0 pending 0 blocked 1 · E1097: ParticleJetEffect visual runtime not implemented", true)]
    public void PresentationCountersUseTheMatchingLabel(string diagnostic, bool expected)
        => Assert.Equal(expected, LiveIssueLog.IsPresentationProblem(diagnostic));

    [Theory]
    [InlineData("Generic Music Theme")]
    [InlineData("Generic Music Theme 2")]
    public void AbsentRetailSoundIsSilentButMalformedMediaIsAnError(string name)
    {
        var paths=EnbNext.Assets.EnbInstalledAssetLocator.TryLocate();
        if(paths is null)return;
        var assets=EnbNext.Assets.EnbRetailAssetBundle.Load(paths);
        var missing=LiveWorldOpenAlAudio.DecodeEffectSound(name,assets);
        Assert.True(missing.MissingMedia);Assert.Null(missing.Error);Assert.Null(missing.Pcm);
        string file=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".wav");
        try
        {
            File.WriteAllBytes(file,[0,1,2]);
            var malformed=LiveWorldOpenAlAudio.DecodeEffectSound(file,assets);
            Assert.False(malformed.MissingMedia);Assert.NotNull(malformed.Error);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void MissingTerrainHeightIsDistinctFromInvalidHeightData()
    {
        using var ini=new EnbNext.Rendering.EnbRetailTerrainOwnedIni(System.Text.Encoding.ASCII.GetBytes("[HEIGHTMAP]\nHEIGHT_MAP=absent.pgm\n"));
        Assert.Throws<EnbNext.Rendering.EnbRetailTerrainLoader.MissingHeightMapException>(()=>
            EnbNext.Rendering.EnbRetailTerrainLoader.Load(1,ini,_=>throw new FileNotFoundException(),1024));
        Assert.Throws<InvalidDataException>(()=>
            EnbNext.Rendering.EnbRetailTerrainLoader.Load(1,ini,_=>[0,1,2],1024));
        Assert.False(LiveIssueLog.IsPresentationProblem("terrain absent: Terrain height map absent: absent.pgm"));
        Assert.True(LiveIssueLog.IsPresentationProblem("terrain rejected: Invalid height data"));
    }

    [Fact]
    public void InactiveProjectorDoesNotMaskAnIndependentFailure()
    {
        const string normal="effect visuals passes 1 blocked 0 inactive-projector 1";
        Assert.False(LiveIssueLog.IsPresentationProblem(normal));
        Assert.True(LiveIssueLog.IsPresentationProblem(normal+" · effect SFX blocked 1"));
    }

    [Theory]
    [InlineData("",false)]
    [InlineData("[EFFECT-1]\n",false)]
    [InlineData("[EFFECT-1]\nEffectClass=UnimplementedFutureEffect",true)]
    public void NativeEmptyEffectOmissionsAreNotDefectsButUnknownClassesAre(string text,bool problem)
    {
        var scheduler=new EnbNext.Rendering.EnbCurrentRetailEffectRslSchedulerRuntime();
        var update=scheduler.Update([EnbR10EffectSoundTests.Root()],EnbR10EffectSoundTests.Catalog(text),0);
        Assert.Empty(update.VisualNodes);Assert.Empty(update.SoundNodes);
        Assert.Equal(problem,LiveIssueLog.IsPresentationProblem(scheduler.LifecycleHistory));
        scheduler.Clear();Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
