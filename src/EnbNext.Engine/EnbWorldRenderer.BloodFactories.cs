using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    //811D0E uses the shared manager+78, including generated-name hits and
    //clone-owned particle tables. Never reconstruct tables from a decoded
    //Mixed description after this factory has returned a retained clone.
    private bool TryCreateBloodFactory(IEnbRenderObject owner,string name,float scale,Vector3 hsv,
        EnbRetailAssetBundle assets,int? mode,out LiveCpuRoot root,out string? error,out bool missing)
    {
        bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(scale)||EnbCurrentRetailPrototypeVariantName.ChangesHsv(hsv);
        string variant=EnbCurrentRetailPrototypeVariantName.Create(name,scale,hsv);
        bool final=false;root=null!;error=null;missing=false;
        if(configured)
        {
            final=TryAcquireRslNamedRoot(owner.BaseAsset,-1,variant,assets,out root,out error,out missing);
            if(!final&&!missing)return false;
        }
        if(!final&&!TryAcquireRslNamedRoot(owner.BaseAsset,-1,name,assets,out root,out error,out missing))return false;
        if(!final)
        {
            var requested=new FactoryAppearance(scale,hsv.X,hsv.Y,hsv.Z);
            root=RetainConfiguredPrimitive(name,root,scale,hsv,assets);
            if(HasConfigurableMeshTree(root))root=RetainConfiguredDirectMeshRoot(root,ref requested);
        }
        if(HasParticleConstruction(root))root=RetainParticleFactoryTree(final?variant:name,root,final?1:scale,final?Vector3.Zero:hsv);
        root=ClearBloodAcceleration(root,mode) with {PhysicalFactoryOwner=new PhysicalFactoryLifetime(),IndependentRslFactory=true,
            ConfiguredFactoryInputs=root.ConfiguredFactoryInputs??(scale,hsv)};
        return true;
    }

    private static LiveCpuRoot ClearBloodAcceleration(LiveCpuRoot root,int? mode)
    {
        if(mode!=0)return root;
        return root with
        {
            RetainedEmitter=root.RetainedEmitter is {} emitter?emitter with
                {Definition=EnbCurrentRetailBloodParticleConfiguration.ForEnvironment(emitter.Definition,mode)}:null,
            HmodelChildren=root.HmodelChildren.Select(c=>c with {Root=ClearBloodAcceleration(c.Root,mode)}).ToArray(),
            CollectionChildren=root.CollectionChildren.Select(c=>ClearBloodAcceleration(c,mode)).ToArray(),
            AggregateAttachments=root.AggregateAttachments.Select(a=>a with
                {Root=a.Root is {} child?ClearBloodAcceleration(child,mode):null,
                    Exhaust=a.Exhaust is {} exhaust?exhaust with {Definition=EnbCurrentRetailBloodParticleConfiguration.ForEnvironment(exhaust.Definition,mode)}:null}).ToArray()
        };
    }
}
