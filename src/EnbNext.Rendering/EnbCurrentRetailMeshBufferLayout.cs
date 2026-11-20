namespace EnbNext.Rendering;

/// <summary>
/// Native container FVF construction (00978820/0097AC10). Inputs describe the
/// retained descriptor arrays and resolved normal requirement, not GPU streams.
/// </summary>
public static class EnbCurrentRetailMeshBufferLayout
{
    /// <summary>
    /// Normal-policy bits set by 009BAA00 with current load policy BB9650=1.
    /// The high attribute family bypasses the low 4000 branch, including when
    /// none of its supported choices is set. This is not the full runtime flag word.
    /// </summary>
    public static uint AuthoredNormalPolicyFlags(uint attributes)
    {
        if ((attributes & 0x0f000000u) != 0)
            return (attributes & 0x04000000u) != 0 ? 0x4000u
                : (attributes & 0x02000000u) != 0 ? 0x2000u : 0;
        return attributes & 0x4000u;
    }

    public static bool RequiresAuthoredNormals(uint attributes,
        IEnumerable<EnbW3dRenderMaterialPass> activePasses, bool devicePresent,
        bool deviceNPatches, uint patchLevel)
        => RequiresNormals(AuthoredNormalPolicyFlags(attributes), devicePresent, deviceNPatches, patchLevel,
            //00948658 chooses the retained uniform slot first. An expanded
            //array is examined only when that slot is null; the effective
            //draw bindings need not describe the retained descriptor slot.
            activePasses.SelectMany(pass => pass.CurrentRetailUniformMaterial is {} uniform
                ? new[] { uniform } : pass.VertexMaterials).SelectMany(binding => new[]
            {
                binding.Material.Mapper0?.TypeSelector ?? EnbW3dMapperTypeSelector.None,
                binding.Material.Mapper1?.TypeSelector ?? EnbW3dMapperTypeSelector.None,
            }));

    /// <summary>
    /// 00959940/00948620. Runtime flags are the translated MeshModel+18 flags,
    /// not the W3D header attributes. Mappers must come from retained active
    /// pass materials, including per-vertex materials where present.
    /// </summary>
    public static bool RequiresNormals(uint runtimeFlags, bool devicePresent,
        bool deviceNPatches, uint patchLevel, IEnumerable<EnbW3dMapperTypeSelector> activeMappers)
    {
        if ((runtimeFlags & 0xe000u) == 0) return true;
        if (devicePresent && deviceNPatches && patchLevel > 1) return true;
        return activeMappers.Any(selector => selector.RequiresCurrentRetailNormals());
    }

    public static uint CalculateFvf(int contiguousCoordinateArrays, bool hasColor1, bool hasColor2,
        bool requiresNormals, bool usesSkinContainer, bool geometrySortRequested,
        bool lowLevelSortingEnabled)
    {
        if (usesSkinContainer) return 0x112;
        if (geometrySortRequested && lowLevelSortingEnabled) return 0x252;
        uint fvf = 2;
        if (hasColor1) fvf |= 0x40;
        if (hasColor2) fvf |= 0x80;
        // Native stops counting at the first absent array, with eight slots.
        fvf |= (uint)Math.Clamp(contiguousCoordinateArrays, 0, 8) << 8;
        if (requiresNormals) fvf |= 0x10;
        return fvf;
    }
}
