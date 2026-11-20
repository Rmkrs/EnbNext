using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Rendering;

public sealed record EnbAvatarPortraitPart(EnbW3dRenderResource Resource, EnbW3dRenderMesh Mesh, EnbW3dDeformedGeometry Geometry)
{
    // Retained mesh data and rigid member transform for 00940530. Skinning
    // changes the draw geometry, not the mesh collision model.
    public EnbW3dRenderMesh? CollisionMesh { get; init; }
    public Matrix4x4 CollisionTransform { get; init; }
}
public delegate EnbRetailAvatarFacialMap CreateFaceMap(int mood,string text,float previous,bool authored,Func<int,int,int>? random=null,string? animationName=null);

public sealed record EnbAvatarPortraitAsset(ImmutableArray<EnbAvatarPortraitPart> Parts, Vector3 HeadAnchor)
{
    public bool PresentationHidden {get;init;}
    // 77E9D0 attaches/reads K_HEAD and skel_reference through RenderObj+80/+B8;
    // both resolve with 98A000: first case-insensitive match, otherwise pivot 0.
    private static int RetailBone(EnbW3dHierarchy hierarchy,string name)
    {
        for(int i=0;i<hierarchy.Pivots.Count;i++)
            if(string.Equals(hierarchy.Pivots[i].Name,name,StringComparison.OrdinalIgnoreCase))return i;
        return 0;
    }
    public Func<EnbResolvedRenderCamera,Matrix4x4,EnbRetailMeshPick[]>? CreatePickMeshes {get;init;}
    public EnbW3dWorldBounds PreviewSphere { get; init; }
    public EnbW3dWorldBounds PreviewBodySphere { get; init; }
    public EnbW3dWorldBounds PreviewHeadSphere { get; init; }
    public Func<double,EnbAvatarPortraitAsset>? Evaluate {get;init;}
    public Func<double,EnbAvatarPortraitAsset?,float,EnbAvatarPortraitAsset>? EvaluateClamped {get;init;}
    public Func<double,EnbAvatarPortraitAsset?,float,Matrix4x4,EnbAvatarPortraitAsset>? EvaluateClampedWithHead {get;init;}
    public float BlinkFrame {get;init;}
    public Matrix4x4 SkeletonReference { get; init; } = Matrix4x4.Identity;
    public double CycleDurationSeconds { get; init; }
    public string AnimationSoundKey { get; init; } = string.Empty;
    public double AnimationFramesPerSecond { get; init; }
    public double FacialFramesPerSecond { get; init; }
    public ImmutableArray<string> MissingAccessories { get; init; } = [];
    public ImmutableArray<EnbW3dResolvedWeightedAnimationSample> BodySamples { get; init; } = [];
    public Func<string,Vector3>? AnimationSoundPosition {get;init;}
    public Func<double,EnbAvatarPortraitAsset,float,EnbAvatarPortraitAsset>? EvaluateBlend { get; init; }
    public Func<double,EnbAvatarPortraitAsset?,float,float,float,float,EnbAvatarPortraitAsset>? EvaluateSpeech {get;init;}
    public double SpeechFramesPerSecond {get;init;}
    public CreateFaceMap? CreateFacialMap {get;init;}
    public Func<double,EnbAvatarPortraitAsset?,float,EnbRetailAvatarFace,EnbAvatarPortraitAsset>? EvaluateFacial {get;init;}
    public Func<double,EnbAvatarPortraitAsset?,float,EnbRetailAvatarFace,Matrix4x4,EnbAvatarPortraitAsset>? EvaluateFacialWithHead {get;init;}
    public Func<double,EnbAvatarPortraitAsset?,float,Matrix4x4,EnbAvatarPortraitAsset>? EvaluateUnboundHead {get;init;}
    public bool HeadAnimationBound {get;init;}=true;
    public (EnbW3dModel Model,EnbW3dRenderResource Resource)? HeadResource {get;init;}
    public string? HeadLoadDiagnostic {get;init;}
    public EnbRetailAvatarFace FacialPose {get;init;}
    public string? FacialResourceName {get;init;}
    // Current client 0077E9D0/0077F000/0077F960; gender/race tables at 00B9CD30/38.
    public static EnbAvatarPortraitAsset Load(EnbRetailAssetBundle assets, EnbSceneAvatarSnapshot avatar, string animationTag = "breathe1")
        => LoadCore(assets, avatar, animationTag, null);
    public static EnbAvatarPortraitAsset LoadRetainingHead(EnbRetailAssetBundle assets,EnbSceneAvatarSnapshot avatar,string tag,int? number,EnbAvatarPortraitAsset? retained,bool preserveHead)
        =>LoadCore(assets,avatar,tag,number,retained,preserveHead);
    private static EnbAvatarPortraitAsset LoadCore(EnbRetailAssetBundle assets, EnbSceneAvatarSnapshot avatar, string animationTag, int? animationNumber,EnbAvatarPortraitAsset? retained=null,bool preserveHead=false)
    {
        // 0077E9D0 / 0077F000 select ANPC body/head for both narrator (1)
        // and the named Help actor Megan (2); only type 0 uses player parts.
        // 0077E9D0/0077F000 test actor+0x100 (AvatarData+8, the type; 004BF550) against zero: every nonzero type takes the ANPC branch.
        bool narrator=avatar.AvatarType!=0;
        if(avatar.Gender>1||avatar.Race>2)throw new InvalidDataException("Unsupported portrait avatar identity.");
        string prefix=(avatar.Gender==0?"AM":"AF")+new[]{"T","J","P"}[avatar.Race];
        var definitions=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.CommonDataDirectory,"avatardef.ini"));
        string section=(avatar.Gender==0?"male":"female")+"_"+new[]{"terran","jenquai","progen"}[avatar.Race]+"_morph";
        // 00780AF0 passes the INI vector in different orders to mesh +0x160
        // (0093EEB0 -> 0092D460) and hierarchy 0098A2D0. The mesh's local
        // axes are not the actor hierarchy's world axes.
        Vector3 authoredBodyScale=narrator?Vector3.One:Scale(avatar.BodyWeight,false);
        Vector3 bodyScale=new(authoredBodyScale.Z,authoredBodyScale.X,authoredBodyScale.Y);
        Vector3 bodyMeshScale=new(authoredBodyScale.Y,authoredBodyScale.Z,authoredBodyScale.X);
        Vector3 headScale=narrator?Vector3.One:Scale(avatar.HeadWeight,true);
        var resources=new Dictionary<string,(EnbW3dModel Model,EnbW3dRenderResource Resource)>(StringComparer.OrdinalIgnoreCase);
        var pickMeshes=new Dictionary<(EnbW3dRenderMesh Mesh,Vector3 Scale),EnbRetailDazzleMeshRay>();
        var accessories=new List<string>();
        var accessoryKinds=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var missingAccessories=ImmutableArray.CreateBuilder<string>();
        foreach(var (kind,number) in new[]{("HAIR",avatar.HairNum),("EAR",avatar.EarNum),("GOGGLE",avatar.GoggleNum),("BEARD",avatar.BeardNum)})
        {
            if(number==0)continue;
            string name=$"{prefix}{kind}{number:00}.w3d";
            // 77F960 removes the previous accessory before acquisition. A null
            // factory result leaves it absent; 789D80 attaches named mesh children
            // only. A directory entry alone does not establish a usable model.
            try
            {
                var accessory=Read(name);
                if(accessory.Model.HierarchicalLods.Count!=1)
                    throw new InvalidDataException("Accessory has no HLOD mesh children: "+name);
                accessories.Add(name);
                accessoryKinds[name]=kind;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {missingAccessories.Add(name);}
        }
        var missing=missingAccessories.ToImmutable();
        var body=Read(narrator?$"ANPCBODY{avatar.BodyType:000}.w3d":$"{prefix}BODY{avatar.BodyType:00}{avatar.PantsType:00}.w3d");
        (EnbW3dModel Model,EnbW3dRenderResource Resource) head;
        string? headDiagnostic=null;
        if(preserveHead && retained?.HeadResource is {} preserved)
        {head=preserved;headDiagnostic=retained.HeadLoadDiagnostic;}
        else
        {
            try{head=Read(narrator?$"ANPCHEAD{avatar.HeadType:000}.w3d":$"{prefix}HEAD{avatar.HeadType:00}.w3d");}
            catch(Exception ex) when(retained?.HeadResource is not null && ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // 77F000 logs a failed source acquisition without replacing
                // the attached HeadObj. Independent body selection continues.
                head=retained.HeadResource.Value;headDiagnostic=ex.Message;
            }
        }
        var personality=EnbRetailIniDocument.Load(Path.Combine(assets.Paths.ClientDataDirectory,"personalities",$"Personality{avatar.Personality:00}.ini"));
        int action = animationNumber ?? int.Parse(personality.EnumerateSections().Select(s=>s.Value)
            .Single(s=>s.TryGetValue("Tag",out var tag)&&tag.Equals(animationTag,StringComparison.OrdinalIgnoreCase))["AnimNumber"],CultureInfo.InvariantCulture);
        string animationName=$"ABANIM{avatar.Personality:00}-{action:00}";
        var animation=Read(animationName+".w3d").Resource.Animations.Single();
        string requestedHierarchy=string.IsNullOrEmpty(avatar.Skeleton) ? (avatar.Gender==0?"AMDEFAULTSKEL":"AFDEFAULTSKEL") : avatar.Skeleton;
        if(narrator && !animation.HierarchyName.Equals(requestedHierarchy,StringComparison.OrdinalIgnoreCase))
            throw new AvatarAnimationUnavailableException(animationName);
        var blinkAnimation=Read((avatar.Gender==0?"AM":"AF")+"HANIM01.w3d").Resource.Animations.Single();
        var mouthAnimation=Read((avatar.Gender==0?"AM":"AF")+"HANIM00.w3d").Resource.Animations.Single();
        var eyeAnimation=Read((avatar.Gender==0?"AM":"AF")+"HANIM02.w3d").Resource.Animations.Single();
        int speechDelay=personality.TryGetSection("HEADER",out var speechHeader)&&int.TryParse(speechHeader.GetValueOrDefault("SpeechDelay"),out int speech)?Math.Max(1,speech):3;
        int blinkDelay=personality.TryGetSection("HEADER",out var header)&&int.TryParse(header.GetValueOrDefault("BlinkDelay"),out int delay)?Math.Max(1,delay):60;
        int cycleFrames=(int)Math.Max(1,animation.NumFrames-1);
        bool locomotion=animationTag is "walk_forward" or "run_forward" or "walk_backward";
        int scheduledCycle=-1;
        EnbRetailAvatarFacialMap? idleFace=null;
        // 00782475 -> 007829B0 passes the complete 00781D10 NAME unchanged.
        // 009C7EB0 / 004E57A0 / 009D0C60 do not append .ini or strip its skeleton.
        string facialName=requestedHierarchy+"."+animationName;
        var facialIni=assets.FileResources.TryReadResource(facialName,out var facialBytes,out _)
            ?EnbRetailIniDocument.Parse(System.Text.Encoding.Latin1.GetString(facialBytes)):null;
        EnbRetailAvatarFacialMap Face(int mood,string text,float previous,bool authored,Func<int,int,int>? random=null,string? animationName=null)
        {
            var selected=facialIni;
            // 782200/7829B0 query the requested command's facial NAME even
            // when its body lookup failed and C0 retained the preceding clip.
            if(animationName is not null)
                selected=assets.FileResources.TryReadResource(requestedHierarchy+"."+animationName,out var bytes,out _)
                    ?EnbRetailIniDocument.Parse(System.Text.Encoding.Latin1.GetString(bytes)):null;
            return new((int)animation.NumFrames,speechDelay,blinkDelay,mood,text,authored?selected:null,previous,random,commandActive:authored);
        }
        var (previewSphere,previewBodySphere,previewHeadSphere)=ComputePreviewSphere();
        (EnbW3dHlodBoundsOwner Body,EnbW3dHlodBoundsOwner Head)? pickParentBounds=null;
        return AtPose(0,null,1,facial:new(0,0,0));
        EnbAvatarPortraitAsset AtTime(double seconds)
            => AtPose(seconds, null, 1);
        EnbAvatarPortraitAsset AtPose(double seconds, EnbAvatarPortraitAsset? source, float transition,float mouthFrom=0,float mouthTo=0,float mouthBlend=0,bool clamped=false,Matrix4x4? headTurn=null,EnbRetailAvatarFace? facial=null,bool headAnimationBound=true)
        {
            var parts=ImmutableArray.CreateBuilder<EnbAvatarPortraitPart>();
            int cycle=clamped?0:(int)(seconds*animation.FrameRate/cycleFrames);
            float bodyFrame=clamped?(float)Math.Clamp(seconds*animation.FrameRate,0,cycleFrames):(float)(seconds*animation.FrameRate%cycleFrames);
            if(headAnimationBound && facial is null && cycle!=scheduledCycle)
            {
                scheduledCycle=cycle;
                idleFace=new((int)animation.NumFrames,speechDelay,blinkDelay,0,"",null,
                    idleFace?.FinalBrow??0,EnbRetailPresentationRandom.Shared.Next);
            }
            var face=headAnimationBound?facial??idleFace!.Sample(bodyFrame):new EnbRetailAvatarFace(0,0,0);
            float blink=face.Brow;
            // 007831C0 / 00783620 blend a completed clip into the next one over
            // 0.5 seconds. A hard modulo reset visibly snaps non-identical endpoints.
            // 007826A0 marks repeated locomotion with +215; 007831C0 then
            // replaces the current clip directly. Only clip changes crossfade.
            float blend = cycle == 0 || locomotion ? 1 : .5f - .5f * MathF.Cos(MathF.PI * Math.Clamp(bodyFrame / animation.FrameRate / .5f,0,1));
            ImmutableArray<EnbW3dResolvedWeightedAnimationSample> samples = blend < 1
                ? [new(animation,animation.NumFrames-1,1-blend),new(animation,bodyFrame,blend)]
                : [new(animation,bodyFrame,1)];
            if(source is not null && transition < 1)
                samples = source.BodySamples.Select(s=>s with {Weight=s.Weight*(1-transition)})
                    .Concat(samples.Select(s=>s with {Weight=s.Weight*transition})).Where(s=>s.Weight>0).ToImmutableArray();
            var bodyPose=Pose(body.Model,bodyScale,animation,bodyFrame,blend,samples,headTurn);
            var attachment=bodyPose.Transforms[RetailBone(bodyPose.Hierarchy,"K_HEAD")];
            Add(body,bodyPose.Transforms,bodyScale,Matrix4x4.Identity,bodyParts:true);
            var headPose=Pose(head.Model,headScale,headAnimationBound?blinkAnimation:null,blink);
            // Retail 0077DA20 installs all three head HAnims even while idle;
            // keep the neutral mouth pose layered with the blink channels.
            if(headAnimationBound)
            {
                var skeleton=MorphedHierarchy(head.Model,headScale);
                var transforms=ImmutableArray.CreateBuilder<Matrix4x4>(skeleton.Pivots.Count);
                transforms.Add(skeleton.Pivots[0].LocalTransform);
                foreach(var pivot in skeleton.Pivots.Skip(1))
                {
                    bool eyelid=blinkAnimation.Channels.Any(c=>c.PivotIndex==pivot.Ordinal)||blinkAnimation.CompressedChannels.Any(c=>c.PivotIndex==pivot.Ordinal);
                    bool eye=eyeAnimation.Channels.Any(c=>c.PivotIndex==pivot.Ordinal)||eyeAnimation.CompressedChannels.Any(c=>c.PivotIndex==pivot.Ordinal);
                    // 00987B90 assigns a pivot to the last of HANIM00/01/02
                    // that authors it; each source samples its retained time map.
                    var clip=eye?eyeAnimation:eyelid?blinkAnimation:mouthAnimation;
                    float from=eye?face.Eye:eyelid?face.Brow:facial is not null?face.Mouth:mouthFrom;
                    float to=eye?face.Eye:eyelid?face.Brow:facial is not null?face.Mouth:mouthTo;
                    float fraction=mouthBlend;
                    var keys=eye?face.EyeKeys:eyelid?face.BrowKeys:facial is not null?face.MouthKeys:null;
                    if(keys is {} endpoints){from=endpoints.From;to=endpoints.To;fraction=endpoints.Blend;}
                    if(!EnbW3dAnimationSampler.TrySamplePivot(clip,pivot.Ordinal,from,out var a,out var error)||
                        !EnbW3dAnimationSampler.TrySamplePivot(clip,pivot.Ordinal,to,out var b,out error))throw new InvalidDataException(error);
                    var rotation=EnbW3dAnimationSampler.InterpolateQuaternionRetail(a.Rotation,b.Rotation,fraction);
                    var translation=Vector3.Lerp(a.Translation,b.Translation,fraction);
                    // 00783470 replaces the head animation at frame zero via +9C;
                    // body +98 blending does not crossfade facial source offsets.
                    var offset=Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(translation);
                    transforms.Add(offset*pivot.LocalTransform*transforms[(int)pivot.ParentIndex]);
                }
                headPose=(headPose.Hierarchy,transforms.ToArray(),headPose.Visibility);
            }
            Add(head,headPose.Transforms,headScale,attachment);
            // 00780F30 scales the five head meshes only. Hair, goggles, beard,
            // and ears attach to its morphed pivots but retain authored vertices.
            foreach(string name in accessories)Add(Read(name),headPose.Transforms,Vector3.One,attachment);
            int reference = RetailBone(bodyPose.Hierarchy,"skel_reference");
            return new(parts.ToImmutable(),attachment.Translation){PreviewSphere=previewSphere,CreatePickMeshes=CreatePicks,
                PreviewBodySphere=previewBodySphere,PreviewHeadSphere=previewHeadSphere,Evaluate=AtTime,BlinkFrame=blink,FacialPose=face,HeadAnimationBound=headAnimationBound,HeadResource=head,HeadLoadDiagnostic=headDiagnostic,
                FacialResourceName=facialIni is null?null:facialName,CreateFacialMap=Face,EvaluateFacial=(time,from,weight,value)=>AtPose(time,from,weight,clamped:true,facial:value),
                EvaluateFacialWithHead=(time,from,weight,value,turn)=>AtPose(time,from,weight,clamped:true,headTurn:turn,facial:value),
                EvaluateUnboundHead=(time,from,weight,turn)=>AtPose(time,from,weight,clamped:true,headTurn:turn,headAnimationBound:false),
                EvaluateClamped=(time,from,weight)=>AtPose(time,from,weight,clamped:true),
                EvaluateClampedWithHead=(time,from,weight,turn)=>AtPose(time,from,weight,clamped:true,headTurn:turn),
                AnimationSoundKey=animation.HierarchyName+"."+animationName,AnimationFramesPerSecond=animation.FrameRate,
                FacialFramesPerSecond=mouthAnimation.FrameRate,
                SkeletonReference=bodyPose.Transforms[reference],CycleDurationSeconds=cycleFrames/(double)animation.FrameRate,
                BodySamples=samples,MissingAccessories=missing,EvaluateBlend=(time,from,weight)=>AtPose(time,from,weight),
                // 9C58C0 ->98A000 resolves case-insensitively, returning pivot0
                // on a miss. Sound callbacks consume the blended body pose.
                AnimationSoundPosition=name=>bodyPose.Transforms[bodyPose.Hierarchy.Pivots.FirstOrDefault(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase))?.Ordinal??0].Translation,
                EvaluateSpeech=(time,from,weight,a,b,t)=>AtPose(time,from,weight,a,b,t),SpeechFramesPerSecond=mouthAnimation.FrameRate/(double)speechDelay};

            EnbRetailMeshPick[] CreatePicks(EnbResolvedRenderCamera camera,Matrix4x4 world)
            {
                var result=new List<EnbRetailMeshPick>();
                var parents=pickParentBounds??=ComputePickParentBounds();
                EnbW3dWorldBounds TransformSphere(EnbW3dHlodBoundsOwner bounds,Matrix4x4 transform)
                {
                    var sphere=EnbCurrentRetailRenderBoundsStorage.Calculate(transform,new(bounds.LocalSphere.Center,bounds.LocalSphere.Radius),
                        bounds.LocalBox.Center,bounds.LocalBox.Extents).Sphere;
                    return new(new(sphere.X,sphere.Y,sphere.Z),sphere.W);
                }
                var bodyParent=TransformSphere(parents.Body,world);
                var headParent=TransformSphere(parents.Head,attachment*world);
                AddPicks(body,bodyPose.Transforms,bodyPose.Visibility,bodyScale,Matrix4x4.Identity,true);
                AddPicks(head,headPose.Transforms,headPose.Visibility,headScale,attachment);
                foreach(string name in accessories)AddPicks(Read(name),headPose.Transforms,headPose.Visibility,Vector3.One,attachment,accessoryKind:accessoryKinds[name]);
                // 77DA20 attaches the independent AVCOLLISION child to
                // skel_reference. 986370's recursive D0 cast includes it;
                // visible skinned body meshes alone are not the actor query.
                foreach(var mesh in Read("avcollision.w3d").Resource.Meshes)
                {
                    var key=(mesh,Vector3.One);
                    if(!pickMeshes.TryGetValue(key,out var ray)||!ray.IsCurrentGeometry)
                        pickMeshes[key]=ray=new(mesh,1,hidden:false,queryMask:1);
                    result.Add(new(ray,bodyPose.Transforms[reference]*world,camera){ParentSphere=bodyParent});
                }
                return result.ToArray();

                void AddPicks((EnbW3dModel Model,EnbW3dRenderResource Resource) asset,Matrix4x4[] transforms,
                    bool[] visibility,Vector3 scale,Matrix4x4 attach,bool bodyParts=false,string? accessoryKind=null)
                {
                    //9671B0 recursively visits +60/+64 children.98E020/98E050
                    //enumerate ALL LODs, not just the LOD used for rendering.
                    var hlod=asset.Model.HierarchicalLods.Single();
                    var members=hlod.Levels.SelectMany(l=>l.Members);
                    if(hlod.Aggregate is {} aggregate)members=members.Concat(aggregate.Members);
                    foreach(var member in members)
                    {
                        // 91D4B0 constructs NULL as type 0x16, not a mesh.
                        // Its D0 cast (462CD0) returns false; retain its bounds
                        // in the parent calculation, but it contributes no hit.
                        if(member.Name.Equals("NULL",StringComparison.OrdinalIgnoreCase))continue;
                        int pivot=accessoryKind is null?(int)member.ReferenceIndex:0;
                        if(!visibility[pivot])continue;
                        var mesh=asset.Resource.Meshes.SingleOrDefault(m=>member.Name.EndsWith("."+m.Name,StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"Avatar pick member '{member.Name}' absent in '{hlod.Name}' ({string.Join(",",asset.Resource.Meshes.Select(m=>m.Name))}).");
                        if(accessoryKind is not null && !Enumerable.Range(0,5).Any(i=>mesh.Name.Equals(accessoryKind+i,StringComparison.OrdinalIgnoreCase)))continue;
                        var vertexScale=bodyParts ? mesh.Name.StartsWith("SHIRT",StringComparison.OrdinalIgnoreCase) ||
                            mesh.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase) ? bodyMeshScale : Vector3.One : scale;
                        var key=(mesh,vertexScale);
                        if(!pickMeshes.TryGetValue(key,out var ray) || !ray.IsCurrentGeometry)
                            //98F090 applies each pivot's visibility via463150,
                            //replacing the mesh's initial hidden bit.
                            pickMeshes[key]=ray=new(mesh,1,hidden:false,queryMask:1,vertexScale:vertexScale);
                        bool last=hlod.Levels[^1].Members.Contains(member) || hlod.Aggregate?.Members.Contains(member)==true;
                        result.Add(new(ray,transforms[pivot]*attach*world,camera)
                            {ParentSphere=last?(bodyParts?bodyParent:headParent):null,AncestorSphere=last&&!bodyParts?bodyParent:null});
                    }
                }
            }

            void Add((EnbW3dModel Model,EnbW3dRenderResource Resource) asset,Matrix4x4[] transforms,Vector3 scale,Matrix4x4 attach,bool bodyParts=false)
            {
                var members=asset.Model.HierarchicalLods.Single().Levels[^1].Members;
                foreach(var member in members)
                {
                    var mesh=asset.Resource.Meshes.Single(m=>member.Name.EndsWith("."+m.Name,StringComparison.OrdinalIgnoreCase));
                    // 00780AF0 scales SHIRT0..4 and PANT0..4, not ARM0..4.
                    // 00780F30 scales the head object's members; attached hair,
                    // beard and other accessories retain their authored geometry.
                    var vertexScale=bodyParts ? mesh.Name.StartsWith("SHIRT",StringComparison.OrdinalIgnoreCase) ||
                        mesh.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase) ? bodyMeshScale : Vector3.One : scale;
                    // MeshModel +0x160 scales positions but does not rewrite its
                    // authored normals (0092D460).
                    var scaled=(mesh).Copy().SetPositions(mesh.Positions.Select(v=>v*vertexScale).ToArray());
                    EnbW3dDeformedGeometry geometry;
                    if(mesh.BoneIndices.Count>0)
                    {
                        if(!EnbW3dSkinDeformer.TryDeform(scaled,new(transforms,Enumerable.Repeat(true,transforms.Length).ToImmutableArray(),null,null),out geometry,out var error))throw new InvalidDataException(error);
                    }
                    else
                    {
                        var transform=transforms[(int)member.ReferenceIndex];
                        geometry=new(scaled.Positions.Select(v=>Vector3.Transform(v,transform)).ToImmutableArray(),scaled.Normals.Select(n=>Vector3.TransformNormal(n,transform)).ToImmutableArray());
                    }
                    geometry=new(geometry.Positions.Select(v=>Vector3.Transform(v,attach)).ToImmutableArray(),geometry.Normals.Select(n=>Vector3.TransformNormal(n,attach)).ToImmutableArray());
                    parts.Add(new(asset.Resource,mesh,geometry)
                    {
                        CollisionMesh=(EnbRetailDazzleCollision.ConfiguredMeshMask(mesh)&4)!=0 ? scaled : null,
                        CollisionTransform=transforms[(int)member.ReferenceIndex]*attach,
                    });
                }
            }
        }
        (EnbW3dHlodBoundsOwner Body,EnbW3dHlodBoundsOwner Head) ComputePickParentBounds()
        {
            //77F750 clones the decorated head HLOD and attaches it to the
            //body's K_HEAD.98F1D0 uses base-pose local child boxes, then
            //98D0B0/9421B0 supply the parent's camera-query sphere.
            EnbW3dRenderMesh ScaleBounds(EnbW3dRenderMesh mesh,Vector3 scale)=>(mesh).Copy().SetBoundingBoxMin(mesh.BoundingBoxMin*scale).SetBoundingBoxMax(mesh.BoundingBoxMax*scale);
            var headHlod=head.Resource.HierarchicalLods.Single();
            var levels=headHlod.Levels.Select(l=>l.Members.ToList()).ToArray();
            var meshes=head.Resource.Meshes.Select(m=>ScaleBounds(m,headScale)).ToList();
            foreach(string name in accessories)
            {
                var accessory=Read(name).Resource;
                var source=accessory.HierarchicalLods.Single();
                //789D80 ->98F920: named0..4 meshes retain the source LOD
                //ordinal but are inserted on pivot0 of the head hierarchy.
                for(int ordinal=0;ordinal<5;ordinal++)
                {
                    var mesh=accessory.Meshes.FirstOrDefault(m=>m.Name.Equals(accessoryKinds[name]+ordinal,StringComparison.OrdinalIgnoreCase));
                    if(mesh is null)continue;
                    int level=source.Levels.ToList().FindIndex(l=>l.Members.Any(m=>m.Name.EndsWith("."+mesh.Name,StringComparison.OrdinalIgnoreCase)));
                    if(level<0)continue;
                    levels[level].Add(new(0,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(mesh)));meshes.Add(mesh);
                }
            }
            headHlod=headHlod with {Levels=headHlod.Levels.Select((l,i)=>l with {Members=levels[i]}).ToArray()};
            var headResource=head.Resource.Copy().SetMeshes(meshes).SetHierarchies([MorphedHierarchy(head.Model,headScale)]).SetHierarchicalLods([headHlod]);
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(headResource,headHlod,Matrix4x4.Identity,null,0,out var headBounds,out var error))
                throw new InvalidDataException(error);
            var bodyHlod=body.Resource.HierarchicalLods.Single();
            var bodyHierarchy=MorphedHierarchy(body.Model,bodyScale);
            var headMember=new EnbW3dRenderHlodMember((uint)EnbCurrentRetailAggregateBoneLookup.Resolve(bodyHierarchy,"K_HEAD"),headHlod.Name);
            var collisionMeshes=Read("avcollision.w3d").Resource.Meshes;
            var collisionPivot=(uint)EnbCurrentRetailAggregateBoneLookup.Resolve(bodyHierarchy,"skel_reference");
            bodyHlod=bodyHlod with {Aggregate=new(bodyHlod.Aggregate?.RawValue??0,[..bodyHlod.Aggregate?.Members??[],headMember,
                ..collisionMeshes.Select(m=>new EnbW3dRenderHlodMember(collisionPivot,EnbW3dRenderObjectNameSemantics.RegisteredMeshName(m)))])};
            var headChild=new EnbW3dRenderCompositeChild(headHlod.Name,0x19,
                new(new(headBounds.LocalSphere.Center,headBounds.LocalSphere.Radius),headBounds.LocalBox.Center,headBounds.LocalBox.Extents),0);
            var bodyResource=body.Resource.Copy().SetHierarchies([bodyHierarchy]).SetHierarchicalLods([bodyHlod]).SetCompositeRenderChildren([..body.Resource.CompositeRenderChildren,headChild]).SetMeshes([..body.Resource.Meshes.Select(m=>ScaleBounds(m,m.Name.StartsWith("SHIRT",StringComparison.OrdinalIgnoreCase)||
                    m.Name.StartsWith("PANT",StringComparison.OrdinalIgnoreCase)?bodyMeshScale:Vector3.One)),..collisionMeshes]);
            if(!EnbW3dHlodCompositeBoundsCompiler.TryCompute(bodyResource,bodyHlod,Matrix4x4.Identity,null,0,out var bodyBounds,out error))
                throw new InvalidDataException(error);
            return(bodyBounds,headBounds);
        }
        (EnbW3dWorldBounds Actor,EnbW3dWorldBounds Body,EnbW3dWorldBounds Head) ComputePreviewSphere()
        {
            // 0098F1D0 builds ordinary HLOD bounds from the base hierarchy pose;
            // 009865F0 then merges the actor's attached child spheres in order.
            var bodyPose=Pose(body.Model,bodyScale);
            var headPose=Pose(head.Model,headScale);
            Matrix4x4 headAttachment=bodyPose.Transforms[RetailBone(bodyPose.Hierarchy,"K_HEAD")];
            var bodySphere=ChildSphere(body,bodyPose.Transforms,bodyScale,Matrix4x4.Identity,bodyParts:true);
            var headSphere=ChildSphere(head,headPose.Transforms,headScale,headAttachment);
            var sphere=Merge(bodySphere,headSphere);
            foreach(string name in accessories)
                sphere=Merge(sphere,ChildSphere(Read(name),headPose.Transforms,Vector3.One,headAttachment));
            return(sphere,bodySphere,headSphere);
        }
        EnbW3dWorldBounds ChildSphere((EnbW3dModel Model,EnbW3dRenderResource Resource) asset,
            Matrix4x4[] transforms,Vector3 scale,Matrix4x4 attach,bool bodyParts=false)
        {
            var hlod=asset.Model.HierarchicalLods.Single();
            var members=hlod.Levels.SelectMany(level=>level.Members).ToArray();
            var special=hlod.Levels[^1].Members.LastOrDefault(member=>
                member.Name.Split('.').Last().Equals("BOUNDINGBOX",StringComparison.OrdinalIgnoreCase));
            if(special.Name is not null)members=[special];
            else if(hlod.Aggregate is not null)members=[..members,..hlod.Aggregate.Members];
            var seen=new HashSet<(string,uint)>();
            bool any=false;
            Vector3 minimum=default,maximum=default;
            foreach(var member in members)
            {
                if(!seen.Add((member.Name.ToUpperInvariant(),member.ReferenceIndex)))continue;
                if(member.ReferenceIndex>=(uint)transforms.Length)
                    throw new InvalidDataException($"Portrait HLOD member '{member.Name}' has an invalid pivot index.");
                if(!EnbW3dHlodCompositeBoundsCompiler.TryGetMemberBoundingBox(asset.Resource,member.Name,
                    out var boxMin,out var boxMax,out var diagnostic))
                    throw new InvalidDataException(diagnostic??$"Portrait HLOD member '{member.Name}' has no bounds.");
                Matrix4x4 memberTransform=transforms[(int)member.ReferenceIndex];
                for(int x=0;x<2;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)
                {
                    var memberScale=bodyParts ? member.Name.Contains(".SHIRT",StringComparison.OrdinalIgnoreCase) ||
                        member.Name.Contains(".PANT",StringComparison.OrdinalIgnoreCase) ? bodyMeshScale : Vector3.One : scale;
                    var corner=new Vector3(x==0?boxMin.X:boxMax.X,y==0?boxMin.Y:boxMax.Y,z==0?boxMin.Z:boxMax.Z)*memberScale;
                    var point=Vector3.Transform(corner,memberTransform);
                    if(!any){minimum=maximum=point;any=true;}
                    else{minimum=Vector3.Min(minimum,point);maximum=Vector3.Max(maximum,point);}
                }
            }
            if(!any)throw new InvalidDataException($"Portrait HLOD '{hlod.Name}' has no bound members.");
            var center=(minimum+maximum)*.5f;
            return new(Vector3.Transform(center,attach),Vector3.Distance(minimum,maximum)*.5f);
        }
        static EnbW3dWorldBounds Merge(EnbW3dWorldBounds a,EnbW3dWorldBounds b)
        {
            // Current retail actor +0x100 at 009865F0.
            float distance=Vector3.Distance(a.Center,b.Center);
            if(distance==0)return a.Radius>=b.Radius?a:b;
            float radius=(a.Radius+b.Radius+distance)*.5f;
            if(a.Radius>=radius)return a;
            if(b.Radius>=radius)return b;
            return new(a.Center+(b.Center-a.Center)*((radius-a.Radius)/distance),radius);
        }
        (EnbW3dModel Model,EnbW3dRenderResource Resource) Read(string name)
        {
            if(resources.TryGetValue(name,out var cached))return cached;
            if(assets.RetailMixResources?.TryReadResource(name,out var bytes,out var location)!=true||!EnbW3dModel.TryDecode(bytes,out var model)||
                !EnbAuthoredUiAsset.TryCompile(assets.RetailMixResources,location,model,Path.GetFileNameWithoutExtension(name),out var resource))
                throw new InvalidDataException("Portrait asset unavailable: "+name);
            return resources[name]=(model,resource);
        }
        EnbW3dRenderHierarchy MorphedHierarchy(EnbW3dModel model,Vector3 scale)
        {
            string name=model.HierarchicalLods.Single().HierarchyName;
            var skeleton=Read(name+".w3d");
            var hierarchy=skeleton.Resource.Hierarchies.Single();
            if(ReferenceEquals(model,body.Model)||ReferenceEquals(model,head.Model))
            {
                // 00780AF0 and 00780F30 call 0098A2D0 on cloned body/head
                // rest hierarchies before animation. Scaling evaluated world
                // pivots instead changes the animated head's side silhouette.
                if(!EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,out var rest,out var restError))
                    throw new InvalidDataException(restError);
                var pivots=new EnbW3dRenderPivot[hierarchy.Pivots.Count];
                pivots[0]=hierarchy.Pivots[0];
                for(int i=1;i<pivots.Length;i++)
                {
                    var pivot=hierarchy.Pivots[i];
                    int parent=(int)pivot.ParentIndex;
                    bool armEnd=ReferenceEquals(model,body.Model) && pivot.Name is "RIGHTFOREARM" or "RIGHTHAND" or "LEFTFOREARM" or "LEFTHAND"
                        or "RIGHTINDEX" or "RIGHTFINGERS" or "RIGHTTHUMB"
                        or "LEFTINDEX" or "LEFTFINGERS" or "LEFTTHUMB";
                    var pivotScale=armEnd?new Vector3(scale.X,scale.Z,scale.Z):scale;
                    var restOffset=(rest[i].Translation-rest[parent].Translation)*pivotScale;
                    if(!Matrix4x4.Invert(rest[parent],out var inverseParent))
                        throw new InvalidDataException($"Avatar hierarchy '{hierarchy.Name}' pivot {parent} is singular.");
                    var local=pivot.LocalTransform;
                    local.Translation=Vector3.TransformNormal(restOffset,inverseParent);
                    pivots[i]=pivot with {Translation=local.Translation,LocalTransform=local};
                }
                hierarchy=hierarchy.Copy().SetPivots(pivots);
            }
            return hierarchy;
        }
        (EnbW3dHierarchy Hierarchy,Matrix4x4[] Transforms,bool[] Visibility) Pose(EnbW3dModel model,Vector3 scale,EnbW3dRenderHierarchyAnimation? animation=null,float frame=0,float blend=1,ImmutableArray<EnbW3dResolvedWeightedAnimationSample> samples=default,Matrix4x4? headTurn=null)
        {
            string name=model.HierarchicalLods.Single().HierarchyName;
            var skeleton=Read(name+".w3d");
            var hierarchy=MorphedHierarchy(model,scale);
            if(animation is not null)
            {
                // Retail evaluates channels against the actor's loaded hierarchy by
                // pivot ordinal (009C3220/009C35B0), not by loading <animation.HierarchyName>.w3d.
                // This happens only after resolving the requested animation identity.
                // A gender animation name and an NPC mesh skeleton may differ, but
                // an unrelated HAnim in a matching filename must not be substituted.
                // 00989670 applies only pivots below both the tree and HAnim
                // counts; channels beyond the actor hierarchy are not applied.
                animation=animation.Copy().SetHierarchyName(hierarchy.Name);
            }
            EnbW3dHierarchyPoseOwner pose;
            string? error;
            IReadOnlyDictionary<int,Matrix4x4>? overrides=headTurn is {} turn
                ? new Dictionary<int,Matrix4x4>{{EnbCurrentRetailAggregateBoneLookup.Resolve(hierarchy,"K_HEAD"),turn}}
                : null;
            bool accepted = !samples.IsDefaultOrEmpty
                ? EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,
                    samples.Select(s=>s with {Animation=s.Animation.Copy().SetHierarchyName(hierarchy.Name)}).ToArray(),overrides,out pose,out error)
                : animation is not null && blend < 1
                ? EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,
                    new EnbW3dResolvedWeightedAnimationSample[] { new(animation,animation.NumFrames-1,1-blend),new(animation,frame,blend) },overrides,out pose,out error)
                : EnbW3dHierarchyPose.TryEvaluate(hierarchy,Matrix4x4.Identity,animation,frame,overrides,out pose,out error);
            if(!accepted)throw new InvalidDataException(error);
            return(skeleton.Model.Hierarchies.Single(),pose.WorldTransforms,pose.Visibility);
        }
        Vector3 Scale(ImmutableArray<uint> raw,bool head)
        {
            if(raw.Length!=5)throw new InvalidDataException("Portrait requires five appearance weights.");
            var weights=raw.Select(BitConverter.UInt32BitsToSingle).ToArray();
            // 0077B870 only asserts the [0,max] range, sums in order and divides
            // when the sum is positive; otherwise the raw weights are kept.
            float sum=0;
            foreach(float w in weights)sum+=w;
            if(sum>0)for(int i=0;i<weights.Length;i++)weights[i]/=sum;
            string[] keys=head?["normal","short_narrow","short_wide","long_wide","long_narrow"]:["normal","short_skinny","short_fat","tall_fat","tall_skinny"];
            Vector3 result=Vector3.Zero;
            for(int i=0;i<5;i++)
            {
                var value=Vector3.One;
                if(i>0&&definitions.TryGetSection(section,out var values)&&values.TryGetValue(keys[i],out var text))
                {
                    var xyz=text.Split(',').Select(s=>float.Parse(s,CultureInfo.InvariantCulture)).ToArray();
                    value=head?new(1,xyz[0],xyz[1]):new(xyz[0],xyz[1],xyz[2]);
                }
                // 0077B870 normalizes the copied AvatarData weights and then
                // calls 00781410/00781570 to re-morph. The final 00780AF0/
                // 00780F30 weighted sum therefore consumes normalized weights,
                // even when 0067BF20 supplies preview weights totaling 1.25.
                result+=value*weights[i];
            }
            return result;
        }
    }
}

public sealed class AvatarAnimationUnavailableException(string name) : Exception("No matching actor HAnim identity for " + name);
