using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbCurrentRetailShipColorizationOutcome : byte
{
    NotColorizationMaterial = 0,
    Applied = 1,
    NoColorization = 2,
    MissingRegionPair = 3,
    InvalidVector = 4,
    InvalidSelector = 5,
    MissingEnvironmentMap = 6,
    MaterialCompileFailure = 7,
    OutsideCapturedRenderObjectScope = 8,
}

public readonly record struct EnbCurrentRetailShipColorizationReceipt(
    EnbCurrentRetailShipColorizationOutcome Outcome,
    int Region,
    int TargetTriangles,
    int AppliedTriangles,
    string? Diagnostic)
{
    public bool IsTarget => Outcome != EnbCurrentRetailShipColorizationOutcome.NotColorizationMaterial;

    public bool Applied => Outcome == EnbCurrentRetailShipColorizationOutcome.Applied;

    public int PrimaryTriangles { get; init; }

    public int SecondaryTriangles { get; init; }

    public int SkinTriangles { get; init; }
}

/// <summary>
/// Current-retail 0x0011 RenderObject colorization mutation. Retail carries one
/// pair of { selector, RGB } values per decimal ~dd hierarchy region and applies
/// them to vertex materials whose names begin ~S1/~S2. ~SK receives black
/// ambient/diffuse without a generated mapper or shader-selector rewrite.
/// </summary>
public static class EnbCurrentRetailShipColorizationRuntime
{
    private const uint Stage1ColorSelectorMask = 0x00F0_0000u;
    private const uint Stage1ColorSelectorClearMask = 0xFF0F_FFFFu;

    public static bool HasColorizationMaterialBindings(EnbW3dRenderMaterialPass source)
        => source.VertexMaterials.Any(binding => Classify(binding.Material.Name) != MaterialFamily.None);

    public static int ResolveRegion(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return 0;

        int region = 0;
        ReadOnlySpan<char> path = instancePath.AsSpan();
        foreach (Range range in path.Split('/'))
        {
            ReadOnlySpan<char> segment = path[range];
            if (segment.Length >= 3 &&
                segment[0] == '~' &&
                char.IsAsciiDigit(segment[1]) &&
                char.IsAsciiDigit(segment[2]))
            {
                region = ((segment[1] - '0') * 10) + (segment[2] - '0');
            }
        }

        return region;
    }

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        EnbEnvironmentMapAxis? reflectionAxis,
        out EnbCurrentRetailShipColorizationReceipt receipt)
        => Apply(packet, entries, region, reflectionAxis, applyMutation: true, out receipt);

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        EnbEnvironmentMapAxis? reflectionAxis,
        bool applyMutation,
        out EnbCurrentRetailShipColorizationReceipt receipt)
        => Apply(packet, entries, region, reflectionAxis, applyMutation, null, out receipt);

    internal static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        EnbEnvironmentMapAxis? reflectionAxis,
        bool applyMutation,
        Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>? replacements,
        out EnbCurrentRetailShipColorizationReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(entries);

        EnbW3dRenderMaterialPass source = packet.Material.Source;
        var loadedMaterials = packet.CurrentRetailLoadedMaterials ?? packet.Mesh.CurrentRetailLoadedMaterials;
        int targetTriangles = source.TriangleBindings.Count(static triangle =>
            triangle.VertexMaterial is EnbW3dRenderVertexMaterialBinding binding &&
            Classify(binding.Material.Name) != MaterialFamily.None);
        if (loadedMaterials.Any(material => material is null))
            return Failure(packet, EnbCurrentRetailShipColorizationOutcome.MaterialCompileFailure, region, targetTriangles,
                "Current-retail colorization requires non-null loaded material entries.", out receipt);
        if (!loadedMaterials.Any(material => Classify(material.Name) != MaterialFamily.None))
        {
            receipt = new EnbCurrentRetailShipColorizationReceipt(
                EnbCurrentRetailShipColorizationOutcome.NotColorizationMaterial,
                region,
                0,
                0,
                null);
            return packet;
        }

        if (!applyMutation)
        {
            receipt = CreateReceipt(
                source,
                EnbCurrentRetailShipColorizationOutcome.OutsideCapturedRenderObjectScope,
                region,
                targetTriangles,
                appliedTriangles: 0,
                "The RenderObject instance was not present in the captured packet-time colorization walk.");
            return packet;
        }

        if (entries.Count == 0)
            return Failure(packet, EnbCurrentRetailShipColorizationOutcome.NoColorization, region, targetTriangles,
                "No current-retail colorization entries have materialized.", out receipt);

        if (region < 0 || region > (int.MaxValue / 2) || ((region * 2) + 1) >= entries.Count)
        {
            return Failure(packet, EnbCurrentRetailShipColorizationOutcome.MissingRegionPair, region, targetTriangles,
                $"Colorization region {region} requires flat entries {region * 2}/{(region * 2) + 1}, but only {entries.Count} entries are present.",
                out receipt);
        }

        if (reflectionAxis is not EnbEnvironmentMapAxis axis)
        {
            return Failure(packet, EnbCurrentRetailShipColorizationOutcome.MissingEnvironmentMap, region, targetTriangles,
                "The installed retail environment-map definition is unavailable for this object.", out receipt);
        }

        int primaryIndex = region * 2;
        if (!TryDecodeSide(entries[primaryIndex], axis, out RegionSide primary, out string? primaryDiagnostic))
        {
            EnbCurrentRetailShipColorizationOutcome outcome = primaryDiagnostic!.StartsWith("selector", StringComparison.Ordinal)
                ? EnbCurrentRetailShipColorizationOutcome.InvalidSelector
                : EnbCurrentRetailShipColorizationOutcome.InvalidVector;
            return Failure(packet, outcome, region, targetTriangles,
                $"Colorization region {region} primary {primaryDiagnostic}", out receipt);
        }

        if (!TryDecodeSide(entries[primaryIndex + 1], axis, out RegionSide secondary, out string? secondaryDiagnostic))
        {
            EnbCurrentRetailShipColorizationOutcome outcome = secondaryDiagnostic!.StartsWith("selector", StringComparison.Ordinal)
                ? EnbCurrentRetailShipColorizationOutcome.InvalidSelector
                : EnbCurrentRetailShipColorizationOutcome.InvalidVector;
            return Failure(packet, outcome, region, targetTriangles,
                $"Colorization region {region} secondary {secondaryDiagnostic}", out receipt);
        }

        // 00812390 mutates material-table objects. All descriptor references to
        // one object must observe the same replacement, including retained slots.
        uint? uniformSelector=null;
        uint uniformShader=source.CurrentRetailUniformShader;
        if(source.Ordinal==0)
        {
            //00812E15 updates pass0's retained slot for the one-material path,
            //including an expanded shader array.0081318E/00813266 updates it
            //for multiple materials only when the shader array is absent.
            var loaded=loadedMaterials;
            bool primaryPresent=loaded.Any(material=>Classify(material.Name)==MaterialFamily.Primary);
            bool secondaryPresent=loaded.Any(material=>Classify(material.Name)==MaterialFamily.Secondary);
            if((loaded.Count==1 || source.ShaderCardinality!=EnbW3dRenderBindingCardinality.PerTriangle) &&
                primaryPresent!=secondaryPresent)
            {
                uint selector=primaryPresent?primary.Stage1ColorSelector:secondary.Stage1ColorSelector;
                uniformSelector=selector;
                uniformShader=(uniformShader&Stage1ColorSelectorClearMask)|((selector<<20)&Stage1ColorSelectorMask);
            }
        }

        var changedMaterials = replacements ?? new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>(
            ReferenceEqualityComparer.Instance);
        var loadedSet = new HashSet<EnbW3dRenderVertexMaterial>(loadedMaterials, ReferenceEqualityComparer.Instance);
        EnbW3dRenderVertexMaterial ChangeMaterial(EnbW3dRenderVertexMaterial material)
        {
            // Descriptor-only pointers are not visited by the loaded-table walk.
            if (!loadedSet.Contains(material)) return material;
            if (!changedMaterials.TryGetValue(material, out var changed))
            {
                changed = ApplyMaterial(material, primary, secondary);
                EnbCurrentRetailMeshModelOwner.PreserveMaterialRuntimeWord64(material, changed);
                changedMaterials.Add(material, changed);
            }
            return changed;
        }

        // The native walk includes loaded entries unused by this descriptor.
        // Keep their replacements available to later passes and weapon metal.
        foreach (var material in loadedMaterials)
            ChangeMaterial(material);

        EnbW3dRenderVertexMaterialBinding[] vertexMaterials = source.VertexMaterials
            .Select(binding => binding with
            {
                Material = ChangeMaterial(binding.Material),
                SourceKind = Classify(binding.Material.Name) == MaterialFamily.None
                    ? binding.SourceKind
                    : EnbW3dRenderBindingSourceKind.PackedRetailState,
            })
            .ToArray();

        var triangles = new EnbW3dRenderTriangleBinding[source.TriangleBindings.Count];
        for (int index = 0; index < source.TriangleBindings.Count; index++)
        {
            EnbW3dRenderTriangleBinding triangle = source.TriangleBindings[index];
            if (triangle.VertexMaterial is not EnbW3dRenderVertexMaterialBinding authoredMaterialBinding ||
                Classify(authoredMaterialBinding.Material.Name) is MaterialFamily.None)
            {
                triangles[index] = triangle;
                continue;
            }

            MaterialFamily family = Classify(authoredMaterialBinding.Material.Name);
            EnbW3dRenderVertexMaterial material = ChangeMaterial(authoredMaterialBinding.Material);
            uint packed = triangle.Shader?.FixedFunction.PackedRetailState ??
                          triangle.Semantics?.FixedFunction.PackedRetailState ??
                          EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
            //The per-triangle selector walk is only the multi-material /
            //expanded-shader branch. Single-material and uniform writes are
            //handled below; mixed primary/secondary uniform shaders exit after RGB.
            if (source.Ordinal == 0 && loadedMaterials.Count > 1 &&
                source.ShaderCardinality == EnbW3dRenderBindingCardinality.PerTriangle &&
                family is MaterialFamily.Primary or MaterialFamily.Secondary)
            {
                byte selector = family == MaterialFamily.Primary
                    ? primary.Stage1ColorSelector
                    : secondary.Stage1ColorSelector;
                packed = (packed & Stage1ColorSelectorClearMask) |
                         (((uint)selector << 20) & Stage1ColorSelectorMask);
            }

            EnbW3dVertexLightingSemantics vertexLighting =
                triangle.Semantics?.VertexLighting ?? EnbW3dVertexLightingSemantics.RetailDefault;
            if (!EnbW3dMaterialCompiler.TryCompilePackedRetailState(
                    packed,
                    material,
                    source.TextureStages.Count,
                    vertexLighting,
                    out EnbW3dCompiledMaterialSemantics semantics,
                    out string? diagnostic))
            {
                return Failure(packet, EnbCurrentRetailShipColorizationOutcome.MaterialCompileFailure, region, targetTriangles,
                    $"RenderObj '{packet.RenderObjectName}' material '{material.Name}' could not compile its current-retail colorization state: {diagnostic}",
                    out receipt);
            }

            EnbW3dRenderShaderBinding? shader = triangle.Shader is EnbW3dRenderShaderBinding authoredShader
                ? authoredShader with
                {
                    FixedFunction = semantics.FixedFunction,
                    SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
                }
                : null;
            triangles[index] = triangle with
            {
                VertexMaterial = authoredMaterialBinding with
                {
                    Material = material,
                    SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
                },
                Shader = shader,
                Semantics = semantics,
            };
        }

        //A uniform shader write affects every triangle, including SK/foreign
        //material users. The one-loaded-material path also writes every entry
        //of an existing expanded array (00812E59..00812EA8).
        if(uniformSelector is {} allSelector)
            for(int index=0;index<triangles.Length;index++)
            {
                var triangle=triangles[index];
                uint packed=triangle.Shader?.FixedFunction.PackedRetailState??triangle.Semantics?.FixedFunction.PackedRetailState??source.CurrentRetailUniformShader;
                uint rewritten=(packed&Stage1ColorSelectorClearMask)|((allSelector<<20)&Stage1ColorSelectorMask);
                if(rewritten==packed)continue;
                if(!EnbW3dMaterialCompiler.TryDecodePackedRetailState(rewritten,out var state,out var error))
                    return Failure(packet,EnbCurrentRetailShipColorizationOutcome.MaterialCompileFailure,region,targetTriangles,error!,out receipt);
                triangles[index]=triangle with {
                    Shader=triangle.Shader is {} shader?shader with {FixedFunction=state,SourceKind=EnbW3dRenderBindingSourceKind.PackedRetailState}:null,
                    Semantics=triangle.Semantics is {} semantics?semantics with {FixedFunction=state,
                        PermutationKey=semantics.PermutationKey with {TextureCombinerSemantics=state.TextureCombinerSemantics}}:null,
                };
            }

        EnbW3dRenderShaderBinding[] shaders = source.Shaders.ToArray();
        if (source.ShaderCardinality == EnbW3dRenderBindingCardinality.PerTriangle)
        {
            foreach (EnbW3dRenderTriangleBinding triangle in triangles)
            {
                if ((uint)triangle.TriangleOrdinal < (uint)shaders.Length &&
                    triangle.Shader is EnbW3dRenderShaderBinding shader)
                {
                    shaders[triangle.TriangleOrdinal] = shader;
                }
            }
        }
        else if (source.ShaderCardinality == EnbW3dRenderBindingCardinality.Uniform &&
                 shaders.Length == 1 &&
                 triangles.Length != 0 &&
                 triangles.All(triangle => triangle.Shader is EnbW3dRenderShaderBinding shader &&
                     shader.FixedFunction.PackedRetailState == triangles[0].Shader!.Value.FixedFunction.PackedRetailState))
        {
            shaders[0] = triangles[0].Shader!.Value;
        }

        EnbW3dCompiledMaterialSemantics? single = source.SingleBindingSemantics;
        if (single is not null && triangles.Length != 0)
        {
            EnbW3dCompiledMaterialSemantics? candidate = triangles[0].Semantics;
            single = candidate is not null && triangles.All(triangle => triangle.Semantics == candidate)
                ? candidate
                : null;
        }

        EnbW3dRenderMaterialPass effectivePass = (source).Copy().SetCurrentRetailUniformShader(uniformShader).SetVertexMaterials(vertexMaterials).SetCurrentRetailUniformMaterial(source.CurrentRetailUniformMaterial is { } uniform &&
                changedMaterials.TryGetValue(uniform.Material, out var changedUniform)
                    ? uniform with { Material = changedUniform }
                    : source.CurrentRetailUniformMaterial).SetShaders(EnbW3dShaderBindingTable.Freeze(shaders)).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(triangles)).SetSingleBindingSemantics(single);
        receipt = CreateReceipt(
            source,
            EnbCurrentRetailShipColorizationOutcome.Applied,
            region,
            targetTriangles,
            targetTriangles,
            diagnostic: null);
        return (packet).ForkTask().SetMaterial(new EnbW3dDrawMaterialBinding(effectivePass, single)).SetCurrentRetailLoadedMaterials(loadedMaterials.Select(material => changedMaterials[material]).ToArray());
    }

    /// <summary>
    /// Resolves whether one composed draw instance was physically present when
    /// the retained 0x0011 update performed its immediate RenderObject walk.
    /// Root/fixed instances remain in scope. Lego descendants are owned by the
    /// longest matching current ShipLego or SubPart path and require the exact
    /// packet-time identity plus materialization revision.
    /// </summary>
    public static bool IsInstanceInCapturedRenderObjectScope(
        string? instancePath,
        EnbSceneShipLegoSnapshot? currentShipLegos,
        IReadOnlyList<EnbSceneSubPartSnapshot> currentSubParts,
        EnbSceneColorizationApplicationAuditSnapshot? audit)
    {
        ArgumentNullException.ThrowIfNull(currentSubParts);
        if (string.IsNullOrWhiteSpace(instancePath) ||
            !instancePath.StartsWith("lego/", StringComparison.Ordinal) ||
            audit is null)
        {
            return true;
        }

        string relativePath = instancePath["lego/".Length..];
        EnbSceneShipLegoAttachmentSnapshot? currentLego = currentShipLegos is null
            ? null
            : currentShipLegos.Attachments
                .Where(attachment => IsOwningPath(attachment.BoneName, relativePath))
                .OrderByDescending(static attachment => attachment.BoneName.Length)
                .FirstOrDefault();
        EnbSceneSubPartSnapshot? currentSubPart = null;
        int currentSubPartOrdinal = -1;
        for (int ordinal = 0; ordinal < currentSubParts.Count; ordinal++)
        {
            EnbSceneSubPartSnapshot candidate = currentSubParts[ordinal];
            if (IsOwningPath(candidate.Name, relativePath) &&
                (currentSubPart is null || candidate.Name.Length > currentSubPart.Name.Length))
            {
                currentSubPart = candidate;
                currentSubPartOrdinal = ordinal;
            }
        }

        // A mounted Lego can live below an existing SubPart path. The more
        // specific current identity owns the instance, so a late weapon cannot
        // inherit its parent SubPart's earlier packet-time eligibility.
        if (currentLego is not null &&
            (currentSubPart is null || currentLego.BoneName.Length >= currentSubPart.Name.Length))
        {
            return audit.ShipLegoScope.Any(captured =>
                captured.Ordinal == currentLego.Ordinal &&
                string.Equals(captured.BoneName, currentLego.BoneName, StringComparison.Ordinal) &&
                captured.Type == currentLego.Type &&
                captured.BaseAsset == currentLego.BaseAsset &&
                captured.MaterializationRevision == currentLego.MaterializationRevision);
        }

        if (currentSubPart is not null)
        {
            return audit.SubPartScope.Any(captured =>
                captured.Ordinal == currentSubPartOrdinal &&
                string.Equals(captured.Name, currentSubPart.Name, StringComparison.Ordinal) &&
                captured.PartValue == currentSubPart.PartValue &&
                captured.MaterializationRevision == currentSubPart.MaterializationRevision);
        }

        return true;
    }

    private static bool TryDecodeSide(
        EnbSceneColorizationSnapshot entry,
        EnbEnvironmentMapAxis axis,
        out RegionSide side,
        out string? diagnostic)
    {
        side = default;
        int selector = unchecked((int)entry.Field);
        if (selector < -2)
        {
            diagnostic = $"selector {selector} is outside the current-retail -2/-1/nonnegative domain.";
            return false;
        }

        ImmutableArray<byte> vector = entry.Vector;
        if (vector.Length != 12)
        {
            diagnostic = $"RGB vector has {vector.Length} bytes; current retail requires three little-endian floats.";
            return false;
        }

        ReadOnlySpan<byte> span = vector.AsSpan();
        Vector3 rgb = new(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, 4)));
        //00812390 copies these words directly; nonnegative selectors replace
        //all three with0.25 before the material setters. No finite-value gate.

        EnbW3dMapperTypeSelector mapperType;
        int log2Width;
        int offset;
        byte shaderSelector;
        if (selector >= 0)
        {
            rgb = new Vector3(0.25f);
            mapperType = EnbW3dMapperTypeSelector.Mode14;
            log2Width = 2;
            offset = selector;
            shaderSelector = 0x0B;
        }
        else if (selector == -1)
        {
            mapperType = EnbW3dMapperTypeSelector.Mode14;
            log2Width = 1;
            offset = 3;
            shaderSelector = 0x0C;
        }
        else
        {
            mapperType = EnbW3dMapperTypeSelector.Mode13;
            log2Width = 4;
            offset = 255;
            shaderSelector = 0x02;
        }

        string axisText = axis.ToString();
        string raw = string.Create(
            CultureInfo.InvariantCulture,
            $"FPS=0\r\nLog2Width={log2Width}\r\nLast=0\r\nOffset={offset}\r\nAxis={axisText}");
        var mapper = new EnbW3dRenderMapper(
            mapperType,
            raw,
            1f,
            1f,
            0f,
            0f,
            0f,
            0f,
            false);
        side = new RegionSide(selector, rgb, mapper, shaderSelector,new(mapper));
        diagnostic = null;
        return true;
    }

    private static EnbW3dRenderVertexMaterial ApplyMaterial(
        EnbW3dRenderVertexMaterial material,
        RegionSide primary,
        RegionSide secondary)
    {
        MaterialFamily family = Classify(material.Name);
        if (family == MaterialFamily.None)
            return material;

        EnbW3dRenderMaterialUniformOverride exact = material.CurrentRetailUniformOverride ??
            new EnbW3dRenderMaterialUniformOverride(
                Normalize(material.Ambient),
                Normalize(material.Diffuse),
                Normalize(material.Specular),
                Normalize(material.Emissive),
                material.Power,
                material.Opacity);
        if (family == MaterialFamily.Skin)
        {
            return material with
            {
                CurrentRetailUniformOverride = exact with
                {
                    Ambient = Vector3.Zero,
                    Diffuse = Vector3.Zero,
                },
            };
        }

        RegionSide selected = family == MaterialFamily.Primary ? primary : secondary;
        return material with
        {
            // VertexMaterial +0x24 is the second mapper pointer. The ordinary
            // loader independently fills +0x20 from Attributes bits 16..23 and
            // +0x24 from bits 8..15; colorization replaces only +0x24.
            Mapper1 = selected.Mapper,
            CurrentRetailMapper1Owner = selected.Owner,
            CurrentRetailUniformOverride = exact with
            {
                Ambient = selected.Rgb,
                Diffuse = selected.Rgb,
            },
        };
    }

    private static MaterialFamily Classify(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length < 3 || name[0] != '~' ||
            char.ToUpperInvariant(name[1]) != 'S')
        {
            return MaterialFamily.None;
        }

        return char.ToUpperInvariant(name[2]) switch
        {
            '1' => MaterialFamily.Primary,
            '2' => MaterialFamily.Secondary,
            'K' => MaterialFamily.Skin,
            _ => MaterialFamily.None,
        };
    }

    private static Vector3 Normalize(EnbRenderPackedRgb8 color)
        => EnbW3dMaterialCompiler.DecodeMaterialRgb(color);

    private static bool IsOwningPath(string? candidate, string relativePath)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return false;
        }
        //843970 ->4FC2F0 omits the final delimiter, retaining literal spaces
        //and interior empty tokens. Drawing already uses this tokenized path;
        //the packet-time owner lookup must use the same controller identity.
        ReadOnlySpan<char> canonical=candidate.AsSpan();
        if(canonical[^1]=='/')canonical=canonical[..^1];
        return relativePath.AsSpan().StartsWith(canonical,StringComparison.Ordinal) &&
            (relativePath.Length==canonical.Length || relativePath[canonical.Length]=='/');
    }

    private static EnbCurrentRetailShipColorizationReceipt CreateReceipt(
        EnbW3dRenderMaterialPass source,
        EnbCurrentRetailShipColorizationOutcome outcome,
        int region,
        int targetTriangles,
        int appliedTriangles,
        string? diagnostic)
        => new(outcome, region, targetTriangles, appliedTriangles, diagnostic)
        {
            PrimaryTriangles = source.TriangleBindings.Count(static triangle =>
                triangle.VertexMaterial is EnbW3dRenderVertexMaterialBinding binding &&
                Classify(binding.Material.Name) == MaterialFamily.Primary),
            SecondaryTriangles = source.TriangleBindings.Count(static triangle =>
                triangle.VertexMaterial is EnbW3dRenderVertexMaterialBinding binding &&
                Classify(binding.Material.Name) == MaterialFamily.Secondary),
            SkinTriangles = source.TriangleBindings.Count(static triangle =>
                triangle.VertexMaterial is EnbW3dRenderVertexMaterialBinding binding &&
                Classify(binding.Material.Name) == MaterialFamily.Skin),
        };

    private static EnbW3dDrawTask Failure(
        EnbW3dDrawTask packet,
        EnbCurrentRetailShipColorizationOutcome outcome,
        int region,
        int targetTriangles,
        string diagnostic,
        out EnbCurrentRetailShipColorizationReceipt receipt)
    {
        receipt = new EnbCurrentRetailShipColorizationReceipt(
            outcome,
            region,
            targetTriangles,
            0,
            diagnostic);
        return packet;
    }

    private readonly record struct RegionSide(
        int Selector,
        Vector3 Rgb,
        EnbW3dRenderMapper Mapper,
        byte Stage1ColorSelector,
        EnbW3dRetainedMapperOwner Owner);

    private enum MaterialFamily : byte
    {
        None,
        Primary,
        Secondary,
        Skin,
    }
}
