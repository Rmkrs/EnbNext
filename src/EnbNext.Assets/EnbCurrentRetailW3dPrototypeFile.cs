using System.Buffers.Binary;

namespace EnbNext.Assets;

/// <summary>An ordered file publication, before any named RenderObj creation.
/// 0091D270/0091D370 publishes 0600 definitions alongside ordinary prototypes;
/// 009A4540 acquires an aggregate's base only when that prototype is created.</summary>
public sealed record EnbCurrentRetailW3dPrototypeFile(EnbW3dModel Model,
    IReadOnlyList<EnbCurrentRetailW3dPrototypeFileEntry> Entries)
{
    // These named render families have dedicated decoders outside Model.
    // Their omission from Entries must never establish native name absence.
    public bool HasUnrepresentedRenderPrototypes { get; init; }
    public IReadOnlyList<(int ChunkOffset,EnbCurrentRetailParticleEmitterDefinition Definition)> LegacyParticleDefinitions {get;init;}=[];
    public bool HasOtherUnrepresentedRenderPrototypes {get;init;}
    public IReadOnlyList<(int ChunkOffset,string TextureName)> LegacyTextureConstructions {get;init;}=[];

    public EnbCurrentRetailW3dPrototypeFile WithCompiledModel(EnbW3dModel model)
        => this with {Model=model,Entries=Entries.Where(e=>e.Aggregate is not null)
            .Concat(model.PrototypeEntries.Select(e=>new EnbCurrentRetailW3dPrototypeFileEntry(e.ChunkOffset,e,null)))
            .OrderBy(e=>e.ChunkOffset).ToArray(),
            HasUnrepresentedRenderPrototypes=HasUnrepresentedRenderPrototypes||model.HasIncompleteRoots,
            HasOtherUnrepresentedRenderPrototypes=HasOtherUnrepresentedRenderPrototypes||model.HasIncompleteRoots};

    public EnbCurrentRetailW3dPrototypeFile BindLegacyParticles(IReadOnlyList<(int ChunkOffset,EnbCurrentRetailParticleEmitterDefinition Definition)> particles)
    {
        var model=Model.WithBoundLegacyParticles(particles);
        var entries=Entries.Where(entry=>!particles.Any(p=>p.ChunkOffset==entry.ChunkOffset)).Concat(model.PrototypeEntries.Where(entry=>entry.Kind==EnbW3dPrototypeKind.ParticleEmitter &&
            particles.Any(p=>p.ChunkOffset==entry.ChunkOffset)).Select(entry=>new EnbCurrentRetailW3dPrototypeFileEntry(entry.ChunkOffset,entry,null)))
            .OrderBy(entry=>entry.ChunkOffset).ToArray();
        return this with {Model=model,Entries=entries,HasUnrepresentedRenderPrototypes=HasOtherUnrepresentedRenderPrototypes};
    }

    internal static bool ContainsUnrepresentedRenderPrototypes(ReadOnlySpan<byte> bytes)
    {
        for(int offset=0;offset<bytes.Length;)
        {
            if(bytes.Length-offset<8)return true;
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+4)..])&0x7fffffffu;
            if(length>(uint)(bytes.Length-offset-8))return true;
            if(kind==0x741 && !EnbCurrentRetailSphereDefinition.TryDecode(bytes.Slice(offset,8+(int)length),out _))return true;
            if(kind==0x742 && !EnbCurrentRetailRingDefinition.TryDecode(bytes.Slice(offset,8+(int)length),out _))return true;
            if(kind==0x500 && (!EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.Slice(offset,8+(int)length),out var particle,out _) ||
                particle.RequiresLegacyTextureShaderResolution))return true;
            offset+=8+(int)length;
        }
        return false;
    }
    public static bool TryDecode(ReadOnlySpan<byte> bytes,out EnbCurrentRetailW3dPrototypeFile file,out string? diagnostic)
        => TryDecode(bytes,null,out file,out diagnostic);

    internal static bool TryDecode(ReadOnlySpan<byte> bytes,EnbW3dModel? model,out EnbCurrentRetailW3dPrototypeFile file,out string? diagnostic)
    {
        file=default!;diagnostic=null;
        if(model is null && !EnbW3dModel.TryDecodePrototypeContainer(bytes,out model))
        {diagnostic="Prototype file contains an undecoded ordinary model or malformed top-level chunk.";return false;}
        var entries=model.PrototypeEntries.Select(entry=>new EnbCurrentRetailW3dPrototypeFileEntry(entry.ChunkOffset,entry,null)).ToList();
        var pending=new List<(int,EnbCurrentRetailParticleEmitterDefinition)>();
        var legacyConstructions=new List<(int,string)>();
        bool otherUnrepresented=model.HasIncompleteRoots;
        for(int offset=0;offset<bytes.Length;)
        {
            //Retain the verified prefix; never scan for a new header after an
            //untrustworthy outer length or a truncated header.
            if(bytes.Length-offset<8)break; //009D4310: end of available headers.
            uint kind=BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
            uint payloadLength=BinaryPrimitives.ReadUInt32LittleEndian(bytes[(offset+4)..])&0x7fffffffu;
            if(payloadLength>(uint)(bytes.Length-offset-8)){otherUnrepresented=true;break;}
            int length=8+(int)payloadLength;
            bool legacyConstruction=kind==0x500 && EnbCurrentRetailParticleEmitterDecoder.TryReadLegacyTextureConstructionName(bytes.Slice(offset,length),out _);
            if(legacyConstruction)
            {
                EnbCurrentRetailParticleEmitterDecoder.TryReadLegacyTextureConstructionName(bytes.Slice(offset,length),out var textureName);
                legacyConstructions.Add((offset,textureName));
            }
            if(kind==0x600)
            {
                if(EnbCurrentRetailW3dAggregateDecoder.TryDecode(bytes.Slice(offset,length),out var aggregate,out _))
                    entries.Add(new(offset,null,aggregate){AggregateBytes=bytes.Slice(offset,length).ToArray()});
                else otherUnrepresented=true;
            }
            else if(legacyConstruction && EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.Slice(offset,length),out var particle,out _) &&
                particle.RequiresLegacyTextureShaderResolution)pending.Add((offset,particle));
            else if(ContainsUnrepresentedRenderPrototypes(bytes.Slice(offset,length)))otherUnrepresented=true;
            offset+=length;
        }
        entries.Sort((first,second)=>first.ChunkOffset.CompareTo(second.ChunkOffset));
        //Preserve the ordinary decoder's unsupported-family boundary. An
        //opened zero-byte file publishes nothing; nonempty undecoded data is
        //not proof that an OBJECT lookup should return the native NULL object.
        if(bytes.Length!=0 && entries.Count==0 && legacyConstructions.Count==0 && model.Meshes.Count==0 && model.Hierarchies.Count==0 && model.Animations.Count==0)
        {diagnostic="Nonempty prototype file has no decoded prototype or hierarchy payload.";return false;}
        file=new(model,entries.ToArray()) {HasUnrepresentedRenderPrototypes=otherUnrepresented||pending.Count!=0,
            HasOtherUnrepresentedRenderPrototypes=otherUnrepresented,LegacyParticleDefinitions=pending,LegacyTextureConstructions=legacyConstructions};
        return true;
    }
}

public sealed record EnbCurrentRetailW3dPrototypeFileEntry(int ChunkOffset,EnbW3dPrototypeEntry? Ordinary,
    EnbCurrentRetailW3dAggregateDefinition? Aggregate)
{
    //Keep the admitted definition's original bytes, independent of later file
    //changes. Reuse the same aggregate resolver for registered acquisitions.
    public ReadOnlyMemory<byte> AggregateBytes {get;init;}
}
