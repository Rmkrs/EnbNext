using System.Numerics;

namespace EnbNext.Rendering;

/// <summary>6E32E0 type 3, 6E3F00 and the shared 6E2CC0 wrap/pixel-grid consumer.</summary>
public sealed class EnbRetailMovieShake
{
    private readonly List<(float Amplitude, uint Deadline)> _impulses = [];
    public int Count => _impulses.Count;
    public void Clear() => _impulses.Clear();
    public void Add(float amplitude, float seconds, uint wallClock)
        => _impulses.Add((amplitude, unchecked(wallClock + (uint)(int)(seconds * 1000))));

    public Vector3 Sample(uint wallClock, int width, int height, Func<int>? random = null)
    {
        Vector2 sum = default;
        for (int i = 0; i < _impulses.Count;)
        {
            var impulse = _impulses[i];
            if (wallClock >= impulse.Deadline) { _impulses.RemoveAt(i); continue; }
            i++;
            sum += new Vector2(random?.Invoke() ?? EnbRetailMovieRandom.Shake.Next(-100, 100),
                random?.Invoke() ?? EnbRetailMovieRandom.Shake.Next(-100, 100)) * impulse.Amplitude;
        }
        if (!float.IsFinite(sum.X) || !float.IsFinite(sum.Y)) return default;
        static float Wrap(float value) => value > 12 ? (value % 12 == 0 ? 12 : value % 12) :
            value < -12 ? (value % 12 == 0 ? -12 : value % 12) : value;
        return EnbShakeRuntime.SnapToPixelGrid(new(Wrap(sum.X),Wrap(sum.Y),0),width,height);
    }
}
