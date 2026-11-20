using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace EnbNext.Assets;

/// <summary>The installed client's shared failed-load texture (009AF1E0 / 009AF0F0).</summary>
public static class EnbRetailMissingTexture
{
    public static EnbTextureImage Load(string executable)
    {
        using var stream = File.OpenRead(EnbNext.Installation.EnbPathCase.Resolve(executable));
        using var pe = new PEReader(stream);
        if (pe.PEHeaders.PEHeader?.ImageBase != 0x400000)
            throw new InvalidDataException("Unsupported retail missing-texture image layout.");
        var header = pe.GetSectionData(0x7BBAEC).GetContent(0, 16).AsSpan();
        int width = BinaryPrimitives.ReadInt32LittleEndian(header);
        int height = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        if (width != 128 || height != 128 || count is <= 0 or > 7332)
            throw new InvalidDataException("Unsupported retail missing-texture table.");
        var runs = pe.GetSectionData(0x7BBAFC).GetContent(0, count).AsSpan();
        var colors = pe.GetSectionData(0x7BD7A0).GetContent(0, checked(count * 4)).AsSpan();
        return Decode(width, height, runs, colors);
    }

    internal static EnbTextureImage Decode(int width, int height, ReadOnlySpan<byte> runs, ReadOnlySpan<byte> colors)
    {
        if (width <= 0 || height <= 0 || colors.Length != checked(runs.Length * 4))
            throw new InvalidDataException("Invalid retail missing-texture runs.");
        var pixels = new EnbColor32[checked(width * height)];
        int written = 0;
        for (int run = 0; run < runs.Length; run++)
        {
            var color = colors.Slice(run * 4, 4);
            // Internal format 2 -> D3DFMT_A8R8G8B8 (00995960 table BBB8F0 = 21).
            var pixel = new EnbColor32(color[2], color[1], color[0], color[3]);
            for (int n = 0; n <= runs[run]; n++)
            {
                if (written >= pixels.Length) throw new InvalidDataException("Retail missing-texture run overflow.");
                int row = written / width;
                // 009AF24A starts at the locked base; subsequent rows count down from height-1.
                int targetRow = row == 0 ? 0 : height - row;
                pixels[targetRow * width + written % width] = pixel;
                written++;
            }
        }
        if (written != pixels.Length) throw new InvalidDataException("Incomplete retail missing-texture image.");
        var mips = new List<EnbTextureMipLevel> { new(width, height, pixels) };
        // 009AF33x uses D3DX filter 5 between successive levels: recursive
        // 2x2 box reduction, as recovered at 008EB0B4 for environment atlases.
        while (width > 1 || height > 1)
        {
            int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
            var next = new EnbColor32[nextWidth * nextHeight];
            for (int y = 0; y < nextHeight; y++)
            for (int x = 0; x < nextWidth; x++)
            {
                int r = 0, g = 0, b = 0, a = 0;
                for (int ky = 0; ky < 2; ky++)
                for (int kx = 0; kx < 2; kx++)
                {
                    var p = pixels[Math.Min(2 * y + ky, height - 1) * width + Math.Min(2 * x + kx, width - 1)];
                    r += p.R; g += p.G; b += p.B; a += p.A;
                }
                next[y * nextWidth + x] = new((byte)((r + 2) / 4), (byte)((g + 2) / 4),
                    (byte)((b + 2) / 4), (byte)((a + 2) / 4));
            }
            width = nextWidth; height = nextHeight; pixels = next;
            mips.Add(new(width, height, pixels));
        }
        return new(EnbTextureFormat.CurrentRetailMissingArgb8, mips);
    }
}
