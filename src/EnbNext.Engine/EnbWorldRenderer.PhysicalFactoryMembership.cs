using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private sealed class PhysicalFactoryLifetime {public int RetainedReferences;public readonly List<CompositeOccurrence> CompositeOccurrences=[];public readonly List<ProjectedDecalReceiver> DecalReceivers=[];}
    private sealed class PhysicalFactoryMembership
    {
        private readonly Dictionary<object,int> retained=new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<object> temporary=new(ReferenceEqualityComparer.Instance);
        private bool useRetained;
        public void Attach(object value)
        {
            retained.TryGetValue(value,out int references);retained[value]=references+1;
            if(value is PhysicalFactoryLifetime lifetime)lifetime.RetainedReferences++;
        }
        public void Detach(object value)
        {
            if(!retained.TryGetValue(value,out int references))return;
            if(references==1)retained.Remove(value);else retained[value]=references-1;
            if(value is PhysicalFactoryLifetime lifetime && --lifetime.RetainedReferences==0)
            {
                foreach(var receiver in lifetime.DecalReceivers)receiver.Retire();
                lifetime.DecalReceivers.Clear();
                while(lifetime.CompositeOccurrences.Count!=0)
                {var occurrence=lifetime.CompositeOccurrences[^1];occurrence.View.RetireCompositeOccurrence(occurrence);}
            }
        }
        public void BeginFrame(bool enabled){useRetained=enabled;temporary.Clear();}
        public void Add(object value){if(!useRetained||!retained.ContainsKey(value))temporary.Add(value);}
        public bool Contains(object value)=>(useRetained&&retained.ContainsKey(value))||temporary.Contains(value);
        public void Clear()
        {
            foreach(var pair in retained)if(pair.Key is PhysicalFactoryLifetime lifetime)lifetime.RetainedReferences-=pair.Value;
            retained.Clear();temporary.Clear();
        }
    }
    private void BindBasePhysicalLifetime(SceneOwner source)
    {
        if(source.BasePhysicalFactory is not null||source.Factory?.Root?.PhysicalFactoryOwner is not {} factory)return;
        source.BasePhysicalFactory=factory;_livePhysicalParticleFactories.Attach(factory);
    }
    private void DetachBasePhysicalLifetime(SceneOwner source)
    {
        if(source.BasePhysicalFactory is {} factory)_livePhysicalParticleFactories.Detach(factory);
        source.BasePhysicalFactory=null;
    }
    private void BindLegoPhysicalLifetime(LegoFactoryBinding binding)
    {
        if(binding.Source is null||binding.PhysicalLifetime is not null||binding.Particle?.PhysicalFactoryOwner is not {} factory)return;
        binding.PhysicalLifetime=factory;_livePhysicalParticleFactories.Attach(factory);
    }
    private void DetachLegoPhysicalLifetime(LegoFactoryBinding binding)
    {
        if(binding.PhysicalLifetime is {} factory)_livePhysicalParticleFactories.Detach(factory);
        binding.PhysicalLifetime=null;
    }
    private void ResetPhysicalFactoryMembership()
    {
        _livePhysicalParticleFactories.Clear();
        if(_ownedScene is not null)foreach(var node in _ownedScene.ActiveNodes)
            if(node.State.BasePhysicalFactory is {} factory)_livePhysicalParticleFactories.Attach(factory);
        foreach(var binding in _legoFactoryBindings.Values)
            if(binding.PhysicalLifetime is {} factory)_livePhysicalParticleFactories.Attach(factory);
    }
}
