using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<EffectHlodKey,EnbCurrentRetailRObjEffectAsset> _effectHlodSceneAssets=[];
    private readonly List<Action> _effectRenderSceneChanges=[];
    private readonly HashSet<EffectHlodKey> _effectCulledRoots=[];

    private bool AdmitEffectBounds(EffectHlodKey key,EnbW3dWorldBounds bounds,EnbResolvedRenderCamera camera)
    {
        bool admitted=!FrustumCullingEnabled||EnbCurrentRetailSceneCulling.SphereIntersectsFrustum(camera,bounds);
        if(!admitted)_effectCulledRoots.Add(key);
        return admitted;
    }

    private bool AdmitEffectSimpleRoot(EffectHlodKey key,EnbCurrentRetailRObjEffectAsset asset,
        EnbW3dRenderResource? retainedResource,Matrix4x4 world,float scale,float sourceScale,
        bool configuredMesh,EnbResolvedRenderCamera camera)
    {
        EnbW3dWorldBounds? bounds=null;
        if(asset.ObjectLine is {} line)bounds=line.Bounds(world,scale);
        //941540 is a fixed .1 sphere; NULL's inherited scale slot does nothing.
        else if(asset.IsNullObject)bounds=new(world.Translation,.1f);
        else if(asset.StandaloneBox is {} box)bounds=new(Vector3.Transform(box.Center,world),box.Extents.Length());
        else if(asset.SpecialMaterial is {} special)bounds=new(Vector3.Transform(special.SphereCenter,world),special.SphereRadius);
        else if(asset.StandaloneDazzle is {} dazzle)
            //96BEC0 reads definition radius and retained instance+E8.96BAB0
            //multiplies E8 separately for factory scale and source radius.
            bounds=new(world.Translation,dazzle.DazzleDefinition!.Radius*(dazzle.DazzleInstanceScale*
                (dazzle.HasConfiguredDazzleScale?sourceScale:scale)));
        else if(retainedResource is {HierarchicalLods.Count:0,HierarchyModels.Count:0,Meshes.Count:1} resource)
        {
            var mesh=resource.Meshes[0];
            //940CE0/940EB0 query the retained model geometry. A configured
            //clone already contains its factory scale; only source scale remains.
            float remaining=configuredMesh?sourceScale:scale/mesh.CurrentRetailLoadedGeometryScale;
            var center=mesh.CurrentRetailLoadedGeometry?.Center??mesh.BoundingSphereCenter;
            float radius=mesh.CurrentRetailLoadedGeometry?.Radius??mesh.BoundingRadius;
            bounds=new(Vector3.Transform(center*remaining,world),radius*remaining);
        }
        return bounds is not {} sphere||AdmitEffectBounds(key,sphere,camera);
    }

    private bool AdmitEffectComposite(EffectHlodKey key,EnbCurrentRetailRObjEffectAsset asset,
        Matrix4x4 world,float scale,float hierarchyScale,EnbResolvedRenderCamera camera,
        uint start,uint now,EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed,EnbW3dHlodQueryOwner queryOwner)
    {
        var resource=asset.Resource;
        var hlod=resource?.HierarchicalLods.SingleOrDefault();
        if(hlod is null && resource?.HierarchyModels.SingleOrDefault() is {} model)
            hlod=EnbW3dHmodelConversion.Create(model,_=>true);
        if(hlod is null)return true;
        var clip=asset.Animation;float frame=0;
        if(asset.Sequence is not null)
        {
            if(observed is not {} sample)throw new InvalidDataException("Sequence bounds require an animation observation");
            clip=sample.BoundAnimation;frame=sample.Frame;
        }
        else if(clip is not null && !EnbCurrentRetailRslAnimationRuntime.TryComputeFrame(clip,asset.AnimationFlags!,start,now,out frame,out var why))
            throw new InvalidDataException(why);
        var post=queryOwner.PreparePost(hlod.HierarchyName,world,scale,asset.ResolveHierarchyScale(scale,hierarchyScale));
        var queried=asset.RetainedBoundsResource??resource!;
        if(!queryOwner.Constructor.TryCompute(queried,queried.HierarchicalLods.SingleOrDefault()??hlod,
            world,queryOwner.Samples(clip,frame),post,out var bounds,out var error))
            throw new InvalidDataException(error);
        //965F40 sets the root's frustum bit before +170/+104.9661C0
        //uses that same admission for Render; callbacks remain independent.
        return AdmitEffectBounds(key,bounds.WorldSphere,camera);
    }

    private void QueueEffectRenderLevel(EffectHlodKey key,EnbW3dHlodSelection selection)
    {
        //9661C0 walks +2C callbacks before invoking +24 Render.98DD50's
        //forced child+120 belongs to that later traversal, not preparation.
        _effectHlodSelections[key]=selection;
        _effectRenderSceneChanges.Add(()=>SetEffectHlodSceneLevel(key,selection.LevelOrdinal));
    }

    private (int Blocked,string? Failure) ApplyEffectRenderSceneChanges()
    {
        int blocked=0;string? failure=null;
        foreach(var change in _effectRenderSceneChanges)
            try{change();}
            catch(Exception error){blocked++;failure??=error.Message;}
        _effectRenderSceneChanges.Clear();
        return (blocked,failure);
    }

    private int EffectSceneLevel(EffectHlodKey key,EnbCurrentRetailRObjEffectAsset asset)
        =>_effectHlodLevels.GetValueOrDefault(key,asset.Resource?.HierarchicalLods.SingleOrDefault() is {} hlod
            ?EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod):0);

    private void PrepareEffectCompositeScene(EnbCurrentRetailEffectOwner node,IEnbRenderObject owner,
        EnbCurrentRetailRObjEffectAsset asset,Matrix4x4 world,float radius,uint clock,
        EnbCurrentRetailRslAnimationSequenceRuntime.Sample? observed,float hierarchyScale)
    {
        var key=EffectHlodKey.For(node,owner);
        _effectHlodSceneAssets[key]=asset;
        if(_shipExhaustOwner is not null)return;
        PrepareEffectCompositeParticles(node,owner,asset,-1,world,radius,null,clock,observed,
            transformsOnly:true,hierarchySourceScale:hierarchyScale,constructForScene:true);
        for(int i=0;i<asset.ModelAttachments.Count;i++)
        {
            var child=asset.ModelAttachments[i];
            var pose=child.Pose(asset,world,node.ActualStartTick,clock,radius,observed);
            PrepareEffectCompositeParticles(node,owner,child.Asset,i,pose.World,radius,null,clock,
                transformsOnly:true,constructForScene:true);
        }
        SynchronizeEffectCompositeScene(key,asset);
    }

    private void SetEffectHlodSceneLevel(EffectHlodKey key,int level)
    {
        if(!_effectHlodSceneAssets.TryGetValue(key with {Child=-1},out var root))return;
        var asset=key.Child<0?root:root.ModelAttachments[key.Child].Asset;
        if(asset.Resource?.HierarchicalLods.SingleOrDefault() is not {} hlod)return;
        int previous=EffectSceneLevel(key,asset);
        _effectHlodLevels[key]=level;
        _effectHlodSelections[key]=new(hlod.Name,level);
        if(previous!=level)SynchronizeEffectCompositeScene(key with {Child=-1},root);
    }

    private void SynchronizeEffectCompositeScene(EffectHlodKey key,EnbCurrentRetailRObjEffectAsset root)
    {
        if(_shipExhaustOwner is not null)return;
        //98DF20/98ED30 recurse through ordinary members first and permanent
        //members second. Preserve that order across direct and nested emitters.
        var selected=new List<CompositeParticle>();
        void Visit(int index,EnbCurrentRetailRObjEffectAsset asset)
        {
            int level=EffectSceneLevel(key with {Child=index},asset);
            var members=new List<(int Permanent,int Ordinal,CompositeParticle? Particle,int Child)>();
            if(_effectCompositeParticles.TryGetValue((key.Effect,key.Generation,key.Sequence,index),out var group))
                foreach(var particle in group.Particles)
                    if(particle.State is {Destroyed:false} &&
                        (particle.Definition.LevelOrdinal<0||particle.Definition.LevelOrdinal==level))
                        members.Add((particle.Definition.LevelOrdinal<0?1:0,particle.Definition.MemberOrdinal,particle,-1));
            for(int childIndex=0;childIndex<root.ModelAttachments.Count;childIndex++)
            {
                var child=root.ModelAttachments[childIndex];
                if(child.ParentModelIndex!=index || child.LevelOrdinal is int childLevel&&childLevel!=level)continue;
                int ordinal;
                if(child.SceneMemberOrdinal is int authored)ordinal=authored;
                else
                {
                    var hlod=asset.Resource!.HierarchicalLods.Single();
                    string name=child.BoundsMemberName??$"<0600:{hlod.Name}:{(child.BoundsOrdinal<0?childIndex:child.BoundsOrdinal)}:{child.Name}>";
                    var storage=child.LevelOrdinal is int ordinary?hlod.Levels[ordinary].Members:hlod.Aggregate?.Members??[];
                    ordinal=Enumerable.Range(0,storage.Count).Single(i=>storage[i].Name==name);
                }
                members.Add((child.LevelOrdinal is null?1:0,ordinal,null,childIndex));
            }
            foreach(var member in members.OrderBy(m=>m.Permanent).ThenBy(m=>m.Ordinal))
                if(member.Particle is {} particle)selected.Add(particle);
                else Visit(member.Child,root.ModelAttachments[member.Child].Asset);
        }
        Visit(-1,root);
        var desired=selected.ToHashSet();
        foreach(var (groupKey,group) in _effectCompositeParticles)
            if(groupKey.Effect==key.Effect&&groupKey.Generation==key.Generation&&groupKey.Sequence==key.Sequence&&groupKey.Model!=-2)
                foreach(var particle in group.Particles)
                    if(!desired.Contains(particle))particle.State?.SetSceneAttached(false);
        foreach(var particle in selected)particle.State!.SetSceneAttached(true);
    }
}
