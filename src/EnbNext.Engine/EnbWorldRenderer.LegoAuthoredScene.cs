using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private static LegoPhysicalModel AuthoredLegoSceneChild(LegoPhysicalModel parent,int ordinal,LiveCpuRoot root)
    {
        if(!parent.AuthoredChildren.TryGetValue(ordinal,out var child)||
            !ReferenceEquals(child.Root.PrimitiveFactoryIdentity,root.PrimitiveFactoryIdentity))
        {
            parent.AuthoredChildren[ordinal]=child=new(root);
            parent.InvalidateScenes();
        }
        return child;
    }

    private AuthoredLegoSceneChildEnumerator AuthoredLegoSceneChildren(LegoPhysicalModel parent,LegoSceneState? scene=null)
        => new(this,parent,scene);

    // Value traversal keeps the original lazy order: building one child can
    // publish state consumed when the next child is resolved.
    private struct AuthoredLegoSceneChildEnumerator(EnbWorldRenderer view,LegoPhysicalModel parent,LegoSceneState? scene)
    {
        private readonly LiveCpuRoot root=parent.Root;
        private RetainedCompositeMembership? membership;
        private LiveRslBoneNode[]? ordinary,aggregate;
        private int phase,index,level;
        public LegoPhysicalObject Current {get;private set;}=null!;
        public AuthoredLegoSceneChildEnumerator GetEnumerator()=>this;
        public bool MoveNext()
        {
            if(phase==0)
            {
                if(root.RslBoneNode is {} bone)
                {
                    int boneLevel=SelectedLevel();
                    var members=scene is null?BoneMembership(bone):
                        scene.BoneMembership.GetValueOrDefault(bone)??BoneMembership(bone,initial:true);
                    ordinary=boneLevel>=0&&boneLevel<members.Levels.Length?members.Levels[boneLevel]:[];
                    aggregate=members.Aggregate;phase=4;
                }
                else
                {
                    if(parent.SceneInstance is {} instance)
                        membership=view.CompositeMembership(instance);
                    phase=1;
                }
            }
            if(phase==4)
            {
                if(index<ordinary!.Length){Current=BoneLegoSceneModel(ordinary[index++]);return true;}
                phase=5;index=0;
            }
            if(phase==5)
            {
                if(index<aggregate!.Length){Current=BoneLegoSceneModel(aggregate[index++]);return true;}
                phase=3;return false;
            }
            if(phase==1)
            {
                while(index<root.CollectionChildren.Count)
                {
                    int ordinal=index++;
                    if(membership?.Removed.Contains(ordinal)==true)continue;
                    Current=AuthoredLegoSceneChild(parent,ordinal,Resolve(ordinal,root.CollectionChildren[ordinal]));return true;
                }
                level=SelectedLevel();phase=2;index=0;
            }
            if(phase==2)
            {
                while(index<root.HmodelChildren.Count)
                {
                    var child=root.HmodelChildren[index++];
                    if((child.LevelOrdinal is null||child.LevelOrdinal==level)&&membership?.Removed.Contains(child.StorageOrdinal)!=true)
                    {Current=AuthoredLegoSceneChild(parent,child.StorageOrdinal,Resolve(child.StorageOrdinal,child.Root));return true;}
                }
                phase=3;
            }
            return false;
        }
        private LiveCpuRoot Resolve(int ordinal,LiveCpuRoot original)
            =>membership?.Replacements.GetValueOrDefault(ordinal,original)??original;
        private int SelectedLevel()
        {
            int selected=root.Hlod is {} hlod?EnbW3dHlodBudgetCompiler.CurrentRetailInitialLevel(hlod):0;
            if(parent.SceneInstance is {} instance&&root.Hlod is {} current)
                selected=instance.HlodSelection?.LevelOrdinal??view.RememberedHlodLevel(instance,current);
            return selected;
        }
    }

    private static LegoPhysicalModel BoneLegoSceneModel(LiveRslBoneNode node)
    {
        var root=node.Exported??node.Root with {RslBoneNode=node};
        var model=node.SceneModel??=new(root);
        model.Root=root;
        return model;
    }
}
