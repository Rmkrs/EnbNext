namespace EnbNext.Rendering;

internal static class EnbRetailSignedRandom
{
    // 009D0250 has signed arithmetic shifts; it is not MT19937.
    public static int Word(uint seed,uint counter)
    {
        ReadOnlySpan<uint> first=[0xBAA96887,0x1E17D32C,0x03BCDC3C,0x0F33D1B2];
        ReadOnlySpan<uint> second=[0x4B0F3B58,0xE874F0C3,0x6955C5A6,0x55A7CA46];
        unchecked
        {
            uint left=seed,right=counter;
            for(int i=0;i<4;i++)
            {
                uint previous=right,mixed=right^first[i];int high=(int)mixed>>16,low=(int)(mixed&65535);
                int sum=~(high*high)+low*low;
                uint folded=(uint)((sum>>16)|(sum<<16));
                right=left^((folded^second[i])+(uint)(high*low));left=previous;
            }
            return (int)right;
        }
    }
}
