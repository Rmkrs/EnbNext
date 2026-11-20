using System.Numerics;
using EnbNext.Assets;

namespace EnbNext.Rendering;

/// <summary>Terrain prelight (retail 46E670 and the stages it calls): quantised
/// normals, the horizon in eight directions per sample and the ambient colour
/// that the sky above those horizons gives.</summary>
public static class EnbRetailTerrainPrelightBuilder
{
    /// <summary>46E670: create authored shadows, build private lighting planes,
    /// then release the retained shadow references after lighting completes.</summary>
    public static EnbRetailTerrainPrelight BuildFromIni(EnbRetailTerrainShadowObjectFactory factory,
        EnbRetailTerrainOwnedIni ini,int width,ReadOnlySpan<ushort> heights,
        float xScale,float yScale,float heightScale,bool wrapping,ReadOnlySpan<byte> colorBgr,ushort originalMaximum,
        CancellationToken cancellation=default)
    {
        var objects=factory.CreateFromIni(ini,cancellation);
        try{return BuildWithShadows(width,heights,xScale,yScale,heightScale,wrapping,colorBgr,objects,originalMaximum,cancellation);}
        finally{foreach(var owner in objects)EnbRetailTerrainShadowObjectFactory.Release(owner);}
    }

    public static EnbRetailTerrainPrelight BuildWithShadows(int width,ReadOnlySpan<ushort> heights,float xScale,float yScale,float heightScale,
        bool wrapping,ReadOnlySpan<byte> colorBgr,IReadOnlyList<EnbRetailShadowObject> objects,ushort originalMaximum,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var shadow=EnbRetailTerrainShadowHeightBuilder.Build(width,heights,xScale,yScale,heightScale,wrapping,objects,cancellation);
        return Build(width,shadow.Heights,xScale,yScale,heightScale,wrapping,colorBgr,shadow.Coverage,originalMaximum,cancellation:cancellation);
    }

    // 0AE8BBC: the 256 quantised normal directions (copied to owner+1FC by 46F120).
    private static readonly uint[] NormalBits =
    [
        0x3f800000,0x00000000,0x00000000,0x3f7f7fe1,0x00000000,0x3d800000,0x3f7dfe00,0x00000000,0x3e000000,0x3f7b75b8,0x00000000,0x3e400000,
        0x3f77def8,0x00000000,0x3e800000,0x3f732dd3,0x00000000,0x3ea00000,0x3f6d5183,0x00000000,0x3ec00000,0x3f663327,0x00000000,0x3ee00000,
        0x3f5db3d0,0x00000000,0x3f000000,0x3f53a8f8,0x00000000,0x3f100000,0x3f47d70a,0x00000000,0x3f200000,0x3f39e732,0x00000000,0x3f300000,
        0x3f295400,0x00000000,0x3f400000,0x3f153cef,0x00000000,0x3f500000,0x3ef7def8,0x00000000,0x3f600000,0x3eb22b17,0x00000000,0x3f700000,
        0x3f6c8366,0x3ec3ef07,0x00000000,0x3f6c0cfa,0x3ec38cfc,0x3d800000,0x3f6aa876,0x3ec265ae,0x3e000000,0x3f685187,0x3ec07571,0x3e400000,
        0x3f6500c5,0x3ebdb65f,0x3e800000,0x3f60ab0d,0x3eba1f08,0x3ea00000,0x3f5b40e6,0x3eb5a2b6,0x3ec00000,0x3f54ad47,0x3eb02ff9,0x3ee00000,
        0x3f4cd38d,0x3ea9af19,0x3f000000,0x3f438c65,0x3ea1ff71,0x3f100000,0x3f38a0c3,0x3e98f367,0x3f200000,0x3f2bc090,0x3e8e48c7,0x3f300000,
        0x3f1c7054,0x3e819913,0x3f400000,0x3f09e0c6,0x3e6471b4,0x3f500000,0x3ee500b4,0x3e3db65f,0x3f600000,0x3ea49b20,0x3e085d31,0x3f700000,
        0x3f3504f7,0x3f3504f7,0x00000000,0x3f34aa54,0x3f34aa54,0x3d800000,0x3f339980,0x3f339980,0x3e000000,0x3f31cf14,0x3f31cf14,0x3e400000,
        0x3f2f456b,0x3f2f456b,0x3e800000,0x3f2bf413,0x3f2bf413,0x3ea00000,0x3f27cf3e,0x3f27cf3e,0x3ec00000,0x3f22c69b,0x3f22c69b,0x3ee00000,
        0x3f1cc469,0x3f1cc469,0x3f000000,0x3f15aa93,0x3f15aa93,0x3f100000,0x3f0d4ee4,0x3f0d4ee4,0x3f200000,0x3f037415,0x3f037415,0x3f300000,
        0x3eef774b,0x3eef774b,0x3f400000,0x3ed30dfa,0x3ed30dfa,0x3f500000,0x3eaf457c,0x3eaf457c,0x3f600000,0x3e7bf7f0,0x3e7bf7f0,0x3f700000,
        0x3ec3ef07,0x3f6c8366,0x00000000,0x3ec38cfc,0x3f6c0cfa,0x3d800000,0x3ec265ae,0x3f6aa876,0x3e000000,0x3ec07571,0x3f685187,0x3e400000,
        0x3ebdb65f,0x3f6500c5,0x3e800000,0x3eba1f08,0x3f60ab0d,0x3ea00000,0x3eb5a2b6,0x3f5b40e6,0x3ec00000,0x3eb02ff9,0x3f54ad47,0x3ee00000,
        0x3ea9af19,0x3f4cd38d,0x3f000000,0x3ea1ff71,0x3f438c65,0x3f100000,0x3e98f367,0x3f38a0c3,0x3f200000,0x3e8e48c7,0x3f2bc090,0x3f300000,
        0x3e819913,0x3f1c7054,0x3f400000,0x3e6471b4,0x3f09e0c6,0x3f500000,0x3e3db65f,0x3ee500b4,0x3f600000,0x3e085d31,0x3ea49b20,0x3f700000,
        0x00000000,0x3f800000,0x00000000,0x00000000,0x3f7f7fe1,0x3d800000,0x00000000,0x3f7dfe00,0x3e000000,0x00000000,0x3f7b75b8,0x3e400000,
        0x00000000,0x3f77def8,0x3e800000,0x00000000,0x3f732dd3,0x3ea00000,0x00000000,0x3f6d5183,0x3ec00000,0x00000000,0x3f663327,0x3ee00000,
        0x00000000,0x3f5db3d0,0x3f000000,0x00000000,0x3f53a8f8,0x3f100000,0x00000000,0x3f47d70a,0x3f200000,0x00000000,0x3f39e732,0x3f300000,
        0x00000000,0x3f295400,0x3f400000,0x00000000,0x3f153cef,0x3f500000,0x00000000,0x3ef7def8,0x3f600000,0x00000000,0x3eb22b17,0x3f700000,
        0xbec3ef07,0x3f6c8366,0x00000000,0xbec38cfc,0x3f6c0cfa,0x3d800000,0xbec265ae,0x3f6aa876,0x3e000000,0xbec07571,0x3f685187,0x3e400000,
        0xbebdb65f,0x3f6500c5,0x3e800000,0xbeba1f08,0x3f60ab0d,0x3ea00000,0xbeb5a2b6,0x3f5b40e6,0x3ec00000,0xbeb02ff9,0x3f54ad47,0x3ee00000,
        0xbea9af19,0x3f4cd38d,0x3f000000,0xbea1ff71,0x3f438c65,0x3f100000,0xbe98f367,0x3f38a0c3,0x3f200000,0xbe8e48c7,0x3f2bc090,0x3f300000,
        0xbe819913,0x3f1c7054,0x3f400000,0xbe6471b4,0x3f09e0c6,0x3f500000,0xbe3db65f,0x3ee500b4,0x3f600000,0xbe085d31,0x3ea49b20,0x3f700000,
        0xbf3504f7,0x3f3504f7,0x00000000,0xbf34aa54,0x3f34aa54,0x3d800000,0xbf339980,0x3f339980,0x3e000000,0xbf31cf14,0x3f31cf14,0x3e400000,
        0xbf2f456b,0x3f2f456b,0x3e800000,0xbf2bf413,0x3f2bf413,0x3ea00000,0xbf27cf3e,0x3f27cf2d,0x3ec00000,0xbf22c69b,0x3f22c69b,0x3ee00000,
        0xbf1cc469,0x3f1cc469,0x3f000000,0xbf15aa93,0x3f15aa93,0x3f100000,0xbf0d4ee4,0x3f0d4ee4,0x3f200000,0xbf037415,0x3f037415,0x3f300000,
        0xbeef774b,0x3eef774b,0x3f400000,0xbed30dfa,0x3ed30dfa,0x3f500000,0xbeaf457c,0x3eaf457c,0x3f600000,0xbe7bf7f0,0x3e7bf7f0,0x3f700000,
        0xbf6c8366,0x3ec3ef07,0x00000000,0xbf6c0cfa,0x3ec38cfc,0x3d800000,0xbf6aa876,0x3ec265ae,0x3e000000,0xbf685187,0x3ec07571,0x3e400000,
        0xbf6500c5,0x3ebdb65f,0x3e800000,0xbf60ab0d,0x3eba1f08,0x3ea00000,0xbf5b40e6,0x3eb5a2b6,0x3ec00000,0xbf54ad58,0x3eb02ff9,0x3ee00000,
        0xbf4cd38d,0x3ea9af19,0x3f000000,0xbf438c65,0x3ea1ff71,0x3f100000,0xbf38a0c3,0x3e98f367,0x3f200000,0xbf2bc090,0x3e8e48c7,0x3f300000,
        0xbf1c7054,0x3e819913,0x3f400000,0xbf09e0c6,0x3e6471b4,0x3f500000,0xbee500b4,0x3e3db65f,0x3f600000,0xbea49b20,0x3e085d31,0x3f700000,
        0xbf800000,0x00000000,0x00000000,0xbf7f7fe1,0x00000000,0x3d800000,0xbf7dfe00,0x00000000,0x3e000000,0xbf7b75b8,0x00000000,0x3e400000,
        0xbf77def8,0x00000000,0x3e800000,0xbf732dd3,0x00000000,0x3ea00000,0xbf6d5183,0x00000000,0x3ec00000,0xbf663327,0x00000000,0x3ee00000,
        0xbf5db3d0,0x00000000,0x3f000000,0xbf53a8f8,0x00000000,0x3f100000,0xbf47d70a,0x00000000,0x3f200000,0xbf39e732,0x00000000,0x3f300000,
        0xbf295400,0x00000000,0x3f400000,0xbf153cef,0x00000000,0x3f500000,0xbef7def8,0x00000000,0x3f600000,0xbeb22b17,0x00000000,0x3f700000,
        0xbf6c8366,0xbec3ef28,0x00000000,0xbf6c0cfa,0xbec38cfc,0x3d800000,0xbf6aa876,0xbec265ae,0x3e000000,0xbf685187,0xbec07571,0x3e400000,
        0xbf6500b4,0xbebdb65f,0x3e800000,0xbf60ab0d,0xbeba1f08,0x3ea00000,0xbf5b40e6,0xbeb5a2b6,0x3ec00000,0xbf54ad47,0xbeb02ff9,0x3ee00000,
        0xbf4cd38d,0xbea9af19,0x3f000000,0xbf438c65,0xbea1ff71,0x3f100000,0xbf38a0c3,0xbe98f367,0x3f200000,0xbf2bc090,0xbe8e48c7,0x3f300000,
        0xbf1c7054,0xbe819913,0x3f400000,0xbf09e0c6,0xbe6471b4,0x3f500000,0xbee500b4,0xbe3db65f,0x3f600000,0xbea49b20,0xbe085d31,0x3f700000,
        0xbf3504f7,0xbf3504f7,0x00000000,0xbf34aa54,0xbf34aa54,0x3d800000,0xbf339980,0xbf339980,0x3e000000,0xbf31cf14,0xbf31cf14,0x3e400000,
        0xbf2f456b,0xbf2f456b,0x3e800000,0xbf2bf413,0xbf2bf413,0x3ea00000,0xbf27cf2d,0xbf27cf3e,0x3ec00000,0xbf22c69b,0xbf22c6ac,0x3ee00000,
        0xbf1cc469,0xbf1cc47a,0x3f000000,0xbf15aa93,0xbf15aa93,0x3f100000,0xbf0d4ee4,0xbf0d4ee4,0x3f200000,0xbf037415,0xbf037415,0x3f300000,
        0xbeef774b,0xbeef774b,0x3f400000,0xbed30dfa,0xbed30dfa,0x3f500000,0xbeaf457c,0xbeaf457c,0x3f600000,0xbe7bf7f0,0xbe7bf7f0,0x3f700000,
        0xbec3ef07,0xbf6c8366,0x00000000,0xbec38cfc,0xbf6c0cfa,0x3d800000,0xbec265ae,0xbf6aa876,0x3e000000,0xbec07571,0xbf685187,0x3e400000,
        0xbebdb65f,0xbf6500c5,0x3e800000,0xbeba1f08,0xbf60ab0d,0x3ea00000,0xbeb5a2b6,0xbf5b40e6,0x3ec00000,0xbeb02ff9,0xbf54ad58,0x3ee00000,
        0xbea9aef7,0xbf4cd38d,0x3f000000,0xbea1ff71,0xbf438c65,0x3f100000,0xbe98f367,0xbf38a0c3,0x3f200000,0xbe8e48c7,0xbf2bc090,0x3f300000,
        0xbe819913,0xbf1c7054,0x3f400000,0xbe6471b4,0xbf09e0c6,0x3f500000,0xbe3db65f,0xbee500b4,0x3f600000,0xbe085d31,0xbea49b20,0x3f700000,
        0x00000000,0xbf800000,0x00000000,0x00000000,0xbf7f7fe1,0x3d800000,0x00000000,0xbf7dfe00,0x3e000000,0x00000000,0xbf7b75b8,0x3e400000,
        0x00000000,0xbf77def8,0x3e800000,0x00000000,0xbf732dd3,0x3ea00000,0x00000000,0xbf6d5183,0x3ec00000,0x00000000,0xbf663327,0x3ee00000,
        0x00000000,0xbf5db3d0,0x3f000000,0x00000000,0xbf53a8f8,0x3f100000,0x00000000,0xbf47d70a,0x3f200000,0x00000000,0xbf39e732,0x3f300000,
        0x00000000,0xbf295400,0x3f400000,0x00000000,0xbf153cef,0x3f500000,0x00000000,0xbef7def8,0x3f600000,0x00000000,0xbeb22b17,0x3f700000,
        0x3ec3ef28,0xbf6c8366,0x00000000,0x3ec38cfc,0xbf6c0cfa,0x3d800000,0x3ec265ae,0xbf6aa876,0x3e000000,0x3ec07592,0xbf685187,0x3e400000,
        0x3ebdb65f,0xbf6500b4,0x3e800000,0x3eba1f08,0xbf60ab0d,0x3ea00000,0x3eb5a2b6,0xbf5b40e6,0x3ec00000,0x3eb02ff9,0xbf54ad47,0x3ee00000,
        0x3ea9af19,0xbf4cd38d,0x3f000000,0x3ea1ff71,0xbf438c65,0x3f100000,0x3e98f367,0xbf38a0c3,0x3f200000,0x3e8e48c7,0xbf2bc090,0x3f300000,
        0x3e819913,0xbf1c7054,0x3f400000,0x3e6471b4,0xbf09e0c6,0x3f500000,0x3e3db65f,0xbee500b4,0x3f600000,0x3e085d31,0xbea49b20,0x3f700000,
        0x3f3504f7,0xbf3504f7,0x00000000,0x3f34aa54,0xbf34aa54,0x3d800000,0x3f339980,0xbf339980,0x3e000000,0x3f31cf14,0xbf31cf14,0x3e400000,
        0x3f2f456b,0xbf2f456b,0x3e800000,0x3f2bf413,0xbf2bf413,0x3ea00000,0x3f27cf3e,0xbf27cf2d,0x3ec00000,0x3f22c6ac,0xbf22c69b,0x3ee00000,
        0x3f1cc47a,0xbf1cc469,0x3f000000,0x3f15aa93,0xbf15aa93,0x3f100000,0x3f0d4ee4,0xbf0d4ee4,0x3f200000,0x3f037415,0xbf037415,0x3f300000,
        0x3eef774b,0xbeef774b,0x3f400000,0x3ed30dfa,0xbed30dfa,0x3f500000,0x3eaf457c,0xbeaf457c,0x3f600000,0x3e7bf7f0,0xbe7bf7f0,0x3f700000,
        0x3f6c8366,0xbec3ef07,0x00000000,0x3f6c0cfa,0xbec38cfc,0x3d800000,0x3f6aa876,0xbec265ae,0x3e000000,0x3f685187,0xbec07571,0x3e400000,
        0x3f6500c5,0xbebdb63d,0x3e800000,0x3f60ab0d,0xbeba1f08,0x3ea00000,0x3f5b40e6,0xbeb5a2b6,0x3ec00000,0x3f54ad58,0xbeb02ff9,0x3ee00000,
        0x3f4cd38d,0xbea9aef7,0x3f000000,0x3f438c65,0xbea1ff71,0x3f100000,0x3f38a0c3,0xbe98f367,0x3f200000,0x3f2bc090,0xbe8e48c7,0x3f300000,
        0x3f1c7054,0xbe819913,0x3f400000,0x3f09e0c6,0xbe6471b4,0x3f500000,0x3ee500b4,0xbe3db65f,0x3f600000,0x3ea49b20,0xbe085d31,0x3f700000,
    ];
    private static readonly Vector3[] Normals = CreateNormals();
    private static Vector3[] CreateNormals()
    {
        var normals = new Vector3[256];
        for (int i = 0; i < 256; i++)
            normals[i] = new(BitConverter.UInt32BitsToSingle(NormalBits[i * 3]),
                BitConverter.UInt32BitsToSingle(NormalBits[i * 3 + 1]), BitConverter.UInt32BitsToSingle(NormalBits[i * 3 + 2]));
        return normals;
    }

    /// <summary>46F120: one quantised normal per height sample. Corners sum the eight
    /// triangles around sample (0,0); edges and the interior use pairs of triangles
    /// around every second sample. The last row and column wrap to row/column 0.</summary>
    public static byte[] NormalCodes(int width, ReadOnlySpan<ushort> heights, float spacing, float heightScale)
    {
        var codes = new byte[width * width];
        float s = spacing, n = -spacing;
        float ss = s * s, nn = n * n, ns = -(n * s);
        // Corners: all eight triangles around (0,0) (cx, cy are 0 or width, which wrap).
        foreach (int cy in (ReadOnlySpan<int>)[0, width])
            foreach (int cx in (ReadOnlySpan<int>)[0, width])
            {
                var d = Around(heights, width, heightScale, cx, cy);
                float x = d.T1X(n) + d.T2X(n) + d.T3X(n) + d.T4X(s) + d.T5X(s) + d.T6X(s) + d.T7X(s) + d.T8X(n);
                float y = d.T1Y(n) + d.T2Y(s) + d.T3Y(s) + d.T4Y(s) + d.T5Y(s) + d.T6Y(n) + d.T7Y(n) + d.T8Y(n);
                float z = nn + ns + ns + ss + ss + ns + ns + nn;
                codes[(cy == 0 ? 0 : (width - 1) * width) + (cx == 0 ? 0 : width - 1)] = Quantise(x, y, z, false);
            }
        // First and last row: columns 1..width-2, two per sample at even columns.
        if (width - 2 >= 2)
            foreach (int cy in (ReadOnlySpan<int>)[0, width])
            {
                int row = cy == 0 ? 0 : (width - 1) * width;
                for (int cx = 2; cx <= width - 2; cx += 2)
                {
                    var d = Around(heights, width, heightScale, cx, cy);
                    codes[row + cx - 1] = Quantise(-(n * d.D) + s * d.D, d.T8Y(n) + d.T7Y(n), nn + ns, false);
                    codes[row + cx] = Quantise(-(d.F * s) + d.F * n, d.T4Y(s) + d.T3Y(s), ss + ns, false);
                }
            }
        for (int cy = 2; cy <= width - 2; cy += 2)
        {
            // First and last column: rows cy-1 and cy.
            foreach (int cx in (ReadOnlySpan<int>)[0, width])
            {
                var d = Around(heights, width, heightScale, cx, cy);
                int column = cx == 0 ? 0 : width - 1;
                codes[(cy - 1) * width + column] = Quantise(d.T2X(n) + d.T1X(n), d.B * s + -(d.B * n), ns + nn, true);
                codes[cy * width + column] = Quantise(d.T6X(s) + d.T5X(s), n * d.H + -(s * d.H), ns + ss, true);
            }
            // Interior: 2x2 outputs around every second sample, two triangles each.
            for (int cx = 2; cx <= width - 2; cx += 2)
            {
                var d = Around(heights, width, heightScale, cx, cy);
                int i0 = (cy - 1) * width + cx - 1;
                codes[i0] = Quantise(d.T8X(n) + d.T1X(n), d.T8Y(n) + d.T1Y(n), nn + nn, false);
                codes[i0 + 1] = Quantise(d.T3X(n) + d.T2X(n), d.T3Y(s) + d.T2Y(s), ns + ns, false);
                codes[i0 + width] = Quantise(d.T7X(s) + d.T6X(s), d.T7Y(n) + d.T6Y(n), ns + ns, false);
                codes[i0 + width + 1] = Quantise(d.T5X(s) + d.T4X(s), d.T5Y(s) + d.T4Y(s), ss + ss, false);
            }
        }
        return codes;
    }

    private static Neighbourhood Around(ReadOnlySpan<ushort> h, int width, float heightScale, int cx, int cy)
    {
        float c = Height(h, width, heightScale, cx, cy);
        return new(Height(h, width, heightScale, cx - 1, cy - 1) - c, Height(h, width, heightScale, cx, cy - 1) - c,
            Height(h, width, heightScale, cx + 1, cy - 1) - c, Height(h, width, heightScale, cx - 1, cy) - c,
            Height(h, width, heightScale, cx + 1, cy) - c, Height(h, width, heightScale, cx - 1, cy + 1) - c,
            Height(h, width, heightScale, cx, cy + 1) - c, Height(h, width, heightScale, cx + 1, cy + 1) - c);
    }

    // 463CD0: grid coordinates wrap by the power-of-two width.
    private static float Height(ReadOnlySpan<ushort> h, int width, float heightScale, int x, int y)
        => h[((y & (width - 1)) * width) + (x & (width - 1))] * heightScale;

    /// <summary>Height differences to the eight neighbours a..i (row above, same row, row below).</summary>
    private readonly record struct Neighbourhood(float A, float B, float C, float D, float F, float G, float H, float I)
    {
        // Triangle normals (edge cross products) around the centre, x and y components.
        public float T1X(float n) => B * n - A * n;
        public float T1Y(float n) => -(B * n);
        public float T2X(float n) => C * n - n * B;
        public float T2Y(float s) => B * s;
        public float T3X(float n) => F * n;
        public float T3Y(float s) => C * s - F * s;
        public float T4X(float s) => -(F * s);
        public float T4Y(float s) => F * s - I * s;
        public float T5X(float s) => s * H - I * s;
        public float T5Y(float s) => -(s * H);
        public float T6X(float s) => s * G - s * H;
        public float T6Y(float n) => n * H;
        public float T7X(float s) => s * D;
        public float T7Y(float n) => n * G - n * D;
        public float T8X(float n) => -(n * D);
        public float T8Y(float n) => n * D - A * n;
    }

    /// <summary>Normalise (unless the length is zero) and pick the table normal with the
    /// largest dot product; ties keep the first.</summary>
    private static byte Quantise(float x, float y, float z, bool zFirstLength)
    {
        float square = zFirstLength ? (z * z + y * y) + x * x : (x * x + z * z) + y * y;
        if (square != 0 && !float.IsNaN(square))
        {
            float r = EnbRetailFloat.ReciprocalSquareRoot(square);
            x *= r; y *= r; z *= r;
        }
        float best = -1; int index = 0;
        for (int i = 0; i < 256; i++)
        {
            Vector3 normal = Normals[i];
            float dot = z * normal.Z + x * normal.X + y * normal.Y;
            if (dot > best) { best = dot; index = i; }
        }
        return (byte)index;
    }

    private static float Bits(uint value) => BitConverter.UInt32BitsToSingle(value);
    // 0AE97BC/0AE97DC: the eight horizon directions (x and y weights).
    private static readonly float[] SectorX = [Bits(0x3f3504f3), Bits(0x3e95f61a), Bits(0xbe95f61a), Bits(0xbf3504f3),
        Bits(0xbf3504f3), Bits(0xbe95f61a), Bits(0x3e95f61a), Bits(0x3f3504f3)];
    private static readonly float[] SectorY = [Bits(0x3e95f61a), Bits(0x3f3504f3), Bits(0x3f3504f3), Bits(0x3e95f61a),
        Bits(0xbe95f61a), Bits(0xbf3504f3), Bits(0xbf3504f3), Bits(0xbe95f61a)];
    // 0AE97C4/0AE97E4 and the stack constants of 472F80: four self-shadow axes.
    private static readonly float[] AxisA = [Bits(0xbe95f61a), Bits(0xbf3504f3), Bits(0xbf3504f3), Bits(0xbe95f61a)];
    private static readonly float[] AxisB = [Bits(0x3f3504f3), Bits(0x3e95f61a), Bits(0xbe95f61a), Bits(0xbf3504f3)];
    private static readonly float[] AxisScale = [Bits(0x3f8a8bd4), Bits(0x40273d75), Bits(0x40273d75), Bits(0x3f8a8bd4)];
    private const float HorizonStart = 0.05f;          // 0AE9AE4
    private const float Diagonal = 0.70710683f;        // 0AE9AE8
    private static readonly float HorizonScale = Bits(0x4722f8e0); // 0AE9AD0: 65535/(pi/2)
    private static readonly float ColorScale = Bits(0x3b808080);   // 0AE981C: 1/255
    private const double AmbientMinimum = 0.00195314;  // 0AE9AD8

    /// <summary>46EF60's second table: for each of 8 directions and 256 encoded horizon
    /// slopes, the sky contribution (x, y and z weights) above that horizon.</summary>
    private static readonly Vector3[] SkyTable = CreateSkyTable();
    private static Vector3[] CreateSkyTable()
    {
        var table = new Vector3[8 * 256];
        float halfPi = Bits(0x3fc90fdb), inverseTwoPi = Bits(0x3e22f983);
        for (int i = 0; i < 256; i++)
        {
            float slope = Bits(((uint)(i + 0x770) << 19) | 0x7ffff);
            float angle = (float)(halfPi - Math.Atan(slope));
            float sine = (float)Math.Sin(angle);
            float area = (angle - (float)(Math.Sin(angle + angle) * 0.5)) * inverseTwoPi;
            float up = sine * sine * 0.125f;
            for (int j = 0; j < 8; j++) table[j * 256 + i] = new(area * SectorX[j], area * SectorY[j], up);
        }
        return table;
    }

    public static EnbRetailTerrainPrelight Build(int width, ReadOnlySpan<ushort> heights, float xScale, float yScale,
        float heightScale, bool wrapping, ReadOnlySpan<byte> colorBgr, ReadOnlySpan<byte> occluded, ushort originalMaximum,
        Action<string, TimeSpan>? stageCompleted = null, CancellationToken cancellation = default)
    {
        var timer = stageCompleted is null ? null : System.Diagnostics.Stopwatch.StartNew();
        void Stage(string name) { if (timer is null) return; stageCompleted!(name, timer.Elapsed); timer.Restart(); }
        cancellation.ThrowIfCancellationRequested();
        int count = checked(width * width);
        if (width <= 0 || (width & (width - 1)) != 0 || heights.Length != count || colorBgr.Length != checked(count * 3) ||
            (!occluded.IsEmpty && occluded.Length != count)) throw new ArgumentException("Prelight planes differ from the height field.");
        byte[] codes = NormalCodes(width, heights, xScale, heightScale);
        Stage("normal codes");
        // 473290: per cell (between samples x..x+1, y..y+1) horizon slopes.
        float ratio = heightScale / xScale;
        var horizontal = new ushort[count * 2];
        var down = new byte[count * 3];
        var up = new byte[count * 3];
        for (int y = 0; y < width; y++)
        {
            SweepHorizontal(width, heights, y, ratio, originalMaximum, horizontal.AsSpan(y * width * 2, width * 2));
            SweepVertical(width, heights, y, ratio, +1, down.AsSpan(y * width * 3, width * 3));
        }
        for (int y = 0; y < width; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            SweepVertical(width, heights, y, ratio, -1, up.AsSpan(y * width * 3, width * 3));
        }
        Stage("light samples");
        // 470F80: interior samples take the horizons of the cell to their lower right;
        // edge samples average the two cells at both ends of their column (first and last
        // row) or row (first and last column), and corners the four corner cells.
        var negative = new ushort[count]; var positive = new ushort[count];
        var red = new byte[count]; var green = new byte[count]; var blue = new byte[count];
        int last = width - 1;
        var cells = new int[4];
        Span<byte> sectors = stackalloc byte[8];
        for (int y = 0; y < width; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int i = y * width + x, n;
                bool edgeX = x == 0 || x == last, edgeY = y == 0 || y == last;
                if (edgeX && edgeY) { cells[0] = 0; cells[1] = last; cells[2] = last * width; cells[3] = last * width + last; n = 4; }
                else if (edgeY) { cells[0] = x; cells[1] = last * width + x; n = 2; }
                else if (edgeX) { cells[0] = y * width; cells[1] = y * width + last; n = 2; }
                else { cells[0] = i; n = 1; }
                int shift = n == 4 ? 2 : n == 2 ? 1 : 0;
                int Sum<T>(T[] plane, int stride, int lane) where T : IBinaryInteger<T>
                {
                    int total = 0;
                    for (int k = 0; k < n; k++) total += int.CreateTruncating(plane[cells[k] * stride + lane]);
                    return total >> shift;
                }
                ushort neg = (ushort)Sum(horizontal, 2, 0), pos = (ushort)Sum(horizontal, 2, 1);
                negative[i] = Angle(neg); positive[i] = Angle(pos);
                sectors[0] = (byte)(neg >> 8); sectors[1] = (byte)(pos >> 8);
                sectors[2] = (byte)Sum(up, 3, 1); sectors[3] = (byte)Sum(down, 3, 1); sectors[4] = (byte)Sum(up, 3, 0);
                sectors[5] = (byte)Sum(down, 3, 2); sectors[6] = (byte)Sum(down, 3, 0); sectors[7] = (byte)Sum(up, 3, 2);
                var color = new Vector3(colorBgr[i * 3 + 2] * ColorScale, colorBgr[i * 3 + 1] * ColorScale, colorBgr[i * 3] * ColorScale);
                (red[i], green[i], blue[i]) = Ambient(codes[i], sectors, color);
                if (!occluded.IsEmpty && occluded[i] != 0) { negative[i] = ushort.MaxValue; positive[i] = ushort.MaxValue; }
            }
        }
        Stage("lighting codes");
        return new(codes, negative, positive, red, green, blue, (Vector3[])Normals.Clone());
    }

    /// <summary>46EF60/470F80: a 16-bit encoded slope back to the horizon angle, 0..65535 over 0..pi/2.</summary>
    private static ushort Angle(ushort encoded)
    {
        float slope = Bits(((uint)(encoded + 0x77000) << 11) | 0x7ff);
        return (ushort)(int)(float)(Math.Atan(slope) * HorizonScale);
    }

    /// <summary>4733C0: along the row pair y, y+1, the steepest slope from each cell's
    /// centre toward decreasing and increasing x (up to 128 samples each way).</summary>
    private static void SweepHorizontal(int width, ReadOnlySpan<ushort> h, int y, float ratio, ushort maximum, Span<ushort> output)
    {
        int mask = width - 1, row = (y & mask) * width, next = ((y + 1) & mask) * width;
        int limit = 2 * Math.Min(width, 128);
        float twice = ratio + ratio;
        for (int x = 0; x < width; x++)
        {
            int right = (x + 1) & mask;
            float average = ((((float)h[next + right] + h[row + right]) + h[next + x]) + h[row + x]) * 0.25f;
            float bound = ((float)maximum - average) * twice;
            output[x * 2] = EncodeWide(Look(h, mask, row, next, limit, average, bound, twice, x, -1));
            output[x * 2 + 1] = EncodeWide(Look(h, mask, row, next, limit, average, bound, twice, right, +1));
        }
    }

    private static float Look(ReadOnlySpan<ushort> h, int mask, int row, int next, int limit, float average, float bound, float twice, int column, int direction)
    {
        float best = HorizonStart, step = 1;
        for (int k = 1; k < limit; k += 2)
        {
            if (step * best >= bound) break;
            float value = ((((float)h[next + column] + h[row + column]) * 0.5f - average) * (1f / k)) * twice;
            if (value > best) best = value;
            step += 2; column = (column + direction) & mask;
        }
        return best;
    }

    /// <summary>473780 (direction +1, rows y+1, y+2, ...) and 473BC0 (direction -1, rows y,
    /// y-1, ...): twelve steps along the left diagonal, straight and right diagonal.</summary>
    private static void SweepVertical(int width, ReadOnlySpan<ushort> h, int y, float ratio, int direction, Span<byte> output)
    {
        int mask = width - 1, total = width * width - 1;
        int row = (y & mask) * width, next = ((y + 1) & mask) * width;
        float twice = ratio + ratio, diagonal = twice * Diagonal;
        for (int x = 0; x < width; x++)
        {
            int right = (x + 1) & mask;
            float average = ((((float)h[next + right] + h[row + right]) + h[next + x]) + h[row + x]) * 0.25f;
            float left = HorizonStart, straight = HorizonStart, across = HorizonStart;
            int offset = direction > 0 ? next : row, l = x, r = right;
            for (int k = 1; k < 24; k += 2)
            {
                float reciprocal = 1f / k;
                float a = (h[offset + (l & mask)] - average) * diagonal * reciprocal;
                float b = ((((float)h[offset + right] + h[offset + x]) * 0.5f) - average) * twice * reciprocal;
                float c = (h[offset + (r & mask)] - average) * diagonal * reciprocal;
                if (a > left) left = a;
                if (b > straight) straight = b;
                if (c > across) across = c;
                l--; r++; offset = (offset + direction * width) & total;
            }
            output[x * 3] = EncodeNarrow(left); output[x * 3 + 1] = EncodeNarrow(straight); output[x * 3 + 2] = EncodeNarrow(across);
        }
    }

    private static ushort EncodeWide(float slope)
        => slope >= 255.96875f ? (ushort)0x7fff : !(slope > 0.00390625f) ? (ushort)0 : (ushort)((BitConverter.SingleToUInt32Bits(slope) >> 11) - 0x7000);

    private static byte EncodeNarrow(float slope)
        => slope >= 248f ? (byte)0x7f : !(slope > 0.00390625f) ? (byte)0 : (byte)((BitConverter.SingleToUInt32Bits(slope) >> 19) - 0x70);

    /// <summary>472F80: raise each horizon to the normal's own tilt, sum the sky seen above
    /// all eight horizons along the normal, and tint by the colour map.</summary>
    private static (byte Red, byte Green, byte Blue) Ambient(byte code, Span<byte> sectors, Vector3 color)
    {
        Vector3 n = Normals[code];
        Span<byte> horizon = stackalloc byte[8];
        horizon[0] = sectors[1]; horizon[1] = sectors[5]; horizon[2] = sectors[3]; horizon[3] = sectors[6];
        horizon[4] = sectors[0]; horizon[5] = sectors[4]; horizon[6] = sectors[2]; horizon[7] = sectors[7];
        for (int i = 0; i < 4; i++)
        {
            float a = n.Z * AxisB[i];
            float b = AxisA[i] * n.Y - AxisB[i] * n.X;
            int sector = b > 0 ? i : i + 4;
            byte tilt = EncodeNarrow(MathF.Abs(b) / (MathF.Abs(a) * AxisScale[i]));
            if (horizon[sector] > tilt) tilt = horizon[sector];
            horizon[sector] = tilt;
        }
        float sx = 0, sy = 0, sz = 0;
        for (int i = 0; i < 8; i++)
        {
            Vector3 sky = SkyTable[i * 256 + horizon[i]];
            sx += sky.X; sy += sky.Y; sz += sky.Z;
        }
        float light = (sy * n.Y + sz * n.Z) + sx * n.X;
        float dark = 1 - light;
        byte Channel(float tint)
        {
            float value = light / (1 - dark * tint);
            // Adding 32768 leaves value*256 in the low mantissa byte.
            float shifted = value > AmbientMinimum ? value + 32768f : (float)(AmbientMinimum + 32768.0);
            return (byte)(BitConverter.SingleToUInt32Bits(shifted) - 1);
        }
        return (Channel(color.X), Channel(color.Y), Channel(color.Z));
    }
}
