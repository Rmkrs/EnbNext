using System.Collections.Immutable;
using EnbNext.Assets;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    // Prepared tasks retain this generation's context even after the source
    // releases it. Retirement must not erase a queued mapper clock.
    private sealed class OwnedMapperContext(LivePlanetGpuIdentity identity)
    {
        public readonly LivePlanetGpuIdentity PlanetIdentity=identity;
        public LivePlanetMapperStateOwnership? PlanetMapper;
        public System.Collections.Immutable.ImmutableDictionary<LivePlanetGpuIdentity,uint>? MapperOverrides;
        public System.Collections.Immutable.ImmutableDictionary<LivePlanetGpuIdentity,float>? PhaseOverrides;
        public uint? FrameMapperStartOverride;
        public float? FrameDayNightOverride;
        public void UpdateMapperContext(LiveFrame frame)
        {
            if(!ReferenceEquals(MapperOverrides,frame.MapperStartMillisecondsOverrides))
            {
                MapperOverrides=frame.MapperStartMillisecondsOverrides;
                FrameMapperStartOverride=MapperOverrides.Count!=0&&MapperOverrides.TryGetValue(PlanetIdentity,out var start)?start:null;
            }
            if(!ReferenceEquals(PhaseOverrides,frame.DayNightPhaseOverrides))
            {
                PhaseOverrides=frame.DayNightPhaseOverrides;
                FrameDayNightOverride=PhaseOverrides.Count!=0&&PhaseOverrides.TryGetValue(PlanetIdentity,out var phase)?phase:null;
            }
        }
    }
    private readonly List<SceneOwner> _ownedPlanetMappers=[];
    private ImmutableHashSet<LivePlanetGpuIdentity> _ownedPlanetMapperIdentities=ImmutableHashSet<LivePlanetGpuIdentity>.Empty;
    private long _ownedPlanetMapperRegistrations,_packetMapperLookups,_ordinaryPreparedMapperLookups;
    private readonly Dictionary<string,long> _packetMapperLookupSites=[];
    private void RetireOwnedPlanetMapper(SceneOwner source)
    {
        if(source.PlanetMapper is not {} mapper)return;
        _ownedPlanetMappers.Remove(source);
        _ownedPlanetMapperIdentities=_ownedPlanetMapperIdentities.Remove(source.PlanetIdentity);
        if(_planetMapperStateOwnership.TryGetValue(source.PlanetIdentity,out var current)&&ReferenceEquals(current,mapper))
            _planetMapperStateOwnership.Remove(source.PlanetIdentity);
        source.MapperContext=new(source.PlanetIdentity);
    }
    private void ClearOwnedPlanetMappers()
    {
        foreach(var source in _ownedPlanetMappers)source.MapperContext=new(source.PlanetIdentity);
        _ownedPlanetMappers.Clear();_ownedPlanetMapperIdentities=ImmutableHashSet<LivePlanetGpuIdentity>.Empty;
    }
    private ImmutableHashSet<LivePlanetGpuIdentity> UpdateOwnedPlanetMappers(LiveFrame frame,EnbRetailAssetBundle assets,IReadOnlyList<LiveRootInstance> roots)
    {
        // First materialization establishes the mapper clock before culling.
        // A later invisible frame does not relinquish its physical owner.
        foreach(var root in roots)
        {
            if(root.PreparedOwner?.SceneSource is not {PlanetEligible:true,PlanetMapper:null} source)continue;
            source.UpdateMapperContext(frame);
            var identity=source.PlanetIdentity;
            if(!_planetMapperStateOwnership.TryGetValue(identity,out var mapper))
                _planetMapperStateOwnership.Add(identity,mapper=new(unchecked((uint)Environment.TickCount)));
            source.PlanetMapper=mapper;_ownedPlanetMappers.Add(source);_ownedPlanetMapperRegistrations++;
            if(mapper.RuntimeSemanticsResolved)_ownedPlanetMapperIdentities=_ownedPlanetMapperIdentities.Add(identity);
        }
        foreach(var source in _ownedPlanetMappers)
        {
            var mapper=source.PlanetMapper!;
            bool wasResolved=mapper.RuntimeSemanticsResolved;
            UpdatePlanetMapperInputs(frame,assets,source,source.PlanetIdentity,mapper,source);
            if(wasResolved!=mapper.RuntimeSemanticsResolved)
                _ownedPlanetMapperIdentities=mapper.RuntimeSemanticsResolved?_ownedPlanetMapperIdentities.Add(source.PlanetIdentity):_ownedPlanetMapperIdentities.Remove(source.PlanetIdentity);
        }
        return _ownedPlanetMapperIdentities;
    }
}
