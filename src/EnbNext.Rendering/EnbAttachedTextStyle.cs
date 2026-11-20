using System.Globalization;
using System.Numerics;
using EnbNext.Presentation;
using EnbNext.Assets;

namespace EnbNext.Rendering;

public sealed record EnbAttachedTextStyle(EnbUiPoint Anchor, EnbUiRect Clip, float HalfWidth,
    float ScrollSpeed, string Font, bool Center)
{
    public Vector4 CenteredClipBounds { get; init; }
    public bool Underline { get; init; }
    public bool Right { get; init; }
    public float BlinkSpeed { get; init; }
    public float RevertSpeed { get; init; }
    public float OpeningWipeRightSpeed { get; init; }
    public static EnbAttachedTextStyle Load(EnbRetailAssetBundle assets, EnbAuthoredUiAsset asset,
        EnbInterfaceTextDefinition label, Matrix4x4 parent, IReadOnlyList<Matrix4x4>? pose = null)
    {
        string attachment=label.Properties["Attach"];
        var pivot=asset.Hierarchy.Pivots.FirstOrDefault(p=>p.Name.Equals(attachment,StringComparison.OrdinalIgnoreCase));
        // 6D6390 -> parent+80/941F60 ->9C58C0/98A000: an absent named
        // bone binds at index zero. 6D5D40 separately keeps the full-screen
        // clip when no named render object exists (installed MFD_CON/info00).
        var transform=(pose ?? EnbAuthoredUiAsset.ResolveTransforms(asset))[pivot?.Ordinal ?? 0]*parent;
        var origin=transform.Translation;
        // 006D5170 scales authored Offset by the 640x480 reference extent;
        // 006D64C0 adds it to the attachment translation, not to its clip.
        var offset=label.Properties.GetValueOrDefault("Offset","0,0").Split(',')
            .Select(v=>float.Parse(v,CultureInfo.InvariantCulture)).ToArray();
        origin.X+=offset[0]*640;origin.Y+=offset[1]*480;
        var mesh=asset.Resource.Meshes.FirstOrDefault(m=>m.Name.Equals(attachment,StringComparison.OrdinalIgnoreCase));
        // 006D5170 initializes a full reference-screen clip. 006D5D40
        // replaces it only when the attachment resolves to a render object.
        var min=mesh is null?new Vector3(-320,-240,0):new Vector3(float.PositiveInfinity);
        var max=mesh is null?new Vector3(320,240,0):new Vector3(float.NegativeInfinity);
        for(int i=0;mesh is not null && i<8;i++)
        {
            var corner=new Vector3((i&1)==0?mesh.BoundingBoxMin.X:mesh.BoundingBoxMax.X,
                (i&2)==0?mesh.BoundingBoxMin.Y:mesh.BoundingBoxMax.Y,
                (i&4)==0?mesh.BoundingBoxMin.Z:mesh.BoundingBoxMax.Z);
            corner=Vector3.Transform(corner,transform);
            min=Vector3.Min(min,corner);max=Vector3.Max(max,corner);
        }
        // 006D5D40: truncate in centered reference coordinates, then clamp.
        var clip=new EnbUiRect(320+Math.Clamp((int)min.X,-320,320),240-Math.Clamp((int)max.Y,-240,240),
            Math.Clamp((int)max.X,-320,320)-Math.Clamp((int)min.X,-320,320),
            Math.Clamp((int)max.Y,-240,240)-Math.Clamp((int)min.Y,-240,240));
        float speed=0,blink=0,revert=0,wipe=0;
        var effects=EnbNext.Rendering.EnbRenderSession.Of(assets).FontEffects;
        foreach(string effect in label.Properties.GetValueOrDefault("EffectConst","").Split(','))
            foreach(var definition in effects.EnumerateSections().Select(s=>s.Value))
                if(definition.GetValueOrDefault("EffectID")==effect.Trim()&&definition.GetValueOrDefault("EffectType")=="SCROLL"&&definition.GetValueOrDefault("Direction")=="LEFT")
                    speed=float.Parse(definition.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
        foreach(var definition in effects.EnumerateSections().Select(s=>s.Value))
        {
            string? id=definition.GetValueOrDefault("EffectID");
            if(id is null)continue;
            if(label.Properties.GetValueOrDefault("EffectOn","").Split(',').Any(v=>v.Trim()==id)
                && definition.GetValueOrDefault("EffectType")=="WIPE" && definition.GetValueOrDefault("Direction")=="RIGHT"
                && float.Parse(definition.GetValueOrDefault("Intensity","0"),CultureInfo.InvariantCulture)>0)
                wipe=float.Parse(definition.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
            if(label.Properties.GetValueOrDefault("MouseOver","").Split(',').Any(v=>v.Trim()==id) && definition.GetValueOrDefault("EffectType")=="BLINK")
                blink=float.Parse(definition.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
            if(label.Properties.GetValueOrDefault("EffectConst","").Split(',').Any(v=>v.Trim()==id) && definition.GetValueOrDefault("EffectType") is "REVERT" or "SCROLL_REVERT")
                revert=float.Parse(definition.GetValueOrDefault("Speed","0"),CultureInfo.InvariantCulture);
        }
        return new(new(origin.X+320,240-origin.Y),clip,(max.X-min.X)*.5f,speed,
            label.Properties["FontType"],label.Properties.GetValueOrDefault("Justify","").EndsWith(",CENTER",StringComparison.OrdinalIgnoreCase))
            {CenteredClipBounds=new(min.X,min.Y,max.X,max.Y),Underline=label.Properties.GetValueOrDefault("Underline")?.Equals("True",StringComparison.OrdinalIgnoreCase)==true,
             Right=label.Properties.GetValueOrDefault("Justify","").EndsWith(",RIGHT",StringComparison.OrdinalIgnoreCase),BlinkSpeed=blink,RevertSpeed=revert,OpeningWipeRightSpeed=wipe};
    }
}
