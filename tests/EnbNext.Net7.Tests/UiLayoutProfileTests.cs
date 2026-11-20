using EnbNext.Engine;
using EnbNext.Presentation;
namespace EnbNext.Net7.Tests;

public sealed class UiLayoutProfileTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"ui-layout-test-"+Guid.NewGuid().ToString("N"));
    private EnbUiLayoutProfiles Open()=>new(Path.Combine(directory,"profiles.json"));
    [Fact]
    public void DefaultIsLockedAndCancelDiscardsTheEntireDraft()
    {
        var ui=Open();ui.SetContext(false,new(1920,1080));ui.Begin();
        Assert.True(ui.Locked);Assert.Throws<InvalidOperationException>(()=>ui.Set(EnbUiGroup.Chat,new(.1,.1,1.5)));
        Assert.Throws<InvalidOperationException>(()=>ui.Rename("Default renamed"));
        ui.Create("Experiment",true);ui.Set(EnbUiGroup.Chat,new(.1,.2,1.5));ui.Cancel();
        Assert.True(ui.Locked);Assert.Empty(ui.Document.Profiles);Assert.Equal(EnbUiPlacement.Identity,ui.Placement(EnbUiGroup.Chat));
        Assert.False(File.Exists(Path.Combine(directory,"profiles.json")));
    }
    [Fact]
    public void SaveReloadDuplicateRenameAndBothScenesRetainIndependentValues()
    {
        var ui=Open();ui.SetContext(false,new(1920,1080));ui.Begin();ui.Create("Combat",false);
        var id=ui.Selected!.Id;ui.Set(EnbUiGroup.Chat,new(.1,.2,1.5));ui.SetContext(true,new(1920,1080));
        Assert.Equal(EnbUiPlacement.Identity,ui.Placement(EnbUiGroup.Chat));ui.Set(EnbUiGroup.Chat,new(.2,.3,2));Assert.True(ui.Save());
        var restored=Open();Assert.Equal(id,restored.Document.Selected);restored.SetContext(false,new(2560,1440));Assert.Equal(new(.1,.2,1.5),restored.Placement(EnbUiGroup.Chat));
        restored.Begin();restored.Create("Mining",true);restored.Rename("Travel");restored.Set(EnbUiGroup.Chat,new(.3,.1,1));
        Assert.NotEqual(id,restored.Selected!.Id);restored.Select(id);Assert.Equal(new(.1,.2,1.5),restored.Placement(EnbUiGroup.Chat));
        restored.SetContext(true,new(2560,1440));Assert.Equal(new(.2,.3,2),restored.Placement(EnbUiGroup.Chat));Assert.True(restored.Save());
        Assert.Equal(2,Open().Document.Profiles.Count);
    }
    [Fact]
    public void SelectingDefaultIsTransactionalAndNeverDestroysCustomProfiles()
    {
        var ui=Open();ui.Begin();ui.Create("Large",false);ui.Set(EnbUiGroup.Cockpit,new(-.2,-.5,1.5));Assert.True(ui.Save());
        var id=ui.Document.Selected;ui.Begin();ui.Select(Guid.Empty);ui.Cancel();Assert.Equal(id,ui.Document.Selected);
        ui.Begin();ui.Select(Guid.Empty);Assert.True(ui.Save());var restored=Open();Assert.True(restored.Locked);Assert.Single(restored.Document.Profiles);
        Assert.Equal(EnbUiPlacement.Identity,restored.Placement(EnbUiGroup.Cockpit));
    }
    [Fact]
    public void FailedSaveRetainsDraftAndDoesNotOverwriteExistingDestination()
    {
        Directory.CreateDirectory(directory);string destination=Path.Combine(directory,"is-a-directory");Directory.CreateDirectory(destination);
        var ui=new EnbUiLayoutProfiles(destination);ui.Begin();ui.Create("Retry",false);Assert.False(ui.Save());
        Assert.True(ui.Editing);Assert.Equal("Retry",ui.Selected!.Name);Assert.NotNull(ui.Error);Assert.True(Directory.Exists(destination));
        Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
    }
    [Fact]
    public void InvalidNamesAndTransformsCannotEnterSavedState()
    {
        var ui=Open();ui.Begin();Assert.Throws<ArgumentException>(()=>ui.Create("Default",false));
        ui.Create("Combat",false);Assert.Throws<ArgumentException>(()=>ui.Create("combat",true));
        Assert.Throws<ArgumentException>(()=>ui.Set(EnbUiGroup.Chat,new(double.NaN,0,1)));
        Assert.Throws<ArgumentException>(()=>ui.Set(EnbUiGroup.Chat,new(0,0,0)));
        Assert.Throws<ArgumentException>(()=>ui.Set(EnbUiGroup.None,EnbUiPlacement.Identity));
        Assert.Throws<ArgumentException>(()=>ui.Select(Guid.NewGuid()));
    }
    [Fact]
    public void ManyProfilesRemainIndependentWithoutAnArbitraryCountLimit()
    {
        var ui=Open();ui.Begin();for(int i=0;i<120;i++)ui.Create("Profile "+i,false);
        Assert.True(ui.Save());Assert.Equal(120,Open().Document.Profiles.Count);
    }
    [Theory]
    [InlineData(800,600,.2,.1,.5)]
    [InlineData(1920,1080,-.1,-.3,1.5)]
    [InlineData(2560,1440,.05,-.4,2)]
    public void DrawingAndInverseHitCoordinatesAgreeAtPanelEdges(double width,double height,double x,double y,double scale)
    {
        var size=new EnbUiSize(width,height);var placement=new EnbUiPlacement(x,y,scale);var rect=new EnbUiRect(200,150,240,180);
        var transformed=placement.Apply(rect,size);
        foreach(var point in new[]{new EnbUiPoint(201,151),rect.Center,new(439,329),new(199,149),new(441,331)})
        {
            var output=placement.Apply(point,size);var input=placement.Inverse(output,size);
            Assert.Equal(point.X,input.X,8);Assert.Equal(point.Y,input.Y,8);Assert.Equal(rect.Contains(point),transformed.Contains(output));
        }
        Assert.Equal(rect,EnbUiPlacement.Identity.Apply(rect,size));
    }
    [Fact]
    public void MalformedOrFutureDocumentsReportFailureInsteadOfCrashing()
    {
        Directory.CreateDirectory(directory);string path=Path.Combine(directory,"profiles.json");
        File.WriteAllText(path,"{bad json");var bad=Open();Assert.NotNull(bad.Error);Assert.True(bad.Locked);
        File.WriteAllText(path,"{\"Version\":2}");Assert.NotNull(Open().Error);
    }
    [Fact]
    public void SeparateClientsMergeIndependentProfilesAndRejectConflictingEdits()
    {
        var first=Open();var second=Open();first.Begin();second.Begin();first.Create("First",false);second.Create("Second",false);
        Assert.True(first.Save());Assert.True(second.Save());Assert.Equal(2,Open().Document.Profiles.Count);
        first=Open();second=Open();first.Begin();second.Begin();first.Rename("One rename");second.Rename("Other rename");
        Assert.True(first.Save());Assert.False(second.Save());Assert.True(second.Editing);Assert.Contains("changed in another client",second.Error);
        Assert.Equal("One rename",Open().Selected!.Name);second.Cancel();second.Begin();Assert.Equal("One rename",second.Selected!.Name);
    }
    [Fact]
    public void ExistingProfileGroupIdsRemainStableAndNewPanelsPersistSeparately()
    {
        Assert.Equal(4,(int)EnbUiGroup.Chat);Assert.Equal(10,(int)EnbUiGroup.Utility);
        var ui=Open();ui.SetContext(true,new(1280,720));ui.Begin();ui.Create("Panels",false);
        foreach(var group in new[]{EnbUiGroup.Cargo,EnbUiGroup.Character,EnbUiGroup.Vault,EnbUiGroup.Friends,EnbUiGroup.Guild,EnbUiGroup.GroupFinder,EnbUiGroup.Options,EnbUiGroup.Map})
            ui.Set(group,new((int)group*.01,.1,.8));
        Assert.True(ui.Save());var restored=Open();restored.SetContext(true,new(1280,720));
        Assert.Equal(new(.16,.1,.8),restored.Placement(EnbUiGroup.Map));
        Assert.Equal(new(.09,.1,.8),restored.Placement(EnbUiGroup.Character));
        restored.SetContext(false,new(1280,720));Assert.Equal(EnbUiPlacement.Identity,restored.Placement(EnbUiGroup.Map));
    }
    [Fact]
    public void DeleteIsTransactionalAndDefaultCannotBeDeleted()
    {
        var ui=Open();ui.Begin();Assert.Throws<InvalidOperationException>(ui.Delete);
        ui.Create("Keep",false);var keep=ui.Selected!.Id;ui.Create("Remove",true);var removed=ui.Selected!.Id;Assert.True(ui.Save());
        ui.Begin();ui.Delete();Assert.True(ui.Locked);Assert.Single(ui.Document.Profiles);ui.Cancel();Assert.Equal(removed,ui.Document.Selected);
        ui.Begin();ui.Delete();Assert.True(ui.Save());var restored=Open();Assert.True(restored.Locked);Assert.Equal(keep,Assert.Single(restored.Document.Profiles).Id);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConcurrentDeleteAndEditDoNotLoseChangesOrResurrectProfiles(bool deleteFirst)
    {
        var seed=Open();seed.Begin();seed.Create("Shared",false);Assert.True(seed.Save());
        var deleting=Open();var editing=Open();deleting.Begin();editing.Begin();deleting.Delete();editing.Rename("Changed");
        if(deleteFirst){Assert.True(deleting.Save());Assert.False(editing.Save());Assert.Empty(Open().Document.Profiles);}
        else{Assert.True(editing.Save());Assert.False(deleting.Save());Assert.Equal("Changed",Open().Selected!.Name);}
    }
    [Fact]
    public void PreviewVisibilityIsIndependentAndNeverPersists()
    {
        var ui=Open();ui.Begin();ui.Toggle(EnbUiGroup.Cargo);ui.Toggle(EnbUiGroup.Equipment);ui.Toggle(EnbUiGroup.Chat);
        Assert.True(ui.Preview(EnbUiGroup.Cargo));Assert.True(ui.Preview(EnbUiGroup.Equipment));Assert.False(ui.Visible(EnbUiGroup.Chat));
        ui.Toggle(EnbUiGroup.Cargo);Assert.False(ui.Preview(EnbUiGroup.Cargo));Assert.True(ui.Preview(EnbUiGroup.Equipment));
        ui.Create("Visibility",false);Assert.True(ui.Save());Assert.True(ui.Visible(EnbUiGroup.Chat));Assert.False(ui.Preview(EnbUiGroup.Equipment));
        ui.Begin();Assert.False(ui.Preview(EnbUiGroup.Equipment));Assert.True(ui.Visible(EnbUiGroup.Chat));ui.Cancel();
    }
    [Theory]
    [InlineData(1920,1080,1d)]
    [InlineData(2560,1440,1d)]
    [InlineData(2560,1440,1.5)]
    [InlineData(2560,1440,2d)]
    public void EditorButtonInkFitsAtEverySupportedScale(int width,int height,double scale)
    {
        var paths=EnbNext.Assets.EnbInstalledAssetLocator.TryLocate();if(paths is null)return;
        var assets=EnbNext.Assets.EnbRetailAssetBundle.Load(paths);
        using var panel=new EnbNext.Rendering.EnbQuitPanel(assets,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory));
        var box=new EnbUiRect(100,100,140,30);
        foreach(string text in new[]{"Group Finder","Duplicate","Rename","Delete","Default (locked)"})
        {
            var draw=panel.Control(text,"quit/BTTN02",box,text,new(width/scale,height/scale),scale);
            var image=Assert.Single(draw.Images);var pixels=image.Raster.BgraPixels.AsSpan();
            Assert.Equal(box,image.Clip);
            for(int y=0;y<image.Raster.Height;y++)for(int x=0;x<image.Raster.Width;x++)if(pixels[(y*image.Raster.Width+x)*4+3]!=0)
            {
                double px=image.Bounds.Left+(x+.5)*image.Bounds.Width/image.Raster.Width,py=image.Bounds.Top+(y+.5)*image.Bounds.Height/image.Raster.Height;
                Assert.True(px>=box.Left&&px<=box.Right&&py>=box.Top&&py<=box.Bottom,"Visible text must fit without relying on clipping.");
            }
        }
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
