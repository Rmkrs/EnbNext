using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>006FD310, after SClientRunFrame's 006F5120/004A53A0
/// normalization. Revisions retain those frame-local changes across snapshots.</summary>
public sealed class EnbRetailDamageDecalUpdates
{
    private readonly ulong[] _poolRevisions = new ulong[3];
    private readonly ulong[][] _slotRevisions = [new ulong[15], new ulong[10], new ulong[5]];

    public bool HasChanges(IReadOnlyList<EnbSceneDamageDecalPoolSnapshot> pools)
    {
        for(int i=0;i<3&&i<pools.Count;i++)
            if(pools[i].State>1&&pools[i].Revision!=_poolRevisions[i])return true;
        return false;
    }

    public void Apply(IReadOnlyList<EnbSceneDamageDecalPoolSnapshot> pools,
        Action<int> clearPool, Action<int, int, EnbSceneDamageDecalSnapshot> updateSlot)
    {
        Span<bool> changed = stackalloc bool[3];
        changed.Clear();
        bool anyChanged=false;
        for (int i = 0; i < 3 && i < pools.Count; i++)
            anyChanged |= changed[i] = pools[i].State > 1 && pools[i].Revision != _poolRevisions[i];
        if (!anyChanged) return;
        for (int pool = 0; pool < 3; pool++)
        {
            if (!changed[pool]) { clearPool(pool); continue; }
            var current = pools[pool]; _poolRevisions[pool] = current.Revision;
            for (int slot = 0; slot < _slotRevisions[pool].Length && slot < current.Slots.Length; slot++)
            {
                var value = current.Slots[slot];
                if (value.State <= 1 || value.Revision == _slotRevisions[pool][slot]) continue;
                _slotRevisions[pool][slot] = value.Revision;
                updateSlot(pool, slot, value);
            }
        }
    }
}
