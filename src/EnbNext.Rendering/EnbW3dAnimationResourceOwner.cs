namespace EnbNext.Rendering;

/// <summary>Owns the structural animation-library view of a render resource.
/// Sampling frames and weights do not change its mesh/material/hierarchy identity.</summary>
public sealed class EnbW3dAnimationResourceOwner
{
    private sealed record Publication(EnbW3dRenderResource Source,EnbW3dRenderResource Resolved,
        EnbW3dRenderHierarchyAnimation[] Animations)
    {
        public bool Matches(EnbW3dRenderResource source,IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> samples)
        {
            if(!ReferenceEquals(Source,source) || Animations.Length!=samples.Count)return false;
            for(int i=0;i<samples.Count;i++)if(!ReferenceEquals(Animations[i],samples[i].Animation))return false;
            return true;
        }
    }
    private readonly object _gate=new();
    private Publication? _published;
    public EnbW3dRenderResource Resolve(EnbW3dRenderResource source,
        IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> samples)
    {
        var state=Volatile.Read(ref _published);
        if(state is not null && state.Matches(source,samples))return state.Resolved;
        lock(_gate)
        {
            state=Volatile.Read(ref _published);
            if(state is not null && state.Matches(source,samples))return state.Resolved;
            List<EnbW3dRenderHierarchyAnimation>? expanded=null;
            foreach(var sample in samples)
            {
                var animation=sample.Animation;
                bool present=false;
                foreach(var candidate in expanded is null?source.Animations:expanded)
                    if(string.Equals(candidate.Name,animation.Name,StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(candidate.HierarchyName,animation.HierarchyName,StringComparison.OrdinalIgnoreCase))
                    {present=true;break;}
                if(!present)(expanded??=source.Animations.ToList()).Add(animation);
            }
            var animations=new EnbW3dRenderHierarchyAnimation[samples.Count];
            for(int i=0;i<samples.Count;i++)animations[i]=samples[i].Animation;
            var resolved=expanded is null?source:source.Copy().SetAnimations(expanded.ToArray());
            Volatile.Write(ref _published,new(source,resolved,animations));
            return resolved;
        }
    }
}
