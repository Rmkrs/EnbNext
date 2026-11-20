using EnbNext.Presentation;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Client.Models;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController : IDisposable
{
    internal Func<long> TickCount = static ()=>Environment.TickCount64;
    private readonly EnbRetailAssetBundle _assets;
    internal EnbRetailAssetBundle Assets=>_assets;
    internal readonly EnbWorldRenderer _gpu;
    internal readonly EnbWorldRenderer _creationTooltipGpu;
    internal readonly EnbFrontendVisual _creationFadeCover = new();
    internal readonly EnbFrontendGpu Composition = new();
    public readonly EnbFrontendUi Owner;
    private readonly Dictionary<string, EnbUiRect> _hits = [];
    private readonly Dictionary<string, EnbUiRect> _actorHits = [];
    private EnbInterfaceLayoutCatalog _layout;
    private string _screen = "login", _hover = "";
    private long _hoverBeganTick;
    private string _splashModel = "install00a";
    private long _started = Environment.TickCount64;
    private IReadOnlyList<CharacterSummary> _characters = [];
    private int _selected = -1;
    private bool _busy;
    private EnbUiPoint _pointer;
    internal readonly EnbFrontendVisual _caret = new();
    private readonly Dictionary<int,EnbAvatarPortraitAsset> _avatars=[];
    private readonly Dictionary<int,LiveAvatarSpeechPlayback> _characterSpeech=[];
    private EnbNext.Presentation.EnbSceneAvatarSnapshot? _narratorAppearance;
    private readonly EnbRetailIniDocument _tutorial;
    private readonly Queue<(string Resource, string Text,bool SkipBlend)> _speech = new();
    private long _speechIdentity;
    private LiveAvatarSpeechPlayback? _narratorSpeech;
    private bool _speechActive;
    private bool _speechAudioOnly;
    private bool _loginExit;
    private TaskCompletionSource? _loginTransition;
    private EnbW3dRenderHierarchyAnimation? _loginAnimationClip;
    private EnbRetailFrontendAnimationClock? _loginAnimationClock;
    private bool _loginEntering;
    private long _loginAnimationTick;
    public EnbUiTextInput Username=>Owner.Username;
    public EnbUiTextInput Password=>Owner.Password;
    public event Action? LoginRequested;
    public event Action? EnterRequested;
    public event Action? PreviousRequested;
    public event Action? DeleteRequested;
    public event Action<int>? CharacterSelected;
    public event Action<int>? CreationSlotRequested;
    public event Action? QuitRequested;
    public event Action? AccountRequested;
    public Action<string>? Diagnostic { get; set; }
    public ImmutableArray<EnbFrontendDraw> Draws { get; private set; } = [];
    public string? Error { get; private set; }
    internal bool IsLoginScreen => _screen == "login";
    internal bool SplashPresented => _gpu.FrontendLastRenderedScreen == "splash";
    internal string? PresentationError => Error ?? (_gpu.StatusText.Contains("render failure", StringComparison.OrdinalIgnoreCase) ? _gpu.StatusText : null);
    internal Task<LiveWorldOpenAlAudio?> TakeEntryAudioAsync() =>
        PresentationError is null ? _gpu.TakeFrontendEntryAudioAsync() : Task.FromResult<LiveWorldOpenAlAudio?>(null);
    public EnbUiRect Bounds {get;set;}
    public double RenderScaling {get=>Owner.Text.RenderScaling;set=>Owner.Text.RenderScaling=value;}
    private bool enabled=true;
    public Action<bool>? EnableChanged;
    public bool IsEnabled {get=>enabled;set{if(enabled==value)return;enabled=value;EnableChanged?.Invoke(value);}}
    public bool Running {get;private set;}=true;
    private readonly SynchronizationContext dispatch;
    public EnbFrontendController(EnbRetailAssetBundle assets,uint? soundSettingsOwner=null,bool introductoryTutorialCompleted=false)
    {
        _assets=assets;
        dispatch=SynchronizationContext.Current ?? throw new InvalidOperationException("Frontend requires the game-thread synchronization context.");
        _tutorial = EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientIniDirectory,"tutorialdef.ini"));
        // 6AC600 -> 6AD4B0 seeds ChooseShard as seen when tutorial.dat can be
        // opened for reading. Import the installed client's completion marker.
        try
        {
            using var marker=File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(Path.Combine(Path.GetDirectoryName(assets.Paths.ClientExecutablePath)!,"tutorial.dat")));
            _chooseShardPlayed=true;
        }
        catch(IOException) { }
        catch(UnauthorizedAccessException) { }
        _chooseShardPlayed|=introductoryTutorialCompleted;
        _introductoryTutorialCompleted=_chooseShardPlayed;
        SeedCreationNarrationHistory();
        _loginNarration=new(_introductoryTutorialCompleted);
        Owner=new(assets);
        _layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(assets.Paths.ClientIniDirectory,"fa_fe01a.ini"));
        _gpu=EnbWorldRenderer.CreateTooltipLayer(()=>assets,()=>null);
        _creationTooltipGpu=EnbWorldRenderer.CreateTooltipLayer(()=>assets,()=>null);
        Owner.SoundRequested+=_gpu.QueueFrontendSound;
        Owner.Dialogs.SoundRequested+=_gpu.QueueFrontendSound;
        Owner.Dialogs.ActivityChanged+=busy=>SetBusy(busy||_busy);
        Owner.Dialogs.QuitRequested+=()=>QuitRequested?.Invoke();
        _gpu.EnableClientAudio=true;
        _gpu.FrontendSoundSettingsOwner=soundSettingsOwner;
        InitializeCreationClassActors();
    }
    private string? Hit(EnbUiPoint point) => _hits.FirstOrDefault(h => h.Value.Contains(point)).Key
        ?? _actorHits.FirstOrDefault(h => h.Value.Contains(point)).Key;
    public void Speak(string cue)
    {
        _reminderCue=cue;_reminderTime=60;
        SelectSpeech(cue);
        NextSpeech();
    }
    private void SelectSpeech(string cue)
    {
        // 6ACF50 replaces the pending step/index, without interrupting the actor.
        _speech.Clear();
        var section = _tutorial.EnumerateSections().Select(s=>s.Value).FirstOrDefault(s=>s.GetValueOrDefault("Step")==cue);
        if(section is not null)
            for(int i=0;section.TryGetValue("Speech"+i,out var resource);i++)
                _speech.Enqueue((resource,section.GetValueOrDefault("Text"+i,""),section.GetValueOrDefault("SkipBlend","false").Equals("true",StringComparison.OrdinalIgnoreCase)));
    }
    private void SpeakCreationResources(params int[] ids)
        => SpeakCreationResources(ids.Select(id=>$"1492_00_{id:000}se.mp3").ToArray());
    private void SpeakCreationResources(params string[] resources)
    {
        _speech.Clear();
        foreach(string resource in resources)_speech.Enqueue((resource,"",false));
        NextSpeech();
    }
    private void NextSpeech()
    {
        _speechIdentity++;
        if(_speech.TryDequeue(out var next))
        {
            _speechActive=true;
            _speechAudioOnly=next.Text.Length==0;
            // 0067CBD0's creation voice queue has no avatar-command consumer.
            // Only 00784C90 narration drives Megan's body/facial state.
            if(!_speechAudioOnly)
            {
                _narratorAppearance??=EnbRetailAvatarDefaults.Narrator(_assets);
                _narratorSpeech??=new(_assets,_narratorAppearance,portraitIdle:true);
                _narratorSpeech.Replace(next.Text,TickCount()*.001,_lastNarratorPose,next.SkipBlend);
            }
            _gpu.SetFrontendSpeech(new(_speechIdentity,next.Resource,_screen));
        }
        else
        {
            _speechActive=false;
            if(!HasSceneNarrator || _narratorSpeech?.Active==true)_narratorSpeech?.Clear();
            _gpu.SetFrontendSpeech(null);
            BeginCreationOutroAfterSpeech();
        }
    }
    // Custom application diagnostics are not a retail login-screen label.
    public Action<string>? StatusReported { get; set; }
    public void SetStatus(string text) { if(!string.IsNullOrEmpty(text))StatusReported?.Invoke(text); }
    public Task LoginAcceptedAsync()
    {
        SetBusy(true); _loginExit=true; _started=TickCount();
        StartLoginAnimation(true);
        _speech.Clear();_speechActive=false;_narratorSpeech?.Clear();_gpu.SetFrontendSpeech(null);
        ChangeNarrator("walk_off");
        _loginTransition=new(TaskCreationOptions.RunContinuationsAsynchronously);
        return _loginTransition.Task;
    }
    private void StartLoginAnimation(bool exiting)
    {
        var model=Model("fe01a");
        model.TryAnimation(exiting?"fe01a-2":"fe01a-1",out var clip);
        ApplyLoginAnimation(clip,exiting);
    }
    private void ApplyLoginAnimation(EnbW3dRenderHierarchyAnimation? clip,bool exiting)
    {
        // 67AD30 retains the prior HAnim on acquisition failure; 5ADA50
        // then starts it in reverse instead of requiring the missing outro.
        if(clip is not null)_loginAnimationClip=clip;
        if(_loginAnimationClip is null)throw new InvalidDataException("Login animation unavailable");
        _loginAnimationClock=new((int)_loginAnimationClip.NumFrames,_loginAnimationClip.FrameRate,exiting&&clip is null);
        _loginAnimationTick=TickCount();
    }
    private EnbFrontendModel Model(string name) => Owner.Resources.Model(name);
    public void SetBusy(bool busy)
    {
        busy|=_screen=="login" && _loginEntering;
        _busy = busy;
        Username.IsEnabled = Password.IsEnabled = !busy && Owner.Dialogs.Active is null;
        _creationShipName.IsEnabled = !busy && Owner.Dialogs.Active is null;
        _creationName.IsEnabled = !busy && Owner.Dialogs.Active is null && !CreationNameMoving;
    }
    public void ShowSplash()
    {
        _creationFadeCover.IsVisible=false;
        _splashModel=RetailSplashSelection.ModelAt(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _screen="splash"; _started=TickCount();
        Username.IsVisible=Password.IsVisible=false;
        _gpu.SetFrontendSpeech(null);_speech.Clear();_narratorSpeech?.Clear();_speechActive=false;ClearLabels();
    }
    // Retail CharacterView modes 7/8 render the frontend without a mode controller.
    public void ShowCharacterReturn()
    {
        _screen="return";
        Username.IsVisible=Password.IsVisible=false;
        _creationName.IsVisible=_creationShipName.IsVisible=false;
        ClearLabels();
        Draws=[];
        _gpu.SetFrontendDraws(Draws,_screen);
        SetBusy(true);
    }
    public void ShowLogin()
    {
        _creationFadeCover.IsVisible=false;
        _creationName.IsVisible=false;
        _creationShipName.IsVisible=false;
        _screen="login";_started=TickCount();_loginExit=false;_loginTransition=null;_loginEntering=true;
        StartLoginAnimation(false);
        Username.Editor.InsertMode=Password.Editor.InsertMode=true;
        SetStatus("");
        _layout=EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fa_fe01a.ini"));
        Username.IsVisible=Password.IsVisible=true;
        Password.Text="";ClearLabels();SetBusy(true);
        _speech.Clear();_speechActive=false;_gpu.SetFrontendSpeech(null);ResetNarrator();
    }
    internal Action<string>? PreparationTrace { get; set; }
    private bool _preparingCharacters, _frontendDisposed;
    public void ShowCharacters(IReadOnlyList<CharacterSummary> characters)
    {
        foreach(var step in CharacterPreparationSteps(characters))step();
    }
    public async Task ShowCharactersAsync(IReadOnlyList<CharacterSummary> characters)
    {
        bool enabled=IsEnabled;
        _preparingCharacters=true;IsEnabled=false;
        int preparationStep=0;
        try
        {
            foreach(var step in CharacterPreparationSteps(characters))
            {
                if(_frontendDisposed)throw new OperationCanceledException("Frontend closed during preparation.");
                PreparationTrace?.Invoke("character-step-"+preparationStep+"-start");
                step();
                PreparationTrace?.Invoke("character-step-"+preparationStep+++"-end");
                // Keep authored load/RNG order on the UI thread, but allow
                // sent messages and input between expensive asset groups.
                await Task.Delay(1);
            }
        }
        finally { _preparingCharacters=false; if(!_frontendDisposed)IsEnabled=enabled; }
    }
    private IEnumerable<Action> CharacterPreparationSteps(IReadOnlyList<CharacterSummary> characters)
    {
        yield return () =>
        {
        _creationFadeCover.IsVisible=false;
        _creationTooltipGpu.SetTooltipPresentation(null);
        _creationName.IsVisible=false;
        _creationShipName.IsVisible=false;
        _gpu.SetFrontendSpeech(null); _speech.Clear(); _narratorSpeech?.Clear(); _speechActive=false; _loginExit=false;
        SetStatus("");
        _characters = characters; _avatars.Clear(); _characterSpeech.Clear(); _selected = -1; _screen = "characters"; _started = TickCount();
        _deleteEffect=null;_deleteEffectStarted=0;_deleteEffectSlot=-1;_deleteBeams=[];
        _layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, "fa_fe12a.ini"));
        _stagedSlot = _nextSlot = _creationPendingSlot = -1;
        };
        yield return () => Model("fe_room");
        yield return () => Model("fe12a").TryAnimation("fe12a-1",out _);
        yield return () => Model("fe01c").TryAnimation("fe01c-1",out _);
        yield return () => Model("fe01c").TryAnimation("fe01c-10",out _);
        for(int i=0;i<characters.Count;i++)
        {
            int slot=i;
            yield return () =>
            {
                Model("fe01c").TryAnimation($"fe01c-{slot+5}",out _);
                if(!characters[slot].IsEmpty && characters[slot].Appearance is {} appearance)
                { _avatars[slot]=EnbAvatarPortraitAsset.Load(_assets,appearance); CharacterSpeech(slot); }
            };
        }
        foreach(var step in CreationProfessionPreparationSteps())yield return step;
        yield return () =>
        {
        QueueCreationShipGpuPrewarm();
        BeginSelectionMotion(SelectionMotion.Entry);
        Username.IsVisible = Password.IsVisible = false;
        ClearLabels();
        };
    }
    private void ClearLabels() => Owner.Text.Clear();
    public void UpdateFrame()
    {
        if (_screen == "return" || _preparingCharacters || _frontendDisposed || Bounds.Width < 2 || Bounds.Height < 2 || Error is not null) return;
        try
        {
            double scaling = RenderScaling;
            int width = (int)(Bounds.Width * scaling), height = (int)(Bounds.Height * scaling);
            var orientation = Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
            if (!EnbResolvedRenderCamera.TryCreateOrthographic(new(0,0,1000), orientation, new(width,height,1), 320,240,new(1,2000),out var camera)) return;
            if(_animationSoundListener is null && EnbResolvedRenderCamera.TryCreate(Vector3.Zero,orientation,
                new(width,height,1),Math.PI/4,4d/3,new(1,1000),out var initialListener))
                _animationSoundListener=initialListener;
            double seconds = (TickCount() - _started) / 1000d;
            _caret.IsVisible = false;
            var speechReceipt=_gpu.SpeechReceipt;
            // 00783B50 advances authored narration when its command animation ends.
            // Audio-only creation prompts retain their separate audio completion boundary.
            if(_speechActive && (_speechAudioOnly
                ? speechReceipt?.Identity==_speechIdentity && speechReceipt.Complete
                : _narratorSpeech?.Active!=true)) NextSpeech();
            if(_screen=="login")
            {
                if(_loginAnimationClock is null)StartLoginAnimation(_loginExit);
                if(_loginEntering && !_loginNarratorCommand && _loginAnimationClock!.ConsumeCompletion())
                {
                    _loginEntering=false;SetBusy(false);
                    if(_loginNarration.TakeIntro(DateTimeOffset.UtcNow.ToUnixTimeSeconds()) is {} cue)Speak(cue);
                }
                if(_loginExit && _loginNarratorTransition.Hidden && _loginAnimationClock!.ConsumeCompletion())
                    _loginTransition?.TrySetResult();
                long now=TickCount();
                _loginAnimationClock!.Advance(unchecked((uint)(now-_loginAnimationTick)));
                _loginAnimationTick=now;
            }
            var draws = ImmutableArray.CreateBuilder<EnbFrontendDraw>();
            var roomDazzles = ImmutableArray<EnbWorldRenderer.FrontendDazzleInstance>.Empty;
            var collision = ImmutableArray.CreateBuilder<EnbWorldRenderer.FrontendCollisionMesh>();
            _actorHits.Clear();
            void DrawModel(string key,EnbFrontendModel model,EnbW3dHierarchyPoseOwner modelPose,EnbResolvedRenderCamera view)
            {
                foreach(var mesh in model.Resource.Meshes.OrderBy(m=>m.StaticSort.IsDeferred).ThenByDescending(m=>m.StaticSort.RuntimeLevel))
                {
                    int pivot=model.Pivot(mesh.Name);if(!modelPose.Visibility[pivot])continue;
                    draws.Add(new(key+"/"+mesh.Name,model.Resource,mesh,modelPose.WorldTransforms[pivot],null,view,(uint)TickCount()));
                }
            }
            if(_screen=="splash")
            {
                var splash=Model(_splashModel);DrawModel("splash",splash,splash.Pose(null,0),camera);
            Owner.Dialogs.Render(draws,camera,Bounds,_pointer);
                Draws=draws.ToImmutable();_gpu.SetFrontendDraws(Draws,_screen,soundListener:_animationSoundListener);return;
            }
            if(_screen=="creation")
            {
                RenderCreation(draws,camera,scaling);
                RenderImportButton(draws);
            if(_pointerInside)UpdateHover(_pointer);
                Draws=draws.ToImmutable();_gpu.SetFrontendDraws(Draws,_screen,_creationEntranceBeams,soundListener:_animationSoundListener);return;
            }
            EnbW3dHierarchyPoseOwner? selectionPanel = null;
            if(_screen=="login")
            {
                EnbResolvedRenderCamera.TryCreate(new(0,0,640),orientation,new(width,height,1),Math.PI/4,4d/3,new(1,10000),out var backgroundCamera);
                var background=Model("comp00a");DrawModel("background",background,background.Pose(null,0),backgroundCamera);
                PlayLoginAnimationSounds();
                var actor=UpdateNarrator();
                var narratorModel=Model("fe_narrator");
                var narratorWorld=narratorModel.Pose(null,0).WorldTransforms[narratorModel.Pivot("NARRATOR")];
                EnbResolvedRenderCamera.TryCreate(new(0,0,640),orientation,new(width,height,1),Math.PI/4,4d/3,new(1,1000),out var narratorCamera);
                if(!_loginNarratorTransition.Hidden && actor is not null)
                {
                CollectAnimationSounds(_narratorSpeech,actor,narratorWorld);
                foreach(var part in actor.Parts)draws.Add(new($"narrator/{part.Resource.Key}/{part.Mesh.Ordinal}",part.Resource,part.Mesh,narratorWorld,part.Geometry,narratorCamera,(uint)TickCount()){Avatar=_narratorAppearance});
                }
            }
            else
            {
                var staging=Model("fe01c");
                var (stagingPose, panelPose) = SelectionPose();
                selectionPanel = panelPose;
                var deleteBeams=ImmutableArray<EnbWorldRenderer.FrontendBeamInstance>.Empty;
                var transform=stagingPose.WorldTransforms[staging.Pivot("CAMERA")];
                Matrix4x4.Decompose(transform,out _,out var rotation,out var position);
                if(!EnbResolvedRenderCamera.TryCreate(position,rotation,new(width,height,1),Math.PI/4,4d/3,new(1,500),out var sceneCamera))throw new InvalidDataException("Frontend camera invalid");
                _animationSoundListener=sceneCamera;
                var room=Model("fe_room");var roomPose=room.Pose(null,0);
                DrawModel("room",room,roomPose,sceneCamera);
                // 0067AB20 puts FE_ROOM and FE01C in scene +6C. Scene list
                // insertion 009D1040 is newest first: room before staging.
                // FE01C has no authored members; its attached avatars follow.
                var roomHlod=room.Resource.HierarchicalLods.Single();
                foreach(var member in roomHlod.Levels[^1].Members.Concat(roomHlod.Aggregate?.Members??[]))
                {
                    var mesh=room.Resource.Meshes.FirstOrDefault(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,member.Name));
                    if(mesh is not null && (EnbRetailDazzleCollision.ConfiguredMeshMask(mesh)&4)!=0 && roomPose.Visibility[(int)member.ReferenceIndex])
                        collision.Add(new(mesh,roomPose.WorldTransforms[(int)member.ReferenceIndex]));
                }

                roomDazzles=room.Dazzles.Select(d => new EnbWorldRenderer.FrontendDazzleInstance(
                    d.Definition,roomPose.WorldTransforms[room.Pivot(d.Name[(d.Name.LastIndexOf('.')+1)..])],sceneCamera){Name=d.Name}).ToImmutableArray();
                for(int i=0;i<_characters.Count;i++)
                {
                    if(_characters[i].IsEmpty||_characters[i].Appearance is not {} appearance)continue;
                    // 007897D0 toggles the avatar's render objects when the
                    // 3.2-second presentation clock reaches its 0.7-second
                    // threshold. 0055CEA0 removes the actor at clock expiry.
                    if(i==_deleteEffectSlot && TickCount()-_deleteEffectStarted>=2500)continue;
                    if(!_avatars.TryGetValue(i,out var actor))_avatars[i]=actor=EnbAvatarPortraitAsset.Load(_assets,appearance);
                    var animated=actor.Evaluate?.Invoke(TickCount()*.001)??actor;
                    if(_characterSpeech.TryGetValue(i,out var speech))
                        animated=speech.Evaluate(TickCount()*.001)??animated;
                    var world=stagingPose.WorldTransforms[staging.Pivot($"DUDE{i:00}")];
                    CollectAnimationSounds(speech,animated,world);
                    foreach(var part in animated.Parts)draws.Add(new($"avatar/{i}/{part.Resource.Key}/{part.Mesh.Ordinal}",part.Resource,part.Mesh,world,part.Geometry,sceneCamera,(uint)TickCount())
                        {Avatar=appearance, Highlight=!_busy && !SelectionMoving && Owner.Dialogs.Active is null && i!=_selected && _hover==$"NAME{i:00}"});
                    foreach(var part in animated.Parts)
                        if(part.CollisionMesh is {} collisionMesh)
                            collision.Add(new(collisionMesh,part.CollisionTransform*world));
                    var points = animated.Parts.SelectMany(p => p.Geometry.Positions);
                    var min = points.Aggregate(Vector3.Min); var max = points.Aggregate(Vector3.Max);
                    var projected = new List<EnbUiPoint>();
                    for (int corner = 0; corner < 8; corner++)
                    {
                        var local = new Vector3((corner & 1) == 0 ? min.X : max.X,
                            (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z);
                        // Keep offscreen corners: clipping them before forming the bounds
                        // loses the visible torso when the feet extend below the viewport.
                        var viewPoint=sceneCamera.WorldToCanonicalView(Vector3.Transform(local,world));
                        if(viewPoint.Z < -sceneCamera.Projection.ClipRange.NearPlane &&
                            sceneCamera.Projection.TryProjectViewPoint(viewPoint,out var ndc))
                            projected.Add(new((ndc.X*.5+.5)*Bounds.Width,(.5-ndc.Y*.5)*Bounds.Height));
                    }
                    if (projected.Count > 0)
                        _actorHits[$"NAME{i:00}"] = new(new EnbUiPoint(projected.Min(p => p.X), projected.Min(p => p.Y)),
                            new EnbUiPoint(projected.Max(p => p.X), projected.Max(p => p.Y)));
                }
                if(_deleteEffect is {} effect && _deleteEffectSlot>=0)
                {
                    long elapsed=TickCount()-_deleteEffectStarted;
                    if(elapsed<=effect.TailDurationMilliseconds)
                    {
                        var world=stagingPose.WorldTransforms[staging.Pivot($"DUDE{_deleteEffectSlot:00}")];
                        // 007896B0 attaches and starts AV_TELE01 immediately;
                        // the later 007897D0 visibility threshold controls
                        // the avatar, not the teleport aggregate.
                        deleteBeams=[new(effect,elapsed,world,sceneCamera,100+_deleteEffectSlot)];
                        _animationSounds ??= EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"W3DAnimSound.ini"));
                        var cues=EnbRetailAnimationSounds.Read(_animationSounds,effect.AnimationName,effect.FrameRate);
                        for(int cue=0;cue<cues.Count;cue++)
                            if(elapsed>=cues[cue].Seconds*1000 && _deleteEffectPlayedSounds.Add(cue))
                                _gpu.QueueFrontendSound(cues[cue].Preset);
                    }
                    else _deleteEffect=null;
                }
                _deleteBeams=deleteBeams;
            }
            var ui = Model(_screen == "login" ? "fe01a" : "fe12a");
            var pose = selectionPanel ?? ui.PoseFrame(_loginAnimationClip,_loginAnimationClock!.Frame);
            EnbUiRect? loginNameBounds=null;
            _hits.Clear();
            foreach (var label in Owner.Text.Labels.Values) label.IsVisible = false;
            foreach (var mesh in ui.Resource.Meshes.OrderByDescending(m => m.StaticSort.RuntimeLevel))
            {
                int pivot = ui.Pivot(mesh.Name);
                if (!pose.Visibility[pivot]) continue;
                if (_screen == "characters" && mesh.Name == "CLICK_ME") continue;
                if (_screen == "characters" && _stagedSlot < 0 && mesh.Name is "PDA" or "L_RACE00") continue;
                var matrix = pose.WorldTransforms[pivot];
                if(_screen=="login" && mesh.Name=="BUTTON_NAME")
                {
                    var left=Vector3.Transform(mesh.BoundingBoxMin,matrix);var right=Vector3.Transform(mesh.BoundingBoxMax,matrix);
                    float x=Math.Min(left.X,right.X),fieldWidth=Math.Abs(right.X-left.X);
                    loginNameBounds=Screen(new(x+320,240-Math.Max(left.Y,right.Y),fieldWidth,Math.Abs(right.Y-left.Y)));
                    matrix*=Matrix4x4.CreateTranslation(-x,0,0)*Matrix4x4.CreateScale((fieldWidth-EnbSavedAccountsPanel.ArrowWidth)/fieldWidth,1,1)*Matrix4x4.CreateTranslation(x,0,0);
                }
                bool interactive = mesh.Name.StartsWith("BUTTON") || mesh.Name.StartsWith("NAME") || mesh.Name is "EXIT" or "CANCEL" or "CONTINUE" or "DELETE";
                bool disabled = interactive && (_busy || Owner.Dialogs.Active is not null || (_screen == "characters" && _selected < 0 && mesh.Name is "CONTINUE" or "DELETE"));
                draws.Add(new("ui/" + mesh.Name, ui.Resource, mesh, matrix, null, camera, (uint)TickCount())
                    { Highlight = interactive && _hover == mesh.Name && !disabled,
                        Pressed = _pressedName == mesh.Name, Disabled = disabled });
                var a = Vector3.Transform(mesh.BoundingBoxMin, matrix); var b = Vector3.Transform(mesh.BoundingBoxMax, matrix);
                var rect = Screen(new(Math.Min(a.X,b.X)+320,240-Math.Max(a.Y,b.Y),Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y)));
                if (mesh.Name.StartsWith("BUTTON") || mesh.Name.StartsWith("NAME") || mesh.Name is "EXIT" or "CANCEL" or "CONTINUE" or "DELETE") _hits[mesh.Name] = rect;
            }
            if (_screen == "login")
            {
                Username.IsVisible=_hits.TryGetValue("BUTTON_NAME",out var nameRect);
                Password.IsVisible=_hits.TryGetValue("BUTTON_PASSWORD",out var passwordRect);
                if(Username.IsVisible)Place(Username,nameRect);
                if(Password.IsVisible)Place(Password,passwordRect);
                Label("Name_fe01a", Username.Text ?? "", ui, pose, scaling);
                Label("Password_fe01a", new string('*', Password.Text?.Length ?? 0), ui, pose, scaling);
                foreach (string id in new[]{"New_fe01a","Quit_fe01a","Register_fe01a"}) Label(id,null,ui,pose,scaling);
                Label("N_fe01a","<< Enter Net7 Member Name",ui,pose,scaling);
                Label("P_fe01a","<< Enter Net7 Password",ui,pose,scaling);
            }
            else
            {
                // FE12A's panel animation moves its anchors while the roster
                // buttons remain visible. Keep the names on their roster anchors
                // until the creator's first frame replaces the selection view.
                var namePose=_creationPendingSlot>=0 ? ui.Pose("fe12a-1",0) : pose;
                for (int i=0;i<_characters.Count;i++) Label($"fe12_Name_{i:00}",_characters[i].IsEmpty?"New Character":_characters[i].FullName,ui,namePose,scaling,i==_selected);
                Label("fe12_Delete",null,ui,pose,scaling);
                Label("fe12_confirm",null,ui,pose,scaling);Label("fe12_exit",null,ui,pose,scaling);
                if(_stagedSlot>=0 && !_characters[_stagedSlot].IsEmpty)
                {
                    var character=_characters[_stagedSlot];
                    Label("fe12_Text_Info",null,ui,pose,scaling);
                    Label("fe12_Race_Title","Race: "+character.RaceName,ui,pose,scaling);
                    Label("fe12_Prof_Title","Class: "+character.ClassName,ui,pose,scaling);
                    Label("fe12_Level_Title","Level: "+character.OverallLevel,ui,pose,scaling);
                    Label("fe12_Server_Title",GalaxyDescription(character),ui,pose,scaling);
                    Owner.Text.WrappedLocation(character.SectorName.Length==0?"":"Location: "+character.SectorName,ui,pose,TickCount(),_layout,Bounds,_stagedSlot,_pointer,_pointerInside);
                }
            }
            RenderImportButton(draws);
            if(_pointerInside)UpdateHover(_pointer);
            Owner.RenderServer(draws,camera,Bounds,_pointer,_screen,_busy,loginNameBounds??(_hits.TryGetValue("BUTTON_NAME",out var serverName)?serverName:null));
            if(_screen=="login"&&!Owner.Accounts.IsOpen)Username.IsEnabled=Password.IsEnabled=!_busy&&!Owner.Accounts.Busy&&Owner.Dialogs.Active is null;
            Owner.Accounts.Render(draws,camera,Bounds,_pointer,loginNameBounds,_screen=="login",_busy||Owner.Dialogs.Active is not null,_caret);
            Owner.Groups.Render(draws,camera,Bounds,_pointer,loginNameBounds,_screen=="login",_busy||Owner.Dialogs.Active is not null,_caret);
            RenderAccountTooltip();
            Owner.Dialogs.Render(draws,camera,Bounds,_pointer);
            Draws = draws.ToImmutable(); _gpu.SetFrontendDraws(Draws,_screen,beams:_deleteBeams,dazzles:roomDazzles,collision:collision.ToImmutable(),soundListener:_animationSoundListener);
        }
        catch(Exception ex) { Error = ex.ToString(); _loginTransition?.TrySetException(ex); Diagnostic?.Invoke("Frontend rendering error: " + ex); Running=false; }
    }
    private void RenderAccountTooltip()
    {
        if(Owner.Groups.Editing){_creationTooltipGpu.SetTooltipPresentation(null);return;}
        var groupTip=Owner.Groups.TooltipAt(_pointer);
        var forget=Owner.Groups.GroupMode?null:Owner.Accounts.ForgetAt(_pointer);
        var import=_hits.TryGetValue("BUTTON_IMPORT",out var importBounds)&&!Owner.Accounts.IsOpen&&importBounds.Contains(_pointer);
        var target=import?importBounds:groupTip?.Bounds??forget?.Bounds??(!Owner.Groups.GroupMode&&!Owner.Accounts.HasAccounts?Owner.Accounts.FindHit("open"):null);
        if(_screen!="login"||_busy||Owner.Dialogs.Active is not null||!_pointerInside||target is not {} arrow||!arrow.Contains(_pointer))
        {_creationTooltipDelay.Reset();_creationTooltipRenderedKey="";_creationTooltipGpu.SetTooltipPresentation(null);return;}
        int delay=EnbCurrentRetailTooltipDelay.Milliseconds(_assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay);
        if(!_creationTooltipDelay.IsReady(import?100:groupTip?.Id??forget?.Id??1,TickCount(),delay)){_creationTooltipGpu.SetTooltipPresentation(null);return;}
        string text=groupTip?.Text??(import?"Import your game accounts and character names from Net-7.org. Review them before saving.":forget is not null?"Forget this login.":Owner.Accounts.Busy?"Loading saved accounts...":"No saved accounts for this server. Use Remember login when signing in, or Import Accounts to import from Net-7.org.");
        string key=$"accounts/{text}/{Bounds.Width}/{Bounds.Height}/{arrow}";
        if(_creationTooltipRenderedKey==key)return;
        var definition=EnbRetailScannerTooltipDefinition.Load(_assets.Paths.ClientIniDirectory);
        if(definition is null)return;
        _creationTooltipFrame??=EnbAuthoredTooltipFrame.Load(_assets,definition);
        if(_creationTooltipFrame is null||Owner.Text.Font(definition.FontId,Bounds.Size) is not {} font)return;
        _creationTooltipGpu.SetTooltipPresentation(EnbTooltipContent.Create(_creationTooltipFrame,font,text,Bounds.Size,1,new(.1f,1,.1f),_pointer,i=>
        {while(_creationTooltipLines.Count<=i)_creationTooltipLines.Add(new());return _creationTooltipLines[i];},arrow));
        _creationTooltipRenderedKey=key;
    }
    private static void Place(EnbUiTextInput input,EnbUiRect bounds)=>input.Bounds=bounds;
    private bool HandleServerClick(EnbUiPoint point)=>Owner.HandleServerClick(point,_screen,_busy);
    private EnbUiRect Screen(EnbUiRect r) => new(r.X*Bounds.Width/640,r.Y*Bounds.Height/480,r.Width*Bounds.Width/640,r.Height*Bounds.Height/480);
    private static string GalaxyDescription(CharacterSummary character)
    {
        if (string.IsNullOrWhiteSpace(character.GalaxyName)) return "";
        double onePercentCapacity = character.GalaxyCapacity * .01d;
        if (onePercentCapacity == 0) onePercentCapacity = 1;
        double percent = character.GalaxyCurrentLoad / onePercentCapacity;
        if (percent >= 100) return $"Galaxy: {character.GalaxyName} (FULL)";
        return $"Galaxy: {character.GalaxyName} ({percent:0}% full)";
    }
    private static void Place(EnbFrontendVisual visual,EnbUiRect bounds)=>visual.Bounds=bounds;
    private void Label(string id,string? text,EnbFrontendModel model,EnbW3dHierarchyPoseOwner pose,double scaling,bool selected=false)
    {
        if (!_layout.TryResolve(id,out var definition)) return;
        int attachPivot=model.Pivot(definition.Properties["Attach"]);
        // The retail label is attached to this authored UI pivot. FE02A-2 hides
        // those pivots during the creation exit; the separate host text layer
        // must obey the same visibility as the GPU geometry.
        if(!pose.Visibility[attachPivot])return;
        var p=pose.WorldTransforms[attachPivot].Translation;
        if(_screen=="login" && id=="Name_fe01a")p.X-=EnbSavedAccountsPanel.ArrowWidth/2;
        string fontId=definition.Properties.GetValueOrDefault("FontType","FONT_SMALL");
        if(Owner.Text.Font(fontId,Bounds.Size) is not {} font)return;
        var label=Owner.Text.Get(id);
        bool hovered=string.Equals(_hover,definition.Properties["Attach"],StringComparison.OrdinalIgnoreCase);
        bool interactive=_hits.ContainsKey(definition.Properties["Attach"].ToUpperInvariant());
        bool blinkApplied=false;
        var color=selected?new EnbRgb(0,1,0):new EnbRgb(1,1,1);
        if((_busy || Owner.Dialogs.Active is not null) && interactive || (_screen=="characters" && _selected<0 && id is "fe12_Delete" or "fe12_confirm"))color=new(.25f,.25f,.25f);
        else if(hovered)
        {
            string effect=definition.Properties.GetValueOrDefault("MouseOver","NONE");
            if(effect == "FA_TOGGLE_COLOR")color=new(0,1,0);
            else if(effect == "FA_BLINK")
            {
                blinkApplied=true;
                long elapsed=Math.Max(0,TickCount()-_hoverBeganTick);
                float phase=(elapsed%Owner.Resources.BlinkPeriodMilliseconds)/(float)Owner.Resources.BlinkPeriodMilliseconds;
                label.Opacity=phase<=.5f?phase*2:2-phase*2;
            }
        }
        // 00616220 replaces the NAME texture once text is entered; the
        // focused field uses the authored FA_GREEN alternate text color.
        if(_screen=="creation" && _creationPage==CreationPage.Name && id=="Name_fe02a" &&
            !string.IsNullOrEmpty(_creationName.Text))color=new(0,1,0);
        // Generic text gadgets set their base/alternate color selector on focus
        // (006AAD10 -> 006CE910 -> 006CCE60), independently of mouse hover.
        EnbUiTextInput? colorEditor = id switch { "Name_fe01a"=>Username, "Password_fe01a"=>Password, "NAME_fa_ship04"=>_creationShipName, _=>null };
        if (colorEditor is { IsFocused:true, IsEnabled:true } && !_busy && Owner.Dialogs.Active is null &&
            definition.Properties.TryGetValue("AltColor",out var alternateColor) && Owner.Resources.TryColor(alternateColor,out var authoredColor))
            color=authoredColor;
        if(!blinkApplied)label.Opacity=1;
        if(_screen=="login" && _loginEntering)label.Opacity=Math.Min(label.Opacity,.33f);
        // 617540 sets the name-page controls/population labels' +2FC to
        // 0x3EA8F5C3; 6D64C0 clamps rendered alpha to that value.
        if(_screen=="creation" && _creationPage==CreationPage.Name &&
            (CreationNameMoving && id!="Banner_fe02a" || id=="Skip_fe02a" && !_introductoryTutorialCompleted))
            label.Opacity=Math.Min(label.Opacity,.33f);
        EnbUiRect? shipNameClip=null;
        if(_screen=="creation" && _creationPage==CreationPage.Ship && _shipTab==3 &&
            definition.Properties.GetValueOrDefault("Attach","").Equals("NAME",StringComparison.OrdinalIgnoreCase) &&
            _hits.TryGetValue("NAME",out var nameBounds))
        {
            // 625A60 sets text-gadget +78 to ten UI units. 6AAD10's
            // 0x400 branch trims the focused display and clips the text mesh;
            // it does not truncate the retained ship name or its decal.
            float ux=(float)Bounds.Width/640,uy=(float)Bounds.Height/480;
            if(_creationTextEditing)
                text=EnbRetailFrontendTextWindow.TrimLeading(text??"",(float)nameBounds.Width-20*ux,
                    value=>font.MeasureWidth(value)*font.Scale);
            int left=(int)(nameBounds.Left/ux-320+10),right=(int)(nameBounds.Right/ux-320-10);
            int top=(int)(p.Y+nameBounds.Height/uy*.5),bottom=(int)(p.Y-nameBounds.Height/uy*.5);
            shipNameClip=Screen(new(left+320,240-top,Math.Max(0,right-left),Math.Max(0,top-bottom)));
        }
        if(_screen=="login" && id=="Name_fe01a" && _hits.TryGetValue("BUTTON_NAME",out var loginEditor))shipNameClip=loginEditor;
        label.SetText(text??definition.Properties.GetValueOrDefault("String0",""),font,color);
        label.SetDisplayScale(1,1);
        if(id is not ("Name_fe01a" or "Password_fe01a" or "Name_fe02a" or "NAME_fa_ship04") &&
            _hits.TryGetValue(definition.Properties["Attach"].ToUpperInvariant(),out var gadgetBounds))
            label.FitTo(gadgetBounds.Size,scaling);
        var anchor=Screen(new(p.X+320,240-p.Y,0,0));
        string justify=definition.Properties.GetValueOrDefault("Justify","");
        label.X=anchor.X-(justify.EndsWith(",CENTER",StringComparison.OrdinalIgnoreCase)?label.Width/2:justify.EndsWith(",RIGHT",StringComparison.OrdinalIgnoreCase)?label.Width:0);
        if(_screen=="creation" && _creationPage==CreationPage.Name &&
            definition.Properties.GetValueOrDefault("EffectConst","").Contains("FA_SCROLL_LEFT",StringComparison.OrdinalIgnoreCase))
        {
            double travel=Bounds.Width+label.Width;
            double elapsed=Math.Max(0,(TickCount()-_creationPageTick)*.001);
            label.X=Bounds.Width-(elapsed*30*Bounds.Width/640)%Math.Max(1,travel);
        }
        label.Y=anchor.Y-label.Height/2;
        if(_screen=="login"&&id=="Name_fe01a")
        {label.X=Math.Round(label.X*scaling)/scaling;label.Y=Math.Round(label.Y*scaling)/scaling;}
        label.Clip=shipNameClip is {} clip?new EnbUiRect(
            clip.X-label.X,clip.Y-label.Y,clip.Width,clip.Height):null;
        label.IsVisible=true;
        var input = id == "Name_fe01a" ? Username : id == "Password_fe01a" ? Password :
            _screen=="creation" && definition.Properties.GetValueOrDefault("Attach","").Equals("NAME",StringComparison.OrdinalIgnoreCase)
                ? _creationPage==CreationPage.Name?_creationName:
                  _creationPage==CreationPage.Ship && _shipTab==3?_creationShipName:null : null;
        if(input is { IsEnabled: true } && (input.IsFocused ||
            _screen=="creation" && _creationTextEditing && (input==_creationName || input==_creationShipName)))
        {
            string value=text??"";
            int index=Math.Clamp(input.CaretIndex,0,value.Length);
            // 6AA810/6AAD10 -> 685AB0: all four frontend gadgets retain
            // centered, steady carets. Geometry is in reference UI units,
            // with character advance (not single-character string width).
            float sx=640f/(float)Bounds.Width, sy=480f/(float)Bounds.Height;
            float Width(string s)=>font.MeasureWidth(s)*font.Scale*sx;
            var rectangle=EnbRetailCaretGeometry.Resolve(new(p.X,p.Y),Width(value),Width(value[..index]),
                font.CharacterAdvance(index<value.Length?value[index]:'n')*font.Scale*sx,
                font.CellHeight*font.Scale*sy,true,true,input.Editor.InsertMode);
            Place(_caret,Screen(new(rectangle.X+320,240-rectangle.Y,rectangle.Z,rectangle.W)));
            _caret.IsVisible=true;
        }
    }
    internal sealed record SessionState(bool ChooseShardPlayed, bool TutorialCompleted,
        string[] CreationCueHistory, EnbRetailLoginNarration LoginNarration);
    internal SessionState CaptureSession() => new(_chooseShardPlayed,
        _introductoryTutorialCompleted, [.._creationCueHistory], _loginNarration);
    internal void RestoreSession(SessionState state)
    {
        _chooseShardPlayed=state.ChooseShardPlayed;
        _introductoryTutorialCompleted=state.TutorialCompleted;
        _creationCueHistory.Clear(); _creationCueHistory.UnionWith(state.CreationCueHistory);
        _loginNarration=state.LoginNarration;
    }
    public Task ShowMessageAsync(string message)=>Owner.Dialogs.ShowMessageAsync(message);
    public Task<bool> ConfirmAsync(string message)=>Owner.Dialogs.ConfirmAsync(message);
    public Task ConfirmQuitAsync()=>Owner.Dialogs.ConfirmQuitAsync();
    internal void HandleEscape()=>Owner.Dialogs.HandleEscape();
    private void CompleteDialog(bool accepted)=>Owner.Dialogs.CompleteDialog(accepted);
    public void Dispose(){_frontendDisposed=true;Running=false;_gpu.SetFrontendSpeech(null);_creationTooltipGpu.SetTooltipPresentation(null);_loginTransition?.TrySetCanceled();Owner.Dispose();foreach(var line in _creationTooltipLines)line.Dispose();_creationTooltipLines.Clear();}
}
