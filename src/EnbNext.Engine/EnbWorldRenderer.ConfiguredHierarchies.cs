using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Factory scaling publishes immutable hierarchy storage. Keep its resource
    // view by both storage and source-resource identity so unchanged factories
    // do not invalidate prepared meshes on every bounds/query/draw traversal.
    private sealed class ConfiguredResourceViews(IReadOnlyList<EnbW3dRenderHierarchy> stored)
    {
        private readonly System.Runtime.CompilerServices.ConditionalWeakTable<EnbW3dRenderResource,EnbW3dRenderResource> _views=new();
        public EnbW3dRenderResource Get(EnbW3dRenderResource source)
        {
            if(_views.TryGetValue(source,out var retained))return retained;
            return _views.GetValue(source,resource=>resource.Copy().SetHierarchies(resource.Hierarchies.Select(tree=>
                stored.SingleOrDefault(candidate=>candidate.Name.Equals(tree.Name,StringComparison.OrdinalIgnoreCase))??tree).ToArray()));
        }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<EnbW3dRenderHierarchy>,ConfiguredResourceViews> ConfiguredViews=new();
    private static EnbW3dRenderResource ConfiguredHierarchyResource(LiveCpuRoot root,EnbW3dRenderResource? resource=null)
    {
        resource??=root.Resource;
        if(root.ConfiguredHierarchyTranslations is not {} stored)return resource;
        return ConfiguredViews.GetValue(stored,static hierarchies=>new(hierarchies)).Get(resource);
    }

    private static bool TryResolveConfiguredAttachmentHierarchy(LiveCpuRoot root,
        out EnbW3dRenderHierarchy? hierarchy,out string? diagnostic)
    {
        if(!TryResolveLiveAttachmentHierarchy(root,out hierarchy,out diagnostic))return false;
        if(hierarchy is not null && root.ConfiguredHierarchyTranslations is {} stored)
        {
            string name=hierarchy.Name;
            hierarchy=stored.Single(tree=>tree.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
        }
        return true;
    }

    private static EnbW3dHierarchyState? ConfiguredHierarchyPostTransforms(LiveRootInstance instance)
    {
        if(instance.Root.ConfiguredHierarchyTranslations is null || instance.Root.AttachmentHierarchyName is not {} name)
            return instance.HierarchyPostTransforms;
        if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out _))
            throw new InvalidDataException("Configured hierarchy owner transform unavailable");
        var transforms=instance.HierarchyPostTransforms?.Transforms??System.Collections.Immutable.ImmutableDictionary<int,Matrix4x4>.Empty;
        float pendingScale=ResolvePendingHierarchyScale(instance,scale);
        if(instance.ConfiguredPost is {} retained && retained.HierarchyName==name && retained.RigidWorldTransform==world &&
            retained.RenderObjectScale==scale && retained.PendingHierarchyScale==pendingScale && ReferenceEquals(retained.Transforms,transforms))return retained;
        var state=instance.ConfiguredPost??=new(name,world,scale,transforms);
        state.Update(name,world,scale,transforms,pendingScale);
        return state;
    }
}
