using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Client;
using EnbNext.Net7.Protocol;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbFrontendController
{
    private static readonly (uint Race,uint Profession,float Angle)[] CreationClasses =
    [
        (0,0,20),(0,2,0),(0,1,340),(2,2,260),(2,1,240),
        (2,0,220),(1,0,140),(1,1,120),(1,2,100)
    ];
    private static string CreationClassMount(uint race,uint profession) => (race,profession) switch
    {
        (0,0)=>"ENFORCER", (0,1)=>"TRADESMAN", (0,2)=>"SCOUT",
        (1,0)=>"DEFENDER", (1,1)=>"SEEKER", (1,2)=>"EXPLORER",
        (2,0)=>"WARRIOR", (2,1)=>"PRIVATEER", (2,2)=>"SENTINEL",
        _=>throw new ArgumentOutOfRangeException(nameof(race))
    };
    private enum CreationPage { Profession, Avatar, Ship, Name }
    private CreationPage _creationPage;
    private int _creationSlot, _avatarTab, _shipTab, _creationColor;
    private bool _skipBeginnerTutorial;
    private Net7AvatarData? _creationAvatar;
    private byte[]? _creationShip;
    private IReadOnlyList<Net7GalaxyRoutingRecord> _creationRoutes = [];
    private int _creationRoute;
    private float _creationYaw;
    private Quaternion _creationShipRotation=Quaternion.Identity;
    private float _creationShipSpinAngle;
    private float _creationShipRadius,_creationShipDistance,_creationShipSeedDistance;
    private Vector2 _creationShipDragVelocity;
    private long _creationShipMotionTick;
    private float _creationShipFrameDelta;
    private float _creationZoom=3f;
    private float _creationAvatarRadius=1f;
    private EnbSceneAvatarSnapshot? _creationBoundsAppearance;
    private bool _creationPreserveBoundsForMorph;
    private bool _creationKeepCameraCenterForMorph;
    private Vector3 _creationAvatarBoundsCenter;
    private float _creationAvatarHeight;
    private float _creationPitch, _creationPanX, _creationPanY;
    private string _creationAvatarIdleTag="breathe3";
    private bool _creationDragging;
    private bool _creationBodyDragging;
    private bool _creationPaletteDragging;
    private float _creationBodyX=.5f, _creationBodyY=.5f;
    private EnbUiPoint _creationLastDrag;
    private int _creationDecalPage;
    private int _creationSkillOffset;
    private float _creationPlanetFromAngle=20f, _creationPlanetToAngle=20f;
    private long _creationPlanetSpinTick;
    private long _creationClassShipSpinTick;
    private bool _creationSkillDragging;
    private string? _creationHeldControl;
    private long _creationHeldLastTick;
    private long _creationHeldRepeatTick;
    private bool _creationPreserveRosterLabels;
    private EnbUiRect _creationSkillTrack;
    private EnbAuthoredTooltipFrame? _creationTooltipFrame;
    private readonly List<EnbRetailText> _creationTooltipLines=[];
    private readonly EnbCurrentRetailTooltipDelay _creationTooltipDelay=new();
    private string _creationTooltipSkill="";
    private string _creationTooltipRenderedKey="";
    private EnbAvatarPortraitAsset? _creationPortrait;
    private LiveAvatarSpeechPlayback? _creationAvatarPlayback;
    private sealed class CreationClassIdle
    {
        public EnbAvatarPortraitAsset? Clip;
        public required EnbSceneAvatarSnapshot Appearance;
        public required LiveAvatarSpeechPlayback Playback;
        public required EnbRetailAvatarHeadMotion HeadMotion;
    }
    private readonly CreationClassIdle?[] _creationClassIdles=new CreationClassIdle?[2];
    private readonly Dictionary<(uint Race,uint Profession,int Gender),CreationClassIdle> _creationClassActors=[];
    private readonly EnbSceneAvatarSnapshot?[] _creationClassAppearances=new EnbSceneAvatarSnapshot?[2];
    private EnbSceneAvatarSnapshot? _creationAppearance;
    private EnbUiTextInput _creationName=>Owner.CharacterName;
    private EnbUiTextInput _creationShipName=>Owner.ShipName;
    private bool _creationTextEditing;
    internal readonly EnbFrontendVisual[] _creationDecalImages = Enumerable.Range(0,10).Select(_=>new EnbFrontendVisual()).ToArray();
    private readonly Dictionary<string,Vector2> _creationPalettePoints = [];
    private sealed record CreationGenderState(Net7AvatarData Avatar,Dictionary<string,Vector2> Palette,float BodyX,float BodyY);
    private CreationGenderState? _creationOtherGender;
    private Dictionary<string,Vector2>? _creationSavedAvatarPalette;

    private readonly Dictionary<string,(long Tick,float Angle)> _creationSwatchTurns = [];
    private readonly Dictionary<string,List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)>> _creationClassShipParts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,EnbW3dWorldBounds> _creationRetailShipBounds =
        new(StringComparer.OrdinalIgnoreCase);
    internal readonly EnbFrontendVisual _creationSkillTrackVisual=new();
    internal readonly EnbFrontendVisual _creationSkillThumbVisual=new();
    private TaskCompletionSource? _creationSpeechCompletion;
    private bool _creationCompletionRequested;
    private bool _creationCompleting;
    private EnbW3dRenderHierarchyAnimation? _creationNameClip;
    private EnbRetailFrontendAnimationClock? _creationNameClock;
    private bool _creationNameAnimationPlaying;
    private long _creationNameTick;
    private bool _creationNameEntering, _creationNameReturning;
    private bool _chooseShardPlayed;
    private bool _introductoryTutorialCompleted;
    public void MarkIntroductoryTutorialCompleted()=>_introductoryTutorialCompleted=true;
    private bool CreationNameMoving => _creationPage==CreationPage.Name &&
        (_creationNameEntering || _creationNameReturning || _creationCompleting);
    private long _creationPageTick;
    private long _creationDialTick;
    private long _creationEntrancePresentationTick;
    private EnbStationBeamEffect? _creationEntranceEffect;
    private bool _creationEntranceLoadAttempted;
    private EnbStationBeamEffect[] _creationEntranceActors=[];
    private readonly EnbRetailAvatarTeleportClock _creationEntranceClock=new();
    private long _creationEntranceElapsed;

    private void ResetCreationEntrance()
    {
        _creationEntranceActors=_creationEntranceEffect is {} prototype
            ? [prototype.CreateInstance(),prototype.CreateInstance()] : [];
        _creationEntrancePresentationTick=TickCount();
        _creationEntranceElapsed=0;
        if(_creationEntranceActors.Length!=0)_creationEntranceClock.StartCreation();
        else _creationEntranceClock.Clear();
    }
    private ImmutableArray<EnbWorldRenderer.FrontendBeamInstance> _creationEntranceBeams=[];
    public event Action<Net7AvatarCreateRequest>? CreateRequested;

    private void LoadCreationEntrance(Func<EnbStationBeamEffect> load)
    {
        if(!_creationEntranceLoadAttempted)
        {
            _creationEntranceLoadAttempted=true;
            try { _creationEntranceEffect=load(); }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            {
                // Optional teleport data must not prevent character creation.
                // Keep the actors available; no substitute effect is invented.
                Diagnostic?.Invoke("Creation teleport unavailable: "+ex);
            }
        }
    }
    private void PreloadCreationProfessionAssets()
    {
        foreach(var step in CreationProfessionPreparationSteps())step();
    }
    private IEnumerable<Action> CreationProfessionPreparationSteps()
    {
        yield return () => Model("comp00a");
        yield return () => Model("fe_class00");
        yield return () => Model("fe_class01");
        yield return () => Model("fe_narrator").Animation("fe_narrator-1");
        yield return () => LoadCreationEntrance(()=>new EnbStationBeamEffect(_assets,"av_tele04"));
        foreach(var (race,profession,_) in CreationClasses)
        {
            string classObject=CreationClassMount(race,profession).ToLowerInvariant()+"03";
            if(_creationClassShipParts.ContainsKey(classObject))continue;
            yield return () =>
            {
                var authored=new List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)>();
                AddCreationAggregateParts(classObject,Matrix4x4.Identity,0,authored,[]);
                _creationClassShipParts.Add(classObject,authored);
            };
        }
    }

    private void QueueCreationShipGpuPrewarm()
    {
        if(Bounds.Width<2 || Bounds.Height<2)return;
        var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
        _creationAvatar=EnbRetailCharacterCreationDefaults.Avatar(definitions,0,0,0);
        _creationShip=EnbRetailCharacterCreationDefaults.Ship(0,0).ToArray();
        _creationPlanetFromAngle=_creationPlanetToAngle=20f;
        _creationPlanetSpinTick=0;
        _creationClassShipSpinTick=TickCount();
        _creationDialTick=TickCount();
        double scaling=RenderScaling;
        var orientation=Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
        if(!EnbResolvedRenderCamera.TryCreateOrthographic(new(0,0,1000),orientation,
            new((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1),320,240,
            new(1,2000),out var camera))return;
        var draws=ImmutableArray.CreateBuilder<EnbFrontendDraw>();
        RenderCreationShip(draws,camera,true);
        _gpu.QueueFrontendCreationPrewarm(draws.ToImmutable());
    }

    public void ShowCreation(int slot, IReadOnlyList<Net7GalaxyRoutingRecord> routes)
    {
        _creationName.Editor.InsertMode=_creationShipName.Editor.InsertMode=true;
        _creationFadeCover.IsVisible=false;
        if (slot is < 0 or >= Net7AvatarRosterCodec.SlotCount || ! _characters[slot].IsEmpty)
            throw new InvalidOperationException("Creation requires an empty roster slot.");
        _creationPreserveRosterLabels=_screen=="characters";
        _creationSlot = slot;
        _creationRoutes = routes;
        _creationRoute = 0;
        _creationPage = CreationPage.Profession;
        _avatarTab = _shipTab = _creationColor = _creationDecalPage = 0;
        _creationYaw=0; _creationShipRotation=Quaternion.Identity;_creationShipSpinAngle=0;
        _creationShipRadius=_creationShipDistance=_creationShipSeedDistance=0;
        _creationShipDragVelocity=Vector2.Zero;_creationShipMotionTick=0;_creationShipFrameDelta=0;_creationZoom=1;
        _creationPitch=_creationPanX=_creationPanY=0;
        _creationDragging=false;
        _creationBodyDragging=false;
        _creationSkillOffset=0; _creationSkillDragging=false;
        _creationPlanetFromAngle=_creationPlanetToAngle=20f; _creationPlanetSpinTick=0;
        _creationClassShipSpinTick=TickCount();
        _creationCompletionRequested=_creationCompleting=false;
        _creationNameEntering=_creationNameReturning=false;
        _creationNameClip=null;_creationNameClock=null;
        _skipBeginnerTutorial = false;
        _screen = "creation";
        _creationPendingSlot = -1;
        _selected = -1;
        _creationName.Text = "";
        _creationShipName.Text = "";
        _creationTextEditing=false;
        _creationShipName.IsEnabled=false;
        SetCreationProfession(0, 0);
        Username.IsVisible = Password.IsVisible = false;
        _creationName.IsVisible = false;
        _creationShipName.IsVisible = false;
        _creationSkillTrackVisual.IsVisible = _creationSkillThumbVisual.IsVisible = false;
        LoadCreationPage();
        _gpu.QueueCreatorArrivalSound();
        SpeakCreationResources([..TakeCreationIntro("ClassSelectionIntro"),344,345,346]);
    }

    private void SetCreationProfession(uint race, uint profession)
    {
        // client.exe 00B7DB60: (race, profession, planet angle) in UI order.
        float target=CreationClasses.First(c=>c.Race==race && c.Profession==profession).Angle;
        float current=CreationPlanetAngle();
        float delta=target-current;
        while(delta>180)delta-=360;
        while(delta< -180)delta+=360;
        if(MathF.Abs(delta)>.001f)
        {
            _creationPlanetFromAngle=current;
            _creationPlanetToAngle=current+delta;
            _creationPlanetSpinTick=TickCount();
            _gpu.QueueFrontendSound("planet_spin.wav");
        }
        var definitions = EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory, "avatardef.ini"));
        uint gender = _creationAvatar?.Gender ?? 0;
        _creationOtherGender=null;_creationSavedAvatarPalette=null;
        _creationAvatar = EnbRetailCharacterCreationDefaults.Avatar(definitions, race, profession, gender);
        _creationShip = EnbRetailCharacterCreationDefaults.Ship(race, profession).ToArray();
        _creationPortrait = null;
        _creationAppearance = null;
        _creationBodyX=_creationBodyY=.5f;
        AttachCreationClassActors(race,profession);
        if(_creationPage==CreationPage.Profession)ResetCreationEntrance();
    }

    private void AttachCreationClassActors(uint race,uint profession)
    {
        for(int previewGender=0;previewGender<2;previewGender++)
        {
            var actor=_creationClassActors[(race,profession,previewGender)];
            _creationClassIdles[previewGender]=actor;
            _creationClassAppearances[previewGender]=actor.Appearance;
            actor.Playback.Idle.StandStill=false;
            actor.Playback.Idle.SuppressFidgets=true;
            actor.Playback.Pause(TickCount()*.001);
            actor.Playback.RequestIdleSelection();
        }
    }

    private float CreationPlanetAngle()
    {
        if(_creationPlanetSpinTick==0)return _creationPlanetToAngle;
        float elapsed=Math.Clamp((TickCount()-_creationPlanetSpinTick)/1000f,0,1);
        if(elapsed>=1){_creationPlanetSpinTick=0;return _creationPlanetToAngle;}
        float eased=(1-MathF.Cos(elapsed*MathF.PI))*.5f;
        return _creationPlanetFromAngle+(_creationPlanetToAngle-_creationPlanetFromAngle)*eased;
    }

    private string CreationModel => _creationPage switch
    {
        CreationPage.Profession => "fe_class00",
        CreationPage.Avatar => "fe_avatar00",
        CreationPage.Ship => $"fa_ship{_shipTab + 1:00}",
        _ => "fe02a"
    };
    private string CreationLayout => _creationPage switch
    {
        CreationPage.Profession => "fa_fe_class00.ini",
        CreationPage.Avatar => "fa_fe_avatar00.ini",
        CreationPage.Ship => $"fa_ship{_shipTab + 1:00}.ini",
        _ => "fa_fe02a.ini"
    };
    private void LoadCreationPage()
    {
        if(_creationPage==CreationPage.Profession)SpeakCreationResources(Array.Empty<int>());
        else if(_creationPage==CreationPage.Avatar)SpeakCreationIntro("AvatarCreationIntro");
        else if(_creationPage==CreationPage.Ship && _shipTab!=0)
            SpeakCreationIntro(_shipTab switch {1=>"ShipCreationColor",2=>"ShipCreationDecal",_=>"ShipCreationName"});
        _creationPageTick=TickCount();
        if(_creationPage==CreationPage.Profession)
        {
            AttachCreationClassActors(_creationAvatar!.Race,_creationAvatar.Profession);
            ResetCreationEntrance();
        }
        _creationTooltipGpu.SetTooltipPresentation(null);
        _creationTooltipDelay.Reset();_creationTooltipSkill="";_creationTooltipRenderedKey="";
        _creationTextEditing=false;
        _creationShipName.IsEnabled=false;
        _creationDragging=false;
        _creationBodyDragging=false;
        _creationPaletteDragging=false;
        _creationSkillDragging=false;
        if(_creationPage==CreationPage.Ship && _shipTab==2 && _creationShip is not null)
        {
            // 00625CBA page 2 seeds +0x260 from the selected +0x264,
            // clamped so ten consecutive swatches remain visible.
            uint selected=BinaryPrimitives.ReadUInt32LittleEndian(_creationShip.AsSpan(16,4));
            var choices=_assets.Decals?.NormalTextures;
            int index=selected==0x07ffffffu ? 0 : unchecked((int)selected)+1;
            _creationDecalPage=Math.Clamp(index,0,Math.Max(0,(choices?.Count??0)+1-10));
        }
        _layout = EnbInterfaceLayoutCatalog.Load(Path.Combine(_assets.Paths.ClientIniDirectory, CreationLayout));
        Model(CreationModel);
        if(_creationPage==CreationPage.Avatar)
        {
            // 00553AF0 constructs a fresh opposite-gender history per editor.
            _creationOtherGender=null;
            if(_creationSavedAvatarPalette is {} savedPalette)
                foreach(var item in savedPalette)_creationPalettePoints[item.Key]=item.Value;
            _creationAvatarIdleTag="breathe3";
            _creationPortrait=null;
            _creationAvatarPlayback=null;
        }
        if(!_creationPreserveRosterLabels)ClearLabels();
        _creationName.IsVisible = _creationPage == CreationPage.Name;
        _creationShipName.IsVisible = _creationPage == CreationPage.Ship && _shipTab == 3;
        if(_creationPage==CreationPage.Name)
        {
            // 615A50 ->67A6C0 constructs the narrator with /walk_on.
            // 616A10 ->67A670 releases it on return to the ship editor.
            ResetNarrator();
            _creationNameEntering=true;
            _creationNameReturning=false;
            _creationName.IsEnabled=false;
            if(!StartCreationNameAnimation("fe02a-1",false))CompleteCreationNameTransition();
            _gpu.QueueFrontendSound("fontshoosh.wav");
        }
        foreach(var decalImage in _creationDecalImages)decalImage.IsVisible=false;
        _creationSkillTrackVisual.IsVisible = _creationSkillThumbVisual.IsVisible = false;
    }

    private void PreviousCreationPage()
    {
        if(CreationNameMoving)return;
        switch (_creationPage)
        {
            case CreationPage.Profession:
                _creationName.IsVisible = false;
                ShowCharacters(_characters);
                break;
            case CreationPage.Avatar:
                _creationPage = CreationPage.Profession;
                LoadCreationPage();
                _gpu.QueueCreatorArrivalSound();
                break;
            case CreationPage.Ship:
                _creationPage = CreationPage.Avatar;
                // The ship preview and avatar preview use different distance scales.
                ResetCreationAvatarView();
                LoadCreationPage();
                break;
            case CreationPage.Name:
                _creationNameReturning=true;
                _creationName.IsEnabled=false;
                _speech.Clear();_speechActive=false;_narratorSpeech?.Clear();_gpu.SetFrontendSpeech(null);
                ChangeNarrator("walk_off");
                if(!StartCreationNameAnimation("fe02a-1",true))CompleteCreationNameTransition();
                break;
        }
    }

    private void NextCreationPage()
    {
        if(CreationNameMoving)return;
        if (_creationPage == CreationPage.Name)
        {
            if (_creationRoutes.Count == 0) { SetStatus("No galaxy is available for character creation."); return; }
            // Retail seeds the text widget with "Click to set Name" (00615A50,
            // 006AA570), while our authored prompt is visual only. Do not turn
            // repeated Next presses into an empty account create transaction.
            if (string.IsNullOrEmpty(_creationName.Text))
            {
                BeginCreationNameEditing();
                return;
            }
            // Retail submits the editor contents; the server owns the named
            // validation reasons (taken, characters, length, vowels, repeats,
            // reserved words) and returns their distinct GlobalError codes.
            string name = EnbFrontendUi.NormalizeCharacterName(_creationName.Text ?? "");
            _creationName.Text=name;
            var avatar = _creationAvatar! with { FirstName = name, LastName = "" };
            var shipName = (_creationShipName.Text ?? "").Trim();
            var encodedShipName = System.Text.Encoding.Latin1.GetBytes(shipName);
            _creationShip!.AsSpan(20, 26).Clear();
            encodedShipName.AsSpan(0, Math.Min(encodedShipName.Length, 25)).CopyTo(_creationShip.AsSpan(20, 25));
            CreateRequested?.Invoke(new(_creationRoutes[_creationRoute].ShardId, _creationSlot,
                (byte)(_skipBeginnerTutorial ? 1 : 0), avatar, new(_creationShip!)));
            return;
        }
        if(_creationPage==CreationPage.Profession)
        {
            _speech.Clear();NextSpeech();
            // Returning through Previous constructs a fresh retail editor.
            var selected=_creationAvatar!;
            SetCreationProfession(selected.Race,selected.Profession);
            SetCreationGender(0);
            _avatarTab=_creationColor=0;
            _creationYaw=_creationPitch=_creationPanX=_creationPanY=0;
            _creationZoom=3;
            _creationPalettePoints.Clear();
            InitializeCreationAvatarPalette();
            _creationSavedAvatarPalette=null;
        }
        if(_creationPage==CreationPage.Avatar)
            _creationSavedAvatarPalette=CreationAvatarPalette();
        _creationPage++;
        if(_creationPage==CreationPage.Ship)
        {
            // A fresh ship editor is constructed after returning from avatar
            // customization; switching tabs only rebuilds that editor's UI.
            _creationShip=EnbRetailCharacterCreationDefaults.Ship(
                _creationAvatar!.Race,_creationAvatar.Profession).ToArray();
            _creationShipName.Text="";
            _creationShipName.IsEnabled=false;
            _shipTab=_creationColor=_creationDecalPage=0;
            _creationPalettePoints.Clear();
            // 004F5FB0 supplies the racial UV pair; 004F6230 repeats it for
            // every part. Retail 00625260 places the marker from those UVs.
            // This dictionary uses top-left screen coordinates, so invert V.
            for(int part=0;part<4;part++)
            for(int channel=0;channel<2;channel++)
            {
                var (u,v)=EnbRetailCharacterCreationDefaults.ShipPaintPaletteUv(_creationAvatar.Race,channel);
                _creationPalettePoints[$"{CreationPage.Ship}/{part*2+channel}"]=new(u,1-v);
            }
            _creationDialTick=TickCount();
            _creationShipRadius=_creationShipDistance=_creationShipSeedDistance=0;
            // A new ShipPIP/FloatParent is built on each avatar-to-ship entry.
            _creationShipRotation=Quaternion.Identity;
            _creationShipDragVelocity=Vector2.Zero;
            _creationDragging=false;
            _creationShipSpinAngle=0;_creationShipMotionTick=0;_creationShipFrameDelta=0;
            SpeakCreationIntro("ShipCreationGeometry");
        }
        LoadCreationPage();
    }

    public Task ShowCreationNameErrorAsync(Net7GlobalError error)
    {
        _creationTextEditing=false;
        _creationName.Text="";
        SetStatus("");
        string? cue=error.Code switch
        {
            8 => "NameTaken",
            9 => "NameWithNumbers",
            10 => "NameTooShort",
            11 => "NameNoVowels",
            12 => "NameRepeatCharacters",
            13 => "NameRestricted",
            _ => null,
        };
        if(cue is not null)Speak(cue);
        return ShowMessageAsync(error.Message);
    }

    private void BeginCreationNameEditing()
    {
        if(CreationNameMoving)return;
        _creationTextEditing=true;
        _creationName.Text="";
        _creationName.Focus();
    }

    private void CreationClick(string id,EnbUiPoint position)
    {
        if(CreationNameMoving)return;
        id = id.ToUpperInvariant();
        if(_creationPage==CreationPage.Name && id=="SKIP" && !_introductoryTutorialCompleted)return;
        if (id is "EXIT" or "CANCEL") { PreviousCreationPage(); return; }
        if (id is "CONFIRM" or "BUTTON_CANCEL") { NextCreationPage(); return; }
        if (_creationPage == CreationPage.Profession)
        {
            if (id is "SKILL_UP" or "SKILL_DOWN")
            {
                ScrollCreationSkills(id=="SKILL_UP"?-1:1);
                return;
            }
            if (id is "SKILL_TRACK" or "SKILL_THUMB")
            {
                SetCreationSkillScroll(position);
                return;
            }
            // 00575660 commands 0x11..0x18: click cue (queued by the input path)
            // and bit 0x40 on the row widget; no selection change.
            if (id.Length==7 && id.StartsWith("SKILL",StringComparison.Ordinal) &&
                int.TryParse(id.AsSpan(5),out int row) && row is >=0 and <8)
                return;
            (uint Race,uint Profession)[] order=[(0,0),(0,2),(0,1),(2,2),(2,1),(2,0),(1,0),(1,1),(1,2)];
            (uint Race, uint Profession)? choice = id switch
            {
                "ENFORCER" => (0, 0), "TRADESMAN" => (0, 1), "SCOUT" => (0, 2),
                "WARRIOR" => (2, 0), "PRIVATEER" => (2, 1), "SENTINEL" => (2, 2),
                "DEFENDER" => (1, 0), "SEEKER" => (1, 1), "EXPLORER" => (1, 2),
                _ => null
            };
            if(id is "SHIRT_STYLE_LEF" or "SHIRT_STYLE_RIG")
            {
                int current=Array.FindIndex(order,p=>p.Race==_creationAvatar!.Race && p.Profession==_creationAvatar.Profession);
                choice=order[(current+(id=="SHIRT_STYLE_RIG"?1:order.Length-1))%order.Length];
            }
            if (choice is {} selected && (_creationAvatar!.Race!=selected.Race || _creationAvatar.Profession!=selected.Profession))
            {
                SetCreationProfession(selected.Race, selected.Profession);
                _creationSkillOffset=0;
                SpeakSelectedProfession(selected.Race,selected.Profession);
            }
        }
        else if (_creationPage == CreationPage.Avatar)
        {
            var a = _creationAvatar!;
            switch (id)
            {
                case "TURN_LEFT": _creationYaw+=.0349f; break;
                case "TURN_RIGHT": _creationYaw-=.0349f; break;
                case "TURN_UP": _creationPitch=Math.Clamp(_creationPitch-.12f,-.6f,.6f); break;
                case "TURN_DOWN": _creationPitch=Math.Clamp(_creationPitch+.12f,-.6f,.6f); break;
                case "TURN": _creationYaw=_creationPitch=0; break;
                case "PAN_LEFT": MoveCreationAvatarPan(-1,0,1f/60); break;
                case "PAN_RIGHT": MoveCreationAvatarPan(1,0,1f/60); break;
                case "PAN_UP": MoveCreationAvatarPan(0,-1,1f/60); break;
                case "PAN_DOWN": MoveCreationAvatarPan(0,1,1f/60); break;
                case "PAN": _creationPanX=_creationPanY=0; _creationZoom=_avatarTab==1?.75f:3f; break;
                case "ZOOM_IN": MoveCreationAvatarZoom(-1,1f/60); break;
                case "ZOOM_OUT": MoveCreationAvatarZoom(1,1f/60); break;
                case "GENDER_TAB":
                    // Retail toggles on every activation, including the first
                    // click from another customization pane.
                    SetCreationGender(a.Gender == 0 ? 1u : 0u);
                    _avatarTab=0;
                    ResetCreationAvatarView();
                    SpeakCreationResources(_creationAvatar!.Gender==0?349:350);
                    break;
                case "HEAD_TAB": _avatarTab = 1; _creationColor=0; ResetCreationAvatarView(); SpeakCreationIntro("AvatarCreationHead"); break;
                case "BODY_TAB": _avatarTab = 2; _creationColor=3; ResetCreationAvatarView(); SpeakCreationIntro("AvatarCreationBody"); break;
                case "RANDOM": RandomizeCreationAvatar(); break;
                case "MOVE_LEFT": ApplyCreationBodyMorph(_creationBodyX-.05f,_creationBodyY); break;
                case "MOVE_RIGHT": ApplyCreationBodyMorph(_creationBodyX+.05f,_creationBodyY); break;
                case "MOVE_UP": ApplyCreationBodyMorph(_creationBodyX,_creationBodyY+.05f); break;
                case "MOVE_DOWN": ApplyCreationBodyMorph(_creationBodyX,_creationBodyY-.05f); break;
                case "HAIR_LEFT": case "HAIR_RIGHT":
                    _creationAvatar = a with { HairNum = CycleAvatarOption("HAIR", a.HairNum, id.EndsWith("RIGHT")) }; break;
                case "BEARD_LEFT": case "BEARD_RIGHT":
                    _creationAvatar = a with { BeardNum = CycleAvatarOption("BEARD", a.BeardNum, id.EndsWith("RIGHT")) }; break;
                case "EYE_LEFT": case "EYE_RIGHT":
                    _creationAvatar = a with { GoggleNum = CycleAvatarOption("GOGGLE", a.GoggleNum, id.EndsWith("RIGHT")) }; break;
                case "EAR_LEFT": case "EAR_RIGHT":
                    _creationAvatar = a with { EarNum = CycleAvatarOption("EAR", a.EarNum, id.EndsWith("RIGHT")) }; break;
                case "SHIRT_STYLE_LEF": case "SHIRT_STYLE_RIG":
                    _creationAvatar = a with { BodyType = (byte)((a.BodyType + (id.EndsWith("RIG") ? 1 : 2)) % 3) }; break;
                case "PANT_STYLE_LEFT": case "PANT_STYLE_RIGH":
                    _creationAvatar = a with { PantsType = (byte)((a.PantsType + (id.EndsWith("RIGH") ? 1 : 2)) % 3) }; break;
                case "HEAD_TYPE_LEFT": case "HEAD_TYPE_RIGHT":
                    _creationAvatar = a with { HeadType = (byte)((a.HeadType + (id.EndsWith("RIGHT") ? 1 : 2)) % 3) }; break;
                case "FACE_LEFT": case "FACE_RIGHT":
                    _creationAvatar = a with { HeadTextureNum = (byte)((a.HeadTextureNum + (id.EndsWith("RIGHT") ? 1 : 2)) % 3) }; break;
                case "TATTOO_LEFT": case "TATTOO_RIGHT":
                    SetCreationTattoo(CycleAvatarOption("TATTOO",a.TattooTextureNum,id.EndsWith("RIGHT"))); break;
                case "SCALE_IN": ScaleCreationTattoo(false,1f/60); break;
                case "SCALE_OUT": ScaleCreationTattoo(true,1f/60); break;
                case "COLOR_PALETTE":
                    if(TryCreationPaletteColor(position,_creationColor==2?"fa_skin_color.tga":"fa_avatar_color.tga",out var avatarColor))
                    {
                        float[] rgb=[avatarColor.X,avatarColor.Y,avatarColor.Z];
                        _creationAvatar=_creationColor switch
                        {
                            0=>a with {HairColor=rgb,BeardColor=(float[])rgb.Clone()},1=>a with {EyeColor=rgb},2=>a with {SkinColor=rgb},
                            3=>a with {ShirtPrimaryColor=rgb,ShirtPrimaryMetal=uint.MaxValue},
                            4=>a with {ShirtSecondaryColor=rgb,ShirtSecondaryMetal=uint.MaxValue},
                            5=>a with {PantsPrimaryColor=rgb,PantsPrimaryMetal=uint.MaxValue},
                            _=>a with {PantsSecondaryColor=rgb,PantsSecondaryMetal=uint.MaxValue}
                        };
                    }
                    break;
                case "METAL00": case "METAL01": case "METAL02":
                    uint metal=(uint)(id[^1]-'0');
                    _creationAvatar=_creationColor switch
                    {
                        3=>a with {ShirtPrimaryMetal=metal},4=>a with {ShirtSecondaryMetal=metal},
                        5=>a with {PantsPrimaryMetal=metal},6=>a with {PantsSecondaryMetal=metal},_=>a
                    };
                    break;
                default:
                    if (id is "HAIR_COLOR" or "SKIN_COLOR" or "EYE_COLOR" or "P_SHIRT_COLOR" or "S_SHIRT_COLOR" or "P_PANT_COLOR" or "S_PANT_COLOR")
                        _creationColor = id switch { "HAIR_COLOR" => 0, "EYE_COLOR" => 1, "SKIN_COLOR" => 2,
                            "P_SHIRT_COLOR" => 3, "S_SHIRT_COLOR" => 4, "P_PANT_COLOR" => 5, _ => 6 };
                    break;
            }
            if (!ReferenceEquals(a, _creationAvatar))
            {
                _creationAppearance = null;
                // Material and tattoo edits update draw uniforms without replacing
                // the animated actor. Rebuilding it here restarts the idle clip.
                if (id is not ("TATTOO_LEFT" or "TATTOO_RIGHT" or "SCALE_IN" or "SCALE_OUT" or
                    "COLOR_PALETTE" or "METAL00" or "METAL01" or "METAL02"))
                {
                    _creationPortrait = null;
                    // Accessory swaps replace the actor attachment but do not
                    // reframe the retail preview camera around its new bounds.
                    if (id is "HAIR_LEFT" or "HAIR_RIGHT" or "BEARD_LEFT" or "BEARD_RIGHT" or
                        "EYE_LEFT" or "EYE_RIGHT" or "EAR_LEFT" or "EAR_RIGHT")
                        _creationPreserveBoundsForMorph = true;
                }
            }
        }
        else if (_creationPage == CreationPage.Ship)
        {
            switch (id)
            {
                case "RANDOM":
                    if(_shipTab==0)RandomizeCreationShip();
                    else if(_shipTab==1)RandomizeCreationShipPaint();
                    else if(_shipTab==2)RandomizeCreationShipDecal();
                    else RandomizeCreationShipNameColor();
                    break;
                case "TURN_LEFT": case "TURN_RIGHT": case "TURN_UP": case "TURN_DOWN":
                case "PAN_LEFT": case "PAN_RIGHT": case "PAN_UP": case "PAN_DOWN":
                case "ZOOM_IN": case "ZOOM_OUT": break;
                case "TURN":
                    _creationShipRotation=Quaternion.Identity;
                    _creationShipDragVelocity=Vector2.Zero;
                    // ShipPIP +0x8c -> 0080E350 also restores mode-0 distance.
                    _creationShipDistance=_creationShipSeedDistance;
                    break;
                case "PAN": _creationPanX=_creationPanY=0;break;
                case "HULL_TAB": _shipTab = 0; LoadCreationPage(); break;
                case "PAINT_TAB": _shipTab = 1; LoadCreationPage(); break;
                case "INSIGNIA_TAB": _shipTab = 2; LoadCreationPage(); break;
                case "NAME_TAB": _shipTab = 3; LoadCreationPage(); break;
                case "HULL_LEFT": ChangeShipStyle(8,false); break;
                case "HULL_RIGHT": ChangeShipStyle(8,true); break;
                case "WING_LEFT": ChangeShipStyle(12,false); break;
                case "WING_RIGHT": ChangeShipStyle(12,true); break;
                case "PRIMARY": _creationColor=(_creationColor/2)*2; break;
                case "SECONDARY": _creationColor=(_creationColor/2)*2+1; break;
                case "HULL": _creationColor=_creationColor%2; break;
                case "WING": _creationColor=2+_creationColor%2; break;
                case "PROFESSION": _creationColor=4+_creationColor%2; break;
                case "ENGINE": _creationColor=6+_creationColor%2; break;
                case "GLOSSY": case "FLAT":
                    if(!CreationShipFinishDisabled())
                        _creationShip![58+_creationColor*17+12]=(byte)(id=="GLOSSY"?1:0);
                    break;
                case "METAL00": case "METAL01": case "METAL02":
                    BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(58+_creationColor*17+13,4),(uint)(id[^1]-'0'));break;
                case "COLOR_PALETTE":
                    if(_shipTab==3 && TryCreationPaletteColor(position,"fa_name_color.tga",out var nameColor))
                    {
                        for(int channel=0;channel<3;channel++)
                            BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(46+channel*4,4),
                                BitConverter.SingleToUInt32Bits(channel switch {0=>nameColor.X,1=>nameColor.Y,_=>nameColor.Z}));
                    }
                    else if(_shipTab==1 && TryCreationPaletteColor(position,"fa_ship_color.tga",out var shipColor))
                    {
                        int at=58+_creationColor*17;
                        var channels = new[]{shipColor.X,shipColor.Y,shipColor.Z};
                        for(int channel=0;channel<channels.Length;channel++)
                            BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(at+channel*4,4),BitConverter.SingleToUInt32Bits(channels[channel]));
                        BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(at+13,4),uint.MaxValue);
                    }
                    break;
                case "NAME":
                    _creationTextEditing=true;
                    _creationShipName.IsEnabled=true;
                    _creationShipName.Focus();
                    _creationShipName.CaretIndex=(_creationShipName.Text??string.Empty).Length;
                    break;
                case "LEFT": _creationDecalPage=Math.Max(0,_creationDecalPage-1);break;
                case "RIGHT":
                    _creationDecalPage=Math.Min(Math.Max(0,(_assets.Decals?.NormalTextures.Count??0)+1-10),_creationDecalPage+1);
                    break;
                default:
                    if(id.Length==3 && id[0]=='D' && int.TryParse(id.AsSpan(1),out int decal))
                    {
                        var choices=_assets.Decals?.NormalTextures.Select(d=>(uint)d.DecalId).ToArray()??[];
                        int index=_creationDecalPage+decal;
                        if(index==0)BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(16,4),0x07ffffffu);
                        else if(index-1<choices.Length)BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(16,4),choices[index-1]);
                    }
                    break;
            }
        }
        else if (_creationPage == CreationPage.Name)
        {
            if (id == "SKIP") _skipBeginnerTutorial = !_skipBeginnerTutorial;
            else if (id == "NAME") BeginCreationNameEditing();
            else if (id.StartsWith("BUTTON_SHARD", StringComparison.Ordinal) &&
                int.TryParse(id.AsSpan(12), out int route) && route < _creationRoutes.Count) _creationRoute = route;
        }
    }

    private bool TryBeginCreationDrag(EnbUiPoint position)
    {
        if (_creationPage is not (CreationPage.Avatar or CreationPage.Ship) ||
            !Screen(new(240,125,270,315)).Contains(position)) return false;
        _creationDragging=true;
        _creationLastDrag=position;
        _creationShipDragVelocity=Vector2.Zero;
        return true;
    }

    private void DragCreation(EnbUiPoint position)
    {
        if (!_creationDragging || Bounds.Width<=0 || Bounds.Height<=0)return;
        var previous=_creationLastDrag;
        var delta=position-previous;
        _creationLastDrag=position;
        float dx=(float)(2*delta.X/Bounds.Width);
        float yaw=MathF.Asin(Math.Clamp(dx/.8f,-1,1));
        if (_creationPage==CreationPage.Avatar) _creationYaw-=yaw;
        else
        {
            ApplyCreationShipTurn(CreationShipTrackball(previous,position));
            // 0080D7B8-0080D81B divides by the global frame-clock delta
            // (00BFAE3C-00BFAE40), not elapsed time between pointer events.
            float dt=_creationShipFrameDelta;
            if(dt>0)
            {
                // 0080D830-0080D892 stores normalized pointer velocity, not
                // angular velocity. A stationary axis retains 0.66 of its
                // prior velocity on each drag event.
                float vx=(float)(2*delta.X/Bounds.Width);
                float vy=(float)(-2*delta.Y/Bounds.Height);
                _creationShipDragVelocity=new Vector2(
                    vx!=0?vx/dt:_creationShipDragVelocity.X*.66f,
                    vy!=0?vy/dt:_creationShipDragVelocity.Y*.66f);
                // 0080D898-0080D8BD clears both components below the
                // retail |vx|+|vy| < 1.0 threshold (00AE9AA0).
                if(MathF.Abs(_creationShipDragVelocity.X)+MathF.Abs(_creationShipDragVelocity.Y)<1f)
                    _creationShipDragVelocity=Vector2.Zero;
            }
        }
    }

    private Vector3 CreationShipTrackball(EnbUiPoint previous,EnbUiPoint current)
    {
        // 00543200 / 008D1CE0: the installed retail preview's sphere joined to
        // a hyperbolic sheet. ShipPIP orbits the camera in forward/left/up
        // coordinates; map the screen right/up/back axis to that basis just as
        // the equipment preview does.
        Vector3 Project(float x,float y)
        {
            float distance=MathF.Sqrt(x*x+y*y);
            float z=distance<.8f*MathF.Sqrt(.5f)?MathF.Sqrt(.64f-distance*distance):.32f/distance;
            return new(x,y,z);
        }
        var from=Project((float)(2*previous.X/Bounds.Width-1),(float)(1-2*previous.Y/Bounds.Height));
        var to=Project((float)(2*current.X/Bounds.Width-1),(float)(1-2*current.Y/Bounds.Height));
        return CreationShipTrackball(from,to);
    }

    private static Vector3 CreationShipTrackball(Vector2 displacement)
    {
        // 0080D1DE-0080D232 invokes the same trackball with origin (0,0)
        // and the damped screen-space velocity times the elapsed frame time.
        Vector3 from=new(0,0,.8f);
        float x=displacement.X,y=displacement.Y;
        float distance=displacement.Length();
        float z=distance<.8f*MathF.Sqrt(.5f)?MathF.Sqrt(.64f-distance*distance):.32f/distance;
        return CreationShipTrackball(from,new Vector3(x,y,z));
    }

    private static Vector3 CreationShipTrackball(Vector3 from,Vector3 to)
    {
        var axis=Vector3.Cross(to,from);
        if(axis.LengthSquared()<1e-16f)return Vector3.Zero;
        float angle=2*MathF.Asin(Math.Clamp(Vector3.Distance(from,to)/1.6f,0,1));
        axis=Vector3.Normalize(new Vector3(-axis.Z,-axis.X,axis.Y));
        return axis*angle;
    }

    private void ApplyCreationShipTurn(Vector3 turn)
    {
        float angle=turn.Length();
        if(angle>1e-7f)_creationShipRotation=Quaternion.Normalize(_creationShipRotation*
            Quaternion.CreateFromAxisAngle(turn/angle,angle));
    }

    private void TickCreationShipMotion()
    {
        long now=TickCount();
        float dt=_creationShipMotionTick==0?0:Math.Max(0,(now-_creationShipMotionTick)*.001f);
        _creationShipMotionTick=now;
        _creationShipFrameDelta=dt;
        if(_creationPage!=CreationPage.Ship)return;
        // ShipPIP 0080CE10 spins the attached model about its Z axis. It does
        // not orbit the camera; pointer drag controls the camera separately.
        _creationShipSpinAngle=(_creationShipSpinAngle+.05f*dt)%MathF.Tau;
        if(_creationDragging)return;
        var displacement=_creationShipDragVelocity*dt;
        // 0080D1B4-0080D1D8 skips the inertial trackball when both
        // per-frame displacement components are below 0.0001 (00B07EA8).
        if(MathF.Abs(displacement.X)>=.0001f || MathF.Abs(displacement.Y)>=.0001f)
        {
            ApplyCreationShipTurn(CreationShipTrackball(displacement));
        }
        // 0080CFD2-0080CFF0 applies 0.99 once per ShipPIP update; it is
        // not a time-normalized decay exponent.
        _creationShipDragVelocity*=.99f;
    }

    private static bool IsCreationContinuousControl(string id)=>id is
        "TURN_LEFT" or "TURN_RIGHT" or "TURN_UP" or "TURN_DOWN" or
        "PAN_LEFT" or "PAN_RIGHT" or "PAN_UP" or "PAN_DOWN" or
        "ZOOM_IN" or "ZOOM_OUT" or "SCALE_IN" or "SCALE_OUT" or
        "MOVE_LEFT" or "MOVE_RIGHT" or "MOVE_UP" or "MOVE_DOWN";

    private bool CreationShipFinishDisabled() => _creationShip is null ||
        // 006277A0 calls gadget +0x40(1) for Glossy/Flat on the -1
        // (palette-color) branch; +0x40 is the enabled setter, as the
        // same function's decal-scroll branch proves.
        BinaryPrimitives.ReadUInt32LittleEndian(_creationShip.AsSpan(58+_creationColor*17+13,4))!=uint.MaxValue;

    private bool CreationClickHasSound(string id)
    {
        if(IsCreationContinuousControl(id))return false;
        if(_creationPage==CreationPage.Ship)
            return id is not ("LEFT" or "RIGHT" or "COLOR_PALETTE");
        if(_creationPage==CreationPage.Avatar)
            return id is "EXIT" or "CANCEL" or "CONFIRM" or "BUTTON_CANCEL" or "RANDOM"
                or "HEAD_TAB" or "BODY_TAB";
        return true;
    }

    private void ClampCreationAvatarPan()
    {
        // Retail ShipPIP mode 1 clamps the horizontal and vertical camera
        // offsets at 0080E020, independently of the zoom distance.
        _creationPanX=Math.Clamp(_creationPanX,-2f,2f);
        // 0080E020 clamps the *absolute* vertical camera offset. The reset
        // offset remains in that value, so available travel is asymmetric.
        float origin=CreationAvatarPanOrigin();
        _creationPanY=Math.Clamp(_creationPanY,-4.75f-origin,4.75f-origin);
    }

    private float CreationAvatarPanOrigin()=>_avatarTab==1?4+(_creationAvatarHeight-7.3f):2.5f;

    private void MoveCreationAvatarPan(float horizontal,float vertical,float elapsed)
    {
        // 0054E3C0 supplies +/-0.75 to ShipPIP's pan axes (00AEF674);
        // 0080D379/391 then integrate 0.6 * camera distance per second.
        float distance=_creationAvatarRadius*_creationZoom;
        _creationPanX+=horizontal*.75f*distance*.6f*elapsed;
        _creationPanY+=vertical*.75f*distance*.6f*elapsed;
        ClampCreationAvatarPan();
    }

    private void MoveCreationAvatarZoom(float direction,float elapsed)
    {
        // 0080D106..0080D197: mode 1 adds 0.6 * current distance * elapsed,
        // then clamps that distance. Retail uses a per-frame Euler step.
        // 0080B2E0 / 0080CA30: distance is clamped to 0.75..3 times radius.
        _creationZoom=Math.Clamp(_creationZoom+direction*.6f*_creationZoom*elapsed,.75f,3f);
    }

    private void ResetCreationAvatarView()
    {
        _creationYaw=_creationPitch=_creationPanX=_creationPanY=0;
        // 0080E350 starts the head view at the near sphere factor and the
        // full-body views at the far factor selected by 0080B2E0.
        _creationZoom=_avatarTab==1?.75f:3f;
    }

    private void BeginCreationControlHold(string id)
    {
        if(!IsCreationContinuousControl(id))return;
        _creationHeldControl=id;
        _creationHeldLastTick=TickCount();
        _creationHeldRepeatTick=_creationHeldLastTick+16;
    }

    private void EndCreationControlHold()
    {
        _creationHeldControl=null;
        _creationHeldLastTick=0;
        _creationHeldRepeatTick=0;
    }

    private void TickCreationControlHold()
    {
        if(_creationHeldControl is not {} id)return;
        long now=TickCount();
        // Retail's 0080CE10 consumes the clock delta on each scene update.
        // A separate 16 ms gate skips otherwise valid frames and makes motion
        // alternate between still and larger steps at common display rates.
        float elapsed=Math.Max(0,(now-_creationHeldLastTick)*.001f);
        _creationHeldLastTick=now;
        if(id is "MOVE_LEFT" or "MOVE_RIGHT" or "MOVE_UP" or "MOVE_DOWN")
        {
            if(now<_creationHeldRepeatTick)return;
            _creationHeldRepeatTick=now+16;
        }
        if(_creationPage==CreationPage.Avatar)
        {
            switch(id)
            {
                case "TURN_LEFT": _creationYaw+=2.18f*elapsed; return;
                case "TURN_RIGHT": _creationYaw-=2.18f*elapsed; return;
                case "PAN_LEFT": MoveCreationAvatarPan(-1,0,elapsed); return;
                case "PAN_RIGHT": MoveCreationAvatarPan(1,0,elapsed); return;
                case "PAN_UP": MoveCreationAvatarPan(0,-1,elapsed); return;
                case "PAN_DOWN": MoveCreationAvatarPan(0,1,elapsed); return;
                case "ZOOM_IN": MoveCreationAvatarZoom(-1,elapsed); return;
                case "ZOOM_OUT": MoveCreationAvatarZoom(1,elapsed); return;
                case "SCALE_IN": ScaleCreationTattoo(false,elapsed); return;
                case "SCALE_OUT": ScaleCreationTattoo(true,elapsed); return;
            }
        }
        if(_creationPage==CreationPage.Ship)
        {
            MoveCreationShipControl(id,elapsed);
            return;
        }
        CreationClick(id,_pointer);
    }

    private void MoveCreationShipControl(string id,float elapsed)
    {
        if(elapsed<=0 || _creationShipRadius<=0)return;
        // 00624270 supplies +/-1. 0080CE10 integrates the mode-0 ShipPIP
        // inputs against elapsed seconds, with a 60-unit/s pan and zoom rate.
        if(id is "TURN_LEFT" or "TURN_RIGHT" or "TURN_UP" or "TURN_DOWN")
        {
            var from=new EnbUiPoint(Bounds.Width*.5,Bounds.Height*.5);
            var to=new EnbUiPoint(from.X+(id=="TURN_RIGHT"?1:id=="TURN_LEFT"?-1:0)*Bounds.Width*.5*elapsed,
                from.Y-(id=="TURN_UP"?1:id=="TURN_DOWN"?-1:0)*Bounds.Height*.5*elapsed);
            ApplyCreationShipTurn(CreationShipTrackball(from,to));
            return;
        }
        switch(id)
        {
            case "PAN_LEFT": _creationPanX=Math.Clamp(_creationPanX-60*elapsed,-70,70);break;
            case "PAN_RIGHT": _creationPanX=Math.Clamp(_creationPanX+60*elapsed,-70,70);break;
            case "PAN_UP": _creationPanY=Math.Clamp(_creationPanY-60*elapsed,-47,53);break;
            case "PAN_DOWN": _creationPanY=Math.Clamp(_creationPanY+60*elapsed,-47,53);break;
            case "ZOOM_IN": _creationShipDistance=Math.Clamp(_creationShipDistance-60*elapsed,
                _creationShipRadius*.5f,_creationShipRadius*2.1f);break;
            case "ZOOM_OUT": _creationShipDistance=Math.Clamp(_creationShipDistance+60*elapsed,
                _creationShipRadius*.5f,_creationShipRadius*2.1f);break;
        }
    }

    private Dictionary<string,Vector2> CreationAvatarPalette()=>_creationPalettePoints
        .Where(p=>p.Key.StartsWith("Avatar/",StringComparison.Ordinal)).ToDictionary(p=>p.Key,p=>p.Value);
    private void SetCreationGender(uint gender)
    {
        var a=_creationAvatar!;
        if(a.Gender==gender)return;
        // 005582A0 saves the outgoing data and UI coordinates, then restores
        // the opposite gender or constructs its defaults on the first switch.
        var outgoing=new CreationGenderState(a,CreationAvatarPalette(),_creationBodyX,_creationBodyY);
        if(_creationOtherGender is {} saved && saved.Avatar.Gender==gender)
        {
            _creationAvatar=saved.Avatar;
            foreach(var item in saved.Palette)_creationPalettePoints[item.Key]=item.Value;
            _creationBodyX=saved.BodyX;_creationBodyY=saved.BodyY;
        }
        else
        {
            var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
            _creationAvatar=EnbRetailCharacterCreationDefaults.Avatar(definitions,a.Race,a.Profession,gender);
            _creationBodyX=_creationBodyY=.5f;
            InitializeCreationAvatarPalette();
        }
        _creationOtherGender=outgoing;
        // 0067A3F0/0067A440 replaces the actor, including its clocks.
        _creationAvatarPlayback=null;
        _creationPortrait=null;_creationAppearance=null;
    }
    private void InitializeCreationAvatarPalette()
    {
        var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
        foreach(int part in new[]{2,0,1,3,4,5,6})
        {
            var a=_creationAvatar!;
            var (u,v)=EnbRetailCharacterCreationDefaults.AvatarPaletteUv(definitions,a.Race,a.Profession,a.Gender,part);
            var image=_assets.Textures.Resolve(part==2?"fa_skin_color.tga":"fa_avatar_color.tga").Image;
            float[] rgb=[0,0,0];
            if(image is not null)
            {
                var pixel=image.GetPixel(Math.Clamp((int)(u*image.Width),0,image.Width-1),Math.Clamp((int)(v*image.Height),0,image.Height-1));
                rgb=[pixel.R/255f,pixel.G/255f,pixel.B/255f];
            }
            _creationAvatar=part switch
            {
                0=>a with {HairColor=rgb,BeardColor=(float[])rgb.Clone()},1=>a with {EyeColor=rgb},2=>a with {SkinColor=rgb},
                3=>a with {ShirtPrimaryColor=rgb},4=>a with {ShirtSecondaryColor=rgb},
                5=>a with {PantsPrimaryColor=rgb},_=>a with {PantsSecondaryColor=rgb}
            };
            _creationPalettePoints[$"Avatar/{part}"]=new(u,v);
        }
    }
    private void ApplyCreationBodyMorph(float x,float y)
    {
        // client.exe 0055A060/005860B0: the pad writes five unnormalized
        // blend weights to both the body and head descriptions.
        _creationBodyX=x=Math.Clamp(x,0,1);
        _creationBodyY=y=Math.Clamp(y,0,1);
        // 005860B0 uses the double at 00AE8728 for the normal weight.
        float center=.5f-Math.Max(MathF.Abs(x-.5f),MathF.Abs(y-.5f));
        float[] weights=[center,
            Math.Max(0,Math.Min(1-x,1-y)-center),
            Math.Max(0,Math.Min(x,1-y)-center),
            Math.Max(0,Math.Min(x,y)-center),
            Math.Max(0,Math.Min(1-x,y)-center)];
        uint[] bits=weights.Select(BitConverter.SingleToUInt32Bits).ToArray();
        _creationAvatar=_creationAvatar! with {BodyWeight=bits,HeadWeight=(uint[])bits.Clone()};
        _creationPortrait=null;_creationAppearance=null;
        // 0055A060 invokes ShipPIP +0x84 (0080CA30) after applying both
        // weight arrays. Re-sample the morphed actor sphere for camera distance,
        // while retaining the current view target. 0080CA30 does not translate
        // the camera matrix to the refreshed sphere center.
        _creationPreserveBoundsForMorph=false;
        _creationKeepCameraCenterForMorph=true;
    }
    private void SetCreationBodyFromPointer(EnbUiPoint position)
    {
        if(_avatarTab==1)
        {
            if(_creationAvatar is not {} avatar || !CreationTattooOptions(avatar.TattooTextureNum).Movable ||
                !_hits.TryGetValue("HEAD_AREA",out var headArea) || headArea.Width<=0 || headArea.Height<=0)return;
            var offset=(float[])avatar.TattooOffset.Clone();
            offset[0]=(float)Math.Clamp((position.X-headArea.X)/headArea.Width,0,1);
            offset[1]=(float)Math.Clamp((position.Y-headArea.Y)/headArea.Height,0,1);
            _creationAvatar=avatar with {TattooOffset=offset};
            _creationAppearance=null;
            return;
        }
        if(!_hits.TryGetValue("BODY_AREA",out var area) || area.Width<=0 || area.Height<=0)return;
        ApplyCreationBodyMorph((float)((position.X-area.X)/area.Width),
            1f-(float)((position.Y-area.Y)/area.Height));
    }
    private bool TryBeginCreationBodyDrag(string id,EnbUiPoint position)
    {
        if(_creationPage!=CreationPage.Avatar ||
            !(_avatarTab==2 && id is ("BODY_AREA" or "BODY_PULL") ||
              _avatarTab==1 && id is ("HEAD_AREA" or "HEAD_PULL") &&
              _creationAvatar is {} avatar && CreationTattooOptions(avatar.TattooTextureNum).Movable))return false;
        _creationBodyDragging=true;
        SetCreationBodyFromPointer(position);
        return true;
    }
    private (bool Movable,int Scale) CreationTattooOptions(byte tattoo)
    {
        var ini=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
        ini.TryGetSection($"FacialFeature-{tattoo:00}",out var section);
        bool movable=section?.GetValueOrDefault("Movable") is {} value &&
            (value.Equals("true",StringComparison.OrdinalIgnoreCase)||value=="1");
        int scale=int.TryParse(section?.GetValueOrDefault("Scale"),out int parsed)?parsed:1;
        return(movable,scale);
    }
    private void SetCreationTattoo(byte tattoo)
    {
        var avatar=_creationAvatar!;
        var options=CreationTattooOptions(tattoo);
        _creationAvatar=avatar with {TattooTextureNum=tattoo,
            TattooOffset=[options.Movable?avatar.TattooOffset[0]:.5f,
                options.Movable?avatar.TattooOffset[1]:.5f,
                options.Scale>1?Math.Clamp(avatar.TattooOffset[2],.5f,options.Scale):1]};
    }
    private void ScaleCreationTattoo(bool increase,float seconds)
    {
        var avatar=_creationAvatar!;
        var options=CreationTattooOptions(avatar.TattooTextureNum);
        if(options.Scale<=1)return;
        var offset=(float[])avatar.TattooOffset.Clone();
        offset[2]=Math.Clamp(offset[2]*MathF.Pow(increase?31f/30:29f/30,seconds*60),.5f,options.Scale);
        _creationAvatar=avatar with {TattooOffset=offset};
        _creationAppearance=null;
    }
    internal Func<int,int,int> CreationAvatarRandom = EnbRetailPresentationRandom.Shared.Next;
    private void RandomizeCreationAvatar()
    {
        // 00557290 preserves gender; mode 0 randomizes both groups, 1 only
        // head, 2 only clothing/morphs. Keep the native consumer draw order.
        byte Option(string kind,int minimum=0) => (byte)CreationAvatarRandom(minimum,CreationAvatarOptionMaximum(kind));
        if(_avatarTab is 0 or 1)
        {
            var a=_creationAvatar!;
            byte head=Option("HEAD"),hair=Option("HAIR",a.Gender==1?1:0);
            byte beard=a.Gender==1?(byte)0:Option("BEARD");
            byte goggles=Option("GOGGLE"),ears=Option("EAR");
            _creationAvatar=a with {HeadType=head,HairNum=hair,BeardNum=beard,GoggleNum=goggles,EarNum=ears};
            RandomizeCreationAvatarColor(2);
            RandomizeCreationAvatarColor(0);
            RandomizeCreationAvatarColor(1);
            _creationAvatar=_creationAvatar with {HeadTextureNum=Option("FACETEX")};
            byte tattoo=Option("TATTOO");
            // 00558170 special-cases feature 3; other selections use authored
            // Scale, including non-movable features (the random handler differs
            // from the manual placement control).
            float[] offset=tattoo==3 ? [.5f,.5f,1] :
                [CreationAvatarRandom(0,100)*.01f,1-CreationAvatarRandom(0,100)*.01f,
                 CreationAvatarRandom(5,CreationTattooOptions(tattoo).Scale*10)*.1f];
            _creationAvatar=_creationAvatar with {TattooTextureNum=tattoo,TattooOffset=offset};
        }
        if(_avatarTab is 0 or 2)
        {
            byte body=(byte)CreationAvatarRandom(0,2),pants=(byte)CreationAvatarRandom(0,2);
            _creationAvatar=_creationAvatar! with {BodyType=body,PantsType=pants};
            for(int part=3;part<=6;part++)RandomizeCreationAvatarColor(part);
            ApplyCreationBodyMorph(CreationAvatarRandom(0,100)*.01f,CreationAvatarRandom(0,100)*.01f);
        }
        _creationPortrait=null;_creationAppearance=null;
    }
    private int CreationAvatarOptionMaximum(string kind)
    {
        var a=_creationAvatar!;
        string prefix=(a.Gender==0?"AM":"AF")+new[]{"T","J","P"}[a.Race];
        int maximum=0;
        // 00585D10 probes successive model/texture names starting at 01.
        while(_assets.RetailMixResources?.TryLocateResource(kind switch
        {
            "TATTOO"=>$"atattoo{maximum+1:00}.tga",
            "FACETEX"=>$"{prefix}FACETEX{maximum+1:00}.tga",
            _=>$"{prefix}{kind}{maximum+1:00}.w3d"
        },out _)==true)maximum++;
        return maximum;
    }
    private void RandomizeCreationAvatarColor(int part)
    {
        // 00557950 -> 00496C00 -> 0094E490: sample the installed palette,
        // truncating normalized coordinates and clamping the upper edge.
        float u=CreationAvatarRandom(0,100)*.01f,v=CreationAvatarRandom(0,100)*.01f;
        var image=_assets.Textures.Resolve(part==2?"fa_skin_color.tga":"fa_avatar_color.tga").Image;
        float[] rgb=[0,0,0];
        if(image is not null)
        {
            var pixel=image.GetPixel(Math.Min((int)(u*image.Width),image.Width-1),Math.Min((int)(v*image.Height),image.Height-1));
            rgb=[pixel.R/255f,pixel.G/255f,pixel.B/255f];
        }
        uint metal=part>=3?unchecked((uint)CreationAvatarRandom(-1,2)):uint.MaxValue;
        var a=_creationAvatar!;
        _creationAvatar=part switch
        {
            0=>a with {HairColor=rgb,BeardColor=(float[])rgb.Clone()},1=>a with {EyeColor=rgb},2=>a with {SkinColor=rgb},
            3=>a with {ShirtPrimaryColor=rgb,ShirtPrimaryMetal=metal},
            4=>a with {ShirtSecondaryColor=rgb,ShirtSecondaryMetal=metal},
            5=>a with {PantsPrimaryColor=rgb,PantsPrimaryMetal=metal},
            _=>a with {PantsSecondaryColor=rgb,PantsSecondaryMetal=metal}
        };
        _creationPalettePoints[$"Avatar/{part}"]=new(u,v);
    }
    private void RandomizeCreationShip()
    {
        var ship=_creationShip!;
        BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(8,4),(uint)EnbRetailPresentationRandom.Shared.Next(0,2));
        BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(12,4),(uint)EnbRetailPresentationRandom.Shared.Next(0,2));
        ResetCreationShipViewForStyle();
    }
    private void RandomizeCreationShipPaint()
    {
        // 00627200, mode 1: choose a color-or-metal operation once per
        // primary/secondary channel, then apply it to all four ship parts.
        var ship=_creationShip!;
        var palette=_assets.Textures.Resolve("fa_ship_color.tga").Image;
        for(int channel=0;channel<2;channel++)
        {
            bool color=EnbRetailPresentationRandom.Shared.Next(0,1)==0;
            bool glossy=EnbRetailPresentationRandom.Shared.Next(0,1)!=0;
            uint metal=(uint)EnbRetailPresentationRandom.Shared.Next(0,2);
            int u=0,v=0;
            Vector3 rgb=default;
            if(color && palette is not null)
            {
                u=EnbRetailPresentationRandom.Shared.Next(0,100);v=EnbRetailPresentationRandom.Shared.Next(0,100);
                var pixel=palette.GetPixel(Math.Clamp((int)(u*.01f*palette.Width),0,palette.Width-1),
                    Math.Clamp((int)((1-v*.01f)*palette.Height),0,palette.Height-1));
                rgb=new(pixel.R/255f,pixel.G/255f,pixel.B/255f);
            }
            else if(color)continue; // Missing optional palette cannot invalidate the editor.
            for(int part=0;part<4;part++)
            {
                int field=part*2+channel,at=58+field*17;
                if(color)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(at,4),BitConverter.SingleToUInt32Bits(rgb.X));
                    BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(at+4,4),BitConverter.SingleToUInt32Bits(rgb.Y));
                    BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(at+8,4),BitConverter.SingleToUInt32Bits(rgb.Z));
                    ship[at+12]=(byte)(glossy?1:0);
                    BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(at+13,4),uint.MaxValue);
                    _creationPalettePoints[$"{CreationPage.Ship}/{field}"]=new(u*.01f,1-v*.01f);
                }
                else BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(at+13,4),metal);
            }
        }
    }
    private void RandomizeCreationShipDecal()
    {
        // 00627200, mode 2 picks one of the installed decal definitions.
        var choices=_assets.Decals?.NormalTextures;
        if(choices is null || choices.Count==0)return;
        int index=EnbRetailPresentationRandom.Shared.Next(0,choices.Count-1);
        BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(16,4),(uint)choices[index].DecalId);
        _creationDecalPage=Math.Min(index+1,Math.Max(0,choices.Count+1-10));
    }
    private void RandomizeCreationShipNameColor()
    {
        // 00627200 mode 3: sample the authored name palette and update the
        // name RGB; the ship's geometry and painted parts are unchanged.
        var image=_assets.Textures.Resolve("fa_name_color.tga").Image;
        if(image is null)return;
        int u=EnbRetailPresentationRandom.Shared.Next(0,100),v=EnbRetailPresentationRandom.Shared.Next(0,100);
        var pixel=image.GetPixel(Math.Clamp((int)(u*.01f*image.Width),0,image.Width-1),
            Math.Clamp((int)((1-v*.01f)*image.Height),0,image.Height-1));
        float[] rgb=[pixel.R/255f,pixel.G/255f,pixel.B/255f];
        for(int channel=0;channel<3;channel++)
            BinaryPrimitives.WriteUInt32LittleEndian(_creationShip!.AsSpan(46+channel*4,4),
                BitConverter.SingleToUInt32Bits(rgb[channel]));
        _creationPalettePoints[CreationPalettePointKey]=new(u*.01f,1-v*.01f);
    }
    private void SpeakSelectedProfession(uint race,uint profession)
    {
        // Current client.exe supplies the six retail sequences. EnbNext owns the
        // three additional profession sequences because no retail resources exist.
        string[]? sequence=(race,profession) switch
        {
            (0,0)=>Retail(344,345,346), (0,1)=>Retail(335,336,337),
            (0,2)=>Next("terran-scout"),
            (2,0)=>Retail(329,330,331), (2,1)=>Next("progen-privateer"),
            (2,2)=>Retail(338,339,340),
            (1,0)=>Retail(341,342,343), (1,1)=>Next("jenquai-seeker"),
            (1,2)=>Retail(332,333,334),
            _=>null
        };
        if(sequence is null){_speech.Clear();NextSpeech();}
        else SpeakCreationResources(sequence);

        static string[] Retail(params int[] ids)=>ids.Select(id=>$"1492_00_{id:000}se.mp3").ToArray();
        static string[] Next(string profession)=>Enumerable.Range(1,3)
            .Select(part=>EnbNextAudioAssetCatalog.CharacterCreation(profession,part)).ToArray();
    }
    private byte CycleAvatarOption(string kind, byte value, bool forward)
    {
        var a = _creationAvatar!;
        string prefix = (a.Gender == 0 ? "AM" : "AF") + new[] { "T", "J", "P" }[a.Race];
        int maximum = 0;
        while (maximum < 127 && _assets.RetailMixResources?.TryLocateResource(
            kind=="TATTOO"?$"atattoo{maximum+1:00}.tga":$"{prefix}{kind}{maximum + 1:00}.w3d", out _) == true) maximum++;
        if (maximum == 0) return 0;
        return (byte)(forward ? value >= maximum ? 0 : value + 1 : value == 0 ? maximum : value - 1);
    }
    private void ChangeShipStyle(int offset,bool forward)
    {
        var ship = _creationShip!;
        uint current = BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(offset, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(ship.AsSpan(offset, 4), (current + (forward ? 1u : 2u)) % 3);
        ResetCreationShipViewForStyle();
    }
    private void ResetCreationShipViewForStyle()
    {
        // Mode-0 style/Random handlers call ShipPIP +0x7C (0080C640), not
        // +0x84 (0080CA30). Full setup re-reads the assembled sphere, writes
        // NewShipScale, seeds the camera and installs the (0,-3) pan offset.
        // The FloatParent itself survives, retaining its ongoing idle spin.
        _creationShipRadius=_creationShipDistance=_creationShipSeedDistance=0;
        _creationShipRotation=Quaternion.Identity;
        _creationPanX=_creationPanY=0;
        _creationShipDragVelocity=Vector2.Zero;
    }
    private bool TryCreationPaletteColor(EnbUiPoint position,string texture,out Vector3 color)
    {
        color=default;
        if(!_hits.TryGetValue("COLOR_PALETTE",out var rect) || rect.Width<=0 || rect.Height<=0)return false;
        var image=_assets.Textures.Resolve(texture).Image;
        if(image is null)return false;
        int x=Math.Clamp((int)((position.X-rect.X)/rect.Width*image.Width),0,image.Width-1);
        int y=Math.Clamp((int)((position.Y-rect.Y)/rect.Height*image.Height),0,image.Height-1);
        _creationPalettePoints[CreationPalettePointKey]=new((x+.5f)/image.Width,(y+.5f)/image.Height);
        var pixel=image.GetPixel(x,y);
        color=new(pixel.R/255f,pixel.G/255f,pixel.B/255f);
        return true;
    }
    private string CreationPalettePointKey => _creationPage==CreationPage.Ship && _shipTab==3
        ? "Ship/Name" : $"{_creationPage}/{_creationColor}";

    private string[] CreationSkillNames()
    {
        var ini=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fa_fe_class00.ini"));
        if(!ini.TryGetSection($"CLASS-{_creationAvatar!.Race}{_creationAvatar.Profession}",out var section))return [];
        var names=new List<string>();
        for(int i=0;section.TryGetValue("Info"+i,out var name);i++)
            if(!string.IsNullOrWhiteSpace(name))names.Add(name.Trim());
        return [..names];
    }

    private void ScrollCreationSkills(int amount)
    {
        _creationSkillOffset=Math.Clamp(_creationSkillOffset+amount,0,Math.Max(0,CreationSkillNames().Length-8));
    }

    private void SetCreationSkillScroll(EnbUiPoint point)
    {
        int maximum=Math.Max(0,CreationSkillNames().Length-8);
        double fraction=(point.Y-_creationSkillTrack.Y)/Math.Max(1,_creationSkillTrack.Height);
        _creationSkillOffset=(int)Math.Round(Math.Clamp(fraction,0,1)*maximum);
    }

    private bool TryBeginCreationSkillDrag(string id)
    {
        if(_creationPage!=CreationPage.Profession || id!="SKILL_THUMB")return false;
        _creationSkillDragging=true;
        return true;
    }

    private void RenderCreationSkillScroll(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera,EnbFrontendModel classModel,EnbW3dHierarchyPoseOwner classPose)
    {
        var top=classPose.WorldTransforms[classModel.Pivot("SLIDER_00_MIN")].Translation;
        var bottom=classPose.WorldTransforms[classModel.Pivot("SLIDER_00_MAX")].Translation;
        float x=top.X;
        var up=DrawCreationSliderPart(draws,camera,"slider_up","CLICK_UP",x,top.Y-6,12,"creation/slider/up");
        var down=DrawCreationSliderPart(draws,camera,"slider_dn","CLICK_DOWN",x,bottom.Y+6,12,"creation/slider/down");
        float trackTop=top.Y-(float)(up.Height*480/Bounds.Height);
        float trackBottom=bottom.Y+(float)(down.Height*480/Bounds.Height);
        float trackHeight=trackTop-trackBottom;
        var track=DrawCreationSliderPart(draws,camera,"slider_vert",null,x,(trackTop+trackBottom)/2,
            trackHeight,"creation/slider/track",true);
        int maximum=Math.Max(0,CreationSkillNames().Length-8);
        float thumbHeight=Math.Max(16,trackHeight*8f/Math.Max(8,CreationSkillNames().Length));
        float travel=Math.Max(0,trackHeight-thumbHeight);
        float thumbY=trackTop-thumbHeight/2-(maximum==0?0:travel*_creationSkillOffset/maximum);
        var thumb=DrawCreationSliderPart(draws,camera,"slider_vert",null,x,thumbY,thumbHeight,
            "creation/slider/thumb");
        _creationSkillTrack=track;
        Place(_creationSkillTrackVisual,track);
        Place(_creationSkillThumbVisual,thumb);
        _creationSkillTrackVisual.IsVisible=_creationSkillThumbVisual.IsVisible=true;
        _hits["SKILL_UP"]=up;
        _hits["SKILL_DOWN"]=down;
        _hits["SKILL_THUMB"]=thumb;
        _hits["SKILL_TRACK"]=track;
    }

    private EnbUiRect DrawCreationSliderPart(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera,string resource,string? meshName,float x,float y,float height,string key,bool dark=false)
    {
        var model=Model(resource);
        var pose=model.Pose(null,0);
        var mesh=meshName is null?model.Resource.Meshes.First():model.Resource.Meshes.First(m=>m.Name.Equals(meshName,StringComparison.OrdinalIgnoreCase));
        var baseTransform=pose.WorldTransforms[model.HasPivot(mesh.Name)?model.Pivot(mesh.Name):0];
        EnbW3dDeformedGeometry? geometry=null;
        if(mesh.BoneIndices.Count>0 &&
            !EnbW3dSkinDeformer.TryDeform(mesh,pose,out geometry,out var diagnostic))
            throw new InvalidDataException(diagnostic);
        var a=geometry is null?Vector3.Transform(mesh.BoundingBoxMin,baseTransform):geometry.Positions.Aggregate(Vector3.Min);
        var b=geometry is null?Vector3.Transform(mesh.BoundingBoxMax,baseTransform):geometry.Positions.Aggregate(Vector3.Max);
        float centerX=(a.X+b.X)/2,centerY=(a.Y+b.Y)/2;
        float width=Math.Abs(b.X-a.X),sourceHeight=Math.Max(.01f,Math.Abs(b.Y-a.Y));
        var transform=(geometry is null?baseTransform:Matrix4x4.Identity)*Matrix4x4.CreateTranslation(-centerX,-centerY,0)*
            Matrix4x4.CreateScale(.75f,height/sourceHeight,1)*Matrix4x4.CreateTranslation(x,y,0);
        string hoverId=key.EndsWith("/up",StringComparison.Ordinal)?"SKILL_UP":
            key.EndsWith("/down",StringComparison.Ordinal)?"SKILL_DOWN":"SKILL_THUMB";
        if(!dark)draws.Add(new(key,model.Resource,mesh,transform,geometry,camera,(uint)TickCount())
            {Highlight=_hover==hoverId});
        return Screen(new(x+320-width*.375f,240-y-height/2,width*.75f,height));
    }

    private void RenderCreationSkillTooltip(double scaling)
    {
        if(_creationPage!=CreationPage.Profession || _hover.Length!=7 ||
            !_hover.StartsWith("SKILL",StringComparison.Ordinal) ||
            !int.TryParse(_hover.AsSpan(5),out int row) || row is <0 or >=8)
        { _creationTooltipDelay.Reset();_creationTooltipSkill="";_creationTooltipRenderedKey="";_creationTooltipGpu.SetTooltipPresentation(null); return; }
        var names=CreationSkillNames();
        int index=_creationSkillOffset+row;
        if(index>=names.Length){_creationTooltipDelay.Reset();_creationTooltipSkill="";_creationTooltipRenderedKey="";_creationTooltipGpu.SetTooltipPresentation(null);return;}
        string name=names[index];
        if(_creationTooltipSkill!=name)
        {_creationTooltipSkill=name;_creationTooltipRenderedKey="";}
        int delay=EnbCurrentRetailTooltipDelay.Milliseconds(
            _assets.ResolvePlayerOptions(null).Options.InterfaceTooltipDelay);
        // 006AB5B0 postpones the row's tooltip while that row is pressed (00575660
        // only cues and sets the row's +2C bit 0x40, which matters only for the
        // fixed-delay controllers).
        if(!_creationTooltipDelay.IsReady(index+1,TickCount(),delay,captured:_pressedName==_hover))
        {_creationTooltipRenderedKey="";_creationTooltipGpu.SetTooltipPresentation(null);return;}
        if(!_hits.TryGetValue(_hover,out var skillBounds))return;
        string renderedKey=FormattableString.Invariant($"{_creationAvatar!.Race}/{_creationAvatar.Profession}/{index}/{name}/{Bounds.Width}/{Bounds.Height}/{scaling}");
        if(_creationTooltipRenderedKey==renderedKey)return;
        var ini=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fa_fe_class00.ini"));
        string className=EnbNext.Client.Models.CharacterClass.ClassName(_creationAvatar!.Race,_creationAvatar.Profession);
        string detail=name;
        if(ini.TryGetSection("SKILL_TOOL_TIPS",out var tips))
            detail=tips.GetValueOrDefault(name+" "+className,tips.GetValueOrDefault(name,name));
        var definition=EnbRetailScannerTooltipDefinition.Load(_assets.Paths.ClientIniDirectory);
        if(definition is null)return;
        _creationTooltipFrame??=EnbAuthoredTooltipFrame.Load(_assets,definition);
        if(_creationTooltipFrame is null || Owner.Text.Font(definition.FontId,Bounds.Size) is not {} font)return;
        _creationTooltipGpu.SetTooltipPresentation(EnbTooltipContent.Create(_creationTooltipFrame,font,detail,
            Bounds.Size,1,new(.1f,1,.1f),_pointer,i=>
            {
                while(_creationTooltipLines.Count<=i)_creationTooltipLines.Add(new());
                return _creationTooltipLines[i];
            },skillBounds));
        _creationTooltipRenderedKey=renderedKey;
    }
    public Task SpeakCreationCompleteAsync()
    {
        _creationSpeechCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _creationCompletionRequested=true;
        // 00784EA0 consumes the AllGood cue's completion event. Only then does
        // 00616A10 start FE02A-2 and request Megan's walk-off.
        Speak("AllGood");
        return _creationSpeechCompletion.Task;
    }

    private void BeginCreationOutroAfterSpeech()
    {
        if(!_creationCompletionRequested || _creationCompleting)return;
        _creationCompletionRequested=false;
        _creationCompleting=true;
        bool animated=StartCreationNameAnimation("fe02a-2",false);
        ChangeNarrator("walk_off");
        _gpu.QueueFrontendSound("fontshoosh.wav");
        if(!animated)CompleteCreationNameTransition();
    }

    private bool StartCreationNameAnimation(string name,bool reverse)
    {
        //67AD30 retains the old clip but stops playback on failed acquisition.
        _creationNameAnimationPlaying=false;
        if(Model("fe02a").TryAnimation(name,out var clip) && clip is not null)
        {
            _creationNameClip=clip;
            _creationNameClock=new(checked((int)clip.NumFrames),clip.FrameRate,reverse);
            _creationNameAnimationPlaying=true;
        }
        _creationNameTick=TickCount();
        if(!_creationNameAnimationPlaying)Diagnostic?.Invoke("Creation transition animation unavailable: "+name);
        return _creationNameAnimationPlaying;
    }

    private void TickCreationNameAnimation()
    {
        if(_creationPage!=CreationPage.Name || _creationNameClock is null || !_creationNameAnimationPlaying)return;
        // 616A10 consumes the previous view update's latch before 67AF70 runs.
        if(CreationNameMoving && _creationNameClock.ConsumeCompletion())
        {
            CompleteCreationNameTransition();
            if(_creationPage!=CreationPage.Name)return;
        }
        long now=TickCount();
        _creationNameClock.Advance(unchecked((uint)(now-_creationNameTick)));
        _creationNameTick=now;
    }

    private void CompleteCreationNameTransition()
    {
            //An unavailable optional animation cannot hold the page's input or
            //completion task forever. Preserve its last pose; do not fake a clip.
            if(_creationNameEntering)
            {
                _creationNameEntering=false;
                _creationName.IsEnabled=!_busy && Owner.Dialogs.Active is null;
                if(!_chooseShardPlayed)
                {
                    _chooseShardPlayed=true;
                    Speak("ChooseShard");
                }
            }
            else if(_creationNameReturning)
            {
                _creationNameReturning=false;
                _creationPage=CreationPage.Ship;LoadCreationPage();return;
            }
            else
            {
                _creationSpeechCompletion?.TrySetResult();
                _creationSpeechCompletion=null;
            }
    }

    private void RenderCreation(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera, double scaling)
    {
        _creationEntranceBeams=[];
        TickCreationShipMotion();
        TickCreationControlHold();
        var background = Model("comp00a");
        if (EnbResolvedRenderCamera.TryCreate(new(0, 0, 640),
            Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1)),
            new((int)(Bounds.Width * scaling),(int)(Bounds.Height * scaling),1), Math.PI/4,
            Math.Max(.01,Bounds.Width/Math.Max(1,Bounds.Height)),new(1,10000),out var backdropCamera))
            foreach (var mesh in background.Resource.Meshes)
            {
                var pose = background.Pose(null, 0);
                int pivot = background.Pivot(mesh.Name);
                if (pose.Visibility[pivot]) draws.Add(new($"background/creation/{_creationAvatar!.Race}/"+mesh.Name,background.Resource,mesh,
                    pose.WorldTransforms[pivot],null,backdropCamera,(uint)TickCount()));
            }

        TickCreationNameAnimation();
        var model = Model(CreationModel);
        bool creationExitComplete=_creationPage==CreationPage.Name && _creationCompleting &&
            _creationNameClock?.Progress>=_creationNameClip?.NumFrames-1;
        var modelPose = _creationPage==CreationPage.Name && _creationNameClip is {} nameClip && _creationNameClock is {} nameClock
            ? model.PoseFrame(nameClip,nameClock.Frame) : model.Pose(null,0);
        // FE02A-2 moves its opaque FADE plane down across the view. Retail
        // draws the text into that same scene; the host labels are above
        // the GPU, so cover them to the plane's authored leading edge.
        _creationFadeCover.IsVisible=false;
        if(_creationPage==CreationPage.Name && _creationCompleting)
        {
            var fadeMesh=model.Resource.Meshes.FirstOrDefault(m=>m.Name=="FADE");
            if(fadeMesh is not null)
            {
                int fadePivot=model.Pivot("FADE");
                if(modelPose.Visibility[fadePivot])
                {
                    float leadingY=Vector3.Transform(
                        new Vector3(0,fadeMesh.BoundingBoxMin.Y,0),
                        modelPose.WorldTransforms[fadePivot]).Y;
                    double coveredHeight=Math.Clamp((240-leadingY)*Bounds.Height/480,0,Bounds.Height);
                    if(coveredHeight>0)
                    {
                        Place(_creationFadeCover,new EnbUiRect(0,0,Bounds.Width,coveredHeight));
                        _creationFadeCover.IsVisible=true;
                    }
                }
            }
        }
        // The class page's authored frame sits over the planet and ships.
        foreach(var mesh in model.Resource.Meshes)
            if((mesh.Name is "BACKGROUND" or "GRID") && _creationPage!=CreationPage.Profession)
            {
                int pivot=model.Pivot(mesh.Name);
                if(modelPose.Visibility[pivot])draws.Add(new("creation/ui/behind/"+mesh.Name,
                    model.Resource,mesh,modelPose.WorldTransforms[pivot],null,camera,(uint)TickCount()));
            }
        if (_creationPage==CreationPage.Profession)
        {
            RenderCreationPlanet(draws,scaling);
            RenderCreationShip(draws,camera,true);
            foreach(var frameMesh in model.Resource.Meshes.Where(m=>m.Name is "BACKGROUND" or "GRID"))
            {
                int pivot=model.Pivot(frameMesh.Name);
                if(modelPose.Visibility[pivot])draws.Add(new("creation/ui/behind/"+frameMesh.Name,model.Resource,frameMesh,
                    modelPose.WorldTransforms[pivot],null,camera,(uint)TickCount()));
            }
            RenderCreationClassStage(draws,scaling);
        }
        if (_creationPage==CreationPage.Name)
        {
            // Retail keeps Megan in the left portrait aperture. Use the same
            // authored narrator anchor/camera as the login scene rather than
            // the player-avatar fitting transform.
            var appearance=_narratorAppearance??=EnbRetailAvatarDefaults.Narrator(_assets);
            var portrait=UpdateNarrator();
            var narratorModel=Model("fe_narrator");
            var world=narratorModel.Pose(null,0).WorldTransforms[narratorModel.Pivot("NARRATOR")];
            if(portrait is not null && !_loginNarratorTransition.Hidden &&
                EnbResolvedRenderCamera.TryCreate(new(0,0,640),
                Quaternion.CreateFromRotationMatrix(new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1)),
                new((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1),Math.PI/4,4d/3,new(1,1000),out var narratorCamera))
            {
                CollectAnimationSounds(_narratorSpeech,portrait,world);
                foreach(var part in portrait.Parts)
                    draws.Add(new("creation/name/narrator/"+part.Resource.Key+"/"+part.Mesh.Ordinal,
                        part.Resource,part.Mesh,world,part.Geometry,narratorCamera,(uint)TickCount()){Avatar=appearance});
            }
        }
        if (_creationPage==CreationPage.Avatar)
        {
            // 553AF0 installs an unanimated FE01C in scene 1. The ShipPIP
            // camera below does not replace CharacterView's sound listener.
            var listenerScene=Model("fe01c");
            SetAnimationSoundListener(listenerScene,listenerScene.Pose(null,0),
                new((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1));
            _creationAppearance ??= EnbAvatarCustomizationProjection.ToScene(_creationAvatar!);
            var appearance=_creationAppearance;
            double avatarNow=TickCount()*.001;
            if(_creationAvatarPlayback is null)
            {
                _creationAvatarPlayback=new(_assets,appearance,portraitIdle:true);
                _creationAvatarPlayback.Idle.SuppressFidgets=true;
                _creationAvatarPlayback.Idle.StandStill=_avatarTab==1;
                _creationAvatarPlayback.StartIdle(avatarNow);
            }
            // 00556B20 requests selection; both retained slots keep advancing.
            bool headMode=_avatarTab==1;
            if(_creationAvatarPlayback.Idle.StandStill!=headMode)
            {
                _creationAvatarPlayback.Idle.StandStill=headMode;
                _creationAvatarPlayback.RequestIdleSelection();
            }
            _creationAvatarPlayback.RefreshAppearance(appearance);
            var portrait=_creationAvatarPlayback.Evaluate(avatarNow);
            _creationAvatarIdleTag=_creationAvatarPlayback.AnimationTag??_creationAvatarIdleTag;
            if(portrait is null)return;
            _creationPortrait=portrait;
            // Retail ShipPIP samples the actor sphere when the render object is
            // attached or replaced (0080C640 / 0080CA30). Recomputing it from
            // every animated pose makes camera framing and pan speed oscillate.
            if(!ReferenceEquals(_creationBoundsAppearance,appearance) &&
                (!_creationPreserveBoundsForMorph || _creationBoundsAppearance is null))
            {
                var sphere=_creationPortrait.PreviewSphere;
                if(!sphere.IsValid || sphere.Radius<=0)
                    throw new InvalidDataException("Retail avatar preview has no valid assembled actor sphere.");
                // 0080CA30 refreshes the actor sphere and its distance limits,
                // then clamps the current absolute distance into those limits.
                // A morph does not reset the camera to the same radius factor.
                float previousDistance=_creationAvatarRadius*_creationZoom;
                bool hadBounds=_creationBoundsAppearance is not null;
                if(!hadBounds || !_creationKeepCameraCenterForMorph)
                    _creationAvatarBoundsCenter=sphere.Center;
                _creationAvatarRadius=sphere.Radius;
                _creationAvatarHeight=_creationPortrait.HeadAnchor.Z-_creationPortrait.SkeletonReference.Translation.Z;
                if(hadBounds)
                    _creationZoom=Math.Clamp(previousDistance/_creationAvatarRadius,.75f,3f);
            }
            _creationBoundsAppearance=appearance;
            _creationPreserveBoundsForMorph=false;
            _creationKeepCameraCenterForMorph=false;
            if(ReferenceEquals(_creationBoundsAppearance,appearance))
            {
                bool headFocus=_avatarTab==1;
                float panOrigin=CreationAvatarPanOrigin();
                // ShipPIP mode 1 resets lateral offset to zero at 0080E260.
                // The actor sphere center contributes depth and height, while
                // the mode-1 lateral reset stays independent of that center.
                var center=new Vector3(_creationAvatarBoundsCenter.X,0,_creationAvatarBoundsCenter.Z+panOrigin);
                var cameraPosition=center+new Vector3(_creationAvatarRadius*_creationZoom,0,0);
                var orientation=Quaternion.CreateFromRotationMatrix(new(-1,0,0,0,0,-1,0,0,0,0,1,0,0,0,0,1));
                var turn=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,_creationYaw);
                cameraPosition=center+Vector3.Transform(cameraPosition-center,turn);
                orientation=Quaternion.Concatenate(orientation,turn);
                var pan=Vector3.Transform(new Vector3(0,-_creationPanX,_creationPanY),turn);
                center+=pan;cameraPosition+=pan;
                var viewport=new EnbRenderViewport((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1);
                // The retail portrait's authored camera aperture is 4:3 even
                // when the surrounding frontend runs in a wide window.
                double aspect=4d/3;
                if(EnbResolvedRenderCamera.TryCreate(cameraPosition,orientation,viewport,50*Math.PI/180,aspect,
                    new(.1f,Math.Max(10,2*cameraPosition.Length())),out var avatarCamera))
                {
                    CollectAnimationSounds(_creationAvatarPlayback,portrait,Matrix4x4.Identity);
                    foreach(var part in portrait.Parts)
                        draws.Add(new("creation/avatar/"+part.Resource.Key+"/"+part.Mesh.Ordinal,part.Resource,part.Mesh,Matrix4x4.Identity,
                            part.Geometry,avatarCamera,(uint)TickCount()){Avatar=appearance});
                }
            }
        }
        if (_creationPage==CreationPage.Ship) RenderCreationShip(draws,camera,false);
        _hits.Clear();
        foreach (var label in Owner.Text.Labels.Values) label.IsVisible = false;
        if(creationExitComplete)_creationName.IsVisible=false;
        var bodyAreaMesh=_creationPage==CreationPage.Avatar && _avatarTab==2
            ? model.Resource.Meshes.FirstOrDefault(m=>m.Name.Equals("BODY_AREA",StringComparison.OrdinalIgnoreCase)) : null;
        var bodyAreaWorld=bodyAreaMesh is null ? Matrix4x4.Identity : modelPose.WorldTransforms[model.Pivot(bodyAreaMesh.Name)];
        var bodyAreaMin=bodyAreaMesh is null ? Vector3.Zero : Vector3.Transform(bodyAreaMesh.BoundingBoxMin,bodyAreaWorld);
        var bodyAreaMax=bodyAreaMesh is null ? Vector3.Zero : Vector3.Transform(bodyAreaMesh.BoundingBoxMax,bodyAreaWorld);
        var headAreaMesh=_creationPage==CreationPage.Avatar && _avatarTab==1
            ? model.Resource.Meshes.FirstOrDefault(m=>m.Name.Equals("HEAD_AREA",StringComparison.OrdinalIgnoreCase)) : null;
        var headAreaWorld=headAreaMesh is null ? Matrix4x4.Identity : modelPose.WorldTransforms[model.Pivot(headAreaMesh.Name)];
        var headAreaMin=headAreaMesh is null ? Vector3.Zero : Vector3.Transform(headAreaMesh.BoundingBoxMin,headAreaWorld);
        var headAreaMax=headAreaMesh is null ? Vector3.Zero : Vector3.Transform(headAreaMesh.BoundingBoxMax,headAreaWorld);
        foreach (var mesh in model.Resource.Meshes.OrderBy(m=>m.StaticSort.IsDeferred).ThenByDescending(m=>m.StaticSort.RuntimeLevel))
        {
            if(mesh.Name is "BACKGROUND" or "GRID")continue;
            if(_creationPage==CreationPage.Profession && mesh.Name.Equals("DETAIL",StringComparison.OrdinalIgnoreCase))continue;
            if(_creationPage==CreationPage.Avatar && !AvatarVisible(mesh.Name))continue;
            if(_creationPage==CreationPage.Ship && _shipTab is not (1 or 3) &&
                mesh.Name is "COLOR_PALETTE" or "FRAME_COLOR" or "PULL_COLOR")continue;
            int pivot = model.Pivot(mesh.Name);
            if (!modelPose.Visibility[pivot]) continue;
            Matrix4x4 matrix = modelPose.WorldTransforms[pivot];
            string key = mesh.Name.ToUpperInvariant();
            if(key=="DIAL")
                matrix=Matrix4x4.CreateRotationZ((float)(-(TickCount()-_creationDialTick)*.00004))*matrix;
            if(key is "METAL00" or "METAL01" or "METAL02")
            {
                uint selected=_creationPage==CreationPage.Avatar?_creationColor switch
                {
                    3=>_creationAvatar!.ShirtPrimaryMetal,4=>_creationAvatar!.ShirtSecondaryMetal,
                    5=>_creationAvatar!.PantsPrimaryMetal,6=>_creationAvatar!.PantsSecondaryMetal,
                    _=>uint.MaxValue
                }:_creationPage==CreationPage.Ship
                    ?BinaryPrimitives.ReadUInt32LittleEndian(_creationShip!.AsSpan(58+_creationColor*17+13,4))
                    :uint.MaxValue;
                long now=TickCount();
                var previous=_creationSwatchTurns.GetValueOrDefault(key,(now,0));
                float angle=previous.Angle+Math.Clamp((now-previous.Tick)*.001f,0,.1f)*
                    (selected==(uint)(key[^1]-'0')?2:.3f);
                _creationSwatchTurns[key]=(now,angle);
                matrix=Matrix4x4.CreateRotationY(angle)*matrix;
            }
            if(key=="PULL_COLOR" && _hits.TryGetValue("COLOR_PALETTE",out var paletteRect))
            {
                var point=_creationPalettePoints.GetValueOrDefault(CreationPalettePointKey,new(.5f,.5f));
                var marker=Vector3.Transform((mesh.BoundingBoxMin+mesh.BoundingBoxMax)*.5f,matrix);
                var target=new Vector3((float)((paletteRect.X+paletteRect.Width*point.X)*640/Bounds.Width-320),
                    (float)(240-(paletteRect.Y+paletteRect.Height*point.Y)*480/Bounds.Height),marker.Z);
                matrix*=Matrix4x4.CreateTranslation(target-marker);
            }
            if(key=="BODY_PULL" && bodyAreaMesh is not null)
            {
                var marker=Vector3.Transform((mesh.BoundingBoxMin+mesh.BoundingBoxMax)*.5f,matrix);
                var target=new Vector3(bodyAreaMin.X+(bodyAreaMax.X-bodyAreaMin.X)*_creationBodyX,
                    bodyAreaMin.Y+(bodyAreaMax.Y-bodyAreaMin.Y)*_creationBodyY,marker.Z);
                matrix*=Matrix4x4.CreateTranslation(target-marker);
            }
            if(key=="HEAD_PULL" && headAreaMesh is not null && _creationAvatar is {} tattooAvatar)
            {
                var marker=Vector3.Transform((mesh.BoundingBoxMin+mesh.BoundingBoxMax)*.5f,matrix);
                var target=new Vector3(headAreaMin.X+(headAreaMax.X-headAreaMin.X)*tattooAvatar.TattooOffset[0],
                    headAreaMin.Y+(headAreaMax.Y-headAreaMin.Y)*(1-tattooAvatar.TattooOffset[1]),marker.Z);
                matrix*=Matrix4x4.CreateTranslation(target-marker);
            }
            bool interactive = IsCreationControl(key);
            bool disabled=interactive && (_busy || CreationNameMoving ||
                _creationPage==CreationPage.Name && key=="SKIP" && !_introductoryTutorialCompleted ||
                _creationPage==CreationPage.Ship && _shipTab==1 &&
                key is ("GLOSSY" or "FLAT") && CreationShipFinishDisabled());
            bool selectedControl=(_creationPage==CreationPage.Profession &&
                key==CreationClassMount(_creationAvatar!.Race,_creationAvatar.Profession)) ||
                (_creationPage==CreationPage.Avatar &&
                (key==(_avatarTab switch {0=>"GENDER_TAB",1=>"HEAD_TAB",_=>"BODY_TAB"}) ||
                 key==(_creationColor switch {0=>"HAIR_COLOR",1=>"EYE_COLOR",2=>"SKIN_COLOR",
                     3=>"P_SHIRT_COLOR",4=>"S_SHIRT_COLOR",5=>"P_PANT_COLOR",_=>"S_PANT_COLOR"}) &&
                     (_avatarTab==1 && _creationColor<=2 || _avatarTab==2 && _creationColor>=3))) ||
                (_creationPage==CreationPage.Ship &&
                 (key==(_shipTab switch {0=>"HULL_TAB",1=>"PAINT_TAB",2=>"INSIGNIA_TAB",_=>"NAME_TAB"}) ||
                  _shipTab==1 && (key==(_creationColor%2==0?"PRIMARY":"SECONDARY") ||
                    key==((_creationColor/2) switch {0=>"HULL",1=>"WING",2=>"PROFESSION",_=>"ENGINE"}) ||
                    !CreationShipFinishDisabled() && key==
                        (_creationShip![58+_creationColor*17+12]==0?"FLAT":"GLOSSY"))));
            // 006609A0: modulus 1000, then a 1 -> 0 -> 1 triangular wave.
            float flashPhase=TickCount()%1000*.001f;
            float selectedStrength=MathF.Abs(2*flashPhase-1);
            // The palette texture is a direct color input, not a highlighted button.
            bool hovered=key!="COLOR_PALETTE" && _hover.Equals(key,StringComparison.OrdinalIgnoreCase);
            string? paletteTexture=key=="COLOR_PALETTE"?_creationPage switch
            {
                CreationPage.Avatar=>_creationColor==2?"fa_skin_color.tga":"fa_avatar_color.tga",
                CreationPage.Ship=>_shipTab==3?"fa_name_color.tga":"fa_ship_color.tga",
                _=>null
            }:null;
            if(_creationPage==CreationPage.Name && key=="NAME" &&
                !string.IsNullOrEmpty(_creationName.Text))
                paletteTexture="co0102nw_gd.tga";
            draws.Add(new("creation/ui/"+mesh.Name,model.Resource,mesh,matrix,null,camera,(uint)TickCount())
                { Highlight=interactive && !disabled && (selectedControl || hovered),
                    HighlightStrength=hovered?1f:selectedStrength,
                    Pressed=interactive && key!="COLOR_PALETTE" && _pressedName==key,
                    Disabled=disabled,TextureOverride=paletteTexture });
            if (interactive && !disabled)
            {
                var a=Vector3.Transform(mesh.BoundingBoxMin,matrix);var b=Vector3.Transform(mesh.BoundingBoxMax,matrix);
                _hits[key]=Screen(new(Math.Min(a.X,b.X)+320,240-Math.Max(a.Y,b.Y),Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y)));
            }
        }
        if(_creationPage==CreationPage.Profession)RenderCreationStats(draws,camera,model,modelPose);
        if(_creationPage==CreationPage.Profession)RenderCreationSkillScroll(draws,camera,model,modelPose);
        if(!creationExitComplete)foreach (var definition in _layout.Definitions)
        {
            if(_creationPage==CreationPage.Profession && definition.Properties.GetValueOrDefault("Attach","").Equals("detail",StringComparison.OrdinalIgnoreCase))continue;
            if (!model.HasPivot(definition.Properties.GetValueOrDefault("Attach",""))) continue;
            if(_creationPage==CreationPage.Avatar && !AvatarVisible(definition.Properties.GetValueOrDefault("Attach","")))continue;
            string? value = CreationLabel(definition);
            Label(definition.StringId,value,model,modelPose,scaling);
        }
        if (!creationExitComplete && _creationPage == CreationPage.Name && _hits.TryGetValue("NAME",out var name))
        { Place(_creationName,name);_creationName.IsVisible=true; }
        if (_creationPage == CreationPage.Ship && _shipTab==3 && _hits.TryGetValue("NAME",out var shipName))
        { Place(_creationShipName,shipName);_creationShipName.IsVisible=true; }
        if (_creationPage == CreationPage.Ship && _shipTab==2)RenderCreationDecalChoices();
        RenderCreationSkillTooltip(scaling);
        Owner.Dialogs.Render(draws,camera,Bounds,_pointer);
        if(_creationPreserveRosterLabels)
        {
            if(_gpu.FrontendLastRenderedScreen=="creation") _creationPreserveRosterLabels=false;
            else foreach(var label in Owner.Text.Labels.Where(pair=>pair.Key.StartsWith("fe12_Name_",StringComparison.Ordinal))
                         .Select(pair=>pair.Value))label.IsVisible=true;
        }
    }
    private void RenderCreationStats(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera,EnbFrontendModel classModel,EnbW3dHierarchyPoseOwner classPose)
    {
        var avatar=_creationAvatar!;
        var ini=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.ClientIniDirectory,"fa_fe_class00.ini"));
        if(!ini.TryGetSection($"CLASS-{avatar.Race}{avatar.Profession}",out var section))return;
        bool hasNextRatings=EnbNextCharacterCreationAttributes.TryGetRatings(
            avatar.Race,avatar.Profession,out ReadOnlyMemory<int> nextRatings);
        for(int i=0;i<7;i++)
        {
            int value;
            if(hasNextRatings)value=nextRatings.Span[i];
            else if(!int.TryParse(section.GetValueOrDefault($"Stat{i}","0"),out value))continue;
            if(value is <1 or >5)continue;
            var stat=Model($"fe_stats{value:00}");var pose=stat.Pose(null,0);
            string mount=$"STATS{i+1:00}";
            if(!classModel.HasPivot(mount))continue;
            foreach(var mesh in stat.Resource.Meshes)
                draws.Add(new($"creation/stat/{i}/{mesh.Name}",stat.Resource,mesh,
                    pose.WorldTransforms[stat.Pivot(mesh.Name)]*classPose.WorldTransforms[classModel.Pivot(mount)],
                    null,camera,(uint)TickCount()));
        }
    }
    private void RenderCreationDecalChoices()
    {
        var choices=_assets.Decals?.NormalTextures;
        foreach(var decalImage in _creationDecalImages)decalImage.IsVisible=false;
        uint selected=BinaryPrimitives.ReadUInt32LittleEndian(_creationShip!.AsSpan(16,4));
        for(int slot=0;slot<10;slot++)
        {
            int index=_creationDecalPage+slot-1;
            if(index<0 || choices is null || index>=choices.Count)continue;
            string resource=choices[index].Resource;
            var texture=_assets.Textures.Resolve(resource).Image;
            if(texture is null)continue;
            if(!_hits.TryGetValue($"D{slot:00}",out var bounds))continue;
            var image=_creationDecalImages[slot];
            image.Texture=texture;Place(image,bounds);image.IsVisible=true;
            bool active=choices[index].DecalId==selected;
            // 006609A0's selected-gadget flash traverses the full 1->0->1 wave.
            image.Opacity=active?Math.Abs((TickCount()%1000)*.002-1):1;
        }
    }
    private bool IsCreationControl(string key) => key is "EXIT" or "CANCEL" or "CONFIRM" or "BUTTON_CANCEL" or "RANDOM"
        or "SKILL00" or "SKILL01" or "SKILL02" or "SKILL03"
        or "SKILL04" or "SKILL05" or "SKILL06" or "SKILL07"
        or "GENDER_TAB" or "HEAD_TAB" or "BODY_TAB" or "HULL_TAB" or "PAINT_TAB" or "INSIGNIA_TAB" or "NAME_TAB"
        or "SKIP" or "NAME" or "HULL_LEFT" or "HULL_RIGHT" or "WING_LEFT" or "WING_RIGHT"
        or "HAIR_LEFT" or "HAIR_RIGHT" or "BEARD_LEFT" or "BEARD_RIGHT" or "HEAD_TYPE_LEFT" or "HEAD_TYPE_RIGHT"
        or "FACE_LEFT" or "FACE_RIGHT" or "TATTOO_LEFT" or "TATTOO_RIGHT"
        or "COLOR_PALETTE" or "PRIMARY" or "SECONDARY" or "HULL" or "WING" or "PROFESSION" or "ENGINE"
        or "GLOSSY" or "FLAT" or "METAL00" or "METAL01" or "METAL02" or "LEFT" or "RIGHT"
        or "TURN_LEFT" or "TURN_RIGHT" or "TURN_UP" or "TURN_DOWN" or "TURN" or "ZOOM_IN" or "ZOOM_OUT"
        or "PAN_LEFT" or "PAN_RIGHT" or "PAN_UP" or "PAN_DOWN" or "PAN"
        or "HEAD_AREA" or "HEAD_PULL" or "SCALE_IN" or "SCALE_OUT"
        or "BODY_AREA" or "BODY_PULL" or "MOVE_UP" or "MOVE_DOWN" or "MOVE_LEFT" or "MOVE_RIGHT"
        or "EYE_LEFT" or "EYE_RIGHT" or "EAR_LEFT" or "EAR_RIGHT" or "SHIRT_STYLE_LEF" or "SHIRT_STYLE_RIG"
        or "PANT_STYLE_LEFT" or "PANT_STYLE_RIGH" or "HAIR_COLOR" or "SKIN_COLOR" or "EYE_COLOR"
        or "P_SHIRT_COLOR" or "S_SHIRT_COLOR" or "P_PANT_COLOR" or "S_PANT_COLOR"
        or "ENFORCER" or "TRADESMAN" or "SCOUT" or "WARRIOR" or "PRIVATEER" or "SENTINEL" or "DEFENDER" or "SEEKER" or "EXPLORER"
        || key.StartsWith("BUTTON_SHARD",StringComparison.Ordinal) ||
           _creationPage==CreationPage.Ship && key.Length==3 && key[0]=='D' && char.IsDigit(key[1]) && char.IsDigit(key[2]);
    private bool AvatarVisible(string key)
    {
        key=key.ToUpperInvariant();
        if (key is "TURN_UP" or "TURN_DOWN") return false;
        bool head=key is "HAIR_LEFT" or "HAIR_RIGHT" or "BEARD_LEFT" or "BEARD_RIGHT" or "EYE_LEFT" or "EYE_RIGHT"
            or "EAR_LEFT" or "EAR_RIGHT" or "HEAD_TYPE_LEFT" or "HEAD_TYPE_RIGHT" or "FACE_LEFT" or "FACE_RIGHT"
            or "TATTOO_LEFT" or "TATTOO_RIGHT" or "HAIR_COLOR" or "SKIN_COLOR" or "EYE_COLOR"
            or "HEAD_AREA" or "HEAD_PULL" or "HEAD_TEXT" or "SCALE_IN" or "SCALE_OUT"
            or "HAIR" or "BEARD" or "EYE" or "EAR" or "FACE" or "HEAD_TYPE" or "TATTOO"
            or "FACE_TITLE" or "HAIR_TITLE" or "ACC_TITLE";
        bool body=key is "SHIRT_STYLE_LEF" or "SHIRT_STYLE_RIG" or "PANT_STYLE_LEFT" or "PANT_STYLE_RIGH"
            or "P_SHIRT_COLOR" or "S_SHIRT_COLOR" or "P_PANT_COLOR" or "S_PANT_COLOR"
            or "METALS" or "METAL00" or "METAL01" or "METAL02" or "BODY_AREA" or "BODY_PULL"
            or "BODY_TEXT" or "MOVE_UP" or "MOVE_DOWN" or "MOVE_LEFT" or "MOVE_RIGHT" or "HEIGHT" or "WEIGHT"
            or "HEIGHT_TEXT" or "WEIGHT_TEXT" or "SHIRT_STYLE" or "PANT_STYLE" or "CLOTH_TITLE" or "BODY_TITLE";
        bool palette=key is "COLOR_PALETTE" or "FRAME_COLOR" or "PULL_COLOR";
        return _avatarTab switch {0=>!head&&!body&&!palette,1=>!body,2=>!head,_=>true};
    }
    private string? CreationLabel(EnbInterfaceTextDefinition definition)
    {
        string attach=definition.Properties.GetValueOrDefault("Attach","").ToUpperInvariant();
        if (_creationPage == CreationPage.Profession)
        {
            var a=_creationAvatar!;
            if (attach=="TITLE") return EnbNext.Client.Models.CharacterClass.ClassName(a.Race,a.Profession);
            if (attach=="SUBTITLE") return EnbNext.Client.Models.CharacterClass.RaceName(a.Race)+" "+EnbNext.Client.Models.CharacterClass.ClassName(a.Race,a.Profession);
            if (attach.StartsWith("SKILL") && int.TryParse(attach.AsSpan(5),out int skill))
            {
                var names=CreationSkillNames();
                int index=_creationSkillOffset+skill;
                return index<names.Length?names[index]:"";
            }
        }
        if (_creationPage==CreationPage.Avatar && attach=="GENDER_TAB") return _creationAvatar!.Gender==0?"Gender: Male":"Gender: Female";
        if (_creationPage==CreationPage.Avatar && attach is "HEIGHT" or "WEIGHT")
        {
            // client.exe 0055BFB0, tables 00B7B134..00B7B17C. The pair index
            // is race*2+gender; retail truncates the computed display integer.
            int pair=(int)(_creationAvatar!.Race*2+_creationAvatar.Gender);
            int[] weightBase=[150,110,150,110,200,160];
            int[] weightStep=[50,35,50,25,60,30];
            int[] heightBase=[64,60,68,64,72,68];
            int[] heightStep=[18,16,16,14,18,16];
            if(attach=="WEIGHT")return $"{(int)(weightBase[pair]+weightStep[pair]*(_creationBodyX+_creationBodyY))} lbs";
            int inches=(int)(heightBase[pair]+heightStep[pair]*_creationBodyY);
            return $"{inches/12} ft, {inches%12} inch";
        }
        if (_creationPage==CreationPage.Ship)
        {
            if(attach=="D00" && _creationDecalPage>0)return "";
            if(attach=="HULL00")return "Hull Style "+(BinaryPrimitives.ReadUInt32LittleEndian(_creationShip!.AsSpan(8,4))+1);
            if(attach=="WING00")return "Wing Style "+(BinaryPrimitives.ReadUInt32LittleEndian(_creationShip!.AsSpan(12,4))+1);
            if(_creationPage==CreationPage.Ship && attach=="NAME")return string.IsNullOrEmpty(_creationShipName.Text)
                ? _creationTextEditing?"":"<Enter Here>" : _creationShipName.Text;
        }
        if (_creationPage==CreationPage.Name)
        {
            if (attach=="NAME") return string.IsNullOrEmpty(_creationName.Text)
                ? _creationTextEditing?"":"Click to set Name" : _creationName.Text;
            if (attach=="SKIP") return _skipBeginnerTutorial?"Skipping Beginner Tutorial":"Playing with Beginner Tutorial";
            if (attach.StartsWith("BUTTON_SHARD") && int.TryParse(attach.AsSpan(12),out int index))
                return index < _creationRoutes.Count ? _creationRoutes[index].GalaxyName : "";
            if (attach.StartsWith("BUTTON_POP") && int.TryParse(attach.AsSpan(10),out int pop))
                return pop < _creationRoutes.Count && _creationRoutes[pop].Capacity>0
                    ? $"{100*_creationRoutes[pop].CurrentLoad/_creationRoutes[pop].Capacity}% full" : "";
        }
        return null;
    }

    private void RenderCreationPlanet(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,double scaling)
    {
        // 00574710 installs fe_class01 as the class scene. Its authored camera,
        // planet and glow pivots belong together; keep this view separate from
        // the orthographic class controls.
        var scene=Model("fe_class01");
        var pose=scene.Pose(null,0);
        Matrix4x4.Decompose(pose.WorldTransforms[scene.Pivot("CAMERA")],out _,out var rotation,out var position);
        if(!EnbResolvedRenderCamera.TryCreate(position,rotation,
            new((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1),Math.PI/2,
            4d/3,
            new(10,10000),out var camera))return;
        _animationSoundListener=camera;
        foreach(var mesh in scene.Resource.Meshes)
            if(mesh.Name is "PLANET" or "GLOW" && scene.HasPivot(mesh.Name))
            {
                var world=pose.WorldTransforms[scene.Pivot(mesh.Name)];
                if(mesh.Name=="PLANET")world*=Matrix4x4.CreateRotationZ(CreationPlanetAngle()*MathF.PI/180f);
                draws.Add(new("creation/planet/"+mesh.Name,scene.Resource,mesh,
                    world,null,camera,(uint)TickCount()));
            }
    }

    private void RenderCreationClassStage(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,double scaling)
    {
        // 00574710 loads FE_NAR_AGG (base FE_NARRATOR); 0067B390 attaches the
        // race/profession gender pair at its MALE and FEMALE markers.
        var stage=Model("fe_narrator");
        var animation=stage.Animation("fe_narrator-1");
        double duration=Math.Max(.001,(animation.NumFrames-1d)/animation.FrameRate);
        var pose=stage.Pose("fe_narrator-1",(TickCount()*.001)%duration);
        // FE_NAR_AGG is scene slot 4 (+0x84), rendered with its own camera
        // (+0x80): retail identity orientation at (0, 0, 640), 45-degree FOV and
        // 1..1000 clip (00678B70 / 00679930). Its authored CAMERA marker is
        // not used for this pass. The 100..2000 camera belongs to slot 0.
        // Convert retail -Z forward/+Y up to Next's +X forward/+Z up.
        var cameraRotation=Quaternion.CreateFromRotationMatrix(
            new Matrix4x4(0,0,-1,0,-1,0,0,0,0,1,0,0,0,0,0,1));
        if(!EnbResolvedRenderCamera.TryCreate(new(0,0,640),cameraRotation,
            new((int)(Bounds.Width*scaling),(int)(Bounds.Height*scaling),1),Math.PI/4,
            4d/3,
            new(1,1000),out var stageCamera))return;
        var stageCenter=pose.WorldTransforms[stage.Pivot("PLATFORM")].Translation;
        // CharacterViewRenderFrame (00679930) advances the FE_NAR_AGG turn by
        // elapsed milliseconds * 00AEDC40 (0.001) * 00AFBDDC (0.2 rad/s).
        float stageAngle=(float)(((TickCount()-_creationClassShipSpinTick)*.0002d)%Math.Tau);
        var stageTurn=Matrix4x4.CreateTranslation(-stageCenter)*Matrix4x4.CreateRotationY(stageAngle)*
            Matrix4x4.CreateTranslation(stageCenter);
        var entrance=ImmutableArray.CreateBuilder<EnbWorldRenderer.FrontendBeamInstance>();
        // 7896B0 starts the actor effect immediately; 7897D0 consumes frame deltas.
        long entranceElapsed=TickCount()-_creationEntrancePresentationTick;
        _creationEntranceClock.Tick(unchecked((uint)(entranceElapsed-_creationEntranceElapsed)));
        _creationEntranceElapsed=entranceElapsed;
        foreach(var mesh in stage.Resource.Meshes)
            if(mesh.Name=="PLATFORM")draws.Add(new("creation/class/stage/"+mesh.Name,stage.Resource,mesh,
                pose.WorldTransforms[stage.Pivot(mesh.Name)]*stageTurn,null,stageCamera,(uint)TickCount()));
        for(int gender=0;gender<2;gender++)
        {
            if(_creationClassAppearances[gender] is null)
            {
                var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
                _creationClassAppearances[gender]=EnbAvatarCustomizationProjection.ToScene(
                    EnbRetailCharacterCreationDefaults.ClassPreviewAvatar(definitions,_creationAvatar!.Race,
                        _creationAvatar.Profession,(uint)gender));
            }
            var appearance=_creationClassAppearances[gender]!;
            // Retail ticks both class actors each frame (00679930). Their
            // +0x232 flag keeps the fidget timer clear, so this presentation
            // uses the 00781EA0 breathing branch, not its bored branch.
            var portrait=UpdateCreationClassIdle(gender,appearance);
            if(portrait is null)continue;
            var marker=pose.WorldTransforms[stage.Pivot(gender==0?"MALE":"FEMALE")]*stageTurn;
            if(gender<_creationEntranceActors.Length && entranceElapsed<=_creationEntranceActors[gender].TailDurationMilliseconds)
                entrance.Add(new(_creationEntranceActors[gender],entranceElapsed,marker,stageCamera,gender){Scene=4});
            // 7897D0 reveals both actor render objects when state 4 expires.
            if(_creationEntranceClock.Hidden)continue;
            CollectAnimationSounds(_creationClassIdles[gender]?.Playback,portrait,marker);
            foreach(var part in portrait.Parts)
                draws.Add(new("creation/class/avatar/"+gender+"/"+part.Resource.Key+"/"+part.Mesh.Ordinal,
                    part.Resource,part.Mesh,marker,part.Geometry,stageCamera,(uint)TickCount())
                    {Avatar=appearance});
        }
        _creationEntranceBeams=entrance.ToImmutable();
    }

    // 0067BF20 constructs the 3 x 3 x 2 actor grid once for the frontend
    // view. 0067B390 reattaches those owners; 0067BEB0 releases them with it.
    private void InitializeCreationClassActors()
    {
        var definitions=EnbRetailIniDocument.Load(Path.Combine(_assets.Paths.CommonDataDirectory,"avatardef.ini"));
        for(uint race=0;race<3;race++)
        for(uint profession=0;profession<3;profession++)
        for(int gender=0;gender<2;gender++)
        {
            var appearance=EnbAvatarCustomizationProjection.ToScene(
                EnbRetailCharacterCreationDefaults.ClassPreviewAvatar(definitions,race,profession,(uint)gender));
            var playback=new LiveAvatarSpeechPlayback(_assets,appearance,portraitIdle:true);
            double now=TickCount()*.001;
            playback.StartIdle(now);
            _creationClassActors[(race,profession,gender)]=new CreationClassIdle
            {
                Clip=playback.Evaluate(now),Appearance=appearance,
                Playback=playback,HeadMotion=playback.HeadMotion
            };
        }
    }
    private EnbAvatarPortraitAsset? UpdateCreationClassIdle(int gender,EnbSceneAvatarSnapshot appearance)
    {
        var idle=_creationClassIdles[gender];
        if(idle is null)return null;
        // 00679930 updates both attached actors through +1C4 -> 00783B50.
        // 0067B390 suppresses fidgets, but does not replace their body clock
        // with a wall-time loop or a frozen outgoing pose.
        return idle.Clip=idle.Playback.Evaluate(TickCount()*.001)??idle.Clip;
    }

    private void RenderCreationShip(System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera,bool classPreview)
    {
        if(classPreview)
        {
            // FE_CLASS01 is a carousel. 00576AC0 updates all nine named class
            // objects every frame; the selected profession is brought to the
            // foreground by the shared planet angle. Rendering only the chosen
            // ship loses the authored neighbouring ships visible at the sides.
            foreach(var (race,profession,_) in CreationClasses)
                RenderCreationShipInstance(draws,camera,
                    EnbRetailCharacterCreationDefaults.Ship(race,profession).ToArray(),race,profession,true);
            return;
        }
        RenderCreationShipInstance(draws,camera,_creationShip!,_creationAvatar!.Race,
            _creationAvatar.Profession,false);
    }

    private void RenderCreationShipInstance(
        System.Collections.Immutable.ImmutableArray<EnbFrontendDraw>.Builder draws,
        EnbResolvedRenderCamera camera,byte[] ship,uint race,uint profession,bool classPreview)
    {
        var parts=new List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)>();
        if(classPreview)
        {
            string classObject=CreationClassMount(race,profession).ToLowerInvariant()+"03";
            if(!_creationClassShipParts.TryGetValue(classObject,out var authored))
            {
                authored=[];
                AddCreationAggregateParts(classObject,Matrix4x4.Identity,0,authored,[]);
                _creationClassShipParts.Add(classObject,authored);
            }
            parts.AddRange(authored);
        }
        else
        {
            int hullStyle=(int)BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(8,4));
            int wingStyle=(int)BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(12,4));
            string variant=$"retail/{race}/{profession}/{hullStyle}/{wingStyle}";
            if(!_creationClassShipParts.TryGetValue(variant,out var authored))
            {
                authored=[];
                AddCreationRetailShipParts(race,profession,hullStyle,wingStyle,authored);
                _creationRetailShipBounds.Add(variant,CreationRetailShipSphere(race,profession,hullStyle,wingStyle));
                _creationClassShipParts.Add(variant,authored);
            }
            parts.AddRange(authored);
        }
        if(parts.Count==0)return;
        var min=new Vector3(float.PositiveInfinity);
        var max=new Vector3(float.NegativeInfinity);
        foreach(var part in parts)
            for(int x=0;x<2;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)
            {
                var point=Vector3.Transform(new(x==0?part.Mesh.BoundingBoxMin.X:part.Mesh.BoundingBoxMax.X,
                    y==0?part.Mesh.BoundingBoxMin.Y:part.Mesh.BoundingBoxMax.Y,
                    z==0?part.Mesh.BoundingBoxMin.Z:part.Mesh.BoundingBoxMax.Z),part.World);
                min=Vector3.Min(min,point);max=Vector3.Max(max,point);
            }
        var center=(min+max)*.5f;
        float radius=Math.Max(1,Vector3.Distance(min,max)*.5f);
        if(!classPreview)
        {
            int hullStyle=(int)BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(8,4));
            int wingStyle=(int)BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(12,4));
            var retailSphere=_creationRetailShipBounds[$"retail/{race}/{profession}/{hullStyle}/{wingStyle}"];
            center=retailSphere.Center;
            radius=Math.Max(1,retailSphere.Radius);
        }
        Matrix4x4 display=Matrix4x4.Identity;
        if(classPreview)
        {
            // FE_CLASS01 supplies one authored placement for each profession.
            // The planet and ship share its 90-degree scene camera.
            var scene=Model("fe_class01");
            var scenePose=scene.Pose(null,0);
            string mount=CreationClassMount(race,profession);
            // 00576AC0 advances the nine named class objects by 0.33 rad/s,
            // independently of the one-second profession/planet transition.
            // MatrixApplyRotationB applies the negative Z rotation in our row
            // vector convention before the authored class placement.
            float shipAngle=(float)(((TickCount()-_creationClassShipSpinTick)*.00033d)%Math.Tau);
            // 00576AC0 rotates the named class object, whose 03 aggregate is
            // attached below it in FE_CLASS01. Keep the authored child offset
            // during that turn; a mesh-bounds center is not the retail pivot.
            var classWorld=scenePose.WorldTransforms[scene.Pivot(mount)];
            var aggregateWorld=scenePose.WorldTransforms[scene.Pivot(mount+"03")];
            if(!Matrix4x4.Invert(classWorld,out var inverseClassWorld))
                throw new InvalidDataException($"Invalid {mount} class pivot transform");
            var aggregateLocal=aggregateWorld*inverseClassWorld;
            display=aggregateLocal*Matrix4x4.CreateRotationZ(shipAngle)*classWorld*
                Matrix4x4.CreateRotationZ(CreationPlanetAngle()*MathF.PI/180f);
            Matrix4x4.Decompose(scenePose.WorldTransforms[scene.Pivot("CAMERA")],out _,out var rotation,out var position);
            if(!EnbResolvedRenderCamera.TryCreate(position,rotation,camera.Viewport,Math.PI/2,
                4d/3,
                new(10,10000),out camera))return;
        }
        else
        {
            // 0080C640 copies the assembled ship sphere center from +0x58,
            // negates it into NewShipScale's bone matrix (0080C814..0080C8FE),
            // then attaches the ship. 0080CE10 spins the FloatParent, so the
            // translated ship turns about its assembled center. The bone's
            // authored rest transform remains below that mode-0 override.
            var floatParent=Model("bone_hov01a");
            var scaleBone=floatParent.Pose(null,0).WorldTransforms[floatParent.Pivot("NewShipScale")];
            display=Matrix4x4.CreateTranslation(-center)*
                scaleBone*
                // 0080CE10 writes (x*cos+y*sin, y*cos-x*sin) across each row
                // of retail's column-vector 3x4 matrix. Its translation sits
                // in the fourth column (008C2780); transposing that convention
                // into System.Numerics' row vectors yields positive Z.
                Matrix4x4.CreateRotationZ(_creationShipSpinAngle);
            _creationShipRadius=radius;
            if(_creationShipSeedDistance<=0)
            {
                _creationShipSeedDistance=radius*2.1f;
                _creationShipDistance=_creationShipSeedDistance;
            }
            else _creationShipDistance=Math.Clamp(_creationShipDistance,radius*.5f,radius*2.1f);
            // Creation's mode-0 ShipPIP derives its limits from the assembled
            // ship sphere: 0.5..2.1 radii (client.exe 0080B2E0/0080CA30).
            // ShipPIP seeds its mode-0 camera at 0080E350 and its own camera
            // constructor uses a 50-degree horizontal aperture and 4:3 aspect.
            var seedPosition=new Vector3(128,-100,_creationShipSeedDistance);
            var forward=Vector3.Normalize(-seedPosition);
            var left=Vector3.Normalize(Vector3.Cross(Vector3.UnitZ,forward));
            var up=Vector3.Cross(forward,left);
            var basis=Quaternion.CreateFromRotationMatrix(new(forward.X,forward.Y,forward.Z,0,
                left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,0,0,0,1));
            var orbit=Quaternion.Normalize(Quaternion.Concatenate(_creationShipRotation,basis));
            forward=Vector3.Transform(Vector3.UnitX,orbit);
            left=Vector3.Transform(Vector3.UnitY,orbit);
            up=Vector3.Transform(Vector3.UnitZ,orbit);
            var orientation=Quaternion.CreateFromRotationMatrix(new(forward.X,forward.Y,forward.Z,0,
                left.X,left.Y,left.Z,0,up.X,up.Y,up.Z,0,0,0,0,1));
            // ShipPIP 0080E260 supplies mode-0 camera offsets (0,-3).
            // 0080E350 looks from (128,-100,2.1*radius) at the parent origin;
            // 0080E530 applies the stored local-axis pan to that same matrix.
            if(!EnbResolvedRenderCamera.TryCreate(-forward*seedPosition.Length()+
                    left*_creationPanX+up*(_creationPanY-3f)+
                    forward*(_creationShipSeedDistance-_creationShipDistance),
                orientation,camera.Viewport,50*Math.PI/180,4d/3,
                new(.1f,Math.Max(1000,radius*20)),out camera))return;
        }
        var colors=Enumerable.Range(0,8).Select(i=>new EnbNext.Presentation.EnbSceneColorizationSnapshot(
            BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(58+i*17+13,4)),
            System.Collections.Immutable.ImmutableArray.Create(ship.AsSpan(58+i*17,12).ToArray()))).ToArray();
        uint decalId=BinaryPrimitives.ReadUInt32LittleEndian(ship.AsSpan(16,4));
        var decalColor=Enumerable.Repeat(1f,4).SelectMany(BitConverter.GetBytes).ToImmutableArray();
        EnbNext.Presentation.EnbSceneDecalSnapshot[] decals=[new(1,decalId,decalColor),new(2,decalId,decalColor)];
        string shipName=classPreview?string.Empty:(_creationShipName.Text??string.Empty);
        byte[] rawName=System.Text.Encoding.Latin1.GetBytes(shipName);
        var nameDecal=string.IsNullOrEmpty(shipName)?null:new EnbNext.Presentation.EnbSceneNameDecalSnapshot(
            shipName,rawName.ToImmutableArray(),new(
                BinaryPrimitives.ReadSingleLittleEndian(ship.AsSpan(46,4)),
                BinaryPrimitives.ReadSingleLittleEndian(ship.AsSpan(50,4)),
                BinaryPrimitives.ReadSingleLittleEndian(ship.AsSpan(54,4))));
        string className=CreationClassMount(race,profession).ToLowerInvariant();
        // Retail's application static-sort queue submits opaque traversal first,
        // then deferred buckets from level 32 down to 1. The authored decal
        // quads are level 20; traversal order alone buried them under the hull.
        foreach(var part in parts.OrderBy(p=>p.Mesh.StaticSort.IsDeferred?1:0)
                     .ThenByDescending(p=>p.Mesh.StaticSort.RuntimeLevel))
        {
            if(part.Mesh.Name.StartsWith("NAMEDECAL",StringComparison.OrdinalIgnoreCase) && nameDecal is null)continue;
            var world=part.World*display;
            if(classPreview && !CreationShipPartInView(part.Mesh,world,camera))continue;
            draws.Add(new((classPreview?$"creation/class/ship/{className}/":"creation/ship/")+part.Key+"/"+part.Mesh.Name,part.Model.Resource,part.Mesh,
                world,null,camera,(uint)TickCount())
                {ShipColors=colors,ShipDecals=decals,ShipNameDecal=nameDecal,ShipColorRegion=part.Region});
        }
    }
    private static bool CreationShipPartInView(EnbW3dRenderMesh mesh,Matrix4x4 world,EnbResolvedRenderCamera camera)
    {
        // A whole carousel ship may cross the viewport while most of its
        // members do not. Cull only when the member's transformed bounding
        // sphere is wholly outside a frustum plane.
        var center=Vector3.Transform((mesh.BoundingBoxMin+mesh.BoundingBoxMax)*.5f,world);
        Matrix4x4.Decompose(world,out var scale,out _,out _);
        float radius=Vector3.Distance(mesh.BoundingBoxMin,mesh.BoundingBoxMax)*.5f*
            MathF.Max(scale.X,MathF.Max(scale.Y,scale.Z));
        var relative=center-camera.Position;
        float depth=Vector3.Dot(relative,camera.Basis.Forward);
        if(depth+radius<camera.Projection.ClipRange.NearPlane ||
           depth-radius>camera.Projection.ClipRange.FarPlane)return false;
        float widest=MathF.Max(0,depth)+radius;
        float horizontal=MathF.Abs(Vector3.Dot(relative,camera.Basis.Right));
        float vertical=MathF.Abs(Vector3.Dot(relative,camera.Basis.Up));
        return horizontal-radius<=widest/camera.Projection.Matrix.M11 &&
               vertical-radius<=widest/camera.Projection.Matrix.M22;
    }
    private void AddCreationRetailShipParts(uint race,uint profession,int hullStyle,int wingStyle,
        List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)> parts)
    {
        // 00621790 calls 00621530 separately for H, E, P and W. The two
        // selector fields are the first numeric digit of H and W respectively;
        // P's first digit is the profession permutation 2,1,3. The class-carousel *03 aggregate
        // is a separate authored object, not the hull-page assembly recipe.
        char racialCode="TJP"[(int)race];
        string hullName=$"S{racialCode}H{hullStyle+1}1";
        string plateName=$"S{racialCode}P{new[]{2,1,3}[(int)profession]}1";
        string wingName=$"S{racialCode}W{wingStyle+1}1";
        string engineName=$"S{racialCode}E01";
        var hull=Model(hullName);
        var plate=Model(plateName);
        var wing=Model(wingName);
        var hullPose=hull.Pose(null,0);
        Matrix4x4 hullWorld=Matrix4x4.Identity;
        AddCreationModelParts(hullName,hull,hullWorld,0,parts);
        // 00621790 assembles components H(0), W(1), P(2), E(3), and
        // 006211D0/0080F020 consume their four paint pairs in that order.
        // Attachment pivot numbers are placement bones, not paint regions.
        AddCreationRetailEngines(hull,hullWorld,3,engineName,parts);
        foreach(var (name,model,bone,region) in new[]{(plateName,plate,"~01",2),(wingName,wing,"~02",1)})
        {
            if(!hull.HasPivot(bone))
                throw new InvalidDataException($"Retail hull {hullName} has no {bone} attachment pivot");
            Matrix4x4 parent=hullPose.WorldTransforms[hull.Pivot(bone)];
            AddCreationModelParts(name,model,parent,region,parts);
            AddCreationRetailEngines(model,parent,3,engineName,parts);
            if(name==plateName && race==2 && profession==0)
                AddCreationRetailRepeatedParts(model,parent,region,"PU43","~weap_",parts);
        }
    }

    private EnbW3dWorldBounds CreationRetailShipSphere(uint race,uint profession,int hullStyle,int wingStyle)
    {
        // 0080C640 calls the assembled HLOD's +0xF0 sphere getter. Its
        // +0x100 refresh (0098F1D0) joins every member sphere from all LODs,
        // then the children inserted through +0x84 (0098E4B0). The ship
        // builder 00621790 inserts hull E children, then P at ~01 and W at
        // ~02; P/W already own their E children when inserted. Using only the
        // visible level's AABB gives a different center and zoom range.
        char code="TJP"[(int)race];
        var hull=Model($"S{code}H{hullStyle+1}1");
        var plate=Model($"S{code}P{new[]{2,1,3}[(int)profession]}1");
        var wing=Model($"S{code}W{wingStyle+1}1");
        string engine=$"S{code}E01";
        var hullSphere=CreationRetailModelSphere(hull);
        hullSphere=CreationRetailJoinRepeated(hull,hullSphere,"~03_",engine);
        var plateSphere=CreationRetailJoinRepeated(plate,CreationRetailModelSphere(plate),"~03_",engine);
        if(race==2 && profession==0)
            plateSphere=CreationRetailJoinRepeated(plate,plateSphere,"~weap_","PU43");
        var wingSphere=CreationRetailJoinRepeated(wing,CreationRetailModelSphere(wing),"~03_",engine);
        var hullPose=hull.Pose(null,0);
        hullSphere=CreationRetailJoinSphere(hullSphere,CreationRetailTransformSphere(
            plateSphere,hullPose.WorldTransforms[hull.Pivot("~01")]));
        hullSphere=CreationRetailJoinSphere(hullSphere,CreationRetailTransformSphere(
            wingSphere,hullPose.WorldTransforms[hull.Pivot("~02")]));
        return hullSphere;
    }

    private EnbW3dWorldBounds CreationRetailJoinRepeated(EnbFrontendModel host,
        EnbW3dWorldBounds sphere,string bonePrefix,string childName)
    {
        var pose=host.Pose(null,0);
        for(int index=1;index<=99;index++)
        {
            string bone=$"{bonePrefix}{index:00}";
            if(!host.HasPivot(bone))break;
            var child=CreationRetailAggregateSphere(childName,[]);
            sphere=CreationRetailJoinSphere(sphere,CreationRetailTransformSphere(
                child,pose.WorldTransforms[host.Pivot(bone)]));
        }
        return sphere;
    }

    private EnbW3dWorldBounds CreationRetailAggregateSphere(string objectName,HashSet<string> visiting)
    {
        if(!visiting.Add(objectName))throw new InvalidDataException($"Cyclic hull sphere aggregate {objectName}");
        if(_assets.RetailMixResources?.TryReadResource(objectName+".w3d",out var bytes,out _)!=true)
            throw new FileNotFoundException($"Missing retail hull sphere resource {objectName}.w3d");
        EnbW3dWorldBounds sphere;
        if(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes,out var aggregate,out _))
        {
            var model=Model(aggregate.BaseModelName);
            sphere=CreationRetailModelSphere(model);
            var pose=model.Pose(null,0);
            foreach(var attachment in aggregate.Attachments)
            {
                var child=CreationRetailAggregateSphere(attachment.SubobjectName,visiting);
                sphere=CreationRetailJoinSphere(sphere,CreationRetailTransformSphere(
                    child,pose.WorldTransforms[model.Pivot(attachment.BoneName)]));
            }
        }
        else if(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out _,out _))
        {
            // ParticleEmitterClass +0xF8, 0097D4B0, writes four zero floats.
            sphere=new EnbW3dWorldBounds(Vector3.Zero,0);
        }
        else if(EnbW3dModel.TryDecode(bytes,out _))sphere=CreationRetailModelSphere(Model(objectName));
        else throw new InvalidDataException($"Unsupported retail hull sphere object {objectName}");
        visiting.Remove(objectName);
        return sphere;
    }

    private static EnbW3dWorldBounds CreationRetailModelSphere(EnbFrontendModel model)
    {
        if(model.Resource.HierarchicalLods.Count!=1)
            throw new InvalidDataException("Retail hull sphere model has no unique HLOD");
        var hlod=model.Resource.HierarchicalLods[0];
        if(hlod.Aggregate is not null)
            throw new InvalidDataException($"Retail hull sphere HLOD {hlod.Name} has an unresolved static aggregate");
        var pose=model.Pose(null,0);
        EnbW3dWorldBounds sphere=default;
        bool have=false;
        foreach(var member in hlod.Levels.SelectMany(level=>level.Members))
        {
            if(member.ReferenceIndex>=pose.WorldTransforms.Length)
                throw new InvalidDataException($"Retail hull sphere member {hlod.Name}/{member.Name} has no pivot");
            if(member.Name.Equals("NULL",StringComparison.OrdinalIgnoreCase))
            {
                // 0098F1D0 initializes from member zero even when it is a
                // NullObject. Later zero-radius members do not enlarge it.
                var empty=CreationRetailTransformSphere(new EnbW3dWorldBounds(Vector3.Zero,0),
                    pose.WorldTransforms[(int)member.ReferenceIndex]);
                sphere=have?CreationRetailJoinSphere(sphere,empty):empty;
                have=true;
                continue;
            }
            var mesh=model.Resource.Meshes.FirstOrDefault(candidate=>
                member.Name.Equals(candidate.ContainerName+"."+candidate.Name,StringComparison.OrdinalIgnoreCase) ||
                member.Name.Equals(candidate.Name,StringComparison.OrdinalIgnoreCase));
            if(mesh is null)
                throw new InvalidDataException($"Retail hull sphere member {hlod.Name}/{member.Name} is unresolved");
            var child=CreationRetailTransformSphere(
                new EnbW3dWorldBounds(mesh.BoundingSphereCenter,mesh.BoundingRadius),
                pose.WorldTransforms[(int)member.ReferenceIndex]);
            sphere=have?CreationRetailJoinSphere(sphere,child):child;
            have=true;
        }
        return have?sphere:new EnbW3dWorldBounds(Vector3.Zero,0);
    }

    private static EnbW3dWorldBounds CreationRetailTransformSphere(EnbW3dWorldBounds sphere,Matrix4x4 transform)
        =>new(Vector3.Transform(sphere.Center,transform),sphere.Radius);

    private static EnbW3dWorldBounds CreationRetailJoinSphere(EnbW3dWorldBounds current,EnbW3dWorldBounds child)
    {
        // 0098F1D0's enclosing-sphere update, also used by 009865F0.
        if(child.Radius==0)return current;
        float separation=Vector3.Distance(current.Center,child.Center);
        if(separation==0)return child.Radius>current.Radius?child:current;
        float joined=(current.Radius+child.Radius+separation)*.5f;
        if(current.Radius>joined)return current;
        if(child.Radius>joined)return child;
        return new EnbW3dWorldBounds(current.Center+(child.Center-current.Center)*
            ((joined-current.Radius)/separation),joined);
    }

    private void AddCreationRetailEngines(EnbFrontendModel host,Matrix4x4 parent,int region,
        string engineName,List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)> parts)
    {
        // 00621790 checks successive ~03_%02d bones on H, P and W, clones
        // the same E render object at each present bone, and stops at the
        // first absent bone (006218DE/006219EA/00621AF4).
        AddCreationRetailRepeatedParts(host,parent,region,engineName,"~03_",parts);
    }

    private void AddCreationRetailRepeatedParts(EnbFrontendModel host,Matrix4x4 parent,int region,
        string childName,string bonePrefix,
        List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)> parts)
    {
        var pose=host.Pose(null,0);
        for(int index=1;index<=99;index++)
        {
            string bone=$"{bonePrefix}{index:00}";
            if(!host.HasPivot(bone))break;
            AddCreationAggregateParts(childName,
                pose.WorldTransforms[host.Pivot(bone)]*parent,region,parts,[]);
        }
    }

    private void AddCreationAggregateParts(string objectName,Matrix4x4 parent,int region,
        List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)> parts,
        HashSet<string> visiting)
    {
        if(!visiting.Add(objectName))throw new InvalidDataException($"Cyclic class ship aggregate {objectName}");
        if(_assets.RetailMixResources?.TryReadResource(objectName+".w3d",out var bytes,out _)!=true)
            throw new FileNotFoundException($"Missing retail class ship resource {objectName}.w3d");
        if(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes,out var aggregate,out _))
        {
            string baseName=aggregate.BaseModelName;
            var baseModel=Model(baseName);
            AddCreationModelParts(baseName,baseModel,parent,region,parts);
            var basePose=baseModel.Pose(null,0);
            foreach(var attachment in aggregate.Attachments)
            {
                if(!baseModel.HasPivot(attachment.BoneName))
                    throw new InvalidDataException($"{aggregate.Name} attachment bone {attachment.BoneName} is absent from {aggregate.BaseModelName}");
                int childRegion=attachment.BoneName.StartsWith("~0",StringComparison.Ordinal)
                    ?EnbCurrentRetailShipColorizationRuntime.ResolveRegion(attachment.BoneName):region;
                AddCreationAggregateParts(attachment.SubobjectName,
                    basePose.WorldTransforms[baseModel.Pivot(attachment.BoneName)]*parent,
                    childRegion,parts,visiting);
            }
        }
        else if(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes,out _,out _))
        {
            // E's EXHAUST01 aggregate children are particle effects rather
            // than ship geometry. They do not contribute to the ShipPIP hull
            // sphere; keep the geometric assembly usable when effects are not
            // represented by this mesh draw list. The general W3D decoder
            // also accepts emitters; successful decoding does not imply a
            // hierarchy exists. Classify this leaf before the mesh branch.
        }
        else if(EnbW3dModel.TryDecode(bytes,out _))
            AddCreationModelParts(objectName,Model(objectName),parent,region,parts);
        else throw new InvalidDataException($"Retail class ship object {objectName} is neither a mesh model nor an aggregate");
        visiting.Remove(objectName);
    }

    private static void AddCreationModelParts(string objectName,EnbFrontendModel model,Matrix4x4 parent,int region,
        List<(string Key,EnbFrontendModel Model,EnbW3dRenderMesh Mesh,Matrix4x4 World,int Region)> parts)
    {
        var pose=model.Pose(null,0);
        foreach(var mesh in model.Resource.Meshes.Where(m=>m.Name.EndsWith('0')))
            parts.Add(($"{objectName}/{parts.Count}",model,mesh,ShipMeshPose(model,pose,mesh)*parent,region));
    }

    private static Matrix4x4 ShipMeshPose(EnbFrontendModel model,EnbW3dHierarchyPoseOwner pose,EnbW3dRenderMesh mesh)
    {
        var member=model.Resource.HierarchicalLods.SelectMany(h=>h.Levels)
            .SelectMany(l=>l.Members).FirstOrDefault(m=>
                m.Name.Equals(mesh.Name,StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals(mesh.ContainerName+"."+mesh.Name,StringComparison.OrdinalIgnoreCase));
        return member.Name is not null && member.ReferenceIndex<pose.WorldTransforms.Length
            ? pose.WorldTransforms[(int)member.ReferenceIndex] : Matrix4x4.Identity;
    }
}
