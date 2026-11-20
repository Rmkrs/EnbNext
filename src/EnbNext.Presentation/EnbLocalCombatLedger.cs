namespace EnbNext.Presentation;

/// <summary>One Local row's combat with the local ship: damage per second in its current fight and totals.</summary>
public readonly record struct EnbLocalCombatRow(double DealtPerSecond, double TakenPerSecond, double Dealt, double Taken, bool Fighting);

/// <summary>
/// Next Modern Local columns: the local character's own hits (the client combat journal, read by cursor) summed per
/// other object. A fight with an object starts at the first hit between the two and ends
/// after its pause (PauseSeconds; Local uses at least 30 s) with neither a hit nor the server listing the object as attacking us (the radar's attacker
/// flag, MarkAttacking): a mob that keeps attacking but lands a hit only now and then stays in one fight. The rate is the
/// total over all the time spent fighting that object (its ended fights plus the current one so far), so rate and total
/// always agree; it shows while a fight is on. EMP drains energy, not hull or
/// shields, so it is left out as in the meter's totals. Totals run until the object leaves Local or the sector changes;
/// a game id reused by a new object (another creation ordinal) starts over.
/// </summary>
public sealed class EnbLocalCombatLedger
{
    private struct Row
    {
        public uint GameId; public ulong Life;
        public double Dealt, Taken;
        /// <summary>Milliseconds of the ended fights with this object.</summary>
        public long FoughtBefore;
        /// <summary>The fight's start and its last sign of life (a hit, or the object listed as attacking us).</summary>
        public long FightStart, LastActive;
    }
    private readonly List<Row> _rows = [];
    private Guid _journal;
    private long _cursor;
    private uint _sector;
    public int PauseSeconds { get; set; } = 10;
    public int Count => _rows.Count;

    /// <summary>The next journal position to read from (0 for a journal not read before).</summary>
    public long Cursor(Guid journal) => journal == _journal ? _cursor : 0;

    public void Accept(EnbCombatBatch batch)
    {
        if (batch.Journal != _journal) { _journal = batch.Journal; _cursor = 0; _rows.Clear(); }
        foreach (var hit in batch.Hits)
        {
            if (hit.Sequence <= _cursor) continue;
            _cursor = hit.Sequence;
            if (!float.IsFinite(hit.Damage) || hit.Damage <= 0) continue;
            bool dealt = hit.Source == hit.Local, taken = hit.Victim == hit.Local;
            if (dealt == taken) continue; // Neither ours, or our own ship on both ends.
            if (hit.Sector != _sector) { _rows.Clear(); _sector = hit.Sector; }
            uint other = dealt ? hit.Victim : hit.Source;
            ulong life = dealt ? hit.VictimLife : hit.SourceLife;
            int index = Find(other);
            if (index < 0) { _rows.Add(new() { GameId = other, Life = life }); index = _rows.Count - 1; }
            var row = _rows[index];
            if (row.Life != life) row = new() { GameId = other, Life = life };
            Activate(ref row, hit.Time);
            if (hit.Type != 4)
            {
                if (dealt) row.Dealt += hit.Damage;
                else row.Taken += hit.Damage;
            }
            _rows[index] = row;
        }
        _cursor = Math.Max(_cursor, batch.Cursor);
    }

    /// <summary>The object's row at <paramref name="now"/>; rates only while its fight is on (zero time gives none).</summary>
    public bool TryRead(uint gameId, long now, out EnbLocalCombatRow result)
    {
        int index = Find(gameId);
        if (index < 0) { result = default; return false; }
        var row = _rows[index];
        bool fighting = now - row.LastActive < Pause;
        double seconds = fighting ? (row.FoughtBefore + now - row.FightStart) / 1000d : 0;
        result = new(seconds > 0 ? row.Dealt / seconds : 0, seconds > 0 ? row.Taken / seconds : 0, row.Dealt, row.Taken, fighting);
        return true;
    }

    /// <summary>The server lists the object as attacking us: its fight with us goes on (or starts again) at this time.</summary>
    public void MarkAttacking(uint gameId, long now)
    {
        int index = Find(gameId);
        if (index < 0) return;
        var row = _rows[index];
        Activate(ref row, now);
        _rows[index] = row;
    }

    private void Activate(ref Row row, long time)
    {
        if (row.LastActive == 0 || time - row.LastActive >= Pause)
        {
            if (row.LastActive != 0) row.FoughtBefore += row.LastActive - row.FightStart;
            row.FightStart = time;
        }
        row.LastActive = Math.Max(row.LastActive, time);
    }

    /// <summary>Drops the rows of objects no longer in Local.</summary>
    public void Retain(Predicate<uint> present)
    {
        for (int i = _rows.Count - 1; i >= 0; i--) if (!present(_rows[i].GameId)) _rows.RemoveAt(i);
    }

    private long Pause => Math.Max(1, PauseSeconds) * 1000L;
    private int Find(uint gameId)
    {
        for (int i = 0; i < _rows.Count; i++) if (_rows[i].GameId == gameId) return i;
        return -1;
    }
}
