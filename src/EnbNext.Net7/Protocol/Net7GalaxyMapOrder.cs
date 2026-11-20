namespace EnbNext.Net7.Protocol;

/// <summary>Observable traversal order of the galaxy's retained hash tables.
/// 00835DD0 requests 100 buckets; 0083AAA0/007AED00 round to 193.
/// Insertions prepend; 0083C0A0/007B2700 rehash by moving each old head.
/// This order affects the first system child and shared label consumers.</summary>
public sealed record Net7GalaxyMapOrder(uint BucketCount, IReadOnlyList<uint> Keys)
{
    private static readonly uint[] Primes = [53,97,193,389,769,1543,3079,6151,12289,24593,49157,
        98317,196613,393241,786433,1572869,3145739,6291469,12582917,25165843,50331653,
        100663319,201326611,402653189,805306457,1610612741,3221225473,4294967291];
    public static Net7GalaxyMapOrder Empty { get; } = new(193,Array.Empty<uint>());

    public Net7GalaxyMapOrder Retain(IEnumerable<uint> orderedKeys)
    {
        var incoming=orderedKeys.ToArray();
        var admitted=incoming.ToHashSet();
        uint count=BucketCount;
        var buckets=new SortedDictionary<uint,List<uint>>();
        var retained=new HashSet<uint>();
        foreach(uint key in Keys)
            if(admitted.Contains(key))
            {
                if(!buckets.TryGetValue(key%count,out var chain)) buckets[key%count]=chain=[];
                chain.Add(key); retained.Add(key);
            }
        foreach(uint key in incoming)
        {
            if(!retained.Add(key)) continue;
            if(retained.Count>count)
            {
                uint next=Primes.First(p=>p>=retained.Count);
                var grown=new SortedDictionary<uint,List<uint>>();
                foreach(uint old in buckets.Values.SelectMany(c=>c))
                {
                    if(!grown.TryGetValue(old%next,out var chain)) grown[old%next]=chain=[];
                    chain.Insert(0,old);
                }
                buckets=grown; count=next;
            }
            if(!buckets.TryGetValue(key%count,out var target)) buckets[key%count]=target=[];
            target.Insert(0,key);
        }
        return new(count,buckets.Values.SelectMany(c=>c).ToArray());
    }
}
