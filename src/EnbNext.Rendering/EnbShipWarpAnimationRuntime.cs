using EnbNext.Presentation;

namespace EnbNext.Rendering;

public sealed record EnbShipWarpAnimation(ulong Sequence, int Suffix, uint StartTime);

/// <summary>006FBEF0 -> 00810540: recursively play suffix 1/2 once, starting at frame zero.</summary>
public sealed class EnbShipWarpAnimationRuntime
{
    private readonly Dictionary<ulong, Owner> _owners = [];
    public EnbShipWarpAnimation? Get(ulong id) => _owners.GetValueOrDefault(id)?.Animation;
    public void Clear() => _owners.Clear();
    public void Update(IReadOnlyList<IEnbRenderObject> objects, uint now)
    {
        var retained = new HashSet<ulong>();
        foreach (var obj in objects)
        {
            if (obj.ObjectType != 1 || obj.ShipPresentation is not { } ship || !retained.Add(obj.RenderIdentity)) continue;
            if (!_owners.TryGetValue(obj.RenderIdentity, out var owner))
                _owners.Add(obj.RenderIdentity, owner = new() { Sequence = ship.WarpChanges.Length > 1 ? ship.WarpChanges[^2].Sequence : 0 });
            foreach (var change in ship.WarpChanges)
            {
                if (change.Sequence <= owner.Sequence) continue;
                owner.Sequence = change.Sequence;
                if (change.State == 1 && !owner.Closed)
                { owner.Closed = true; owner.Animation = new(change.Sequence, 1, now); }
                else if (change.State is 3 or 7 or 8 or 9 or 11 && owner.Closed)
                { owner.Closed = false; owner.Animation = new(change.Sequence, 2, now); }
            }
        }
        foreach (ulong key in _owners.Keys.Where(k => !retained.Contains(k)).ToArray()) _owners.Remove(key);
    }
    public static float Frame(EnbShipWarpAnimation animation, uint now, float rate, int count)
        => Math.Clamp(unchecked(now - animation.StartTime) * .001f * rate, 0, Math.Max(0, count - 1));
    private sealed class Owner
    {
        public ulong Sequence;
        public bool Closed;
        public EnbShipWarpAnimation? Animation;
    }
}
