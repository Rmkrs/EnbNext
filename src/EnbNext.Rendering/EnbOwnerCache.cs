namespace EnbNext.Rendering;

/// <summary>Lazily computed data owned by the object it describes, held in a field on that
/// object (retail-style ownership) instead of a ConditionalWeakTable side table.
/// Same semantics as ConditionalWeakTable.GetValue: thread-safe, the factory may run more
/// than once under contention, exactly one value is published and returned to every caller,
/// and a factory exception publishes nothing.</summary>
internal static class OwnerCache
{
    public static TValue Get<TOwner, TValue>(ref TValue? slot, TOwner owner, Func<TOwner, TValue> create)
        where TValue : class
    {
        var value = Volatile.Read(ref slot);
        if (value is not null) return value;
        value = create(owner);
        return Interlocked.CompareExchange(ref slot, value, null) ?? value;
    }
}
