using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Client.Models;
using EnbNext.Engine;
using EnbNext.Net7.Gameplay;

namespace EnbNext.App;

internal sealed partial class SdlApplication
{
    private async Task LoginAsync(StartupLogin? shortcut = null)
    {
        if (_busy) return;
        if (_frontend?.Owner.SelectedServer is not string host)
            return;

        SaveLoginSelection();
        string username = shortcut?.Username ?? _frontend?.Username.Text?.Trim() ?? string.Empty;
        string password;
        try { password=shortcut is null ? _frontend?.Password.Text??string.Empty : await _accountSecrets.ReadAsync(AccountSecretStore.GameKey(host,username))??string.Empty; }
        catch(IOException ex){await FailStartupLoginAsync(ex.Message);return;}
        if(shortcut is not null && password.Length==0){await FailStartupLoginAsync("No saved game password for this account. Log in with Remember login first.");return;}
        if (username.Length == 0 || password.Length == 0)
        {
            AppendTranscript("Login rejected locally: missing username or password.");
            if (_frontend is {} frontend)
            {
                frontend.SetStatus(""); frontend.Speak("RejectedOnce");
                SetBusy(true);
                try { await frontend.ShowMessageAsync("Please enter your login name and password."); }
                finally { SetBusy(false); }
            }
            else SetStatus("Enter a username and password.", isError: true);
            return;
        }

        SetBusy(true);
        SetStatus($"Connecting to {host}...", isError: false);
        try
        {
            if (!await EnsureLauncherAsync()) { if(shortcut is not null)await FailStartupLoginAsync("Choose a Net-7 installation to log in.");else SetStatus("Choose a Net-7 installation to log in.", true); return; }
            if(_client is not null)await DisposeClientAsync();
            _client = CreateClient(host);

            IReadOnlyList<CharacterSummary> characters = await _client.LoginAsync(username, password);
            if (_frontendClosed) return;
            if (shortcut is not null)
            {
                _selectedCharacter = shortcut.Resolve(characters);
                if (_selectedCharacter is null)
                { await FailStartupLoginAsync("Character not found on this account."); return; }
            }
            // Authentication admits the authored outro; local persistence need not delay it.
            Task? outro=_frontend is {IsLoginScreen:true} view && (shortcut is null||groupLaunch is not null)?view.LoginAcceptedAsync():null;
            await RememberGameLoginAsync(host,username,password,characters,shortcut is not null);
            if(outro is not null)await outro;
            if (_frontendClosed) return;
            if (shortcut is not null)
            {
                SetBusy(false);
                await EnterSelectedCharacterAsync();
                return;
            }
            await ShowCharacterScreen(characters);
            SetStatus($"Roster loaded from {host}.", isError: false);
        }
        catch (Exception ex)
        {
            await ShowLoginFailureAsync(ex, shortcut);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private EnbClient CreateClient(string host)
    {
        _diagnostics.Clear();
        _runtimeIssues.Clear();
        var client = new EnbClient(new EnbClientOptions { TextureOverrideDirectory = EnbTextureOverridePack.ConfiguredDirectory, TextureOverridesEnabled=_preferences.TextureOverrides, TexturePack=_preferences.TexturePack, ServerHost = host, SectorEntryPostInWorldCaptureDuration = BenchmarkCaptureDuration,
            WireCaptureDirectory = DiagnosticFiles.Enabled ? Path.Combine(Environment.CurrentDirectory, "captures") : null });
        client.ConfigureQualityOfLife(_preferences.QualityOfLife??new());
        client.Diagnostic += AppendDiagnostic;
        client.NewMissionStarted += OnNewMissionStarted;
        client.CharacterSettingsPreparing = (character, session) => PrepareCharacterSettings(client, character, session);
        client.LogoffCompleted += destination => Post(async () => await CompleteLogoffAsync(client, destination));
        return client;
    }

    private async Task ShowLoginFailureAsync(Exception ex, StartupLogin? shortcut)
    {
        var failure = LoginFailureFeedback.Report(ex, AppendTranscript, AppendDiagnostic);
        if (_frontendClosed) return;
        if (shortcut is not null)
        {
            await FailStartupLoginAsync(failure.Message);
            return;
        }
        if (_frontend is {} frontend)
        {
            frontend.SetStatus("");
            frontend.Speak(failure.Cue);
            await frontend.ShowMessageAsync(failure.Message);
        }
    }

    private async Task ShowCharacterScreen(IReadOnlyList<CharacterSummary> characters)
    {
        EnsureFrontend();
        if (_frontend is not {} frontend)return;
        await RefreshSavedRosterAsync(characters);
        _selectedCharacter=null;CloseWorldWindow();await frontend.ShowCharactersAsync(characters);
    }

    private async Task EnterSelectedCharacterAsync(bool playEntrySound = false)
    {
        if (_busy || _client is null || _selectedCharacter is null)
            return;

        if (_selectedCharacter.IsEmpty)
        {
            SetStatus("Character creation is not available yet.", isError: true);
            return;
        }

        CharacterSummary character = _selectedCharacter;
        // 00769400: no usable route for the slot's shard shows this notice (00B9BC58)
        // through 00588A70 and leaves the selection screen without a request.
        if (!_client.IsCharacterGalaxyAvailable(character))
        {
            if (_frontend is {} notice)
                await notice.ShowMessageAsync("The galaxy this character resides on is currently unavailable.  Please try again later.");
            return;
        }
        SetBusy(true);
        SetStatus($"Entering the game with {character.FullName}...", isError: false);
        LiveWorldOpenAlAudio? entryAudio = null;
        try
        {
            if (playEntrySound && _frontend is not null)
                entryAudio = await _frontend.TakeEntryAudioAsync();
            if (_frontendClosed) { entryAudio?.Dispose(); return; }
            // Character selection is an internal account transaction. The player-facing
            // action is one operation: choose a character, then Enter Game.
            ShowWorldWindowWithAudio(entryAudio, character);
            entryAudio = null; // The world now owns the playing source and its device.
            if(worldScreen is {} loading)
            {
                await loading.Host.WaitForPresentationAsync();
                if(_frontendClosed)return;
                ActivateCharacterDisplay(character);
                window.RefreshSize();cursor?.SetScale(window.Scale);loading.Resize();
                await loading.Host.WaitForPresentationAsync();
                if(_frontendClosed)return;
            }
            await _client.SelectCharacterAsync(character);
            // Before login there is no character identity. Remember the last selection
            // so the next intro and frontend use that character's saved sound settings.
            _preferences = _preferences with { SoundSettingsOwner = _client.SelectedFixedSessionId };
            SavePreferences();
            SectorRouteResult route = await _client.EnterGameAsync(character);
            if(activeGroup is not null)await GroupOnlineAsync();
            // Loading and gameplay remain in the same screen and native window.
            SetStatus($"Connected to {route.GameplaySectorEndPoint}.", isError: false, success: true);
        }
        catch (Exception ex)
        {
            entryAudio?.Dispose();
            AppendDiagnostic($"ENTER GAME ERROR: {ex}");
            CloseWorldWindow();
            if (_shortcutActive)
            { await FailStartupLoginAsync("Unable to enter the game."); return; }
            string message = ex.Message;
            if (_client?.LastWireCapturePath is string capturePath)
            {
                AppendDiagnostic($"WIRE CAPTURE: {capturePath}");
                message = $"{message}  Capture: {capturePath}";
            }

            if (_client?.State != ClientLifecycleState.CharacterSelection)
            {
                // Recoverable intermediate-login failure: the client
                // session has been retired and is safe to restart from credentials.
                await DisposeClientAsync();
                ShowLoginScreen();
                SetStatus($"{message}  Returned to login.", isError: true);
            }
              else
            {
                await ShowCharacterScreen(_client.Characters);
                SetStatus(message, isError: true);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CompleteLogoffAsync(EnbClient source, Net7LogoffDestination destination)
    {
        // The BA notification can be queued while another session is opening.
        // Never navigate or dispose on behalf of a retired connection.
        if (!ReferenceEquals(_client, source) || source.State != ClientLifecycleState.CharacterSelection)
            return;
        switch (destination)
        {
            case Net7LogoffDestination.CharacterSelection:
                SetBusy(true);
                try
                {
                    // Retire the world before authentication states can show sector-loading art.
                    CloseWorldWindow();
                    EnsureFrontend()?.ShowCharacterReturn();
                    var characters = await source.RefreshCharacterSelectionAsync();
                    if (!ReferenceEquals(_client, source) || _frontendClosed) return;
                    await ShowCharacterScreen(characters);
                    SetStatus("Choose a character.", isError: false);
                    window.Raise();
                }
                catch (Exception ex)
                {
                    if (!ReferenceEquals(_client, source) || _frontendClosed) return;
                    await DisposeClientAsync();
                    ShowLoginScreen();
                    await ShowLoginFailureAsync(ex, null);
                }
                finally { SetBusy(false); }
                break;
            case Net7LogoffDestination.LoginScreen:
                await DisposeClientAsync();
                // Disposal yields; a new connection must retain its own screen.
                if (_client is not null) return;
                ShowLoginScreen();
                SetBusy(false);
                SetStatus("Logged out.", isError: false);
                window.Raise();
                break;
            case Net7LogoffDestination.Desktop:
                await DisposeClientAsync();
                if (_client is not null) return;
                _frontendClosed=true;
                break;
        }
    }

    private async Task DeleteSelectedCharacterAsync()
    {
        if(_busy || _frontend is not {} frontend || _client is not {} client || _selectedCharacter is not {IsEmpty:false} character)return;
        SetBusy(true);
        try
        {
            bool accepted=await frontend.ConfirmAsync("Confirm deletion of the character?");
            if(_frontendClosed || !ReferenceEquals(_client,client))return;
            frontend.ResolveCharacterDeleteGesture(accepted);
            if(!accepted)return;
            if(_frontendClosed || !ReferenceEquals(_client,client))return;
            // Retail 0055D9C0 starts the delete transaction immediately, while
            // 0055CEA0 retains the actor until its 3.2-second timer expires.
            var presentation=Task.Delay(TimeSpan.FromSeconds(3.2));
            var roster=await client.DeleteCharacterAsync(character);
            await presentation;
            if(!_frontendClosed && ReferenceEquals(_client,client))await ShowCharacterScreen(roster);
        }
        catch(EnbNext.Net7.Protocol.Net7GlobalErrorException ex)
        {
            frontend.CancelCharacterDeletePresentation();
            AppendDiagnostic("Character deletion rejected: "+ex);
            if(!_frontendClosed && ReferenceEquals(_client,client))
            {
                // Retail: the selection mode controller does not consume a
                // GlobalError (vtable slot 0x78 -> 00515750 returns false), so
                // LoginTask 0x0076B000 shows it and, for a non-special code,
                // exits the login task after the dialog (00767F50 0x0F -> 0x11).
                SetStatus("",false);
                await frontend.ShowMessageAsync(ex.Error.Message);
                if(!ex.Error.IsLoginTaskRecoverable && !_frontendClosed && ReferenceEquals(_client,client))
                {
                    await DisposeClientAsync();
                    if(!_frontendClosed && _client is null)ShowLoginScreen();
                }
            }
        }
        catch(Exception ex){frontend.CancelCharacterDeletePresentation();AppendDiagnostic("Character deletion: "+ex);SetStatus(ex.Message,true);}
        finally{SetBusy(false);}
    }
    private async Task CreateCharacterAsync(EnbNext.Net7.Protocol.Net7AvatarCreateRequest request)
    {
        if(_busy || _frontend is not {} frontend || _client is not {} client)return;
        SetBusy(true);
        try
        {
            var roster=await client.CreateCharacterAsync(request);
            if(_frontendClosed || !ReferenceEquals(_client,client))return;
            // Retail 00784EA0 completes AllGood first; 00616A10 starts the
            // walk-off/FE02A-2, and its animation completion advances.
            var farewell=frontend.SpeakCreationCompleteAsync();
            await farewell;
            if(_frontendClosed || !ReferenceEquals(_client,client))return;
            CompleteIntroductoryTutorial();
            await ShowCharacterScreen(roster);
            var created=roster.FirstOrDefault(c=>c.Slot==request.TargetSlot && !c.IsEmpty);
            if(created is not null)
            {
                _selectedCharacter=created;
                SetBusy(false);
                await EnterSelectedCharacterAsync();
            }
        }
        catch(EnbNext.Net7.Protocol.Net7GlobalErrorException ex)
        {
            AppendDiagnostic("Character creation rejected: "+ex);
            if(!_frontendClosed && ReferenceEquals(_client,client))
            {
                await frontend.ShowCreationNameErrorAsync(ex.Error);
                // Retail LoginTask 0x0076B000 sends non-special codes through
                // its error-dialog wait state, then exits the login task. The
                // creation controller's code 8..13/17 path remains retryable.
                if(!ex.Error.IsLoginTaskRecoverable && !_frontendClosed && ReferenceEquals(_client,client))
                {
                    await DisposeClientAsync();
                    if(!_frontendClosed && _client is null)ShowLoginScreen();
                }
            }
        }
        catch(TimeoutException ex)
        {
            AppendDiagnostic("Character creation timed out: "+ex);
            if(!_frontendClosed && ReferenceEquals(_client,client))
            {
                SetStatus("",false);
                await frontend.ShowMessageAsync("The server did not confirm character creation. Return to login to refresh your character list before trying again.");
            }
        }
        catch(Exception ex)
        {
            AppendDiagnostic("Character creation: "+ex);
            if(!_frontendClosed && ReferenceEquals(_client,client))
            {
                SetStatus("",false);
                await frontend.ShowMessageAsync("Character creation could not be completed. Reconnect and check your character list before trying again.");
            }
        }
        finally{SetBusy(false);}
    }
    private void CompleteIntroductoryTutorial()
    {
        // 616A10 mode 9 -> 6AD3E0 records completion after the creation outro.
        // Keep Next's marker in its own profile rather than modifying retail.
        _preferences=_preferences with {IntroductoryTutorialCompleted=true};
        _frontend?.MarkIntroductoryTutorialCompleted();
        SavePreferences();
    }
}
