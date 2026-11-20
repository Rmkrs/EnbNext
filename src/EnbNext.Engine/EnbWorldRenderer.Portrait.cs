using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    internal Func<EnbSceneAvatarSnapshot,EnbAvatarPortraitAsset?>? StationLocalPortraitAccessor {get;set;}
    internal Func<uint,EnbSceneAvatarSnapshot,EnbAvatarPortraitAsset?>? StationRemotePortraitAccessor {get;set;}
    internal Func<IEnumerable<EnbUiModelDraw>>? StationPortraitsAccessor {get;set;}
    // One retained actor per visible portrait. Trading draws two different avatars
    // in the same frame; a single cache reloads both actors on every draw.
    private sealed class PortraitState
    {
        public EnbSceneAvatarSnapshot? _portraitIdentity;
        public EnbSceneEnvironmentSnapshot? _portraitEnvironment;
        public EnbRetailAssetBundle? _portraitAssets;
        public long TextureRevision;
        public string? Diagnostic;
        public EnbAvatarPortraitAsset? _portraitAsset;
        public readonly List<(LiveLanePlan Lane,LiveGpuResource Gpu)> _portraitGpu=[];
        public (int W,int H) _portraitSize;
        public long _portraitStarted;
        public LiveAvatarSpeechPlayback? IdlePlayback;
        public EnbDialogueSpeechPage? SpeechPage;
        public string EditorTag="breathe1";
        public EnbAvatarPortraitAsset? EditorLast;
        public (bool FullBody,float Heading) _portraitView;
        public Vector3? _portraitCustomizationView;
        public double? _portraitAspect;
        public float _portraitCameraDistance;
        public float _portraitPanOrigin;
        public EnbResolvedRenderCamera? _portraitPreviewCamera;
    }
    private readonly Dictionary<string,PortraitState> _portraits=[];
    // The dialogue buffer is cleared after consumption, independently of the
    // avatar released by 0064F4F0 -> 007E4350 on vendor entry.
    private EnbDialogueSpeechPage? _consumedPortraitSpeechPage;
    private readonly EnbOpenGlScannerFramebuffer _portraitFramebuffer=new();
    private float _portraitCameraDistance;
    private float _portraitPanOrigin;
    public string? PortraitDiagnostic {get;private set;}
    private double _portraitPoseMs,_portraitUploadMs,_portraitDrawMs;
    public int PortraitBuildCount {get;private set;}
    private void DestroyPortrait(EnbGlApi? gl)
    {
        foreach(var state in _portraits.Values)DestroyPortrait(gl,state);
        _portraits.Clear();
    }
    private void DestroyPortrait(EnbGlApi? gl,PortraitState state,bool preservePlayback=false)
    {
        foreach(var part in state._portraitGpu)part.Gpu.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);
        state._portraitGpu.Clear();state._portraitIdentity=null;state._portraitAsset=null;
        if(!preservePlayback){state.SpeechPage=null;state.IdlePlayback=null;state.EditorLast=null;state.EditorTag="breathe1";}
    }
    private void RenderPortrait(EnbGlApi gl,int fb,int width,int height,double scaling,EnbUiRect bounds,EnbSceneAvatarSnapshot avatar,EnbSceneEnvironmentSnapshot? environment,bool fullBody=false,float heading=0,Vector3? customizationView=null,double? authoredAspect=null,string cacheKey="default",EnbAvatarPortraitAsset? pose=null,EnbDialogueSpeechPage? speechPage=null,bool sharedActor=false)
    {
        //7E3A20/7E4520 retain the same actor and do not override its HLOD visibility.
        if(pose?.PresentationHidden==true)return;
        if(_retailAssetsAccessor() is not {} assets)return;
        if(!_portraits.TryGetValue(cacheKey,out var state))_portraits.Add(cacheKey,state=new());
        int x=(int)Math.Round(bounds.X*scaling),y=height-(int)Math.Round(bounds.Bottom*scaling),w=(int)Math.Round(bounds.Width*scaling),h=(int)Math.Round(bounds.Height*scaling);
        if(w<1||h<1||x<0||y<0||x+w>width||y+h>height)return;
        bool sameTextures=state.TextureRevision==assets.TextureRevision;
        bool sameAvatar=SamePortrait(state._portraitIdentity,avatar);
        // 007E3A20/007E4520 render the retained actor itself, including any
        // retained head after 0077F000 fails to acquire a replacement. A fresh
        // load from the description is not necessarily that actor's model.
        bool sameModel=pose is null || state._portraitAsset is {} previousModel &&
            previousModel.Parts.Select(p=>(p.Resource.Key,p.Mesh.Ordinal,p.Geometry.Positions.Count,p.Geometry.Normals.Count,p.Mesh.MaterialPasses.Count))
                .SequenceEqual(pose.Parts.Select(p=>(p.Resource.Key,p.Mesh.Ordinal,p.Geometry.Positions.Count,p.Geometry.Normals.Count,p.Mesh.MaterialPasses.Count)));
        // Portrait lighting is fixed. Its only environment input is the reflection
        // selector (customization/null environment use station reflection). Snapshot
        // metadata contains freshly copied arrays and is not a resource identity.
        short? reflectionSelector=customizationView is null?environment?.BaseAsset:null;
        short? previousReflectionSelector=state._portraitCustomizationView is null?state._portraitEnvironment?.BaseAsset:null;
        bool sameEnvironment=reflectionSelector==previousReflectionSelector;
        if(!sameTextures||!sameAvatar||!sameModel||state._portraitSize!=(w,h)||!sameEnvironment||!ReferenceEquals(state._portraitAssets,assets)||state._portraitView!=(fullBody,heading)||state._portraitCustomizationView!=customizationView||state._portraitAspect!=authoredAspect)
        {
            PortraitBuildCount++;
            // Editing appearance preserves the breathing animation's phase.
            if(state._portraitIdentity is null||!ReferenceEquals(state._portraitAssets,assets))state._portraitStarted=Environment.TickCount64;
            var retained=sameAvatar&&sameModel&&ReferenceEquals(state._portraitAssets,assets)?state._portraitAsset:null;
            // Camera movement changes lane transforms, not mesh/texture storage.
            // Recreating those GL objects on every held-control tick stalls motion.
            var reusable=new Queue<LiveGpuResource>();
            if(sameTextures&&sameAvatar&&sameModel&&sameEnvironment&&ReferenceEquals(state._portraitAssets,assets)){foreach(var old in state._portraitGpu)reusable.Enqueue(old.Gpu);state._portraitGpu.Clear();}
            else DestroyPortrait(gl,state,ReferenceEquals(state._portraitAssets,assets) && ((!sameTextures && sameAvatar) || customizationView is not null && state._portraitCustomizationView is not null));
            state.TextureRevision=assets.TextureRevision;
            state._portraitIdentity=avatar;state._portraitSize=(w,h);state._portraitEnvironment=environment;state._portraitAssets=assets;state.Diagnostic=null;
            state._portraitView=(fullBody,heading);
            state._portraitCustomizationView=customizationView;
            state._portraitAspect=authoredAspect;
            try
            {
                state._portraitAsset=pose??retained??EnbAvatarPortraitAsset.Load(assets,avatar);
                if(!sharedActor && pose is null && state.IdlePlayback is null)
                {
                    state.IdlePlayback=new(assets,avatar,portraitIdle:true);
                    if(customizationView is null)state.IdlePlayback.StartIdle(Environment.TickCount64*.001);
                    else
                    {
                        // 0054BE44: appearance editor suppresses fidgets, while
                        // retaining default head motion and facial animation.
                        state.IdlePlayback.Idle.SuppressFidgets=true;
                        state.IdlePlayback.Idle.StandStill=!fullBody;
                        state.IdlePlayback.StartIdle(Environment.TickCount64*.001);
                    }
                }
                var orientation=Quaternion.CreateFromRotationMatrix(new(-1,0,0,0,0,-1,0,0,0,0,1,0,0,0,0,1));
                var center=state._portraitAsset.HeadAnchor+new Vector3(0,0,.1f);
                var cameraPosition=center+new Vector3(2.5f,0,0);
                if(fullBody) { center=new(0,0,state._portraitAsset.HeadAnchor.Z*.5f); cameraPosition=center+new Vector3(Math.Max(3,state._portraitAsset.HeadAnchor.Z*1.3f),0,0); }
                if(customizationView is {} editor)
                {
                    // The avatar's bound delegates to its main body object;
                    // attached head/accessories do not enlarge this camera fit.
                    var bodyResource=state._portraitAsset.Parts[0].Resource;
                    var positions=state._portraitAsset.Parts.Where(p=>ReferenceEquals(p.Resource,bodyResource)).SelectMany(p=>p.Geometry.Positions).ToArray();
                    var min=positions.Aggregate(Vector3.Min);var max=positions.Aggregate(Vector3.Max);
                    var midpoint=(min+max)*.5f;
                    // 0080C640 recenters the actor; 0080E260/E350 select the
                    // body/head distance factors 3/.75 and vertical pan 2.5/4.
                    float avatarHeight=state._portraitAsset.HeadAnchor.Z-state._portraitAsset.SkeletonReference.Translation.Z;
                    state._portraitPanOrigin=fullBody?2.5f:4+(avatarHeight-7.3f);
                    center=midpoint+new Vector3(0,0,state._portraitPanOrigin);
                    float extent=(max.Z-min.Z)*.5f;
                    cameraPosition=center+new Vector3(extent*(fullBody?3:.75f)*editor.Z,0,0);
                }
                if(heading!=0) { var turn=Quaternion.CreateFromAxisAngle(Vector3.UnitZ,heading); cameraPosition=center+Vector3.Transform(cameraPosition-center,turn); orientation=Quaternion.Concatenate(orientation,turn); }
                if(customizationView is {} panView)
                {
                    var pan=Vector3.Transform(new Vector3(0,-panView.X,panView.Y),Quaternion.CreateFromAxisAngle(Vector3.UnitZ,heading));
                    center+=pan;cameraPosition+=pan;
                }
                // 0054743B selects close portrait; 00786410 uses K_HEAD +0.1, distance 2.5.
                float distance=cameraPosition.Length();
                state._portraitCameraDistance=Vector3.Distance(cameraPosition,center);
                // 007E4740 derives the character preview projection from authored PICTURE bounds.
                double aspect=customizationView is null?authoredAspect??(double)w/h:640.0/480;
                if(!EnbResolvedRenderCamera.TryCreate(cameraPosition,orientation,new(w,h,1),2*Math.Atan(.5*aspect),aspect,new(.1f,2*distance),out var camera))return;
                state._portraitPreviewCamera=camera;
                // 007E31B0: ambient 0.5, diffuse 1, specular 0; 007E3A20 positions the light.
                var lighting=EnbW3dLightEnvironment.CreateCurrentRetail(0,[new(true,new(EnbW3dLightType.Point,Vector3.One,Vector3.Zero,new(.5f),new(0,distance*1.5f,distance*1.5f),Vector3.Zero,1000000,1,1,0,0,0,0))]);
                if(customizationView is not null)
                {
                    Vector3 Direction(Vector3 p)=>camera.Basis.Right*p.X+camera.Basis.Up*p.Y-camera.Basis.Forward*p.Z;
                    // 0080BAB0, race-indexed static initializers 0080B930/B9B0/BA30.
                    var ambient=avatar.Race==0?new Vector3(.15f,.1f,.09f):new Vector3(.15f,.1f,.1f);
                    var diffuse=avatar.Race==0?new Vector3(.9f,.9f,.75f):new Vector3(.9f,.8f,.85f);
                    var fill=avatar.Race switch {0=>new Vector3(.5f,0,0),1=>new Vector3(.5f,.4f,.1f),_=>new Vector3(.1f,.45f,.45f)};
                    // 0080C220 resets the mode-1 preview scene's ambient to black
                    // after construction; its key/fill lights remain unit intensity.
                    lighting=EnbW3dLightEnvironment.CreateCurrentRetail(0,[
                        new(true,new(EnbW3dLightType.Directional,diffuse,Vector3.Zero,ambient,Vector3.Zero,Direction(new(.5f,-1,-2)),1000000,1,1,0,0,0,0)),
                        new(true,new(EnbW3dLightType.Directional,fill,Vector3.Zero,Vector3.Zero,Vector3.Zero,Direction(new(-.75f,1,1)),1000000,1,1,0,0,0,0))]);
                }
                // Customization retains station entry's local reflection selection,
                // independently of the server's space backdrop.
                EnbEnvironmentReflectionDefinition reflection;
                if(customizationView is not null || environment is null)
                {
                    if(assets.EnvironmentMaps?.TryResolveReflection(EnbCurrentRetailStationInterior.ReflectionBaseAsset,out reflection!)!=true)
                        throw new InvalidDataException("Station portrait reflection unavailable.");
                }
                else if(!EnbCurrentRetailEnvironmentMapRuntime.TryResolveReflection(assets.EnvironmentMaps,environment,out reflection,out var reflectionError))
                    throw new InvalidDataException(reflectionError);
                var atlas=ResolveEnvironmentAtlasSet(assets,assets.EnvironmentMaps!,reflection,0,out var atlasError);
                if(atlas is null)throw new InvalidDataException(atlasError);
                EnbSceneColorizationSnapshot Entry(uint metal,ImmutableArray<float> color)=>new(metal,color.SelectMany(BitConverter.GetBytes).ToImmutableArray());
                EnbSceneColorizationSnapshot[] colors=[Entry(avatar.ShirtPrimaryMetal,avatar.ShirtPrimaryColor),Entry(avatar.ShirtSecondaryMetal,avatar.ShirtSecondaryColor),Entry(avatar.PantsPrimaryMetal,avatar.PantsPrimaryColor),Entry(avatar.PantsSecondaryMetal,avatar.PantsSecondaryColor)];
                foreach(var part in state._portraitAsset.Parts)foreach(var authored in part.Mesh.MaterialPasses)
                {
                    var packet=new EnbW3dDrawTask(new(EnbRenderPassKind.ModernOverlayBoundary,EnbW3dDrawBlendClass.Blended,part.Mesh.StaticSort,part.Resource.Key,part.Mesh.Ordinal,authored.Ordinal,1,0),1,1,0,
                        Matrix4x4.Identity,new(Vector3.Zero,20),part.Mesh,part.Geometry,new(authored,authored.SingleBindingSemantics),lighting,0,null,0,part.Mesh.Name);
                    packet=EnbCurrentRetailShipColorizationRuntime.Apply(packet,colors,part.Mesh.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase)?1:0,reflection.Axis,out var colorReceipt);
                    if(colorReceipt.IsTarget&&!colorReceipt.Applied)throw new InvalidDataException(colorReceipt.Diagnostic);
                    var pass=PortraitMaterial(packet.Material.Source,part.Mesh.Name,avatar);
                    packet=(packet).ForkTask().SetMaterial(new(pass,pass.SingleBindingSemantics));
                    if(!EnbW3dIndexedSubmissionCompiler.TryCompile(packet,out var submission,out var error)||
                        !TryCompileLane(submission,camera,EnbRenderTextureRuntimeSettings.RetailDefaults,EnbRenderTextureBackendCapabilities.ModernDefault,assets.Textures,new Dictionary<string,EnbTextureImage>(),atlas,LiveLaneKind.Textured,out var lane,out error))throw new InvalidDataException("Portrait GPU: "+error);
                    LiveGpuResource gpu;
                    if(reusable.Count>0)gpu=reusable.Dequeue();
                    else if(!TryCreateGpuResource(gl,lane,LiveLaneKind.Textured,0,out gpu,out error))throw new InvalidDataException("Portrait GPU: "+error);
                    state._portraitGpu.Add((lane,gpu));
                }
            }
            catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
            {
                DestroyPortrait(gl,state);state._portraitIdentity=avatar;state.Diagnostic=ex.Message;
                System.Diagnostics.Trace.TraceError("Avatar portrait unavailable: {0}",ex.Message);
            }
            finally {while(reusable.TryDequeue(out var unused))unused.Destroy(gl,_unlitBackend,_litBackend,_texturedBackend);}
        }
        PortraitDiagnostic=state.Diagnostic;
        _portraitCameraDistance=state._portraitCameraDistance;_portraitPanOrigin=state._portraitPanOrigin;
        if(state._portraitGpu.Count==0)return;
        // 0064F630 -> 007E3A20 -> 00784A50 replaces the portrait queue
        // once for each nonempty page; ordinary redraws only advance playback.
        if(speechPage is not null && !ReferenceEquals(state.SpeechPage,speechPage) && state.IdlePlayback is {} speech)
        {
            state.SpeechPage=speechPage;
            if(!ReferenceEquals(_consumedPortraitSpeechPage,speechPage))
            {
                _consumedPortraitSpeechPage=speechPage;
                if(speechPage.Text.Length>0)speech.Replace(speechPage.Text,Environment.TickCount64*.001);
            }
        }
        try
        {
            if(state._portraitAsset?.Evaluate is {} evaluate)
            {
                long profile=System.Diagnostics.Stopwatch.GetTimestamp();
                var posed=pose ?? (sharedActor ? state._portraitAsset : customizationView is not null ? EvaluateEditorPortrait(state,assets,avatar)
                    : state.IdlePlayback?.Evaluate(Environment.TickCount64*.001)) ?? evaluate((Environment.TickCount64-state._portraitStarted)*.001);
                // 7E3A20 advances the avatar through +1C4; its HLOD callback
                // releases animation sounds at captured actor/bone positions.
                // Externally owned poses already dispatch through their owner.
                if(pose is null && !sharedActor && state.IdlePlayback is {} playback && posed.AnimationSoundPosition is {} position)
                    foreach(var cue in playback.AnimationSounds)
                        QueueClientAnimationSound(cue.Sound.Sound,cue.Sound.Flat,position(cue.Bone));
                _portraitPoseMs+=System.Diagnostics.Stopwatch.GetElapsedTime(profile).TotalMilliseconds;profile=System.Diagnostics.Stopwatch.GetTimestamp();
                if(posed.Parts.Sum(p=>p.Mesh.MaterialPasses.Count)!=state._portraitGpu.Count)
                    throw new InvalidDataException("Portrait animation changed model membership.");
                int index=0;
                foreach(var part in posed.Parts)foreach(var pass in part.Mesh.MaterialPasses)
                {
                    var cached=state._portraitGpu[index];
                    var previous=cached.Lane.Textured!;
                    var packet=(previous.Submission.Packet).ForkTask().SetDeformedGeometry(part.Geometry);
                    if(part.Geometry.Positions.Count!=previous.Submission.Positions.Count ||
                        part.Geometry.Normals.Count!=previous.Submission.Normals.Count)
                        throw new InvalidDataException("Portrait animation changed vertex topology.");
                    // The actor retains the exact mesh/material pass for its lifetime.
                    // Skinning changes vertices, not indices or triangle bindings; do
                    // not resolve every triangle's material again on every pose.
                    var submission=previous.Submission with {Packet=packet};
                    // Pose changes only the vertex stream: camera, lighting and authored
                    // materials stay fixed for this portrait resource's lifetime.
                    var lighting=(previous.LightingPlan).Copy().SetSubmission(submission);
                    var lane=(previous).Copy().SetLightingPlan(lighting);
                    state._portraitGpu[index++]=(LiveLanePlan.From(lane),LiveGpuResource.From(_texturedBackend.UpdateGeometry(gl,cached.Gpu.Textured!,lane)));
                }
                _portraitUploadMs+=System.Diagnostics.Stopwatch.GetElapsedTime(profile).TotalMilliseconds;
            }
            }
        catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            // Optional avatar resources must not take down unrelated UI/world
            // rendering. Keep the diagnostic; do not invent replacement parts.
            DestroyPortrait(gl,state);state._portraitIdentity=avatar;
            PortraitDiagnostic=state.Diagnostic=ex.Message;
            System.Diagnostics.Trace.TraceError("Avatar portrait unavailable: {0}",ex.Message);
            return;
        }
        long drawStarted=System.Diagnostics.Stopwatch.GetTimestamp();
        int target=_portraitFramebuffer.Begin(gl,fb,x,y,w,h);
        try
        {
            foreach(var part in state._portraitGpu)SubmitGpuResource(gl,target,w,h,part.Gpu,part.Lane,new(false,0,0,0),default,null,0,compositeOpaqueCoverage:true);
        }
        finally{_portraitFramebuffer.End(gl,fb,x,y);gl.Viewport(0,0,width,height);_portraitDrawMs+=System.Diagnostics.Stopwatch.GetElapsedTime(drawStarted).TotalMilliseconds;}
    }
    internal static bool SamePortrait(EnbSceneAvatarSnapshot? a,EnbSceneAvatarSnapshot b)
    {
        if(ReferenceEquals(a,b))return true;
        if(a is null)return false;
        return a with {TattooOffset=b.TattooOffset,HairColor=b.HairColor,BeardColor=b.BeardColor,EyeColor=b.EyeColor,SkinColor=b.SkinColor,
            ShirtPrimaryColor=b.ShirtPrimaryColor,ShirtSecondaryColor=b.ShirtSecondaryColor,PantsPrimaryColor=b.PantsPrimaryColor,PantsSecondaryColor=b.PantsSecondaryColor,
            BodyWeight=b.BodyWeight,HeadWeight=b.HeadWeight}==b&&
            a.TattooOffset.AsSpan().SequenceEqual(b.TattooOffset.AsSpan())&&a.HairColor.AsSpan().SequenceEqual(b.HairColor.AsSpan())&&a.BeardColor.AsSpan().SequenceEqual(b.BeardColor.AsSpan())&&
            a.EyeColor.AsSpan().SequenceEqual(b.EyeColor.AsSpan())&&a.SkinColor.AsSpan().SequenceEqual(b.SkinColor.AsSpan())&&
            a.ShirtPrimaryColor.AsSpan().SequenceEqual(b.ShirtPrimaryColor.AsSpan())&&a.ShirtSecondaryColor.AsSpan().SequenceEqual(b.ShirtSecondaryColor.AsSpan())&&
            a.PantsPrimaryColor.AsSpan().SequenceEqual(b.PantsPrimaryColor.AsSpan())&&a.PantsSecondaryColor.AsSpan().SequenceEqual(b.PantsSecondaryColor.AsSpan())&&
            a.BodyWeight.AsSpan().SequenceEqual(b.BodyWeight.AsSpan())&&a.HeadWeight.AsSpan().SequenceEqual(b.HeadWeight.AsSpan());
    }
    private static EnbW3dRenderMaterialPass PortraitMaterial(EnbW3dRenderMaterialPass pass,string mesh,EnbSceneAvatarSnapshot avatar)
    {
        if(avatar.AvatarType!=0)return pass;
        Vector3? Color(EnbW3dRenderVertexMaterial material)
        {
            var values=material.Name.ToLowerInvariant() switch
            {
                "~skin"=>avatar.SkinColor,"~hair"=>avatar.HairColor,"~beard"=>avatar.BeardColor,
                _=>mesh.Contains("EYE01")&&material.Ordinal==1?avatar.EyeColor:default
            };
            return !values.IsDefault&&values.Length==3?new(values[0],values[1],values[2]):null;
        }
        EnbW3dCompiledMaterialSemantics? Tint(EnbW3dCompiledMaterialSemantics? semantics,EnbW3dRenderVertexMaterial? material)
        {
            if(semantics is not {} s)return semantics;
            if(material is {} m&&Color(m) is {} color)s=s with {Uniforms=s.Uniforms with {Ambient=color,Diffuse=color}};
            if(mesh.StartsWith("HEAD",StringComparison.OrdinalIgnoreCase)&&avatar.TattooOffset.Length==3)
            {
                float scale=Math.Clamp(avatar.TattooOffset[2],.5f,16);
                // 00785C00 installs a linear mapper, not just shader uniforms:
                // the runtime program otherwise overwrites those uniforms.
                var mapper=new EnbW3dMapperUniforms(new(scale),Vector2.Zero,new(.5f-avatar.TattooOffset[0]*scale,.5f-avatar.TattooOffset[1]*scale),true,true);
                s=s with {Uniforms=s.Uniforms with {Mapper1=mapper},
                    Mapper1Program=s.Mapper1Program with {Kind=EnbW3dMapperRuntimeProgramKind.Linear,CoordinateSource=EnbW3dMapperCoordinateSource.PassThrough,TransformShape=EnbW3dMapperTransformShape.Count2,BaseUniforms=mapper,UsesElapsedTime=true,RequiresMutableState=true,RequiresViewTransform=false},
                    PermutationKey=s.PermutationKey with {Mapper1=new(EnbW3dMapperTypeSelector.Mode04,EnbW3dMapperRuntimeProgramKind.Linear,EnbW3dMapperCoordinateSource.PassThrough,EnbW3dMapperTransformShape.Count2,true)}};
            }
            return s;
        }
        string prefix=(avatar.Gender==0?"AM":"AF")+new[]{"T","J","P"}[avatar.Race];
        EnbW3dRenderTextureBinding Replace(EnbW3dRenderTextureBinding t,int stage)=>!mesh.StartsWith("HEAD")?t:t with
            {LogicalName=stage==0?$"{prefix}FACETEX{avatar.HeadTextureNum:00}.tga":$"ATATTOO{avatar.TattooTextureNum:00}.tga",Location=null,ResolveLogicalNameAtRuntime=true};
        return (pass).Copy().SetSingleBindingSemantics(Tint(pass.SingleBindingSemantics,pass.VertexMaterials.Count>0?pass.VertexMaterials[0].Material:null)).SetTextureStages(pass.TextureStages.Select(s=>(s).Copy().SetTextures(s.Textures.Select(t=>Replace(t,s.Ordinal)).ToArray())).ToArray()).SetTriangleBindings(pass.TriangleBindings.Select(t=>t with {Semantics=Tint(t.Semantics,t.VertexMaterial?.Material),Textures=t.Textures.Select((b,i)=>b is null?null:Replace(b,i)).ToArray()}).ToArray());
    }
    private static EnbAvatarPortraitAsset? EvaluateEditorPortrait(PortraitState state,EnbRetailAssetBundle assets,EnbSceneAvatarSnapshot avatar)
    {
        if(state.IdlePlayback is not {} playback)return null;
        try
        {
        bool standStill=!state._portraitView.FullBody;
        if(playback.Idle.StandStill!=standStill)
        {
            // 0054DFE0 changes +231 and requests selection after slot advance.
            playback.Idle.StandStill=standStill;
            playback.RequestIdleSelection();
        }
        playback.RefreshAppearance(avatar);
        var result=playback.Evaluate(Environment.TickCount64*.001);
        state.EditorTag=playback.AnimationTag??state.EditorTag;
        state.Diagnostic=playback.Diagnostic;
        return state.EditorLast=result??state.EditorLast;
        }
        catch(AvatarAnimationUnavailableException ex)
        {
            state.Diagnostic="Avatar editor action unavailable: "+ex.Message;
            return state.EditorLast ?? state._portraitAsset;
        }
    }
}

