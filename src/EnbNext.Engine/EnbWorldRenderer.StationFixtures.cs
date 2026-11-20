using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private sealed record DoorPick(uint Destination, EnbRetailMeshPick[] Meshes,bool InRange,Vector3 Origin);
    private DoorPick[] _stationDoorPicks=[];
    private sealed record TerminalPick(EnbNext.Presentation.EnbSceneStationFacility Facility, EnbRetailMeshPick[] Meshes,bool InRange,Vector3 Origin);
    private TerminalPick[] _stationTerminalPicks=[];
    public EnbNext.Presentation.EnbSceneStationFacility? PickStationTerminal(double x,double y)
        =>PickStationInteraction(x,y) is {InRange:true} hit?hit.Facility:null;
    public string? PickStationDoor(double x,double y)
    {
        var hit=PickStationInteraction(x,y);
        return (hit is {InRange:true}?hit.Door:null) switch {0=>"To Hangar",1=>"To Main Deck",2=>"To Bazaar",3=>"To Lounge",_=>null};
    }
    private IEnumerable<(string Key,string Name,Matrix4x4 World)> StationFixturePlacements(EnbCurrentRetailStationInterior room)
        => _stationScene?.StationDefinition is {} definition ? StationFixturePlacements(room,definition) : [];
    private static IEnumerable<(string Key,string Name,Matrix4x4 World)> StationFixturePlacements(EnbCurrentRetailStationInterior room,EnbNext.Presentation.EnbSceneStationDefinition definition)
    {
        bool Has(string pivot)=>room.Resource.Hierarchies.Single().Pivots.Any(p=>p.Name.Equals(pivot,StringComparison.OrdinalIgnoreCase));
        string[] icons=["ico_refinery","ico_analyze","ico_manufacture","ico_jobs","ico_browser","ico_ship_con","ico_ava_con","ico_encyclo"];
        string prefix=definition.DefinitionValue switch {0=>"t",1=>"j",2=>"p",_=>"n"};
        uint flags=definition.Rooms.FirstOrDefault(r=>r.RoomType==room.RoomType)?.Flags ?? 0;
        // 006987A0 maps flags 4/8 to room bytes +6/+7. 00634140
        // attaches %sbar05 at 'bar', and %sbar07 at authored table_%02d bones.
        if((flags&4)!=0 && Has("BAR"))yield return ("BAR",prefix+"bar05",room.PivotTransform("BAR"));
        if((flags&8)!=0)
            foreach(var pivot in room.Resource.Hierarchies.Single().Pivots.Where(p=>p.Name.StartsWith("TABLE",StringComparison.OrdinalIgnoreCase)))
                yield return (pivot.Name,prefix+"bar07",room.PivotTransform(pivot.Name));
        foreach(var facility in definition.Facilities.Where(f=>f.RoomType==room.RoomType))
        {
            string pivot=$"TERMINAL{facility.Slot:00}";
            bool wall=room.RoomType==0 && room.HangarAttachments is not null;
            string attachment=wall?$"WALLTERM{facility.Slot:00}":pivot;
            if(wall?!room.HangarAttachments!.Pivots.Any(p=>p.Name.Equals(attachment,StringComparison.OrdinalIgnoreCase)):!Has(pivot))continue;
            var world=wall?room.HangarAttachmentTransform(attachment):room.PivotTransform(pivot);
            yield return (pivot+"/monitor",prefix+(wall?"_wall_term":"_monitor"),world);
            yield return (pivot+"/icon/"+facility.Type,icons[Math.Min(facility.Type,7)],world);
        }
        string[] booths=["VB_WEAPON","VB_SYSTEM","VB_CORE","VB_CONSUMABLE","VB_JUNK","VB_COMPONENT","VB_RESOURCE","VB_BLACKMKT"];
        // 006987A0 maps wire role 0..7 to internal 2..9; 006976C0 subtracts 2.
        // 00634140 attaches ordinary booths alongside avatars; role 7's
        // dedicated black-market branch constructs only the booth.
        foreach(var npc in definition.RoomNpcs(room.RoomType).Where(n=>n.Variant==0 && n.Role<8))
        {
            string pivot=$"AVATAR{npc.Slot:00}";
            if(Has(pivot))yield return (pivot+"/booth/"+npc.Role,booths[npc.Role],room.PivotTransform(pivot));
        }
        // 00635209..00635451: each connected door also gets its destination's sign, attached to the room's door
        // pivot (00B8AA3C bone names) beside the doorway model.
        foreach(var door in room.Doorways)
            if(door.Destination is uint destination && StationDoorSign(definition,destination) is {} sign)
                yield return (door.Pivot+"/sign",sign,door.Transform);
    }
    // 00B8A9A0, by room class: hangar, main deck, NPC lounge, merchants (bazaar), player lounge.
    private static readonly string[] StationSigns=["sn_hangar","sn_maindeck","sn_npclounge","sn_merchants","sn_lounge"];
    /// <summary>
    /// The sign over a door to <paramref name="destination"/> (a wire room type). 006987A0's jump table 00698E24 gives
    /// the room class (wire 2 and 3 swap: the bazaar is class 3, the NPC lounge class 2) and sets the room record's
    /// byte +0B from wire flag 0x80. With that byte clear the class table 00B8A9A0 names the sign; with it set the
    /// station family's table does (00B8A9B8/D0/E8/AA00), which differ only for a Jenquai bazaar (sn_const) and the
    /// fourth family's NPC lounge (sn_casino).
    /// </summary>
    internal static string? StationDoorSign(EnbNext.Presentation.EnbSceneStationDefinition definition,uint destination)
    {
        if(definition.Rooms.FirstOrDefault(r=>r.RoomType==destination) is not {} record)return null;
        uint roomClass=destination switch {2=>3,3=>2,_=>destination};
        if(roomClass>=StationSigns.Length)return null;
        if((record.Flags&0x80)!=0)
        {
            if(definition.DefinitionValue==1 && roomClass==3)return "sn_const";
            if(definition.DefinitionValue>2 && roomClass==2)return "sn_casino";
        }
        return StationSigns[roomClass];
    }
    /// <summary>Retail attaches a sign only when its model loads (a null result is skipped); so does Next.</summary>
    private bool TryStationFixtureAsset(string name,out EnbW3dRenderResource? resource)
    {
        try{resource=StationFixtureAsset(name);return true;}
        catch(InvalidDataException){resource=null;return false;}
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string,EnbW3dRenderResource> _stationFixtureAssets=new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string,List<(LiveLanePlan Lane,LiveGpuResource Gpu)>> _stationFixtures=[];
    private readonly Dictionary<EnbW3dRenderResource,Dictionary<string,List<(LiveLanePlan Lane,LiveGpuResource Gpu)>>> _stationResidentFixtures=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string,EnbRetailStationDoorAnimation> _stationDoorFrames=[];
    private void DestroyStationFixtures(EnbGlApi? gl,bool preserveScene=false)
    {
        foreach(var list in _stationFixtures.Values)ReleaseStationDraws(gl,list,preserveScene);
        if(!preserveScene)_stationFixtures.Clear();
        if(!preserveScene){_stationFixtureAssets.Clear();_stationDoorFrames.Clear();_stationPickMeshRays.Clear();ResetStationBooths();
            _stationShadowAttempted=false;_stationShadow=null;_stationShadowDiagnostic=null;}
    }
    private EnbW3dRenderResource StationFixtureAsset(string name)
        => LoadStationFixtureAsset(name,_retailAssetsAccessor()!);
    private EnbW3dRenderResource LoadStationFixtureAsset(string name,EnbRetailAssetBundle assets)
        =>_stationFixtureAssets.TryGetValue(name,out var cached)?cached:_stationFixtureAssets[name]=CompileStationFixture(name,assets);
    private static EnbW3dRenderResource CompileStationFixture(string name,EnbRetailAssetBundle assets)
    {
        var resources=assets.RetailMixResources!;
        if(!resources.TryReadResource(name+".w3d",out var bytes,out var location)||!EnbW3dModel.TryDecode(bytes,out var model))
            throw new InvalidDataException("Missing station fixture "+name);
        var textures=model.Meshes.SelectMany(m=>m.TextureNames).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n=>new EnbResolvedTextureReference(n,resources.TryLocateResource(n,out var t)?t:null)).ToArray();
        if(!EnbW3dRenderResourceCompiler.TryCompile(new(location.Name,name,location,model,textures),out var resource,out var error))throw new InvalidDataException(error);
        return EnbW3dRenderResourceCache.BindSpecialMaterialFile(assets,new(location.Name,name,location,model,textures),resource);
    }
    /// <summary>
    /// A fixture's packets static-sort queue order, as the room's: deferred buckets follow the immediate
    /// meshes. A connected door's alpha-blended LOGO_L emblem quads (bucket 20, no depth write) and GLARE quads (10)
    /// so draw over the door panels they lie on, instead of being painted over by them in file order.
    /// </summary>
    private static System.Collections.Immutable.ImmutableArray<EnbW3dDrawTask> StationStaticSortOrder(EnbW3dDrawTask[] packets,EnbW3dFrameSubmissionWorkspace? workspace)
        => EnbW3dFrameSubmissionOrderCompiler.TryCompile(EnbRenderPassKind.OrdinaryWorld,packets,out var order,out var error,workspace)
            ? order.Packets : throw new InvalidDataException(error);
    private readonly EnbW3dFrameSubmissionWorkspace _stationSortWorkspace=new();
    private void RenderStationFixtures(EnbGlApi gl,int fb,int width,int height,EnbCurrentRetailStationInterior room,long elapsed)
    {
        if(_stationScene?.StationDefinition is not {} definition)return;
        var camera=StationCamera(room,width,height); var assets=_retailAssetsAccessor()!;
        HashSet<string> seen=[];
        var pickPackets=new Dictionary<string,EnbRetailMeshPick[]>();
        EnbRetailMeshPick[] Picks(string key)=>pickPackets.TryGetValue(key,out var packets)?packets:[];
        void Draw(string key,string name,Matrix4x4 world,EnbW3dRenderResource? resource=null,EnbW3dAnimationSample? sample=null,bool pickable=false)
        {
            seen.Add(key);
            resource ??=StationFixtureAsset(name);
            var retained=EnbCurrentRetailStationInterior.BuildModelDraws(resource,world,elapsed,sample,retainRenderHidden:true).Packets;
            EnbW3dWorldBounds? parentSphere=null;
            if(pickable && resource.HierarchicalLods.SingleOrDefault() is {} hlod)
            {
                var animation=sample is {} pose?resource.Animations.Single(a=>a.Name.Equals(pose.Name,StringComparison.OrdinalIgnoreCase)):null;
                if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(resource,hlod,world,animation,sample?.Frame??0,out var bounds,out var why))
                    throw new InvalidDataException(why);
                parentSphere=bounds.WorldSphere;
            }
            //BuildModelDraws requires a single level. Its visible-pivot members
            //are exactly the98E680 child set, including normal-hidden meshes.
            if(pickable)pickPackets[key]=retained.Where(p=>p.SpecialMaterial is null).Select(p=>
                new EnbRetailMeshPick(StationPickMesh(p.Mesh),p.WorldTransform,camera){ParentSphere=parentSphere}).ToArray();
            var framePackets=retained.Where(p=>(p.Mesh.Attributes&0x1000)==0).ToArray();
            var packets=StationStaticSortOrder(framePackets.Where(p=>p.SpecialMaterial is null).ToArray(),_stationSortWorkspace);
            if(!_stationFixtures.TryGetValue(key,out var draws))_stationFixtures[key]=draws=[];
            // Machinery can animate visibility. Rebuild only when the submitted
            // mesh sequence changes, rather than reusing another mesh's buffers.
            if(draws.Count!=packets.Length || draws.Where((d,i)=>!ReferenceEquals(d.Lane.Textured!.Submission.Packet.Mesh,packets[i].Mesh)).Any())
            {foreach(var d in draws)d.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);draws.Clear();}
            for(int i=0;i<packets.Length;i++)
            {
                if(i==draws.Count)
                {
                    if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packets[i],out var submission,out var error))throw new InvalidDataException(error);
                    if(!TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,
                        assets.Textures,new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out var lane,out error)||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out var gpu,out error,StationRecoveryState(draws,i,lane)))throw new InvalidDataException("Station fixture: "+error);
                    ConsumeStationRecoveryState(draws,i);
                    draws.Add((lane,gpu));
                }
                else
                {
                    var old=draws[i];
                    var submission=old.Lane.Textured!.Submission with {Packet=packets[i]};
                    if(!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(old.Lane.Textured!,submission,camera,out var lane,out var error))throw new InvalidDataException(error);
                    draws[i]=(LiveLanePlan.From(lane),old.Gpu);
                }

            }
            SubmitSpecialScene(gl,fb,width,height,draws,framePackets.Where(p=>p.SpecialMaterial is not null).Select(p=>(p).ForkTask().SetRenderInstancePath($"station-fixture/{key}/{p.RenderInstancePath}")).ToArray(),
                camera,EnbRetailStationLighting.UnregisteredEnvironment,new Vector3(.5f),unchecked((uint)elapsed));
        }
        var doorPicks=new List<DoorPick>();
        uint doorClock=unchecked((uint)ReadMapperClock());
        foreach(var door in room.Doorways)
        {
            EnbW3dAnimationSample? sample=null;
            if(door.Destination is not null)
            {
                var animation=door.Animation;
                bool near=_stationMovement is {} movement && door.IsNear(movement.Position);
                // 63C590 queries retained room actors after 63AAF0 advances them.
                // A received waypoint is not the actor's current position.
                near |= _stationPeople.Any(p=>!p.Key.Npc && door.IsNear(p.Value.World.Translation));
                if(!_stationDoorFrames.TryGetValue(door.Pivot,out var state))
                    _stationDoorFrames[door.Pivot]=state=new(checked((int)(animation?.NumFrames??0)),animation?.FrameRate??0,doorClock);
                if(state.Update(near,doorClock) is {} opening && EnbRetailStationDoorAnimation.Sound(definition.DefinitionValue,opening) is {} cue)
                    QueueInteractionSound(cue);
                if(animation is not null)sample=new(animation.Name,state.Frame);
            }
            Draw(door.Pivot,"",door.Transform,door.Resource,sample,pickable:door.Destination is not null);
            // Current retail 00639630: only connected, hovered doors within
            // avatar radius 3 + door radius 10 + surface distance 3 show text.
            if(door.Destination is uint destination)
                doorPicks.Add(new(destination,Picks(door.Pivot),
                    _stationMovement is {} player && door.IsNear(player.Position),door.Transform.Translation));
        }
        Volatile.Write(ref _stationDoorPicks,doorPicks.AsEnumerable().Reverse().ToArray());
        UpdateStationBooths(room);
        var boothPicks=new List<StationBoothPick>();
        foreach(var fixture in StationFixturePlacements(room))
        {
            if(fixture.Key.EndsWith("/sign",StringComparison.Ordinal))
            {
                if(TryStationFixtureAsset(fixture.Name,out var sign))Draw(fixture.Key,fixture.Name,fixture.World,sign);
                continue;
            }
            var npc=definition.RoomNpcs(room.RoomType).FirstOrDefault(n=>fixture.Key==$"AVATAR{n.Slot:00}/booth/7");
            if(npc is not null && _stationBooths.TryGetValue((room.RoomType,npc.Slot),out var booth))
                Draw(fixture.Key,fixture.Name,fixture.World,booth.Resource,booth.Sample(doorClock),pickable:true);
            else Draw(fixture.Key,fixture.Name,fixture.World,pickable:npc is not null || fixture.Key.EndsWith("/monitor",StringComparison.Ordinal));
            if(npc is not null)
                boothPicks.Add(new(npc,Picks(fixture.Key),
                    _stationMovement is {} boothPlayer && Vector3.Distance(boothPlayer.Position,fixture.World.Translation)<12,fixture.World.Translation));
        }
        Volatile.Write(ref _stationBoothPicks,boothPicks.OrderByDescending(p=>p.Npc.Slot).ToArray());
        foreach(var machine in room.Machinery)Draw(machine.Name,machine.Name,Matrix4x4.Identity,machine.Resource,machine.Sample(ReadMapperClock()));
        PrepareStationShadow();
        if(_stationShadow is not null)
            foreach(var shadow in StationAvatarShadows())Draw(shadow.Key,"avatar_shadow",shadow.World,_stationShadow);
        foreach(var person in _stationPeople.Where(p=>p.Key.Npc && p.Value.Ambient?.Prop is not null))
        {
            var actor=person.Value.Ambient!;
            Draw($"ambient/{person.Key.Id}","",person.Value.World,actor.Prop,actor.Sample(ReadMapperClock()));
        }
        Volatile.Write(ref _stationTerminalPicks,definition.Facilities.Where(f=>f.RoomType==room.RoomType && f.Type<=6).OrderByDescending(f=>f.Slot)
            .Select(f=>new TerminalPick(f,Picks($"TERMINAL{f.Slot:00}/monitor"),
                _stationMovement is {} terminalPlayer && StationFixturePlacements(room).Any(p=>p.Key==$"TERMINAL{f.Slot:00}/monitor" &&
                    Vector3.Distance(terminalPlayer.Position,p.World.Translation)<11),
                StationFixturePlacements(room).FirstOrDefault(p=>p.Key==$"TERMINAL{f.Slot:00}/monitor").World.Translation)).ToArray());
        PublishStationRoster(room,definition);
        foreach(var key in _stationFixtures.Keys.Where(k=>!seen.Contains(k)).ToArray())
        {foreach(var d in _stationFixtures[key])d.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);_stationFixtures.Remove(key);}
    }
}
