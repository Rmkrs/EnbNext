using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

public enum EnbCurrentRetailWeaponMetalColorOutcome : byte
{
    NotWeaponInstance = 0,
    NotPrimaryMetalMaterial = 1,
    Applied = 2,
    NoColorization = 3,
    MissingRegion = 4,
    InvalidVector = 5,
    MetalEquivalentUnresolved = 6,
    MaterialCompileFailure = 7,
    Unchanged = 8,
}

public readonly record struct EnbCurrentRetailWeaponMetalColorReceipt(
    EnbCurrentRetailWeaponMetalColorOutcome Outcome,
    int Region,
    int TargetTriangles,
    int AppliedTriangles,
    string? Diagnostic)
{
    public bool IsTarget => TargetTriangles != 0;

    public bool Applied => Outcome == EnbCurrentRetailWeaponMetalColorOutcome.Applied;
}

/// <summary>
/// Current-retail ship-initialization weapon-metal pass. InitializeShipVisualEffects
/// calls FUN_0080F220/FUN_0080F250, which recognizes a ~WEAP container, inherits
/// the enclosing decimal ~dd region, and calls FUN_0080F470 on its mesh leaves.
/// That leaf pass changes only ambient/diffuse RGB on ~S1* vertex materials. It
/// deliberately retains the authored mapper and packed stage-one selector.
/// </summary>
public static class EnbCurrentRetailWeaponMetalColorRuntime
{
    public static bool IsWeaponInstance(string? instancePath)
    {
        if (string.IsNullOrWhiteSpace(instancePath))
            return false;

        ReadOnlySpan<char> path = instancePath.AsSpan();
        foreach (Range range in path.Split('/'))
        {
            ReadOnlySpan<char> segment = path[range];
            if (segment.Length >= 5 && segment[..5].Equals("~WEAP", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        string? instancePath,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        out EnbCurrentRetailWeaponMetalColorReceipt receipt)
        => Apply(packet, instancePath, entries, region, null, out receipt);

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        string? instancePath,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        EnbMetalEquivalentRgbCatalog? metalEquivalentRgbs,
        out EnbCurrentRetailWeaponMetalColorReceipt receipt)
        => Apply(packet, instancePath, entries, region, metalEquivalentRgbs, null, null, out receipt);

    internal static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        string? instancePath,
        IReadOnlyList<EnbSceneColorizationSnapshot> entries,
        int region,
        EnbMetalEquivalentRgbCatalog? metalEquivalentRgbs,
        Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>? sharedReplacements,
        IReadOnlyDictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>? precedingReplacements,
        out EnbCurrentRetailWeaponMetalColorReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(entries);

        if (!IsWeaponInstance(instancePath))
        {
            receipt = new EnbCurrentRetailWeaponMetalColorReceipt(
                EnbCurrentRetailWeaponMetalColorOutcome.NotWeaponInstance,
                region,
                0,
                0,
                null);
            return packet;
        }

        EnbW3dRenderMaterialPass source = packet.Material.Source;
        var loadedMaterials = packet.CurrentRetailLoadedMaterials ?? packet.Mesh.CurrentRetailLoadedMaterials;
        int targetTriangles = source.TriangleBindings.Count(static triangle =>
            triangle.VertexMaterial is EnbW3dRenderVertexMaterialBinding binding &&
            IsPrimaryMetal(binding.Material.Name));
        if (loadedMaterials.Any(material => material is null))
            return Failure(packet, EnbCurrentRetailWeaponMetalColorOutcome.MaterialCompileFailure, region, targetTriangles,
                "Current-retail weapon color requires non-null loaded material entries.", out receipt);
        if (targetTriangles == 0 && !loadedMaterials.Any(material => IsPrimaryMetal(material.Name)))
        {
            receipt = new EnbCurrentRetailWeaponMetalColorReceipt(
                EnbCurrentRetailWeaponMetalColorOutcome.NotPrimaryMetalMaterial,
                region,
                0,
                0,
                null);
            return packet;
        }

        if (entries.Count == 0)
            return Failure(packet, EnbCurrentRetailWeaponMetalColorOutcome.NoColorization, region, targetTriangles,
                "No current-retail ship material-region definitions have materialized.", out receipt);

        if (region < 0 || region > (int.MaxValue / 2) || (region * 2) >= entries.Count)
        {
            return Failure(packet, EnbCurrentRetailWeaponMetalColorOutcome.MissingRegion, region, targetTriangles,
                $"Weapon region {region} requires primary flat entry {region * 2}, but only {entries.Count} entries are present.",
                out receipt);
        }

        EnbSceneColorizationSnapshot primary = entries[region * 2];
        int selector = unchecked((int)primary.Field);
        Vector3 rgb;
        if (selector >= 0)
        {
            // 0080F470 continues after an absent file/section/key: each
            // 009CA770 read receives the explicit default 1.0.
            EnbRgb metalRgb = metalEquivalentRgbs?.ResolveOrDefault(selector) ?? new EnbRgb(1, 1, 1);
            rgb = new Vector3(metalRgb.R, metalRgb.G, metalRgb.B);
        }
        else if (!TryDecodeRgb(primary.Vector, out rgb, out string? vectorDiagnostic))
        {
            return Failure(packet, EnbCurrentRetailWeaponMetalColorOutcome.InvalidVector, region, targetTriangles,
                $"Weapon region {region} primary {vectorDiagnostic}", out receipt);
        }

        // 0080F5DD..F6E0 scans the whole loaded table before copying/writing.
        // In the surface pipeline that table has already received colorization.
        bool changes = loadedMaterials.Any(material =>
            RequiresRgbMutation(precedingReplacements is not null && precedingReplacements.TryGetValue(material, out var preceding)
                ? preceding : material, rgb));
        if (!changes)
        {
            receipt = new(EnbCurrentRetailWeaponMetalColorOutcome.Unchanged, region, targetTriangles, 0, null);
            return packet;
        }

        // 0080F470 changes the material object, so retained and expanded
        // references to that object must observe the same replacement.
        var replacements = sharedReplacements ?? new Dictionary<EnbW3dRenderVertexMaterial, EnbW3dRenderVertexMaterial>(ReferenceEqualityComparer.Instance);
        var loadedSet = new HashSet<EnbW3dRenderVertexMaterial>(loadedMaterials, ReferenceEqualityComparer.Instance);
        EnbW3dRenderVertexMaterial Recolor(EnbW3dRenderVertexMaterial material)
        {
            if (!loadedSet.Contains(material) || !IsPrimaryMetal(material.Name)) return material;
            if (!replacements.TryGetValue(material, out var changed))
            {
                changed = ApplyRgb(material, rgb);
                EnbCurrentRetailMeshModelOwner.PreserveMaterialRuntimeWord64(material, changed);
                replacements.Add(material, changed);
            }
            return changed;
        }

        var changedTable = loadedMaterials.Select(material => Recolor(material)).ToArray();
        EnbW3dRenderVertexMaterialBinding[] vertexMaterials = source.VertexMaterials
            .Select(binding => IsPrimaryMetal(binding.Material.Name)
                ? binding with { Material = Recolor(binding.Material) }
                : binding)
            .ToArray();

        var triangles = new EnbW3dRenderTriangleBinding[source.TriangleBindings.Count];
        for (int index = 0; index < source.TriangleBindings.Count; index++)
        {
            EnbW3dRenderTriangleBinding triangle = source.TriangleBindings[index];
            if (triangle.VertexMaterial is not EnbW3dRenderVertexMaterialBinding authoredMaterialBinding ||
                !IsPrimaryMetal(authoredMaterialBinding.Material.Name))
            {
                triangles[index] = triangle;
                continue;
            }

            EnbW3dRenderVertexMaterial material = Recolor(authoredMaterialBinding.Material);
            uint packed = triangle.Shader?.FixedFunction.PackedRetailState ??
                          triangle.Semantics?.FixedFunction.PackedRetailState ??
                          EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
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
                return Failure(packet, EnbCurrentRetailWeaponMetalColorOutcome.MaterialCompileFailure, region, targetTriangles,
                    $"RenderObj '{packet.RenderObjectName}' material '{material.Name}' could not compile its current-retail weapon-metal state: {diagnostic}",
                    out receipt);
            }

            triangles[index] = triangle with
            {
                VertexMaterial = authoredMaterialBinding with { Material = material },
                Semantics = semantics,
            };
        }

        EnbW3dCompiledMaterialSemantics? single = source.SingleBindingSemantics;
        if (single is not null && triangles.Length != 0)
        {
            EnbW3dCompiledMaterialSemantics? candidate = triangles[0].Semantics;
            single = candidate is not null && triangles.All(triangle => triangle.Semantics == candidate)
                ? candidate
                : null;
        }

        EnbW3dRenderMaterialPass effectivePass = (source).Copy().SetVertexMaterials(vertexMaterials).SetCurrentRetailUniformMaterial(source.CurrentRetailUniformMaterial is { } uniform
                ? uniform with { Material = Recolor(uniform.Material) } : null).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(triangles)).SetSingleBindingSemantics(single);
        receipt = new EnbCurrentRetailWeaponMetalColorReceipt(
            EnbCurrentRetailWeaponMetalColorOutcome.Applied,
            region,
            targetTriangles,
            targetTriangles,
            null);
        return (packet).ForkTask().SetMaterial(new EnbW3dDrawMaterialBinding(effectivePass, single)).SetCurrentRetailLoadedMaterials(changedTable);
    }

    private static bool TryDecodeRgb(ImmutableArray<byte> vector, out Vector3 rgb, out string? diagnostic)
    {
        if (vector.Length != 12)
        {
            rgb = default;
            diagnostic = $"RGB vector has {vector.Length} bytes; current retail requires three little-endian floats.";
            return false;
        }

        ReadOnlySpan<byte> span = vector.AsSpan();
        rgb = new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, 4)));
        // The native leaf loads these words directly. Its ordered threshold
        // comparisons, rather than a finiteness rejection, control mutation.
        diagnostic = null;
        return true;
    }

    /// <summary>0080F640..F6DF: FLD/FSUB/FABS, double-memory FCOMP,
    /// then TEST AH,41. Equal and unordered comparisons do not enter mutation.</summary>
    public static bool RequiresRgbMutation(EnbW3dRenderVertexMaterial material, Vector3 rgb,
        EnbRetailLinePrecision precision = EnbRetailLinePrecision.Single24)
    {
        if (!IsPrimaryMetal(material.Name)) return false;
        using var scope = new EnbRetailLineArithmetic.Scope(precision);
        bool Different(float actual, float desired)
        {
            var value = EnbRetailExtendedX87Register.FromFloatBits(BitConverter.SingleToUInt32Bits(actual));
            value = EnbRetailExtendedX87Register.SubtractFloatMemory(value, BitConverter.SingleToUInt32Bits(desired));
            ushort status = EnbRetailExtendedX87Register.CompareDoubleMemory(EnbRetailExtendedX87Register.Abs(value),
                0x3f70624de0000000, precision == EnbRetailLinePrecision.Single24 ? (ushort)0x7f : (ushort)0x27f);
            return (status & 0x4100) == 0;
        }
        var ambient = material.CurrentRetailUniformOverride?.Ambient ?? Normalize(material.Ambient);
        var diffuse = material.CurrentRetailUniformOverride?.Diffuse ?? Normalize(material.Diffuse);
        return Different(diffuse.X, rgb.X) || Different(diffuse.Y, rgb.Y) || Different(diffuse.Z, rgb.Z) ||
            Different(ambient.X, rgb.X) || Different(ambient.Y, rgb.Y) || Different(ambient.Z, rgb.Z);
    }

    private static EnbW3dRenderVertexMaterial ApplyRgb(EnbW3dRenderVertexMaterial material, Vector3 rgb)
    {
        EnbW3dRenderMaterialUniformOverride exact = material.CurrentRetailUniformOverride ??
            new EnbW3dRenderMaterialUniformOverride(
                Normalize(material.Ambient),
                Normalize(material.Diffuse),
                Normalize(material.Specular),
                Normalize(material.Emissive),
                material.Power,
                material.Opacity);
        return material with
        {
            CurrentRetailUniformOverride = exact with
            {
                Ambient = rgb,
                Diffuse = rgb,
            },
        };
    }

    private static bool IsPrimaryMetal(string? name)
        => name is { Length: >= 3 } &&
           name[0] == '~' &&
           char.ToUpperInvariant(name[1]) == 'S' &&
           name[2] == '1';

    private static Vector3 Normalize(EnbRenderPackedRgb8 color)
        => EnbW3dMaterialCompiler.DecodeMaterialRgb(color);

    private static EnbW3dDrawTask Failure(
        EnbW3dDrawTask packet,
        EnbCurrentRetailWeaponMetalColorOutcome outcome,
        int region,
        int targetTriangles,
        string diagnostic,
        out EnbCurrentRetailWeaponMetalColorReceipt receipt)
    {
        receipt = new EnbCurrentRetailWeaponMetalColorReceipt(
            outcome,
            region,
            targetTriangles,
            0,
            diagnostic);
        return packet;
    }
}
