namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    // 5C2F00 -> 5C2920 queries the current model after callbacks, not its
    // animated draw packets. Callbacks can invalidate the prepared detail bound.
    private EnbNext.Rendering.EnbW3dWorldBounds? ResolveScannerRootSphere(LiveRootInstance instance)
    {
        if(instance.CurrentRetailWorldSphere is {} prepared)return prepared;
        if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out _,out _))return null;
        if(instance.Root.Hlod is {} hlod)
            return TryResolveHlodCompositeBounds(instance,hlod,world,instance.AnimationBlend?.ResolvedSamples ?? [],out var bounds,out _)?bounds.WorldSphere:null;
        if(!TryComputeRootLocalBounds(instance.RetainedQueryRoot ?? CreateConfiguredBoundsRoot(instance.Root,1),out var local,out _))return null;
        var stored=EnbNext.Rendering.EnbCurrentRetailRenderBoundsStorage.Calculate(world,local.Sphere,local.BoxCenter,local.BoxExtent);
        return new(new(stored.Sphere.X,stored.Sphere.Y,stored.Sphere.Z),stored.Sphere.W);
    }
    private bool RetainsPhysicalFactory(object factory)=>_livePhysicalParticleFactories.Contains(factory) ||
        EnumerateOrdinaryFactories().Any(owner=>ReferenceEquals(owner.Root?.PhysicalFactoryOwner,factory)) ||
        _legoParticleFactories.Values.Any(root=>ReferenceEquals(root.PhysicalFactoryOwner,factory)) ||
        _influenceHelperFactories.Values.Any(helper=>ReferenceEquals(helper.Root.PhysicalFactoryOwner,factory));

    private static bool ScannerSourceRetainsFactory(LiveRootInstance instance,EnbWorldRenderer source)
    {
        //5C2F22 reads the retained GameObj+AC, not a previously submitted draw.
        //The first secondary draw can therefore create history on that owner.
        if(PhysicalParticleFactory(instance) is {} factory)
            return source.RetainsPhysicalFactory(factory);
        if(instance.IsRsl)
            for(var ancestor=instance;ancestor is not null;ancestor=ancestor.Parent)
                if(source._rslConfiguredRoots.TryGetValue((instance.Object.RenderIdentity,instance.Object.RenderObjectGeneration,
                    ancestor.Root.RslFactoryStateId),out var root) && ReferenceEquals(root,ancestor.Root))return true;
        return false;
    }

    //5C2F00 retains GameObj+AC;4800A0 draws that object rather than cloning it.
    //90C4D0/911B30 call90D6E0/912680 on every admitted draw, consuming the
    //global frame delta again. Share sampled values, clocks and detail, while
    //the view retains its own placement/admission records.
    private static bool TryResolveScannerPrimitiveSource(LiveRootInstance instance,
        EnbWorldRenderer source,out LiveRootInstance retained)
    {
        var member=CompositeMembershipKey(instance);
        if(source._retainedCompositeFrameRoots.TryGetValue((member.Owner,member.Generation,member.Path,member.PhysicalFactory,member.BoneFactory),out var slot) &&
            slot.Epoch==source._compositeFrameEpoch && slot.Current is {} shared &&
            shared.Root.Resource.Key==instance.Root.Resource.Key &&
            shared.Root.RootRenderObjectName==instance.Root.RootRenderObjectName &&
            ReferenceEquals(PhysicalParticleFactory(instance),PhysicalParticleFactory(shared)))
        {retained=shared;return true;}
        retained=null!;return false;
    }
}
