using EnbNext.Rendering;

namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // Slots follow the RSL runtime's retained binding order. Only its mutation
    // notifications replace this plan; animation frame samples do not own it.
    private sealed class RetainedRslRootPlan(int count)
    {
        public readonly RetainedRslRootSlot[] Slots=Enumerable.Range(0,count+1)
            .Select(_=>new RetainedRslRootSlot()).ToArray();
        public LiveRslAnimationBinding?[] AnimationSlots=[];
        public RslAnimationEvaluationOwner[] AnimationResults=[];
        private bool membershipChanged;
        private LiveCpuRoot? blendRoot;
        private LiveRslAnimationBlendBinding? blend;
        public void PrepareAnimations(int count)
        {if(AnimationSlots.Length==count)return;AnimationSlots=new LiveRslAnimationBinding?[count];AnimationResults=new RslAnimationEvaluationOwner[count];for(int i=0;i<count;i++)AnimationResults[i]=new();membershipChanged=true;}
        public void SetAnimation(int index,LiveRslAnimationBinding? binding)
        {if(ReferenceEquals(AnimationSlots[index],binding))return;AnimationSlots[index]=binding;membershipChanged=true;}
        public LiveRslAnimationBlendBinding ReadBlend(LiveCpuRoot root)
        {
            if(blend is null||membershipChanged||!ReferenceEquals(blendRoot,root))
            {
                var members=new List<LiveRslAnimationBinding>();
                foreach(var binding in AnimationSlots)if(binding is not null)members.Add(binding);
                blend=CreateRslAnimationBlend(root,members);blendRoot=root;membershipChanged=false;
            }
            return blend;
        }

    }
    private sealed class RetainedRslRootSlot
    {
        public LiveCpuRoot? Root,WithBones;
    }
}
