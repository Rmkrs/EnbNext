using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _stationPersonTextureRevision;
    private EnbSceneAvatarSnapshot? _stationAvatarIdentity;
    private EnbAvatarPortraitAsset? _stationAvatarAsset;
    private readonly List<(LiveLanePlan Lane, LiveGpuResource Gpu)> _stationAvatarGpu = [];
    private EnbStationMovement? _stationMovement;
    private EnbCurrentRetailStationInterior? _stationArrivalSpawnRoom;
    internal Func<int,int,int> StationArrivalRandom = EnbRetailPresentationRandom.Shared.Next;
    /// <summary>Shared spawn: index for the highest spawn index, or null for the random draw.</summary>
    public Func<int,int?>? StationArrivalSpawnChoice { get; set; }
    /// <summary>The arrival spawn chosen in the current station: (index, highest index).</summary>
    public (int Index,int Max)? StationArrivalSpawn { get; private set; }
    private EnbStationCollision? _stationCollision;
    private long _stationMotionTick;
    private string _stationAnimationTag = "breathe1";
    private long _stationAnimationStarted;
    private EnbAvatarPortraitAsset? _stationLastPose;
    private EnbAvatarPortraitAsset? _stationFramePose;
    private bool _stationThirdPersonMode=true;
    public bool StationThirdPersonMode
    {
        get=>_stationThirdPersonMode;
        set
        {
            if(value==_stationThirdPersonMode)return;
            _stationThirdPersonMode=value;_stationCameraFrameTarget=null;
            //6388C0 copies the newly selected camera into the follow state.
            if(_stationInterior is {} room && _stationAvatarAsset is {} avatar)
            {
                _stationCameraPrevious=StationAvatarCameraTransform(room,avatar).Translation;
                _stationCameraRetainedRooms[room.RoomType]=_stationCameraPrevious.Value;
            }
        }
    }
    private LiveAvatarSpeechPlayback? _stationAvatarPlayback;
    internal IReadOnlyList<EnbNext.Net7.Gameplay.Net7PlayerNlpRecord>? ProduceStationAvatarChat(string text)
        =>_stationAvatarPlayback?.ProduceChat(text);
    internal EnbAvatarPortraitAsset? StationLocalPortrait(EnbSceneAvatarSnapshot identity)
        => _stationInterior is not null && SamePortrait(_stationAvatarIdentity,identity) ? _stationLastPose : null;
    private (Vector3 Position,float Heading,bool Run)? _stationMoveUpdate;
    public (Vector3 Position,float Heading,bool Run)? TakeStationMovementUpdate()
    { var update=_stationMoveUpdate; _stationMoveUpdate=null; return update; }
    public void SetStationMovement(int thrust,float turn,bool run)
    {
        if(_stationMovement is not {} movement)return;
        movement.Thrust=Math.Clamp(thrust,-1,1); movement.Turn=Math.Clamp(turn,-1,1); movement.Run=run; movement.Free=null;
    }
    /// <summary>Next free station movement: the mouse turns the view; with turnAvatar (or in first person) the avatar faces it.</summary>
    public void TurnStationFree(float yaw,float pitch,bool turnAvatar)
    {
        if(_stationMovement is {} movement)movement.TurnFree(yaw,pitch,turnAvatar || !StationThirdPersonMode);
    }
    /// <summary>Next free station movement: the wished (right, forward) direction from the camera; turn steers the avatar in first person.</summary>
    public void SetStationFreeMovement(System.Numerics.Vector2 wish,float turn,bool run)
    {
        if(_stationMovement is not {} movement)return;
        movement.Thrust=0; movement.Turn=StationThirdPersonMode?0:Math.Clamp(turn,-1,1); movement.Run=run;
        movement.Free=wish; movement.FreeFirstPerson=!StationThirdPersonMode;
    }
    public void SetStationThrust(int thrust,bool run)
    {if(_stationMovement is {} movement){movement.Thrust=Math.Clamp(thrust,-1,1);movement.Run=run;}}
    internal EnbRetailCameraRandomSource StationResetRandom = EnbRetailCameraRandomSource.Process;
    public bool ResetStationAvatar()
    {
        if (_stationMovement is not {} movement || _stationInterior is not {} room) return false;
        movement.Relocate(room.SelectResetSpawn(StationResetRandom));
        return true;
    }
    public void StopStationMovement() => _stationMovement?.Stop();
    public void SetStationLook(float look) { if(_stationMovement is {} movement)movement.Look=look; }
    public void SetStationCameraTurn(float turn)
    {
        if(_stationMovement is {} movement && (StationThirdPersonMode || turn==0))movement.CameraTurn=turn;
    }
    public void RestoreStationCamera() => _stationMovement?.RestoreCamera();
    private void DestroyStationAvatar(EnbGlApi? gl,bool preserveScene=false)
    {
        ReleaseStationDraws(gl,_stationAvatarGpu,preserveScene);
        if(preserveScene)return;
        _stationAvatarIdentity=null; _stationAvatarAsset=null;
        _stationMovement=null; _stationCollision=null; _stationMoveUpdate=null;
        _stationLastPose=null;_stationFramePose=null;
        _stationAvatarPlayback=null;
    }
    private void PrepareStationAvatar(EnbGlApi gl, EnbSceneAvatarSnapshot identity)
    {
        bool appearanceRefresh=false;
        if(!SamePortrait(_stationAvatarIdentity,identity))
        {
            var movement=_stationMovement;
            appearanceRefresh=movement is not null;
            DestroyStationAvatar(gl);
            _stationMovement=movement;
            _stationAvatarAsset=EnbAvatarPortraitAsset.Load(_retailAssetsAccessor()!,identity);
            _stationAvatarIdentity=identity;

            _stationAvatarPlayback=new(_retailAssetsAccessor()!,identity,portraitIdle:true);
            // 0063BDB0/0063C080 disable only +230 for the local station actor.
            _stationAvatarPlayback.Idle.HeadMotionEnabled=false;
            // 63C080 acquires74C7B0 ->77E3F0's initialized idle actor,
            // before arrival visibility permits its first world update.
            _stationAvatarPlayback.StartIdle(_stationElapsedMilliseconds*.001);
        }
        if(_stationCollision is not null)return;
        var spawn=_stationInterior!.AvatarSpawn ?? _stationInterior.PivotTransform(_stationInterior.SpawnPivot);
        bool continuing=_stationMovement is not null;
        if(continuing && !appearanceRefresh)_stationMovement!.Relocate(spawn);
        // Retail avatar constructor 0077BA58 initializes +2F4 to 1 before
        // the arrival publication. The input loop applies the saved run option later.
        else if(!continuing)_stationMovement=new(spawn) { Run=true };
        _stationCollision=new(_stationInterior,_retailAssetsAccessor()!,_stationAvatarAsset!.HeadAnchor.Z-_stationAvatarAsset.SkeletonReference.Translation.Z);
        foreach(var fixture in StationFixturePlacements(_stationInterior))
            if(!fixture.Key.Contains("/icon/",StringComparison.Ordinal) && !fixture.Key.EndsWith("/sign",StringComparison.Ordinal))_stationCollision.Add(StationFixtureAsset(fixture.Name),fixture.World);
        _stationMotionTick=_stationElapsedMilliseconds;
        if(!continuing){_stationAnimationStarted=(long)Math.Ceiling(_stationInterior.ArrivalDurationMilliseconds);_stationAnimationTag="breathe1";}
    }

    private Matrix4x4 StationAvatarCameraTransform(EnbCurrentRetailStationInterior room, EnbAvatarPortraitAsset avatar)
    {
        // 00787A90/00786EB0 sample the current body pivots. Third-person
        // height uses the model's stored head/reference difference (+234);
        // first-person uses the current K_HEAD height, +0.2 and +0.5 forward.
        var spawn=_stationMovement?.Transform ?? room.AvatarSpawn ?? room.PivotTransform(room.SpawnPivot);
        var posed=_stationFramePose??avatar;
        var reference=posed.SkeletonReference*spawn;
        return EnbRetailStationCameraTransform.Build(reference,Vector3.Transform(posed.HeadAnchor,spawn).Z,
            avatar.HeadAnchor.Z-avatar.SkeletonReference.Translation.Z,_stationMovement?.CameraPitch??0,_stationMovement?.CameraYaw??0,StationThirdPersonMode);
    }

    private void PrepareStationAvatarPose(long elapsed)
    {
        var assets=_retailAssetsAccessor()!;
        string tag=_stationMovement?.Moving switch { >0 => _stationMovement.Run ? "run_forward" : "walk_forward", <0 => "walk_backward", _ => "breathe1" };
        bool locomotion=tag!="breathe1";
        uint localId=_stationScene!.LocalAvatarObjectId??0;
        var playback=_stationAvatarPlayback!;
        ReceiveStationCommands(localId,playback,elapsed);
        if(locomotion)playback.ClearPendingCommands();
        playback.SetLocomotion(locomotion?tag:null);
        var posed=playback.Evaluate(elapsed*.001);
        if(_stationAnimationTag!=playback.AnimationTag){_stationAnimationTag=playback.AnimationTag??"breathe1";_stationAnimationStarted=elapsed;}

        _stationFramePose=posed;
        if(posed is null)return;
        _stationLastPose=posed;
        bool step=locomotion && playback.Footstep;
        _audio?.UpdateStationFootstep(step,_stationCollision?.FloorSurface??0,_stationMovement?.Run==true,
            assets,assets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundSfx/100f);
        var world=_stationMovement?.Transform ?? _stationInterior!.PivotTransform("SPAWN00");
        CollectStationAnimationSounds(localId,playback,posed,world,elapsed);
    }

    private void RenderStationAvatar(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior room,long elapsed)
    {
        if(_stationFramePose is not {} posed)return;
        var world=_stationMovement?.Transform ?? room.PivotTransform("SPAWN00");
        RenderStationPerson(gl,fb,width,height,posed,_stationAvatarIdentity!,world,StationCamera(room,width,height),elapsed,_stationAvatarGpu,
            _stationLightingState.Current.Select(world.Translation).Environment);
    }

    private void RenderStationPerson(EnbGlApi gl,int fb,int width,int height,EnbAvatarPortraitAsset posed,
        EnbSceneAvatarSnapshot identity,Matrix4x4 world,EnbResolvedRenderCamera camera,long elapsed,
        List<(LiveLanePlan Lane,LiveGpuResource Gpu)> draws,
        EnbW3dLightEnvironment? sceneLights = null, EnbEnvironmentReflectionDefinition? sceneReflection = null)
    {
        var assets=_retailAssetsAccessor()!;
        // Optional pack changes retire texture uploads, not actor pose or movement.
        if(_stationPersonTextureRevision!=assets.TextureRevision)
        {
            ReleaseStationDraws(gl,_stationAvatarGpu,false);
            foreach(var person in _stationPeople.Values)ReleaseStationDraws(gl,person.Draws,false);
            _stationPersonTextureRevision=assets.TextureRevision;
        }
        // 006366A0 selects the station reflection independently of space lighting.
        var reflection = sceneReflection;
        if(assets.EnvironmentMaps is not { } maps ||
            (reflection is null && !maps.TryResolveReflection(EnbCurrentRetailStationInterior.ReflectionBaseAsset,out reflection)))
            throw new InvalidDataException("Station avatar reflection unavailable.");
        var atlas=ResolveEnvironmentAtlasSet(assets,maps,reflection,0,out var atlasError);
        if(atlas is null)throw new InvalidDataException(atlasError);
        EnbSceneColorizationSnapshot Entry(uint metal,ImmutableArray<float> color)=>new(metal,color.SelectMany(BitConverter.GetBytes).ToImmutableArray());
        EnbSceneColorizationSnapshot[] colors=[Entry(identity.ShirtPrimaryMetal,identity.ShirtPrimaryColor),Entry(identity.ShirtSecondaryMetal,identity.ShirtSecondaryColor),
            Entry(identity.PantsPrimaryMetal,identity.PantsPrimaryColor),Entry(identity.PantsSecondaryMetal,identity.PantsSecondaryColor)];
        int index=0;
        foreach(var part in posed.Parts) foreach(var material in part.Mesh.MaterialPasses)
        {
            // Shared skinning submissions own world-space deformed vertices.
            var geometry=new EnbW3dDeformedGeometry(part.Geometry.Positions.Select(v=>Vector3.Transform(v,world)).ToImmutableArray(),
                part.Geometry.Normals.Select(v=>Vector3.TransformNormal(v,world)).ToImmutableArray());
            if(index==draws.Count)
            {
                var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.OrdinaryWorld,EnbW3dDrawBlendClass.Blended,part.Mesh.StaticSort,
                    part.Resource.Key,part.Mesh.Ordinal,material.Ordinal,2,0),2,1,0,Matrix4x4.Identity,new(world.Translation,20),part.Mesh,geometry,
                    new(material,material.SingleBindingSemantics),sceneLights ?? EnbW3dLightEnvironment.CreateCurrentRetail(0x7f7f7f,[]),elapsed,null,0,part.Mesh.Name);
                packet=EnbCurrentRetailShipColorizationRuntime.Apply(packet,colors,part.Mesh.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase)?1:0,reflection.Axis,out var receipt);
                if(receipt.IsTarget&&!receipt.Applied)throw new InvalidDataException(receipt.Diagnostic);
                var pass=PortraitMaterial(packet.Material.Source,part.Mesh.Name,identity);
                packet=(packet).ForkTask().SetMaterial(new(pass,pass.SingleBindingSemantics));
                if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error)||
                    !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
                        assets.Textures,new Dictionary<string,EnbTextureImage>(),atlas,LiveLaneKind.Textured,out var lane,out error)||
                    !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error,StationRecoveryState(draws,index,lane)))throw new InvalidDataException("Station avatar GPU: "+error);
                ConsumeStationRecoveryState(draws,index);
                draws.Add((lane,gpu));
            }
            else
            {
                var cached=draws[index];
                var previous=cached.Lane.Textured!;
                var packet=(previous.Submission.Packet).ForkTask().SetDeformedGeometry(geometry).SetLightEnvironment(sceneLights ?? previous.Submission.Packet.LightEnvironment);
                var submission=previous.Submission with {Packet=packet};
                if(!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(previous,submission,camera,out var lane,out var error,geometryWillBeUploaded:true))
                    throw new InvalidDataException("Station avatar camera: "+error);
                draws[index]=(LiveLanePlan.From(lane),LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu.Textured!,lane)));
            }
            var draw=draws[index++];
            SubmitGpuResource(gl,fb,width,height,draw.Gpu,draw.Lane,new(false,0,0,0),default,null,unchecked((uint)elapsed));
        }
    }
}
