using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Net7.Tests;

[Collection(LoadSensitiveCollection.Name)]
public sealed class EnbInformationPanelTests
{
    [Fact]
    public void PreparedPanelsAreSessionOwnedAndViewsRetireTheirOwnLabelsAndGadgets()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session=EnbRenderSession.Of(assets);
        var palette=new EnbFontEffectPalette();palette.Reload(assets.Paths.ClientIniDirectory);
        using var a=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        using var b=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        using var body=new EnbRetailTextEditor(1024,true){Text="draft"};
        using var entry=new EnbRetailTextEditor(250){Text="2"};
        using var player=new EnbRetailTextEditor(250);
        foreach(string prefix in new[]{"log","help","petition","player","complaint"})
        {
            var left=a.Load(prefix);var right=b.Load(prefix);
            Assert.Same(left,a.Load(prefix));Assert.Same(left.Resource,right.Resource);
            Assert.Same(session.Information.Get(prefix),left.Resource);
            var x=ImmutableList.CreateBuilder<EnbUiModelDraw>();var y=ImmutableList.CreateBuilder<EnbUiModelDraw>();int scroll=0;bool follow=false;
            left.Draw(x,new(800,600),1,default,10,0,2,null,null,1,null,body,entry,player,ref scroll,ref follow);
            right.Draw(y,new(800,600),1,default,10,0,2,null,null,1,null,body,entry,player,ref scroll,ref follow);
            Assert.Equal(x.Count,y.Count);Assert.Same(x[0].Asset,y[0].Asset);Assert.NotSame(x[0].GadgetOwner,y[0].GadgetOwner);
            var label=Enumerable.Range(0,left.Resource.Layout.Count).Select(left.Label).First(l=>l?.Raster is not null)!;
            var other=Enumerable.Range(0,right.Resource.Layout.Count).Select(right.Label).First(l=>l?.Raster is not null)!;
            Assert.NotSame(label,other);left.Dispose();Assert.Null(label.Raster);Assert.NotNull(other.Raster);
        }
        var second=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        Assert.NotSame(session.Information,EnbRenderSession.Of(second).Information);
        Assert.NotSame(session.Information.Get("log"),EnbRenderSession.Of(second).Information.Get("log"));
    }
    [Fact]
    public void EachPanelOwnsItsActorAndBrowserRetentionPreservesOnlyHelp()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session=EnbRenderSession.Of(assets);var palette=EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        using var body=new EnbRetailTextEditor(1024,true);using var entry=new EnbRetailTextEditor(250);using var player=new EnbRetailTextEditor(250);
        var draws=ImmutableList.CreateBuilder<EnbUiModelDraw>();bool follow=false;
        void Draw(int page)
        {draws.Clear();Assert.Null(ui.Draw(draws,new(800,600),1,page,0,1,null,1,null,ref follow,1000000));}
        var help=ui.Load("help");Draw(11);var helpActor=help.PortraitPlayback;Assert.NotNull(helpActor);
        Assert.Same(session.Information.HelpAvatar,draws.Last().Portrait);
        var complaint=ui.Load("complaint");Draw(12);Assert.NotNull(complaint.PortraitPlayback);Assert.NotSame(helpActor,complaint.PortraitPlayback);
        ui.EndPortraits(true);Assert.Same(helpActor,help.PortraitPlayback);Assert.Null(complaint.PortraitPlayback);
        Draw(11);Assert.Same(helpActor,help.PortraitPlayback);ui.EndPortraits();Assert.Null(help.PortraitPlayback);
        Draw(11);Assert.NotSame(helpActor,help.PortraitPlayback);ui.Dispose();Assert.Null(help.PortraitPlayback);
        var other=EnbRetailAssetBundle.Load(assets.Paths);
        Assert.NotSame(session.Information.HelpAvatar,EnbRenderSession.Of(other).Information.HelpAvatar);
    }

    [Fact]
    public async Task RetiringUiReleasesOwnedEditorsAndInvalidatesItsPendingTransaction()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);
        var session=EnbRenderSession.Of(assets);var palette=EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        var input=ui.BindLog(new());Assert.Same(ui.Body,input.Body);Assert.Same(ui.Entry,input.Entry);
        ui.Entry.Activate();Assert.Same(ui.Entry,ui.TextInput.Active);
        ui.Player.Activate();Assert.False(ui.Entry.IsFocused);Assert.Same(ui.Player,ui.TextInput.Active);
        using var dialog=new EnbConfirmationPanel(assets,palette);
        ui.Petition.Open(3);var petition=ui.BindPetition(dialog,new());
        Assert.Same(ui.Body,petition.Editor.Body);Assert.Same(ui.Entry,petition.Editor.Entry);Assert.Same(ui.Player,petition.Player);
        int completed=0;ui.Petition.Completed+=_=>completed++;
        var transport=new TaskCompletionSource<bool>();
        var pending=ui.Petition.SendAsync(1,0,"subject","body","player",()=>1,(_,_,_,_,_,_)=>transport.Task);
        Assert.True(ui.Petition.Sending);ui.Dispose();Assert.Null(ui.TextInput.Active);Assert.False(ui.Petition.Sending);
        using var replacement=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        Assert.NotSame(ui.Body,replacement.Body);Assert.NotSame(ui.Petition,replacement.Petition);
        replacement.Petition.Open(2);replacement.Body.Text="new draft";
        transport.SetResult(true);Assert.Equal(EnbPetitionSendResult.Ignored,await pending);
        Assert.Equal(0,completed);Assert.Equal(2u,replacement.Petition.Category);Assert.Equal("new draft",replacement.Body.Text);
    }

    [Fact]
    public void FormActionsRetainDraftOnNoAndApplyBackOnlyOnAcceptedCancellation()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var session=EnbRenderSession.Of(assets);
        var palette=EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        using var dialog=new EnbConfirmationPanel(assets,palette);var confirmation=new EnbNext.Client.EnbConfirmationState();
        ui.BeginPetition(3,dialog,confirmation);Assert.Equal("PETITION_PLAYER_ENTRY_NO_DATA",ui.ValidationPrompt(true));Assert.Equal("PETITION_ENTRY_NO_DATA",ui.ValidationPrompt(false));
        ui.Entry.Text="subject";ui.Body.Text="details";Assert.Equal("PETITION_PLAYER_ENTRY_NO_DATA",ui.ValidationPrompt(true));
        ui.Player.Text="name";Assert.Null(ui.ValidationPrompt(true));ui.FocusEditor(ui.Entry);Assert.True(ui.Editing);
        Assert.Null(ui.ShowPrompt("PETITION_ENTRY_CANCEL",true,confirmation));Assert.False(ui.Editing);
        Assert.False(ui.AnswerPrompt(false,confirmation).Canceled);Assert.Equal("details",ui.Body.Text);Assert.Equal(3u,ui.Petition.Category);
        Assert.Null(ui.ShowPrompt("PETITION_ENTRY_CANCEL",true,confirmation));var answer=ui.AnswerPrompt(true,confirmation);
        Assert.True(answer.Canceled);Assert.True(answer.Back);ui.FinishPetition(answer.Back,confirmation);
        Assert.Equal(0u,ui.Petition.Category);Assert.False(ui.Editing);Assert.Null(confirmation.Text);
        ui.BeginPetition(1,dialog,confirmation);Assert.Equal("",ui.Entry.Text);Assert.Equal("",ui.Body.Text);Assert.Equal("",ui.Player.Text);
        Assert.Equal(EnbRetailEditorCharacterClass.Printable,ui.Entry.CharacterClass);
        Assert.False(ui.ImportPlayer(null));Assert.Equal("",ui.Player.Text);
    }

    [Fact]
    public async Task CommandsPreserveSoundBeforeEditorMutationAndExcludeModalOrPendingActions()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var session=EnbRenderSession.Of(assets);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory));
        ui.BindLog(new());var sounds=new List<(string Text,bool Focus)>();
        ui.CommandSoundRequested+=_=>sounds.Add((ui.Body.Text,ui.Entry.IsFocused));
        Assert.Equal(EnbInformationCommandKind.None,ui.Activate("log/PREVIOUS",10,1).Kind);Assert.Empty(sounds);
        ui.Entry.Activate();Assert.Equal(EnbInformationCommandKind.None,ui.Activate("log/NEXT",10,2).Kind);
        Assert.True(sounds[0].Focus);Assert.False(ui.Entry.IsFocused);
        Assert.Equal(new EnbInformationCommand(EnbInformationCommandKind.ChangeLogPage,3),ui.Activate("log/NEXT",10,2));
        ui.Body.Text="prefix ";Assert.Equal(EnbInformationCommandKind.FollowBody,ui.Activate("log/DATE_STAMP",10,2,new DateTime(2026,11,2)).Kind);
        Assert.Equal("prefix ",sounds[^1].Text);Assert.Equal("prefix November 02, 2026: ",ui.Body.Text);
        Assert.Equal(new EnbInformationCommand(EnbInformationCommandKind.Browser,2),ui.Activate("help/BUG",11,2));
        Assert.Equal(EnbInformationCommandKind.Complaint,ui.Activate("help/PLAYER",11,2).Kind);
        Assert.Equal(new EnbInformationCommand(EnbInformationCommandKind.Petition,3),ui.Activate("complaint/BUTTON_01",12,2));
        ui.Petition.Open(1);int count=sounds.Count;Assert.Equal(EnbInformationCommandKind.None,ui.Activate("help/GAMEPLAY",11,2).Kind);Assert.Equal(count,sounds.Count);
        var confirmation=new EnbNext.Client.EnbConfirmationState();Assert.Null(ui.ShowPrompt("PETITION_ENTRY_CANCEL",true,confirmation));
        Assert.Equal(EnbInformationCommandKind.None,ui.Activate("petition/SUBMIT",11,2).Kind);Assert.Equal(count,sounds.Count);
        Assert.Equal(new EnbInformationCommand(EnbInformationCommandKind.AnswerPrompt,1),ui.Activate("control/dialog/yes",11,2));
        ui.AnswerPrompt(false,confirmation);var transport=new TaskCompletionSource<bool>();
        var pending=ui.Petition.SendAsync(1,0,"subject","body","",()=>1,(_,_,_,_,_,_)=>transport.Task);
        Assert.Equal(EnbInformationCommandKind.None,ui.Activate("petition/SUBMIT",11,2).Kind);Assert.Equal(count,sounds.Count);
        ui.RetireForm();transport.SetResult(true);Assert.Equal(EnbPetitionSendResult.Ignored,await pending);
    }

    [Fact]
    public void SharedCollectionPreservesQueuedInputAcrossPanelsAndRetiresAttachedModalGraph()
    {
        var assets=EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()!);var session=EnbRenderSession.Of(assets);
        var palette=EnbFontEffectPalette.Load(assets.Paths.ClientIniDirectory);
        using var ui=new EnbInformationUi(session.Information,session.OptionsPanels,palette);
        var graph=new EnbOptionsGadgetCollection();var utility=new EnbUiGadgetPointer(1,hasBackground:false);
        var observed=new List<int>();graph.Changed+=()=>observed.Add(graph.Input.Count);
        graph.Input.Push(new(65,0,0,0));Assert.True(graph.Synchronize(null,null,utility,null,null,true,.25f,.75f));
        Assert.Equal(new[]{1},observed);Assert.Equal(0,graph.Input.Count);Assert.Equal(.25f,graph.Input.X);Assert.Equal(.75f,graph.Input.Y);Assert.Equal(1,graph.Context.Count);
        graph.Input.Push(new(66,0,.5f,.5f));var log=ui.BindLog(new());var root=ui["log"].Pointer;
        Assert.True(graph.Synchronize(null,root,null,log,null,true,0,0));Assert.Equal(1,graph.Input.Count);Assert.Equal(4,graph.Context.Count);
        Assert.False(graph.Synchronize(null,root,null,log,null,true,0,0));Assert.Equal(2,observed.Count);
        using var dialog=new EnbConfirmationPanel(assets,palette);var confirmation=new EnbNext.Client.EnbConfirmationState();
        var petition=ui.BeginPetition(3,dialog,confirmation);root=ui.Load("complaint").Pointer;
        Assert.True(graph.Synchronize(null,root,null,null,petition,true,0,0));Assert.False(log.IsAttached);Assert.True(petition.IsAttached);
        Assert.Equal(7,graph.Context.Count);Assert.Equal(1,graph.Input.Count);
        Assert.Null(ui.ShowPrompt("PETITION_ENTRY_CANCEL",true,confirmation));
        Assert.False(graph.Synchronize(null,root,null,null,petition,true,0,0));Assert.False(ui.Body.Pointer.Enabled(0));Assert.Equal(10,graph.Context.Count);
        Assert.True(graph.Synchronize(null,null,null,null,null,false,0,0));Assert.Equal(0,graph.Context.Count);Assert.Equal(0,graph.Input.Count);
        Assert.False(graph.HasCapture);Assert.False(petition.IsAttached);Assert.Null(ui.Player.TabTarget);Assert.Equal(4,observed.Count);
    }

}
