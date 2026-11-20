using EnbNext.Engine;
using EnbNext.App;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Net7.Tests;

public sealed class EnbMissingEffectDefinitionTests
{
    private static EnbRetailPresentationCatalog Installed()
    {
        var paths=EnbInstalledAssetLocator.TryLocate();Assert.NotNull(paths);
        return EnbRetailAssetBundle.Load(paths).PresentationCatalog!;
    }
    private static EnbSceneEffectSnapshot Root(ushort definition) =>
        new(10,1,1,2,definition,0,0,"",0,true,1,default,0,0,false,default,1,
            EnbSceneEffectGeometryKind.ObjectToObject,default,-1,default,0,0,null);

    [Theory]
    [InlineData(524)]
    [InlineData(20151)]
    [InlineData(20369)]
    [InlineData(560)]
    [InlineData(10142)]
    public void AbsentOrEmptyRootsRemainDiagnosticTombstonesWithoutIssues(ushort definition)
    {
        var catalog=Installed();var root=Root(definition);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Equal(1,scheduler.Update([root],catalog,0).Receipt.RejectedRoots);
        var history=scheduler.LifecycleHistory;
        Assert.False(LiveIssueLog.IsPresentationProblem(history));
        var repeated=scheduler.Update([root],catalog,100);
        Assert.Empty(repeated.VisualNodes);Assert.Empty(repeated.SoundNodes);
        Assert.Equal(history,scheduler.LifecycleHistory);
        scheduler.Update([],catalog,101);Assert.Equal(0,scheduler.TrackedRootCount);
    }

    [Fact]
    public void MissingSoundNextLinkKeepsAuthoredFirstTwoVoicesWithoutSubstitution()
    {
        var catalog=Installed();var root=Root(10140);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        Assert.Contains(scheduler.Update([root],catalog,0).SoundNodes,n=>n.Active && n.Definition.DefinitionId==10140);
        Assert.Contains(scheduler.Update([root],catalog,8000).SoundNodes,n=>n.Active && n.Definition.DefinitionId==10141);
        var ended=scheduler.Update([root],catalog,15000);
        Assert.Empty(ended.SoundNodes);Assert.Empty(ended.VisualNodes);
        Assert.False(LiveIssueLog.IsPresentationProblem(scheduler.LifecycleHistory));
        Assert.Contains("E10141->E10142",scheduler.LifecycleHistory);
        Assert.DoesNotContain("E10143",scheduler.LifecycleHistory);
    }

    [Fact]
    public void MissingSplineStartLinkPreservesFourDefinedSiblings()
    {
        var catalog=Installed();var root=Root(556);
        var scheduler=new EnbCurrentRetailEffectRslSchedulerRuntime();
        foreach(uint tick in new uint[]{0,10,20})scheduler.Update([root],catalog,tick);
        var active=scheduler.Update([root],catalog,30);
        Assert.Equal(new[]{556,557,558,559},active.VisualNodes.Where(n=>n.Active).Select(n=>n.Definition.DefinitionId).Order());
        Assert.False(LiveIssueLog.IsPresentationProblem(scheduler.LifecycleHistory));
        Assert.Contains("E559->E560",scheduler.LifecycleHistory);
        Assert.Empty(scheduler.Update([root],catalog,280).VisualNodes);
        scheduler.Update([],catalog,281);Assert.Equal(0,scheduler.TrackedRootCount);
    }
}
