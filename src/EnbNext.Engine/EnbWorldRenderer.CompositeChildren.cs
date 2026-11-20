using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static long _nextDrawPreparationEpoch;
    private bool TryPrepareRetainedRootDraw(LiveRootInstance instance,out string? diagnostic,long preparationEpoch=0)
    {
        // A stamp belongs only to this callback-free draw traversal, never to a
        // whole frame. Captures, later views and callback phases prepare afresh.
        diagnostic=null;
        if(preparationEpoch!=0 && instance.DrawPreparationEpoch==preparationEpoch)return true;
        //A parent's forced level can change this child's admission. Resolve
        //that state before the caller tests ShouldDraw, in native tree order.
        if(instance.Parent is {} parent&&!TryPrepareRetainedRootDraw(parent,out diagnostic,preparationEpoch))return false;
        bool previousAdmission=instance.ShouldDraw;
        ApplyAggregateRenderLevel(instance,parentAlreadyPrepared:preparationEpoch!=0);
        if(!TryRefreshOrdinaryHlodChildCore(instance,false,out diagnostic))return false;
        //Only ordinary children have a level-dependent mask and bypass scene
        //gathering. Preserve culling/failure decisions for every other owner.
        if(!instance.IsOrdinaryHlodChild)instance.ShouldDraw&=previousAdmission;
        instance.EngineChildHidden=!instance.IsExternalRslModel && (instance.IsOrdinaryHlodChild||instance.IsAggregateHlodChild||instance.IsExternalLegoChild)&&!RetainedEngineChildVisible(instance);
        if(preparationEpoch!=0)instance.DrawPreparationEpoch=preparationEpoch;
        return true;
    }

    private bool RetainedEngineChildVisible(LiveRootInstance instance)
    {
        //00810750 applies +184 to every render object on an exhaust pivot,
        //including meshes, not just particle emitters.
        return instance.Parent is not {} parent || RetainedEnginePivotVisible(parent, instance.OrdinaryHlodPivot);
    }

    private sealed record EnginePivotLayout(bool[] EnginePivots);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<LiveCpuRoot,EnginePivotLayout> EnginePivots=new();
    private static EnginePivotLayout ResolveEnginePivots(LiveCpuRoot root)
    {
        if(!TryResolveLiveAttachmentHierarchy(root,out var hierarchy,out _) || hierarchy is null)return new([]);
        var pivots=new bool[hierarchy.Pivots.Count];
        for(int i=0;i<pivots.Length;i++)pivots[i]=
            EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(hierarchy.Pivots[i].Name)||
            EnbCurrentRetailShipExhaustVisibilityRuntime.IsEngineBoneName(hierarchy.Pivots[i].Name);
        return new(pivots);
    }
    private bool RetainedEngineSwitchVisible(LiveRootInstance parent)
        =>_renderingMovie?.Visuals.TryGetValue(parent.Object.RenderIdentity,out var visual)==true
            ? !visual.EngineTrailsTouched || visual.EngineTrails
            : parent.Object.ShipPresentation?.IsOrganic!=true && EnbCurrentRetailShipExhaustVisibilityRuntime.Resolve(parent.Object).ShouldDraw;

    private bool RetainedEnginePivotVisible(LiveRootInstance parent,uint pivot)
    {
        if(parent.Object.ObjectType is not (0 or 1 or 25))return true;
        var pivots=(parent.PreparedEnginePivots??=EnginePivots.GetValue(parent.Root,ResolveEnginePivots)).EnginePivots;
        return pivot>=(uint)pivots.Length || !pivots[pivot] || RetainedEngineSwitchVisible(parent);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbW3dRenderResource,
        System.Runtime.CompilerServices.ConditionalWeakTable<LiveCpuRoot, EnbW3dRenderResource>> _hiddenEngineResourceViews = new();

    private EnbW3dRenderResource VisibleRetainedDrawResource(LiveRootInstance instance)
    {
        var resource=RetainedDrawResource(instance);
        if(instance.Root.Hlod is not {} selected || instance.Object.ObjectType is not (0 or 1 or 25) ||
            RetainedEngineSwitchVisible(instance))return resource;
        if(_retainedOrdinaryMeshes && ReferenceEquals(instance.EngineVisibilitySource,resource))return instance.EngineHiddenResource!;
        var enginePivots=(instance.PreparedEnginePivots??=EnginePivots.GetValue(instance.Root,ResolveEnginePivots)).EnginePivots;
        var current=resource.HierarchicalLods.FirstOrDefault(h=>h.Name.Equals(selected.Name,StringComparison.OrdinalIgnoreCase));
        if(current is null||!current.Levels.SelectMany(l=>l.Members).Concat(current.Aggregate?.Members??[])
            .Any(m=>m.ReferenceIndex<(uint)enginePivots.Length && enginePivots[m.ReferenceIndex]))
        {
            if(_retainedOrdinaryMeshes){instance.EngineVisibilitySource=resource;instance.EngineHiddenResource=resource;}
            return resource;
        }
        //810750 toggles every concrete child, including meshes represented
        //inline in our HLOD data. Keep hidden members in bounds/membership;
        //93F390/98DD50 suppress their draw through +174.
        var variants = _hiddenEngineResourceViews.GetValue(resource, static _ => new());
        if (_retainedOrdinaryMeshes && variants.TryGetValue(instance.Root, out var hidden))
        {instance.EngineVisibilitySource=resource;return instance.EngineHiddenResource=hidden;}
        var filtered = resource.Copy().SetHierarchicalLods(resource.HierarchicalLods.Select(hlod=>
            hlod.Name.Equals(selected.Name,StringComparison.OrdinalIgnoreCase)?hlod with {
                Levels=hlod.Levels.Select(level=>level with {Members=level.Members.Where(m=>RetainedEnginePivotVisible(instance,m.ReferenceIndex)).ToArray()}).ToArray(),
                Aggregate=hlod.Aggregate is {} aggregate?aggregate with {Members=aggregate.Members.Where(m=>RetainedEnginePivotVisible(instance,m.ReferenceIndex)).ToArray()}:null
            }:hlod).ToArray());
        // Engine visibility is a uniform runtime switch over the engine-named
        // pivots. Geometry/membership/root changes select another weak key.
        if (_retainedOrdinaryMeshes)
        {
            variants.Add(instance.Root, filtered);
            instance.EngineVisibilitySource=resource;instance.EngineHiddenResource=filtered;
        }
        return filtered;
    }

    private long _ownedCompositePreparations;
    private RootInvocations? PrepareCompositeInvocation(LiveRootInstance parent,int slot,bool collection,int ordinal,LiveCpuRoot child)
    {
        if(!_retainedOrdinaryMeshes)return null;
        if(parent.CompositeInvocations.Length==0)
            parent.CompositeInvocations=new RootInvocations?[parent.Root.CollectionChildren.Count+parent.Root.HmodelChildren.Count+parent.Root.AggregateAttachments.Count];
        if(parent.CompositeInvocations[slot] is {} retained)return retained;
        _ownedCompositePreparations++;
        return parent.CompositeInvocations[slot]=new($"{parent.InstancePath}/{(collection?"collection":"hmodel")}/{ordinal}:{child.RootRenderObjectName}");
    }

    private RootInvocations? PrepareAggregateInvocation(LiveRootInstance parent,int ordinal,LiveAggregateAttachment attachment)
    {
        if(!_retainedOrdinaryMeshes)return null;
        int slot=parent.Root.CollectionChildren.Count+parent.Root.HmodelChildren.Count+ordinal;
        if(parent.CompositeInvocations.Length==0)
            parent.CompositeInvocations=new RootInvocations?[parent.Root.CollectionChildren.Count+parent.Root.HmodelChildren.Count+parent.Root.AggregateAttachments.Count];
        if(parent.CompositeInvocations[slot] is {} retained)return retained;
        string attachmentPath=$"{parent.InstancePath}/{ordinal}:{attachment.SubobjectName}";
        string path=attachment.RslStateId is {} stateId?$"{parent.InstancePath}/rsl-bone/{stateId}":attachmentPath;
        _ownedCompositePreparations++;
        return parent.CompositeInvocations[slot]=new(path,attachmentPath);
    }

    //The ordinary and Lego attachment paths must walk the same retained
    //storage:0098DD50 draws it and0098F090 refreshes every child transform.
    private bool TryAppendRetainedCompositeChildren(LiveRootInstance instance,Matrix4x4 objectWorld,
        RootTaskList staged,out string? diagnostic)
    {
        diagnostic=null;var root=instance.Root;
        for(int index=0;index<root.CollectionChildren.Count;index++)
        {
            int first=staged.Count;
            var slots=PrepareCompositeInvocation(instance,index,true,index,root.CollectionChildren[index]);
            if(!TryAppendRootInstanceTreePrepared(instance.Object,root.CollectionChildren[index],instance.IsRsl,objectWorld,
                slots?.Path??$"{instance.InstancePath}/collection/{index}:{root.CollectionChildren[index].RootRenderObjectName}",
                true,null,staged,out diagnostic,parentInstance:instance,invocationSlots:slots))return false;
            staged[first].ParentStorageOrdinal=index;
        }
        if(root.HmodelChildren.Count==0)return true;
        var hierarchy=RetainAggregateAttachmentBinding(instance).Hierarchy;
        if(hierarchy is null){diagnostic=$"HMODEL '{root.RootRenderObjectName}' child hierarchy is unavailable.";return false;}
        var animations=instance.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
        var post=ConfiguredHierarchyPostTransforms(instance);
        bool scaled=post is not null&&string.Equals(post.HierarchyName,hierarchy.Name,StringComparison.OrdinalIgnoreCase);
        bool ready=TryResolveRetainedHierarchyPose(instance,hierarchy,
            scaled?post!.RigidWorldTransform:objectWorld,scaled?post!.HierarchyTranslationScale:null,
            animations,scaled?post!.Transforms:null,out var pose,out diagnostic);
        if(!ready)return false;
        ObservePlanetExhaust(instance.Object, hierarchy, pose.WorldTransforms, instance.InstancePath);
        for(int childIndex=0;childIndex<root.HmodelChildren.Count;childIndex++)
        {
            var child=root.HmodelChildren[childIndex];
            if(child.Pivot>=(uint)pose.WorldTransforms.Length)
            {diagnostic=$"HMODEL '{root.RootRenderObjectName}' child '{child.Root.RootRenderObjectName}' references missing pivot {child.Pivot}.";return false;}
            if (UsesPlanetExhaust(instance.Object) && EnbCurrentRetailShipExhaustVisibilityRuntime.IsExhaustBoneName(hierarchy.Pivots[(int)child.Pivot].Name)) continue;
            int index=staged.Count;
            var childWorld=pose.WorldTransforms[(int)child.Pivot];
            //0098EF60 scales child data separately from the rigid pivot matrix.
            //The existing frame refresh below publishes that explicit scale.
            if(scaled)childWorld=Matrix4x4.CreateScale(post!.RenderObjectScale)*childWorld;
            var slots=PrepareCompositeInvocation(instance,root.CollectionChildren.Count+childIndex,false,child.Ordinal,child.Root);
            if(!TryAppendRootInstanceTreePrepared(instance.Object,child.Root,instance.IsRsl,childWorld,
                slots?.Path??$"{instance.InstancePath}/hmodel/{child.Ordinal}:{child.Root.RootRenderObjectName}",true,null,staged,out diagnostic,
                parentInstance:instance,isOrdinaryHlodChild:!child.IsAggregate,
                ordinaryHlodPivot:child.Pivot,isAggregateHlodChild:child.IsAggregate,invocationSlots:slots))return false;
            var retained=staged[index];
            retained.ParentStorageOrdinal=child.StorageOrdinal;
            retained.IsAggregateHlodChild=child.IsAggregate;
            retained.OrdinaryHlodPivot=child.Pivot;
            retained.OrdinaryHlodLevelOrdinal=child.LevelOrdinal;
            retained.PivotVisible=pose.Visibility[(int)child.Pivot];
            retained.ShouldDraw=retained.PivotVisible&&(child.LevelOrdinal is null||child.LevelOrdinal==
                (instance.HlodSelection?.LevelOrdinal??EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(root.Hlod!)));
        }
        return true;
    }

    private bool TryAppendShipRetainedCompositeChildren(LiveRootInstance parent,Matrix4x4 world,
        RootTaskList destination,List<ShipAttachmentOwner> searchNodes,
        LiveShipLegoFrameTracker tracker,out string? diagnostic)
    {
        int start=destination.Count;
        if(!TryAppendRetainedCompositeChildren(parent,world,destination,out diagnostic))return false;
        for(int index=start;index<destination.Count;index++)
        {
            var child=destination[index];tracker.ChildRoots++;
            if(child.IsAggregateHlodChild)tracker.AggregateChildRoots++;
            if(!TryRefreshOrdinaryHlodChild(child,out diagnostic))return false;
            if(!TryResolveConfiguredAttachmentHierarchy(child.Root,out var hierarchy,out diagnostic))return false;
            if(hierarchy is null)continue;
            var post=child.HierarchyPostTransforms;
            Matrix4x4 rigid=post?.RigidWorldTransform??child.WorldTransformOverride??world;
            float scale=post?.RenderObjectScale??new Vector3(rigid.M11,rigid.M12,rigid.M13).Length();
            if(post is null&&scale!=1)
            {
                rigid.M11/=scale;rigid.M12/=scale;rigid.M13/=scale;
                rigid.M21/=scale;rigid.M22/=scale;rigid.M23/=scale;
                rigid.M31/=scale;rigid.M32/=scale;rigid.M33/=scale;
            }
            searchNodes.Add(PrepareFixedAttachment(child.InstancePath,child.Root,hierarchy,rigid,scale,
                child.AnimationBlend?.ResolvedSamples,Array.Empty<EnbCurrentRetailShipLegoRuntime.HierarchyPivotOverride>(),child));
        }
        return true;
    }
}
