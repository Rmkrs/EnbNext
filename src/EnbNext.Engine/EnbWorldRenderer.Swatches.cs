using System.Runtime.CompilerServices;
using EnbNext.Assets;
using EnbNext.Presentation;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbRetailAssetBundle? _swatchAssetNamespace;
    private EnbCurrentRetailSwatchCatalog? _swatchCatalog;
    // 0091D4B0 resolves through the asset manager, independently of a view/GPU owner.
    private EnbW3dRenderResourceCache _prototypeResources = new();

    //769FCA enables deferred texture construction after login. Neither world
    //prewarm nor worker preparation is a native synchronous-init phase.
    private EnbCurrentRetailW3dFilePublicationContext CreateLiveWorldPublicationContext(uint clock)
        => _prototypeResources.CreatePublicationContext(clock);

    private void EnsureSwatchCatalog(EnbRetailAssetBundle assets)
    {
        if (_sharedMaterialOwner is {} owner)
        {
            _swatchAssetNamespace = assets;
            _prototypeResources = owner._prototypeResources;
            _swatchCatalog = owner._swatchCatalog;
            return;
        }
        if (ReferenceEquals(_swatchAssetNamespace, assets) && _swatchTextureRevision==assets.TextureRevision) return;
        _swatchTextureRevision=assets.TextureRevision;_swatchAssetNamespace = assets;
        _prototypeResources = EnbW3dRenderResourceCache.ForAssetNamespace(assets);
        var prototypes=_prototypeResources;
        _swatchCatalog = assets.Decals is {} definitions && assets.MixResources is {} resources
            ? EnbRenderSession.Of(assets).Decals(()=>EnbCurrentRetailSwatchCatalog.Load(definitions, resources, ReadMapperClock(), prototypes,assets.FileResources))
            : null;
    }

    private long _swatchLayoutPreparations,_swatchTextureRevision;
    private sealed class PreparedSwatchLayout(long revision,int count,PreparedSwatchGroup[] groups,bool contiguous)
    {
        public readonly long Revision=revision;
        public readonly int Count=count;
        public readonly PreparedSwatchGroup[] Groups=groups;
        public readonly bool Contiguous=contiguous;
    }
    private sealed class PreparedSwatchGroup(int[] indices,int? slot) : IReadOnlyList<EnbW3dDrawTask>
    {
        public readonly int[] Indices=indices;
        public readonly int? Slot=slot;
        public List<EnbW3dDrawTask>? Packets;
        public int Start;
        public int Count=>Indices.Length;
        public EnbW3dDrawTask this[int index]=>Packets![Start+Indices[index]];
        public IEnumerator<EnbW3dDrawTask> GetEnumerator(){for(int i=0;i<Count;i++)yield return this[i];}
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }
    private static PreparedSwatchLayout PrepareSwatchLayout(List<EnbW3dDrawTask> packets,int start,long revision)
    {
        var indices=new Dictionary<(EnbW3dRenderResourceKey,int,int,string),int>();
        var groups=new List<(List<int> Indices,int? Slot)>();
        for(int i=start;i<packets.Count;i++)
        {
            var packet=packets[i];
            var key=(packet.OrderKey.ResourceKey,packet.OrderKey.MeshOrdinal,
                packet.OrderKey.BindingOrdinal-packet.Material.Source.Ordinal,packet.RenderInstancePath);
            if(!indices.TryGetValue(key,out int group))
            {
                indices.Add(key,group=groups.Count);
                groups.Add(([],EnbCurrentRetailShipDecalRuntime.TryParseSlot(packet.RenderObjectName,out int slot)?slot:null));
            }
            groups[group].Indices.Add(i-start);
        }
        int expected=0;bool contiguous=true;
        var prepared=new PreparedSwatchGroup[groups.Count];
        for(int g=0;g<groups.Count;g++)
        {
            var group=groups[g];
            foreach(int index in group.Indices)if(index!=expected++)contiguous=false;
            prepared[g]=new(group.Indices.ToArray(),group.Slot);
        }
        return new(revision,packets.Count-start,prepared,contiguous);
    }
    private int ApplyOwnedSwatchGroups(List<EnbW3dDrawTask> packets,int start,LiveRootInstance instance)
    {
        if(!_retainedOrdinaryMeshes)return ApplySwatchGroups(packets,start,instance.Object);
        if(_swatchCatalog is null || start==packets.Count)return 0;
        var layout=instance.SwatchLayout;
        // Material preparation reports an actual binding/context change. Pose,
        // lights and clock values never rebuild the descriptor grouping.
        if(layout is null || layout.Revision!=instance.SurfaceLayoutRevision || layout.Count!=packets.Count-start)
        {instance.SwatchLayout=layout=PrepareSwatchLayout(packets,start,instance.SurfaceLayoutRevision);_swatchLayoutPreparations++;}
        return ApplyPreparedSwatchGroups(packets,start,instance.Object,layout);
    }
    private int ApplyPreparedSwatchGroups(List<EnbW3dDrawTask> packets,int start,IEnbRenderObject obj,PreparedSwatchLayout layout)
    {
        if(layout.Contiguous && obj.Decals.IsEmpty)return 0;
        int applied=0;bool replacing=!layout.Contiguous;
        var replacement=_swatchReplacement;replacement.Clear();
        try
        {
            foreach(var group in layout.Groups)
            {
                int? decalId=null;
                if(group.Slot is {} slot)
                    foreach(var entry in obj.Decals)
                        if(Math.Min(entry.Field0,15u)==(uint)slot)decalId=unchecked((int)entry.Field1);
                group.Packets=packets;group.Start=start;
                try
                {
                    if(decalId is <0 && _swatchCatalog!.TryGet(decalId.Value,out var descriptor,out _) &&
                        _shipMaterials.TryApplySwatch(group,descriptor,obj.DecalApplyCount,out var effective,out _))
                    {
                        if(!replacing)
                        {
                            // All preceding groups were unchanged and contiguous.
                            for(int i=start;i<start+group.Indices[0];i++)replacement.Add(packets[i]);
                            replacing=true;
                        }
                        replacement.AddRange(effective);applied+=group.Count;
                    }
                    else if(replacing)
                        foreach(int index in group.Indices)replacement.Add(packets[start+index]);
                }
                finally{group.Packets=null;group.Start=0;}
            }
            if(replacing){packets.RemoveRange(start,packets.Count-start);packets.AddRange(replacement);}
            return applied;
        }
        finally{replacement.Clear();}
    }

    private readonly EnbFrameGroups<(EnbW3dRenderResourceKey Resource,int Mesh,int Binding,string Path),EnbW3dDrawTask> _swatchGroups=new();
    private readonly List<EnbW3dDrawTask> _swatchReplacement=[];

    private int ApplySwatchGroups(List<EnbW3dDrawTask> packets, int start,
        IEnbRenderObject obj)
    {
        if (_swatchCatalog is null || start == packets.Count) return 0;
        int applied = 0;
        var replacement=_swatchReplacement;
        replacement.Clear();
        _swatchGroups.Clear();
        try
        {
        for(int index=start;index<packets.Count;index++)
        {
            var packet=packets[index];
            _swatchGroups.Add((packet.OrderKey.ResourceKey,packet.OrderKey.MeshOrdinal,
                packet.OrderKey.BindingOrdinal-packet.Material.Source.Ordinal,packet.RenderInstancePath),packet);
        }
        for(int groupIndex=0;groupIndex<_swatchGroups.Count;groupIndex++)
        {
            var target=_swatchGroups.Values(groupIndex);
            var first = target[0];
            int? decalId = null;
            if (EnbCurrentRetailShipDecalRuntime.TryParseSlot(first.RenderObjectName, out int slot))
                foreach (var entry in obj.Decals)
                    if (Math.Min(entry.Field0, 15u) == (uint)slot)
                        decalId = unchecked((int)entry.Field1);
            if (decalId is < 0 && _swatchCatalog.TryGet(decalId.Value, out var descriptor, out _) &&
                _shipMaterials.TryApplySwatch(target, descriptor, obj.DecalApplyCount,
                    out var effective, out _))
            {
                replacement.AddRange(effective);
                applied += target.Count;
            }
            else replacement.AddRange(target);
        }
        packets.RemoveRange(start, packets.Count - start);
        packets.AddRange(replacement);
        return applied;
        }
        finally {replacement.Clear();_swatchGroups.Clear();}
    }
}
