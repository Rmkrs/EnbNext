using System.Diagnostics;
using EnbNext.Client;
using EnbNext.Presentation;
using SDL;
using EnbNext.Client.Models;
using EnbNext.Engine;
using EnbNext.Host.Sdl;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private readonly SavedGroupStore _groups=new(SavedGroupStore.DefaultPath);
    private GroupLaunchSession? groupLaunch;
    private GroupLaunchReporter? groupReporter;
    private SavedLoginGroup? activeGroup;
    private SavedGroupMember? activeGroupMember;
    private bool groupStarting,groupWasOnline;
    /// <summary>The launch was taken up in game (a client started solo chose the group and launched a member): its
    /// members this client did not start show as their clients are, online or offline.</summary>
    private bool adoptedLaunch;
    private EnbGroupLaunchPanel? launchPanel;
    private int launchPanelRevision=-1;
    private Action<int>? launchRetry;
    private long panelClaim;
    private bool panelOwned=true;
    /// <summary>The panel lists the running launch while this process still uses that launch's group.</summary>
    private bool LaunchRows=>groupLaunch is not null&&(!groupSwitched||switchedGroup?.Id==activeGroup?.Id);
    private void ClaimMultiboxPanel()=>panelClaim=DateTime.UtcNow.Ticks;
    /// <summary>A login from the Account tab is solo: the earlier group launch, its member, the group chosen in game
    /// and the group's character panel no longer apply. Clients still running keep running and show as peers.</summary>
    private void LeaveGroup()
    {
        groupLaunch?.Dispose();groupLaunch=null;activeGroup=null;activeGroupMember=null;switchedGroup=null;groupSwitched=false;
        groupWasOnline=false;launchPanelRevision=-1;launchRetry=null;invitedLaunch=null;invitedInstances.Clear();launchInput=Guid.Empty;adoptedLaunch=false;
        if(groupReporter is {} reporter){groupReporter=null;dispatch.Observe(reporter.DisposeAsync().AsTask());}
        if(launchPanel is {} panel){if(worldScreen is {} world&&ReferenceEquals(world.Owner.Hud.LaunchPanel,panel))world.Owner.Hud.LaunchPanel=null;panel.Dispose();launchPanel=null;}
    }
    private void ReadStartupGroupDisplay()
    {
        if(_startupLogin is not {Error:null,Group.Length:>0} shortcut)return;
        try
        {
            _groups.Load();
            var group=_groups.Groups.FirstOrDefault(g=>(g.Id.ToString()==shortcut.Group||SavedAccountStore.Same(g.Name,shortcut.Group))&&SavedAccountStore.Same(g.Server,shortcut.Server));
            var member=shortcut.Member==Guid.Empty?group?.Members.FirstOrDefault():group?.Members.FirstOrDefault(m=>m.Id==shortcut.Member);
            if(group is null||member is null)return;
            activeGroup=group;activeGroupMember=member;
            _characterDisplayPath=SavedGroupStore.DisplayPath(_preferencesPath,group.Id,member.Id);
            ReadCharacterDisplay();
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {AppendDiagnostic("Group display preferences could not be read: "+ex.Message);}
    }
    private void ConfigureGroups(EnbFrontendController frontend)
    {
        var panel=frontend.Owner.Groups;
        panel.RefreshRequested=async ()=>
        {
            if(panel.Busy)return;panel.Busy=true;
            try
            {
                await LoadAccountsAsync();_groups.Load();panel.Groups=_groups.Groups;frontend.Owner.Accounts.Accounts=_accounts.Accounts;
                var eligible=new HashSet<Guid>();
                foreach(var account in _accounts.Accounts)
                    if(!string.IsNullOrEmpty(await _accountSecrets.ReadAsync(AccountSecretStore.GameKey(account.Server,account.Login))))eligible.Add(account.Id);
                panel.Eligible=eligible;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){panel.Status=ex.Message;if(!panel.Editing)await frontend.ShowMessageAsync(ex.Message);}
            finally{panel.Busy=false;}
        };
        panel.DeleteRequested=async group=>
        {
            if(panel.Busy)return;panel.Busy=true;
            try
            {
                if(!await frontend.ConfirmAsync("Delete group '"+group.Name+"'? Saved accounts and passwords will be kept."))return;
                _groups.Load();_groups.Delete(group.Id);panel.Groups=_groups.Groups;panel.Deleted(group.Id);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){await frontend.ShowMessageAsync(ex.Message);}
            finally{panel.Busy=false;}
        };
        panel.SaveRequested=async draft=>
        {
            if(panel.Busy)return;panel.Busy=true;
            try
            {
                _groups.Load();var existing=_groups.Find(draft.Server,draft.Name);
                if(existing is not null)
                {
                    if(!await frontend.ConfirmAsync("Overwrite group '"+existing.Name+"'?"))return;
                    draft=draft with {Id=existing.Id,Members=draft.Members.Select(m=>m with {Id=existing.Members.FirstOrDefault(old=>old.AccountId==m.AccountId)?.Id??m.Id}).ToArray()};
                }
                await ValidateGroupAsync(draft);_groups.Save(draft);panel.Groups=_groups.Groups;panel.Saved(draft);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){panel.Status=ex.Message;if(!panel.Editing)await frontend.ShowMessageAsync(ex.Message);}
            finally{panel.Busy=false;}
        };
    }
    private async Task ValidateGroupAsync(SavedLoginGroup group)
    {
        if(LoginGroupRules.Validate(group) is {} error)throw new IOException(error);
        await LoadAccountsAsync();_accounts.Load();
        foreach(var member in group.Members)
        {
            var account=_accounts.Accounts.FirstOrDefault(a=>a.Id==member.AccountId&&SavedAccountStore.Same(a.Server,group.Server));
            if(account is null)throw new IOException("A group account was forgotten. Edit the group before launching.");
            if(!account.Characters.Any(c=>SavedAccountStore.Same(c.Name,member.Character)))throw new IOException(member.Character+" is no longer in the saved roster. Import accounts or edit the group.");
            if(string.IsNullOrEmpty(await _accountSecrets.ReadAsync(AccountSecretStore.GameKey(account.Server,account.Login))))throw new IOException("Save the game password for "+account.Login+" before launching this group.");
        }
    }
    private async Task StartGroupAsync(SavedLoginGroup group)
    {
        if(groupStarting||_busy)return;
        if(groupLaunch?.Members.Any(m=>m.State is "Online" or "Starting")==true){if(_frontend is {} existing)await existing.ShowMessageAsync("This client already manages a running group.");return;}
        groupStarting=true;launchInput=Guid.NewGuid();invitedLaunch=null;invitedInstances.Clear();adoptedLaunch=false;
        try
        {
            await ValidateGroupAsync(group);if(_frontendClosed)return;groupLaunch?.Dispose();groupLaunch=new(group);activeGroup=group;
            launchPanel?.Dispose();launchPanel=new(_frontend!.Assets){GroupName=group.Name};
            launchPanel.LaunchRequested=launchRetry=index=>{if(groupStarting)return;groupStarting=true;dispatch.Observe(RetryAsync(index));};
            launchPanelRevision=-1;
            // The whole group starts together: every other member's client is started first, then this one logs in.
            for(int index=1;index<group.Members.Length&&!_frontendClosed;index++)await LaunchGroupMemberAsync(index,validated:true);
            await LaunchGroupMemberAsync(0,validated:true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){if(_frontend is {} frontend)await frontend.ShowMessageAsync(ex.Message);}
        finally{groupStarting=false;}
    }
    /// <summary>
    /// Launch from the character panel of a group chosen in game by a client started solo: this client takes the group
    /// up as its launch, as if the group had been started from the login screen with this character, and starts the
    /// member as a group client. This client is the launch's own member (first row); members already running stay as they are.
    /// </summary>
    private async Task AdoptGroupAsync(SavedLoginGroup group,string self,string character)
    {
        if(groupStarting||_busy||worldScreen is null)return;
        var own=group.Members.FirstOrDefault(m=>SavedAccountStore.Same(m.Character,self));
        var target=group.Members.FirstOrDefault(m=>SavedAccountStore.Same(m.Character,character));
        if(own is null){worldScreen?.Owner.Hud.QueueUtilitySound("SR_scifielectronic42.wav");AppendTranscript("Group launch: "+self+" is not in "+group.Name+", so this client cannot launch its members.");return;}
        if(target is null||target.Id==own.Id)return;
        groupStarting=true;
        try
        {
            await ValidateGroupAsync(group);if(_frontendClosed)return;
            groupLaunch?.Dispose();
            groupLaunch=new(group with {Members=[own,..group.Members.Where(m=>m.Id!=own.Id)]});
            activeGroup=group;activeGroupMember=own;switchedGroup=null;groupSwitched=false;adoptedLaunch=true;
            launchInput=Guid.NewGuid();invitedLaunch=group.Id;invitedInstances.Clear();
            groupLaunch.Report(0,"Online");groupWasOnline=true;SyncAdoptedMembers();
            launchRetry=index=>{if(groupStarting)return;groupStarting=true;dispatch.Observe(RetryAsync(index));};
            launchPanelRevision=-1;
            await LaunchGroupMemberAsync(Array.FindIndex(groupLaunch.Members,m=>m.Definition.Id==target.Id),validated:true);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){AppendTranscript("Group launch: "+ex.Message);}
        finally{groupStarting=false;}
    }
    /// <summary>A taken-up launch's members this client never started: online while their client is in the game.</summary>
    private void SyncAdoptedMembers()
    {
        if(!adoptedLaunch||groupLaunch is not {} launch)return;
        for(int i=1;i<launch.Members.Length;i++)
        {
            var member=launch.Members[i];if(member.Attempt!=0)continue;
            bool running=multibox?.Peers.Any(p=>SavedAccountStore.Same(p.Name,member.Definition.Character))==true;
            launch.Report(i,running?"Online":"Offline");
        }
    }
    private async Task RetryAsync(int index)
    {try{await LaunchGroupMemberAsync(index);if(groupLaunch?.Members[index].State is "Starting" or "Online")groupLaunch.Continue();}finally{groupStarting=false;}}
    private async Task LaunchGroupMemberAsync(int index,bool validated=false)
    {
        var launch=groupLaunch;if(launch is null)return;
        if(!launch.Begin(index)){launch.Report(index,launch.Members[index].State,"Close this offline client before launching it again.");return;}
        try
        {
            if(!validated)await ValidateGroupAsync(launch.Group);if(_frontendClosed)return;
            var member=launch.Members[index].Definition;var account=_accounts.Accounts.Single(a=>a.Id==member.AccountId);
            if(index==0)
            {
                activeGroupMember=member;_shortcutActive=true;
                try{await LoginAsync(StartupLogin.ForMember(account.Login,member.Character,launch.Group.Server));}
                finally{_shortcutActive=false;}
            }
            else
            {
                string pipe=launch.Listen(index);
                var info=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("Client executable path unavailable.")){UseShellExecute=false};
                if(string.Equals(Path.GetFileNameWithoutExtension(info.FileName),"dotnet",StringComparison.OrdinalIgnoreCase))info.ArgumentList.Add(typeof(SdlApplication).Assembly.Location);
                foreach(var argument in new[]{"--group",launch.Group.Id.ToString(),"--member",member.Id.ToString(),"--server",launch.Group.Server,"--launch-pipe",pipe})info.ArgumentList.Add(argument);
                // A group started from a logging session logs too.
                if(DiagnosticFiles.Enabled)info.ArgumentList.Add(DiagnosticFiles.Option);
                launch.Attach(index,Process.Start(info)??throw new IOException("The client process could not be started."));
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){launch.Report(index,"Failed",ex.Message);}
    }
    private async Task StartSavedShortcutAsync(StartupLogin shortcut)
    {
        if(shortcut.Error is {} error){await FailStartupLoginAsync(error);return;}
        _frontend!.Owner.SelectServer(shortcut.Server);
        if(!SavedAccountStore.Same(_frontend.Owner.SelectedServer,shortcut.Server)){await FailStartupLoginAsync("Unknown login server.");return;}
        if(shortcut.Group.Length==0){await LoginAsync(shortcut);return;}
        try
        {
            _groups.Load();var group=_groups.Groups.FirstOrDefault(g=>g.Id.ToString()==shortcut.Group||SavedAccountStore.Same(g.Name,shortcut.Group)&&SavedAccountStore.Same(g.Server,shortcut.Server))??throw new IOException("Saved group not found.");
            if(shortcut.Member==Guid.Empty){await StartGroupAsync(group);return;}
            _frontend.Owner.SelectServer(group.Server);
            activeGroup=group;activeGroupMember=group.Members.SingleOrDefault(m=>m.Id==shortcut.Member)??throw new IOException("Saved group member not found.");
            groupReporter=new(shortcut.Pipe,shortcut.Member);await groupReporter.ConnectAsync();
            await ValidateGroupAsync(group);var account=_accounts.Accounts.Single(a=>a.Id==activeGroupMember.AccountId);
            await LoginAsync(StartupLogin.ForMember(account.Login,activeGroupMember.Character,group.Server));
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or TimeoutException){await FailStartupLoginAsync(ex.Message);}
    }
    private async Task GroupFailureAsync(string message)
    {
        groupLaunch?.Report(0,"Failed",message);if(groupReporter is {} reporter)await reporter.SendAsync("Failed",message);
        groupWasOnline=false;
        if(groupReporter is not null){_frontendClosed=true;return;}
        ShowLoginScreen();
    }
    private async Task GroupOnlineAsync()
    {
        groupWasOnline=true;groupLaunch?.Report(0,"Online");if(groupReporter is {} reporter)await reporter.SendAsync("Online");
    }
    private void TickGroupLaunch()
    {
        if(groupWasOnline && (_client is null || _client.State is ClientLifecycleState.Disconnected or ClientLifecycleState.Faulted or ClientLifecycleState.CharacterSelection))
        {groupWasOnline=false;groupLaunch?.Report(0,"Offline","Client left the game.");if(groupReporter is {} reporter)dispatch.Observe(reporter.SendAsync("Offline"));}
        if(groupLaunch is not {} launch)return;
        launch.Tick();
        if(launchPanel is {} panel)
        {
            if(LaunchRows&&launchPanelRevision!=launch.Revision){launchPanelRevision=launch.Revision;panel.Members=launch.Members.Select(m=>(m.Definition.Character,m.State,m.Error)).ToArray();panel.Paused=launch.Paused;panel.Stopped=launch.Stopped;panel.Invalidate();}
            if(worldScreen is {} world){panel.Layout=world.Owner.Ui.Layout;world.Owner.Hud.LaunchPanel=groupWasOnline&&panelOwned&&multiboxOptions.ShowCharacterPanel?panel:null;}
        }
        int next=launch.Next;if(next>=0&&!groupStarting){groupStarting=true;dispatch.Observe(NextAsync(next));}
        async Task NextAsync(int index){try{await LaunchGroupMemberAsync(index);}finally{groupStarting=false;}}
    }
    private bool HandleGroupPanel(SDL_Event e)
    {
        if(launchPanel is not {} panel || worldScreen is not {} world || !ReferenceEquals(world.Owner.Hud.LaunchPanel,panel))return false;
        if(world.Owner.Ui.Layout.Editing)return false;
        var type=(SDL_EventType)e.type;
        if(type is SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST or SDL_EventType.SDL_EVENT_WINDOW_MINIMIZED){panel.EndDrag();world.Capture.Release(SdlPointerOwner.GroupPanel);return false;}
        if(!panel.Dragging && PointerUnderGameUi(world,e)){if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION)panel.Pointer(new(-100000,-100000));return false;}
        if(type==SDL_EventType.SDL_EVENT_MOUSE_MOTION){var point=world.Owner.Ui.Layout.Inverse(EnbUiGroup.Multibox,new(e.motion.x,e.motion.y));panel.Pointer(point);return panel.Dragging||panel.Contains(point);}
        if(type is SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP)
        {
            var point=world.Owner.Ui.Layout.Inverse(EnbUiGroup.Multibox,new(e.button.x,e.button.y));
            if(e.button.button==3 && type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN)return panel.RightClick(point);
            if(e.button.button==1 && type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP){bool held=panel.Dragging;panel.EndDrag();world.Capture.Release(SdlPointerOwner.GroupPanel);return held||panel.Contains(point);}
            if(type==SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN&&e.button.button==1){bool handled=panel.Click(point);if(panel.Dragging)world.Capture.Acquire(SdlPointerOwner.GroupPanel);return handled;}
            return panel.Contains(point);
        }
        if(type==SDL_EventType.SDL_EVENT_MOUSE_WHEEL)return panel.Contains(world.Owner.Ui.Layout.Inverse(EnbUiGroup.Multibox,new(e.wheel.mouse_x,e.wheel.mouse_y)));
        return false;
    }

}
