namespace EnbNext.Rendering;

public readonly record struct EnbRetailTerrainDecorationPrototype(EnbW3dRenderMesh? Decoration,EnbW3dRenderMesh? Shadow)
{
    public EnbRetailTerrainDecorationMaterial? DecorationMaterial {get;init;}
    public EnbRetailTerrainDecorationMaterial? ShadowMaterial {get;init;}
}

/// <summary>47C580 over retained mesh/NULL/HLOD instances from the shared registry.</summary>
public static class EnbRetailTerrainDecorationSelector
{
    public static EnbRetailTerrainDecorationPrototype Select(EnbRetailShadowObject root)
    {
        var regular=Find(false);var shadow=Find(true);
        return new(regular.Mesh,shadow.Mesh){DecorationMaterial=regular.Material,ShadowMaterial=shadow.Material};
        (EnbW3dRenderMesh? Mesh,EnbRetailTerrainDecorationMaterial? Material) Find(bool shadow)
        {
            var selected=EnbRetailTerrainShadowObjectFactory.SelectMesh(root,shadow);
            if(selected is null)return default;
            try{return (EnbRetailTerrainShadowObjectFactory.Descriptor(selected),EnbRetailTerrainDecorationMaterial.Create(selected));}
            finally{EnbRetailTerrainShadowObjectFactory.Release(selected);}
        }
    }
}
