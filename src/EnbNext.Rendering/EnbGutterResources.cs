using EnbNext.Assets;
using EnbNext.Presentation;

namespace EnbNext.Rendering;

/// <summary>Installed gutter presentation data owned by the asset render session.</summary>
public sealed class EnbGutterResources
{
    private readonly record struct Resource(EnbUiRaster? Icon, EnbTargetReticleGlyphSet? Reticle);
    private readonly EnbRetailAssetBundle assets;
    private readonly Dictionary<(string Name,byte R,byte G,byte B,int Size),Resource> resources = [];
    private readonly Dictionary<(string Name,int Spacing,float Scale),EnbRetailFontFace> fonts = [];
    public EnbGutterFrameGeometry? Frame { get; }
    public float? IconOpacity { get; }
    internal EnbGutterResources(EnbRetailAssetBundle assets)
    {
        this.assets = assets;
        if (EnbGutterFrameGeometry.TryCreate(assets,out var frame)) Frame = frame;
        if (assets.RetailMixResources is {} catalog &&
            catalog.TryReadResource("gutter_icon.w3d",out byte[] bytes,out _) &&
            EnbW3dModel.TryDecode(bytes,out var model))
            IconOpacity=model.Meshes.FirstOrDefault()?.VertexMaterials.FirstOrDefault()?.Info.Opacity;
    }
    public EnbUiRaster? Icon(string name,EnbRgb tint,int size)
    {
        if(IconOpacity is null)return null;
        var key=Key(name,tint,size);
        if(resources.TryGetValue(key,out var cached))return cached.Icon;
        if(!EnbGutterIconRasterFactory.TryCreate(assets,name,tint,size,out var raster))return null;
        resources.Add(key,new(raster,null));return raster;
    }
    public EnbTargetReticleGlyphSet? Reticle(EnbRgb tint)
    {
        var key=Key("hud01a.w3d",tint,0);
        if(resources.TryGetValue(key,out var cached))return cached.Reticle;
        if(!EnbTargetReticleRasterFactory.TryCreate(assets,tint,out var glyphs))return null;
        resources.Add(key,new(null,glyphs));return glyphs;
    }
    public EnbRetailFontFace? Font(EnbFontDefinition definition)
    {
        var key=(definition.Resource!,definition.Spacing,definition.Scale);
        if(fonts.TryGetValue(key,out var cached))return cached;
        if(!EnbRetailFontFactory.TryCreate(assets,definition,out var face))return null;
        fonts.Add(key,face);return face;
    }
    private static (string,byte,byte,byte,int) Key(string name,EnbRgb tint,int size)
        => (name,Byte(tint.R),Byte(tint.G),Byte(tint.B),size);
    private static byte Byte(float value)=>(byte)Math.Clamp((int)MathF.Round(value*255f),0,255);
}
