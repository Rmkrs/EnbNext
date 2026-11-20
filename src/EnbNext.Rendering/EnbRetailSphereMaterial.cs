namespace EnbNext.Rendering;

// Creation 0090E230 and submission 0090C0F0. Spheres retain authored culling.
public static class EnbRetailSphereMaterial
{
    // 0090BF80 seeds zero reflectance, emissive white, alpha .25 and power 0.
    // 0090C645 updates emissive, and only non-additive draws replace alpha.
    public static EnbW3dMaterialUniforms ApplyColor(EnbW3dMaterialUniforms material,System.Numerics.Vector3 color,float opacity,uint packed)
    {
        bool additive=(packed&0xe0)==0x20;
        return material with {Ambient=System.Numerics.Vector3.Zero,Diffuse=System.Numerics.Vector3.Zero,
            Specular=System.Numerics.Vector3.Zero,Emissive=additive?color*opacity:color,
            Power=0,DiffuseAlpha=additive?.25f:opacity};
    }
    public static EnbW3dFixedFunctionState Decode(EnbW3dRenderShaderRecord shader,bool optimize,bool textureBound)
    {
        if(!EnbW3dMaterialCompiler.TryDecodeShader(shader,out var authored,out var error))
            throw new InvalidDataException(error);
        uint packed=authored.PackedRetailState;
        if(optimize)packed=EnbW3dRetailShaderOptimizer.OptimizeGeneralFog(packed);
        packed=textureBound?packed|0x10000u:packed&~0x10000u;
        if(!EnbW3dMaterialCompiler.TryDecodePackedRetailState(packed,out var result,out error))
            throw new InvalidDataException(error);
        return result;
    }
}
