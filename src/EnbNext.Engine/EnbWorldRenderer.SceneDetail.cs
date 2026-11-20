using EnbNext.Assets;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private long _ownedSceneModelResolutions;
    private Action<LiveRootInstance,int> PrepareHlodSceneChanges(RootTaskList roots,EnbRetailAssetBundle assets)
    {
        foreach(var instance in roots)
        {
            // Ordinary FILE occurrences bind once to their concrete scene model.
            // Source mutations invalidate these links locally, before the next
            // view; pose, lighting and visibility updates leave them intact.
            var source=!instance.IsRsl?instance.PreparedOwner?.SceneSource:null;
            if(source is not null && instance.SceneModelBound)
            {
                if(instance.PreparedSceneModel is {} retained)retained.SceneInstance=instance;
                continue;
            }
            if(source is not null)_ownedSceneModelResolutions++;
            int state=-1;
            for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                if(ancestor.Root.RslFactoryStateId>=0){state=ancestor.Root.RslFactoryStateId;break;}
            //Prototype identity alone is not an occurrence: two authored
            //members may instantiate the same HLOD. Resolve its retained
            //parent/storage path, including inactive ordinary subtrees.
            var factory=PhysicalParticleFactory(instance);
            ulong generation=factory is null?(instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration):0;
            instance.PreparedSceneModel=RetainedInstanceSceneModel(instance.Object.RenderIdentity,generation,factory,state,instance);
            if(source is not null)
            {instance.SceneModelBound=true;source.SceneModelBindings.Add(instance);}
        }
        return (instance,level)=>
        {
            if(instance.Root.Hlod is not {} hlod)return;
            int previous=instance.HlodSelection?.LevelOrdinal??RememberedHlodLevel(instance,hlod);
            instance.HlodSelection=new(hlod.Name,level);
            RememberHlodLevel(instance,hlod,level);
            if(previous==level)return;
            //98E9A0's minimum clamp and every96F180 +10C/+110 call
            //remove the old ordinary children before adding the new level.
            //Even an upgrade immediately undone by the budget has side effects.
            if(instance.PreparedSceneModel is {} model)
            {
                bool Contains(LegoSceneNode node)=>ReferenceEquals(node.Value,model)||node.Children.Any(Contains);
                foreach(var scene in _legoScenes.Values)
                    if(scene.Tree is {} tree&&Contains(tree))RebuildLegoScene(scene,assets);
            }
        };
    }
}
