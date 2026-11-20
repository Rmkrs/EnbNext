namespace EnbNext.Rendering;

/// <summary>00960D40 queue and00960F10 live-slot lifetime. Motion and property consumers use the retained slots.</summary>
public sealed class EnbRetailParticleBufferRing
{
    public readonly record struct State(int QueueRead, int QueueWrite, int Queued, int Oldest,
        int ExistingEnd, int WriteEnd, int ExistingCount, int NewCount, uint Clock);
    public readonly record struct Particle(int Slot, EnbCurrentRetailParticleBirth Birth, uint StoredTime);
    private readonly EnbCurrentRetailParticleBirth[] _queue, _particles;
    private readonly uint[] _times;
    private readonly uint _lifetime;
    private int _queueRead, _queueWrite, _queued, _oldest, _existingEnd, _writeEnd, _existingCount, _newCount;
    private uint _clock;
    internal ulong LiveRevision { get; private set; }
    public State CurrentState => new(_queueRead, _queueWrite, _queued, _oldest, _existingEnd, _writeEnd, _existingCount, _newCount, _clock);
    public uint StoredTime(int slot) => _times[slot];
    public int CountLivingRecords(uint clock)
    {
        int count=0;
        for(int i=0;i<_existingCount;i++)
            if(unchecked(clock-GetLiveParticle(i).StoredTime)<_lifetime)count++;
        for(int i=0,slot=_queueRead;i<_queued;i++,slot=Next(slot))
            if(unchecked(clock-(uint)_queue[slot].Time)<_lifetime)count++;
        return Math.Min(_particles.Length,count);
    }

    public EnbRetailParticleBufferRing(int capacity, uint lifetime, uint initialClock)
    {
        if (capacity <= 0) throw new InvalidDataException("Particle ring requires allocated slots");
        _queue = new EnbCurrentRetailParticleBirth[capacity];
        _particles = new EnbCurrentRetailParticleBirth[capacity];
        _times = new uint[capacity];
        _lifetime = lifetime;
        _clock = initialClock;
    }

    public void Enqueue(EnbCurrentRetailParticleBirth birth)
    {
        int slot = _queueWrite;
        _queueWrite = Next(_queueWrite);
        _queued++;
        if (_queued == _queue.Length + 1)
        {
            _queueRead = Next(_queueRead);
            _queued--;
        }
        _queue[slot] = birth;
    }

    public int LiveParticleCount => _existingCount;

    public Particle GetLiveParticle(int index)
    {
        if((uint)index>=(uint)_existingCount)throw new ArgumentOutOfRangeException(nameof(index));
        int slot=_oldest+index;
        if(slot>=_particles.Length)slot-=_particles.Length;
        return new(slot,_particles[slot],_times[slot]);
    }

    public IReadOnlyList<Particle> LiveParticles
    {
        get
        {
            var result=new Particle[_existingCount];
            for(int i=0;i<result.Length;i++)result[i]=GetLiveParticle(i);
            return result;
        }
    }

    public void Advance(uint clock, Action<int, EnbCurrentRetailParticleBirth, uint>? initialize = null,
        Action<int, EnbCurrentRetailParticleBirth, uint>? advanceExisting = null)
    {
        uint delta = unchecked(clock - _clock);
        if (delta == 0) return;
        // Queuing alone does not publish births. Every actual buffer update
        // can change slots or stored times, including expired queue entries.
        LiveRevision++;
        while (_queued != 0)
        {
            EnbCurrentRetailParticleBirth birth = _queue[_queueRead];
            _queueRead = Next(_queueRead);
            _queued--;
            //00962070 writes this timestamp even when the queued particle has
            //already expired. A full live ring can observe that overwrite.
            _times[_writeEnd] = unchecked((uint)birth.Time);
            uint age = unchecked(clock - _times[_writeEnd]);
            if (age >= _lifetime) continue;
            int slot = _writeEnd;
            _particles[slot] = birth;
            initialize?.Invoke(slot, birth, age);
            _writeEnd = Next(_writeEnd);
            int before = _newCount++;
            if (_existingCount + _newCount == _particles.Length + 1)
            {
                _oldest = Next(_oldest);
                _existingCount--;
                if (_existingCount == -1)
                {
                    _existingEnd = Next(_existingEnd);
                    _existingCount = 0;
                    _newCount = before;
                }
            }
        }
        ExpireExisting(clock);
        if (_existingCount > 0 && advanceExisting is not null)
            foreach (var particle in LiveParticles) advanceExisting(particle.Slot, particle.Birth, delta);
        _existingEnd = _writeEnd;
        _existingCount += _newCount;
        _newCount = 0;
        _clock = clock;
    }

    private int Next(int slot) => slot + 1 == _particles.Length ? 0 : slot + 1;

    private void ExpireExisting(uint clock)
    {
        int firstEnd = _existingEnd, secondStart = _existingEnd;
        if (_existingEnd <= _oldest && (_oldest != _existingEnd || _existingCount != 0))
        { firstEnd = _particles.Length; secondStart = 0; }
        int slot = _oldest;
        while (slot < firstEnd)
        {
            if (unchecked(clock - _times[slot]) < _lifetime) { _oldest = slot; return; }
            slot++; _existingCount--;
        }
        slot = secondStart;
        while (slot < _existingEnd)
        {
            if (unchecked(clock - _times[slot]) < _lifetime) break;
            slot++; _existingCount--;
        }
        _oldest = slot;
    }
}
