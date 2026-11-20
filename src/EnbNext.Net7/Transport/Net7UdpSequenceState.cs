namespace EnbNext.Net7.Transport;

/// <summary>
/// Per-UDP-channel outgoing normal sequence state.
///
/// Normal construction writes the current value and advances it immediately,
/// before socket success is known. Fixed-ID construction reuses the most recent
/// normal value and never advances the counter. Channel senders serialize frame
/// construction so this state represents causal builder order rather than the
/// legacy proxy's shared-buffer races.
/// </summary>
public sealed record Net7UdpSequenceSnapshot(uint Next,uint Last,bool HasLast);

public sealed class Net7UdpSequenceState
{
    private readonly object _sync = new();
    private uint _nextNormalSequence;
    private uint _lastNormalSequence;
    private bool _hasLastNormalSequence;

    public Net7UdpSequenceState(uint initialNormalSequence = 0)
    {
        _nextNormalSequence = initialNormalSequence;
    }

    public uint NextNormalSequence
    {
        get { lock (_sync) return _nextNormalSequence; }
    }

    public uint LastNormalSequence
    {
        get { lock (_sync) return _lastNormalSequence; }
    }

    public bool HasLastNormalSequence
    {
        get { lock (_sync) return _hasLastNormalSequence; }
    }

    public Net7UdpSequenceSnapshot Capture()
    {lock(_sync)return new(_nextNormalSequence,_lastNormalSequence,_hasLastNormalSequence);}
    internal void RestoreRecorded(Net7UdpSequenceSnapshot value)
    {
        lock(_sync)
        {
            if(_hasLastNormalSequence || _nextNormalSequence!=0)throw new InvalidOperationException("Replay sequence restore requires a fresh channel.");
            if(value.HasLast && value.Next!=unchecked(value.Last+1))throw new InvalidDataException("Invalid recorded sequence lineage.");
            _nextNormalSequence=value.Next;_lastNormalSequence=value.Last;_hasLastNormalSequence=value.HasLast;
        }
    }
    public uint AllocateNormalSequence()
    {
        lock (_sync)
        {
            uint sequence = _nextNormalSequence;
            _lastNormalSequence = sequence;
            _hasLastNormalSequence = true;
            _nextNormalSequence = unchecked(_nextNormalSequence + 1);
            return sequence;
        }
    }

    public uint GetFixedSendSequence()
    {
        lock (_sync)
        {
            if (!_hasLastNormalSequence)
            {
                throw new InvalidOperationException(
                    "Fixed-ID UDP send requires a previously constructed normal UDP packet on this channel.");
            }

            return _lastNormalSequence;
        }
    }
}
