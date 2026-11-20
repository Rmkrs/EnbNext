using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace EnbNext.Assets;

public enum EnbW3dPrototypeKind { Mesh, HierarchyModel, HierarchicalLod, Box, NullObject, Collection, LegacyLod, Dazzle, ParticleEmitter, Sphere, Ring, SpecialMaterial }

/// <summary>A top-level prototype loader entry in file order (0091D270).
/// Ordinal indexes its corresponding decoded collection.</summary>
public readonly record struct EnbW3dPrototypeEntry(EnbW3dPrototypeKind Kind, int Ordinal, int ChunkOffset);

/// <summary>
/// Safe, platform-neutral decoder for the generic W3D mesh structures already
/// established by the SCENE renderer research. This is intentionally a data
/// decoder only: it does not choose LODs, allocate GPU objects, or interpret
/// Net7 world state.
/// </summary>
public sealed class EnbW3dModel
{
    private const uint MeshChunk = 0x0000;
    private const uint HierarchyChunk = 0x0100;
    private const uint AnimationChunk = 0x0200;
    private const uint CompressedAnimationChunk = 0x0280;
    private const uint HierarchyModelChunk = 0x0300;
    private const uint HierarchicalLodChunk = 0x0700;
    private const uint ContainerFlag = 0x8000_0000;
    private const uint SizeMask = 0x7FFF_FFFF;
    private const int ChunkHeaderLength = 8;
    private const int MaximumDepth = 48;
    private const int MaximumMeshCount = 65_536;

    private EnbW3dModel(
        IReadOnlyList<EnbW3dMesh> meshes,
        IReadOnlyList<EnbW3dHierarchy> hierarchies,
        IReadOnlyList<EnbW3dHierarchyAnimation> animations,
        IReadOnlyList<EnbW3dHierarchyModel> hierarchyModels,
        IReadOnlyList<EnbW3dHierarchicalLod> hierarchicalLods, IReadOnlyList<EnbW3dBox> boxes,
        IReadOnlyList<EnbW3dPrototypeEntry> prototypeEntries, IReadOnlyList<int> hierarchyEntries, IReadOnlyList<EnbW3dNullObject> nullObjects, List<EnbCurrentRetailW3dCollectionDefinition> collections,
        List<EnbCurrentRetailW3dLegacyLodDefinition> legacyLods, List<EnbW3dDazzle> dazzles,
        List<EnbCurrentRetailParticleEmitterDefinition> particleEmitters,
        List<EnbCurrentRetailSphereDefinition> spheres,List<EnbCurrentRetailRingDefinition> rings,
        List<EnbCurrentRetailSpecialMaterialDefinition> specialMaterials)
    {
        Meshes = meshes;
        Hierarchies = hierarchies;
        Animations = animations;
        HierarchyModels = hierarchyModels;
        HierarchicalLods = hierarchicalLods;
        Boxes = boxes;
        NullObjects = nullObjects;
        Collections = collections;
        LegacyLods = legacyLods;
        Dazzles = dazzles;
        ParticleEmitters=particleEmitters;
        Spheres=spheres;Rings=rings;SpecialMaterials=specialMaterials;
        PrototypeEntries = prototypeEntries;
        TopLevelHierarchyOrdinals = hierarchyEntries;
    }

    public IReadOnlyList<EnbW3dMesh> Meshes { get; }

    public IReadOnlyList<EnbW3dBox> Boxes { get; }
    public IReadOnlyList<EnbW3dNullObject> NullObjects { get; }
    public IReadOnlyList<EnbCurrentRetailW3dCollectionDefinition> Collections { get; }
    public IReadOnlyList<EnbCurrentRetailW3dLegacyLodDefinition> LegacyLods { get; }

    public IReadOnlyList<EnbW3dDazzle> Dazzles { get; }
    public IReadOnlyList<EnbCurrentRetailParticleEmitterDefinition> ParticleEmitters {get;}
    public IReadOnlyList<EnbCurrentRetailSphereDefinition> Spheres {get;}
    public IReadOnlyList<EnbCurrentRetailRingDefinition> Rings {get;}
    public IReadOnlyList<EnbCurrentRetailSpecialMaterialDefinition> SpecialMaterials {get;}

    public EnbW3dModel WithBoundLegacyParticles(IReadOnlyList<(int ChunkOffset,EnbCurrentRetailParticleEmitterDefinition Definition)> particles)
    {
        var definitions=ParticleEmitters.ToList();var entries=PrototypeEntries.ToList();
        foreach(var particle in particles)
        {
            if(particle.Definition.RequiresLegacyTextureShaderResolution)throw new ArgumentException("Legacy shader is unresolved.",nameof(particles));
            int prior=entries.FindIndex(entry=>entry.Kind==EnbW3dPrototypeKind.ParticleEmitter && entry.ChunkOffset==particle.ChunkOffset);
            if(prior>=0)definitions[entries[prior].Ordinal]=particle.Definition;
            else
            {
                entries.Add(new(EnbW3dPrototypeKind.ParticleEmitter,definitions.Count,particle.ChunkOffset));
                definitions.Add(particle.Definition);
            }
        }
        entries.Sort((a,b)=>a.ChunkOffset.CompareTo(b.ChunkOffset));
        return new(Meshes,Hierarchies,Animations,HierarchyModels,HierarchicalLods,Boxes,entries,TopLevelHierarchyOrdinals,
            NullObjects,Collections.ToList(),LegacyLods.ToList(),Dazzles.ToList(),definitions,Spheres.ToList(),Rings.ToList(),SpecialMaterials.ToList()){HasIncompleteRoots=HasIncompleteRoots};
    }

    public IReadOnlyList<EnbW3dPrototypeEntry> PrototypeEntries { get; }
    public IReadOnlyList<int> TopLevelHierarchyOrdinals { get; }

    /// <summary>
    /// Authored W3D 0x100 hierarchy trees. Pivot names and indices are the
    /// model-side coordinate systems used by hierarchical render objects.
    /// </summary>
    public IReadOnlyList<EnbW3dHierarchy> Hierarchies { get; }

    /// <summary>
    /// Authored W3D hierarchy-animation data, including classic 0x0200 and
    /// current-retail compressed 0x0280 flavor 0. Channels remain asset data
    /// here; frame sampling and runtime transform composition belong to the
    /// presentation/renderer layer.
    /// </summary>
    public IReadOnlyList<EnbW3dHierarchyAnimation> Animations { get; }

    /// <summary>
    /// Authored W3D 0x300 hierarchy-model connection sets. These associate
    /// render-object names with pivot indices without applying any renderer
    /// transforms in the asset layer.
    /// </summary>
    public IReadOnlyList<EnbW3dHierarchyModel> HierarchyModels { get; }

    /// <summary>
    /// Authored W3D 0x700 HLOD membership records. Detail selection remains a
    /// renderer/runtime concern; this collection only preserves the alternatives
    /// and thresholds supplied by the asset.
    /// </summary>
    public IReadOnlyList<EnbW3dHierarchicalLod> HierarchicalLods { get; }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out EnbW3dModel model)
        => TryDecode(bytes,out model,false);

    //A prototype file may contain only aggregate definitions. Keep that file
    //admission separate from the ordinary drawable-model classifier.
    internal static bool TryDecodePrototypeContainer(ReadOnlySpan<byte> bytes,out EnbW3dModel model)
        => TryDecode(bytes,out model,true);

    //0091C290 initializes the global NULL prototype through00941580;
    //009415E0 creates the same named no-draw class as the0750 loader.
    //This is in-memory factory data, with no authored FILE payload.
    internal static EnbW3dModel CreateBuiltinNullObject()
        => new([],[],[],[],[],[],[new(EnbW3dPrototypeKind.NullObject,0,0)],[],[new("NULL",[])],[],[],[],[],[],[],[]);

    private static bool TryDecode(ReadOnlySpan<byte> bytes,out EnbW3dModel model,bool allowEmpty)
    {
        model = default!;
        if (bytes.Length < ChunkHeaderLength && !(allowEmpty && bytes.Length==0))
            return false;

        var boxes = new List<EnbW3dBox>();
        var nullObjects = new List<EnbW3dNullObject>();
        var collections = new List<EnbCurrentRetailW3dCollectionDefinition>();
        var legacyLods = new List<EnbCurrentRetailW3dLegacyLodDefinition>();
        var dazzles = new List<EnbW3dDazzle>();
        var particleEmitters=new List<EnbCurrentRetailParticleEmitterDefinition>();
        var spheres=new List<EnbCurrentRetailSphereDefinition>();
        var rings=new List<EnbCurrentRetailRingDefinition>();
        var specialMaterials=new List<EnbCurrentRetailSpecialMaterialDefinition>();
        var meshes = new List<EnbW3dMesh>();
        var hierarchies = new List<EnbW3dHierarchy>();
        var animations = new List<EnbW3dHierarchyAnimation>();
        var hierarchyModels = new List<EnbW3dHierarchyModel>();
        var hierarchicalLods = new List<EnbW3dHierarchicalLod>();
        var prototypeEntries = new List<EnbW3dPrototypeEntry>();
        var hierarchyEntries = new List<int>();
        bool complete=true;
        if(allowEmpty)
        {
            //0091D270 ignores each loader's result, then closes that root.
            //Only a verified outer extent permits moving to the next root.
            for(int offset=0;offset<bytes.Length;)
            {
                //009D4310 returns false on an incomplete next header;
                //0091D270 keeps the already published prefix.
                if(bytes.Length-offset<ChunkHeaderLength)break;
                if(!TryReadChunk(bytes,offset,bytes.Length,out var root)){complete=false;break;}
                complete &= WalkForModelParts(bytes,offset,root.PayloadEnd,0,meshes,hierarchies,animations,
                    hierarchyModels,hierarchicalLods,boxes,prototypeEntries,hierarchyEntries,nullObjects,
                    collections,legacyLods,dazzles,particleEmitters,spheres,rings,specialMaterials);
                offset=root.PayloadEnd;
            }
        }
        else complete=WalkForModelParts(
                bytes,
                0,
                bytes.Length,
                0,
                meshes,
                hierarchies,
                animations,
                hierarchyModels,
                hierarchicalLods, boxes, prototypeEntries, hierarchyEntries, nullObjects, collections, legacyLods, dazzles,particleEmitters,spheres,rings,specialMaterials);
        if ((!allowEmpty && !complete) ||
            (!allowEmpty && meshes.Count == 0 &&
             hierarchies.Count == 0 &&
             animations.Count == 0 &&
             hierarchyModels.Count == 0 &&
             hierarchicalLods.Count == 0 && boxes.Count == 0 && nullObjects.Count == 0 && collections.Count == 0 && legacyLods.Count == 0 && dazzles.Count == 0 && particleEmitters.Count==0 && spheres.Count==0 && rings.Count==0 && specialMaterials.Count==0))
        {
            return false;
        }

        model = new EnbW3dModel(meshes, hierarchies, animations, hierarchyModels, hierarchicalLods, boxes, prototypeEntries, hierarchyEntries, nullObjects, collections, legacyLods, dazzles,particleEmitters,spheres,rings,specialMaterials){HasIncompleteRoots=!complete};
        return true;
    }

    public bool HasIncompleteRoots {get;private init;}

    //Compilation can reject a decoded root (for example non-finite geometry).
    //Keep the surviving ordinals coherent across the source and GPU resource.
    public EnbW3dModel RetainCompiledRoots(IReadOnlySet<int> meshes,IReadOnlySet<int> hierarchies,IReadOnlySet<int> animations)
    {
        var meshMap=Enumerable.Range(0,Meshes.Count).Where(meshes.Contains).Select((old,index)=>(old,index)).ToDictionary(x=>x.old,x=>x.index);
        var hierarchyMap=Enumerable.Range(0,Hierarchies.Count).Where(hierarchies.Contains).Select((old,index)=>(old,index)).ToDictionary(x=>x.old,x=>x.index);
        return new(Meshes.Where((_,i)=>meshes.Contains(i)).ToArray(),Hierarchies.Where((_,i)=>hierarchies.Contains(i)).ToArray(),
            Animations.Where((_,i)=>animations.Contains(i)).ToArray(),HierarchyModels,HierarchicalLods,Boxes,
            PrototypeEntries.Where(e=>e.Kind!=EnbW3dPrototypeKind.Mesh || meshes.Contains(e.Ordinal))
                .Select(e=>e.Kind==EnbW3dPrototypeKind.Mesh?e with {Ordinal=meshMap[e.Ordinal]}:e).ToArray(),
            TopLevelHierarchyOrdinals.Where(hierarchies.Contains).Select(i=>hierarchyMap[i]).ToArray(),NullObjects,
            Collections.ToList(),LegacyLods.ToList(),Dazzles.ToList(),ParticleEmitters.ToList(),Spheres.ToList(),Rings.ToList(),SpecialMaterials.ToList())
            {HasIncompleteRoots=HasIncompleteRoots || meshes.Count!=Meshes.Count || hierarchies.Count!=Hierarchies.Count || animations.Count!=Animations.Count};
    }

    private static bool WalkForModelParts(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        int depth,
        List<EnbW3dMesh> meshes,
        List<EnbW3dHierarchy> hierarchies,
        List<EnbW3dHierarchyAnimation> animations,
        List<EnbW3dHierarchyModel> hierarchyModels,
        List<EnbW3dHierarchicalLod> hierarchicalLods, List<EnbW3dBox> boxes,
        List<EnbW3dPrototypeEntry> prototypeEntries, List<int> hierarchyEntries, List<EnbW3dNullObject> nullObjects, List<EnbCurrentRetailW3dCollectionDefinition> collections,
        List<EnbCurrentRetailW3dLegacyLodDefinition> legacyLods, List<EnbW3dDazzle> dazzles,
        List<EnbCurrentRetailParticleEmitterDefinition> particleEmitters,
        List<EnbCurrentRetailSphereDefinition> spheres,List<EnbCurrentRetailRingDefinition> rings,
        List<EnbCurrentRetailSpecialMaterialDefinition> specialMaterials)
    {
        if (depth > MaximumDepth || start < 0 || end < start || end > bytes.Length)
            return false;

        int offset = start;
        while (offset < end)
        {
            if (!TryReadChunk(bytes, offset, end, out ChunkView chunk))
                return false;

            // Known mesh/tree/HMODEL loaders open children by type, regardless
            // of the optional size hint (retail OpenChunk at 009D4310).
            if (chunk.Type == MeshChunk)
            {
                if (meshes.Count >= MaximumMeshCount)
                    return false;

                if (!TryDecodeMesh(bytes.Slice(chunk.PayloadStart, chunk.PayloadLength), out EnbW3dMesh mesh))
                    return false;
                meshes.Add(mesh);
                if (depth == 0) prototypeEntries.Add(new(EnbW3dPrototypeKind.Mesh, meshes.Count - 1, offset));
            }
            else if (chunk.Type == HierarchyChunk)
            {
                if (!TryDecodeHierarchy(
                        bytes.Slice(chunk.PayloadStart, chunk.PayloadLength),
                        out EnbW3dHierarchy hierarchy))
                {
                    return false;
                }

                hierarchies.Add(hierarchy with { LoadOffset = offset });
                if (depth == 0) hierarchyEntries.Add(hierarchies.Count - 1);
            }
            else if (chunk.Type == AnimationChunk)
            {
                if (!TryDecodeHierarchyAnimation(
                        bytes.Slice(chunk.PayloadStart, chunk.PayloadLength),
                        out EnbW3dHierarchyAnimation animation))
                {
                    return false;
                }

                animations.Add(animation with { LoadOffset = offset });
            }
            else if (chunk.Type == CompressedAnimationChunk)
            {
                if (!TryDecodeCompressedHierarchyAnimation(
                        bytes.Slice(chunk.PayloadStart, chunk.PayloadLength),
                        out EnbW3dHierarchyAnimation animation))
                {
                    return false;
                }

                animations.Add(animation with { LoadOffset = offset });
            }
            else if (chunk.Type == 0x02C0)
            {
                if (!EnbW3dTimeMappedAnimation.TryDecode(bytes.Slice(chunk.PayloadStart, chunk.PayloadLength), out var animation))
                    return false;
                animations.Add(animation with { LoadOffset = offset });
            }
            else if (chunk.Type == HierarchyModelChunk)
            {
                if (!TryDecodeHierarchyModel(
                        bytes.Slice(chunk.PayloadStart, chunk.PayloadLength),
                        out EnbW3dHierarchyModel hierarchyModel))
                {
                    return false;
                }

                hierarchyModels.Add(hierarchyModel);
                if (depth == 0) prototypeEntries.Add(new(EnbW3dPrototypeKind.HierarchyModel, hierarchyModels.Count - 1, offset));
            }
            else if (chunk.Type == HierarchicalLodChunk)
            {
                // Retail's typed HLOD loader opens child chunks regardless of
                // the optional container hint; FE_CLASS00 omits that hint.
                if (!TryDecodeHierarchicalLod(
                        bytes.Slice(chunk.PayloadStart, chunk.PayloadLength),
                        out EnbW3dHierarchicalLod hierarchicalLod))
                {
                    return false;
                }

                hierarchicalLods.Add(hierarchicalLod);
                if (depth == 0) prototypeEntries.Add(new(EnbW3dPrototypeKind.HierarchicalLod, hierarchicalLods.Count - 1, offset));
            }
            else if(chunk.Type==0x0500)
            {
                //00917450 retains the decoded definition in prototype+8;
                //009174C0 returns its name. Creation00917440 is separate.
                if(EnbCurrentRetailParticleEmitterDecoder.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var emitter,out _) &&
                    !emitter.RequiresLegacyTextureShaderResolution)
                {
                    particleEmitters.Add(emitter);
                    if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.ParticleEmitter,particleEmitters.Count-1,offset));
                }
                //Legacy conversion needs the load-time texture registry.
                //Until that binding exists, preserve its standalone decoder.
                //An unsupported particle definition stays outside the table;
                //PrototypeFile marks it as unrepresented, never proven absent.
            }
            else if(chunk.Type==0x0b00)
            {
                if(!EnbCurrentRetailSpecialMaterialDefinition.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var special))return false;
                specialMaterials.Add(special);
                if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.SpecialMaterial,specialMaterials.Count-1,offset));
            }
            else if(chunk.Type==0x0741)
            {
                //0090D8B0 retains the complete definition;0090E210 names it.
                if(EnbCurrentRetailSphereDefinition.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var sphere))
                {
                    spheres.Add(sphere);
                    if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.Sphere,spheres.Count-1,offset));
                }
            }
            else if(chunk.Type==0x0742)
            {
                //00912850/00912F30 publish independently of00913180 creation.
                if(EnbCurrentRetailRingDefinition.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var ring))
                {
                    rings.Add(ring);
                    if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.Ring,rings.Count-1,offset));
                }
            }
            else if (chunk.Type == 0x0400)
            {
                if(!EnbCurrentRetailW3dLegacyLodDecoder.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var legacyLod,out _))return false;
                legacyLods.Add(legacyLod);
                if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.LegacyLod,legacyLods.Count-1,offset));
            }
            else if (chunk.Type == 0x0420)
            {
                if(!EnbCurrentRetailW3dCollectionDecoder.TryDecode(bytes.Slice(offset,chunk.PayloadEnd-offset),out var collection,out _))return false;
                collections.Add(collection);
                if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.Collection,collections.Count-1,offset));
            }
            else if (chunk.Type == 0x0750)
            {
                //009416A0 copies48 bytes to prototype+8;009415C0 names
                //prototype+18. Partial reads need incoming stack bytes;
                //an unterminated inline name needs adjacent native storage.
                var payload=bytes.Slice(chunk.PayloadStart,chunk.PayloadLength);
                if(payload.Length<48)return false;
                int nul=payload.Slice(16,32).IndexOf((byte)0);if(nul<0)return false;
                nullObjects.Add(new(Encoding.Latin1.GetString(payload.Slice(16,nul)),Array.AsReadOnly(payload[..48].ToArray())));
                if(depth==0)prototypeEntries.Add(new(EnbW3dPrototypeKind.NullObject,nullObjects.Count-1,offset));
            }
            else if (chunk.Type == 0x0900)
            {
                //0096EE60/0096ED30 publish the named dazzle prototype through
                //the same ordered manager registry used by HLOD children.
                try
                {
                    var decoded = EnbW3dDazzle.Decode(bytes.Slice(offset, chunk.PayloadEnd - offset));
                    if (decoded.Count != 1) return false;
                    dazzles.Add(decoded[0]);
                    if (depth == 0) prototypeEntries.Add(new(EnbW3dPrototypeKind.Dazzle, dazzles.Count - 1, offset));
                }
                catch (InvalidDataException) { return false; }
            }
            else if (chunk.Type == 0x0740)
            {
                // 0099A830 reads 0x44 bytes; version/extra payload are not gates.
                // A short read leaves native stack bytes undefined; contain it here.
                var payload = bytes.Slice(chunk.PayloadStart, chunk.PayloadLength);
                if (payload.Length < 68) return false;
                boxes.Add(new EnbW3dBox(
                    BinaryPrimitives.ReadUInt32LittleEndian(payload),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]),
                    ReadNullTerminatedAsciiRaw(payload.Slice(8, 32)),
                    BinaryPrimitives.ReadUInt32LittleEndian(payload[40..]),
                    ReadVector3(payload.Slice(44, 12)), ReadVector3(payload.Slice(56, 12))));
                if (depth == 0) prototypeEntries.Add(new(EnbW3dPrototypeKind.Box, boxes.Count - 1, offset));
            }
            //0091D270 dispatches only the opened top-level type.0091D370
            //returns when no prototype loader matches;009D4370 then skips
            //the entire payload. A container hint does not recursively
            //publish embedded models or validate unknown payload bytes.

            offset = chunk.PayloadEnd;
        }

        return offset == end;
    }

    private static bool TryDecodeMesh(ReadOnlySpan<byte> payload, out EnbW3dMesh mesh)
    {
        mesh = default!;
        var state = new MeshDecodeState();
        if (!WalkMesh(payload, 0, payload.Length, 0, state, currentPass: null, currentStage: null) ||
            state.Header is null ||
            state.Vertices.Count != state.Header.VertexCount ||
            state.Triangles.Count != state.Header.TriangleCount ||
            (state.Normals.Count != 0 && state.Normals.Count != state.Vertices.Count) ||
            (state.VertexInfluences.Count != 0 && state.VertexInfluences.Count != state.Vertices.Count) ||
            ((state.Header.VertexChannels & EnbW3dVertexChannels.BoneId) != 0 &&
             state.VertexInfluences.Count != state.Vertices.Count) ||
            !ValidateTriangles(state.Triangles, state.Vertices.Count) ||
            !ValidateMaterialState(state))
        {
            return false;
        }

        mesh = new EnbW3dMesh(
            state.Header.Name,
            state.Header.ContainerName,
            state.Header.Version,
            state.Header.Attributes,
            state.Header.VertexChannels,
            state.Header.FaceChannels,
            state.Header.BoundingRadius,
            state.Vertices.ToImmutableArray(),
            state.Normals.ToImmutableArray(),
            state.VertexInfluences.ToArray(),
            state.Triangles.ToArray(),
            state.MaterialInfo,
            state.Shaders.ToArray(),
            state.Textures.Select(static texture => texture.Name).ToArray(),
            state.VertexMaterials.Select(static material => material.ToPublic()).ToArray(),
            state.MaterialPasses
                .Select(static pass => pass.ToPublic())
                .ToArray())
        {
            AuthoredSortLevel = state.Header.AuthoredSortLevel,
            BoundingBoxMin = state.Header.BoundingBoxMin,
            BoundingBoxMax = state.Header.BoundingBoxMax,
            BoundingSphereCenter = state.Header.BoundingSphereCenter,
            TextureInfos = state.Textures.Select(static texture => texture.Info).ToArray(),
            TextureAcquisitions = state.TextureAcquisitions.ToArray(),
            CurrentRetailLoadedTextureNames = state.CurrentRetailLoadedTextureNames.ToArray(),
            CurrentRetailLoadedTextureReferences = state.CurrentRetailLoadedTextureReferences.ToArray(),
            CurrentRetailLoadedMaterialReferences = state.CurrentRetailLoadedMaterialReferences.ToArray(),
            SecondaryMaterialDescriptorChunks = state.SecondaryMaterialDescriptorChunks.ToArray(),
            AuthoredNormalChunkType = state.AuthoredNormalChunkType,
            AuthoredVertexJoinMap = state.AuthoredVertexJoinMap,
            AuthoredAabTreePayload = state.AuthoredAabTreePayload,
            CurrentRetailMaterialVariantChunkType = state.CurrentRetailMaterialVariantChunkType,
            LegacyRootTextureCoordinates = state.LegacyRootTextureCoordinates.ToArray(),
            LegacyRootColor1 = state.LegacyRootColor1.ToArray(),
            CurrentRetailPrimaryTextureCoordinateSets = state.CurrentRetailPrimaryTextureCoordinateSets.ToArray(),
            CurrentRetailPrimaryTextureCoordinateBindings = state.CurrentRetailPrimaryTextureCoordinateBindings
                .OrderBy(static binding => binding.Key.PassOrdinal)
                .ThenBy(static binding => binding.Key.StageOrdinal)
                .Select(static binding => new EnbW3dPrimaryTextureCoordinateBinding(
                    binding.Key.PassOrdinal,
                    binding.Key.StageOrdinal,
                    binding.Value))
                .ToArray(),
            LegacyRootMaterialCount = state.Header.LegacyMaterialCount,
            LegacyRootMaterials = state.LegacyRootMaterials.ToArray(),
            LegacyRootPackedShaders = state.LegacyRootPackedShaders.ToArray(),
            LegacyRootTextures = state.LegacyRootTextures.ToArray(),
            LegacyRootMaterialMappings = state.LegacyRootMaterialMappings.ToArray(),
            CurrentRetailPrimaryPass0MaterialDescriptor = state.LegacyRootMaterialMappings.Count != 0
                ? new EnbW3dPrimaryPass0MaterialDescriptor(
                    state.PrimaryPass0Descriptor.UniformMaterial,
                    state.PrimaryPass0Descriptor.PerVertexMaterials ?? Array.Empty<EnbW3dPrimaryMaterialReference?>(),
                    state.PrimaryPass0Descriptor.UniformShader,
                    state.PrimaryPass0Descriptor.PerTriangleShaders ?? Array.Empty<EnbW3dPrimaryShaderReference>(),
                    state.PrimaryPass0Descriptor.UniformTextureStage0,
                    state.PrimaryPass0Descriptor.PerTriangleTexturesStage0 ?? Array.Empty<EnbW3dPrimaryTextureReference?>())
                : null,
            CurrentRetailGeometrySortRequestedByLegacyRoot = state.CurrentRetailGeometrySortRequestedByLegacyRoot,
            CurrentRetailGeometrySortRequested = state.CurrentRetailGeometrySortRequested,
            CurrentRetailPrimaryPass0HasColorSource = state.PrimaryColorSourcePassOrdinals.Contains(0),
        };
        return true;
    }

    private static bool WalkMesh(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        int depth,
        MeshDecodeState state,
        MaterialPassState? currentPass,
        TextureStageState? currentStage,
        VertexMaterialState? currentVertexMaterial = null,
        TextureDefinitionState? currentTexture = null)
    {
        if (depth > MaximumDepth || start < 0 || end < start || end > bytes.Length)
            return false;

        int offset = start;
        while (offset < end)
        {
            if (!TryReadChunk(bytes, offset, end, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> payload = bytes.Slice(chunk.PayloadStart, chunk.PayloadLength);
            bool suppressContainerWalk = false;
            switch (chunk.Type)
            {
                case W3dChunk.MeshHeader3:
                    if (!TryReadHeader(payload, out MeshHeader? header) || header is null || state.Header is not null)
                        return false;
                    state.Header = header;
                    state.CurrentRetailMaterialVariantChunkType = ResolveCurrentRetailMaterialVariantChunkType(header.Attributes);
                    break;

                case W3dChunk.Vertices:
                    if (!TryReadVector3Array(payload, state.Vertices))
                        return false;
                    break;

                case W3dChunk.LegacyRootTextureCoordinates
                    when depth == 0 && currentPass is null && currentStage is null:
                    // FUN_009BAD80 case 0x0005 allocates from payloadLength / 8
                    // but writes VertexCount entries. Current retail can overrun a
                    // malformed short child; EnbNext deliberately fails closed.
                    if (state.Header is null || payload.Length != checked(state.Header.VertexCount * 8))
                        return false;
                    state.LegacyRootTextureCoordinates.Clear();
                    if (!TryReadRuntimeTexCoords(payload, state.LegacyRootTextureCoordinates))
                        return false;
                    AssignCurrentRetailPrimaryTextureCoordinateSet(
                        state,
                        passOrdinal: 0,
                        stageOrdinal: 0,
                        state.LegacyRootTextureCoordinates);
                    break;

                case W3dChunk.LegacyRootColor1
                    when depth == 0 && currentPass is null:
                    // FUN_009BB950 consumes four authored bytes per vertex, but
                    // only RGB survives into runtime COLO. Alpha is always 255.
                    if (state.Header is null || payload.Length != checked(state.Header.VertexCount * 4))
                        return false;
                    if (!state.HasPrimaryColor1)
                    {
                        if (!TryReadLegacyRootColor1(payload, state.LegacyRootColor1))
                            return false;
                        state.HasPrimaryColor1 = true;
                    }
                    MarkPrimaryColorSource(state, passOrdinal: 0);
                    break;

                case W3dChunk.LegacyRootMaterials
                    when depth == 0 && currentPass is null:
                    if (state.Header is null ||
                        !TryReadLegacyRootMaterials(bytes, chunk.PayloadStart, chunk.PayloadEnd, state))
                    {
                        return false;
                    }
                    suppressContainerWalk = true;
                    break;

                case W3dChunk.LegacyRootMaterialSelectors
                    when depth == 0 && currentPass is null:
                    if (state.Header is null || !ApplyLegacyRootMaterialSelectors(payload, state))
                        return false;
                    break;

                case W3dChunk.VertexNormals:
                case W3dChunk.AlternateVertexNormals:
                    // Current retail allocates one fixed vertex-normal array from
                    // MeshHeader3.VertexCount before visiting children. Both 0x0003
                    // and 0x0004 overwrite that same array from element zero; a
                    // later accepted normal child therefore wins rather than
                    // appending another stream. Preserve the authored winner here.
                    state.Normals.Clear();
                    if (!TryReadVector3Array(payload, state.Normals))
                        return false;
                    state.AuthoredNormalChunkType = chunk.Type;
                    break;

                case W3dChunk.VertexInfluences:
                    if (!TryReadVertexInfluences(payload, state.VertexInfluences))
                        return false;
                    break;

                case W3dChunk.VertexJoinMap when depth==0:
                    //009BAD80 chunk0022→0092D360 reads one DWORD per
                    //header vertex. Repeated children overwrite the array;
                    //extra payload bytes are skipped by009D4370.
                    if(state.Header is null||payload.Length<(long)state.Header.VertexCount*4)return false;
                    var joinMap=new uint[state.Header.VertexCount];
                    for(int vertex=0;vertex<joinMap.Length;vertex++)joinMap[vertex]=BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(vertex*4,4));
                    state.AuthoredVertexJoinMap=Array.AsReadOnly(joinMap);
                    break;

                case 0x0090 when depth == 0:
                    //0092D400 replaces the previous tree for each root child.
                    //Retain the complete source; missing records require the
                    //loader's incoming storage, not fabricated decoded values.
                    state.AuthoredAabTreePayload = Array.AsReadOnly(payload.ToArray());
                    suppressContainerWalk = true;
                    break;

                case W3dChunk.Triangles:
                    if (!TryReadTriangles(payload, state.Triangles))
                        return false;
                    break;

                case W3dChunk.MaterialInfo:
                    if (!TryReadMaterialInfo(payload, out EnbW3dMaterialInfo info) || state.MaterialInfo is not null)
                        return false;
                    state.MaterialInfo = info;
                    break;

                case W3dChunk.Shaders:
                    if (!TryReadShaders(payload, state.Shaders))
                        return false;
                    break;

                case W3dChunk.TextureName when currentTexture is not null:
                    // FUN_0094CCC0 accepts repeated 0x0032 children and the last
                    // authored name wins inside one 0x0031 texture record.
                    currentTexture.Name = ReadNullTerminatedAscii(payload);
                    currentTexture.HasName = true;
                    break;

                case W3dChunk.TextureInfo when currentTexture is not null:
                    // Current retail reads exactly the first 12 bytes of each
                    // 0x0033 child. Repeated children overwrite the same local
                    // three-dword record, so preserve the last authored value.
                    if (!TryReadTextureInfo(payload, out EnbW3dTextureInfo textureInfo))
                        return false;
                    currentTexture.Info = textureInfo;
                    break;

                case W3dChunk.VertexMaterialName when currentVertexMaterial is not null:
                    if (currentVertexMaterial.HasName)
                        return false;
                    currentVertexMaterial.Name = ReadNullTerminatedAscii(payload);
                    currentVertexMaterial.HasName = true;
                    break;

                case W3dChunk.VertexMaterialInfo when currentVertexMaterial is not null:
                    if (currentVertexMaterial.Info is not null ||
                        !TryReadVertexMaterialInfo(payload, out EnbW3dVertexMaterialInfo materialInfo))
                    {
                        return false;
                    }
                    currentVertexMaterial.Info = materialInfo;
                    break;

                case W3dChunk.MapperArgs0 when currentVertexMaterial is not null:
                    if (currentVertexMaterial.Mapper0 is not null)
                        return false;
                    currentVertexMaterial.Mapper0 = ParseMapperArguments(ReadNullTerminatedAsciiRaw(payload));
                    break;

                case W3dChunk.MapperArgs1 when currentVertexMaterial is not null:
                    if (currentVertexMaterial.Mapper1 is not null)
                        return false;
                    currentVertexMaterial.Mapper1 = ParseMapperArguments(ReadNullTerminatedAsciiRaw(payload));
                    break;

                case W3dChunk.VertexMaterialIds when currentPass is not null:
                    if (currentPass.HasPrimaryVertexMaterialIds ||
                        (currentPass.Ordinal == 0 && state.PrimaryPass0Descriptor.HasMaterialBinding))
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage: null, chunk.Type, payload);
                        break;
                    }
                    if (!TryReadUInt32Array(payload, currentPass.VertexMaterialIds))
                        return false;
                    currentPass.HasPrimaryVertexMaterialIds = true;
                    if (currentPass.Ordinal == 0)
                        ApplyModernPrimaryPass0MaterialBinding(state, currentPass.VertexMaterialIds);
                    break;

                case W3dChunk.ShaderIds when currentPass is not null:
                    ObserveModernGeometrySortRequest(state, currentPass.Ordinal, payload);
                    if ((currentPass.Ordinal == 0 && IsPrimaryPass0ShaderOccupied(state)) ||
                        (currentPass.Ordinal != 0 && currentPass.HasPrimaryShaderIds))
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage: null, chunk.Type, payload);
                        break;
                    }
                    if (!TryReadUInt32Array(payload, currentPass.ShaderIds))
                        return false;
                    currentPass.HasPrimaryShaderIds = true;
                    if (currentPass.Ordinal == 0)
                        ApplyModernPrimaryPass0ShaderBinding(state, currentPass.ShaderIds);
                    break;

                case W3dChunk.DiffuseColor when currentPass is not null:
                    // FUN_009BC0D0 routes 0x003B to the temporary/secondary
                    // descriptor whenever the primary pass's color-source slot
                    // is already non-zero. Both accepted 0x003B and accepted
                    // 0x003C set that slot, so 0x003C -> 0x003B is asymmetric:
                    // the later 0x003B is secondary rather than another primary
                    // COLO operation.
                    if (currentPass.HasPrimaryColorSource)
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage: null, chunk.Type, payload);
                        break;
                    }
                    if (!TryReadColors(payload, currentPass.DiffuseColors))
                        return false;
                    state.HasPrimaryColor1 = true;
                    MarkPrimaryColorSource(state, currentPass.Ordinal);
                    currentPass.ColorChunkOrder.Add(EnbW3dMaterialPassColorChunkKind.DiffuseColor);
                    break;

                case W3dChunk.DiffuseIllumination when currentPass is not null:
                    // FUN_009BC3A0 uses one descriptor-global conflict flag for
                    // 0x003C, not the per-pass source slot used by 0x003B.
                    if (state.HasPrimaryDiffuseIllumination)
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage: null, chunk.Type, payload);
                        break;
                    }
                    if (!TryReadColors(payload, currentPass.DiffuseIllumination))
                        return false;
                    state.HasPrimaryDiffuseIllumination = true;
                    state.HasPrimaryColor1 = true;
                    MarkPrimaryColorSource(state, currentPass.Ordinal);
                    currentPass.ColorChunkOrder.Add(EnbW3dMaterialPassColorChunkKind.DiffuseIllumination);
                    break;

                case W3dChunk.SpecularColor when currentPass is not null:
                    if (!TryReadColors(payload, currentPass.SpecularColors))
                        return false;
                    break;

                case W3dChunk.TextureIds when currentPass is not null && currentStage is not null:
                    if (currentStage.HasPrimaryTextureIds ||
                        (currentPass.Ordinal == 0 && currentStage.Ordinal == 0 &&
                         state.PrimaryPass0Descriptor.HasTextureStage0Binding))
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage, chunk.Type, payload);
                        break;
                    }
                    if (!TryReadUInt32Array(payload, currentStage.TextureIds))
                        return false;
                    currentStage.HasPrimaryTextureIds = true;
                    if (currentPass.Ordinal == 0 && currentStage.Ordinal == 0)
                        ApplyModernPrimaryPass0TextureBinding(state, currentStage.TextureIds);
                    break;

                case W3dChunk.LegacyRootTextureCoordinates when currentPass is not null && currentStage is not null:
                case W3dChunk.StageTexCoords when currentPass is not null && currentStage is not null:
                    // Modern stage coordinates first consult the primary
                    // descriptor binding. A preceding root 0x0005 therefore
                    // diverts this child to the secondary descriptor. A later
                    // root 0x0005 bypasses this gate and can rewrite the primary
                    // binding while the modern set remains in the finite pool.
                    if (HasCurrentRetailPrimaryTextureCoordinateBinding(state, currentPass.Ordinal, currentStage.Ordinal))
                    {
                        PreserveSecondaryMaterialDescriptorChunk(state, currentPass, currentStage, chunk.Type, payload);
                        break;
                    }
                    currentStage.RuntimeTexCoords.Clear();
                    if (!TryReadRuntimeTexCoords(payload, currentStage.RuntimeTexCoords))
                        return false;
                    currentStage.HasPrimaryRuntimeTexCoords = AssignCurrentRetailPrimaryTextureCoordinateSet(
                        state,
                        currentPass.Ordinal,
                        currentStage.Ordinal,
                        currentStage.RuntimeTexCoords) >= 0;
                    break;
            }

            // Current retail opens these schema containers by type. The high
            // size bit is an optional hint, absent throughout older missiles.
            // Unknown chunks retain the existing explicit-hint traversal rule.
            bool knownMaterialContainer = chunk.Type is W3dChunk.VertexMaterials or W3dChunk.VertexMaterial or
                W3dChunk.Textures or W3dChunk.Texture or W3dChunk.MaterialPass or W3dChunk.TextureStage;
            if ((chunk.IsContainer || knownMaterialContainer) && !suppressContainerWalk)
            {
                // Current retail does not recursively merge all four root-level
                // material-variant containers (0x0023..0x0026). R4I_TempMeshLoader
                // derives one active container type from MeshHeader3.Attributes and
                // FUN_009BC9F0 consumes only the matching root container. Preserve
                // that selection boundary before material parsing reaches Rendering.
                if (depth == 0 && IsMaterialVariantContainer(chunk.Type))
                {
                    if (state.Header is null)
                        return false;
                    if (state.CurrentRetailMaterialVariantChunkType != chunk.Type)
                    {
                        offset = chunk.PayloadEnd;
                        continue;
                    }
                }

                MaterialPassState? childPass = currentPass;
                TextureStageState? childStage = currentStage;
                VertexMaterialState? childVertexMaterial = currentVertexMaterial;
                TextureDefinitionState? childTexture = currentTexture;

                if (chunk.Type == W3dChunk.Texture)
                {
                    childTexture = new TextureDefinitionState(state.Textures.Count);
                    state.Textures.Add(childTexture);
                }
                else if (chunk.Type == W3dChunk.MaterialPass)
                {
                    int passOrdinal = state.MaterialPasses.Count;
                    childPass = new MaterialPassState(passOrdinal)
                    {
                        HasPrimaryColorSource = state.PrimaryColorSourcePassOrdinals.Contains(passOrdinal),
                    };
                    state.MaterialPasses.Add(childPass);
                    childStage = null;
                }
                else if (chunk.Type == W3dChunk.TextureStage)
                {
                    if (currentPass is null)
                        return false;
                    childStage = new TextureStageState(currentPass.TextureStages.Count);
                    currentPass.TextureStages.Add(childStage);
                }
                else if (chunk.Type == W3dChunk.VertexMaterial)
                {
                    childVertexMaterial = new VertexMaterialState(state.VertexMaterials.Count);
                    state.VertexMaterials.Add(childVertexMaterial);
                    //009BAD80 case2A appends every modern material to the same
                    // temporary C8/D4 table used by the legacy loader.
                    state.CurrentRetailLoadedMaterialReferences.Add(new(
                        EnbW3dPrimaryMaterialSourceKind.ModernVertexMaterial,
                        checked((uint)childVertexMaterial.Ordinal)));
                }

                if (!WalkMesh(
                        bytes,
                        chunk.PayloadStart,
                        chunk.PayloadEnd,
                        depth + 1,
                        state,
                        childPass,
                        childStage,
                        childVertexMaterial,
                        childTexture))
                {
                    return false;
                }
                if (chunk.Type == W3dChunk.Texture && childTexture is not null)
                {
                    state.TextureAcquisitions.Add(new(childTexture.Name, childTexture.Info));
                    state.CurrentRetailLoadedTextureNames.Add(childTexture.Name);
                    state.CurrentRetailLoadedTextureReferences.Add(new(EnbW3dPrimaryTextureSourceKind.ModernTexture, (uint)childTexture.Ordinal));
                }
            }

            offset = chunk.PayloadEnd;
        }

        return offset == end;
    }

    private static bool TryReadLegacyRootMaterials(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        MeshDecodeState state)
    {
        const uint RetailShaderBaseline = 0x0008_441Bu;
        MeshHeader header = state.Header!;
        int offset = start;
        for (int ordinal = 0; ordinal < header.LegacyMaterialCount; ordinal++)
        {
            if (!TryReadChunk(bytes, offset, end, out ChunkView record) ||
                record.Type != W3dChunk.LegacyRootMaterial ||
                !TryReadLegacyRootMaterialRecord(bytes, record.PayloadStart, record.PayloadEnd, ordinal, state))
            {
                return false;
            }

            offset = record.PayloadEnd;
        }

        // FUN_009BB260 binds the first entries of the process-local dedup tables
        // after every root 0x0015 load. These uniform setters do not clear any
        // already allocated expanded descriptor arrays, so the latter continue
        // to win in the current descriptor accessors.
        if (state.CurrentRetailLoadedMaterialReferences.Count != 0)
        {
            state.PrimaryPass0Descriptor.UniformMaterial = state.CurrentRetailLoadedMaterialReferences[0];
        }
        if (state.LegacyRootTextures.Count != 0)
        {
            state.PrimaryPass0Descriptor.UniformTextureStage0 = new EnbW3dPrimaryTextureReference(
                EnbW3dPrimaryTextureSourceKind.LegacyRootTexture,
                0u);
        }
        if (state.LegacyRootPackedShaders.Count != 0)
        {
            state.PrimaryPass0Descriptor.UniformShader =
                EnbW3dPrimaryShaderReference.FromPackedRetailState(state.LegacyRootPackedShaders[0]);
            state.PrimaryPass0Descriptor.HasUniformShaderBinding = true;
        }
        else
        {
            state.PrimaryPass0Descriptor.UniformShader =
                EnbW3dPrimaryShaderReference.FromPackedRetailState(RetailShaderBaseline);
            // No material record means current retail issued no primary shader setter.
            state.PrimaryPass0Descriptor.HasUniformShaderBinding = false;
        }

        // Current retail closes the parent chunk after consuming exactly the
        // MeshHeader3 material count. Any trailing bytes/children are skipped by
        // that close rather than promoted into another material record.
        return true;
    }

    private static bool TryReadLegacyRootMaterialRecord(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        int ordinal,
        MeshDecodeState state)
    {
        int offset = start;
        if (!TryReadChunk(bytes, offset, end, out ChunkView nameChunk) ||
            nameChunk.Type != W3dChunk.LegacyRootMaterialName ||
            nameChunk.PayloadLength > 256)
        {
            return false;
        }
        string name = ReadNullTerminatedAscii(bytes.Slice(nameChunk.PayloadStart, nameChunk.PayloadLength));
        offset = nameChunk.PayloadEnd;

        if (!TryReadChunk(bytes, offset, end, out ChunkView infoChunk) ||
            infoChunk.Type != W3dChunk.LegacyRootMaterialInfo ||
            infoChunk.PayloadLength < 0x2C)
        {
            return false;
        }

        byte[] rawInfo = bytes.Slice(infoChunk.PayloadStart, 0x2C).ToArray();
        if (!TryDecodeLegacyRootMaterialInfo(rawInfo, out LegacyRootMaterialRuntime material))
            return false;
        offset = infoChunk.PayloadEnd;

        uint packedShader = 0x0008_441Bu;
        if ((rawInfo[0] & 1) != 0)
            packedShader = (packedShader & 0xFFFF_BFB7u) | 0x0000_80A0u;

        // The older 0x0015 path sets MeshModel+0x18 SORT here, before optional
        // 0x0019/0x001E texture children can rewrite packedShader.
        if ((packedShader & 0x0000_00E0u) != 0)
        {
            state.CurrentRetailGeometrySortRequestedByLegacyRoot = true;
            state.CurrentRetailGeometrySortRequested = true;
        }

        LegacyRootTextureRuntime? selectedTexture = null;
        while (offset < end)
        {
            if (!TryReadChunk(bytes, offset, end, out ChunkView optional))
                return false;

            if (optional.Type == W3dChunk.LegacyRootTexture)
            {
                if (!TryReadLegacyRootTexture(bytes, optional.PayloadStart, optional.PayloadEnd, false, out selectedTexture))
                {
                    return false;
                }
                packedShader |= 0x0001_0000u;
                state.TextureAcquisitions.Add(new(selectedTexture!.Name, null));
            }
            else if (optional.Type == W3dChunk.LegacyRootAlternateTexture && material.Diffuse == Vector3.Zero)
            {
                if (!TryReadLegacyRootTexture(bytes, optional.PayloadStart, optional.PayloadEnd, true, out selectedTexture))
                {
                    return false;
                }
                packedShader = (packedShader & 0xFFFF_633Fu) | 0x0001_4020u;
                state.TextureAcquisitions.Add(new(selectedTexture!.Name, null));
            }

            // Unknown optional children, and 0x001E when diffuse is non-zero,
            // are skipped wholesale by current retail.
            offset = optional.PayloadEnd;
        }

        if ((packedShader & 0x0001_0000u) == 0)
        {
            material = material with
            {
                Ambient = material.Diffuse,
                Diffuse = Vector3.Zero,
            };
        }

        uint materialCrc = ComputeCurrentRetailLegacyMaterialCrc32(material);
        int materialIndex = state.CurrentRetailLegacyMaterialCrcs.IndexOf(materialCrc);
        if (materialIndex < 0) materialIndex = state.CurrentRetailLegacyMaterialCrcs.Count;
        //009BDBC7 compares the CRC search index against D4 (material count),
        // not EC (CRC count). Modern entries do not append a CRC. Preserve this
        // exact distinction when modern entries precede a legacy record.
        if (materialIndex == state.CurrentRetailLoadedMaterialReferences.Count)
        {
            int legacyIndex = state.LegacyRootMaterials.Count;
            state.LegacyRootMaterials.Add(new EnbW3dLegacyRootMaterial(
                legacyIndex,
                name,
                rawInfo[0],
                material.Diffuse,
                material.DiffuseAlpha,
                material.Ambient,
                material.Specular,
                material.Emissive,
                material.Power,
                materialCrc,
                rawInfo));
            state.CurrentRetailLoadedMaterialReferences.Add(new(
                EnbW3dPrimaryMaterialSourceKind.LegacyRootMaterial, checked((uint)legacyIndex)));
            state.CurrentRetailLegacyMaterialCrcs.Add(materialCrc);
        }

        int shaderIndex = state.LegacyRootPackedShaders.IndexOf(packedShader);
        if (shaderIndex < 0)
        {
            shaderIndex = state.LegacyRootPackedShaders.Count;
            state.LegacyRootPackedShaders.Add(packedShader);
        }

        int textureIndex = -1;
        if (selectedTexture is LegacyRootTextureRuntime texture)
        {
            //009BDB10 searches the shared temporary table, including earlier
            // modern entries;009BAD80 appends modern entries without dedup.
            textureIndex = state.LegacyRootTextures.FindIndex(value =>
                string.Equals(value.Name, texture.Name, StringComparison.OrdinalIgnoreCase));
            if (textureIndex < 0)
            {
                textureIndex = state.LegacyRootTextures.Count;
                state.LegacyRootTextures.Add(new EnbW3dLegacyRootTexture(
                    textureIndex,
                    texture.Name,
                    texture.SelectedByAlternate001E,
                    texture.RawInfo));
            }
            if (!state.CurrentRetailLoadedTextureNames.Contains(texture.Name, StringComparer.OrdinalIgnoreCase))
            {
                state.CurrentRetailLoadedTextureNames.Add(texture.Name);
                state.CurrentRetailLoadedTextureReferences.Add(new(EnbW3dPrimaryTextureSourceKind.LegacyRootTexture, (uint)textureIndex));
            }
        }

        state.LegacyRootMaterialMappings.Add(new EnbW3dLegacyRootMaterialMapping(
            materialIndex,
            shaderIndex,
            textureIndex));
        return true;
    }

    private static bool TryReadLegacyRootTexture(
        ReadOnlySpan<byte> bytes,
        int start,
        int end,
        bool selectedByAlternate001E,
        out LegacyRootTextureRuntime? texture)
    {
        texture = null;
        int offset = start;
        if (!TryReadChunk(bytes, offset, end, out ChunkView nameChunk) ||
            nameChunk.Type != W3dChunk.LegacyRootTextureName ||
            nameChunk.PayloadLength >= 0x200)
        {
            return false;
        }
        string name = ReadNullTerminatedAscii(bytes.Slice(nameChunk.PayloadStart, nameChunk.PayloadLength));
        offset = nameChunk.PayloadEnd;

        if (!TryReadChunk(bytes, offset, end, out ChunkView infoChunk) ||
            infoChunk.Type != W3dChunk.LegacyRootTextureInfo ||
            infoChunk.PayloadLength < 8)
        {
            return false;
        }

        // The active current-client path reads these eight bytes but never
        // consumes them before looking up the texture by name.
        texture = new LegacyRootTextureRuntime(
            name,
            selectedByAlternate001E,
            bytes.Slice(infoChunk.PayloadStart, 8).ToArray());
        return true;
    }

    private static bool TryDecodeLegacyRootMaterialInfo(
        ReadOnlySpan<byte> raw,
        out LegacyRootMaterialRuntime material)
    {
        material = default;
        if (raw.Length < 0x2C)
            return false;

        Vector3 diffuse = Vector3.Multiply(ReadLegacyRgb(raw.Slice(4, 3)), ReadLegacyRgb(raw.Slice(0x14, 3)));
        Vector3 specular = Vector3.Multiply(ReadLegacyRgb(raw.Slice(8, 3)), ReadLegacyRgb(raw.Slice(0x18, 3)));
        Vector3 emissive = ReadLegacyRgb(raw.Slice(0x0C, 3));
        Vector3 ambient = ReadLegacyRgb(raw.Slice(0x10, 3));
        float power = ReadSingle(raw.Slice(0x1C, 4));
        float diffuseAlpha = ReadSingle(raw.Slice(0x20, 4));
        if (!IsFinite(diffuse) || !IsFinite(specular) || !IsFinite(emissive) || !IsFinite(ambient) ||
            !float.IsFinite(power) || !float.IsFinite(diffuseAlpha))
        {
            return false;
        }

        material = new LegacyRootMaterialRuntime(diffuse, diffuseAlpha, ambient, specular, emissive, power);
        return true;
    }

    private static Vector3 ReadLegacyRgb(ReadOnlySpan<byte> bytes)
    {
        const float scale = 1.0f / 255.0f;
        return new Vector3(bytes[0] * scale, bytes[1] * scale, bytes[2] * scale);
    }

    private static uint ComputeCurrentRetailLegacyMaterialCrc32(LegacyRootMaterialRuntime material)
    {
        Span<byte> backing = stackalloc byte[0x44];
        backing.Clear();
        WriteVector3(backing.Slice(0x00, 12), material.Diffuse);
        WriteSingle(backing.Slice(0x0C, 4), material.DiffuseAlpha);
        WriteVector3(backing.Slice(0x10, 12), material.Ambient);
        WriteVector3(backing.Slice(0x20, 12), material.Specular);
        WriteVector3(backing.Slice(0x30, 12), material.Emissive);
        WriteSingle(backing.Slice(0x40, 4), material.Power);

        uint crc = ComputeCurrentRetailCrc32(backing, 0);
        Span<byte> zero4 = stackalloc byte[4];
        zero4.Clear();
        crc = ComputeCurrentRetailCrc32(zero4, crc); // material object +0x0C
        crc = ComputeCurrentRetailCrc32(zero4, crc); // +0x18
        crc = ComputeCurrentRetailCrc32(zero4, crc); // +0x10
        crc = ComputeCurrentRetailCrc32(zero4, crc); // +0x14

        Span<byte> coordinateIndices = stackalloc byte[0x20];
        for (int index = 0; index < 8; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(coordinateIndices.Slice(index * 4, 4), checked((uint)index));
        crc = ComputeCurrentRetailCrc32(coordinateIndices, crc);
        Span<byte> zero1 = stackalloc byte[1];
        zero1[0] = 0;
        crc = ComputeCurrentRetailCrc32(zero1, crc);
        crc = ComputeCurrentRetailCrc32(zero4, crc);
        return crc;
    }

    private static bool ApplyLegacyRootMaterialSelectors(ReadOnlySpan<byte> payload, MeshDecodeState state)
    {
        MeshHeader header = state.Header!;
        // FUN_009BB710 returns immediately when the header count is exactly one;
        // the entire 0x0021 payload is ignored by the parent chunk close.
        if (header.LegacyMaterialCount == 1)
            return true;

        if (header.LegacyMaterialCount <= 0 ||
            state.LegacyRootMaterialMappings.Count == 0 ||
            state.Triangles.Count != header.TriangleCount ||
            payload.Length < checked(header.TriangleCount * 2))
        {
            return false;
        }

        bool expandMaterials = state.CurrentRetailLoadedMaterialReferences.Count > 1;
        bool expandTextures = state.LegacyRootTextures.Count > 1;
        bool expandShaders = state.LegacyRootPackedShaders.Count > 1;
        EnbW3dLegacyRootMaterialMapping first = state.LegacyRootMaterialMappings[0];

        if (!expandMaterials)
        {
            if ((uint)first.MaterialIndex >= (uint)state.CurrentRetailLoadedMaterialReferences.Count)
                return false;
            state.PrimaryPass0Descriptor.UniformMaterial = state.CurrentRetailLoadedMaterialReferences[first.MaterialIndex];
        }

        if (!expandTextures)
        {
            state.PrimaryPass0Descriptor.UniformTextureStage0 = first.TextureIndex < 0
                ? null
                : new EnbW3dPrimaryTextureReference(
                    EnbW3dPrimaryTextureSourceKind.LegacyRootTexture,
                    checked((uint)first.TextureIndex));
        }

        if (!expandShaders)
        {
            if ((uint)first.ShaderIndex >= (uint)state.LegacyRootPackedShaders.Count)
                return false;
            state.PrimaryPass0Descriptor.UniformShader = EnbW3dPrimaryShaderReference.FromPackedRetailState(
                state.LegacyRootPackedShaders[first.ShaderIndex]);
            state.PrimaryPass0Descriptor.HasUniformShaderBinding = true;
        }

        if (expandMaterials && state.PrimaryPass0Descriptor.PerVertexMaterials is null)
            state.PrimaryPass0Descriptor.PerVertexMaterials = new EnbW3dPrimaryMaterialReference?[header.VertexCount];
        if (expandTextures && state.PrimaryPass0Descriptor.PerTriangleTexturesStage0 is null)
            state.PrimaryPass0Descriptor.PerTriangleTexturesStage0 = new EnbW3dPrimaryTextureReference?[header.TriangleCount];
        if (expandShaders && state.PrimaryPass0Descriptor.PerTriangleShaders is null)
            state.PrimaryPass0Descriptor.PerTriangleShaders = new EnbW3dPrimaryShaderReference[header.TriangleCount];

        for (int triangleIndex = 0; triangleIndex < header.TriangleCount; triangleIndex++)
        {
            ushort selector = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(triangleIndex * 2, 2));
            if (selector >= state.LegacyRootMaterialMappings.Count)
                return false;

            EnbW3dLegacyRootMaterialMapping mapping = state.LegacyRootMaterialMappings[selector];
            if (expandShaders)
            {
                if ((uint)mapping.ShaderIndex >= (uint)state.LegacyRootPackedShaders.Count)
                    return false;
                state.PrimaryPass0Descriptor.PerTriangleShaders![triangleIndex] =
                    EnbW3dPrimaryShaderReference.FromPackedRetailState(state.LegacyRootPackedShaders[mapping.ShaderIndex]);
            }

            if (expandTextures)
            {
                state.PrimaryPass0Descriptor.PerTriangleTexturesStage0![triangleIndex] = mapping.TextureIndex < 0
                    ? null
                    : new EnbW3dPrimaryTextureReference(
                        EnbW3dPrimaryTextureSourceKind.LegacyRootTexture,
                        checked((uint)mapping.TextureIndex));
            }

            if (expandMaterials)
            {
                if ((uint)mapping.MaterialIndex >= (uint)state.CurrentRetailLoadedMaterialReferences.Count)
                    return false;
                var reference = state.CurrentRetailLoadedMaterialReferences[mapping.MaterialIndex];
                EnbW3dTriangle triangle = state.Triangles[triangleIndex];
                state.PrimaryPass0Descriptor.PerVertexMaterials![checked((int)triangle.Vertex0)] = reference;
                state.PrimaryPass0Descriptor.PerVertexMaterials[checked((int)triangle.Vertex1)] = reference;
                state.PrimaryPass0Descriptor.PerVertexMaterials[checked((int)triangle.Vertex2)] = reference;
            }
        }

        return true;
    }

    private static void ApplyModernPrimaryPass0MaterialBinding(MeshDecodeState state, IReadOnlyList<uint> ids)
    {
        if (ids.Count == 1)
        {
            state.PrimaryPass0Descriptor.UniformMaterial = ResolveLoadedMaterialReference(state, ids[0]);
        }
        else if (state.Header is not null && ids.Count == state.Header.VertexCount)
        {
            state.PrimaryPass0Descriptor.PerVertexMaterials = ids
                .Select(id => (EnbW3dPrimaryMaterialReference?)ResolveLoadedMaterialReference(state, id))
                .ToArray();
        }
    }

    private static EnbW3dPrimaryMaterialReference ResolveLoadedMaterialReference(MeshDecodeState state, uint index)
        => index < (uint)state.CurrentRetailLoadedMaterialReferences.Count
            ? state.CurrentRetailLoadedMaterialReferences[(int)index]
            : new(EnbW3dPrimaryMaterialSourceKind.ModernVertexMaterial, index);

    private static void ApplyModernPrimaryPass0ShaderBinding(MeshDecodeState state, IReadOnlyList<uint> ids)
    {
        if (ids.Count == 1)
        {
            state.PrimaryPass0Descriptor.UniformShader =
                EnbW3dPrimaryShaderReference.FromModernShader(ids[0]);
            state.PrimaryPass0Descriptor.HasUniformShaderBinding = true;
        }
        else if (state.Header is not null && ids.Count == state.Header.TriangleCount)
        {
            state.PrimaryPass0Descriptor.PerTriangleShaders = ids
                .Select(static id => EnbW3dPrimaryShaderReference.FromModernShader(id))
                .ToArray();
        }
    }

    private static void ApplyModernPrimaryPass0TextureBinding(MeshDecodeState state, IReadOnlyList<uint> ids)
    {
        if (ids.Count == 1)
        {
            state.PrimaryPass0Descriptor.UniformTextureStage0 = new EnbW3dPrimaryTextureReference(
                EnbW3dPrimaryTextureSourceKind.ModernTexture,
                ids[0]);
        }
        else if (state.Header is not null && ids.Count == state.Header.TriangleCount)
        {
            state.PrimaryPass0Descriptor.PerTriangleTexturesStage0 = ids
                .Select(static id => id == uint.MaxValue
                    ? (EnbW3dPrimaryTextureReference?)null
                    : new EnbW3dPrimaryTextureReference(
                        EnbW3dPrimaryTextureSourceKind.ModernTexture,
                        id))
                .ToArray();
        }
    }

    private static bool IsPrimaryPass0ShaderOccupied(MeshDecodeState state)
        => state.PrimaryPass0Descriptor.HasUniformShaderBinding ||
           state.PrimaryPass0Descriptor.PerTriangleShaders is not null;

    private static uint PackCurrentRetailShader(EnbW3dShader shader)
    {
        ReadOnlySpan<byte> raw = shader.Raw.Span;
        uint packed = 0x0008_441Bu;
        packed = (packed & 0xFFFF_FFF8u) | raw[0];
        packed = ((uint)(raw[1] | 2) << 3) | (packed & 0xFFFF_FFF7u);
        packed = ((uint)(raw[3] & 0xE7) << 5) | (packed & 0xFFFF_FC1Fu);
        packed = (packed & 0xFFFF_E3FFu) | ((uint)raw[5] << 10);
        packed = (packed & 0xFFFF_DFFFu) | ((uint)raw[6] << 13);
        packed = (packed & 0xFFFF_3FFFu) | ((uint)raw[7] << 14);
        packed = (packed & 0xFFFE_FFFFu) | ((uint)raw[8] << 16);
        packed = (packed & 0xFFFB_FFFFu) | ((uint)raw[12] << 18);
        packed = (packed & 0xFF0F_FFFFu) | ((uint)raw[9] << 20);
        packed = (packed & 0xF8FF_FFFFu) | ((uint)raw[10] << 24);
        return packed;
    }

    private static void ObserveModernGeometrySortRequest(MeshDecodeState state, int passOrdinal, ReadOnlySpan<byte> payload)
    {
        if (passOrdinal != 0 || state.Header is null || (byte)state.Header.AuthoredSortLevel != 0)
            return;

        int count = payload.Length == 4 ? 1 : state.Header.TriangleCount;
        if (payload.Length < checked(count * 4))
            return;

        for (int index = 0; index < count; index++)
        {
            uint shaderId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(index * 4, 4));
            if (shaderId >= (uint)state.Shaders.Count)
                continue;
            uint packed = PackCurrentRetailShader(state.Shaders[(int)shaderId]);
            if ((packed & 0x0000_00E0u) != 0 && (packed & 0x0004_0000u) == 0)
            {
                state.CurrentRetailGeometrySortRequested = true;
                return;
            }
        }
    }

    private static uint ComputeCurrentRetailCrc32(ReadOnlySpan<byte> bytes, uint seed)
    {
        uint crc = ~seed;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1u) != 0 ? 0xEDB8_8320u : 0u);
        }
        return ~crc;
    }

    private static void WriteSingle(Span<byte> destination, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static void WriteVector3(Span<byte> destination, Vector3 value)
    {
        WriteSingle(destination.Slice(0, 4), value.X);
        WriteSingle(destination.Slice(4, 4), value.Y);
        WriteSingle(destination.Slice(8, 4), value.Z);
    }

    private readonly record struct LegacyRootMaterialRuntime(
        Vector3 Diffuse,
        float DiffuseAlpha,
        Vector3 Ambient,
        Vector3 Specular,
        Vector3 Emissive,
        float Power);

    private sealed record LegacyRootTextureRuntime(
        string Name,
        bool SelectedByAlternate001E,
        IReadOnlyList<byte> RawInfo);

    private static bool TryReadLegacyRootColor1(ReadOnlySpan<byte> payload, List<EnbColor32> destination)
    {
        if ((payload.Length & 3) != 0)
            return false;

        destination.Clear();
        for (int offset = 0; offset < payload.Length; offset += 4)
            destination.Add(new EnbColor32(payload[offset], payload[offset + 1], payload[offset + 2], 255));
        return true;
    }

    private static void MarkPrimaryColorSource(MeshDecodeState state, int passOrdinal)
    {
        state.PrimaryColorSourcePassOrdinals.Add(passOrdinal);
        if ((uint)passOrdinal < (uint)state.MaterialPasses.Count)
            state.MaterialPasses[passOrdinal].HasPrimaryColorSource = true;
    }

    private static bool HasCurrentRetailPrimaryTextureCoordinateBinding(
        MeshDecodeState state,
        int passOrdinal,
        int stageOrdinal)
        => state.CurrentRetailPrimaryTextureCoordinateBindings.ContainsKey((passOrdinal, stageOrdinal));

    private static int AssignCurrentRetailPrimaryTextureCoordinateSet(
        MeshDecodeState state,
        int passOrdinal,
        int stageOrdinal,
        IReadOnlyList<Vector2> coordinates)
    {
        const int MaximumCoordinateSets = 8;
        uint crc32 = ComputeCurrentRetailTextureCoordinateCrc32(coordinates);
        int setIndex = state.CurrentRetailPrimaryTextureCoordinateSets.FindIndex(set => set.RetailCrc32 == crc32);
        if (setIndex < 0)
        {
            if (state.CurrentRetailPrimaryTextureCoordinateSets.Count >= MaximumCoordinateSets)
                return -1;

            setIndex = state.CurrentRetailPrimaryTextureCoordinateSets.Count;
            state.CurrentRetailPrimaryTextureCoordinateSets.Add(new EnbW3dPrimaryTextureCoordinateSet(
                setIndex,
                crc32,
                coordinates.ToImmutableArray()));
        }

        state.CurrentRetailPrimaryTextureCoordinateBindings[(passOrdinal, stageOrdinal)] = setIndex;
        return setIndex;
    }

    private static uint ComputeCurrentRetailTextureCoordinateCrc32(IReadOnlyList<Vector2> coordinates)
    {
        uint crc = 0xFFFF_FFFFu;
        Span<byte> pair = stackalloc byte[8];
        foreach (Vector2 coordinate in coordinates)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pair[..4], BitConverter.SingleToInt32Bits(coordinate.X));
            BinaryPrimitives.WriteInt32LittleEndian(pair.Slice(4, 4), BitConverter.SingleToInt32Bits(coordinate.Y));
            foreach (byte value in pair)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1u) != 0 ? 0xEDB8_8320u : 0u);
            }
        }
        return ~crc;
    }

    private static uint? ResolveCurrentRetailMaterialVariantChunkType(uint attributes)
    {
        // Current retail image selector DAT_00BB9650 is 1 and has one application
        // reference/no writer. Under that selector the high attribute family gates
        // the root material variant chosen by R4I_TempMeshLoader:
        //   no 0x0F000000 bits -> no variant container
        //   0x04000000       -> 0x0025 (wins over 0x02000000)
        //   0x02000000       -> 0x0024
        //   otherwise        -> 0x0023
        // Container 0x0026 belongs to the selector-2 branch and is unreachable in
        // the current retail image.
        if ((attributes & 0x0F00_0000u) == 0)
            return null;
        if ((attributes & 0x0400_0000u) != 0)
            return W3dChunk.MaterialVariant25;
        if ((attributes & 0x0200_0000u) != 0)
            return W3dChunk.MaterialVariant24;
        return W3dChunk.MaterialVariant23;
    }

    private static bool IsMaterialVariantContainer(uint chunkType)
        => chunkType is W3dChunk.MaterialVariant23
            or W3dChunk.MaterialVariant24
            or W3dChunk.MaterialVariant25
            or W3dChunk.MaterialVariant26;

    private static void PreserveSecondaryMaterialDescriptorChunk(
        MeshDecodeState state,
        MaterialPassState pass,
        TextureStageState? currentStage,
        uint chunkType,
        ReadOnlySpan<byte> payload)
    {
        // Current retail routes conflicting material-pass fields into a temporary
        // descriptor, merges that over the primary descriptor and retains the
        // result at MeshModel+0x78. Its normal render path remains pinned to the
        // primary descriptor through MeshModel+0x7C. Preserve the authored
        // conflicting chunk verbatim, but do not invent a selector for +0x78.
        state.SecondaryMaterialDescriptorChunks.Add(new EnbW3dSecondaryMaterialDescriptorChunk(
            pass.Ordinal,
            currentStage?.Ordinal,
            chunkType,
            payload.ToArray()));
    }

    private static bool ValidateMaterialState(MeshDecodeState state)
    {
        if (state.MaterialInfo is EnbW3dMaterialInfo info)
        {
            if (info.PassCount > int.MaxValue ||
                info.ShaderCount > int.MaxValue ||
                info.TextureCount > int.MaxValue ||
                state.MaterialPasses.Count != (int)info.PassCount ||
                state.VertexMaterials.Count != (int)info.VertexMaterialCount ||
                state.Shaders.Count != (int)info.ShaderCount ||
                state.Textures.Count != (int)info.TextureCount)
            {
                return false;
            }
        }

        foreach (TextureDefinitionState texture in state.Textures)
        {
            if (!texture.HasName)
                return false;
        }

        foreach (VertexMaterialState material in state.VertexMaterials)
        {
            if (!material.HasName || material.Info is null)
                return false;
        }

        foreach (MaterialPassState pass in state.MaterialPasses)
        {
            // Current retail's material-pass loader uses payload length to choose
            // between one uniform binding and an expanded stream. Expanded
            // material IDs are vertex indexed; expanded shader IDs are triangle
            // indexed. Zero means the corresponding authored chunk was absent.
            bool hasDiffuseColor = pass.ColorChunkOrder.Contains(EnbW3dMaterialPassColorChunkKind.DiffuseColor);
            bool hasDiffuseIllumination = pass.ColorChunkOrder.Contains(EnbW3dMaterialPassColorChunkKind.DiffuseIllumination);

            if (!HasRetailBindingCardinality(pass.VertexMaterialIds.Count, state.Vertices.Count) ||
                !HasRetailBindingCardinality(pass.ShaderIds.Count, state.Triangles.Count) ||
                (hasDiffuseColor ? pass.DiffuseColors.Count != state.Vertices.Count : pass.DiffuseColors.Count != 0) ||
                (hasDiffuseIllumination ? pass.DiffuseIllumination.Count != state.Vertices.Count : pass.DiffuseIllumination.Count != 0))
            {
                return false;
            }

            // 0x003E SpecularColor is preserved by this format layer for source
            // provenance, but the current active retail material-pass loader does
            // not dispatch that chunk. Do not promote its cardinality into an
            // active-runtime validation rule here.
            foreach (TextureStageState stage in pass.TextureStages)
            {
                if (!HasRetailBindingCardinality(stage.TextureIds.Count, state.Triangles.Count) ||
                    (stage.TextureIds.Count == 1 && stage.TextureIds[0] == uint.MaxValue) ||
                    (stage.RuntimeTexCoords.Count != 0 && stage.RuntimeTexCoords.Count != state.Vertices.Count))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool HasRetailBindingCardinality(int count, int expandedCount)
        => count == 0 || count == 1 || count == expandedCount;

    private static bool ValidateTriangles(IReadOnlyList<EnbW3dTriangle> triangles, int vertexCount)
    {
        uint count = checked((uint)vertexCount);
        foreach (EnbW3dTriangle triangle in triangles)
        {
            if (triangle.Vertex0 >= count || triangle.Vertex1 >= count || triangle.Vertex2 >= count)
                return false;
        }

        return true;
    }

    private static bool TryDecodeHierarchy(
        ReadOnlySpan<byte> payload,
        out EnbW3dHierarchy hierarchy)
    {
        hierarchy = default!;
        HierarchyHeader? header = null;
        EnbW3dPivot[]? pivots = null;
        EnbW3dPivotFixup[]? fixups = null;

        int offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> child = payload.Slice(chunk.PayloadStart, chunk.PayloadLength);
            if (offset == 0)
            {
                if (chunk.Type != W3dChunk.HierarchyHeader || child.Length < 36)
                    return false;

                uint pivotCount = BinaryPrimitives.ReadUInt32LittleEndian(child.Slice(20, 4));
                if (pivotCount > int.MaxValue)
                    return false;

                Vector3 center = ReadVector3(child.Slice(24, 12));
                if (!IsFinite(center))
                    return false;

                header = new HierarchyHeader(
                    BinaryPrimitives.ReadUInt32LittleEndian(child[..4]),
                    ReadFixedAscii(child.Slice(4, 16)),
                    checked((int)pivotCount),
                    center);
            }
            else if (chunk.Type == W3dChunk.Pivots)
            {
                // 00989060 reads the header's record count on every 102;
                // closing the chunk skips its remainder. Later records replace
                // the same hierarchy storage rather than rejecting the file.
                // Bound the native DWORD count by available records before
                // multiplication or allocation, rather than a 16-bit ceiling.
                if (header!.PivotCount > child.Length / 60)
                    return false;
                int recordBytes = header.PivotCount * 60;
                if (!TryReadPivots(child[..recordBytes], out pivots))
                    return false;
            }
            else if (chunk.Type == W3dChunk.PivotFixups)
            {
                // Metadata only: retail skips 103 entirely. Retaining a
                // decodable record must not make its shape an admission gate.
                if (!chunk.IsContainer && fixups is null && TryReadPivotFixups(child, out var authoredFixups))
                    fixups = authoredFixups;
            }
            // 00988FF6..00989013 closes every non-102 child without
            // interpreting it. Unknown children cannot reject a hierarchy.
            offset = chunk.PayloadEnd;
        }

        if (header is null || pivots is null || pivots.Length != header.PivotCount)
            return false;
        if (!ValidatePivotParents(pivots))
            return false;

        hierarchy = new EnbW3dHierarchy(
            header.Version,
            header.Name,
            header.Center,
            pivots,
            fixups ?? Array.Empty<EnbW3dPivotFixup>());
        return true;
    }

    private static bool TryReadPivots(ReadOnlySpan<byte> payload, out EnbW3dPivot[] pivots)
    {
        const int recordSize = 60;
        pivots = [];
        if ((payload.Length % recordSize) != 0)
            return false;

        var result = new EnbW3dPivot[payload.Length / recordSize];
        for (int ordinal = 0; ordinal < result.Length; ordinal++)
        {
            ReadOnlySpan<byte> record = payload.Slice(ordinal * recordSize, recordSize);
            Vector3 translation = ReadVector3(record.Slice(20, 12));
            Vector3 eulerAngles = ReadVector3(record.Slice(32, 12));
            var rotation = new EnbW3dRawQuaternion(
                ReadSingle(record.Slice(44, 4)),
                ReadSingle(record.Slice(48, 4)),
                ReadSingle(record.Slice(52, 4)),
                ReadSingle(record.Slice(56, 4)));
            // 00989060 consumes translation and quaternion; authored Euler words are unused.
            if (!IsFinite(translation) || !rotation.IsFinite)
                return false;

            result[ordinal] = new EnbW3dPivot(
                ordinal,
                ReadFixedAscii(record[..16]),
                BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(16, 4)),
                translation,
                eulerAngles,
                rotation);
        }

        pivots = result;
        return true;
    }

    private static bool TryReadPivotFixups(ReadOnlySpan<byte> payload, out EnbW3dPivotFixup[] fixups)
    {
        const int recordSize = 48;
        fixups = [];
        if ((payload.Length % recordSize) != 0)
            return false;

        var result = new EnbW3dPivotFixup[payload.Length / recordSize];
        for (int ordinal = 0; ordinal < result.Length; ordinal++)
        {
            ReadOnlySpan<byte> record = payload.Slice(ordinal * recordSize, recordSize);
            float[] values = new float[12];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = ReadSingle(record.Slice(index * 4, 4));
            }

            result[ordinal] = new EnbW3dPivotFixup(
                values[0], values[1], values[2],
                values[3], values[4], values[5],
                values[6], values[7], values[8],
                values[9], values[10], values[11]);
        }

        fixups = result;
        return true;
    }

    private static bool ValidatePivotParents(IReadOnlyList<EnbW3dPivot> pivots)
    {
        for (int index = 0; index < pivots.Count; index++)
        {
            uint parent = pivots[index].ParentIndex;
            if (parent == uint.MaxValue)
                continue;
            if (parent >= (uint)pivots.Count || parent == (uint)index)
                return false;
        }

        // Parent links must form a forest. A single-root requirement is not
        // invented here, but cycles are never a valid hierarchy tree.
        var states = new byte[pivots.Count];
        for (int start = 0; start < pivots.Count; start++)
        {
            int current = start;
            var path = new List<int>();
            while (current >= 0 && states[current] == 0)
            {
                states[current] = 1;
                path.Add(current);
                uint parent = pivots[current].ParentIndex;
                current = parent == uint.MaxValue ? -1 : checked((int)parent);
            }

            if (current >= 0 && states[current] == 1)
                return false;

            foreach (int member in path)
                states[member] = 2;
        }

        return true;
    }

    private static bool TryDecodeHierarchyAnimation(
        ReadOnlySpan<byte> payload,
        out EnbW3dHierarchyAnimation animation)
    {
        animation = default!;
        AnimationHeader? header = null;
        var channels = new List<EnbW3dAnimationChannel>();
        var bitChannels = new List<EnbW3dAnimationBitChannel>();

        int offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> child = payload.Slice(chunk.PayloadStart, chunk.PayloadLength);
            if (offset == 0)
            {
                //009C2EF0 requires201 first and reads44 bytes. Chunk close
                //skips its tail; later201 records are unknown to the channel loop.
                if (chunk.Type != W3dChunk.AnimationHeader || child.Length < 44)
                    return false;

                uint numFrames = BinaryPrimitives.ReadUInt32LittleEndian(child.Slice(36, 4));
                uint frameRate = BinaryPrimitives.ReadUInt32LittleEndian(child.Slice(40, 4));
                //009C3024..009C3043 copies the DWORD frame count and converts
                //the unsigned DWORD rate to float without either numeric cap.
                //Storage is determined by channel payloads, not the clip duration.
                if (numFrames == 0)
                    return false;

                header = new AnimationHeader(
                    BinaryPrimitives.ReadUInt32LittleEndian(child[..4]),
                    ReadFixedAscii(child.Slice(4, 16)),
                    ReadFixedAscii(child.Slice(20, 16)),
                    numFrames,
                    frameRate);
            }
            else if (chunk.Type == W3dChunk.AnimationChannel)
            {
                if (!TryReadAnimationChannel(child, out EnbW3dAnimationChannel channel))
                    return false;
                channels.Add(channel);
            }
            else if (chunk.Type == W3dChunk.BitChannel)
            {
                if (!TryReadAnimationBitChannel(child, out EnbW3dAnimationBitChannel channel))
                    return false;
                bitChannels.Add(channel);
            }
            //009C316F closes every unrecognized type without descending.

            offset = chunk.PayloadEnd;
        }

        if (header is null)
            return false;
        // 009C2EF0 keeps channels whose frame range runs past NumFrames; only
        // a pivot at or beyond the hierarchy's pivot count is discarded (at
        // bind time). The runtime treats frames outside a channel as absent.

        animation = new EnbW3dHierarchyAnimation(
            header.Version,
            header.Name,
            header.HierarchyName,
            header.NumFrames,
            header.FrameRate,
            channels.ToArray(),
            bitChannels.ToArray());
        return true;
    }

    private static bool TryDecodeCompressedHierarchyAnimation(
        ReadOnlySpan<byte> payload,
        out EnbW3dHierarchyAnimation animation)
    {
        animation = default!;
        CompressedAnimationHeader? header = null;
        var channels = new List<EnbW3dCompressedAnimationChannel>();
        var bitChannels = new List<EnbW3dCompressedAnimationBitChannel>();

        int offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> child = payload.Slice(chunk.PayloadStart, chunk.PayloadLength);
            if (offset == 0)
            {
                //009C3DC0 has the same first-header contract for281.
                if (chunk.Type != W3dChunk.CompressedAnimationHeader || child.Length < 44)
                    return false;

                uint numFrames = BinaryPrimitives.ReadUInt32LittleEndian(child.Slice(36, 4));
                ushort frameRate = BinaryPrimitives.ReadUInt16LittleEndian(child.Slice(40, 2));
                ushort flavor = BinaryPrimitives.ReadUInt16LittleEndian(child.Slice(42, 2));
                //009C3EDC..009C3F12 likewise retains the full frame count;
                //compressed frame rate is authored as a WORD.
                if (numFrames == 0 || flavor != 0)
                    return false;

                header = new CompressedAnimationHeader(
                    BinaryPrimitives.ReadUInt32LittleEndian(child[..4]),
                    ReadFixedAscii(child.Slice(4, 16)),
                    ReadFixedAscii(child.Slice(20, 16)),
                    numFrames,
                    frameRate,
                    flavor);
            }
            else if (chunk.Type == W3dChunk.CompressedAnimationChannel)
            {
                if (!TryReadCompressedAnimationChannel(child, out EnbW3dCompressedAnimationChannel channel))
                    return false;
                channels.Add(channel);
            }
            else if (chunk.Type == W3dChunk.CompressedBitChannel)
            {
                if (!TryReadCompressedAnimationBitChannel(child, out EnbW3dCompressedAnimationBitChannel channel))
                    return false;
                bitChannels.Add(channel);
            }

            offset = chunk.PayloadEnd;
        }

        if (header is null)
            return false;

        animation = new EnbW3dHierarchyAnimation(
            header.Version,
            header.Name,
            header.HierarchyName,
            header.NumFrames,
            header.FrameRate,
            Array.Empty<EnbW3dAnimationChannel>(),
            Array.Empty<EnbW3dAnimationBitChannel>())
        {
            Family = EnbW3dHAnimFamily.Compressed0280,
            CompressionFlavor = header.Flavor,
            CompressedChannels = channels.ToArray(),
            CompressedBitChannels = bitChannels.ToArray(),
        };
        return true;
    }

    private static bool TryReadCompressedAnimationChannel(
        ReadOnlySpan<byte> payload,
        out EnbW3dCompressedAnimationChannel channel)
    {
        channel = default!;
        if (payload.Length < 12)
            return false;

        uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        ushort pivotIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        byte vectorLength = payload[6];
        byte flags = payload[7];
        if (keyCount == 0 || vectorLength == 0 || vectorLength > 64)
            return false;

        long stride = checked(1L + vectorLength);
        long expectedLength = checked(8L + (keyCount * stride * 4L));
        //009C6910 retains the declared count and reads the remaining chunk.
        //Only complete declared keys are sampled; extra payload is not a key.
        if (expectedLength > payload.Length)
            return false;

        EnbW3dAnimationChannelKind? knownKind = flags <= (byte)EnbW3dAnimationChannelKind.Quaternion
            ? (EnbW3dAnimationChannelKind)flags
            : null;
        if (knownKind.HasValue)
        {
            byte expectedVectorLength = knownKind.Value == EnbW3dAnimationChannelKind.Quaternion
                ? (byte)4
                : (byte)1;
            if (vectorLength != expectedVectorLength)
                return false;
        }

        var timeCodes = new uint[checked((int)keyCount)];
        var values = new float[checked((int)(keyCount * vectorLength))];
        int cursor = 8;
        uint previousFrame = 0;
        for (int keyIndex = 0; keyIndex < timeCodes.Length; keyIndex++)
        {
            uint timeCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(cursor, 4));
            cursor += 4;
            uint authoredFrame = timeCode & 0x7FFF_FFFFu;
            if (keyIndex > 0 && authoredFrame < previousFrame)
                return false;
            previousFrame = authoredFrame;
            timeCodes[keyIndex] = timeCode;

            for (int component = 0; component < vectorLength; component++)
            {
                float value = ReadSingle(payload.Slice(cursor, 4));
                cursor += 4;
                if (!float.IsFinite(value))
                    return false;
                values[(keyIndex * vectorLength) + component] = value;
            }
        }

        channel = new EnbW3dCompressedAnimationChannel(
            pivotIndex,
            vectorLength,
            flags,
            knownKind,
            timeCodes,
            values);
        return true;
    }

    private static bool TryReadCompressedAnimationBitChannel(
        ReadOnlySpan<byte> payload,
        out EnbW3dCompressedAnimationBitChannel channel)
    {
        channel = default!;
        if (payload.Length < 12)
            return false;

        uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]);
        ushort pivotIndex = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        byte flags = payload[6];
        byte reserved = payload[7];
        if (keyCount == 0)
            return false;

        long expectedLength = checked(8L + (keyCount * 4L));
        //009C6E70 reads exactly keyCount DWORDs, then chunk close skips tails.
        if (expectedLength > payload.Length)
            return false;

        EnbW3dAnimationBitChannelKind? knownKind = flags <= (byte)EnbW3dAnimationBitChannelKind.Visibility
            ? (EnbW3dAnimationBitChannelKind)flags
            : null;
        var timeCodes = new uint[checked((int)keyCount)];
        uint previousFrame = 0;
        for (int keyIndex = 0; keyIndex < timeCodes.Length; keyIndex++)
        {
            uint timeCode = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8 + (keyIndex * 4), 4));
            uint authoredFrame = timeCode & 0x7FFF_FFFFu;
            if (keyIndex > 0 && authoredFrame < previousFrame)
                return false;
            previousFrame = authoredFrame;
            timeCodes[keyIndex] = timeCode;
        }

        channel = new EnbW3dCompressedAnimationBitChannel(
            pivotIndex,
            flags,
            knownKind,
            reserved,
            timeCodes);
        return true;
    }

    private static bool TryReadAnimationChannel(
        ReadOnlySpan<byte> payload,
        out EnbW3dAnimationChannel channel)
    {
        channel = default!;
        if (payload.Length < 12)
            return false;

        ushort firstFrame = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        ushort lastFrame = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        ushort vectorLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        ushort pivot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2));
        ushort padding = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(10, 2));

        if (lastFrame < firstFrame || vectorLength == 0 || vectorLength > 64)
            return false;

        uint frameCount = checked((uint)(lastFrame - firstFrame + 1));
        long valueCount = (long)frameCount * vectorLength;
        long expectedLength = 12L + (valueCount * 4L);
        // 009C6650 reads the declared samples, then seeks past the remaining
        // chunk bytes. TSI16A resize channels carry one trailing float.
        if (expectedLength > payload.Length)
            return false;

        EnbW3dAnimationChannelKind? knownKind = flags <= (ushort)EnbW3dAnimationChannelKind.Quaternion
            ? (EnbW3dAnimationChannelKind)flags
            : null;
        if (knownKind.HasValue)
        {
            ushort expectedVectorLength = knownKind.Value == EnbW3dAnimationChannelKind.Quaternion
                ? (ushort)4
                : (ushort)1;
            if (vectorLength != expectedVectorLength)
                return false;
        }

        var values = new float[checked((int)valueCount)];
        for (int index = 0; index < values.Length; index++)
        {
            float value = ReadSingle(payload.Slice(12 + (index * 4), 4));
            if (!float.IsFinite(value))
                return false;
            values[index] = value;
        }

        channel = new EnbW3dAnimationChannel(
            firstFrame,
            lastFrame,
            vectorLength,
            flags,
            knownKind,
            pivot,
            padding,
            values);
        return true;
    }

    private static bool TryReadAnimationBitChannel(
        ReadOnlySpan<byte> payload,
        out EnbW3dAnimationBitChannel channel)
    {
        channel = default!;
        if (payload.Length < 9)
            return false;

        ushort firstFrame = BinaryPrimitives.ReadUInt16LittleEndian(payload[..2]);
        ushort lastFrame = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(4, 2));
        ushort pivot = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2));
        byte defaultValue = payload[8];
        if (lastFrame < firstFrame)
            return false;

        int frameCount = lastFrame - firstFrame + 1;
        int packedBytes = checked((frameCount + 7) / 8);
        // 009C67C0 reads the declared bytes and fails only when they are absent;
        // the chunk close skips any extra bytes.
        if (payload.Length < 9 + packedBytes)
            return false;

        channel = new EnbW3dAnimationBitChannel(
            firstFrame,
            lastFrame,
            flags,
            flags == 0 ? EnbW3dAnimationBitChannelKind.Visibility : null,
            pivot,
            defaultValue,
            payload.Slice(9, packedBytes).ToArray());
        return true;
    }

    private static bool TryDecodeHierarchyModel(ReadOnlySpan<byte> payload,out EnbW3dHierarchyModel hierarchyModel)
    {
        hierarchyModel=default!;
        // Whole9B6000 requires301 first and reads40 bytes, independent of its container flag.
        if(!TryReadChunk(payload,0,payload.Length,out ChunkView first)||first.Type!=W3dChunk.HierarchyModelHeader||
            !TryReadHierarchyModelHeader(payload.Slice(first.PayloadStart,first.PayloadLength),out var header))return false;
        var nodes=new List<EnbW3dHierarchyModelNode>();int offset=first.PayloadEnd;
        while(payload.Length-offset>=ChunkHeaderLength)
        {
            if(!TryReadChunk(payload,offset,payload.Length,out ChunkView chunk))return false;
            if(chunk.Type is W3dChunk.HierarchyModelNode or W3dChunk.HierarchyModelCollisionNode or W3dChunk.HierarchyModelSkinNode)
            {
                TryGetHierarchyNodeKind(chunk.Type,out var kind);
                if(!TryReadHierarchyModelNode(payload.Slice(chunk.PayloadStart,chunk.PayloadLength),nodes.Count,kind,out var node))return false;
                string name=header!.Name+"."+node.RenderObjectName;
                nodes.Add(node with {RenderObjectName=name});
            }
            //306,305 and unknown children are skipped.440 owns the separate
            //auxiliary vector; its status does not control these connection records.
            offset=chunk.PayloadEnd;
        }
        // Missing source records require incoming native heap bytes; only complete
        //authored arrays are admitted by this safe decoder.
        if(nodes.Count!=header!.ConnectionCount)return false;
        hierarchyModel=new(header.Version,header.Name,header.HierarchyName,nodes.ToArray(),null);return true;
    }

    private static string ReadRetailHmodelAscii(ReadOnlySpan<byte> bytes)
    {
        int zero=bytes.IndexOf((byte)0);return Encoding.Latin1.GetString(zero>=0?bytes[..zero]:bytes);
    }
    private static bool TryReadHierarchyModelHeader(
        ReadOnlySpan<byte> payload,
        out HierarchyModelHeader? header)
    {
        header = null;
        if (payload.Length < 40)
            return false;

        header = new HierarchyModelHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            ReadRetailHmodelAscii(payload.Slice(4, 15)),
            ReadRetailHmodelAscii(payload.Slice(20, 15)),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(36, 2)));
        return true;
    }

    private static bool TryGetHierarchyNodeKind(uint type, out EnbW3dHierarchyNodeKind kind)
    {
        kind = type switch
        {
            W3dChunk.HierarchyModelNode => EnbW3dHierarchyNodeKind.RenderObject,
            W3dChunk.HierarchyModelCollisionNode => EnbW3dHierarchyNodeKind.Collision,
            W3dChunk.HierarchyModelSkinNode => EnbW3dHierarchyNodeKind.Skin,
            W3dChunk.HierarchyModelShadowNode => EnbW3dHierarchyNodeKind.Shadow,
            _ => default,
        };
        return type is W3dChunk.HierarchyModelNode or
            W3dChunk.HierarchyModelCollisionNode or
            W3dChunk.HierarchyModelSkinNode or
            W3dChunk.HierarchyModelShadowNode;
    }

    private static bool TryReadHierarchyModelNode(
        ReadOnlySpan<byte> payload,
        int ordinal,
        EnbW3dHierarchyNodeKind kind,
        out EnbW3dHierarchyModelNode node)
    {
        node = default;
        if (payload.Length < 18)
            return false;

        node = new EnbW3dHierarchyModelNode(
            ordinal,
            kind,
            ReadRetailHmodelAscii(payload[..16]),
            BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(16, 2)));
        return true;
    }

    private static bool TryDecodeHierarchicalLod(
        ReadOnlySpan<byte> payload,
        out EnbW3dHierarchicalLod hierarchicalLod)
    {
        hierarchicalLod = default!;
        HlodHeader? header = null;
        var levels = new List<EnbW3dLodLevel>();
        var aggregateExtensions = new List<ReadOnlyMemory<byte>>();
        var proxyArrayExtensions = new List<ReadOnlyMemory<byte>>();
        EnbW3dHlodAggregate? aggregate = null;
        EnbW3dHlodProxyArray? proxyArray = null;

        int offset = 0;
        // 0098B700 requires the first child to be0701 and reads its40-byte
        // prefix. Its two strdup consumers preserve spaces and read C strings.
        if (!TryReadChunk(payload, offset, payload.Length, out ChunkView first) ||
            first.Type != W3dChunk.HlodHeader || first.PayloadLength < 40)
            return false;
        ReadOnlySpan<byte> headerBytes = payload.Slice(first.PayloadStart, 40);
        uint declaredLevels = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes.Slice(4, 4));
        if (declaredLevels > int.MaxValue ||
            !TryReadHlodCString(headerBytes[8..], out string ownerName) ||
            !TryReadHlodCString(headerBytes[24..], out string hierarchyName))
            return false;
        header = new HlodHeader(BinaryPrimitives.ReadUInt32LittleEndian(headerBytes),
            (int)declaredLevels, ownerName, hierarchyName);
        offset = first.PayloadEnd;
        for (int ordinal = 0; ordinal < header.LodCount; ordinal++)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView levelChunk) ||
                levelChunk.Type != W3dChunk.HlodLevel ||
                !TryDecodeHlodLevel(payload.Slice(levelChunk.PayloadStart, levelChunk.PayloadLength), ordinal, out var level))
                return false;
            levels.Add(level);
            offset = levelChunk.PayloadEnd;
        }
        while (offset < payload.Length)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> child = payload.Slice(chunk.PayloadStart, chunk.PayloadLength);
            if (chunk.Type == W3dChunk.HlodAggregate)
            {
                if (!TryDecodeHlodAggregate(child, out EnbW3dHlodAggregate decoded))
                    return false;

                // Current retail stores 0x0705 into one level-shaped slot at
                // HLOD-definition +0x10. If more than one chunk is authored,
                // later chunks overwrite that runtime slot. Keep all raw
                // payloads for diagnostics while exposing the final effective
                // typed value separately.
                aggregateExtensions.Add(child.ToArray());
                aggregate = decoded;
            }
            else if (chunk.Type == W3dChunk.HlodProxyArray)
            {
                if (!TryDecodeHlodProxyArray(child, out EnbW3dHlodProxyArray decoded))
                    return false;

                // FUN_0098B7D0 releases the previous proxy-array object before
                // replacing HLOD-definition +0x20, so the final chunk wins.
                proxyArrayExtensions.Add(child.ToArray());
                proxyArray = decoded;
            }
            // 0098B5D0 closes all other remaining chunks without consuming them.

            offset = chunk.PayloadEnd;
        }

        if (header is null || levels.Count != header.LodCount)
            return false;

        hierarchicalLod = new EnbW3dHierarchicalLod(
            header.Version,
            header.Name,
            header.HierarchyName,
            levels.ToArray())
        {
            AggregateExtensions = aggregateExtensions.ToArray(),
            ProxyArrayExtensions = proxyArrayExtensions.ToArray(),
            Aggregate = aggregate,
            ProxyArray = proxyArray,
        };
        return true;
    }

    private static bool TryDecodeHlodAggregate(
        ReadOnlySpan<byte> payload,
        out EnbW3dHlodAggregate aggregate)
    {
        aggregate = default!;
        if (!TryDecodeHlodMemberList(payload, fixedProxyNames: false, out uint rawValue, out EnbW3dLodMember[] members))
            return false;

        aggregate = new EnbW3dHlodAggregate(rawValue, members);
        return true;
    }

    private static bool TryDecodeHlodProxyArray(
        ReadOnlySpan<byte> payload,
        out EnbW3dHlodProxyArray proxyArray)
    {
        proxyArray = default!;
        if (!TryDecodeHlodMemberList(payload, fixedProxyNames: true, out uint rawValue, out EnbW3dLodMember[] members))
            return false;

        // FUN_0098B7D0 reads the second DWORD from the 0x0703 header but does
        // not store it in the runtime proxy-array object. Preserve it neutrally
        // for byte-accurate asset inspection without inventing semantics.
        proxyArray = new EnbW3dHlodProxyArray(rawValue, members);
        return true;
    }

    private static bool TryDecodeHlodMemberList(
        ReadOnlySpan<byte> payload,
        bool fixedProxyNames,
        out uint rawSecondHeaderValue,
        out EnbW3dLodMember[] members)
    {
        rawSecondHeaderValue = 0;
        members = Array.Empty<EnbW3dLodMember>();
        var decodedMembers = new List<EnbW3dLodMember>();
        if (!TryReadChunk(payload, 0, payload.Length, out ChunkView first) ||
            first.Type != W3dChunk.HlodLevelHeader || first.PayloadLength < 8)
            return false;
        ReadOnlySpan<byte> header = payload.Slice(first.PayloadStart, 8);
        uint expectedMemberCount = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (expectedMemberCount > int.MaxValue)
            return false;
        rawSecondHeaderValue = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        int offset = first.PayloadEnd;
        // 0098BAD0 reads exactly the declared number of704 prefixes. Trailing
        // children are skipped by its caller's Close, and thresholds stay raw.
        for (int index = 0; index < (int)expectedMemberCount; index++)
        {
            if (!TryReadChunk(payload, offset, payload.Length, out ChunkView chunk))
                return false;

            ReadOnlySpan<byte> child = payload.Slice(chunk.PayloadStart, chunk.PayloadLength);
            if (chunk.Type != W3dChunk.HlodSubObject || child.Length < 36)
                return false;
            ReadOnlySpan<byte> nameBytes = child.Slice(4, 32);
            string name;
            if (fixedProxyNames)
            {
                int end = nameBytes.IndexOf((byte)0);
                name = Encoding.ASCII.GetString(end < 0 ? nameBytes : nameBytes[..end]);
            }
            else if (!TryReadHlodCString(nameBytes, out name))
                return false;
            decodedMembers.Add(new EnbW3dLodMember(BinaryPrimitives.ReadUInt32LittleEndian(child), name));

            offset = chunk.PayloadEnd;
        }

        members = decodedMembers.ToArray();
        return true;
    }

    private static bool TryReadHlodCString(ReadOnlySpan<byte> bytes, out string value)
    {
        int end = bytes.IndexOf((byte)0);
        value = end < 0 ? string.Empty : Encoding.ASCII.GetString(bytes[..end]);
        return end >= 0;
    }

    private static bool TryDecodeHlodLevel(
        ReadOnlySpan<byte> payload,
        int ordinal,
        out EnbW3dLodLevel level)
    {
        level = default!;
        if (!TryDecodeHlodMemberList(payload, fixedProxyNames: false, out uint rawThreshold, out EnbW3dLodMember[] members))
            return false;

        float threshold = BitConverter.Int32BitsToSingle(unchecked((int)rawThreshold));
        level = new EnbW3dLodLevel(ordinal, threshold, rawThreshold, members);
        return true;
    }

    private static bool TryReadHeader(ReadOnlySpan<byte> payload, out MeshHeader? header)
    {
        header = null;
        if (payload.Length < 116)
            return false;

        uint triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(40, 4));
        uint vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(44, 4));
        uint legacyMaterialCount = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(48, 4));
        if (triangleCount > 10_000_000 ||
            vertexCount == 0 || vertexCount > 10_000_000 ||
            legacyMaterialCount > 10_000_000)
            return false;

        Vector3 boundingBoxMin = ReadVector3(payload.Slice(76, 12));
        Vector3 boundingBoxMax = ReadVector3(payload.Slice(88, 12));
        Vector3 boundingSphereCenter = ReadVector3(payload.Slice(100, 12));
        float radius = ReadSingle(payload.Slice(112, 4));
        if (!IsFinite(boundingBoxMin) ||
            !IsFinite(boundingBoxMax) ||
            !IsFinite(boundingSphereCenter) ||
            !float.IsFinite(radius) ||
            radius < 0)
        {
            return false;
        }

        header = new MeshHeader(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
            ReadFixedAscii(payload.Slice(8, 16)),
            ReadFixedAscii(payload.Slice(24, 16)),
            checked((int)triangleCount),
            checked((int)vertexCount),
            checked((int)legacyMaterialCount),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(56, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(68, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(72, 4)),
            boundingBoxMin,
            boundingBoxMax,
            boundingSphereCenter,
            radius);
        return true;
    }

    private static bool TryReadVector3Array(ReadOnlySpan<byte> payload, List<Vector3> destination)
    {
        if ((payload.Length % 12) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / 12);
        for (int offset = 0; offset < payload.Length; offset += 12)
        {
            Vector3 value = new(
                ReadSingle(payload.Slice(offset, 4)),
                ReadSingle(payload.Slice(offset + 4, 4)),
                ReadSingle(payload.Slice(offset + 8, 4)));
            if (!IsFinite(value))
                return false;
            destination.Add(value);
        }

        return true;
    }

    private static bool TryReadVertexInfluences(
        ReadOnlySpan<byte> payload,
        List<EnbW3dVertexInfluence> destination)
    {
        const int recordSize = 8;
        if ((payload.Length % recordSize) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / recordSize);
        for (int offset = 0; offset < payload.Length; offset += recordSize)
        {
            destination.Add(new EnbW3dVertexInfluence(
                BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, 2))));
        }

        return true;
    }

    private static bool TryReadTriangles(ReadOnlySpan<byte> payload, List<EnbW3dTriangle> destination)
    {
        const int recordSize = 32;
        if ((payload.Length % recordSize) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / recordSize);
        for (int offset = 0; offset < payload.Length; offset += recordSize)
        {
            Vector3 normal = new(
                ReadSingle(payload.Slice(offset + 16, 4)),
                ReadSingle(payload.Slice(offset + 20, 4)),
                ReadSingle(payload.Slice(offset + 24, 4)));
            float distance = ReadSingle(payload.Slice(offset + 28, 4));
            if (!IsFinite(normal) || !float.IsFinite(distance))
                return false;

            destination.Add(new EnbW3dTriangle(
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + 12, 4)),
                normal,
                distance));
        }

        return true;
    }

    private static bool TryReadTextureInfo(ReadOnlySpan<byte> payload, out EnbW3dTextureInfo info)
    {
        // FUN_0094CCC0 requests 0x0C bytes from the current chunk and ignores
        // any remaining payload when the chunk is closed. Match that active
        // behavior while retaining all three dwords it actually reads.
        if (payload.Length < 12)
        {
            info = default;
            return false;
        }

        info = new EnbW3dTextureInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(0, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)));
        return true;
    }

    private static bool TryReadMaterialInfo(ReadOnlySpan<byte> payload, out EnbW3dMaterialInfo info)
    {
        info = default;
        if (payload.Length != 16)
            return false;

        info = new EnbW3dMaterialInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(8, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(12, 4)));
        return true;
    }

    private static bool TryReadVertexMaterialInfo(
        ReadOnlySpan<byte> payload,
        out EnbW3dVertexMaterialInfo info)
    {
        info = default;
        if (payload.Length != 32)
            return false;

        float power = ReadSingle(payload.Slice(20, 4));
        float opacity = ReadSingle(payload.Slice(24, 4));
        float thirdFloat = ReadSingle(payload.Slice(28, 4));
        if (!float.IsFinite(power) || !float.IsFinite(opacity) || !float.IsFinite(thirdFloat))
            return false;

        info = new EnbW3dVertexMaterialInfo(
            BinaryPrimitives.ReadUInt32LittleEndian(payload[..4]),
            ReadPackedRgb(payload.Slice(4, 4)),
            ReadPackedRgb(payload.Slice(8, 4)),
            ReadPackedRgb(payload.Slice(12, 4)),
            ReadPackedRgb(payload.Slice(16, 4)),
            power,
            opacity,
            thirdFloat);
        return true;
    }

    private static EnbW3dPackedRgb ReadPackedRgb(ReadOnlySpan<byte> bytes)
        => new(bytes[0], bytes[1], bytes[2], bytes[3]);

    private static EnbW3dMapperArguments ParseMapperArguments(string raw)
    {
        float uScale = 1.0f;
        float vScale = 1.0f;
        float uPerSecond = 0.0f;
        float vPerSecond = 0.0f;
        float uOffset = 0.0f;
        float vOffset = 0.0f;
        bool clampFix = false;

        foreach (string sourceLine in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = sourceLine.Trim();
            if (line.Length == 0)
                continue;

            int equals = line.IndexOf('=');
            if (equals <= 0 || equals == line.Length - 1)
                continue;

            string key = line[..equals].Trim();
            string text = line[(equals + 1)..].Trim();

            if (key.Equals("ClampFix", StringComparison.OrdinalIgnoreCase))
            {
                clampFix = ParseMapperBoolean(text, defaultValue: false);
                continue;
            }

            if (!TryParseMapperFloat(text, out float value))
                continue;

            if (key.Equals("UScale", StringComparison.OrdinalIgnoreCase))
                uScale = value;
            else if (key.Equals("VScale", StringComparison.OrdinalIgnoreCase))
                vScale = value;
            else if (key.Equals("UPerSec", StringComparison.OrdinalIgnoreCase))
                uPerSecond = value;
            else if (key.Equals("VPerSec", StringComparison.OrdinalIgnoreCase))
                vPerSecond = value;
            else if (key.Equals("UOffset", StringComparison.OrdinalIgnoreCase))
                uOffset = value;
            else if (key.Equals("VOffset", StringComparison.OrdinalIgnoreCase))
                vOffset = value;
        }

        return new EnbW3dMapperArguments(
            raw,
            uScale,
            vScale,
            uPerSecond,
            vPerSecond,
            uOffset,
            vOffset,
            clampFix);
    }

    private static bool TryParseMapperFloat(string text, out float value)
        => EnbRetailMapperNumber.TryParse(text, out value);

    private static bool ParseMapperBoolean(string text, bool defaultValue)
    {
        if (text.Length == 0)
            return defaultValue;

        return char.ToUpperInvariant(text[0]) switch
        {
            '0' or 'F' or 'N' => false,
            '1' or 'T' or 'Y' => true,
            _ => defaultValue,
        };
    }

    private static bool TryReadShaders(ReadOnlySpan<byte> payload, List<EnbW3dShader> destination)
    {
        const int recordSize = 16;
        if ((payload.Length % recordSize) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / recordSize);
        for (int offset = 0; offset < payload.Length; offset += recordSize)
            destination.Add(new EnbW3dShader(payload.Slice(offset, recordSize).ToArray()));
        return true;
    }

    private static bool TryReadUInt32Array(ReadOnlySpan<byte> payload, List<uint> destination)
    {
        if ((payload.Length % 4) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / 4);
        for (int offset = 0; offset < payload.Length; offset += 4)
            destination.Add(BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, 4)));
        return true;
    }

    private static bool TryReadColors(ReadOnlySpan<byte> payload, List<EnbColor32> destination)
    {
        if ((payload.Length % 4) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / 4);
        for (int offset = 0; offset < payload.Length; offset += 4)
        {
            destination.Add(new EnbColor32(
                payload[offset],
                payload[offset + 1],
                payload[offset + 2],
                payload[offset + 3]));
        }

        return true;
    }

    private static bool TryReadRuntimeTexCoords(ReadOnlySpan<byte> payload, List<Vector2> destination)
    {
        if ((payload.Length % 8) != 0)
            return false;

        destination.Clear();
        destination.Capacity = Math.Max(destination.Capacity, payload.Length / 8);
        for (int offset = 0; offset < payload.Length; offset += 8)
        {
            float u = ReadSingle(payload.Slice(offset, 4));
            float sourceV = ReadSingle(payload.Slice(offset + 4, 4));
            //9BC964 copies U verbatim and9BC977 subtracts V from1, with no
            //finite-value rejection. GST08/NPC15A contain authored NaN UVs;
            //rejecting those coordinates discards otherwise valid meshes.
            // Current retail W3D import stores (U, 1-V). Keep that recovered
            // format semantic in the platform-neutral asset representation so
            // every renderer backend receives the same canonical coordinates.
            destination.Add(new Vector2(u, 1.0f - sourceV));
        }

        return true;
    }

    private static bool TryReadChunk(ReadOnlySpan<byte> bytes, int offset, int end, out ChunkView chunk)
    {
        chunk = default;
        if (offset < 0 || end < offset || end > bytes.Length || end - offset < ChunkHeaderLength)
            return false;

        uint type = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
        uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
        uint size = rawSize & SizeMask;
        if (size > int.MaxValue)
            return false;

        int payloadStart = offset + ChunkHeaderLength;
        int payloadEnd;
        try
        {
            payloadEnd = checked(payloadStart + (int)size);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (payloadEnd > end)
            return false;

        chunk = new ChunkView(
            type,
            (rawSize & ContainerFlag) != 0,
            payloadStart,
            (int)size,
            payloadEnd);
        return true;
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));

    private static Vector3 ReadVector3(ReadOnlySpan<byte> bytes)
        => new(
            ReadSingle(bytes[..4]),
            ReadSingle(bytes.Slice(4, 4)),
            ReadSingle(bytes.Slice(8, 4)));

    private static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> value = zero >= 0 ? bytes[..zero] : bytes;
        //Binary mesh/tree/pivot/animation names are retained C strings.
        //009BAA73,00988F71,0098914E and the HAnim loaders do not trim them.
        return Encoding.ASCII.GetString(value);
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> value = zero >= 0 ? bytes[..zero] : bytes;
        return Encoding.ASCII.GetString(value).Trim();
    }

    private static string ReadNullTerminatedAsciiRaw(ReadOnlySpan<byte> bytes)
    {
        int zero = bytes.IndexOf((byte)0);
        ReadOnlySpan<byte> value = zero >= 0 ? bytes[..zero] : bytes;
        return Encoding.ASCII.GetString(value);
    }

    private static bool IsFinite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private readonly record struct ChunkView(
        uint Type,
        bool IsContainer,
        int PayloadStart,
        int PayloadLength,
        int PayloadEnd);

    private sealed record AnimationHeader(
        uint Version,
        string Name,
        string HierarchyName,
        uint NumFrames,
        uint FrameRate);

    private sealed record CompressedAnimationHeader(
        uint Version,
        string Name,
        string HierarchyName,
        uint NumFrames,
        ushort FrameRate,
        ushort Flavor);

    private sealed record HierarchyHeader(
        uint Version,
        string Name,
        int PivotCount,
        Vector3 Center);

    private sealed record HierarchyModelHeader(
        uint Version,
        string Name,
        string HierarchyName,
        ushort ConnectionCount);

    private sealed record HlodHeader(
        uint Version,
        int LodCount,
        string Name,
        string HierarchyName);

    private sealed record MeshHeader(
        uint Version,
        uint Attributes,
        string Name,
        string ContainerName,
        int TriangleCount,
        int VertexCount,
        int LegacyMaterialCount,
        uint AuthoredSortLevel,
        uint VertexChannels,
        uint FaceChannels,
        Vector3 BoundingBoxMin,
        Vector3 BoundingBoxMax,
        Vector3 BoundingSphereCenter,
        float BoundingRadius);

    private sealed class MeshDecodeState
    {
        public MeshHeader? Header { get; set; }
        public List<Vector3> Vertices { get; } = [];
        public List<Vector3> Normals { get; } = [];
        public uint? AuthoredNormalChunkType { get; set; }
        public IReadOnlyList<uint>? AuthoredVertexJoinMap { get; set; }
        public IReadOnlyList<byte>? AuthoredAabTreePayload { get; set; }
        public uint? CurrentRetailMaterialVariantChunkType { get; set; }
        public List<EnbW3dVertexInfluence> VertexInfluences { get; } = [];
        public List<EnbW3dTriangle> Triangles { get; } = [];
        public EnbW3dMaterialInfo? MaterialInfo { get; set; }
        public List<EnbW3dShader> Shaders { get; } = [];
        public List<TextureDefinitionState> Textures { get; } = [];
        public List<EnbW3dTextureAcquisition> TextureAcquisitions { get; } = [];
        public List<string> CurrentRetailLoadedTextureNames { get; } = [];
        public List<EnbW3dPrimaryTextureReference> CurrentRetailLoadedTextureReferences { get; } = [];
        public List<VertexMaterialState> VertexMaterials { get; } = [];
        public List<MaterialPassState> MaterialPasses { get; } = [];
        public bool HasPrimaryColor1 { get; set; }
        public bool HasPrimaryDiffuseIllumination { get; set; }
        public HashSet<int> PrimaryColorSourcePassOrdinals { get; } = [];
        public List<Vector2> LegacyRootTextureCoordinates { get; } = [];
        public List<EnbColor32> LegacyRootColor1 { get; } = [];
        public List<EnbW3dPrimaryTextureCoordinateSet> CurrentRetailPrimaryTextureCoordinateSets { get; } = [];
        public Dictionary<(int PassOrdinal, int StageOrdinal), int> CurrentRetailPrimaryTextureCoordinateBindings { get; } = [];
        public List<EnbW3dSecondaryMaterialDescriptorChunk> SecondaryMaterialDescriptorChunks { get; } = [];
        public List<EnbW3dLegacyRootMaterial> LegacyRootMaterials { get; } = [];
        public List<EnbW3dPrimaryMaterialReference> CurrentRetailLoadedMaterialReferences { get; } = [];
        public List<uint> CurrentRetailLegacyMaterialCrcs { get; } = [];
        public List<uint> LegacyRootPackedShaders { get; } = [];
        public List<EnbW3dLegacyRootTexture> LegacyRootTextures { get; } = [];
        public List<EnbW3dLegacyRootMaterialMapping> LegacyRootMaterialMappings { get; } = [];
        public PrimaryPass0DescriptorState PrimaryPass0Descriptor { get; } = new();
        public bool CurrentRetailGeometrySortRequestedByLegacyRoot { get; set; }
        public bool CurrentRetailGeometrySortRequested { get; set; }
    }

    private sealed class PrimaryPass0DescriptorState
    {
        public EnbW3dPrimaryMaterialReference? UniformMaterial { get; set; }
        public EnbW3dPrimaryMaterialReference?[]? PerVertexMaterials { get; set; }
        // Primary descriptor shader slots begin unbound (zero) in current retail.
        // Missing shader state is promoted to the packed 0x0008441B runtime baseline
        // later during render-state preparation. Binding the literal baseline here
        // is therefore materially different from leaving the slot unbound because
        // a later modern 0x003A sees the slot as occupied and diverts to secondary.
        public EnbW3dPrimaryShaderReference UniformShader { get; set; }
            = EnbW3dPrimaryShaderReference.FromPackedRetailState(0x0008_441Bu);
        public bool HasUniformShaderBinding { get; set; }
        public EnbW3dPrimaryShaderReference[]? PerTriangleShaders { get; set; }
        public EnbW3dPrimaryTextureReference? UniformTextureStage0 { get; set; }
        public EnbW3dPrimaryTextureReference?[]? PerTriangleTexturesStage0 { get; set; }

        public bool HasMaterialBinding => UniformMaterial is not null || PerVertexMaterials is not null;
        public bool HasTextureStage0Binding => UniformTextureStage0 is not null || PerTriangleTexturesStage0 is not null;
    }

    private sealed class TextureDefinitionState
    {
        public TextureDefinitionState(int ordinal) => Ordinal = ordinal;

        public int Ordinal { get; }
        public string Name { get; set; } = string.Empty;
        public bool HasName { get; set; }
        public EnbW3dTextureInfo? Info { get; set; }
    }

    private sealed class VertexMaterialState
    {
        public VertexMaterialState(int ordinal) => Ordinal = ordinal;

        public int Ordinal { get; }
        public string Name { get; set; } = string.Empty;
        public bool HasName { get; set; }
        public EnbW3dVertexMaterialInfo? Info { get; set; }
        public EnbW3dMapperArguments? Mapper0 { get; set; }
        public EnbW3dMapperArguments? Mapper1 { get; set; }

        public EnbW3dVertexMaterial ToPublic()
            => new(
                Ordinal,
                Name,
                Info!.Value,
                Mapper0,
                Mapper1);
    }

    private sealed class MaterialPassState
    {
        public MaterialPassState(int ordinal) => Ordinal = ordinal;

        public int Ordinal { get; }
        public bool HasPrimaryVertexMaterialIds { get; set; }
        public bool HasPrimaryShaderIds { get; set; }
        public bool HasPrimaryColorSource { get; set; }
        public List<uint> VertexMaterialIds { get; } = [];
        public List<uint> ShaderIds { get; } = [];
        public List<EnbColor32> DiffuseColors { get; } = [];
        public List<EnbColor32> DiffuseIllumination { get; } = [];
        public List<EnbW3dMaterialPassColorChunkKind> ColorChunkOrder { get; } = [];
        public List<EnbColor32> SpecularColors { get; } = [];
        public List<TextureStageState> TextureStages { get; } = [];

        public EnbW3dMaterialPass ToPublic()
            => new(
                Ordinal,
                VertexMaterialIds.ToArray(),
                ShaderIds.ToArray(),
                DiffuseColors.ToArray(),
                DiffuseIllumination.ToArray(),
                SpecularColors.ToArray(),
                TextureStages.Select(static stage => stage.ToPublic()).ToArray())
            {
                ColorChunkOrder = ColorChunkOrder.ToArray(),
                HasPrimaryColorSource = HasPrimaryColorSource,
            };
    }

    private sealed class TextureStageState
    {
        public TextureStageState(int ordinal) => Ordinal = ordinal;

        public int Ordinal { get; }
        public bool HasPrimaryTextureIds { get; set; }
        public bool HasPrimaryRuntimeTexCoords { get; set; }
        public List<uint> TextureIds { get; } = [];
        public List<Vector2> RuntimeTexCoords { get; } = [];

        public EnbW3dTextureStage ToPublic()
            => new(Ordinal, TextureIds.ToArray(), RuntimeTexCoords.ToImmutableArray());
    }

    private static class W3dChunk
    {
        public const uint Vertices = 0x0002;
        public const uint VertexNormals = 0x0003;
        public const uint AlternateVertexNormals = 0x0004;
        public const uint LegacyRootTextureCoordinates = 0x0005;
        public const uint LegacyRootColor1 = 0x000D;
        public const uint VertexInfluences = 0x000E;
        public const uint LegacyRootMaterials = 0x0015;
        public const uint LegacyRootMaterial = 0x0016;
        public const uint LegacyRootMaterialName = 0x0017;
        public const uint LegacyRootMaterialInfo = 0x0018;
        public const uint LegacyRootTexture = 0x0019;
        public const uint LegacyRootTextureName = 0x001A;
        public const uint LegacyRootTextureInfo = 0x001B;
        public const uint LegacyRootAlternateTexture = 0x001E;
        public const uint MeshHeader3 = 0x001F;
        public const uint Triangles = 0x0020;
        public const uint LegacyRootMaterialSelectors = 0x0021;
        public const uint VertexJoinMap = 0x0022;
        public const uint MaterialVariant23 = 0x0023;
        public const uint MaterialVariant24 = 0x0024;
        public const uint MaterialVariant25 = 0x0025;
        public const uint MaterialVariant26 = 0x0026;
        public const uint MaterialInfo = 0x0028;
        public const uint Shaders = 0x0029;
        public const uint VertexMaterials = 0x002A;
        public const uint VertexMaterial = 0x002B;
        public const uint VertexMaterialName = 0x002C;
        public const uint VertexMaterialInfo = 0x002D;
        public const uint MapperArgs0 = 0x002E;
        public const uint MapperArgs1 = 0x002F;
        public const uint Textures = 0x0030;
        public const uint Texture = 0x0031;
        public const uint TextureName = 0x0032;
        public const uint TextureInfo = 0x0033;
        public const uint MaterialPass = 0x0038;
        public const uint VertexMaterialIds = 0x0039;
        public const uint ShaderIds = 0x003A;
        public const uint DiffuseColor = 0x003B;
        public const uint DiffuseIllumination = 0x003C;
        public const uint SpecularColor = 0x003E;
        public const uint TextureStage = 0x0048;
        public const uint TextureIds = 0x0049;
        public const uint StageTexCoords = 0x004A;
        public const uint HierarchyHeader = 0x0101;
        public const uint Pivots = 0x0102;
        public const uint PivotFixups = 0x0103;
        public const uint AnimationHeader = 0x0201;
        public const uint AnimationChannel = 0x0202;
        public const uint BitChannel = 0x0203;
        public const uint CompressedAnimationHeader = 0x0281;
        public const uint CompressedAnimationChannel = 0x0282;
        public const uint CompressedBitChannel = 0x0283;
        public const uint HierarchyModelHeader = 0x0301;
        public const uint HierarchyModelNode = 0x0302;
        public const uint HierarchyModelCollisionNode = 0x0303;
        public const uint HierarchyModelSkinNode = 0x0304;
        public const uint HierarchyModelAuxData = 0x0305;
        public const uint HierarchyModelShadowNode = 0x0306;
        public const uint HlodHeader = 0x0701;
        public const uint HlodLevel = 0x0702;
        public const uint HlodLevelHeader = 0x0703;
        public const uint HlodSubObject = 0x0704;
        public const uint HlodAggregate = 0x0705;
        public const uint HlodProxyArray = 0x0706;
    }
}

public sealed record EnbW3dMesh(
    string Name,
    string ContainerName,
    uint Version,
    uint Attributes,
    uint VertexChannels,
    uint FaceChannels,
    float BoundingRadius,
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<Vector3> Normals,
    IReadOnlyList<EnbW3dVertexInfluence> VertexInfluences,
    IReadOnlyList<EnbW3dTriangle> Triangles,
    EnbW3dMaterialInfo? MaterialInfo,
    IReadOnlyList<EnbW3dShader> Shaders,
    IReadOnlyList<string> TextureNames,
    IReadOnlyList<EnbW3dVertexMaterial> VertexMaterials,
    IReadOnlyList<EnbW3dMaterialPass> MaterialPasses)
{
    /// <summary>
    /// Raw MeshHeader3 SortLevel dword. Current retail stores only its low byte
    /// in MeshModel+0x1C; renderer code derives that exact signed-byte runtime
    /// view rather than rewriting the authored asset value here.
    /// </summary>
    public uint AuthoredSortLevel { get; init; }

    /// <summary>
    /// Authored MeshHeader3 axis-aligned bounding box. Current retail copies
    /// these values into MeshModel+0x48..+0x5C and exposes them through its
    /// render-model bounds getter.
    /// </summary>
    public Vector3 BoundingBoxMin { get; init; }
    public Vector3 BoundingBoxMax { get; init; }

    /// <summary>
    /// Authored MeshHeader3 bounding-sphere center. This is distinct from the
    /// mesh origin and is consumed together with BoundingRadius by current
    /// retail's live mesh submit/culling path.
    /// </summary>
    public Vector3 BoundingSphereCenter { get; init; }

    /// <summary>
    /// Last authored normal child consumed by current-retail-equivalent mesh
    /// loading. Current retail accepts both 0x0003 and the alternate 0x0004 form
    /// and lets a later child overwrite the same fixed normal array.
    /// Null means no authored normal child was present.
    /// </summary>
    public uint? AuthoredNormalChunkType { get; init; }

    /// <summary>Root0022's last complete fixed-count DWORD join map consumed
    /// by0092C220. Null means absent; values are retained without inventing
    /// representatives or normalizing authored forward references.</summary>
    public IReadOnlyList<uint>? AuthoredVertexJoinMap { get; init; }
    public IReadOnlyList<byte>? AuthoredAabTreePayload { get; init; }

    /// <summary>
    /// Root material-variant container selected by the current retail image from
    /// MeshHeader3.Attributes before nested material records are consumed. Null
    /// means the current selector chooses no 0x0023..0x0026 container. This is a
    /// runtime compatibility selection, not a friendly semantic name for the
    /// historical variant family.
    /// </summary>
    public uint? CurrentRetailMaterialVariantChunkType { get; init; }

    /// <summary>
    /// Optional authored 0x0033 TextureInfo records aligned one-for-one with
    /// <see cref="TextureNames"/>. The active retail loader reads three dwords;
    /// All three are kept, while only proven first-dword behavior is
    /// interpreted by the renderer.
    /// </summary>
    public IReadOnlyList<EnbW3dTextureInfo?> TextureInfos { get; init; }
        = Array.Empty<EnbW3dTextureInfo?>();

    /// <summary>Loader acquisition order, including legacy textures later
    /// replaced by an alternate. This differs from final table membership.</summary>
    public IReadOnlyList<EnbW3dTextureAcquisition> TextureAcquisitions { get; init; }
        = Array.Empty<EnbW3dTextureAcquisition>();
    public IReadOnlyList<string> CurrentRetailLoadedTextureNames { get; init; }
        = Array.Empty<string>();
    // Decoded native selector table. Null denotes a directly constructed model
    // whose modern IDs already index TextureNames (the public model contract).
    public IReadOnlyList<EnbW3dPrimaryTextureReference>? CurrentRetailLoadedTextureReferences { get; init; }

    /// <summary>
    /// Authored material-pass chunks that current retail routes into its
    /// secondary MeshModel descriptor (+0x78) after a primary field conflicts.
    /// The current normal render path is proven to consume the primary descriptor
    /// through +0x7C; these bytes are retained as provenance and are not rendered
    /// without a proven secondary-descriptor selection path.
    /// </summary>
    public IReadOnlyList<EnbW3dSecondaryMaterialDescriptorChunk> SecondaryMaterialDescriptorChunks { get; init; }
        = Array.Empty<EnbW3dSecondaryMaterialDescriptorChunk>();

    /// <summary>
    /// Current-retail canonicalized root 0x0005 coordinates. The root path
    /// targets primary material pass 0 / stage 0 and uses (U, 1-V).
    /// </summary>
    public IReadOnlyList<Vector2> LegacyRootTextureCoordinates { get; init; }
        = Array.Empty<Vector2>();

    /// <summary>
    /// Runtime COLO created by root 0x000D when no primary COLO existed yet.
    /// Current retail consumes authored RGB and forces every alpha byte to 255.
    /// Empty means the root did not become the runtime COLO owner.
    /// </summary>
    public IReadOnlyList<EnbColor32> LegacyRootColor1 { get; init; }
        = Array.Empty<EnbColor32>();

    /// <summary>
    /// Current retail's finite primary descriptor coordinate pool in first-seen
    /// order. Deduplication is CRC-only and the pool accepts at most eight sets.
    /// </summary>
    public IReadOnlyList<EnbW3dPrimaryTextureCoordinateSet> CurrentRetailPrimaryTextureCoordinateSets { get; init; }
        = Array.Empty<EnbW3dPrimaryTextureCoordinateSet>();

    /// <summary>
    /// Effective primary descriptor pass/stage bindings after order-sensitive
    /// root and modern coordinate assignment has completed.
    /// </summary>
    public IReadOnlyList<EnbW3dPrimaryTextureCoordinateBinding> CurrentRetailPrimaryTextureCoordinateBindings { get; init; }
        = Array.Empty<EnbW3dPrimaryTextureCoordinateBinding>();

    /// <summary>
    /// MeshHeader3 dword at payload +0x30. Current retail FUN_009BB260 uses it
    /// as the exact record count for the legacy root 0x0015 material family;
    /// FUN_009BB710 also uses the same value to decide whether 0x0021 selectors
    /// are consumed.
    /// </summary>
    public int LegacyRootMaterialCount { get; init; }

    /// <summary>
    /// Current-retail CRC-only deduplicated material table produced by root
    /// 0x0015. Names are retained from the first record owning each CRC; the
    /// renderer consumes the exact float material state rather than quantizing
    /// the legacy RGB products back to modern packed bytes.
    /// </summary>
    public IReadOnlyList<EnbW3dLegacyRootMaterial> LegacyRootMaterials { get; init; }
        = Array.Empty<EnbW3dLegacyRootMaterial>();

    /// <summary>009BAD80/009BDB10 shared temporary C8/D4 material table in
    /// acquisition order; modern entries append, legacy entries use CRC lookup.</summary>
    public IReadOnlyList<EnbW3dPrimaryMaterialReference>? CurrentRetailLoadedMaterialReferences { get; init; }

    /// <summary>
    /// Exact packed current-retail shader states deduplicated by u32 identity
    /// while loading root 0x0015.
    /// </summary>
    public IReadOnlyList<uint> LegacyRootPackedShaders { get; init; }
        = Array.Empty<uint>();

    /// <summary>
    /// Case-insensitively deduplicated texture table produced by root 0x0015.
    /// The nested 0x001B eight-byte records are retained only as provenance;
    /// current retail does not consume their bytes on this path.
    /// </summary>
    public IReadOnlyList<EnbW3dLegacyRootTexture> LegacyRootTextures { get; init; }
        = Array.Empty<EnbW3dLegacyRootTexture>();

    /// <summary>
    /// One mapping record for each authored legacy 0x0016 record, matching the
    /// current loader's material/shader/texture dedup indices.
    /// </summary>
    public IReadOnlyList<EnbW3dLegacyRootMaterialMapping> LegacyRootMaterialMappings { get; init; }
        = Array.Empty<EnbW3dLegacyRootMaterialMapping>();

    /// <summary>
    /// Effective primary pass-0 descriptor after order-sensitive interaction
    /// between root 0x0015/0x0021 and modern 0x0039/0x003A/0x0049 bindings.
    /// Null means no legacy root material family participated, so the ordinary
    /// modern material-pass representation remains controlling.
    /// </summary>
    public EnbW3dPrimaryPass0MaterialDescriptor? CurrentRetailPrimaryPass0MaterialDescriptor { get; init; }

    /// <summary>
    /// Older root 0x0015 sets MeshModel's geometry-SORT request as soon as the
    /// initial packed state has a non-zero destination-blend selector, before
    /// optional legacy texture children can rewrite that shader.
    /// </summary>
    public bool CurrentRetailGeometrySortRequestedByLegacyRoot { get; init; }

    /// <summary>
    /// Final parse-time MeshModel geometry-SORT request latch. This includes the
    /// legacy root 0x0015 request and modern pass-0 ShaderIds requests observed
    /// even when a later binding overwrites the descriptor state.
    /// </summary>
    public bool CurrentRetailGeometrySortRequested { get; init; }

    public bool CurrentRetailPrimaryPass0HasColorSource { get; init; }
}

public enum EnbW3dPrimaryMaterialSourceKind : byte
{
    ModernVertexMaterial = 0,
    LegacyRootMaterial = 1,
}

public readonly record struct EnbW3dPrimaryMaterialReference(
    EnbW3dPrimaryMaterialSourceKind SourceKind,
    uint SourceIndex);

public enum EnbW3dPrimaryShaderSourceKind : byte
{
    ModernAuthoredShader = 0,
    PackedRetailState = 1,
}

public readonly record struct EnbW3dPrimaryShaderReference(
    EnbW3dPrimaryShaderSourceKind SourceKind,
    uint Value)
{
    public static EnbW3dPrimaryShaderReference FromModernShader(uint sourceIndex)
        => new(EnbW3dPrimaryShaderSourceKind.ModernAuthoredShader, sourceIndex);

    public static EnbW3dPrimaryShaderReference FromPackedRetailState(uint packedRetailState)
        => new(EnbW3dPrimaryShaderSourceKind.PackedRetailState, packedRetailState);
}

public enum EnbW3dPrimaryTextureSourceKind : byte
{
    ModernTexture = 0,
    LegacyRootTexture = 1,
}

public readonly record struct EnbW3dPrimaryTextureReference(
    EnbW3dPrimaryTextureSourceKind SourceKind,
    uint SourceIndex);

public sealed record EnbW3dPrimaryPass0MaterialDescriptor(
    EnbW3dPrimaryMaterialReference? UniformMaterial,
    IReadOnlyList<EnbW3dPrimaryMaterialReference?> PerVertexMaterials,
    EnbW3dPrimaryShaderReference UniformShader,
    IReadOnlyList<EnbW3dPrimaryShaderReference> PerTriangleShaders,
    EnbW3dPrimaryTextureReference? UniformTextureStage0,
    IReadOnlyList<EnbW3dPrimaryTextureReference?> PerTriangleTexturesStage0);

public sealed record EnbW3dLegacyRootMaterial(
    int Ordinal,
    string Name,
    byte Attributes,
    Vector3 Diffuse,
    float DiffuseAlpha,
    Vector3 Ambient,
    Vector3 Specular,
    Vector3 Emissive,
    float Power,
    uint CurrentRetailCrc32,
    IReadOnlyList<byte> RawInfo);

public sealed record EnbW3dLegacyRootTexture(
    int Ordinal,
    string Name,
    bool SelectedByAlternate001E,
    IReadOnlyList<byte> RawInfo);

public readonly record struct EnbW3dLegacyRootMaterialMapping(
    int MaterialIndex,
    int ShaderIndex,
    int TextureIndex);

public sealed record EnbW3dTextureAcquisition(string LogicalName, EnbW3dTextureInfo? Info);

public readonly record struct EnbW3dTextureInfo(
    uint Attributes,
    uint Word1,
    uint Word2);

public static class EnbW3dVertexChannels
{
    public const uint Location = 0x0000_0001;
    public const uint Normal = 0x0000_0002;
    public const uint TextureCoordinate = 0x0000_0004;
    public const uint Color = 0x0000_0008;
    public const uint BoneId = 0x0000_0010;
}

public static class EnbW3dFaceChannels
{
    public const uint Face = 0x0000_0001;
}

/// <summary>
/// One W3D skin binding. The on-disk record is an authored BoneIdx followed
/// by six padding bytes; the padding carries no format semantics.
/// </summary>
public readonly record struct EnbW3dVertexInfluence(ushort BoneIndex);

public readonly record struct EnbW3dTriangle(
    uint Vertex0,
    uint Vertex1,
    uint Vertex2,
    uint Attributes,
    Vector3 Normal,
    float Distance);

public readonly record struct EnbW3dMaterialInfo(
    uint PassCount,
    uint VertexMaterialCount,
    uint ShaderCount,
    uint TextureCount);

/// <summary>
/// Raw 16-byte W3D shader record. The renderer maps the retail conversion of
/// these bytes into effective blend/depth/cull state onto backend render-state
/// descriptors.
/// </summary>
public sealed class EnbW3dShader
{
    private readonly byte[] _raw;

    internal EnbW3dShader(byte[] raw)
    {
        if (raw.Length != 16)
            throw new ArgumentException("A W3D shader record must contain exactly 16 bytes.", nameof(raw));
        _raw = (byte[])raw.Clone();
    }

    public ReadOnlyMemory<byte> Raw => _raw;

    public byte this[int index] => _raw[index];
}

public readonly record struct EnbW3dPackedRgb(byte R, byte G, byte B, byte Reserved);

/// <summary>
/// Exact 32-byte W3D vertex-material info payload: the four
/// packed RGB triplets plus Power and Opacity. The final float remains
/// deliberately neutral because its historical label is not required for the
/// recovered presentation contract.
/// </summary>
public readonly record struct EnbW3dVertexMaterialInfo(
    uint Attributes,
    EnbW3dPackedRgb Ambient,
    EnbW3dPackedRgb Diffuse,
    EnbW3dPackedRgb Specular,
    EnbW3dPackedRgb Emissive,
    float Power,
    float Opacity,
    float UnknownFloat)
{
    // Current retail FUN_0092F2A0 owns mapper selection in the material-info
    // Attributes word, independently of whether 0x002E/0x002F argument text is
    // present. Stage 0 uses bits 16..23 and stage 1 uses bits 8..15.
    public byte Mapper0TypeSelector => (byte)((Attributes >> 16) & 0xFFu);
    public byte Mapper1TypeSelector => (byte)((Attributes >> 8) & 0xFFu);
}

public sealed record EnbW3dMapperArguments(
    string Raw,
    float UScale,
    float VScale,
    float UPerSecond,
    float VPerSecond,
    float UOffset,
    float VOffset,
    bool ClampFix);

public sealed record EnbW3dVertexMaterial(
    int Ordinal,
    string Name,
    EnbW3dVertexMaterialInfo Info,
    EnbW3dMapperArguments? Mapper0,
    EnbW3dMapperArguments? Mapper1);

public sealed record EnbW3dHierarchy(
    uint Version,
    string Name,
    Vector3 Center,
    IReadOnlyList<EnbW3dPivot> Pivots,
    IReadOnlyList<EnbW3dPivotFixup> PivotFixups)
{
    public int LoadOffset { get; init; }
}

public sealed record EnbW3dPivot(
    int Ordinal,
    string Name,
    uint ParentIndex,
    Vector3 Translation,
    Vector3 EulerAngles,
    EnbW3dRawQuaternion Rotation)
{
    public bool IsRoot => ParentIndex == uint.MaxValue;
}

/// <summary>
/// Raw four-float W3D quaternion payload. The historical format exposes Q[4];
/// Deliberately does not map those components onto System.Numerics.Quaternion
/// until current-renderer transform semantics need and prove that conversion.
/// </summary>
public readonly record struct EnbW3dRawQuaternion(float Q0, float Q1, float Q2, float Q3)
{
    public bool IsFinite
        => float.IsFinite(Q0) && float.IsFinite(Q1) && float.IsFinite(Q2) && float.IsFinite(Q3);
}

/// <summary>
/// Exact row-major 4x3 pivot-fixup payload. This is retained as authored data;
/// no matrix convention is imposed by the asset decoder.
/// </summary>
public readonly record struct EnbW3dPivotFixup(
    float M00, float M01, float M02,
    float M10, float M11, float M12,
    float M20, float M21, float M22,
    float M30, float M31, float M32);

public enum EnbW3dAnimationChannelKind : ushort
{
    TranslationX = 0,
    TranslationY = 1,
    TranslationZ = 2,
    RotationX = 3,
    RotationY = 4,
    RotationZ = 5,
    Quaternion = 6,
}

public enum EnbW3dAnimationBitChannelKind : ushort
{
    Visibility = 0,
}

public sealed record EnbW3dHierarchyAnimation(
    uint Version,
    string Name,
    string HierarchyName,
    uint NumFrames,
    uint FrameRate,
    IReadOnlyList<EnbW3dAnimationChannel> Channels,
    IReadOnlyList<EnbW3dAnimationBitChannel> BitChannels)
{
    public int LoadOffset { get; init; }
    public EnbW3dTimeMappedAnimation? TimeMapped { get; init; }
    public EnbW3dHAnimFamily Family { get; init; } = EnbW3dHAnimFamily.Classic0200;
    public ushort? CompressionFlavor { get; init; }
    public IReadOnlyList<EnbW3dCompressedAnimationChannel> CompressedChannels { get; init; } =
        Array.Empty<EnbW3dCompressedAnimationChannel>();
    public IReadOnlyList<EnbW3dCompressedAnimationBitChannel> CompressedBitChannels { get; init; } =
        Array.Empty<EnbW3dCompressedAnimationBitChannel>();
}

/// <summary>
/// Current-retail 0x0282 flavor-0 sparse animation channel. Each time code
/// stores an authored frame in bits 0..30. Bit 31 on the next key suppresses
/// interpolation across the preceding segment. Data stays key-major.
/// </summary>
public sealed record EnbW3dCompressedAnimationChannel(
    ushort PivotIndex,
    byte VectorLength,
    byte Flags,
    EnbW3dAnimationChannelKind? KnownKind,
    IReadOnlyList<uint> TimeCodes,
    IReadOnlyList<float> Data)
{
    public int KeyCount => TimeCodes.Count;
}

/// <summary>
/// Current-retail 0x0283 sparse boolean channel. Bits 0..30 are the authored
/// frame and bit 31 is the value returned from the latest key at that frame.
/// </summary>
public sealed record EnbW3dCompressedAnimationBitChannel(
    ushort PivotIndex,
    byte Flags,
    EnbW3dAnimationBitChannelKind? KnownKind,
    byte Reserved,
    IReadOnlyList<uint> TimeCodes)
{
    public int KeyCount => TimeCodes.Count;
}

/// <summary>
/// Classic W3D 0x0202 hierarchy-animation channel. Data is retained as the
/// authored frame-major float vector; runtime interpolation is deliberately
/// outside the asset layer.
/// </summary>
public sealed record EnbW3dAnimationChannel(
    ushort FirstFrame,
    ushort LastFrame,
    ushort VectorLength,
    ushort Flags,
    EnbW3dAnimationChannelKind? KnownKind,
    ushort PivotIndex,
    ushort Padding,
    IReadOnlyList<float> Data)
{
    public int FrameCount => LastFrame - FirstFrame + 1;
}

/// <summary>
/// Classic W3D 0x0203 packed boolean channel. PackedData stays opaque here so
/// bit-order interpretation remains at the consuming animation seam rather
/// than being guessed in the file decoder.
/// </summary>
public sealed record EnbW3dAnimationBitChannel(
    ushort FirstFrame,
    ushort LastFrame,
    ushort Flags,
    EnbW3dAnimationBitChannelKind? KnownKind,
    ushort PivotIndex,
    byte DefaultValue,
    ReadOnlyMemory<byte> PackedData)
{
    public int FrameCount => LastFrame - FirstFrame + 1;
}

public enum EnbW3dHierarchyNodeKind
{
    RenderObject,
    Collision,
    Skin,
    Shadow,
}

public sealed record EnbW3dHierarchyModel(
    uint Version,
    string Name,
    string HierarchyName,
    IReadOnlyList<EnbW3dHierarchyModelNode> Nodes,
    EnbW3dHierarchyModelAuxData? AuxData)
{
}

public readonly record struct EnbW3dHierarchyModelNode(
    int Ordinal,
    EnbW3dHierarchyNodeKind Kind,
    string RenderObjectName,
    ushort PivotIndex);

public sealed record EnbW3dHierarchyModelAuxData(
    uint Attributes,
    uint MeshCount,
    uint CollisionCount,
    uint SkinCount,
    uint ShadowCount,
    IReadOnlyList<uint> FutureCounts,
    float LodMin,
    float LodMax,
    IReadOnlyList<uint> FutureUse);

public sealed record EnbW3dHierarchicalLod(
    uint Version,
    string Name,
    string HierarchyName,
    IReadOnlyList<EnbW3dLodLevel> Levels)
{
    /// <summary>
    /// Raw current-retail 0x0705 aggregate children retained for diagnostics.
    /// Also exposes the final effective typed definition below, matching the
    /// retail loader's single aggregate runtime slot.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> AggregateExtensions { get; init; }
        = Array.Empty<ReadOnlyMemory<byte>>();

    /// <summary>
    /// Raw current-retail 0x0706 proxy-array children retained for diagnostics.
    /// These are query records, not attached render children, and
    /// exposes the final effective typed definition below.
    /// </summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> ProxyArrayExtensions { get; init; }
        = Array.Empty<ReadOnlyMemory<byte>>();

    /// <summary>
    /// Effective current-retail 0x0705 aggregate definition. The retail loader
    /// stores only one level-shaped aggregate slot and later 0x0705 chunks win.
    /// Its second 0x0703 DWORD is preserved as RawValue because the runtime
    /// child-construction path does not consume it.
    /// </summary>
    public EnbW3dHlodAggregate? Aggregate { get; init; }

    /// <summary>
    /// Effective current-retail 0x0706 proxy-array definition. Retail exposes
    /// these records through dedicated proxy count/query virtuals and does not
    /// attach them as ordinary HLOD render children.
    /// </summary>
    public EnbW3dHlodProxyArray? ProxyArray { get; init; }
}

public sealed record EnbW3dHlodAggregate(
    uint RawValue,
    IReadOnlyList<EnbW3dLodMember> Members);

public sealed record EnbW3dHlodProxyArray(
    uint RawHeaderValue,
    IReadOnlyList<EnbW3dLodMember> Members);

public sealed record EnbW3dLodLevel(
    int Ordinal,
    float Threshold,
    uint RawThreshold,
    IReadOnlyList<EnbW3dLodMember> Members);

/// <summary>
/// HLOD 0x0704 member record. The corresponding current-retail
/// runtime pair stores this value as the hierarchy state-record selector used
/// to bind the child transform. The raw-to-runtime remap for legacy hierarchies
/// with a synthetic root remains deliberately unresolved, so the neutral
/// ReferenceIndex name is retained rather than overclaiming a universal pivot
/// index contract.
/// </summary>
public readonly record struct EnbW3dLodMember(uint ReferenceIndex, string Name);

public sealed record EnbW3dSecondaryMaterialDescriptorChunk(
    int MaterialPassOrdinal,
    int? TextureStageOrdinal,
    uint ChunkType,
    ReadOnlyMemory<byte> Payload);

public sealed record EnbW3dPrimaryTextureCoordinateSet(
    int Index,
    uint RetailCrc32,
    IReadOnlyList<Vector2> Coordinates);

public readonly record struct EnbW3dPrimaryTextureCoordinateBinding(
    int MaterialPassOrdinal,
    int TextureStageOrdinal,
    int CoordinateSetIndex);

public enum EnbW3dMaterialPassColorChunkKind
{
    DiffuseColor,
    DiffuseIllumination,
}

public sealed record EnbW3dMaterialPass(
    int Ordinal,
    IReadOnlyList<uint> VertexMaterialIds,
    IReadOnlyList<uint> ShaderIds,
    IReadOnlyList<EnbColor32> DiffuseColors,
    IReadOnlyList<EnbColor32> DiffuseIllumination,
    IReadOnlyList<EnbColor32> SpecularColors,
    IReadOnlyList<EnbW3dTextureStage> TextureStages)
{
    public IReadOnlyList<EnbW3dMaterialPassColorChunkKind> ColorChunkOrder { get; init; }
        = Array.Empty<EnbW3dMaterialPassColorChunkKind>();

    /// <summary>
    /// Effective current-retail primary descriptor color-source occupancy for
    /// this pass. Root 0x000D can assert pass 0 even when it appears outside or
    /// after the material-pass container.
    /// </summary>
    public bool HasPrimaryColorSource { get; init; }
}

public sealed record EnbW3dTextureStage(
    int Ordinal,
    IReadOnlyList<uint> TextureIds,
    IReadOnlyList<Vector2> RuntimeTexCoords)
{
    public bool TryResolveSingleTextureName(IReadOnlyList<string> textureNames, out string textureName)
    {
        textureName = string.Empty;
        if (TextureIds.Count != 1 || TextureIds[0] >= (uint)textureNames.Count)
            return false;

        textureName = textureNames[(int)TextureIds[0]];
        return true;
    }
}

/// <summary>Retail 0740 prototype. Attributes bit0 chooses class1B/1A;
/// local center/extents and cost12 survive the normal zero display mask.</summary>
public sealed record EnbW3dBox(uint Version, uint Attributes, string Name,
    uint Color, Vector3 Center, Vector3 Extents)
{
    public int RetailClassId => (Attributes & 1) != 0 ? 0x1b : 0x1a;
    public int IntegerRenderCost => 12;
}

/// <summary>Registered0750 class16 prototype. The first48 authored bytes
/// are retained unchanged; the C-string begins at byte16.</summary>
public sealed record EnbW3dNullObject(string Name,IReadOnlyList<byte> Payload);
