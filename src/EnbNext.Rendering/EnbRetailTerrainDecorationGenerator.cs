using System.Globalization;
using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Procedural terrain decorations from decoration.ini (section = sector number):
/// NUM_DECOS placements of NUM_PROTOS prototypes (PROTOn, default TDTREE01A), scale uniform in
/// [MIN_SCALE, MAX_SCALE] (defaults 1 and 2), position uniform over the height field (redrawn while
/// at or below sea level when the planet has a sea), and a random rotation about the vertical axis.
/// Random numbers come from a Mersenne Twister (MT19937, 1998 seeding) seeded with 4357. Checked
/// against the previous translated generator: bit-identical placements on every planet.</summary>
public static class EnbRetailTerrainDecorationGenerator
{
    public static EnbRetailTerrainDecorations Generate(EnbRetailTerrain terrain, ReadOnlySpan<byte> ini,
        CancellationToken cancellation = default) => Generate(terrain.Sector, terrain.Width, terrain.Heights,
            terrain.HorizontalScale / terrain.Width, terrain.HorizontalScale / terrain.Width, terrain.VerticalScale,
            terrain.HasSea, terrain.SeaLevel, ini, cancellation);

    public static EnbRetailTerrainDecorations Generate(uint sector, int width, ReadOnlySpan<ushort> heights,
        float xScale, float yScale, float heightScale, bool hasSea, float seaLevel, ReadOnlySpan<byte> ini,
        CancellationToken cancellation = default)
    {
        if (width <= 0 || !BitOperations.IsPow2(width) || heights.Length != checked(width * width)) throw new ArgumentException("Invalid height field.");
        var empty = new EnbRetailTerrainDecorations([], []);
        using var config = new EnbRetailTerrainOwnedIni(ini);
        string section = unchecked((int)sector).ToString(CultureInfo.InvariantCulture);
        int count = config.ReadInteger(section, "NUM_DECOS", 0);
        if (count <= 0) return empty;
        float minimum = config.ReadFloat(section, "MIN_SCALE", 1), maximum = config.ReadFloat(section, "MAX_SCALE", 2);
        int protoCount = config.ReadInteger(section, "NUM_PROTOS", 0);
        var prototypes = new List<string>();
        for (int i = 0; i < protoCount; i++)
            prototypes.Add(System.Text.Encoding.Latin1.GetString(config.ReadText(section, "PROTO" + i.ToString(CultureInfo.InvariantCulture), 0x400, "TDTREE01A")));
        if (prototypes.Count == 0) return empty;

        var random = new MersenneTwister(4357);
        uint range = (uint)(prototypes.Count - 1), mask = 0;
        if (range != 0)
        {
            int bit = 31; while (bit > 0 && (range & (1u << bit)) == 0) bit--;
            mask = bit == 31 ? uint.MaxValue : ~(uint.MaxValue << (bit + 1));
        }
        var field = new EnbTerrainHeightField(width, heights, xScale, yScale, heightScale);
        var placements = new EnbRetailTerrainDecoration[count];
        for (int n = 0; n < count; n++)
        {
            if ((n & 255) == 0) cancellation.ThrowIfCancellationRequested();
            uint prototype = 0;
            if (range != 0) do prototype = random.Next() & mask; while (prototype > range);
            float scale = (maximum - minimum) * random.NextSingle() + minimum;
            Vector3 point;
            do
            {
                float b = random.NextSingle(), a = random.NextSingle();
                point = field.World(a, b);
            } while (hasSea && point.Z <= seaLevel);
            float angle = random.NextSingle() * 6.28318548f;
            float cos = (float)Math.Cos(angle), sin = (float)Math.Sin(angle);
            var matrix = new Matrix4x4(cos, sin, 0, 0, -sin, cos, 0, 0, 0, 0, 1, 0, point.X, point.Y, point.Z, 1);
            placements[n] = new((int)prototype, Quaternion.Identity, new(point.X, point.Y), scale) { GeneratedTransform = matrix };
        }
        return new(prototypes.ToArray(), placements);
    }

    private sealed class MersenneTwister
    {
        private readonly uint[] state = new uint[624]; private int index;
        public MersenneTwister(uint seed)
        {
            state[0] = seed == 0 ? 4375u : seed;
            for (index = 1; index < 624; index++) state[index] = unchecked(69069 * state[index - 1]);
        }
        public uint Next()
        {
            if (index >= 624)
            {
                for (int k = 0; k < 624; k++)
                {
                    uint y = (state[k] & 0x80000000u) | (state[(k + 1) % 624] & 0x7fffffffu);
                    state[k] = state[(k + 397) % 624] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0);
                }
                index = 0;
            }
            uint r = state[index++];
            r ^= r >> 11; r ^= (r << 7) & 0x9d2c5680u; r ^= (r << 15) & 0xefc60000u; r ^= r >> 18;
            return r;
        }
        /// <summary>Next word times 2^-32, rounded to single precision (can reach 1.0).</summary>
        public float NextSingle() => (float)(Next() * (1.0 / 4294967296.0));
    }
}
