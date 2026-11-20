using EnbNext.Assets;
using EnbNext.Engine;

namespace EnbNext.Net7.Tests;
public sealed class CharacterInterfaceSettingsTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"interface-"+Guid.NewGuid().ToString("N"));
    private EnbCharacterInterfaceSettings Load(ulong account,ulong avatar)=>EnbCharacterInterfaceSettings.Load(account,avatar,directory);
    [Fact]
    public void ChoicesAreIndependentByAccountAndAvatarAndRetainDisabledAutopilot()
    {
        var a=Load(1,1);Assert.False(a.RoutePlanner);Assert.False(a.AutoPilot);
        a.Save(true,true,Guid.Empty);Assert.True(Load(1,1).AutoPilotAvailable);
        Assert.False(Load(1,2).AutoPilot);Assert.False(Load(2,1).RoutePlanner);
        a.Save(false,true,Guid.Empty);var restored=Load(1,1);
        Assert.True(restored.AutoPilot);Assert.False(restored.AutoPilotAvailable);
        restored.Save(true,restored.AutoPilot,Guid.Empty);Assert.True(Load(1,1).AutoPilotAvailable);
    }
    [Fact]
    public void ProfilesAreSharedButSelectionsArePerCharacterAndDeletedSelectionIsSilent()
    {
        string path=Path.Combine(directory,"profiles.json");
        var a=new EnbUiLayoutProfiles(null);a.OpenStore(path,Load(1,1));
        a.Begin();a.Create("Shared",false);Assert.True(a.Save());Guid id=a.Document.Selected;
        var b=new EnbUiLayoutProfiles(null);b.OpenStore(path,Load(2,1));
        Assert.Single(b.Document.Profiles);Assert.Equal(Guid.Empty,b.Document.Selected);
        b.Begin();b.Select(id);Assert.True(b.Save());
        a.OpenStore(path,Load(1,1));Assert.Equal(id,a.Document.Selected);
        b.Begin();b.Delete();Assert.True(b.Save());
        a.OpenStore(path,Load(1,1));Assert.Null(a.Error);Assert.Equal(Guid.Empty,a.Document.Selected);Assert.True(a.Locked);
        a.Begin();Assert.Null(a.Error);a.Cancel();
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
