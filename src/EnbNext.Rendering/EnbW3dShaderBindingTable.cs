using System.Collections;

namespace EnbNext.Rendering;

// Per-polygon shader selection is an index into immutable definitions. This
// table preserves expanded cardinality/order even when every value is equal.
internal sealed class EnbW3dShaderBindingTable : IReadOnlyList<EnbW3dRenderShaderBinding>
{
    private readonly EnbW3dRenderShaderBinding[] definitions;
    private readonly ushort[] indices;
    private EnbW3dShaderBindingTable(EnbW3dRenderShaderBinding[] definitions, ushort[] indices)
    { this.definitions=definitions; this.indices=indices; }
    internal int DefinitionCount => definitions.Length;
    public int Count => indices.Length;
    public EnbW3dRenderShaderBinding this[int index]
    {
        get
        {
            if((uint)index >= (uint)indices.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return definitions[indices[index]];
        }
    }
    public IEnumerator<EnbW3dRenderShaderBinding> GetEnumerator()
    {
        foreach(ushort index in indices) yield return definitions[index];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class ExactShaderComparer : IEqualityComparer<EnbW3dRenderShaderBinding>
    {
        internal static readonly ExactShaderComparer Instance=new();
        private static int Bits(float value)=>BitConverter.SingleToInt32Bits(value);
        public bool Equals(EnbW3dRenderShaderBinding x,EnbW3dRenderShaderBinding y)
        {
            if(!x.Equals(y))return false;
            var a=x.FixedFunction.TextureCombinerSemantics.BumpEnvironment;
            var b=y.FixedFunction.TextureCombinerSemantics.BumpEnvironment;
            return Bits(a.Matrix2X2.X)==Bits(b.Matrix2X2.X) && Bits(a.Matrix2X2.Y)==Bits(b.Matrix2X2.Y)
                && Bits(a.Matrix2X2.Z)==Bits(b.Matrix2X2.Z) && Bits(a.Matrix2X2.W)==Bits(b.Matrix2X2.W)
                && Bits(a.LuminanceScale)==Bits(b.LuminanceScale) && Bits(a.LuminanceOffset)==Bits(b.LuminanceOffset);
        }
        public int GetHashCode(EnbW3dRenderShaderBinding value)
        {
            var b=value.FixedFunction.TextureCombinerSemantics.BumpEnvironment;var h=new HashCode();h.Add(value);
            h.Add(Bits(b.Matrix2X2.X));h.Add(Bits(b.Matrix2X2.Y));h.Add(Bits(b.Matrix2X2.Z));h.Add(Bits(b.Matrix2X2.W));
            h.Add(Bits(b.LuminanceScale));h.Add(Bits(b.LuminanceOffset));return h.ToHashCode();
        }
    }

    internal static IReadOnlyList<EnbW3dRenderShaderBinding> Freeze(IEnumerable<EnbW3dRenderShaderBinding> source)
    {
        if(source is EnbW3dShaderBindingTable existing) return existing;
        var values=source as IReadOnlyList<EnbW3dRenderShaderBinding> ?? source.ToArray();
        if(values.Count < 32) return Array.AsReadOnly(values.ToArray());
        var map=new Dictionary<EnbW3dRenderShaderBinding,ushort>(ExactShaderComparer.Instance);
        var table=new List<EnbW3dRenderShaderBinding>();
        var indices=new ushort[values.Count];
        for(int i=0;i<values.Count;i++)
        {
            var value=values[i];
            if(!map.TryGetValue(value,out ushort index))
            {
                if(table.Count==ushort.MaxValue) return Array.AsReadOnly(values.ToArray());
                index=(ushort)table.Count;table.Add(value);map.Add(value,index);
            }
            indices[i]=index;
        }
        // Distinct expanded streams gain nothing from an additional index array.
        if(table.Count*4 > values.Count*3) return Array.AsReadOnly(values.ToArray());
        return new EnbW3dShaderBindingTable(table.ToArray(),indices);
    }
}
