using System.Collections.Immutable;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;
using System.Numerics;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    internal uint? FrontendSoundSettingsOwner { get; set; }
    internal EnbResolvedRenderCamera? CurrentAudioListener => _audio?.EffectListener;
    internal Func<EnbResolvedRenderCamera?>? ClientAudioListenerAccessor { get; set; }

    private void UpdateClientAnimationTails()
    {
        if(_audio is not {} audio || _retailAssetsAccessor() is not {} assets)return;
        // Frontend entry transfers this owner to the HUD. Released animation
        // voices keep their positions and finish there; the main world/station
        // listener replaces the retained frontend listener when it is ready.
        if((ClientAudioListenerAccessor?.Invoke()??audio.EffectListener) is {} listener)
        {
            var cues=new List<EnbCurrentRetailEffectSoundNode>();
            while(_frontendAnimationSounds.TryDequeue(out var cue))cues.Add(cue);
            audio.UpdateEffects(cues,assets,listener,_=>null,
                assets.ResolvePlayerOptions(_fixedSessionIdAccessor()).Options.SoundSfx/100f,
                sampleTime:unchecked((uint)Environment.TickCount64),dopplerEnabled:false);
        }
    }
    private sealed class FrontendGpuEntry(LiveGpuResource gpu, EnbW3dRenderMesh mesh)
    {
        internal LiveGpuResource? Gpu = gpu;
        internal readonly EnbW3dRenderMesh Mesh = mesh;
        internal EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState? Recovery;
        internal EnbW3dProgrammableTextureLanePlan? RecoveryLane;
        internal EnbNext.Rendering.OpenGL.EnbOpenGlTextureMeshRecoveryState? Recover(EnbW3dProgrammableTextureLanePlan lane)
            => RecoveryLane is {} previous &&
                ReferenceEquals(previous.Submission.Packet.CurrentRetailSharedMaterialMappers,lane.Submission.Packet.CurrentRetailSharedMaterialMappers) &&
                !FrontendTextureStorageChanged(previous,lane) && !FrontendMapper1Changed(previous,lane) ? Recovery : null;
    }
    private readonly Dictionary<(string, int), FrontendGpuEntry> _frontendGpu = [];
    private readonly Dictionary<(string, int), (EnbW3dRenderMaterialPass Source,
        EnbW3dDrawMaterialBinding Material,EnbCurrentRetailEnvironmentAtlasSet? Atlas)> _frontendClassShipMaterials=[];
    private long _frontendRenderCount;
    internal long FrontendRenderCount => Interlocked.Read(ref _frontendRenderCount);
    private long _frontendLastRenderMilliseconds;
    internal long FrontendLastRenderMilliseconds => Interlocked.Read(ref _frontendLastRenderMilliseconds);
    private long _frontendLastRenderCompletedTick;
    internal long FrontendLastRenderCompletedTick => Interlocked.Read(ref _frontendLastRenderCompletedTick);
    private string _frontendLastRenderedScreen="";
    internal string FrontendLastRenderedScreen => Volatile.Read(ref _frontendLastRenderedScreen);
    private string _frontendLastCreationCostSummary="";
    internal string FrontendLastCreationCostSummary => Volatile.Read(ref _frontendLastCreationCostSummary);
    private string _frontendLastShipCostBreakdown="";
    internal string FrontendLastShipCostBreakdown => Volatile.Read(ref _frontendLastShipCostBreakdown);
    private int _frontendShipDecalAppliedPackets,_frontendShipNameAppliedPackets;
    internal int FrontendShipDecalAppliedPackets => Volatile.Read(ref _frontendShipDecalAppliedPackets);
    internal int FrontendShipNameAppliedPackets => Volatile.Read(ref _frontendShipNameAppliedPackets);
    internal sealed record FrontendBeamInstance(EnbStationBeamEffect Effect,long Elapsed,
        Matrix4x4 World,EnbResolvedRenderCamera Camera,int Actor)
    {public int Scene {get;init;}=1;}
    private readonly Dictionary<int,EnbRetailTeleportScene> _frontendTeleportScenes=[];
    private readonly Dictionary<EnbStationBeamEffect,FrontendBeamInstance> _frontendTeleportOwners=[];
    private uint? _frontendTeleportClock;
    internal sealed record FrontendDazzleInstance(EnbDazzleDefinition Definition,
        Matrix4x4 World,EnbResolvedRenderCamera Camera)
    {
        public string Name { get; init; } = "";
    }
    internal sealed record FrontendCollisionMesh(EnbW3dRenderMesh Mesh,Matrix4x4 World);
    public void SetFrontendDraws(ImmutableArray<EnbFrontendDraw> draws,string screen,
        ImmutableArray<FrontendBeamInstance> beams=default,
        ImmutableArray<FrontendDazzleInstance> dazzles=default,
        ImmutableArray<FrontendCollisionMesh> collision=default,
        EnbResolvedRenderCamera? soundListener=null)
    {
        if(screen is not "characters")Volatile.Write(ref _frontendPrewarmRequest,null);
        Volatile.Write(ref _frontendFrame, new(draws,screen,beams,dazzles) { Collision=collision, SoundListener=soundListener });
        RequestFrame();
    }
    private sealed record FrontendFrame(ImmutableArray<EnbFrontendDraw> Draws,string Screen,
        ImmutableArray<FrontendBeamInstance> Beams,ImmutableArray<FrontendDazzleInstance> Dazzles)
    {
        public ImmutableArray<FrontendCollisionMesh> Collision { get; init; }
        public EnbResolvedRenderCamera? SoundListener { get; init; }
    }
    private FrontendFrame? _frontendFrame;
    private readonly Dictionary<(EnbStationBeamEffect Owner,int Actor,string Mesh,int Pass),(LiveLanePlan Lane,LiveGpuResource Gpu)> _frontendBeamGpu=[];
    internal sealed record FrontendSpeech(long Identity, string Resource,string Screen);
    internal sealed record FrontendSpeechReceipt(long Identity, bool Complete, double Seconds, string Status);
    private FrontendSpeech? _frontendSpeechRequest;
    private FrontendSpeechReceipt? _frontendSpeechReceipt;
    public void SetFrontendSpeech(FrontendSpeech? speech) => Volatile.Write(ref _frontendSpeechRequest, speech);
    public FrontendSpeechReceipt? SpeechReceipt => Volatile.Read(ref _frontendSpeechReceipt);
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _frontendSounds = new();
    public void QueueFrontendSound(string name) => _frontendSounds.Enqueue(name);
    private static long _frontendAnimationSoundSequence;
    private readonly System.Collections.Concurrent.ConcurrentQueue<EnbCurrentRetailEffectSoundNode> _frontendAnimationSounds=new();
    internal void QueueClientAnimationSound(string name,bool flat,Vector3 position)
    {
        uint tick=unchecked((uint)Environment.TickCount64);
        // This is an internal callback identity, not an emitted game object.
        // Reserve a generation outside the session's positive render identities
        // so transferred frontend tails cannot collide with world actor cues.
        var root=new EnbSceneEffectSnapshot(0,ulong.MaxValue,0,0,0,tick,0,"",0,
            false,1,Vector3.Zero,0,0,false,Vector3.Zero,1,EnbSceneEffectGeometryKind.Point,position,0,Vector3.Zero,0,0,null);
        var definition=new EnbEffectDefinition(0,"SoundEffect",EnbEffectClassKind.SoundEffect,
            null,null,null,null,false,null,null,null,null,false,false,false,false,
            false,false,false,false,false,false,name,
            new Dictionary<string,string>{{"SFX2D",flat?"TRUE":"FALSE"}});
        _frontendAnimationSounds.Enqueue(new(unchecked((ulong)Interlocked.Increment(ref _frontendAnimationSoundSequence)),
            definition,root,null,EnbCurrentRetailEffectLinkKind.Root,true,tick)
            {ClientAnimation=true,ReleasedWorldPosition=position});
    }
    private TaskCompletionSource<LiveWorldOpenAlAudio?>? _frontendEntryAudio;
    private bool _frontendEntryCueQueued;
    private void CancelFrontendEntryAudio()
    {
        Interlocked.Exchange(ref _frontendEntryAudio, null)?.TrySetResult(null);
        _frontendEntryCueQueued = false;
    }
    internal Task<LiveWorldOpenAlAudio?> TakeFrontendEntryAudioAsync()
    {
        var request = new TaskCompletionSource<LiveWorldOpenAlAudio?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _frontendEntryAudio, request, null) is {} pending)
            return pending.Task;
        RequestFrame();
        return request.Task;
    }
    // Called before this new surface is attached; the old render owner has released it.
    internal void AdoptClientAudio(LiveWorldOpenAlAudio audio)
    {
        if (_audio is not null) throw new InvalidOperationException("Audio owner already initialized.");
        _speechScope = audio.Speech;
        _audio = audio;
    }
    private void CompleteFrontendEntryAudio()
    {
        if (Volatile.Read(ref _frontendEntryAudio) is not {} request ||
            _audio?.InteractionSoundDecoding == true) return;
        // 0055D060 case 7 plays before entering the next screen. Transfer the
        // live device/voice, so detaching this surface cannot cut off the click.
        var audio = _audio;
        audio?.LeaveFrontend();
        _audio = null;
        EnableClientAudio = false;
        _frontendEntryCueQueued = false;
        Interlocked.Exchange(ref _frontendEntryAudio, null);
        request.TrySetResult(audio);
    }
    private int _creatorArrivalSoundPending;
    public void QueueCreatorArrivalSound() => Interlocked.Exchange(ref _creatorArrivalSoundPending,1);
    private void RenderFrontend(EnbGlApi gl, int fb, int width, int height, FrontendFrame frame)
    {
        _frameDazzles.Clear();
        _dazzleCollisionRoots.Clear();
        PrepareFrontendDazzleCollision(frame.Collision);
        var renderTimer=System.Diagnostics.Stopwatch.StartNew();
        Interlocked.Increment(ref _frontendRenderCount);
        if (_retailAssetsAccessor() is not {} assets) return;
        EnsureSwatchCatalog(assets);
        var options = assets.ResolvePlayerOptions(FrontendSoundSettingsOwner ?? _fixedSessionIdAccessor()).Options;
        if (_audio is not null) _audio.DialogGain = options.SoundDialog / 100f;
        if(frame.Screen=="creation" && Interlocked.Exchange(ref _creatorArrivalSoundPending,0)==1)
            _frontendSounds.Enqueue("Login_Hand_Glow");
        if (!_frontendEntryCueQueued && _audio is {} soundOwner)
            while (_frontendSounds.TryDequeue(out var cue)) soundOwner.QueueInteractionSound(cue);
        _audio?.PrepareStationSound("Login_Mouse_Click", assets);
        if (Volatile.Read(ref _frontendEntryAudio) is not null && !_frontendEntryCueQueued)
        {
            _audio?.QueueInteractionSound("Login_Mouse_Click");
            _frontendEntryCueQueued = true;
        }
        _audio?.UpdateInteractionSounds(assets, options.SoundSfx / 100f);
        if(frame.SoundListener is {} listener)
        {
            var cues=new List<EnbCurrentRetailEffectSoundNode>();
            while(_frontendAnimationSounds.TryDequeue(out var cue))cues.Add(cue);
            _audio?.UpdateEffects(cues,assets,listener,_=>null,options.SoundSfx/100f,
                sampleTime:unchecked((uint)Environment.TickCount64),dopplerEnabled:false);
        }
        _audio?.UpdateFrontendMusic(frame.Draws.Any(d => !d.Key.StartsWith("splash/", StringComparison.Ordinal)), assets, options.SoundMusic / 100f);
        CompleteFrontendEntryAudio();
        var active = new HashSet<(string, int)>();
        bool profileCreation=frame.Screen=="creation" && FrontendLastRenderedScreen!="creation";
        Dictionary<string,(int Count,long Ticks)>? creationCosts=profileCreation?[]:null;
        long shipSetup=0,shipLane=0,shipGpu=0,shipSubmit=0;
        int shipDecalApplied=0,shipNameApplied=0;
        var frontendNameTextures=new Dictionary<string,EnbTextureImage>(StringComparer.OrdinalIgnoreCase);
        var nameTextureCache=new Dictionary<string,EnbCurrentRetailShipNameDecalTexture>(StringComparer.Ordinal);
        EnbResolvedRenderCamera? previousCamera = null;
        bool roomDazzlesRendered=false;
        foreach (var draw in frame.Draws.SelectMany(d => d.Highlight && d.Avatar is null && !d.Disabled ? new[] { d with { Highlight = false }, d } : new[] { d }))
        {
        var prepared = new List<(EnbW3dDrawTask Packet, EnbCurrentRetailEnvironmentAtlasSet? Atlas, long Start)>();
        bool classShip=draw.Key.StartsWith("creation/class/ship/",StringComparison.Ordinal);
        foreach (var pass in draw.Mesh.MaterialPasses)
        {
            if(!roomDazzlesRendered && draw.Key.StartsWith("ui/",StringComparison.Ordinal))
            {RenderRoomDazzles();roomDazzlesRendered=true;}
            long drawStart=profileCreation ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (!Equals(previousCamera,draw.Camera)) { gl.DepthMask(1);gl.Clear(EnbGlConstants.GL_DEPTH_BUFFER_BIT); previousCamera=draw.Camera; }
            if (!Matrix4x4.Invert(draw.Transform, out var inverse)) throw new InvalidDataException("Singular frontend transform");
            var normalTransform = Matrix4x4.Transpose(inverse);
            var geometry = draw.Geometry is {} deformed ? new EnbW3dDeformedGeometry(
                deformed.Positions.Select(v => Vector3.Transform(v, draw.Transform)).ToImmutableArray(),
                deformed.Normals.Select(v => Vector3.TransformNormal(v, normalTransform)).ToImmutableArray()) : null;
            var packet = new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary, EnbW3dDrawBlendClass.Blended,
                draw.Mesh.StaticSort, draw.Resource.Key, draw.Mesh.Ordinal, pass.Ordinal, 1, 0), 1, 1, 0,
                geometry is null ? draw.Transform : Matrix4x4.Identity, new(draw.Transform.Translation, draw.Mesh.BoundingRadius), draw.Mesh, geometry,
                new(pass, pass.SingleBindingSemantics), EnbW3dLightEnvironment.BlackNoLights, draw.Time, null, 0, draw.Mesh.Name);
            if(draw.TextureOverride is {} texture)
            {
                EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding binding)=>binding with
                    {LogicalName=texture,Location=null,ResolveLogicalNameAtRuntime=true};
                var colored=(pass).Copy().SetTextureStages(pass.TextureStages.Select((stage,index)=>index==0?(stage).Copy().SetTextures(stage.Textures.Select(Replace).ToArray()):stage).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(binding=>binding with
                        {Textures=binding.Textures.Select((item,index)=>index==0 && item is not null?Replace(item):item).ToArray()}).ToArray());
                packet=(packet).ForkTask().SetMaterial(new(colored,colored.SingleBindingSemantics));
            }
            EnbCurrentRetailEnvironmentAtlasSet? atlas = null;
            // 0067B7A0 profile 1, raw stack loads at 0067B975 and 0067B9F9.
            // The decompiler loses callee stack cleanup and incorrectly reports a black fill.
            // 0067AB20 attaches selection modes 1 and 2 to CharacterView
            // scene +0x6C. 00678B70 explicitly sets that scene's ambient to
            // black; the white ambient belongs to the separate +0x1C scene.
            var roomLighting = EnbW3dLightEnvironment.CreateCurrentRetail(0,[new(true,new(EnbW3dLightType.Directional,
                Vector3.One,Vector3.Zero,Vector3.Zero,Vector3.Zero,new(-1,-.4f,0),1000000,1,1,0,0,0,0)),
                new(true,new(EnbW3dLightType.Directional,new(.8f,.3f,.3f),Vector3.Zero,Vector3.Zero,Vector3.Zero,new(1,.4f,0),1000000,1,1,0,0,0,0))]);
            if(draw.Key.StartsWith("narrator/",StringComparison.Ordinal))
            {
                // The login narrator has no avatar appearance snapshot. It still
                // belongs to scene 4, whose 0067BBE0 light pair is independent
                // of the avatar customization path (005ACB80/00678B70).
                packet=(packet).ForkTask().SetLightEnvironment(EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                    new(true,new(EnbW3dLightType.Directional,Vector3.One,Vector3.Zero,new(.03f),Vector3.Zero,
                        new(1,0,-.75f),1000000,1,1,0,0,0,0)),
                    new(true,new(EnbW3dLightType.Directional,new(.8f,.3f,.3f),Vector3.Zero,Vector3.Zero,Vector3.Zero,
                        new(-1,0,.75f),1000000,1,1,0,0,0,0))]));
                // 005ACB80 builds this login scene's BACK-100000 reflection
                // textures even though no customization snapshot is attached.
                if(assets.EnvironmentMaps?.TryResolveReflection(100000,out var narratorReflection)==true)
                    atlas=ResolveEnvironmentAtlasSet(assets,assets.EnvironmentMaps,narratorReflection,0,out _);
            }
            if(draw.Key.StartsWith("room/",StringComparison.Ordinal))
                packet=(packet).ForkTask().SetLightEnvironment(roomLighting);
            if(draw.Key.StartsWith("creation/planet/",StringComparison.Ordinal) ||
                draw.Key.StartsWith("creation/class/ship/",StringComparison.Ordinal))
                // 00574710 installs profile 0 on FE_CLASS01. The raw stores
                // at 0067B7BE..0067B84B and loads at 0067B94E/0067B975
                // give a white key at (-.66,-1,1) and a black reverse light.
                // 0067B853 writes intensity 3.0 for profile 0, loaded into
                // light +0x98 at 0067B8F1; 009261C0 multiplies RGB by it.
                // 0067AB20 attaches FE_CLASS01 and its profession ships to
                // CharacterView scene +0x6C.
                // 00678B70 sets that scene's ambient to black. Its white
                // ambient assignment belongs to the separate scene +0x1C.
                packet=(packet).ForkTask().SetLightEnvironment(EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                    new(true,new(EnbW3dLightType.Directional,new(3),Vector3.Zero,Vector3.Zero,Vector3.Zero,
                        new(-.66f,-1f,1f),1000000,1,1,0,0,0,0)),
                    new(true,new(EnbW3dLightType.Directional,Vector3.Zero,Vector3.Zero,Vector3.Zero,Vector3.Zero,
                        new(.66f,1f,-1f),1000000,1,1,0,0,0,0))]));
            if(draw.Key.StartsWith("creation/ship/",StringComparison.Ordinal))
            {
                // The existing customization ShipPIP uses this camera-relative
                // key/fill pair for its transparent preview (Inventory.cs).
                Vector3 Direction(Vector3 p)=>draw.Camera.Basis.Right*p.X+
                    draw.Camera.Basis.Up*p.Y-draw.Camera.Basis.Forward*p.Z;
                packet=(packet).ForkTask().SetLightEnvironment(EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                    new(true,new(EnbW3dLightType.Directional,new(3),Vector3.Zero,Vector3.Zero,Vector3.Zero,
                        Direction(new(1,-1,-1)),1000000,1,1,0,0,0,0)),
                    new(true,new(EnbW3dLightType.Directional,new Vector3(.06f,.42f,.83f)*1.3f,
                        Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(-1,1,1)),1000000,1,1,0,0,0,0))]));
            }

            int shipReflectionId=classShip
                ? 100000 : 0x186a5; // 00574710 class scene; 00622180 ship creation.
            if(classShip && _frontendClassShipMaterials.TryGetValue((draw.Key,pass.Ordinal),out var classMaterial) &&
                ReferenceEquals(classMaterial.Source,pass))
            {
                // The nine profession ships use fixed retail default colors and
                // decals. Their transforms move, but their material does not.
                packet=(packet).ForkTask().SetMaterial(classMaterial.Material);
                atlas=classMaterial.Atlas;
            }
            else
            {
                if(draw.ShipColors is {} shipColors &&
                    assets.EnvironmentMaps?.TryResolveReflection(shipReflectionId,out var shipReflection)==true)
                {
                    atlas=ResolveEnvironmentAtlasSet(assets,assets.EnvironmentMaps,shipReflection,0,out _);
                    packet=EnbCurrentRetailShipColorizationRuntime.Apply(packet,shipColors,draw.ShipColorRegion,shipReflection.Axis,out _);
                }
                if(draw.ShipDecals is {} shipDecals)
                {
                    packet=EnbCurrentRetailShipDecalRuntime.Apply(packet,shipDecals,assets.Decals,out var decalReceipt,_swatchCatalog);
                    if(draw.Key.StartsWith("creation/ship/",StringComparison.Ordinal) &&
                        decalReceipt.Outcome is EnbCurrentRetailShipDecalOutcome.AppliedTexture or
                            EnbCurrentRetailShipDecalOutcome.AppliedTextureAndTint)
                        shipDecalApplied++;
                    if(decalReceipt.Outcome is EnbCurrentRetailShipDecalOutcome.Hidden or EnbCurrentRetailShipDecalOutcome.OmittedDescriptorPass)
                        continue;
                }
                if(draw.ShipNameDecal is {} shipName &&
                    EnbCurrentRetailShipNameDecalRuntime.IsTarget(packet.RenderObjectName))
                {
                    if(!nameTextureCache.TryGetValue(shipName.Name,out var generated))
                    {
                        var font=assets.Textures.Resolve(EnbCurrentRetailShipNameDecalRuntime.RetailFontTextureName).Image;
                        if(font is null || !EnbCurrentRetailShipNameDecalRuntime.TryBuildTexture(
                            shipName,font,"__creation_ship_name_"+
                                Convert.ToHexString(shipName.RawName.AsSpan())+".argb4444",out generated,out _))
                            continue; // A missing optional name texture cannot stall the creator.
                        nameTextureCache.Add(shipName.Name,generated);
                    }
                    frontendNameTextures[generated.LogicalName]=generated.Image;
                    packet=EnbCurrentRetailShipNameDecalRuntime.Apply(packet,shipName,generated,out var nameReceipt);
                    if(nameReceipt.Applied)shipNameApplied++;
                }
                if(classShip)_frontendClassShipMaterials[(draw.Key,pass.Ordinal)]=(pass,packet.Material,atlas);
            }
            if (draw.Key.StartsWith("background/", StringComparison.Ordinal))
            {
                // 0067C3C0 initializes the three authored race palette entries;
                // 0067C530/0092EF70 assign the selected RGB to material +30
                // (emissive). Keep the exact binary values, not display guesses.
                Vector3 backgroundTint=draw.Key.StartsWith("background/creation/1/",StringComparison.Ordinal)
                    ?new(.95f,.51f,.13f):draw.Key.StartsWith("background/creation/2/",StringComparison.Ordinal)
                    ?new(0f,.52f,.45f):new(.8f,.1f,.05f);
                EnbW3dCompiledMaterialSemantics? Tint(EnbW3dCompiledMaterialSemantics? s) =>
                    s is {} value ? value with { Uniforms = value.Uniforms with { Emissive = backgroundTint } } : null;
                var tinted = (pass).Copy().SetSingleBindingSemantics(Tint(pass.SingleBindingSemantics)).SetTriangleBindings(pass.TriangleBindings.Select(b => b.VertexMaterial?.Material.Ordinal == 0 ? b with { Semantics = Tint(b.Semantics) } : b).ToArray());
                packet = (packet).ForkTask().SetMaterial(new(tinted,tinted.SingleBindingSemantics));
            }
            if (draw.Avatar is {} avatar)
            {
                var stageLighting = EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                    new(true,new(EnbW3dLightType.Directional,Vector3.One,Vector3.Zero,new(.03f),Vector3.Zero,new(1,0,-.75f),1000000,1,1,0,0,0,0)),
                    new(true,new(EnbW3dLightType.Directional,new(.8f,.3f,.3f),Vector3.Zero,Vector3.Zero,Vector3.Zero,new(-1,0,.75f),1000000,1,1,0,0,0,0))]);
                var lighting = avatar.AvatarType == 1 ? stageLighting : roomLighting;
                if(draw.Key.StartsWith("creation/class/avatar/",StringComparison.Ordinal))
                {
                    // 0067B390 attaches the pair to mode-4 FE_NAR_AGG in scene
                    // +0x84. 00678B70 sets that scene's ambient to black, and
                    // 0067AB20 mode 4 calls 0067BBE0 to install this light pair.
                    // Profile 0's separate key belongs to class scene +0x6C.
                    lighting=stageLighting;
                }
                else if(draw.Key.StartsWith("creation/avatar/",StringComparison.Ordinal))
                {
                    Vector3 Direction(Vector3 p)=>draw.Camera.Basis.Right*p.X+
                        draw.Camera.Basis.Up*p.Y-draw.Camera.Basis.Forward*p.Z;
                    var ambient=avatar.Race==0?new Vector3(.15f,.1f,.09f):new Vector3(.15f,.1f,.1f);
                    var diffuse=avatar.Race==0?new Vector3(.9f,.9f,.75f):new Vector3(.9f,.8f,.85f);
                    var fill=avatar.Race switch {0=>new Vector3(.5f,0,0),1=>new Vector3(.5f,.4f,.1f),_=>new Vector3(.1f,.45f,.45f)};
                    // Creator ShipPIP mode 1 uses unit-intensity lights (0080BE05,
                    // 0080BF69). 0080C220 explicitly resets the newly constructed
                    // preview scene's ambient to black through scene vtable +0x1C.
                    lighting=EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                        new(true,new(EnbW3dLightType.Directional,diffuse,Vector3.Zero,ambient,Vector3.Zero,Direction(new(.5f,-1,-2)),1000000,1,1,0,0,0,0)),
                        new(true,new(EnbW3dLightType.Directional,fill,Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(-.75f,1,1)),1000000,1,1,0,0,0,0))]);
                }
                packet=(packet).ForkTask().SetLightEnvironment(lighting);
                // 00553AF0 selects the avatar editor's reflection through the
                // race-indexed table at 00B7B128: Terran 100003, Jenquai
                // 100004, Progen 100002. Class selection uses 100000
                // (00574710); the roster uses 100001 (0055C650).
                int avatarReflectionId=draw.Key.StartsWith("creation/avatar/",StringComparison.Ordinal)
                    ? avatar.Race switch { 0 => 100003, 1 => 100004, 2 => 100002, _ => throw new InvalidDataException("Unknown avatar race") }
                    : draw.Key.StartsWith("creation/class/avatar/",StringComparison.Ordinal) || avatar.AvatarType != 0
                    ? 100000 : 100001;
                if(assets.EnvironmentMaps?.TryResolveReflection(avatarReflectionId,out var reflection)==true)
                {
                    atlas=ResolveEnvironmentAtlasSet(assets,assets.EnvironmentMaps,reflection,0,out _);
                    if(avatar.AvatarType == 0)
                    {
                    EnbSceneColorizationSnapshot Entry(uint metal,ImmutableArray<float> color)=>new(metal,color.SelectMany(BitConverter.GetBytes).ToImmutableArray());
                    EnbSceneColorizationSnapshot[] colors=[Entry(avatar.ShirtPrimaryMetal,avatar.ShirtPrimaryColor),Entry(avatar.ShirtSecondaryMetal,avatar.ShirtSecondaryColor),Entry(avatar.PantsPrimaryMetal,avatar.PantsPrimaryColor),Entry(avatar.PantsSecondaryMetal,avatar.PantsSecondaryColor)];
                    packet=EnbCurrentRetailShipColorizationRuntime.Apply(packet,colors,draw.Mesh.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase)?1:0,reflection.Axis,out _);
                    }
                }
                var tinted=PortraitMaterial(packet.Material.Source,draw.Mesh.Name,avatar);
                packet=(packet).ForkTask().SetMaterial(new(tinted,tinted.SingleBindingSemantics));
            }
            if(draw.Avatar is not null || draw.Key.StartsWith("narrator/",StringComparison.Ordinal) ||
                draw.Key.StartsWith("room/",StringComparison.Ordinal))
            {
                // Current retail's generic mesh initializer 009BCC40 runs
                // 009BD3A0 while DAT_00BFAE52 is set. Avatar resource loading
                // (0077E9D0) sets that flag around body/head creation;
                // CharacterView 0067AB20 sets it for selection scene mode 2.
                // This rewrites authored stage-0 MODULATE to MODULATE2X.
                packet=EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(packet,false,out var overbright);
                if(overbright.Failed)
                    throw new InvalidDataException("Frontend overbright: "+overbright.Diagnostic);
            }
            prepared.Add(((packet).ForkTask().SetRenderInstancePath(draw.Key).SetOrderKey(packet.OrderKey with { BindingOrdinal = pass.Ordinal }), atlas, drawStart));
        }
        if (prepared.Count != 0 && draw.ShipDecals is {} swatchEntries && _swatchCatalog is not null &&
            EnbCurrentRetailShipDecalRuntime.TryParseSlot(draw.Mesh.Name, out int swatchSlot))
        {
            int? selected = null;
            foreach (var entry in swatchEntries)
                if (Math.Min(entry.Field0, 15u) == (uint)swatchSlot) selected = unchecked((int)entry.Field1);
            if (selected is < 0 && _swatchCatalog.TryGet(selected.Value, out var descriptor, out _) &&
                descriptor.TryApply(prepared.Select(p => p.Packet).ToArray(), 0, out var expanded, out _))
            {
                var prior = prepared.ToArray();
                prepared.Clear();
                foreach (var effective in expanded)
                {
                    var original = prior.FirstOrDefault(p => p.Packet.Material.Source.Ordinal == effective.Material.Source.Ordinal);
                    if (original.Packet is null) original = prior[0];
                    prepared.Add((effective, original.Atlas, original.Start));
                }
                shipDecalApplied += expanded.Count;
            }
        }
        foreach (var item in prepared)
        {
            var packet = item.Packet;
            var atlas = item.Atlas;
            long drawStart = item.Start;            var key = (draw.Key + (draw.Highlight ? "/hover" : draw.Disabled ? "/disabled" : "") +
                (draw.Pressed ? "/pressed" : ""), packet.Material.Source.Ordinal); active.Add(key);
            if (draw.Disabled || draw.Highlight)
            {
                var styled = draw.Avatar is not null && draw.Highlight
                    ? EnbCurrentRetailVerbPresentation.ApplyAvatarHighlight(packet.Material.Source)
                    : EnbCurrentRetailVerbPresentation.Apply(packet.Material.Source, draw.Highlight, draw.Disabled,
                        draw.HighlightStrength);
                packet = (packet).ForkTask().SetMaterial(new(styled, styled.SingleBindingSemantics));
            }
            if (draw.Pressed)
            {
                var styled = EnbCurrentRetailVerbPresentation.ApplyPressed(packet.Material.Source);
                packet = (packet).ForkTask().SetMaterial(new(styled, styled.SingleBindingSemantics));
            }
            long beforeLane=profileCreation?System.Diagnostics.Stopwatch.GetTimestamp():0;
            string? error;
            EnbW3dIndexedSubmissionPlan submission;
            FrontendGpuEntry? classGpu=null;
            bool reusableClassShip=classShip && _frontendGpu.TryGetValue(key,out classGpu) &&
                classGpu.Gpu is not null && ReferenceEquals(classGpu.Mesh,draw.Mesh) &&
                EnbW3dIndexedSubmissionCompiler.CanReuseGeometry(classGpu.Gpu.Textured!.LanePlan.LightingPlan.Submission,packet);
            if(reusableClassShip)
                submission=classGpu!.Gpu!.Textured!.LanePlan.LightingPlan.Submission with {Packet=packet};
            else if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out submission,out error))
                throw new InvalidDataException("Frontend: "+error);
            LiveLanePlan lane;
            if(reusableClassShip && EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(
                classGpu!.Gpu!.Textured!.LanePlan,submission,draw.Camera,out var classLane,out _))
                lane=LiveLanePlan.From(classLane);
            else if(!TryCompileLane(submission, draw.Camera, EnbRenderTextureRuntimeSettings.RetailDefaults,
                EnbRenderTextureBackendCapabilities.ModernDefault, assets.Textures, frontendNameTextures,
                atlas, LiveLaneKind.Textured, out lane, out error))
                throw new InvalidDataException("Frontend: " + error);
            long beforeGpu=profileCreation?System.Diagnostics.Stopwatch.GetTimestamp():0;
            bool mapper1Changed=packet.CurrentRetailSharedMaterialMappers is null && _frontendGpu.TryGetValue(key,out var previous) &&
                previous.Gpu is not null && ReferenceEquals(previous.Mesh,draw.Mesh) &&
                FrontendMapper1Changed(previous.Gpu.Textured!.LanePlan,lane.Textured!);
            if (!_frontendGpu.TryGetValue(key, out var cached) || cached.Gpu is null || !ReferenceEquals(cached.Mesh, draw.Mesh) ||
                !ReferenceEquals(cached.Gpu!.Textured!.LanePlan.Submission.Packet.CurrentRetailSharedMaterialMappers, packet.CurrentRetailSharedMaterialMappers) ||
                FrontendTextureStorageChanged(cached.Gpu!.Textured!.LanePlan,lane.Textured!))
            {
                cached?.Gpu?.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
                if (!TryCreateGpuResource(gl, lane, LiveLaneKind.Textured, 0, out var gpu, out error,
                    cached is not null && ReferenceEquals(cached.Mesh,draw.Mesh) ? cached.Recover(lane.Textured!) : null)) throw new InvalidDataException(error);
                _frontendGpu[key] = cached = new(gpu, draw.Mesh);
            }
            else if(draw.Geometry is not null && lane.Textured is {} skinned)
                cached.Gpu=LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu!.Textured!,skinned));
            // UpdateGeometry replaces LanePlan, so compare against the prior GPU
            // plan before refreshing geometry or an edited tattoo mapper is lost.
            if(mapper1Changed)
            {
                var states=ImmutableArray.CreateBuilder<EnbW3dMapperRuntimeState>(lane.Textured!.RunStates.Length);
                for(int i=0;i<lane.Textured.RunStates.Length;i++)
                {
                    if(!EnbW3dMapperRuntimeState.TryCreate(lane.Textured.RunStates[i].Mapper1Program,
                        cached.Gpu!.Textured!.Mapper1RuntimeStates[i].InitialMilliseconds,out var state,out error))
                        throw new InvalidDataException("Frontend mapper: "+error);
                    states.Add(state);
                }
                cached.Gpu=LiveGpuResource.From(cached.Gpu!.Textured! with
                    {Mapper1RuntimeStates=states.MoveToImmutable(),LanePlan=lane.Textured});
            }
            long beforeSubmit=profileCreation?System.Diagnostics.Stopwatch.GetTimestamp():0;
            SubmitGpuResource(gl, fb, width, height, cached.Gpu!, lane, new(false, 0, 0, 0), default, null, packet.CurrentRetailSharedMaterialMappers is null ? draw.Time : ReadMapperClock(), compositeOpaqueCoverage: true);
            if(profileCreation && draw.Key.StartsWith("creation/class/ship/",StringComparison.Ordinal))
            {
                long now=System.Diagnostics.Stopwatch.GetTimestamp();
                shipSetup+=beforeLane-drawStart;shipLane+=beforeGpu-beforeLane;
                shipGpu+=beforeSubmit-beforeGpu;shipSubmit+=now-beforeSubmit;
            }
            if(creationCosts is not null)
            {
                string group=draw.Key.StartsWith("creation/class/ship/",StringComparison.Ordinal)?"class ships":
                    draw.Key.StartsWith("creation/class/avatar/",StringComparison.Ordinal)?"avatars":
                    draw.Key.StartsWith("creation/planet/",StringComparison.Ordinal)?"planet":
                    draw.Key.StartsWith("creation/ui/",StringComparison.Ordinal)?"creator UI":
                    draw.Key.StartsWith("background/",StringComparison.Ordinal)?"background":"other";
                var old=creationCosts.GetValueOrDefault(group);
                creationCosts[group]=(old.Count+1,old.Ticks+System.Diagnostics.Stopwatch.GetTimestamp()-drawStart);
            }
        }
        }
        if(!roomDazzlesRendered)RenderRoomDazzles();
        Volatile.Write(ref _frontendShipDecalAppliedPackets,shipDecalApplied);
        Volatile.Write(ref _frontendShipNameAppliedPackets,shipNameApplied);
        long beamStart=profileCreation?System.Diagnostics.Stopwatch.GetTimestamp():0;
        RenderFrontendBeams(gl,fb,width,height,frame.Beams,assets);
        void RenderRoomDazzles()
        {
            var active=frame.Dazzles.IsDefault?[]:frame.Dazzles.Select((d,i)=>(frame.Screen,d.Name,i,d.Definition)).ToHashSet();
            foreach(var key in _frontendDazzleHistory.Keys.ToArray())if(!active.Contains(key))_frontendDazzleHistory.Remove(key);
            if(frame.Dazzles.IsDefaultOrEmpty)return;
            for(int i=0;i<frame.Dazzles.Length;i++)
            {
                var dazzle=frame.Dazzles[i];
                var key=(frame.Screen,dazzle.Name,i,dazzle.Definition);
                if(!_frontendDazzleHistory.TryGetValue(key,out var history))_frontendDazzleHistory.Add(key,history=new(_dazzlePreviousFrameClock));
                _frameDazzles.Add((new(dazzle.Definition,dazzle.World){History=history},1,dazzle.Camera,0,1));
            }
            RenderDazzles(gl,fb,width,height,assets);
            _frameDazzles.Clear();
        }
        if(creationCosts is not null)
            creationCosts["arrival effect"]=(frame.Beams.IsDefault?0:frame.Beams.Length,
                System.Diagnostics.Stopwatch.GetTimestamp()-beamStart);
        if (Volatile.Read(ref _frontendSpeechRequest) is {} speech &&
            speech.Screen==frame.Screen && _audio is {} audio)
        {
            var receipt = audio.UpdateFrontendSpeech(speech.Identity,speech.Resource,assets,
                options.SoundSfx / 100f);
            Volatile.Write(ref _frontendSpeechReceipt,new(speech.Identity,receipt.Complete,receipt.Seconds,receipt.Status));
        }
        else
        {
            _audio?.StopFrontendSpeech();
            // A missing output device must not hold the creator at Megan's cue.
            // It is a local presentation failure, not a protocol failure.
            if(Volatile.Read(ref _frontendSpeechRequest) is {} requested &&
                requested.Screen==frame.Screen && _audio is null)
                Volatile.Write(ref _frontendSpeechReceipt,new(requested.Identity,true,0,"audio unavailable"));
        }
        if(frame.Screen=="characters")
        {
            WarmFrontendCreation(gl,assets);
            active.UnionWith(_frontendPrewarmKeys);
        }
        else if(frame.Screen=="creation")active.UnionWith(_frontendPrewarmKeys);
        foreach (var key in _frontendGpu.Keys.Where(k => !active.Contains(k)).ToArray())
        {
            _frontendGpu[key].Gpu?.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
            _frontendGpu.Remove(key);
        }
        Interlocked.Exchange(ref _frontendLastRenderMilliseconds,renderTimer.ElapsedMilliseconds);
        if(creationCosts is not null)
        {
            Volatile.Write(ref _frontendLastCreationCostSummary,string.Join("; ",creationCosts.Select(p=>
                $"{p.Key}={p.Value.Count} draws/{p.Value.Ticks*1000/System.Diagnostics.Stopwatch.Frequency} ms")));
            Volatile.Write(ref _frontendLastShipCostBreakdown,
                $"setup={shipSetup*1000/System.Diagnostics.Stopwatch.Frequency} ms; lane={shipLane*1000/System.Diagnostics.Stopwatch.Frequency} ms; GPU create={shipGpu*1000/System.Diagnostics.Stopwatch.Frequency} ms; submit={shipSubmit*1000/System.Diagnostics.Stopwatch.Frequency} ms");
        }
        Volatile.Write(ref _frontendLastRenderedScreen,frame.Screen);
        Interlocked.Exchange(ref _frontendLastRenderCompletedTick,Environment.TickCount64);
        CompleteCaptureLightingFrame();
    }

    private static bool FrontendTextureStorageChanged(EnbW3dProgrammableTextureLanePlan old,
        EnbW3dProgrammableTextureLanePlan current)
    {
        if(old.RunStates.Length!=current.RunStates.Length)return true;
        for(int i=0;i<old.RunStates.Length;i++)
        {
            var before=old.RunStates[i];var after=current.RunStates[i];
            if(!string.Equals(before.Texture0.LogicalName,after.Texture0.LogicalName,StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(before.Texture1?.LogicalName,after.Texture1?.LogicalName,StringComparison.OrdinalIgnoreCase) ||
                before.Mapper0Program!=after.Mapper0Program)
                return true;
        }
        return false;
    }

    private static bool FrontendMapper1Changed(EnbW3dProgrammableTextureLanePlan old,
        EnbW3dProgrammableTextureLanePlan current)
    {
        if(old.RunStates.Length!=current.RunStates.Length)return true;
        for(int i=0;i<old.RunStates.Length;i++)
            if(old.RunStates[i].Mapper1Program!=current.RunStates[i].Mapper1Program)return true;
        return false;
    }
    private void DestroyFrontend(EnbGlApi? gl)
    {
        _frontendClassShipMaterials.Clear();
        foreach (var item in _frontendGpu.Values)
        {
            if(item.Gpu is not {} gpu)continue;
            item.Recovery=RetainGpuState(gpu);
            item.RecoveryLane=gpu.Textured!.LanePlan;
            gpu.Destroy(gl, _unlitBackend, _litBackend, _texturedBackend);
            item.Gpu=null;
        }
        foreach(var item in _frontendBeamGpu.Values)item.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        _frontendBeamGpu.Clear();
    }
    private void RenderFrontendBeams(EnbGlApi gl,int fb,int width,int height,
        ImmutableArray<FrontendBeamInstance> beams,EnbRetailAssetBundle assets)
    {
        var active=new HashSet<(EnbStationBeamEffect Owner,int Actor,string Mesh,int Pass)>();
        uint clock=ReadMapperClock();
        uint delta=_frontendTeleportClock is {} previous?unchecked(clock-previous):0;
        _frontendTeleportClock=clock;
        var requested=beams.IsDefault?[]:beams.ToArray();
        foreach(var retained in _frontendTeleportOwners.Values.ToArray())
            if(!requested.Any(b=>ReferenceEquals(b.Effect,retained.Effect)))retained.Effect.Destroy(clock);
        foreach(var beam in requested)
        {
            try
            {
                if(!_frontendTeleportScenes.TryGetValue(beam.Scene,out var scene))
                    _frontendTeleportScenes[beam.Scene]=scene=new();
                beam.Effect.Prepare(scene,clock,unchecked((uint)beam.Elapsed),beam.World);
                _frontendTeleportOwners[beam.Effect]=beam;
            }
            catch(Exception ex)
            {
                beam.Effect.Destroy(clock);
                Volatile.Write(ref _status,new LiveWorldW3dStatus("Frontend teleport unavailable: "+ex.Message));
            }
        }
        foreach(var scene in _frontendTeleportScenes.Values)scene.Advance(clock,delta,error=>
            Volatile.Write(ref _status,new LiveWorldW3dStatus("Frontend teleport unavailable: "+error.Message)));
        if(_frontendTeleportOwners.Count!=0)
        {
            gl.DepthMask(1);gl.Clear(EnbGlConstants.GL_DEPTH_BUFFER_BIT);
            foreach(var beam in _frontendTeleportOwners.Values)
            {
            try
            {
            foreach(var packet in beam.Effect.Draw(clock,beam.Camera))
            {
                if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error))
                    throw new InvalidDataException("Creator teleport: "+error);
                bool particle=packet.Mesh.Name.EndsWith(".ParticleBuffer",StringComparison.Ordinal);
                var key=(beam.Effect,beam.Actor,packet.Mesh.Name,packet.Material.Source.Ordinal);
                LiveLanePlan lane;LiveGpuResource gpu;
                if(!particle && _frontendBeamGpu.TryGetValue(key,out var cached))
                {
                    if(!EnbW3dProgrammableTextureLaneCompiler.TryUpdateFrame(cached.Lane.Textured!,submission,
                        beam.Camera,out var updated,out error,geometryWillBeUploaded:true))
                        throw new InvalidDataException("Creator teleport frame: "+error);
                    lane=LiveLanePlan.From(updated);
                    gpu=LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu.Textured!,updated));
                    _frontendBeamGpu[key]=(lane,gpu);
                }
                else
                {
                    if(!TryCompileLane(submission,beam.Camera,EnbRenderTextureRuntimeSettings.RetailDefaults,
                        EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,
                        new Dictionary<string,EnbTextureImage>(),null,LiveLaneKind.Textured,out lane,out error) ||
                        !TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out gpu,out error))
                        throw new InvalidDataException("Creator teleport: "+error);
                    if(!particle)_frontendBeamGpu[key]=(lane,gpu);
                }
                active.Add(key);
                try { SubmitGpuResource(gl,fb,width,height,gpu,lane,new(false,0,0,0),default,null,
                    clock); }
                finally { if(particle)gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend); }
            }
            }
            catch(Exception ex)
            {
                // Optional teleport failure is local to this effect, including
                // creation actors as well as the deletion presentation.
                Volatile.Write(ref _status,new LiveWorldW3dStatus("Frontend teleport unavailable: "+ex.Message));
            }
            }
        }
        foreach(var scene in _frontendTeleportScenes.Values)scene.CompleteRender(clock);
        foreach(var owner in _frontendTeleportOwners.Keys.Where(e=>e.Retired).ToArray())
            _frontendTeleportOwners.Remove(owner);
        foreach(var key in _frontendBeamGpu.Keys.Where(k=>!active.Contains(k)).ToArray())
        {
            _frontendBeamGpu[key].Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
            _frontendBeamGpu.Remove(key);
        }
    }
}
