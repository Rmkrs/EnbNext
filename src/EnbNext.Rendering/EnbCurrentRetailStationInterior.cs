using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Initial hangar environment; current client 006A2120, 0069B5E0, 0069E260.</summary>
public sealed record EnbCurrentRetailStationInterior(
    string ResourceName, EnbW3dRenderResource Resource, Vector3 CameraPosition, Quaternion CameraOrientation)
{
    // Current client 006366A0 selects this local reflection environment on
    // station entry. It is not the server's 16-bit space backdrop identifier.
    public const int ReflectionBaseAsset = 100000;
    public IReadOnlyList<EnbStationMachinery> Machinery { get; init; } = [];
    public System.Collections.Immutable.ImmutableArray<EnbStationDoorway> Doorways { get; init; } = [];
    public uint RoomType { get; init; }
    public EnbRetailStationLighting Lighting { get; init; } = new(Vector3.Zero, .5f, []);
    public EnbW3dSceneFogState Fog { get; init; } = new(false,0,0,1000);
    public static EnbW3dSceneFogState ResolveRoomFog(EnbSceneStationRoom room)
    {
        // 00636B24..43 gates on byte+C (wire0x100). 006A20C0 passes
        // RGB then near/far; 00965460 uses truncating FISTP, without tuning.
        if((room.Flags&0x100)==0)return new(false,0,0,1000);
        var p=room.Parameters;
        int r=unchecked((int)MathF.Truncate(BitConverter.UInt32BitsToSingle(p[2])*255f));
        int g=unchecked((int)MathF.Truncate(BitConverter.UInt32BitsToSingle(p[3])*255f));
        int b=unchecked((int)MathF.Truncate(BitConverter.UInt32BitsToSingle(p[4])*255f));
        return new(true,unchecked((uint)((r<<16)|(g<<8)|b)),
            BitConverter.UInt32BitsToSingle(p[0]),BitConverter.UInt32BitsToSingle(p[1]));
    }
    public EnbW3dRenderHierarchy? HangarAttachments { get; init; }
    public EnbW3dRenderHierarchy? ExtraAttachments { get; init; }
    public EnbW3dRenderResource? SupplementalResource { get; init; }
    public string? AttachmentDiagnostic { get; init; }

    public EnbCurrentRetailStationInterior WithSupplementalAttachments(EnbRetailAssetBundle assets,string name)
    {
        EnbW3dRenderHierarchy? hierarchy=null;
        EnbW3dRenderResource? retained=null;
        string? diagnostic=null;
        try
        {
            //6333E0 uses the ordinary asset manager: loose search paths,
            //packages, then the parent-prefixed file on a failed primary open.
            if(!assets.W3dFiles.TryResolveLazyFile(Path.GetFileNameWithoutExtension(name),out var source,out diagnostic))
                diagnostic="Station attachments unavailable: "+name+"; "+diagnostic;
            else if(!EnbW3dRenderResourceCompiler.TryCompile(source,out var resource,out diagnostic)) { }
            else if(resource.Hierarchies.Count!=1)
                diagnostic="Station attachments require one hierarchy: "+name;
            else {hierarchy=resource.Hierarchies[0];retained=resource;}
        }
        catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
        {diagnostic=name+": "+ex.Message;}
        // 6333E0 retains a null factory result at +BC. 6390A0 then uses
        // the base room's five arrival spawns; 642740 skips ambient routes.
        // Malformed optional data is contained at this same boundary in Next.
        return this with{ExtraAttachments=hierarchy,HangarAttachments=RoomType==0?hierarchy:null,SupplementalResource=retained,
            AttachmentDiagnostic=diagnostic};
    }
    public Matrix4x4 HangarAttachmentTransform(string name)
    {
        var hierarchy=HangarAttachments??throw new InvalidDataException("Hangar attachments unavailable.");
        var pivot=hierarchy.Pivots.Single(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        if(!EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,out var pose,out var error))throw new InvalidDataException(error);
        return pose[pivot.Ordinal];
    }
    public string SpawnPivot { get; init; } = "SPAWN00";
    // 006415B0 -> 006390A0: after the entry animation, select from the
    // supplemental hangar's 18 spawns, or the base room's five when absent.
    public Matrix4x4 SelectArrivalSpawn(Func<int,int,int>? random=null)
    {
        int index=(random??EnbRetailPresentationRandom.Shared.Next)(0,HangarAttachments is null?4:17);
        string name=$"SPAWN{index:00}";
        return HangarAttachments is null?PivotTransform(name):HangarAttachmentTransform(name);
    }
    // 00640150 -> RenderObj+B8 (009C5CB0): absent name resolves bone zero.
    public Matrix4x4 SelectResetSpawn(EnbRetailCameraRandomSource? random = null)
    {
        var hierarchy = Resource.Hierarchies.Single();
        int bone = EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy, "safe_spot");
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out var pose, out var error))
            throw new InvalidDataException(error);
        var safe = pose[bone];
        var rotated = safe * Matrix4x4.CreateRotationZ((random ?? EnbRetailCameraRandomSource.Process).Scale(MathF.Tau));
        rotated.Translation = safe.Translation;
        return rotated;
    }
    public bool PlayDocking { get; init; } = true;
    public Matrix4x4? AvatarSpawn { get; init; }
    public EnbW3dRenderHierarchyAnimation? Arrival { get; init; }
    public EnbW3dRenderHierarchyAnimation? Departure { get; init; }
    public string? ArrivalDiagnostic { get; init; }
    public string? DepartureDiagnostic { get; init; }
    public EnbCurrentRetailStationInterior WithDockingAnimations(EnbRetailAssetBundle assets,string arrivalName,string departureName)
    {
        var resource=Resource;
        EnbW3dRenderHierarchyAnimation? Resolve(string name,out string? diagnostic)
        {
            try
            {
                if(EnbCurrentRetailRslHAnimResolver.TryResolve(resource,new("",EnbRslFileDependencyStatus.NotAuthored),
                    assets.Paths,assets.MixResources,assets.RetailMixResources!,name,out var resolved,out diagnostic))
                {resource=resolved.DrawResource;return resolved.Animation;}
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
            {diagnostic=ex.Message;}
            return null;
        }
        // 5DCD20 leaves a failed request without combo data; 5DD3C0 rejects
        // it locally. 5BA320 treats the empty slot as completed, allowing
        // 6390A0 to finish arrival/departure with the room still present.
        var arrival=Resolve(arrivalName,out var arrivalDiagnostic);
        var departure=Resolve(departureName,out var departureDiagnostic);
        return this with{Resource=resource,Arrival=arrival,Departure=departure,
            ArrivalDiagnostic=arrivalDiagnostic,DepartureDiagnostic=departureDiagnostic};
    }
    public string? AvatarArrivalSound { get; init; }
    public double AvatarArrivalSoundDelayMilliseconds { get; init; }
    public string? AvatarDepartureSound { get; init; }
    public double AvatarDepartureSoundDelayMilliseconds { get; init; }
    public string UndockSound => DockSound.Replace("_dock_","_undock_",StringComparison.Ordinal);
    public string DockSound => ResourceName[0] switch
    {
        't' => "terren_space_ship_dock_1.wav",
        'j' => "jenquai_space_ship_dock_1.wav",
        'p' => "progen_space_ship_dock_1.wav",
        _ => throw new InvalidDataException("Unknown station docking sound.")
    };
    public double ArrivalDurationMilliseconds => Arrival is { } animation ? (animation.NumFrames - 1) * 1000d / animation.FrameRate : 0;
    public float ArrivalFrame(long milliseconds) => Arrival is { } animation
        ? (float)Math.Clamp(milliseconds * animation.FrameRate / 1000d, 0, animation.NumFrames - 1) : 0;
    public Matrix4x4 ShipTransform => PivotTransform("SHIP");
    public Matrix4x4 PivotTransform(string name, long? milliseconds = null)
    {
        var hierarchy = Resource.Hierarchies.Single();
        var pivot = hierarchy.Pivots.Single(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity,
            milliseconds.HasValue ? Arrival : null, milliseconds.HasValue ? ArrivalFrame(milliseconds.Value) : 0,
            out var pose, out var error))
            throw new InvalidDataException(error);
        return pose.WorldTransforms[pivot.Ordinal];
    }

    private readonly EnbSceneFrame _dockedScene=new();
    private readonly EnbSampledSceneObject _dockedShip=new();
    private IEnbRenderObject[]? _dockedObjects;
    public IEnbSceneFrame DockedScene(IEnbSceneFrame scene,long? milliseconds=null)
    {
        _dockedScene.AdoptMetadata(scene);
        _dockedScene.Objects=[];_dockedScene.Effects=[];_dockedScene.StationAvatars=[];_dockedScene.Environment=null;
        var ship=scene.Objects.FirstOrDefault(o=>o.IsLocalShip);
        if(ship is null)return _dockedScene;
        if(!Matrix4x4.Decompose(PivotTransform("SHIP",milliseconds),out _,out var rotation,out var position))
            throw new InvalidDataException("Invalid station ship attachment.");
        // 006415B0 attaches the retained ship to the room's SHIP transform.
        _dockedShip.AdoptViewObject(ship);_dockedShip.ShipVisualBankRadians=null;
        _dockedShip.SetFixedPose(new(true,new(scene.ClientTime,position,rotation,Vector3.Zero,0,Vector3.Zero)
            {OrientationColumn0=Vector3.Transform(Vector3.UnitX,rotation)}));
        _dockedScene.Objects=_dockedObjects??=[_dockedShip];
        _dockedScene.Lighting=new(0x7f7f7f,[]);
        return _dockedScene;
    }

    public static string RoomResourceName(uint definition,uint roomType,uint flags)
    {
        if (definition > 9) throw new InvalidDataException("Unsupported station definition " + definition);
        string race=definition switch {0=>"t",1=>"j",2=>"p",_=>"n"};
        string name = roomType switch {0=>definition switch {0=>"tsd01a",1=>"jsd01a",2=>"psd01a",_=>"tsd07a"},
            1=>race+"bar01_h",2=>race+"bar01_v",3=>race+"bar01",_=>throw new InvalidDataException("Unsupported station room "+roomType)};
        return name + ((flags & 0x80) != 0 ? "3" : (flags & 0x40) != 0 ? "2" : "");
    }

    public static EnbCurrentRetailStationInterior Load(EnbRetailAssetBundle assets, EnbSceneStationDefinition definition,uint roomType=0)
    {
        var room = definition.Rooms.Single(r => r.RoomType == roomType);
        var resources = assets.RetailMixResources ?? throw new InvalidDataException("Retail resources unavailable.");
        string name = RoomResourceName(definition.DefinitionValue,roomType,room.Flags);
        string machineryRoom = name;
        if (!resources.TryLocateResource(name + ".w3d", out _)) name = RoomResourceName(definition.DefinitionValue,roomType,0);
        if (!resources.TryReadResource(name + ".w3d", out var bytes, out var location) ||
            !EnbW3dModel.TryDecode(bytes, out var model)) throw new InvalidDataException("Missing station model " + name);
        var textures = model.Meshes.SelectMany(m => m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => new EnbResolvedTextureReference(n, resources.TryLocateResource(n, out var t) ? t : null)).ToArray();
        var source = new EnbResolvedW3dResource(location.Name, name, location, model, textures);
        if (!EnbW3dRenderResourceCompiler.TryCompile(source, out var resource, out var error)) throw new InvalidDataException(error);
        var hierarchy = resource.Hierarchies.Single();
        var pivot = hierarchy.Pivots.Single(p => p.Name.Equals(roomType==0 ? "CAMERA_3RD" : "CAMERA_1ST", StringComparison.OrdinalIgnoreCase));
        if (!EnbW3dHierarchyPose.TryEvaluate(hierarchy, Matrix4x4.Identity, out var pose, out error) ||
            !Matrix4x4.Decompose(pose[pivot.Ordinal], out _, out var rotation, out var position))
            throw new InvalidDataException(error ?? "Invalid station camera transform.");
        // Retail's two -pi/2 basis rotations convert authored X-forward/Z-up
        // into its -Z-forward camera. EnbNext already uses authored X/-Y/Z.
        // 006415B0 uses the literal "%sa.%sa-in" (00B8B704); the hyphen
        // is significant and is lost in Ghidra's generated string symbol name.
        if(roomType!=0)return WithDoors(new(name,resource,position,rotation) {RoomType=roomType,PlayDocking=false});
        EnbW3dAnimationSoundCue? cue=null;
        uint soundFrameRate=0;
        if(assets.AnimationSounds?.TryGet("av_tele03a.Av_tele03a-1",out var soundTrack)==true &&
            resources.TryReadResource("av_tele03a-1.w3d",out var soundBytes,out _) && EnbW3dModel.TryDecode(soundBytes,out var soundModel))
        { cue=soundTrack.Cues.FirstOrDefault(); soundFrameRate=soundModel.Animations.Single().FrameRate; }
        EnbW3dAnimationSoundCue? departureCue=null;
        uint departureRate=0;
        // 00788F90 state 3 selects av_tele02a.Av_tele02a-1.
        if(assets.AnimationSounds?.TryGet("av_tele02a.Av_tele02a-1",out var departureTrack)==true &&
            resources.TryReadResource("av_tele02a-1.w3d",out var departureBytes,out _) && EnbW3dModel.TryDecode(departureBytes,out var departureModel))
        {departureCue=departureTrack.Cues.FirstOrDefault();departureRate=departureModel.Animations.Single().FrameRate;}
        return WithDoors(new EnbCurrentRetailStationInterior(name, resource, position, rotation) {
            AvatarDepartureSound=departureCue?.Sound,AvatarDepartureSoundDelayMilliseconds=departureCue is null?0:departureCue.Frame*1000d/departureRate,
            AvatarArrivalSound=cue?.Sound, AvatarArrivalSoundDelayMilliseconds=cue is null?0:cue.Frame*1000d/soundFrameRate }
            .WithDockingAnimations(assets,name+"."+name+"-in",name+"."+name+"-out"));
        EnbCurrentRetailStationInterior WithDoors(EnbCurrentRetailStationInterior interior)
        {
            {
                // 006333E0 + table 00B8A880 loads supplemental bones for every
                // room. 00642740 uses these for authored ambient NPC routes.
                string prefix=definition.DefinitionValue switch {0=>"t",1=>"j",2=>"p",_=>"n"};
                string extraRoom=roomType switch {0=>"hangar",1=>"main",2=>"bazaar",3=>"lounge",_=>throw new InvalidDataException("Unsupported station room "+roomType)};
                string extra=prefix+"extra_"+extraRoom+((room.Flags&0x80)!=0?"_3":(room.Flags&0x40)!=0?"_2":"")+".w3d";
                interior=interior.WithSupplementalAttachments(assets,extra);
            }
            return interior with {Lighting=EnbRetailStationLighting.Load(assets,definition.DefinitionValue,roomType,room.Flags,interior.Resource),
                Fog=ResolveRoomFog(room),Doorways=EnbStationDoorway.Load(assets,definition,interior), Machinery=EnbStationMachinery.Load(assets,machineryRoom)};
        }
    }

    public EnbResolvedRenderCamera Camera(int width, int height)
        => EnbResolvedRenderCamera.TryCreate(CameraPosition, CameraOrientation, new(width, height, 1),
            // 0069B5E0 uses CameraCtor's 4:3 projection with FOV(1.134464,-1).
            1.134464, 4.0 / 3.0, new(1, 2000), out var camera) ? camera : throw new InvalidDataException("Invalid station viewport.");

    public EnbW3dDrawInvocation BuildDraws(long time)
        => BuildModelDraws(Resource,Matrix4x4.Identity,time,Arrival is {} animation ? new(animation.Name,ArrivalFrame(time)) : null);

    public IReadOnlyList<EnbStationSoundObject> SoundObjects(long time,uint? machineryClock=null)
        => [new(ResourceName, Resource, Arrival is {} animation ? new(animation.Name, ArrivalFrame(time)) : null),
            .. Machinery.Select(m => new EnbStationSoundObject(m.Name, m.Resource, m.Sample(machineryClock??unchecked((uint)time))))];

    public static EnbW3dDrawInvocation BuildModelDraws(EnbW3dRenderResource resource,Matrix4x4 world,long time,EnbW3dAnimationSample? sample=null,bool retainRenderHidden=false)
    {
        // Presentation-only identity for the shared W3D compiler. Never inserted
        // into Net7 world state or used for requests/targeting.
        var owner = new EnbSceneObjectSnapshot(1, 1, 0, false, false, 0, 0, 1, 0, 0, 0, 0, false,
            "station", new(false, 0, null), null, null, null, [], [], [], [], [], [], null, [], null, 0, 0);
        var hlod = resource.HierarchicalLods.SingleOrDefault();
        // The entry assets have one authored level. Do not guess a policy for
        // a different asset until its station detail behavior is established.
        EnbW3dHlodSelection? selection = hlod is null ? null : new(hlod.Name, hlod.Levels.Single().Ordinal);
        if (!EnbW3dDrawPacketCompiler.TryBuildAtWorldTransform(owner, resource, world, time,
            EnbRenderPassKind.OrdinaryWorld, selection, EnbRetailStationLighting.UnregisteredEnvironment,
            sample,
            out var draws, out var error)) throw new InvalidDataException(error);
        // MeshClass load at 00940470 applies the authored hidden bit through
        // RenderObj's visibility setter. Keep these meshes in Resource for
        // collision, but do not submit them (vendor booths include such hulls).
        if(!retainRenderHidden)draws.Packets.RemoveAll(static p=>(p.Mesh.Attributes&0x1000)!=0);
        return draws;
    }
}
