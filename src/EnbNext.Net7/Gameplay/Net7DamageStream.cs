using System.Collections.Immutable;

namespace EnbNext.Net7.Gameplay;

public readonly record struct Net7DamageEvent(long Sequence, uint ClientTime, Net7DamagePresentation Damage);

/// <summary>Ordered network-to-render handoff. Only the producer appends; the
/// render owner acknowledges after materializing every entry, including bursts.</summary>
public sealed class Net7DamageStream
{
    private ImmutableList<Net7DamageEvent> _pending;
    private long _sequence, _acknowledged;
    public ImmutableList<Net7DamageEvent> Pending => Volatile.Read(ref _pending);

    public Net7DamageStream(ImmutableList<Net7DamageEvent>? pending = null)
    {
        _pending = pending ?? [];
        _sequence = _pending.Count == 0 ? 0 : _pending[^1].Sequence;
    }

    public void Append(Net7DamagePresentation damage, uint clientTime)
    {
        var pending = _pending;
        long acknowledged = Volatile.Read(ref _acknowledged);
        int consumed = 0;
        while (consumed < pending.Count && pending[consumed].Sequence <= acknowledged) consumed++;
        if (consumed != 0) pending = pending.RemoveRange(0, consumed);
        Volatile.Write(ref _pending, pending.Add(new(++_sequence, clientTime, damage)));
    }

    public void Acknowledge(long sequence) => Interlocked.Exchange(ref _acknowledged, sequence);

    public Net7DamageStream Capture()
    {
        var pending = Pending;
        return new(pending);
    }
}
