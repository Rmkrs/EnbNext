using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

public sealed class EnbStationMachineryClockTests
{
    [InstalledRetailFact]
    public void MissingAnimationKeepsTheAuthoredModelWithoutAnAnimationSample()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var installed=EnbStationMachinery.Load(assets,"psd01a").First();
        var machine=EnbStationMachinery.BindAnimation(assets,installed.Name,installed.Resource,"offline-missing.machinery");
        Assert.Same(installed.Resource,machine.Resource);
        Assert.Null(machine.Animation);
        Assert.NotNull(machine.AnimationDiagnostic);
        machine.StartPlayback(10000);
        Assert.Null(machine.Sample(10000));Assert.Null(machine.Sample(20000));
    }
    [InstalledRetailFact]
    public void LoopRetainsFrameAndUsesRetailSingleWrapAfterAbsence()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var installed=EnbStationMachinery.Load(assets,"psd01a").First();
        var machine=new EnbStationMachinery(installed.Name,installed.Resource,
            installed.Animation!.Copy().SetNumFrames(31).SetFrameRate(10));
        machine.StartPlayback(10000);
        Assert.Equal(0,machine.Sample(10000)!.Value.Frame);
        Assert.Equal(5,machine.Sample(10500)!.Value.Frame,4);
        Assert.Equal(0,machine.Sample(13000)!.Value.Frame,4);
        Assert.Equal(5,machine.Sample(13500)!.Value.Frame,4);
        Assert.Equal(machine.Sample(13500),machine.Sample(13500));
        //The room is detached, then reattached with no animation setter.
        //9C6270 subtracts one period and returns zero if still past it.
        Assert.Equal(0,machine.Sample(20000)!.Value.Frame);
        Assert.Equal(5,machine.Sample(20500)!.Value.Frame,4);
    }
}
