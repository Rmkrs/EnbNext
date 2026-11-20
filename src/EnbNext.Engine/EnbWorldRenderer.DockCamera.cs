using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private ImmutableDictionary<uint,EnbDockCameraAnchor> _dockAnchors = ImmutableDictionary<uint,EnbDockCameraAnchor>.Empty;
    public ImmutableDictionary<uint,EnbDockCameraAnchor> DockAnchors => Volatile.Read(ref _dockAnchors);
    private EnbRetailAssetBundle? _dockAssets;
    private EnbRetailIniDocument? _dockIni;

    private sealed record DockDefinitionState(EnbRetailIniDocument Ini,string Name,EnbRetailDockCameraDefinition? Definition);
    private readonly record struct DockCandidate(int FirstOwnerIndex,LiveRootInstance Main,EnbRetailDockCameraDefinition Definition);
    private readonly List<DockCandidate> _dockCandidates=[];
    private readonly HashSet<ulong> _dockCandidateOwners=[];
    private long _dockDefinitionPreparations;
    private EnbRetailDockCameraDefinition? PreparedDockDefinition(LiveCpuRoot root)
    {
        var prepared=root.DockDefinition;
        if(prepared is null || !ReferenceEquals(prepared.Ini,_dockIni) || prepared.Name!=root.RuntimeRenderObjectName)
        {
            prepared=new(_dockIni!,root.RuntimeRenderObjectName,EnbRetailDockCameraDefinition.Resolve(_dockIni!,root.RuntimeRenderObjectName));
            root.DockDefinition=prepared;_dockDefinitionPreparations++;
        }
        return prepared.Definition;
    }

    private void PublishDockAnchors(RootTaskList roots, EnbRetailAssetBundle assets)
    {
        if(!ReferenceEquals(_dockAssets,assets))
        {
            _dockAssets=assets;
            string path=Path.Combine(assets.Paths.ClientIniDirectory,"dock.ini");
            _dockIni=EnbNext.Installation.EnbPathCase.FileExists(path)?EnbRetailIniDocument.Load(path):EnbRetailIniDocument.Parse("");
        }
        try
        {
        // Most scene objects have no dock definition. Retain that admission at
        // the root; only actual dock owners need a per-frame bone publication.
        foreach(var root in roots)
        {
            var definition=PreparedDockDefinition(root.Root);
            if(definition is null || !_dockCandidateOwners.Add(root.Object.RenderIdentity))continue;
            int first=0;
            while(roots[first].Object.RenderIdentity!=root.Object.RenderIdentity)first++;
            _dockCandidates.Add(new(first,root,definition));
        }
        if(_dockCandidates.Count==0)
        {Volatile.Write(ref _dockAnchors,ImmutableDictionary<uint,EnbDockCameraAnchor>.Empty);return;}
        // Preserve GroupBy's original owner order, including duplicate GameIDs
        // and an eligible root occurring later than its owner's first child.
        _dockCandidates.Sort(static (a,b)=>a.FirstOwnerIndex.CompareTo(b.FirstOwnerIndex));
        var result=ImmutableDictionary.CreateBuilder<uint,EnbDockCameraAnchor>();
        foreach(var candidate in _dockCandidates)
        {
            var main=candidate.Main;var definition=candidate.Definition;var selected=main;
            if(!string.IsNullOrEmpty(definition.SubObject))
                foreach(var root in roots)
                    if(root.Object.RenderIdentity==main.Object.RenderIdentity &&
                        string.Equals(root.Root.RuntimeRenderObjectName,definition.SubObject,StringComparison.OrdinalIgnoreCase))
                    {selected=root;break;}
            if(!TryBuildObjectWorld(selected.Object,out var world))continue;
            world=selected.WorldTransformOverride??world;
            if(!Matrix4x4.Decompose(world,out var scale,out var rotation,out var position))continue;
            var rigid=Matrix4x4.CreateFromQuaternion(rotation)*Matrix4x4.CreateTranslation(position);
            var bone=rigid;
            if(TryResolveConfiguredAttachmentHierarchy(selected.Root,out var hierarchy,out _) && hierarchy is not null)
            {
                if(!EnbCurrentRetailShipFixedVisualRuntime.TryResolveBoneWorld(hierarchy,rigid,ResolvePendingHierarchyScale(selected,scale.X),definition.Bone,
                    selected.AnimationBlend?.ResolvedSamples,out var resolved,out bool found,out _))continue;
                if(found)bone=resolved;
            }
            result[main.Object.GameId]=new(main.Object.RenderIdentity,definition,bone);
        }
        Volatile.Write(ref _dockAnchors,result.ToImmutable());
        }
        finally{_dockCandidates.Clear();_dockCandidateOwners.Clear();}
    }
}
