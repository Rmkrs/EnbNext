using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>
/// Equality of the retained state hashed by 0092ED00. Names and material-table
/// indices are not identity. Mapper entries use reference identity, including
/// distinct clones (0092EB90/00951B60). Accidental native CRC collisions are not
/// reproduced. The caller supplies resolved runtime coordinates and mapper owners.
/// </summary>
public sealed class EnbCurrentRetailMaterialBatchIdentity : IEquatable<EnbCurrentRetailMaterialBatchIdentity>
{
    private readonly int[] _words;
    private readonly object?[] _mappers;
    private readonly int _hashCode;

    private EnbCurrentRetailMaterialBatchIdentity(int[] words, object?[] mappers)
    {
        _words=words;
        _mappers=mappers;
        var hash=new HashCode();
        foreach (int word in words) hash.Add(word);
        foreach (var mapper in mappers) hash.Add(mapper is null ? 0 : RuntimeHelpers.GetHashCode(mapper));
        _hashCode=hash.ToHashCode();
    }

    /// <summary>
    /// File-loaded backing material: diffuse RGBA, ambient/specular/emissive RGB
    /// with constructor-zero alpha, and power. Uses exact effective floats,
    /// including legacy material overrides. Coordinate indices are the final
    /// shared material state, not necessarily the current pass's descriptor.
    /// RuntimeWord64 is explicit; ordinary constructor/loader state is zero.
    /// </summary>
    public static EnbCurrentRetailMaterialBatchIdentity Create(
        EnbW3dMaterialUniforms uniforms, uint runtimeFlags,
        EnbW3dVertexLightingSemantics lighting, ReadOnlySpan<int> coordinateIndices,
        uint runtimeWord64, ReadOnlySpan<object?> mapperOwners)
    {
        if (coordinateIndices.Length!=8 || mapperOwners.Length!=8)
            throw new ArgumentException("A retained material has eight coordinate and mapper slots.");
        int[] words=new int[31];
        WriteWords(words,uniforms,runtimeFlags,lighting,coordinateIndices,runtimeWord64);
        return new(words,mapperOwners.ToArray());
    }

    internal bool Matches(EnbW3dMaterialUniforms uniforms,uint flags,EnbW3dVertexLightingSemantics lighting,
        int stage0,int stage1,uint word64,object? mapper0,object? mapper1)
    {
        Span<int> coordinates=stackalloc int[8]{stage0,stage1,2,3,4,5,6,7};
        Span<int> words=stackalloc int[31];
        WriteWords(words,uniforms,flags,lighting,coordinates,word64);
        if(!words.SequenceEqual(_words)||!ReferenceEquals(_mappers[0],mapper0)||!ReferenceEquals(_mappers[1],mapper1))return false;
        for(int i=2;i<8;i++)if(_mappers[i] is not null)return false;
        return true;
    }
    private static void WriteWords(Span<int> words,EnbW3dMaterialUniforms uniforms,uint runtimeFlags,
        EnbW3dVertexLightingSemantics lighting,ReadOnlySpan<int> coordinateIndices,uint runtimeWord64)
    {
        ReadOnlySpan<float> backing=stackalloc float[]{uniforms.Diffuse.X,uniforms.Diffuse.Y,uniforms.Diffuse.Z,uniforms.DiffuseAlpha,
            uniforms.Ambient.X,uniforms.Ambient.Y,uniforms.Ambient.Z,0,
            uniforms.Specular.X,uniforms.Specular.Y,uniforms.Specular.Z,0,
            uniforms.Emissive.X,uniforms.Emissive.Y,uniforms.Emissive.Z,0,uniforms.Power};
        for(int i=0;i<backing.Length;i++)words[i]=BitConverter.SingleToInt32Bits(backing[i]);
        words[17]=unchecked((int)runtimeFlags);words[18]=(int)lighting.DiffuseSource;
        words[19]=(int)lighting.AmbientSource;words[20]=(int)lighting.EmissiveSource;
        coordinateIndices.CopyTo(words.Slice(21,8));words[29]=lighting.LightingEnabled?1:0;words[30]=unchecked((int)runtimeWord64);
    }

    public bool Equals(EnbCurrentRetailMaterialBatchIdentity? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || !_words.AsSpan().SequenceEqual(other._words)) return false;
        for (int i=0;i<8;i++)
            if (!ReferenceEquals(_mappers[i],other._mappers[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is EnbCurrentRetailMaterialBatchIdentity other && Equals(other);

    public override int GetHashCode() => _hashCode;
}
