using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly ConditionalWeakTable<EnbResolvedRslStateResource,LiveCpuRoot> _rslParticleTemplates=new();
    private readonly Dictionary<string,LiveCpuRoot> _configuredParticleVariants=new(StringComparer.OrdinalIgnoreCase);

    private LiveCpuRoot RetainRslParticleTemplate(EnbResolvedRslStateResource state,LiveCpuRoot root)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainRslParticleTemplate(state,root);
        if(!HasParticleConstruction(root))return root;
        //45E5F0 (precached) or45EF80 (lazy) constructs the template once.
        //45EDC0/45E9F0 may clone or retain it; neither draws new property values.
        return _rslParticleTemplates.GetValue(state,_=>root.ParticleConstructionPrepared
            ?CloneConfiguredParticleTree(root):ConstructParticleFactoryTree(root));
    }

    private LiveCpuRoot RetainParticleFactoryTree(string name,LiveCpuRoot root,float scale,Vector3 hsv)
        =>RetainParticleFactoryTreeCore(name,root,scale,hsv,false);

    private LiveCpuRoot RetainParticleFactoryTreeCore(string name,LiveCpuRoot root,float scale,Vector3 hsv,bool specializedExhaust)
    {
        if(!HasParticleConstruction(root))return root;
        bool changesHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
        bool configured=changesHsv||EnbCurrentRetailPrototypeVariantName.ChangesScale(scale);
        string key=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        //45C990 always calls45F280 after copying each state. The latter
        //replaces +30 through48B220, even for unchanged scale/HSV.
        if(configured && _configuredParticleVariants.TryGetValue(key,out var cached))
        {
            var copy=CloneConfiguredParticleTree(cached);
            return (specializedExhaust?copy:GenericParticleFactoryTree(copy)) with {RslFactoryStateId=root.RslFactoryStateId};
        }
        var instance=root.ParticleConstructionPrepared?CloneConfiguredParticleTree(root):ConstructParticleFactoryTree(root);
        if(!configured && root.ParticleConstructionPrepared)
            return specializedExhaust?instance:GenericParticleFactoryTree(instance);
        if(changesHsv)instance=ConfigureParticleFactoryTree(instance,hsv);
        if(EnbCurrentRetailPrototypeVariantName.ChangesScale(scale))instance=ScaleSpecializedParticleBindings(instance,scale);
        instance=instance with {ConfiguredFactoryInputs=HasConfigurableMeshTree(root) && root.ConfiguredFactoryInputs is not null?root.ConfiguredFactoryInputs:
            ((root.ConfiguredFactoryInputs?.Scale??1)*(EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)?scale:1),
            changesHsv?hsv:root.ConfiguredFactoryInputs?.Hsv??Vector3.Zero)};
        //48B220 registers a retained prototype, then returns its Clone even
        //on the first acquisition. The live owner must not be that prototype.
        if(configured)
        {
            //Mesh configuration precedes particle HSV. Capture the effect
            //projection only now, with the final retained emitter tables.
            if(_configuredDirectMeshVariants.TryGetValue(key,out var meshVariant) && meshVariant.UnconfiguredCompositeRoot is {} original)
                RetainConfiguredEffectAssembly(key,original,instance,meshVariant.Model,meshVariant.Scale,meshVariant.Hsv,particlesConfigured:true);
            _configuredParticleVariants.Add(key,CloneConfiguredParticleTree(instance));
            _prototypeResources.RegisterConfiguredPrototypeName(key);
        }
        return specializedExhaust?instance:GenericParticleFactoryTree(instance);
    }

    private static LiveCpuRoot ScaleSpecializedParticleBindings(LiveCpuRoot root,float scale)
    {
        //0098EF60 forwards +164 to every HLOD level and permanent child.
        if(root.Hlod is null)return root;
        return root with
        {
            HmodelChildren=root.HmodelChildren.Select(c=>c with {Root=ScaleSpecializedParticleBindings(c.Root,scale)}).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(a=>a with
            {FactoryParticleScale=a.Exhaust is null?a.FactoryParticleScale:a.FactoryParticleScale*scale,
                Root=a.Root is {} child?ScaleSpecializedParticleBindings(child,scale):null}).ToArray()
        };
    }
}
