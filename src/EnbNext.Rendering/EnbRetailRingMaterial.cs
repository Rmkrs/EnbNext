namespace EnbNext.Rendering;

// Ring-specific stages around the existing native compact-shader translator.
// Callers must provide the actual renderer optimization gate and texture binding.
// This does not admit a ring resource into production.
public static class EnbRetailRingMaterial
{
    public static EnbW3dFixedFunctionState Decode(EnbW3dRenderShaderRecord shader, bool optimize, bool textureBound)
    {
        if(!EnbW3dMaterialCompiler.TryDecodeShader(shader,out var authored,out var error))
            throw new InvalidDataException(error);
        uint packed=authored.PackedRetailState;
        // 009131D9 applies general blend/fog optimization; 009131DE clears culling.
        if(optimize)packed=EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(packed)&~0x80000u;
        // 009116E5..170B selects texturing from the resolved texture pointer.
        packed=textureBound?packed|0x10000u:packed&~0x10000u;
        if(!EnbW3dMaterialCompiler.TryDecodePackedRetailState(packed,out var result,out error))
            throw new InvalidDataException(error);
        return result;
    }
}
