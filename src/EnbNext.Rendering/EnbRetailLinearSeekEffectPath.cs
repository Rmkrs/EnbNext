using System.Numerics;

namespace EnbNext.Rendering;

// 006F1C40 time-keyed points, 008D4210 linear sampling, 006F1AD0 pose.
// Initial construction only: factory writes OneChange after this path is built.
public sealed class EnbRetailLinearSeekEffectPath
{
    private readonly (Vector3 Point, float Time)[] _keys;

    public EnbRetailLinearSeekEffectPath(uint duration, uint changeTime, uint timeToChange, float distance, uint seed)
        : this(duration,changeTime,timeToChange,distance,new EnbRetailParticleBirthRandomStream(seed,0)) { }

    public EnbRetailLinearSeekEffectPath(uint duration, uint changeTime, uint timeToChange, float distance, EnbRetailParticleBirthRandomStream random)
    {
        duration = duration is 0 or uint.MaxValue ? 1 : duration;
        changeTime = Math.Max(100, changeTime);
        timeToChange = Math.Max(100, timeToChange);
        if (!float.IsFinite(distance)) throw new InvalidDataException("Invalid linear seek distance");
        distance = Math.Max(0, distance);
        int count = duration < 2ul * changeTime ? 3 : checked((int)(duration / changeTime + 1));
        // 006F1C40 appends every knot; it has no count limit.
        float d = duration;
        if (duration < 2ul * changeTime) distance *= d / (2f * changeTime);
        double gap = (d - count * (double)timeToChange) / (count - 1);
        bool paired = gap >= .05f;
        float transition = timeToChange / d;
        float step = paired ? (float)(gap / d) : 1f / (count - 1);
        float x = paired ? transition : step;
        var keys = new List<(Vector3 Point, float Time)>(paired ? 2 * count - 2 : count)
        {
            (Vector3.Zero, 0)
        };
        uint first;
        lock(random)first=random.Reserve(count-2);
        for (int i = 0; i < count - 2; i++)
        {
            double angle = EnbRetailSignedRandom.Word(random.Seed, unchecked(first+(uint)i)) * (double)BitConverter.Int32BitsToSingle(0x30c90fdb);
            // The paired branch stores sin to float before multiplying R.
            float y = paired ? (float)Math.Sin(angle) * distance : (float)(Math.Sin(angle) * distance);
            float z = (float)(Math.Cos(angle) * distance);
            keys.Add((new(x, y, z), x * d));
            x += step;
            if (paired)
            {
                keys.Add((new(x, y, z), x * d));
                x += transition;
            }
        }
        keys.Add((Vector3.UnitX, d));
        _keys = keys.ToArray();
    }

    public Matrix4x4 Sample(uint age, Vector3 source, Vector3 target)
    {
        float time = age;
        Vector3 point;
        if (time <= _keys[0].Time) point = _keys[0].Point;
        else if (time >= _keys[^1].Time) point = _keys[^1].Point;
        else
        {
            int next = 1;
            while (time > _keys[next].Time) next++;
            var a = _keys[next - 1];
            var b = _keys[next];
            float fraction = (time - a.Time) / (b.Time - a.Time);
            point = a.Point + (b.Point - a.Point) * fraction;
        }
        point.X *= Vector3.Distance(source, target);
        var basis = EnbRetailSeekEffectPath.LookAlong(target - source, source);
        basis.Translation = Vector3.Transform(point, basis);
        return basis;
    }
}
