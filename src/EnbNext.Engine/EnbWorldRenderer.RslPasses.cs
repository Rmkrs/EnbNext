using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // Templates belong to the same asset-manager namespace as W3D prototypes.
    // A camera draws an existing RSL instance; it does not copy that instance.
    private readonly Dictionary<(ulong RenderIdentity, ulong VisualGeneration),
        (int RslId, EnbCurrentRetailRslEffectPassLibrary Library)> _rslPassOwners = [];
    private readonly HashSet<(ulong RenderIdentity,ulong VisualGeneration)> _compatRslPassOwners=[];
    private readonly List<EnbCurrentRetailRslEffectPassLibrary> _retiredOwnedRslPasses=[];
    private void DetachOwnedRslPass(SceneOwner owner)
    {
        var key=(owner.Definition.RenderIdentity,owner.Definition.RenderObjectGeneration);
        if(_rslPassOwners.Remove(key,out var entry))_retiredOwnedRslPasses.Add(entry.Library);
        _compatRslPassOwners.Remove(key);
        owner.RslPassOwner=null;owner.FrameRslPassOwner=null;owner.RslPassPrepared=false;
    }
    private void DrainOwnedRslPasses()
    {foreach(var library in _retiredOwnedRslPasses)library.Dispose();_retiredOwnedRslPasses.Clear();}
    private IReadOnlyDictionary<ulong, EnbCurrentRetailRslEffectPassLibrary> _frameRslPassOwners =
        new Dictionary<ulong, EnbCurrentRetailRslEffectPassLibrary>();

    private EnbCurrentRetailRslEffectPassLibrary? RetainRslPassOwner(IEnbRenderObject obj,
        EnbResolvedRslReference rsl, EnbRetailAssetBundle assets, bool prepareWholeLibrary=false)
    {
        var key = (obj.RenderIdentity, obj.RenderObjectGeneration);
        if (_rslPassOwners.TryGetValue(key, out var existing))
        {
            if (existing.RslId == rsl.RslId) return existing.Library;
            existing.Library.Dispose();
            _rslPassOwners.Remove(key);_compatRslPassOwners.Remove(key);
        }
        if (rsl.ResourceGraph is not {} graph || !prepareWholeLibrary && !graph.States.Any(state =>
            state.FileDependency.EffectPass is { ConstructsUsablePass: true, Texture.Length: > 0 })) return null;
        var templates = EnbRenderSession.Of(assets).RslPassTemplates;
        if (!templates.TryGetValue(rsl.RslId, out var template))
        {
            template = EnbCurrentRetailRslEffectPassLibrary.Create(graph, _prototypeResources, state =>
            {
                bool objectState = state.Definition.Type is EnbRslStateType.Object or EnbRslStateType.Bone;
                //0045ADF8 loads an Animation FILE before reading its flags.
                //0045AB98 tests100 for Object; Bone0045BD4B instead tests
                //literal bit8. The shared child constructor still tests100.
                bool preloadFile = EnbCurrentRetailRslEffectPassLibrary.PreloadsFile(state.Definition);
                //0045AB98 preloads FILE before0045E5F0 asks for OBJECT.
                //Keep that write order around effect-pass texture clamping.
                if (preloadFile && state.FileDependency is { Model: {} model, ResourceLocation: {} location, Textures: {} textures } file)
                    _prototypeResources.TryLoadFile(new(file.LogicalFileName,
                        state.ObjectPrototypeName ?? string.Empty, location, model, textures)
                        { LooseFilePath = file.LooseFilePath,PrototypeFile=file.PrototypeFile }, out _, out _, ReadMapperClock());
                if (objectState && state.Definition.Flags.Precached)
                    PrecacheRslObjectRoot(obj.BaseAsset,state,assets);
            });
            templates.Add(rsl.RslId, template);
        }
        var instance = template.Copy();
        _rslPassOwners.Add(key, (rsl.RslId, instance));
        if(_ownedScene is null||!_ownedScene.TryFind(obj.RenderIdentity,out var node)||
            node.Definition.RenderObjectGeneration!=obj.RenderObjectGeneration)_compatRslPassOwners.Add(key);
        return instance;
    }

    private void ClearRslPassOwners()
    {
        foreach (var owner in _rslPassOwners.Values) owner.Library.Dispose();
        _rslPassOwners.Clear();_compatRslPassOwners.Clear();DrainOwnedRslPasses();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)
        {node.State.RslPassOwner=null;node.State.RslPassPrepared=false;}
        _frameRslPassOwners = new Dictionary<ulong, EnbCurrentRetailRslEffectPassLibrary>();
    }

    private void BindRslPassContext(List<EnbW3dDrawTask> packets, int start, LiveRootInstance instance,
        IReadOnlyDictionary<ulong, EnbRslObjectState> updates)
    {
        //Follow actual retained parent links. Similar resource/path names and
        //independent Effect objects do not establish00460FF0 child ownership.
        var parent = instance;
        while (parent is not null && !parent.IsRsl) parent = parent.Parent;
        if(parent is null||parent.Object.RenderIdentity!=instance.Object.RenderIdentity)return;
        EnbCurrentRetailRslEffectPassLibrary? owner;
        EnbRslObjectState? update;
        if(_ownedSceneFrame&&parent.PreparedOwner?.SceneSource is {} source)
        {owner=source.FrameRslPassOwner;update=source.RslUpdate;}
        else
        {_frameRslPassOwners.TryGetValue(parent.Object.RenderIdentity,out owner);updates.TryGetValue(parent.Object.RenderIdentity,out update);}
        if(owner is null||update is null||update.CurrentObjectHidden||update.ActiveEffectPassStateIds.Count==0)return;
        var context = owner.CreateContext(update.ActiveEffectPassStateIds).Snapshot();
        for (int i = start; i < packets.Count; i++)
            if (packets[i].Mesh.CurrentRetailUsesMeshRenderer)
                packets[i] = (packets[i]).ForkTask().SetCurrentRetailRslPassContext(context);
    }
}
