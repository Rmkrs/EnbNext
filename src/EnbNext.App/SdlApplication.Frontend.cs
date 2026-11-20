using System.Diagnostics;
using EnbNext.Assets;
using EnbNext.Engine;
using EnbNext.Host.Sdl;
using EnbNext.Rendering;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private string? frontendScreenshotDirectory;
    private async Task InitializeFrontendAsync()
    {
        try
        {
            var paths=await LocateRetailAsync();
            if(paths is null){_frontendClosed=true;return;}
            await AskLauncherFolderAsync();
            if(_frontendClosed)return;
            window.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","Icon","EnbNext.png"));
            var assets=await Task.Run(()=>EnbRetailAssetBundle.Load(paths,EnbTextureOverridePack.ConfiguredDirectory));
            if(_frontendClosed)return;
            assets.SelectTexturePack(_preferences.TexturePack??(_preferences.TextureOverrides?null:""));
            CreateFrontend(assets);
            _frontend!.ShowSplash();
            if (_startupLogin is {} shortcut)
            {
                _startupLogin=null; _shortcutActive=true;
                try
                {
                    if(shortcut.Error is {} error) await FailStartupLoginAsync(error);
                    else await StartSavedShortcutAsync(shortcut);
                }
                finally { _shortcutActive=false; }
                return;
            }
            // Keep splash-before-intro ordering, but omit retail's idle Sleep(4000)
            // at 005272B0. Wait only for the artwork's first GPU frame.
            while (!_frontendClosed && !_frontend.SplashPresented)
            {
                if (_frontend.PresentationError is {} error) { AppendDiagnostic("Startup splash unavailable: " + error); break; }
                await Task.Delay(16);
            }
            if (_frontendClosed) return;
            if (_startupLogin is null)
            {
                // RunClient 52738E..5273CB: Bink playlist precedes scripted movies.
                if (_preferences.PlayStartupIntro)
                {
                    using var stop=new CancellationTokenSource();
                    string codec=Path.Combine(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,"binkw32.dll");
                    var entries=new EnbRetailVideoEntry[]{new(Path.Combine(assets.Paths.MixFilesDirectory,"eb_sizzle.bik"),Volume:StartupIntroVolume(assets,_preferences.SoundSettingsOwner))};
                    var runtime=new EnbRetailVideoRuntime(entries,entry=>OperatingSystem.IsWindows()?new RetailBinkDecoder(codec,entry,true,stop.Token,message=>Post(()=>AppendDiagnostic(message))):new FfmpegVideoDecoder(entry,true,stop.Token,message=>Post(()=>AppendDiagnostic(message))),
                        file=>assets.FileResources.TryRead("subtitle.txt",out var bytes,out _,out _)?EnbRetailVideoSubtitles.Parse(bytes,file):[],()=>unchecked((uint)Environment.TickCount64));
                    using var video=new SdlVideoScreen(window,runtime,()=>OperatingSystem.IsWindows()?new RetailVideoFont():new PortableVideoFont(),stop.Cancel){Diagnostic=AppendDiagnostic};
                    videoScreen=video;
                    try{await video.Completion;}finally{if(ReferenceEquals(videoScreen,video))videoScreen=null;}
                }
                if (_frontendClosed) return;
                // RunClient 527499..5274FF admits the contiguous moviescriptN.txt task.
                for (int number = 1; assets.FileResources.TryRead($"moviescript{number}.txt", out var bytes, out _, out _); number++)
                {
                    try
                    {
                        using var movie=new SdlMovieScreen(window,assets,EnbRetailMovieScript.Parse(bytes));
                        movie.Owner.Diagnostic=AppendDiagnostic;movie.Owner.CapturePrefix=MovieCapturePrefix;
                        movie.Owner.CaptureFrame=prefix=>CaptureMovieAsync(assets,prefix);
                        movieScreen=movie;
                        try{await movie.Owner.Completion;}finally{if(ReferenceEquals(movieScreen,movie))movieScreen=null;}
                    }
                    catch (InvalidDataException ex) { AppendDiagnostic($"moviescript{number}.txt: {ex.Message}"); }
                    if (_frontendClosed) return;
                }
            }
            if(!_frontendClosed) ShowLoginScreen();
        }
        catch(Exception ex)
        {
            AppendDiagnostic("Frontend startup: "+ex);
            if(hidden)throw;
            if(_startupLogin is not null || _shortcutActive)
            { await FailStartupLoginAsync("Unable to load the retail frontend.");return; }
            if(!_frontendClosed){window.Message("EnbNext","Unable to load the retail frontend.\n"+ex.Message);_frontendClosed=true;}
        }
    }

    private EnbFrontendController? EnsureFrontend()
    {
        if(_frontend is null&&!_frontendClosed)
        {
            var assets=_client?.RetailAssets??EnbRetailAssetBundle.Load(EnbInstalledAssetLocator.TryLocate()??throw new InvalidOperationException("Retail assets are unavailable."),EnbTextureOverridePack.ConfiguredDirectory);
            assets.SelectTexturePack(_preferences.TexturePack??(_preferences.TextureOverrides?null:""));
            CreateFrontend(assets);
        }
        return _frontend;
    }
    private void CreateFrontend(EnbRetailAssetBundle assets)
    {
        frontendScreenshotDirectory=Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!;
        cursor??=new(assets);
        frontendScreen=new(window,cursor,assets,_preferences.SoundSettingsOwner,_preferences.IntroductoryTutorialCompleted);
        var frontend=_frontend!;frontend.Diagnostic=AppendDiagnostic;frontend.StatusReported=AppendTranscript;
        frontend.Owner.SelectServer(_preferences.Server);
        frontend.Owner.ServerChanged+=SavePreferences;
        frontend.Owner.ServerStatus.Diagnostic=AppendDiagnostic;
        if(inspect is null)_=frontend.Owner.ServerStatus.RefreshAsync(frontend.Owner.SelectedServer);
        loginSelectionRestored=false;
        ConfigureSavedAccounts(frontend);ConfigureGroups(frontend);
        frontend.LoginRequested+=async ()=>{if(frontend.Owner.Groups.GroupMode){if(frontend.Owner.Groups.Selected is {} group)await StartGroupAsync(group);else await frontend.ShowMessageAsync("Choose or create a group first.");}else{LeaveGroup();await LoginAsync();}};
        frontend.EnterRequested+=async ()=>await EnterSelectedCharacterAsync(playEntrySound:true);
        frontend.CharacterSelected+=index=>_selectedCharacter=_client?.Characters.ElementAtOrDefault(index);
        frontend.CreationSlotRequested+=index=>
        {
            if(_busy || _client is not {} current || _frontend is not {} view)return;
            view.ShowCreation(index,current.CharacterCreationRoutes);
        };
        frontend.CreateRequested+=async request=>await CreateCharacterAsync(request);
        frontend.DeleteRequested+=async ()=>await DeleteSelectedCharacterAsync();
        frontend.PreviousRequested+=async ()=>{if(_busy)return;SetBusy(true);try{await DisposeClientAsync();ShowLoginScreen();}finally{SetBusy(false);}};
        frontend.QuitRequested+=()=>_frontendClosed=true;
        frontend.AccountRequested+=()=>
        {
            try { Process.Start(new ProcessStartInfo("https://www.net-7.org/"){UseShellExecute=true}); }
            catch(Exception ex){AppendDiagnostic("Account website: "+ex.Message);SetStatus("Unable to open the account website: "+ex.Message,true);}
        };

        if(_frontendSession is {} state)frontend.RestoreSession(state);
        if(_frontendUsername is {} username)frontend.Username.Text=username;
    }
    private void ReleaseFrontend()
    {
        if(_frontend is not {} frontend)return;
        SavePreferences();_frontendSession=frontend.CaptureSession();_frontendUsername=frontend.Username.Text;
        frontendScreen!.Dispose();frontendScreen=null;frontendScreenshotDirectory=null;surface.Deinitialize();
    }
    private void ShowLoginScreen(){_selectedCharacter=null;CloseWorldWindow();if(EnsureFrontend() is {} frontend)_=ShowLoginWithSavedAccountAsync(frontend);}
    private async Task FailStartupLoginAsync(string message)
    {
        if(activeGroup is not null){await DisposeClientAsync();await GroupFailureAsync(message);return;}
        if(_frontendClosed)return;_startupLogin=null;await DisposeClientAsync();if(_frontendClosed)return;
        EnsureFrontend();
        if(_frontend is {} frontend){frontend.ShowSplash();await frontend.ShowMessageAsync(message);}
        else window.Message("EnbNext",message);
        _frontendClosed=true;
    }
    internal static int StartupIntroVolume(EnbRetailAssetBundle assets,uint? owner)=>
        (int)Math.Round(0x8000*Math.Clamp(assets.ResolvePlayerOptions(owner).Options.SoundMusic/100d,0,1));
}
