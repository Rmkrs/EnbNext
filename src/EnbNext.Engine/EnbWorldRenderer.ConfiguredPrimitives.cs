using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
using EnbNext.Presentation;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<string,LiveCpuRoot> _configuredPrimitiveVariants=new(StringComparer.OrdinalIgnoreCase);

    //006EA830 uses the same 006F51D0 BASE factory as ordinary objects.
    //A generated prototype is a retained object; it has no decoded FILE source.
    private bool TryAcquireConfiguredEffectPrimitive(EnbRetailAssetBundle assets,int id,
        (float Scale,Vector3 Hsv) inputs,out EnbCurrentRetailRObjEffectAsset asset)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryAcquireConfiguredEffectPrimitive(assets,id,inputs,out asset);
        asset=null!;
        if(assets.PresentationCatalog?.ResolveBaseAsset(id) is not
            {Kind:EnbBaseAssetDefinitionKind.FileName,Name:{} name})return false;
        bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Hsv);
        string variant=EnbCurrentRetailPrototypeVariantName.Create(name,inputs.Scale,inputs.Hsv);
        bool final=configured&&_configuredPrimitiveVariants.ContainsKey(variant);
        if(!final&&!_configuredPrimitiveVariants.ContainsKey(name))return false;
        if(!TryAcquireConfiguredPrototype(id,final?variant:name,-1,assets,out var root))return false;
        if(!final)root=RetainConfiguredPrimitive(name,root,inputs.Scale,inputs.Hsv,assets);
        asset=CreateConfiguredPrimitiveEffectAsset(root);
        return true;
    }

    private static EnbCurrentRetailRObjEffectAsset CreateConfiguredPrimitiveEffectAsset(LiveCpuRoot root)=>
        new(IsDirectDazzle(root)||IsNoOpConfiguredLeaf(root)?root.Resource:null,null,root.OverbrightOff,null,null,
            Rings:root.Ring is {} ring?new(null,null,null,[ring],null):null,
            Spheres:root.Sphere is {} sphere?new(null,null,null,[sphere],[],null):null)
            {FactoryObjectName=root.FactoryLookupName,IsPreconfiguredFactoryObject=true,SpecialMaterial=root.SpecialMaterial?.Definition,IsNullObject=IsNamedNullLeaf(root)};

    private bool TryPublishEffectPrimitive(EnbRetailAssetBundle assets,int id,
        EnbCurrentRetailRObjEffectAsset.FactoryPreparation selected,(float Scale,Vector3 Hsv) inputs,
        out EnbCurrentRetailRObjEffectAsset asset)
    {
        if(_shipExhaustOwner is {} primary)return primary.TryPublishEffectPrimitive(assets,id,selected,inputs,out asset);
        asset=null!;
        if(selected.IsMissingFactoryObject||selected.IsPreconfiguredFactoryObject ||
            !EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Scale)&&!EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Hsv))return false;
        //91D4B0's built-in NULL has no FILE source, but a FILE/NAME BASE
        //still calls48B220. The NULLOBJECT BASE branch bypasses that factory.
        if(assets.PresentationCatalog?.ResolveBaseAsset(id) is {Kind:EnbBaseAssetDefinitionKind.FileName,Name:{} builtin} definition &&
            string.Equals(builtin,"NULL",StringComparison.OrdinalIgnoreCase))
        {
            var request=new EnbSceneObjectSnapshot(0,0,0,false,false,byte.MaxValue,id,inputs.Scale,inputs.Hsv.X,inputs.Hsv.Y,inputs.Hsv.Z,
                0,false,null,null!,null,null,null,[],[],[],[],[],[],null,[],null,0,0);
            var factory=CreateOrdinaryBaseFactory(request,assets,definition);
            asset=CreateConfiguredPrimitiveEffectAsset(factory.Root??throw new InvalidDataException(factory.Diagnostic));
            return true;
        }
        if(selected.Source is not {} source ||
            (source.CurrentRetailAggregate?.BaseRegisteredPrototype??source.RegisteredPrototype) is not {Kind:EnbW3dPrototypeKind.Sphere or EnbW3dPrototypeKind.Ring or EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.Box or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.NullObject} entry)return false;
        //6EA830 ->6F51D0 ->48B220 publishes at construction, before the
        //effect's later source-radius setter. The selected FILE is already
        //published. PrepareOwnedFactory has also acquired wrapper children;
        //do not rerun their named factories while materializing the primitive.
        var resource=new EnbW3dRenderResource(EnbW3dRenderResourceKey.From(source.CurrentRetailAggregate?.BaseResourceLocation??source.ResourceLocation),
            source.LogicalFileName,[],[],[],[],[]);
        LiveCpuRoot root;
        if(entry.Kind is EnbW3dPrototypeKind.Dazzle or EnbW3dPrototypeKind.Box or EnbW3dPrototypeKind.SpecialMaterial or EnbW3dPrototypeKind.NullObject)
        {
            if(!TryGetCpuRoot(new(id,-1,source.LogicalFileName,source.RequestedObjectName),source,assets,out root,out var diagnostic))
                throw new InvalidDataException(diagnostic);
        }
        else root=(entry.Kind==EnbW3dPrototypeKind.Sphere
            ?CreateRetainedSphereRoot(resource,source.RequestedObjectName,EnbRetailSphereAttachment.LoadFromSource(assets,source))
            :CreateRetainedRingRoot(resource,source.RequestedObjectName,EnbRetailRingAttachment.LoadFromSource(assets,source))) with
            {DiscardedParticleConstructions=selected.DiscardedParticleConstructions};
        root=RetainConfiguredPrimitive(source.RequestedObjectName,root,inputs.Scale,inputs.Hsv,assets);
        asset=CreateConfiguredPrimitiveEffectAsset(root) with {OverbrightOff=assets.PresentationCatalog!.ResolveBaseAsset(id).OverbrightOff};
        return true;
    }

    //Box26/27 and SpecialMaterial24 inherit462FB0 (RET4) for Scale.
    //48B220 still registers their configured name and skips HSV dispatch.
    private static bool IsNoOpConfiguredLeaf(LiveCpuRoot root)=>root.SpecialMaterial is not null || IsNamedNullLeaf(root) ||
        root.Hlod is null && root.Collection is null && root.Resource.HierarchyModels.Count==0 &&
        root.Resource.ExternalRenderObjects.Count==1 && root.Resource.ExternalRenderObjects[0].BoxDefinition is not null;

    private static bool IsNamedNullLeaf(LiveCpuRoot root)=>root.Hlod is null && root.Collection is null &&
        root.Resource.HierarchyModels.Count==0 && root.Resource.ExternalRenderObjects.Count==1 &&
        root.Resource.ExternalRenderObjects[0].Kind==EnbW3dExternalRenderObjectKind.NullObject;

    private static bool IsDirectDazzle(LiveCpuRoot root)=>root.Hlod is null && root.Collection is null &&
        root.Resource.HierarchyModels.Count==0 && root.Resource.ExternalRenderObjects.Count==1 &&
        root.Resource.ExternalRenderObjects[0].DazzleDefinition is not null;

    //48B220 configures the copied RenderObj before publishing its prototype.
    //90D340/912480 multiply stored property keys;90BB00/911190 copy those
    //keys into each acquired sphere/ring. Do not reconfigure from authored
    //Definition or collapse successive multiplications into one factor.
    private LiveCpuRoot RetainConfiguredPrimitive(string name,LiveCpuRoot root,float scale,Vector3 hsv,EnbRetailAssetBundle assets)
    {
        if(_shipExhaustOwner is {} primary)return primary.RetainConfiguredPrimitive(name,root,scale,hsv,assets);
        if(root.Sphere is null && root.Ring is null && !IsDirectDazzle(root) && !IsNoOpConfiguredLeaf(root))return root;
        bool changesScale=EnbCurrentRetailPrototypeVariantName.ChangesScale(scale);
        bool changesHsv=EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
        if(!changesScale&&!changesHsv)return root;
        string key=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        if(!_configuredPrimitiveVariants.TryGetValue(key,out var prototype))
        {
            //9A3A50 constructs discarded wrapper children before48B220
            //configures the primitive.90C490/911AF0 clone only primitive
            //state; they do not replay those released child constructors.
            if(!root.ParticleConstructionPrepared)
                foreach(var discarded in root.DiscardedParticleConstructions)_=new EnbRetailParticleRandomOwner(discarded);
            var sphere=root.Sphere;
            var ring=root.Ring;
            var resource=root.Resource;
            if(changesScale)
            {
                if(sphere is not null)sphere=sphere with {Asset=sphere.Asset.Scale(scale)};
                if(ring is not null)ring=ring with {Asset=ring.Asset.ScaleRadiusMultipliers(scale)};
                //96BAB0 multiplies the retained float, not the authored radius
                //or halo size.48B220 skips HSV dispatch for class32.
                if(IsDirectDazzle(root))resource=resource.Copy().SetExternalRenderObjects(resource.ExternalRenderObjects
                    .Select(e=>e with {DazzleInstanceScale=e.DazzleInstanceScale*scale,HasConfiguredDazzleScale=true}).ToArray());
            }
            if(changesHsv)
            {
                sphere=sphere?.ApplyHsv(assets,hsv);
                ring=ring?.ApplyHsv(assets,hsv);
            }
            //The binding now contains the complete factory configuration.
            //Only subsequent parent/effect placement may supply another scale.
            prototype=root with {Resource=resource,Sphere=sphere,Ring=ring,ConfiguredFactoryInputs=(1,Vector3.Zero),RslFactoryStateId=-1,
                DiscardedParticleConstructions=[],ParticleConstructionPrepared=true};
            _configuredPrimitiveVariants.Add(key,prototype);
            _prototypeResources.RegisterConfiguredPrototypeName(key);
        }
        //Bindings/property tables are immutable; playback and sampled values
        //are separate WorldSphere/WorldRing owners keyed by this identity.
        return prototype with {PrimitiveFactoryIdentity=new object(),PhysicalFactoryOwner=new PhysicalFactoryLifetime(),DazzleFactoryCreationClock=_dazzlePreviousFrameClock,
            FactoryLookupName=key,RslFactoryStateId=root.RslFactoryStateId};
    }
}
