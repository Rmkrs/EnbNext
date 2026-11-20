using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong Identity,ulong Generation,int Base,LiveCpuRoot Source),LiveCpuRoot> _influenceParticleRoots=[];

    private LiveCpuRoot RetainInfluenceParticleSource(IEnbRenderObject obj,LiveCpuRoot source)
    {
        if(source.ParticleConstructionPrepared)return source;
        var key=(obj.RenderIdentity,obj.RenderObjectGeneration,obj.BaseAsset,source);
        if(_influenceParticleRoots.TryGetValue(key,out var retained))return retained;
        if(!HasParticleConstruction(source))return source;
        var hsv=new Vector3(obj.HsvH,obj.HsvS,obj.HsvV);
        //48B220 registers a configured prototype;48B050 creates by Clone.
        //The namespace is shared with45F280's RSL factory, not per helper.
        retained=RetainParticleFactoryTree(source.FactoryLookupName??source.RootRenderObjectName,source,obj.Scale,hsv);
        _influenceParticleRoots.Add(key,retained);
        return retained;
    }

    private static bool HasParticleConstruction(LiveCpuRoot root)=>root.RetainedEmitter is not null ||
            root.DiscardedParticleConstructions.Count!=0 || root.CollectionChildren.Any(HasParticleConstruction) ||
            root.HmodelChildren.Any(c=>HasParticleConstruction(c.Root)) ||
            root.AggregateAttachments.Any(a=>a.Exhaust is not null || a.Root is {} child&&HasParticleConstruction(child));

    private LiveCpuRoot ConstructParticleFactoryTree(LiveCpuRoot root)
    {
        if(root.ParticleConstructionPrepared)return CloneConfiguredParticleTree(root);
        var random=root.RetainedEmitter is {} emitter?new EnbRetailParticleRandomOwner(emitter.Definition):null;
        var collections=root.CollectionChildren.Select(ConstructParticleFactoryTree).ToArray();
        var children=root.HmodelChildren.Select(child=>child with {Root=ConstructParticleFactoryTree(child.Root)}).ToArray();
        var attachments=root.AggregateAttachments.Select(attachment=>
        {
            if(attachment.Root is {} child)return attachment with {Root=ConstructParticleFactoryTree(child)};
            if(attachment.Exhaust is {} exhaust)
            {
                //Keep the class15 child and its original specialized binding.
                //The factory is shared; the scene consumer chooses its owner.
                var empty=new EnbW3dRenderResource(root.Resource.Key,root.Resource.LogicalFileName,[],[],[],[],[]);
                var particle=new LiveCpuRoot(root.OverbrightOff,empty,null,null,attachment.SubobjectName,attachment.SubobjectName,null,null,[])
                    {RetainedEmitter=exhaust};
                return attachment with {Root=ConstructParticleFactoryTree(particle)};
            }
            return attachment;
        }).ToArray();
        foreach(var discarded in root.DiscardedParticleConstructions)_=new EnbRetailParticleRandomOwner(discarded);
        return root with {FactoryParticleRandom=random,CollectionChildren=collections,HmodelChildren=children,
            AggregateAttachments=attachments,ParticleConstructionPrepared=true};
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiveCpuRoot,LiveCpuRoot> _genericParticleFactoryTrees=new();
    private LiveCpuRoot GenericParticleFactoryTree(LiveCpuRoot root)
    {
        if(!HasSpecializedExhaustFactory(root))return root;
        return _genericParticleFactoryTrees.GetValue(root,source=>source with
        {
            CollectionChildren=source.CollectionChildren.Select(GenericParticleFactoryTree).ToArray(),
            HmodelChildren=source.HmodelChildren.Select(c=>c with {Root=GenericParticleFactoryTree(c.Root)}).ToArray(),
            AggregateAttachments=source.AggregateAttachments.Select(a=>a.Root is {} child
                ?a with {Root=GenericParticleFactoryTree(child),Exhaust=null,IsRetailNoOp=a.Exhaust is null&&a.IsRetailNoOp}:a).ToArray()
        });
    }

    private LiveCpuRoot ConfigureParticleFactoryTree(LiveCpuRoot root,Vector3 shift)
    {
        //48D710 traverses HLOD+60/+64 after every constructor has run.
        //Class23 Collections are not in its switch.
        if(root.RetainedEmitter is {} emitter && root.FactoryParticleRandom is {} random)
        {
            var assets=_retailAssetsAccessor()??throw new InvalidDataException("Particle factory HSV assets unavailable");
            var shifted=new EnbCurrentRetailRObjParticleAsset(emitter.Key,emitter.Texture,emitter.Definition)
                {RuntimeTextureName=emitter.RuntimeTextureName,RetainedTexture=emitter.RetainedTexture};
            shifted=shifted.ApplyHsv(assets,shift);
            random.SetColorRandom(shifted.Definition.Color.Random);
            return root with {RetainedEmitter=emitter with {Definition=shifted.Definition,Texture=shifted.Texture,
                RuntimeTextureName=shifted.RuntimeTextureName,RetainedTexture=shifted.RetainedTexture}};
        }
        if(root.Hlod is null)return root;
        return root with {HmodelChildren=root.HmodelChildren.Select(child=>child with {Root=ConfigureParticleFactoryTree(child.Root,shift)}).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(attachment=>attachment.Root is {} child
                ?attachment with {Root=ConfigureParticleFactoryTree(child,shift)}:attachment).ToArray()};
    }
}
