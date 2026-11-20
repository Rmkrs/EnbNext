using System.Collections.Immutable;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed class EnbPreparedRslAnimationSequence
{
    private readonly EnbResolvedRslLibraryResourceGraph _graph;
    private readonly int _initial;
    private readonly ImmutableDictionary<int,EnbW3dRenderHierarchyAnimation> _clips;
    private EnbPreparedRslAnimationSequence(EnbResolvedRslLibraryResourceGraph graph,int initial,
        ImmutableDictionary<int,EnbW3dRenderHierarchyAnimation> clips)
    {_graph=graph;_initial=initial;_clips=clips;}
    public EnbCurrentRetailRslAnimationObservations CreateObservations()
        =>new(new EnbCurrentRetailRslAnimationSequenceRuntime(_graph,_initial,_clips,0));

    public static EnbPreparedRslAnimationSequence Load(EnbRetailAssetBundle assets,
        EnbResolvedRslLibraryResourceGraph graph,int initial,ref EnbW3dRenderResource resource,
        EnbW3dRenderHierarchyAnimation? initialAnimation=null)
    {
        var clips=ImmutableDictionary.CreateBuilder<int,EnbW3dRenderHierarchyAnimation>();
        var loaded=new List<(EnbResolvedRslFileDependency File,string Name,EnbW3dRenderHierarchyAnimation Clip)>();
        int id=initial;
        string hierarchy=resource.HierarchicalLods.SingleOrDefault()?.HierarchyName ?? resource.HierarchyModels.Single().HierarchyName;
        while(true)
        {
            if(clips.ContainsKey(id))break; // A retained state closes a NEXT cycle.
            if(!graph.TryFindState(id,out var state) ||
                state.Definition.Type!=EnbRslStateType.Animation)
                throw new InvalidDataException("Unsupported effect animation NEXT chain");
            var d=state.Definition;
            string name=EnbCurrentRetailRslAnimationRuntime.BuildHAnimName(hierarchy,d.Animation,d.Suffix);
            if(id==initial && initialAnimation is {} current)
            {
                //The activating Object->Animation path already resolved and
                //retained this exact clip. Reopening FILE would compare it
                //against its own appended registry entry as a false duplicate.
                if(!resource.Animations.Contains(current) ||
                    !EnbCurrentRetailRslHAnimResolver.BuildRegistryName(current.Name,current.HierarchyName).Equals(name,StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Initial sequence clip does not match the retained activated animation",nameof(initialAnimation));
                clips.Add(id,current);
            }
            else if(loaded.FirstOrDefault(entry=>ReferenceEquals(entry.File,state.FileDependency) &&
                entry.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Clip is {} reused)
            {
                //The exact dependency and registry name were resolved in this
                //preparation. A second state retains that HAnim; it does not
                //introduce a competing FILE definition into the registry.
                clips.Add(id,reused);
            }
            else
            {
                if(!EnbCurrentRetailRslHAnimResolver.TryResolve(resource,state.FileDependency,assets.Paths,
                    assets.MixResources,assets.RetailMixResources,name,out var resolved,out var error))
                    throw new InvalidDataException(error);
                clips.Add(id,resolved.Animation);resource=resolved.DrawResource;
            }
            loaded.Add((state.FileDependency,name,clips[id]));
            if(!d.Flags.ActivateNext || d.NextStateId is not int next)break;
            id=next;
        }
        var sequence=new EnbPreparedRslAnimationSequence(graph,initial,clips.ToImmutable());
        _=sequence.CreateObservations(); // Validate every control/transition before admission.
        return sequence;
    }
}
