using System.Numerics;
using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong Identity,ulong Generation,int State),LiveCpuRoot> _rslConfiguredRoots=[];
    private readonly HashSet<(ulong Identity,ulong Generation)> _rslFactoryPrepared=[];
    private readonly HashSet<(ulong Identity,ulong Generation,int State)> _compatRslConfiguredRoots=[];
    private void DetachOwnedRslFactories(SceneOwner source)
    {
        foreach(var key in source.ConfiguredRslKeys)_rslConfiguredRoots.Remove(key);
        source.ConfiguredRslKeys.Clear();source.RslFactoriesPrepared=false;source.RslFactoryLifetimePrepared=false;
    }
    private string? _rslFactoryDiagnostic;

    private bool TryAcquireRslNamedRoot(int baseAsset,int stateId,string name,EnbRetailAssetBundle assets,
        out LiveCpuRoot root,out string? diagnostic,out bool nativeMissing)
    {
        root=default!;
        if(TryAcquireConfiguredPrototype(baseAsset,name,stateId,assets,out root))
        {diagnostic=null;nativeMissing=false;return true;}
        bool found=EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
            assets.FileResources,assets.MixResources,name,out var selected,out diagnostic,
            out var publications,out nativeMissing,_prototypeResources.CapturePrototypeSources(out _));
        foreach(var publication in publications)
            if(!_prototypeResources.TryLoadFile(publication,out _,out var publicationError,ReadMapperClock()))
            {diagnostic=publicationError;nativeMissing=false;return false;}
        if(!found)return false;
        if(publications.Count!=0 && !EnbCurrentRetailW3dObjectPrototypeResolver.TryResolveSimpleName(
            assets.FileResources,assets.MixResources,name,out selected,out diagnostic,
            _prototypeResources.CapturePrototypeSources(out _)))return false;
        return TryGetCpuRoot(new(baseAsset,stateId,selected.LogicalFileName,selected.RequestedObjectName),
            selected,assets,out root,out diagnostic);
    }

    private void PrecacheRslObjectRoot(int baseAsset,EnbResolvedRslStateResource state,EnbRetailAssetBundle assets)
    {
        try
        {
            int id=state.Definition.StateId;
            var key=state;
            if(_rslObjectPrototypes.ContainsKey(key))
            {TryMaterializeRslObjectRoot(baseAsset,id,state,assets,out _,out _);return;}
            //45E5F0 tests the constructor's named request only. Failure does not
            //set prepared, retry FILE, or install NULL;45EF80 can try later.
            if(state.ObjectPrototypeName is not {} name ||
                !TryAcquireRslNamedRoot(baseAsset,id,name,assets,out var root,out _,out _))return;
            root=RetainRslParticleTemplate(state,root);
            RemoveRslTemplatePrototype(state);
            if(root.IsDirectMeshPrototype)
                foreach(var mesh in root.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,root.RootRenderObjectName)))
                    PostprocessOrdinaryNamedMesh(mesh);
            root=root with {RslFactoryStateId=id};
            _rslObjectPrototypes[key]=(root,null);
        }
        catch(Exception error) when(error is InvalidDataException or NotSupportedException)
        {
            _rslFactoryDiagnostic=$"RSL precache state {state.Definition.StateId}: {error.Message}";
        }
    }

    private void RemoveRslTemplatePrototype(EnbResolvedRslStateResource state)
    {
        //45E5F0/45EF80 remove the requested name after constructing the
        //template, before setting prepared.45E9F0 clears this flag in copies;
        //accessing an already prepared template must not remove a later FILE
        //publication. Existing roots retain their compiled resources.
        if(state.Definition.Flags.RemovePrototype && state.ObjectPrototypeName is {} name)
        {
            _configuredDirectMeshRoots.Remove(name);
            _configuredDirectMeshVariants.Remove(name);
            _configuredParticleVariants.Remove(name);
            _configuredPrimitiveVariants.Remove(name);
            if(_configuredEffectAssemblies.Remove(name,out var assembly))
                foreach(var model in assembly.Models)model.Dispose();
            _prototypeResources.RemovePrototype(name);
        }
    }

    private void PrepareRslObjectFactories(IEnbRenderObject obj,EnbResolvedRslReference reference,
        EnbRetailAssetBundle assets,(float Scale,Vector3 Hsv)? effectInputs=null,SceneOwner? source=null)
    {
        if(_shipExhaustOwner is not null || (source is null&&!_rslFactoryPrepared.Add((obj.RenderIdentity,obj.RenderObjectGeneration))))return;
        var inputs=effectInputs??(obj.Scale,new Vector3(obj.HsvH,obj.HsvS,obj.HsvV));
        if(reference.ResourceGraph is not {} graph)return;
        //Precached templates are constructed in authored order before the
        //reverse-order live-state copy walk, including libraries without passes.
        RetainRslPassOwner(obj,reference,assets,prepareWholeLibrary:true);
        //45FFB0/45C990 visits the complete template list before STARTS_ENABLED
        //and BASE RSID.45F280 supplies OBJECT, raw scale and HSV to48B220.
        foreach(var state in graph.States.OrderByDescending(s=>s.Definition.SectionOrdinal))
        {
            if(state.Definition.Type is not (EnbRslStateType.Object or EnbRslStateType.Bone))continue;
            //457BE0 snapshots template flags before45E9F0 calls its getter.
            //45EDC0 sets100 on the first non-precached/non-instance-data
            //template access. Only that first live copy still clones on access;
            //later copies inherit100. Templates share the asset namespace.
            bool clonesOnFirstAccess=!state.Definition.Flags.Precached&&!state.Definition.Flags.InstanceData &&
                EnbRenderSession.Of(assets).RslAccessedObjectTemplates.Add((reference.RslId,state.Definition.StateId));
            try
            {
                if(!TryMaterializeRslObjectRoot(obj.BaseAsset,state.Definition.StateId,state,assets,out var root,out _))continue;
                bool configured=EnbCurrentRetailPrototypeVariantName.ChangesScale(inputs.Item1)||
                    EnbCurrentRetailPrototypeVariantName.ChangesHsv(inputs.Item2);
                string variantName=EnbCurrentRetailPrototypeVariantName.Create(state.ObjectPrototypeName!,inputs.Item1,inputs.Item2);
                bool retainedVariant=configured && (_configuredDirectMeshVariants.ContainsKey(variantName)||
                    _configuredParticleVariants.ContainsKey(variantName));
                if(configured && _configuredParticleVariants.TryGetValue(variantName,out var particleVariant))
                    root=particleVariant;
                else if(configured && _configuredDirectMeshRoots.TryGetValue(variantName,out var meshVariantRoot))
                    root=meshVariantRoot.Root;
                else if(configured && _configuredDirectMeshVariants.TryGetValue(variantName,out var meshVariant) &&
                    meshVariant.CompositeRoot is {} compositeVariant)
                    root=compositeVariant;
                bool loadedVariant=false;
                //45F280 always calls the named factory, even for neutral
                //inputs.48B220's neutral branch queries91D4B0 again; a retained
                //template NULL must not mask a subsequently published name.
                if(!configured || !retainedVariant)
                {
                    string name=state.ObjectPrototypeName!;
                    //48B220 first queries the formatted variant name. On a
                    //miss it queries the original name afresh, not the RSL
                    //template child. A FILE-authored variant is already final.
                    if(configured)
                    {
                        loadedVariant=TryAcquireRslNamedRoot(obj.BaseAsset,state.Definition.StateId,variantName,assets,
                            out var variantRoot,out var variantError,out bool variantMissing);
                        if(loadedVariant)
                        {
                            root=variantRoot;configured=false;
                            //48B3E8 postprocesses even a previously registered
                            //variant. Composite children already use the named
                            //constructor; a direct mesh has no such child walk.
                            if(root.IsDirectMeshPrototype)
                                foreach(var mesh in root.Resource.Meshes.Where(mesh=>EnbW3dRenderObjectNameSemantics.MatchesMesh(mesh,root.RootRenderObjectName)))
                                    PostprocessOrdinaryNamedMesh(mesh);
                        }
                        else if(!variantMissing)throw new NotSupportedException(variantError);
                    }
                    if(!loadedVariant && !TryAcquireRslNamedRoot(obj.BaseAsset,state.Definition.StateId,name,assets,out root,out var failure,out bool missing))
                    {
                        if(!missing)throw new NotSupportedException(failure);
                        if(!TryAcquireRslNamedRoot(obj.BaseAsset,state.Definition.StateId,"NULL",assets,out root,out failure,out _))
                            throw new NotSupportedException(failure);
                        //45F2B6 failure constructs941330 directly; it does not
                        //publish a configured variant of the missing name.
                        configured=false;
                        root=root with {ConfiguredFactoryInputs=(1,Vector3.Zero)};
                    }
                    //This is the copied state's factory result, not a new
                    //template construction. Its particle constructor runs below.
                    root=root with {RslFactoryStateId=state.Definition.StateId};
                }
                if(configured)root=RetainConfiguredPrimitive(state.ObjectPrototypeName!,root,inputs.Item1,inputs.Item2,assets);
                if(configured && HasConfigurableMeshTree(root))
                {
                    var resource=root.Resource.Copy().SetHierarchicalLods(root.Hlod is {} hlod?[hlod]:[]).SetHierarchyModels([]);
                    if(root.IsDirectMeshPrototype)resource=resource.Copy().SetMeshes(resource.Meshes.Where(m=>
                        EnbW3dRenderObjectNameSemantics.MatchesMesh(m,root.RootRenderObjectName)).ToArray());
                    uint clock=ReadMapperClock();
                    var variant=root.Hlod is not null || root.Collection is not null?RetainConfiguredMeshTree(state.ObjectPrototypeName!,inputs.Item1,inputs.Item2,assets,root):
                        RetainConfiguredDirectMeshVariant(state.ObjectPrototypeName!,inputs.Item1,inputs.Item2,assets,clock,
                        ()=>new(resource,clock,CameraMeshBatches,_prototypeResources,resource.Meshes.Cast<EnbW3dRenderMesh?>().ToArray(),_configuredDirectMeshModels),
                        composite:root.Hlod is not null);
                    root=root with
                    {
                        Resource=variant.CompositeRoot?.Resource??variant.Model.Resource,
                        Hlod=variant.CompositeRoot?.Hlod??(root.Hlod is null?null:variant.Model.Resource.HierarchicalLods.First()),
                        HmodelChildren=variant.CompositeRoot?.HmodelChildren??root.HmodelChildren,
                        CollectionChildren=variant.CompositeRoot?.CollectionChildren??root.CollectionChildren,
                        PrimitiveConfigurationOwner=variant.CompositeRoot?.PrimitiveConfigurationOwner,
                        HierarchyConfigurationOwner=variant.CompositeRoot?.HierarchyConfigurationOwner,
                        ConfiguredHierarchyTranslations=variant.CompositeRoot?.ConfiguredHierarchyTranslations,
                        AuthoredHlodMemberNames=variant.CompositeRoot?.AuthoredHlodMemberNames,
                        ConfiguredFactoryInputs=(variant.Scale,variant.Hsv)
                    };
                    _configuredDirectMeshRoots.TryAdd(variantName,(root with {RslFactoryStateId=-1},variant.Scale,variant.Hsv));
                }
                root=root with {RslFactoryStateId=state.Definition.StateId};
                root=RetainParticleFactoryTree(loadedVariant?variantName:state.ObjectPrototypeName!,root,
                    loadedVariant?1:inputs.Item1,loadedVariant?Vector3.Zero:inputs.Item2);
                //45F280 constructs before selection, but45EDC0 can replace
                //that model on first access. Preserve the constructor epoch
                //only when the copied flags suppress that later clone.
                root=root with {DazzleFactoryCreationClock=clonesOnFirstAccess?null:_dazzlePreviousFrameClock};
                if(loadedVariant && root.ConfiguredFactoryInputs is null)
                    root=root with {ConfiguredFactoryInputs=(1,Vector3.Zero)};
                var configuredKey=(obj.RenderIdentity,obj.RenderObjectGeneration,state.Definition.StateId);
                _rslConfiguredRoots[configuredKey]=root;
                if(source is null)_compatRslConfiguredRoots.Add(configuredKey);
                else source.ConfiguredRslKeys.Add(configuredKey);
            }
            catch(Exception error) when(error is InvalidDataException or NotSupportedException)
            {
                //Optional model preparation must not discard the whole frame.
                //Retain the last working prototype and expose the actual failure.
                _rslFactoryDiagnostic=$"RSL {reference.RslId} state {state.Definition.StateId}: {error.Message}";
            }
        }
    }

    private LiveCpuRoot ResolveConfiguredRslRoot(IEnbRenderObject obj,int state,LiveCpuRoot root)
    {
        var owner=_shipExhaustOwner??this;
        var key=(obj.RenderIdentity,obj.RenderObjectGeneration,state);
        if(!owner._rslConfiguredRoots.TryGetValue(key,out var retained))return root;
        //45EDC0 clones only on this first access, then retains the same object.
        //Stamp before traversal so later-admitted children share that epoch.
        if(retained.DazzleFactoryCreationClock is null)
            owner._rslConfiguredRoots[key]=retained=retained with {DazzleFactoryCreationClock=owner._dazzlePreviousFrameClock};
        return retained;
    }
}
