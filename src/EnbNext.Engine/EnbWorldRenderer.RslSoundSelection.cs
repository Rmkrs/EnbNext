using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly Dictionary<(ulong,ulong),RslSoundSelections> _rslSoundSelections=[];
    private readonly HashSet<(ulong,ulong)> _compatRslSoundSelections=[];
    private long _rslSoundSelectionEpoch;
    private ulong _nextRslSoundHandle;
    private void ClearRslSoundSelections()
    {_rslSoundSelections.Clear();_compatRslSoundSelections.Clear();_rslSoundSelectionEpoch++;}
    private void DetachOwnedRslSoundSelections(SceneOwner source)
    {
        _rslSoundSelections.Remove((source.Definition.RenderIdentity,source.Definition.RenderObjectGeneration));
        source.SoundSelections=null;
    }
    private RslSoundSelections ResolveRslSoundSelections(IEnbRenderObject obj)
    {
        if(_buildingSceneOwner is {} source && source.Definition.RenderIdentity==obj.RenderIdentity &&
            source.Definition.RenderObjectGeneration==obj.RenderObjectGeneration)
        {
            if(source.SoundSelections is {} retained && source.SoundSelectionEpoch==_rslSoundSelectionEpoch)return retained;
            var key=(obj.RenderIdentity,obj.RenderObjectGeneration);
            if(!_rslSoundSelections.TryGetValue(key,out var owned))_rslSoundSelections[key]=owned=new();
            _compatRslSoundSelections.Remove(key);
            source.SoundSelectionEpoch=_rslSoundSelectionEpoch;
            return source.SoundSelections=owned;
        }
        var compatibilityKey=(obj.RenderIdentity,obj.RenderObjectGeneration);
        if(!_rslSoundSelections.TryGetValue(compatibilityKey,out var compatibility))
        {_rslSoundSelections[compatibilityKey]=compatibility=new();_compatRslSoundSelections.Add(compatibilityKey);}
        return compatibility;
    }
    internal Func<uint> RslSoundChoiceSample { get; set; }=EnbRetailMovieRandom.Damage.NextWord;
    internal Func<float> RslSoundModifierSample { get; set; }=() => EnbRetailCrtRandomSource.Current.NextUnit();
    private static LiveRslSoundInstanceKey RslSoundKey(IEnbRenderObject obj,int state,uint tick)
        =>new(obj.GameId,state,tick) {RenderIdentity=obj.RenderIdentity,VisualGeneration=obj.RenderObjectGeneration};

    private void ReconcileRslSoundSelections(IEnbRenderObject obj,EnbRslObjectState update,
        EnbResolvedRslLibraryResourceGraph graph,EnbRetailAssetBundle assets)
    {
        if(_shipExhaustOwner is not null)return;
        var owner=ResolveRslSoundSelections(obj);
        for(int ordinal=owner.Actions;ordinal<=update.Actions.Count;ordinal++)
        {
            while(owner.Releases<update.SoundHandleReleases.Count && update.SoundHandleReleases[owner.Releases].BeforeActionOrdinal<=ordinal)
                owner.Handles.Remove(update.SoundHandleReleases[owner.Releases++].StateId);
            if(ordinal==update.Actions.Count)break;
            var action=update.Actions[ordinal];
            if(action.Opcode==0x0032 && !action.PreserveSoundHandle)owner.Handles.Remove(action.StateId);
            if(action.Opcode!=0x0030 || !update.ActivationVirtualActionOrdinals.Contains(ordinal) ||
                !graph.TryFindState(action.StateId,out var state) || state.Definition.Type!=EnbRslStateType.Sound)continue;
            RemoveSoundCompletion(RslSoundKey(obj,action.StateId,action.ClientTime));
            // Acquire in native action order, even if released before this frame's update.
            // A silent factory choice leaves no native handle to retain on reactivation.
            if(owner.Handles.TryGetValue(action.StateId,out var prior) && prior.Definition is null)
                owner.Handles.Remove(action.StateId);
            DescribeR6Z30SoundActivationResolution(assets,graph,obj,action.StateId,state,action.ClientTime,out _, describe: false);
        }
        owner.Actions=update.Actions.Count;
    }

    internal RslSoundSelection SelectRslSound(IEnbRenderObject obj,int state,EnbRetailSoundDefinition definition)
    {
        if(_shipExhaustOwner is not null)return _shipExhaustOwner.SelectRslSound(obj,state,definition);
        var owner=ResolveRslSoundSelections(obj);
        if(owner.Handles.TryGetValue(state,out var retained))return retained;
        int choice=_speechScope.Calls.TryCall(definition)?EnbCurrentRetailWeightedSoundSelection.NextChoice(definition.Files,RslSoundChoiceSample):-1; // 0043E360 gates before selection.
        var file=choice<0?null:definition.Files[choice];
        if(file is null || file.IsNullChoice)return owner.Handles[state]=new(++_nextRslSoundHandle,null);
        var modifiers=EnbCurrentRetailEffectSoundRuntime.SampleModifiers(definition,RslSoundModifierSample(),RslSoundModifierSample());
        return owner.Handles[state]=new(++_nextRslSoundHandle,definition with
        {
            Files=[file with {Weight=null}], Volume=modifiers.Volume,Pitch=modifiers.Pitch,
            MinVolumeShift=0,MaxVolumeShift=0,MinPitchShift=0,MaxPitchShift=0,SecondsBetweenCalls=0
        });
    }

    private sealed class RslSoundSelections
    {
        public int Actions;
        public int Releases;
        public Dictionary<int,RslSoundSelection> Handles { get; }=[];
    }
    internal sealed record RslSoundSelection(ulong HandleId,EnbRetailSoundDefinition? Definition)
    {
        private EnbRetailAssetBundle? resolvedAssets;
        private EnbRetailSoundResolution? resolution;
        public EnbRetailSoundResolution Resolve(EnbRetailAssetBundle assets)
        {
            if(!ReferenceEquals(resolvedAssets,assets) || resolution is null)
            {
                resolution=assets.Sounds!.Resolve(Definition!,assets.Paths,assets.RetailMixResources);
                resolvedAssets=assets;
            }
            return resolution;
        }
    }
}
