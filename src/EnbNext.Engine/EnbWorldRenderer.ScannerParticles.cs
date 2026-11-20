namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private readonly HashSet<RetainedEmitterKey> _scannerParticleMembers=[];
    private readonly HashSet<(ulong Owner,ulong Generation,string Path,object? PhysicalFactory,object? BoneFactory)> _scannerObjectMembers=[];

    private void PrepareScannerParticleMembers(RootTaskList roots,EnbWorldRenderer source)
    {
        _scannerParticleMembers.Clear();_scannerObjectMembers.Clear();
        //4800A0/480A40 enumerate the current RenderObj tree, including all
        //HLOD levels. Independent buffers of removed models are not children.
        foreach(var instance in roots.Concat(_hiddenRslEmitterRoots).Concat(_hiddenCombatSourceRoots).Distinct())
        {
            var retained=TryResolveScannerPrimitiveSource(instance,source,out var shared)?shared:instance;
            var member=CompositeMembershipKey(retained);
            _scannerObjectMembers.Add((member.Owner,member.Generation,member.Path,member.PhysicalFactory,member.BoneFactory));
            if(retained.Root.RetainedEmitter is null ||
                !TryResolvePrimitiveFactoryTransform(retained,out _,out float scale,out var hsv))continue;
            var key=RetainedEmitterOwnerKey(retained,scale,hsv);
            if(source._retainedEmitters.ContainsKey(key) && !source._removedRetainedEmitters.Contains(key))
                _scannerParticleMembers.Add(key);
        }
    }

    private bool ScannerContainsParent(RetainedEmitterKey parent)
        =>_scannerObjectMembers.Contains((parent.Owner,parent.Generation,parent.Path,parent.PhysicalFactory,parent.BoneFactory));
}
