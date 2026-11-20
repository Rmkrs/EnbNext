using EnbNext.App;
using EnbNext.Client.Models;
namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class GroupLaunchTests
{
    private static SavedLoginGroup Group(int count=3)=>new(Guid.NewGuid(),"Team","server",Enumerable.Range(0,count).Select(i=>new SavedGroupMember(Guid.NewGuid(),Guid.NewGuid(),"Pilot"+i)).ToArray());
    [Theory][InlineData(1,false)][InlineData(2,true)][InlineData(6,true)][InlineData(7,false)]
    public void CompositionBounds(int count,bool valid)=>Assert.Equal(valid,LoginGroupRules.Validate(Group(count)) is null);
    [Fact]public void RejectsDuplicateAccountAndMissingName()
    {
        var group=Group();group.Members[1]=group.Members[1] with {AccountId=group.Members[0].AccountId};
        Assert.NotNull(LoginGroupRules.Validate(group));Assert.NotNull(LoginGroupRules.Validate(Group() with {Name=" "}));
    }
    [Fact]public void SequenceWaitsForReadinessAndPausesOnFailure()
    {
        using var session=new GroupLaunchSession(Group());Assert.Equal(0,session.Next);Assert.True(session.Begin(0));Assert.Equal(1,session.Next);
        Assert.False(session.Begin(0));session.Report(0,"Online");Assert.Equal(1,session.Next);Assert.True(session.Begin(1));
        session.Report(1,"Failed","Rejected");Assert.Equal(-1,session.Next);session.Report(1,"Offline");Assert.Equal("Failed",session.Members[1].State);
        session.Continue();Assert.True(session.Begin(1));session.Report(1,"Online");Assert.Equal(2,session.Next);
        session.Report(1,"Offline");Assert.True(session.Paused);session.Stop();session.Continue();Assert.Equal(2,session.Next);
    }
    [Fact]public void StopPreventsQueuedLaunches()
    {
        using var session=new GroupLaunchSession(Group());session.Stop();Assert.Equal(-1,session.Next);session.Continue();Assert.Equal(0,session.Next);
    }
    [Fact]public void GroupRoundTripAndRenamePreserveIdentityAndDisplayPath()
    {
        string dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            var store=new SavedGroupStore(Path.Combine(dir,"groups.json"));var group=Group();store.Save(group);store.Load();
            Assert.Equal(group.Id,store.Find("SERVER","team")!.Id);string path=SavedGroupStore.DisplayPath(Path.Combine(dir,"settings.json"),group.Id,group.Members[0].Id);
            store.Save(group with {Name="Renamed"});store.Load();Assert.Single(store.Groups);Assert.Equal(group.Id,store.Groups[0].Id);
            Assert.Equal(path,SavedGroupStore.DisplayPath(Path.Combine(dir,"settings.json"),store.Groups[0].Id,store.Groups[0].Members[0].Id));
            Assert.NotEqual(path,SavedGroupStore.DisplayPath(Path.Combine(dir,"settings.json"),Guid.NewGuid(),group.Members[0].Id));
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact]public void DeletePersistsOnlyTheSelectedGroupRemoval()
    {
        string dir=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
        try
        {
            string path=Path.Combine(dir,"groups.json");var store=new SavedGroupStore(path);
            var first=Group();var second=Group() with {Name="Other"};store.Save(first);store.Save(second);
            store.Delete(first.Id);var loaded=new SavedGroupStore(path);loaded.Load();
            Assert.Equal(second.Id,Assert.Single(loaded.Groups).Id);
            loaded.Delete(first.Id);loaded.Load();Assert.Single(loaded.Groups);
            loaded.Delete(second.Id);loaded.Load();Assert.Empty(loaded.Groups);
        }
        finally{Directory.Delete(dir,true);}
    }
    [Fact]public void StartupAcceptsGroupAndRejectsUnboundMember()
    {
        Assert.Null(StartupLogin.Parse(["--group","Team"])!.Error);
        Assert.NotNull(StartupLogin.Parse(["--username","a","--character","b","--member",Guid.NewGuid().ToString()])!.Error);
        var member=Guid.NewGuid();var value=StartupLogin.Parse(["--group","Team","--member",member.ToString(),"--launch-pipe","EnbNext-launch-test"])!;
        Assert.Null(value.Error);Assert.Equal(member,value.Member);
    }
    [Fact]public async Task LocalPipePreservesFailureAfterChildCloses()
    {
        using var session=new GroupLaunchSession(Group());session.Begin(1);string name=session.Listen(1);
        await using(var reporter=new GroupLaunchReporter(name,session.Members[1].Definition.Id))
        {await reporter.ConnectAsync();await reporter.SendAsync("Failed","Synthetic rejection");}
        await session.ReadersCompleted.WaitAsync(TimeSpan.FromSeconds(4));session.Tick();
        Assert.True(session.Paused);Assert.Equal("Failed",session.Members[1].State);Assert.Equal("Synthetic rejection",session.Members[1].Error);
    }
    [Fact]public async Task LocalPipeDoesNotAcceptAnotherMemberIdentity()
    {
        using var session=new GroupLaunchSession(Group());session.Begin(1);string name=session.Listen(1);
        await using(var reporter=new GroupLaunchReporter(name,Guid.NewGuid()))
        {await reporter.ConnectAsync();await reporter.SendAsync("Online");}
        await session.ReadersCompleted.WaitAsync(TimeSpan.FromSeconds(4));session.Tick();
        Assert.Equal("Offline",session.Members[1].State);Assert.True(session.Paused);
    }

    [Fact]public async Task ChildProcessExitPausesQueueAndAllowsRelaunch()
    {
        using var session=new GroupLaunchSession(Group());session.Begin(0);session.Report(0,"Online");session.Begin(1);
        var info=new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows()?Environment.GetEnvironmentVariable("ComSpec")??"cmd.exe":"/bin/sh"){UseShellExecute=false,CreateNoWindow=true};
        if(OperatingSystem.IsWindows()){info.ArgumentList.Add("/d");info.ArgumentList.Add("/c");}else info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exit 0");var process=System.Diagnostics.Process.Start(info)!;session.Attach(1,process);
        await process.WaitForExitAsync();session.Tick();Assert.Equal("Offline",session.Members[1].State);Assert.Equal(-1,session.Next);
        Assert.True(session.Begin(1));Assert.False(session.Begin(1));
    }

}
