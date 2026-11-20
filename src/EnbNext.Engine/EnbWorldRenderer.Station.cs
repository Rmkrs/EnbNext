using System.Diagnostics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using System.Numerics;
using System.Collections.Immutable;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly EnbRetailStationLightingState _stationLightingState = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name,float? Volume,bool Utility)> _pendingInteractionSounds=new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Name,float Volume,float Duration,uint Started)> _pendingFontSounds=new();
    public void QueueFontSound(string name,float volume,float duration)
    {_pendingFontSounds.Enqueue((name,volume,duration,unchecked((uint)Environment.TickCount)));RequestFrame();}
    private long _queuedInteractionSoundCount;
    private string _lastQueuedInteractionSound="";
    private string _lastClientAudioStatus="";
    public void QueueInteractionSound(string name){Volatile.Write(ref _lastQueuedInteractionSound,name);Interlocked.Increment(ref _queuedInteractionSoundCount);_pendingInteractionSounds.Enqueue((name,null,false));RequestFrame();}
    public void QueueAuthoredInteractionSound(string name,float volume){_pendingInteractionSounds.Enqueue((name,volume,false));RequestFrame();}
    public void QueueUtilitySound(string name){_pendingInteractionSounds.Enqueue((name,null,true));RequestFrame();}
    private void FlushInteractionSounds()
    {
        if(_audio is not {} audio)return;
        while(_pendingFontSounds.TryDequeue(out var font))audio.QueueFontSound(font.Name,font.Volume,font.Duration,font.Started);
        while(_pendingInteractionSounds.TryDequeue(out var cue))
            if(cue.Utility)audio.QueueUtilitySound(cue.Name);
            else if(cue.Volume is {} volume)audio.QueueAuthoredInteractionSound(cue.Name,volume);
            else audio.QueueInteractionSound(cue.Name);
    }
    public void SetWeaponRangeSound(int slot,bool enabled)=>_audio?.SetWeaponRangeSound(slot,enabled);
    public bool EnableClientAudio {get;set;}
    public string ClientAudioStatus=>(_audio?.ClientSoundStatus??(_lastClientAudioStatus.Length>0
        ? " · client audio disposed; final " + _lastClientAudioStatus
        : " · client audio not initialized"))+
        $" · UI cues queued {Interlocked.Read(ref _queuedInteractionSoundCount)}; last {Volatile.Read(ref _lastQueuedInteractionSound)} · pending UI sounds {_pendingInteractionSounds.Count}";
    private IEnbSceneFrame? _clientAudioScene;
    public void SetClientAudioScene(IEnbSceneFrame? scene)=>Volatile.Write(ref _clientAudioScene,scene);
    private EnbCurrentRetailStationInterior? _stationInterior;
    private int _stationResetRequested;
    private long? _stationClockRebaseFrom;
    private EnbCurrentRetailStationInterior? _stationPresentedInterior;
    public bool StationFrameReady => Volatile.Read(ref _stationInterior) is {} current &&
        ReferenceEquals(current,Volatile.Read(ref _stationPresentedInterior));
    private EnbCurrentRetailStationInterior? _stationGpuAsset;
    private EnbCurrentRetailStationInterior? _stationClockAsset;
    private EnbCurrentRetailStationInterior? _stationGpuContextLost;
    private bool _stationClockPresented;
    private long _stationStarted;
    private IEnbSceneFrame? _stationScene;
    private EnbWorldRenderer? _stationShipRenderer;
    private EnbW3dSceneFogState? _stationShipSceneFog;
    private long _stationElapsedMilliseconds;
    private float _stationGpuArrivalFrame = -1;
    private long? _stationDepartureStarted;
    private bool _stationDepartureAudioStarted;
    private bool _stationDepartureFrameComplete;
    private (Vector3 Position, Quaternion Orientation)? _stationLastCameraPose, _stationDepartureCameraPose;
    public bool StationDepartureComplete => Volatile.Read(ref _stationDepartureFrameComplete);
    public void BeginStationDeparture()
    {
        if(_stationDepartureStarted is not null || !StationArrivalComplete)return;
        // Start the retail CAMERA_LEAVE transition from the displayed viewpoint.
        _stationDepartureCameraPose=_stationLastCameraPose;
        Volatile.Write(ref _stationDepartureFrameComplete,false);
        _stationDepartureStarted=_stationElapsedMilliseconds; StopStationMovement();
    }
    public (Vector3 Position,float Heading,bool Run)? StationAvatarPose => _stationMovement is {} m ? (m.Position,m.Heading,m.Run) : null;
    public bool PickStationShip(double x,double y)
        =>PickStationInteraction(x,y) is {InRange:true,Ship:true};
    public bool StationArrivalComplete => _stationInterior is { } room && _stationElapsedMilliseconds >= room.ArrivalDurationMilliseconds;
    // 007897D0 clears the arrival teleport state (+0x380) when its countdown ends; without the docking arrival no state is set.
    public bool StationArrivalBeamComplete => _stationInterior is { } room && (!room.PlayDocking ||
        _stationElapsedMilliseconds >= room.ArrivalDurationMilliseconds + EnbStationBeamEffect.ArrivalOwnerDurationMilliseconds);
    public bool StationMovementPaused {get;set;}
    public void UpdateStationFrame(IEnbSceneFrame scene)
    {
        Volatile.Write(ref _stationScene, scene);
        // LiveWorldView owns presentation cadence (continuousPresentation=false).
        RequestFrame();
    }
    private EnbResolvedRenderCamera StationCamera(EnbCurrentRetailStationInterior interior, int width, int height)
    {
        if (_stationDepartureCameraPose is {} held && _stationDepartureStarted is long started)
        {
            // 0063869D..006386A8: camera_leave, duration 6, preserve-orientation=false, height=0.
            // 0069ED80: eased translation/quaternion interpolation (0077B7D0, 008C33B0).
            if(!Matrix4x4.Decompose(interior.PivotTransform("CAMERA_LEAVE"),out _,out var targetRotation,out var targetPosition))
                throw new InvalidDataException("Invalid station departure camera.");
            float phase=(float)Math.Clamp((_stationElapsedMilliseconds-started)/6000d,0,1);
            float weight=.5f-.5f*MathF.Cos(MathF.PI*phase);
            return (interior with { CameraPosition=Vector3.Lerp(held.Position,targetPosition,weight),
                CameraOrientation=Quaternion.Slerp(held.Orientation,targetRotation,weight) }).Camera(width,height);
        }
        var transform=interior.RoomType==0 ? interior.PivotTransform("CAMERA_3RD",_stationElapsedMilliseconds) : Matrix4x4.CreateFromQuaternion(interior.CameraOrientation)*Matrix4x4.CreateTranslation(interior.CameraPosition);
        if(StationArrivalComplete && _stationAvatarAsset is {} avatar)
        {
            var target=_stationCameraFrameTarget??StationAvatarCameraTransform(interior,avatar);
            // RefreshStationAvatarModel 0063C1E4..0063C210 requests a one-second
            // transition; 0069ED80 uses half-cosine translation/quaternion easing.
            float phase=interior.PlayDocking ? (float)Math.Clamp((_stationElapsedMilliseconds-interior.ArrivalDurationMilliseconds)/1000d,0,1) : 1;
            Matrix4x4.Decompose(transform,out _,out var fromRotation,out var fromPosition);
            Matrix4x4.Decompose(target,out _,out var toRotation,out var toPosition);
            float weight=.5f-.5f*MathF.Cos(MathF.PI*phase);
            transform=Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(fromRotation,toRotation,weight))*
                Matrix4x4.CreateTranslation(Vector3.Lerp(fromPosition,toPosition,weight));
        }
        if(_stationConversationCamera is {} conversation)transform=conversation;
        if(_stationConversationFrom is {} previous)
        {
            Matrix4x4.Decompose(transform,out _,out var targetRotation,out var targetPosition);
            float phase=(float)Math.Clamp((_stationElapsedMilliseconds-_stationConversationStarted)/1000d,0,1);
            float weight=.5f-.5f*MathF.Cos(MathF.PI*phase);
            transform=Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(previous.Orientation,targetRotation,weight))*
                Matrix4x4.CreateTranslation(Vector3.Lerp(previous.Position,targetPosition,weight));
            if(phase>=1)_stationConversationFrom=null;
        }
        if(!Matrix4x4.Decompose(transform,out _,out var rotation,out var position))
            throw new InvalidDataException("Invalid station camera pose.");
        if(_stationTerminalCamera is {} terminal)
            Matrix4x4.Decompose(terminal,out _,out rotation,out position);
        if(_stationTerminalFrom is {} terminalFrom)
        {
            // Exit returns to the normal avatar camera in one second
            // (0063B417..0063B430), using the same retail easing as entry.
            float phase=(float)Math.Clamp((_stationElapsedMilliseconds-_stationTerminalStarted)/1000d,0,1);
            float weight=.5f-.5f*MathF.Cos(MathF.PI*phase);
            position=Vector3.Lerp(terminalFrom.Position,position,weight);
            rotation=Quaternion.Slerp(terminalFrom.Orientation,rotation,weight);
            if(phase>=1)_stationTerminalFrom=null;
        }
        _stationLastCameraPose=(position,rotation);
        return (interior with {CameraPosition=position,CameraOrientation=rotation}).Camera(width,height);
    }
    private (int W, int H) _stationGpuSize;
    private List<(LiveLanePlan Lane, LiveGpuResource Gpu)> _stationGpu = [];
    private string _stationRenderStatus = "Waiting for station renderer";
    public string StationRenderStatus => Volatile.Read(ref _stationRenderStatus);
    private int _stationRenderFailed;
    public bool StationRenderFailed => Volatile.Read(ref _stationRenderFailed) != 0;

    public void SetStationInterior(EnbCurrentRetailStationInterior? interior)
    {
        if (ReferenceEquals(Volatile.Read(ref _stationInterior), interior)) return;
        if(interior is not null && _stationInterior is not null)_stationPopulation.ChangeRoom(_stationScene);
        else _stationPopulation.Reset();
        if(interior is {PlayDocking:true} && _retailAssetsAccessor() is {} assets)
        {
            _audio?.PrepareStationSound(interior.DockSound,assets);
            _audio?.PrepareStationSound(interior.UndockSound,assets);
            _audio?.PrepareStationSound(interior.AvatarArrivalSound,assets);
            _audio?.PrepareStationSound(interior.AvatarDepartureSound,assets);
        }
        if(interior is {PlayDocking:false})_stationAnimationStarted-=_stationElapsedMilliseconds;
        _stationClockRebaseFrom=interior is not null && _stationInterior is not null?_stationElapsedMilliseconds:null;
        Volatile.Write(ref _stationInterior, interior);
        Volatile.Write(ref _stationRenderFailed, 0);
        Volatile.Write(ref _stationPresentedInterior, null);
        if(interior is null){_stationArrivalSpawnRoom=null;StationArrivalSpawn=null;}
        Volatile.Write(ref _stationDoorPicks, []);
        _stationElapsedMilliseconds = 0;
        _stationInteractionSortTick=0;
        _stationInteractionPriorities.Clear();
        Volatile.Write(ref _stationInteractionPicks,[]);
        _stationDepartureStarted=null;
        Volatile.Write(ref _stationDepartureFrameComplete,false);
        _stationDepartureAudioStarted=false;
        _stationLastCameraPose=null; _stationDepartureCameraPose=null;
        _stationConversationOwner=null;_stationConversationCamera=null;_stationConversationFrom=null;
        _stationTerminalOwner=null;_stationTerminalCamera=null;_stationTerminalFrom=null;
        if (interior is null){ClearStationRoster();StationMovementPaused=false;StationLabelInteractionOpen=false;StationLabelServiceOpen=false;Volatile.Write(ref _stationScene, null);Interlocked.Exchange(ref _stationResetRequested,1);}
        RequestFrame();
    }

    // Device release drops handles, while each retained scene draw list owns
    // its material phases. Weak list identity also retires abandoned rooms/actors.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        List<(LiveLanePlan Lane,LiveGpuResource Gpu)>,
        Dictionary<int,(EnbW3dRenderMesh Mesh,EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState State)>> _stationRecoveryState = new();

    private void ReleaseStationDraws(EnbGlApi? gl,List<(LiveLanePlan Lane,LiveGpuResource Gpu)> draws,bool preserveScene)
    {
        if(preserveScene && draws.Count>0)
        {
            var retained=_stationRecoveryState.GetOrCreateValue(draws);
            for(int i=0;i<draws.Count;i++)
                retained[i]=(draws[i].Lane.Textured!.Submission.Packet.Mesh,RetainGpuState(draws[i].Gpu));
        }
        else if(!preserveScene)_stationRecoveryState.Remove(draws);
        foreach(var draw in draws)draw.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        draws.Clear();
    }

    private EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState? StationRecoveryState(
        List<(LiveLanePlan Lane,LiveGpuResource Gpu)> draws,int index,LiveLanePlan lane)
        => _stationRecoveryState.TryGetValue(draws,out var retained) &&
            retained.TryGetValue(index,out var entry) && ReferenceEquals(entry.Mesh,lane.Textured!.Submission.Packet.Mesh)
            ? entry.State : default;

    private void ConsumeStationRecoveryState(List<(LiveLanePlan Lane,LiveGpuResource Gpu)> draws,int index)
    {
        if(_stationRecoveryState.TryGetValue(draws,out var retained))
        {retained.Remove(index);if(retained.Count==0)_stationRecoveryState.Remove(draws);}
    }

    private void DestroyStationResources(EnbGlApi? gl,bool changingRoom=false,bool preserveScene=false)
    {
        _stationCameraFrameTarget=null;
        if(!changingRoom && !preserveScene)
        {_stationCameraRoom=null;_stationCameraPrevious=null;_stationCameraRetainedRooms.Clear();}
        Volatile.Write(ref _stationPresentedInterior, null);
        if(!preserveScene)_stationGpuContextLost=null;
        if(changingRoom && _stationGpuAsset is {} resident)
        {
            _stationResidentRooms[resident.Resource]=_stationGpu;
            _stationResidentFixtures[resident.Resource]=_stationFixtures;
            _stationFixtures=[];
        }
        else
        {
            if(!preserveScene)_stationLightingState.Reset();
            foreach(var draws in _stationResidentRooms.Values)ReleaseStationDraws(gl,draws,preserveScene);
            if(!preserveScene)_stationResidentRooms.Clear();
            foreach(var fixtures in _stationResidentFixtures.Values)foreach(var draws in fixtures.Values)
                ReleaseStationDraws(gl,draws,preserveScene);
            if(!preserveScene)_stationResidentFixtures.Clear();
            ReleaseStationDraws(gl,_stationGpu,preserveScene);
            _stationPreparedRooms.Clear();_stationUploadingRoom=null;
            _stationPreparedPeople.Clear();
        }
        if(!preserveScene)_stationGpu=[];
        _stationGpuAsset = null;
        _stationShipRenderer?.ReleaseGpuContext(gl);
        if(!preserveScene)_stationShipRenderer = null;
        if(changingRoom){_stationCollision=null;}
        else DestroyStationAvatar(gl,preserveScene);
        DestroyStationPeople(gl,preserveScene,changingRoom);
        if(changingRoom){_stationDoorFrames.Clear();DetachStationBoothSlot();}
        else DestroyStationFixtures(gl,preserveScene);
        DestroyStationBeam(gl,preserveScene);
        if(!preserveScene && !changingRoom){_stationArrivalBeam=null;_stationDepartureBeam=null;Array.Clear(_stationBeamFailures);}
        _stationGpuArrivalFrame = -1;
        if (gl is not null || !preserveScene)
        {
            _audio?.StopStationSound();
            if (!changingRoom) _audio?.StopStationAmbient();
        }
    }

    private void RenderStation(EnbGlApi gl, int fb, int width, int height, EnbCurrentRetailStationInterior interior, double uiScale)
    {
        if (!ReferenceEquals(_stationClockAsset, interior))
        {
            _stationClockAsset = interior;
            _stationStarted = Stopwatch.GetTimestamp();
            _stationClockPresented = false;
        }
        bool firstFrame=!_stationClockPresented;
        RenderStationAtTime(gl, fb, width, height, interior,
            (long)Stopwatch.GetElapsedTime(_stationStarted).TotalMilliseconds, uiScale);
        // Cold uploads are loading time, not time spent viewing the arrival clip.
        if(firstFrame && StationFrameReady)
        { _stationStarted=Stopwatch.GetTimestamp();_stationClockPresented=true; }
    }

    // One shared frame clock drives authored mapper programs. Rebuilding GPU
    // resources for a resize must not restart those programs.
    private void RenderStationAtTime(EnbGlApi gl,int fb,int width,int height,
        EnbCurrentRetailStationInterior interior,long elapsedMilliseconds,double uiScale)
    {
        var requestedInterior=interior;
        long performanceStart=Stopwatch.GetTimestamp();
        try
        {
            _stationElapsedMilliseconds = elapsedMilliseconds;
            _stationCameraFrameTarget=null;
            if(_stationClockRebaseFrom is long priorRoomTime)
            {
                // Retail uses one global clock across retained room controllers.
                // Rebase every retained actor, including those hidden elsewhere.
                _stationClockRebaseFrom=null;
                _stationAvatarPlayback?.RebaseTime(priorRoomTime*.001,0);
                foreach(var person in _stationRemotePeople.Values.Concat(_stationAmbientPeople.Values).Distinct())
                    person.Speech?.RebaseTime(priorRoomTime*.001,0);
            }
            long sceneTime=_stationDepartureStarted is long start ? elapsedMilliseconds-start : elapsedMilliseconds;
            // The authored -out clip owns room/ship motion; the camera transitions to CAMERA_LEAVE.
            if(_stationDepartureStarted is not null)interior=interior with {Arrival=interior.Departure};
            // 00639481 completes the room animation; 0069C9CC then selects
            // panel -1. Its +5C method (00636D50) detaches the ship at +184
            // before 0074BA30 starts loading. The final pose alone does not
            // carry the entire ship through the exit: this detach is visible.
            bool departureFinished=_stationDepartureStarted is not null && sceneTime>=interior.ArrivalDurationMilliseconds;
            if (_stationGpuAsset?.Resource != interior.Resource)
            {
                bool reset=Interlocked.Exchange(ref _stationResetRequested,0)!=0;
                bool recovering=!reset && ReferenceEquals(_stationGpuContextLost,requestedInterior);
                // Recovery already released the GPU resources; the scene and
                // its child renderer continue at their existing clocks/poses.
                if(!recovering)DestroyStationResources(gl,changingRoom:!reset && !interior.PlayDocking);
                // Consume prepared uploads; any incomplete room finishes here.
                if(_stationResidentRooms.Remove(interior.Resource,out var residentDraws))_stationGpu=residentDraws;
                if(_stationResidentFixtures.Remove(interior.Resource,out var residentFixtures))_stationFixtures=residentFixtures;
                var camera = StationCamera(interior, width, height);
                var textures = new Dictionary<string, EnbTextureImage>();
                if (!EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld,
                    interior.BuildDraws(0).Packets.Where(p=>p.SpecialMaterial is null).ToArray(), out var order, out var orderError)) throw new InvalidDataException(orderError);
                foreach (var packet in order.Packets.Skip(_stationGpu.Count))
                {
                    if (!EnbW3dIndexedSubmissionCompiler.TryCompile(packet, out var submission, out var error) ||
                        !TryCompileLane(submission, camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                            EnbRenderTextureBackendCapabilities.ModernDefault, _retailAssetsAccessor()!.Textures,
                            textures, null, LiveLaneKind.Textured, out var lane, out error) ||
                        !TryCreateGpuResource(gl, lane, LiveLaneKind.Textured, 0, out var gpu, out error, StationRecoveryState(_stationGpu,_stationGpu.Count,lane)))
                        throw new InvalidDataException("Station GPU: " + error);
                    ConsumeStationRecoveryState(_stationGpu,_stationGpu.Count);
                    _stationGpu.Add((lane, gpu));
                }
                _stationGpuAsset = interior;
                _stationGpuContextLost=null;
                _stationGpuSize = (width, height);
            }
            if (Volatile.Read(ref _stationScene)?.Objects.FirstOrDefault(o => o.IsLocalShip)?.Avatar is { } identity)
                PrepareStationAvatar(gl, identity);
            PrepareStationNpcPopulation(gl,interior,elapsedMilliseconds);
            PrepareStationRemotePopulation(gl,interior,elapsedMilliseconds);
            if(StationArrivalComplete && _stationDepartureStarted is null && interior.PlayDocking && interior.RoomType==0 &&
                interior.AvatarSpawn is null && _stationMovement is {} arrived && !ReferenceEquals(_stationArrivalSpawnRoom,requestedInterior))
            {
                int max=-1,index=-1;
                arrived.Relocate(interior.SelectArrivalSpawn((low,high)=>
                {
                    max=high;
                    index=StationArrivalSpawnChoice?.Invoke(high) is int shared && shared>=low && shared<=high ? shared : StationArrivalRandom(low,high);
                    return index;
                }));
                StationArrivalSpawn=(index,max);
                _stationArrivalSpawnRoom=requestedInterior;
            }
            _stationLightingState.Enter(interior.Lighting);
            long performanceAssets=Stopwatch.GetTimestamp();
            UpdateStationEmotes(elapsedMilliseconds);
            // Remote players never block (retail: users walk through every other player).
            // 0063BC30/00640F50 cast only against the room object (*(room+0x84)+0x1C) and its
            // bone attachments; 0063CA50 adds remote avatars to the scene, not the room. Retail
            // NPCs do block, but 006366A0 also adds NPC avatars through the scene, so the retail
            // mechanism is not yet traced: keep the established NPC avcollision meanwhile.
            if (_stationMovement is { } movement && _stationCollision is { } collision)
            {
                collision.ReplaceActors(StationFixtureAsset("avcollision"),_stationPeople
                    .Where(p=>p.Key.Npc && p.Value.Failure is null && p.Value.LastPose is not null)
                    .Select(p=>p.Value.LastPose!.SkeletonReference*p.Value.World));
                if (!StationMovementPaused && _stationDepartureStarted is null && StationArrivalComplete && elapsedMilliseconds > _stationMotionTick &&
                    movement.Advance((elapsedMilliseconds-Math.Max(_stationMotionTick,(long)Math.Ceiling(interior.ArrivalDurationMilliseconds)))*.001,collision.Move))
                    _stationMoveUpdate=(movement.Position,movement.Heading,movement.Run);
                _stationMotionTick=elapsedMilliseconds;
            }
            _stationFramePose=null;
            if (_stationDepartureStarted is null && StationArrivalComplete && _stationAvatarAsset is not null)
                PrepareStationAvatarPose(elapsedMilliseconds);
            PrepareStationCameraFollow(interior);
            long performancePose=Stopwatch.GetTimestamp();
            if (_stationGpuSize != (width, height) ||
                _stationGpuArrivalFrame != interior.ArrivalFrame(elapsedMilliseconds) || StationArrivalComplete)
            {
                var camera = StationCamera(interior, width, height);
                if (!EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld,
                    interior.BuildDraws(sceneTime).Packets.Where(p=>p.SpecialMaterial is null).ToArray(), out var order, out var orderError)) throw new InvalidDataException(orderError);
                if (order.Packets.Length != _stationGpu.Count) throw new InvalidDataException("Station animation changed draw topology.");
                for (int i = 0; i < _stationGpu.Count; i++)
                {
                    var draw = _stationGpu[i];
                    var previous = draw.Lane.Textured!;
                    // Immutable topology/material runs were compiled when this GPU draw
                    // was created. TryUpdateFrame validates their identity before reuse.
                    var submission=previous.Submission with {Packet=order.Packets[i]};
                    if (!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous, submission, camera,
                        out var resized, out var error)) throw new InvalidDataException("Station pose: " + error);
                    // Preserve GPU geometry, textures and mutable mapper state.
                    _stationGpu[i] = (LiveLanePlan.From(resized), draw.Gpu);
                }
                _stationGpuSize = (width, height);
                _stationGpuArrivalFrame = interior.ArrivalFrame(elapsedMilliseconds);
            }
            string shipStatus = departureFinished ? "ship departed" : interior.RoomType==0 ? "ship data pending" : $"room {interior.RoomType}";
            long performanceRoom=Stopwatch.GetTimestamp();
            if (!departureFinished && interior.RoomType==0 && Volatile.Read(ref _stationScene) is { } source && source.Objects.Any(o => o.IsLocalShip))
            {
                if (_stationShipRenderer is null)
                {
                    _stationShipRenderer = new(() => _retailAssetsAccessor()?.BaseAssetResolver, _retailAssetsAccessor,
                        _fixedSessionIdAccessor, enableAudio: false, useMainViewEnvironment: false, continuousPresentation: false);
                    _stationShipRenderer._mapModelDetail = true;
                    _stationShipRenderer._graphicsOptionsOwner = this;
                    _stationShipRenderer.InitializeGl(gl);
                }
                var scene = interior.DockedScene(source, sceneTime);
                _stationShipRenderer._customizationLightEnvironment = _stationLightingState.Current.Select(interior.PivotTransform("SHIP",sceneTime).Translation).Environment;
                _stationShipRenderer._stationShipSceneFog=interior.Fog;
                var frame = new LiveFrame(scene, StationCamera(interior, width, height), false, scene.ClientTime,
                    Stopwatch.GetTimestamp(), null, 0, ImmutableDictionary<LivePlanetGpuIdentity, float>.Empty,
                    ImmutableDictionary<LivePlanetGpuIdentity, uint>.Empty);
                _stationShipRenderer._publishStationShipGeometry=interior.HangarAttachments is null;
                _stationShipRenderer.RenderFrame(gl, fb, width, height, frame);
                PublishStationShipPickProxy(interior,width,height);
                shipStatus = "ship " + scene.Objects[0].Name;
            }
            long performanceShip=Stopwatch.GetTimestamp();
            SubmitSpecialScene(gl,fb,width,height,_stationGpu,
                interior.Resource.SpecialMaterials.Count==0?[]:interior.BuildDraws(sceneTime).Packets.Where(p=>p.SpecialMaterial is not null).ToArray(),
                StationCamera(interior,width,height),EnbRetailStationLighting.UnregisteredEnvironment,new Vector3(.5f),unchecked((uint)elapsedMilliseconds));
            if (_stationDepartureStarted is null && StationArrivalComplete && _stationAvatarAsset is not null)
                RenderStationAvatar(gl, fb, width, height, interior, elapsedMilliseconds);
            RenderStationBeam(gl,fb,width,height,interior,elapsedMilliseconds);
            RenderStationPeople(gl,fb,width,height,interior,elapsedMilliseconds);
            RenderStationFixtures(gl,fb,width,height,interior,elapsedMilliseconds);
            PublishStationInteractionPicks();
            long performanceActors=Stopwatch.GetTimestamp();
            var options = _retailAssetsAccessor()!.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options;
            _audio?.UpdateEffects(_stationAnimationSoundRequests,_retailAssetsAccessor()!,StationCamera(interior,width,height),
                _=>null,options.SoundSfx/100f,sampleTime:unchecked((uint)Environment.TickCount),dopplerEnabled:options.DopplerEnabled);
            _stationAnimationSoundRequests.Clear();
            if (_audio is not null) _audio.DialogGain = options.SoundDialog / 100f;
            if(_stationDepartureStarted is not null && !_stationDepartureAudioStarted)
            {_audio?.StopStationSound();_audio?.StopStationAmbient();_stationDepartureAudioStarted=true;}
            if (_stationDepartureStarted is null && _stationScene?.ActiveStarbaseId is uint starbaseId)
                _audio?.UpdateStationAmbient(starbaseId, interior.RoomType, interior.ResourceName, _retailAssetsAccessor()!,
                    Environment.TickCount64, options.SoundSfx / 100f, options.SoundMusic / 100f, StationCamera(interior, width, height),
                    () => {
                        var pivots = interior.Resource.Hierarchies.Single().Pivots;
                        return interior.PivotTransform(pivots[EnbRetailPresentationRandom.Shared.Next(0,pivots.Count-1)].Name, elapsedMilliseconds).Translation;
                    }, interior.SoundObjects(elapsedMilliseconds,ReadMapperClock()), options.DopplerEnabled);
            // Departure is a shared UI voice: the HUD owns its full tail through loading.
            if(interior.PlayDocking && _stationDepartureStarted is null) _audio?.UpdateStationSound(interior.DockSound, _retailAssetsAccessor()!, options.SoundSfx / 100f,Math.Max(0,sceneTime)*.001);
            var speech=_stationConversationOwner is null?null:_stationScene?.StationConversation?.Tree;
            if(ReferenceEquals(speech,_stationConversationPreviousTree))speech=null;
            _audio?.UpdateStationSpeech(speech,speech?.NpcSoundFileName,_retailAssetsAccessor()!,options.SoundSfx/100f);
            if(_stationDepartureStarted is not null && interior.AvatarDepartureSound is {} leaving && sceneTime>=interior.AvatarDepartureSoundDelayMilliseconds)
                _audio?.UpdateStationBeamSound(leaving,_retailAssetsAccessor()!,options.SoundSfx/100f);
            if (interior.PlayDocking && _stationDepartureStarted is null && interior.AvatarArrivalSound is { } beamSound &&
                elapsedMilliseconds-interior.ArrivalDurationMilliseconds >= interior.AvatarArrivalSoundDelayMilliseconds)
                _audio?.UpdateStationBeamSound(beamSound,_retailAssetsAccessor()!,options.SoundSfx/100f);
            long performanceAudio=Stopwatch.GetTimestamp();
            gl.SceneAnisotropicFiltering=false;fb=_supersampling.Complete(gl,fb);if(_bloom.CompleteScene(gl,fb,width,height,EnhancedGraphics.BloomActive) is {} postFailure)LastRenderFailure=postFailure;
            RenderStationNames(gl,fb,width,height,uiScale);
            if(_stationGpu.Count>0) Volatile.Write(ref _stationPresentedInterior,requestedInterior);
            WarmStationGpu(gl);
            long performanceEnd=Stopwatch.GetTimestamp();
            _framePerformanceScene="Station";
            _framePerformanceStages=[ProfileMilliseconds(performanceStart,performanceAssets),
                ProfileMilliseconds(performanceAssets,performancePose),ProfileMilliseconds(performancePose,performanceRoom),
                ProfileMilliseconds(performanceRoom,performanceShip),ProfileMilliseconds(performanceShip,performanceActors),
                ProfileMilliseconds(performanceActors,performanceAudio),ProfileMilliseconds(performanceAudio,performanceEnd)];
            _lastFrameProfile=$"render profile: station CPU ms · assets {_framePerformanceStages[0]:0.00} · pose {_framePerformanceStages[1]:0.00} · room prep {_framePerformanceStages[2]:0.00} · ship {_framePerformanceStages[3]:0.00} · room/actors {_framePerformanceStages[4]:0.00} · audio {_framePerformanceStages[5]:0.00} · names/preload {_framePerformanceStages[6]:0.00}";
            // Publish only after the ship-free terminal hangar has rendered.
            // The UI thread must not tear down this view based on the clock written
            // at callback entry while that final GPU frame is still being built.
            if(departureFinished)
                Volatile.Write(ref _stationDepartureFrameComplete,true);
            Volatile.Write(ref _stationRenderFailed, 0);
            Volatile.Write(ref _stationRenderStatus, $"{interior.ResourceName} · {_stationGpu.Count} room GPU draws · {shipStatus} · " +
                (_stationDepartureStarted is not null ? $"departure {interior.ArrivalFrame(sceneTime):0}/{interior.Arrival?.NumFrames-1}" : StationArrivalComplete ? $"avatar {_stationAvatarGpu.Count} draws · third person" : $"arrival {interior.ArrivalFrame(elapsedMilliseconds):0}/{interior.Arrival?.NumFrames-1}") +
                $" · {_stationPeople.Values.Count(p=>p.Failure is null)} people · {StationVisibleNameCount} names{StationNameDiagnostics} · {_stationFixtures.Count} fixtures · material animation {elapsedMilliseconds}ms · {_audio?.StationSoundStatus ?? "audio disabled"} · beam {_audio?.StationBeamSoundStatus}" + _audio?.StationAmbientStatus + _audio?.StationSpeechStatus + _audio?.ClientSoundStatus + StationPeopleWarnings + StationBeamWarnings + StationFixtureWarnings);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _stationRenderFailed, 1);
            Volatile.Write(ref _stationPresentedInterior, null);
            Volatile.Write(ref _stationRenderStatus, "Station render blocked: " + ex);
        }
    }
}
