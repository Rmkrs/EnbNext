namespace EnbNext.Rendering;

/// <summary>Post-load material mutation for stable installed asset passes.
/// The immutable source pass owns the result; object pose/time never enter this cache.</summary>
public sealed class EnbCurrentRetailBaseAssetOverbrightCache
{
    private readonly Dictionary<EnbW3dRenderMaterialPass,
        (EnbW3dDrawMaterialBinding Material, EnbCurrentRetailBaseAssetOverbrightReceipt Receipt)> _passes = new(ReferenceEqualityComparer.Instance);

    public void Clear() => _passes.Clear();

    public bool OwnsDescriptor(EnbW3dRenderMaterialPass pass)
        => _passes.Values.Any(value => ReferenceEquals(value.Material.Source, pass));

    public EnbW3dDrawTask Apply(EnbW3dDrawTask packet, bool overbrightOff,
        out EnbCurrentRetailBaseAssetOverbrightReceipt receipt)
    {
        var material = Resolve(packet, overbrightOff, out receipt);
        return ReferenceEquals(packet.Material, material) ? packet : (packet).ForkTask().SetMaterial(material);
    }

    // Resolve persistent material state without allocating an intermediate
    // frame packet. The caller combines this with its instance surface writes.
    public EnbW3dDrawMaterialBinding Resolve(EnbW3dDrawTask packet, bool overbrightOff,
        out EnbCurrentRetailBaseAssetOverbrightReceipt receipt)
    {
        if (overbrightOff)
        {
            receipt = new(EnbCurrentRetailBaseAssetOverbrightOutcome.DisabledByBaseAsset, 0, 0, null);
            return packet.Material;
        }
        if (!_passes.TryGetValue(packet.Material.Source, out var cached))
        {
            var result = EnbCurrentRetailBaseAssetOverbrightRuntime.Apply(packet, false, out receipt);
            // Failure text is object-specific; keep the existing rejection path.
            if (receipt.Failed) return result.Material;
            cached = (result.Material, receipt);
            _passes.Add(packet.Material.Source, cached);
        }
        receipt = cached.Receipt;
        return cached.Material;
    }
}

public enum EnbCurrentRetailBaseAssetOverbrightOutcome : byte
{
    DisabledByBaseAsset = 0,
    NoModulateShader = 1,
    Applied = 2,
    MissingCompiledSemantics = 3,
    StateDecodeFailure = 4,
}

public readonly record struct EnbCurrentRetailBaseAssetOverbrightReceipt(
    EnbCurrentRetailBaseAssetOverbrightOutcome Outcome,
    int TargetTriangles,
    int AppliedTriangles,
    string? Diagnostic)
{
    public bool Enabled => Outcome != EnbCurrentRetailBaseAssetOverbrightOutcome.DisabledByBaseAsset;

    public bool Applied => Outcome == EnbCurrentRetailBaseAssetOverbrightOutcome.Applied;

    public bool Failed => Outcome is
        EnbCurrentRetailBaseAssetOverbrightOutcome.MissingCompiledSemantics or
        EnbCurrentRetailBaseAssetOverbrightOutcome.StateDecodeFailure;
}

/// <summary>
/// Exact post-load overbright mutation performed by current retail
/// FUN_009BD3A0 when its global load flag is set. BaseAsset enables it by
/// default, subject to authored basset.ini OverbrightOff. Frontend avatar
/// loading and CharacterView mode 2 also set the same flag. The mutation changes only stage-zero selector 1 (MODULATE)
/// to selector 5 (MODULATE2X), preserving every other packed shader bit.
/// </summary>
public static class EnbCurrentRetailBaseAssetOverbrightRuntime
{
    private const uint Stage0ColorSelectorMask = 0x0000_1C00u;
    private const uint Stage0ModulateSelector = 0x0000_0400u;
    private const uint RetailClearMask = 0xFFFF_F7FFu;
    private const uint Stage0Modulate2XSelector = 0x0000_1400u;

    public static EnbW3dDrawTask Apply(
        EnbW3dDrawTask packet,
        bool overbrightOff,
        out EnbCurrentRetailBaseAssetOverbrightReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(packet);

        if (overbrightOff)
        {
            receipt = new EnbCurrentRetailBaseAssetOverbrightReceipt(
                EnbCurrentRetailBaseAssetOverbrightOutcome.DisabledByBaseAsset,
                0,
                0,
                null);
            return packet;
        }

        EnbW3dRenderMaterialPass source = packet.Material.Source;
        //009BD3C8 updates descriptor+94 independently of its optional expanded
        //shader array at+E4. Even an empty/mixed triangle array retains this slot.
        uint uniformShader = source.CurrentRetailUniformShader;
        if ((uniformShader & Stage0ColorSelectorMask) == Stage0ModulateSelector)
            uniformShader = (uniformShader & RetailClearMask) | Stage0Modulate2XSelector;
        var triangles = new EnbW3dRenderTriangleBinding[source.TriangleBindings.Count];
        int targets = 0;
        for (int index = 0; index < source.TriangleBindings.Count; index++)
        {
            EnbW3dRenderTriangleBinding triangle = source.TriangleBindings[index];
            uint packed = triangle.Shader?.FixedFunction.PackedRetailState ??
                          triangle.Semantics?.FixedFunction.PackedRetailState ??
                          EnbW3dMaterialCompiler.RetailW3dShaderBaseline;
            if ((packed & Stage0ColorSelectorMask) != Stage0ModulateSelector)
            {
                triangles[index] = triangle;
                continue;
            }

            targets++;
            if (triangle.Semantics is not EnbW3dCompiledMaterialSemantics authoredSemantics)
            {
                receipt = new EnbCurrentRetailBaseAssetOverbrightReceipt(
                    EnbCurrentRetailBaseAssetOverbrightOutcome.MissingCompiledSemantics,
                    targets,
                    0,
                    $"RenderObj '{packet.RenderObjectName}' triangle {triangle.TriangleOrdinal} has no compiled semantics for the current-retail BaseAsset overbright rewrite.");
                return packet;
            }

            // This is the literal current-client assignment. The source
            // predicate proves bit 0x0800 is clear; retaining the exact mask is
            // still important evidence that no other shader field is changed.
            uint rewrittenPacked = (packed & RetailClearMask) | Stage0Modulate2XSelector;
            if (!EnbW3dMaterialCompiler.TryDecodePackedRetailState(
                    rewrittenPacked,
                    out EnbW3dFixedFunctionState fixedFunction,
                    out string? diagnostic))
            {
                receipt = new EnbCurrentRetailBaseAssetOverbrightReceipt(
                    EnbCurrentRetailBaseAssetOverbrightOutcome.StateDecodeFailure,
                    targets,
                    0,
                    $"RenderObj '{packet.RenderObjectName}' triangle {triangle.TriangleOrdinal} could not decode the current-retail BaseAsset overbright state: {diagnostic}");
                return packet;
            }

            EnbW3dCompiledMaterialSemantics semantics = authoredSemantics with
            {
                FixedFunction = fixedFunction,
                PermutationKey = authoredSemantics.PermutationKey with
                {
                    TextureCombinerSemantics = fixedFunction.TextureCombinerSemantics,
                },
            };
            EnbW3dRenderShaderBinding? shader = triangle.Shader is EnbW3dRenderShaderBinding authoredShader
                ? authoredShader with
                {
                    FixedFunction = fixedFunction,
                    SourceKind = EnbW3dRenderBindingSourceKind.PackedRetailState,
                }
                : null;
            triangles[index] = triangle with
            {
                Shader = shader,
                Semantics = semantics,
            };
        }

        if (targets == 0 && uniformShader == source.CurrentRetailUniformShader)
        {
            receipt = new EnbCurrentRetailBaseAssetOverbrightReceipt(
                EnbCurrentRetailBaseAssetOverbrightOutcome.NoModulateShader,
                0,
                0,
                null);
            return packet;
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

        EnbW3dRenderMaterialPass effectivePass = (source).Copy().SetCurrentRetailUniformShader(uniformShader).SetShaders(EnbW3dShaderBindingTable.Freeze(shaders)).SetTriangleBindings(EnbW3dTriangleBindingPool.Freeze(triangles)).SetSingleBindingSemantics(single);
        receipt = new EnbCurrentRetailBaseAssetOverbrightReceipt(
            EnbCurrentRetailBaseAssetOverbrightOutcome.Applied,
            targets,
            targets,
            null);
        return (packet).ForkTask().SetMaterial(new EnbW3dDrawMaterialBinding(effectivePass, single));
    }
}
