namespace EnbNext.Rendering;

/// <summary>Trim unused immutable asset plans toward a residency target.
/// Live effects and in-flight loads may exceed it; it is not an admission limit.</summary>
public static class EnbEffectAssetCacheAdmission
{
    public static void TrimForAdmission<TKey,TValue>(Dictionary<TKey,Task<TValue>> cache,
        IReadOnlySet<TKey> required,int capacity) where TKey:notnull
    {
        if(capacity<=0)throw new ArgumentOutOfRangeException(nameof(capacity));
        if(cache.Count<capacity)return;
        foreach(var pair in cache.ToArray())
        {
            // Do not restart a worker still decoding, or disturb a plan needed
            // by another current/scheduled effect later in this same frame.
            if(!pair.Value.IsCompleted || required.Contains(pair.Key))continue;
            cache.Remove(pair.Key);
            if(cache.Count<capacity)return;
        }
    }
}
