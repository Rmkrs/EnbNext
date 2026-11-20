using EnbNext.Presentation;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private long _ownedDazzleEpoch;
    private readonly HashSet<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,object? Factory)> _compatDazzleRoots=[];
    private readonly HashSet<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,string Hlod,int Level,int Member,object? Factory)> _compatDazzleMembers=[];
    private sealed class OwnedDazzleRoot
    {
        public required (ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,object? Factory) Key;
        public required long Epoch;
        public required uint Created;
        public required bool IsRsl;
        public required string HlodName;
        public required EnbRetailDazzleHistoryState?[][] Levels;
        public required EnbRetailDazzleHistoryState?[] Direct;
        public readonly List<(ulong Owner,ulong Generation,string Path,EnbW3dRenderResourceKey Asset,string Hlod,int Level,int Member,object? Factory)> Members=[];
    }
    private OwnedDazzleRoot? BindOwnedDazzleRoot(LiveRootInstance instance)
    {
        if(_shipExhaustOwner is not null||instance.PreparedOwner?.SceneSource is not {} source)return null;
        if(instance.PreparedDazzleRoot is {} ready&&ready.Epoch==_ownedDazzleEpoch)return ready;
        var factory=PhysicalParticleFactory(instance);
        var local=(Generation:factory is not null?0:instance.IsRsl?instance.Object.RenderObjectGeneration:instance.Object.VisualGeneration,
            Path:RetainedModelOwnerPath(instance),Asset:instance.Root.Resource.Key,Factory:factory);
        if(!source.DazzleRoots.TryGetValue(local,out var bound))
        {
            var key=(instance.Object.RenderIdentity,local.Generation,local.Path,local.Asset,local.Factory);
            if(!_dazzleCreationClocks.TryGetValue(key,out uint created))_dazzleCreationClocks.Add(key,created=DazzleConstructionClock(instance));
            _compatDazzleRoots.Remove(key);
            var hlod=instance.Root.Hlod;
            bound=new(){Key=key,Epoch=_ownedDazzleEpoch,Created=created,IsRsl=instance.IsRsl,HlodName=hlod?.Name??"",
                Levels=hlod?.Levels.Select(level=>new EnbRetailDazzleHistoryState?[level.Members.Count]).ToArray()??[],
                Direct=new EnbRetailDazzleHistoryState?[instance.Root.Resource.ExternalRenderObjects.Count]};
            source.DazzleRoots.Add(local,bound);
        }
        return instance.PreparedDazzleRoot=bound;
    }
    private EnbRetailDazzleHistoryState RetainOwnedDazzleMember(OwnedDazzleRoot owner,LiveRootInstance instance,EnbW3dDazzleDrawInstance draw)
    {
        var slots=draw.HlodName.Length==0?owner.Direct:
            draw.HlodName==owner.HlodName&&(uint)draw.LevelOrdinal<(uint)owner.Levels.Length?owner.Levels[draw.LevelOrdinal]:null;
        if(slots is not null&&(uint)draw.MemberOrdinal<(uint)slots.Length&&slots[draw.MemberOrdinal] is {} ready)return ready;
        var root=owner.Key;var key=(root.Owner,root.Generation,root.Path,root.Asset,draw.HlodName,draw.LevelOrdinal,draw.MemberOrdinal,root.Factory);
        if(!_dazzleHistory.TryGetValue(key,out var history))_dazzleHistory.Add(key,history=new(owner.Created));
        if(!owner.Members.Contains(key))owner.Members.Add(key);
        _compatDazzleMembers.Remove(key);
        if(slots is not null&&(uint)draw.MemberOrdinal<(uint)slots.Length)slots[draw.MemberOrdinal]=history;
        return history;
    }
    private void ReleaseOwnedDazzleRoot(OwnedDazzleRoot root)
    {
        _dazzleCreationClocks.Remove(root.Key);
        foreach(var member in root.Members)_dazzleHistory.Remove(member);
    }
    private void DetachOwnedDazzles(SceneOwner source)
    {foreach(var root in source.DazzleRoots.Values)ReleaseOwnedDazzleRoot(root);source.DazzleRoots.Clear();}
    private void InvalidateOwnedDazzles(SceneOwner source,IEnbRenderObject next)
    {
        foreach(var entry in source.DazzleRoots.ToArray())
            if(entry.Key.Factory is null&&entry.Key.Generation!=(entry.Value.IsRsl?next.RenderObjectGeneration:next.VisualGeneration))
            {ReleaseOwnedDazzleRoot(entry.Value);source.DazzleRoots.Remove(entry.Key);}
    }
}
