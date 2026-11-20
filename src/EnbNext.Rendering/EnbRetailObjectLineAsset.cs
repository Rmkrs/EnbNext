using System.Numerics;
using System.Globalization;
using EnbNext.Assets;

namespace EnbNext.Rendering;

// 004983A0 retains authored Point0..Point31 in the procedural RenderObj.
// Contiguous points stay in one strip, including authored closed/repeated paths.
public sealed record EnbRetailObjectLineAsset(int BaseAssetId, EnbRetailTractorLineAsset Line, Vector3 Start, Vector3 End)
{
    public IReadOnlyList<Vector3> Points {get;init;}=[Start,End];
    public static EnbRetailObjectLineAsset Load(EnbRetailAssetBundle assets,int id,Vector3 hsv=default)
    {
        var definition=assets.PresentationCatalog!.ResolveBaseAsset(id);
        var properties=definition.Properties ?? throw new InvalidDataException("Object line properties unavailable");
        Vector3 Point(string name)
        {
            if(!properties.TryGetValue(name,out var raw))throw new InvalidDataException("Object line missing "+name);
            var parts=raw.Split(',');
            var values=new float[3];
            if(parts.Length!=3)throw new InvalidDataException("Invalid object line "+name);
            for(int i=0;i<3;i++)
                if(!float.TryParse(parts[i],NumberStyles.Float,CultureInfo.InvariantCulture,out values[i]) || !float.IsFinite(values[i]))
                    throw new InvalidDataException("Invalid object line "+name);
            return new(values[0],values[1],values[2]);
        }
        // 0049887A..004988CB: no authored Point0 supplies two zero points.
        // Noise subdivides this coincident centerline into the authored spark
        // shape (e.g. Plasma Jagged Projectile B442); do not invent its length.
        if(!properties.ContainsKey("Point0"))
            return new(id,EnbRetailTractorLineAsset.Load(assets,id,hsv),Vector3.Zero,Vector3.Zero);
        var points=new List<Vector3>{Point("Point0"),Point("Point1")};
        for(int i=2;i<32 && properties.ContainsKey($"Point{i}");i++)points.Add(Point($"Point{i}"));
        return new(id,EnbRetailTractorLineAsset.Load(assets,id,hsv),points[0],points[^1]){Points=points.AsReadOnly()};
    }

    public EnbW3dDrawTask? Build(ulong owner,ulong generation,uint gameId,Matrix4x4 rigidWorld,
        float scale,EnbResolvedRenderCamera camera,uint age,int? detailLevel=null)
        => Line.Build(owner,generation,gameId,Points.Select(p=>p*scale).ToArray(),camera,age,scale,detailLevel,
            geometryModelToWorld:rigidWorld);

    public EnbW3dWorldBounds Bounds(Matrix4x4 rigidWorld,float scale)
    {
        // SegLine builds its local sphere before the inherited RenderObj
        // transforms its center. Reboxing rotated points changes its radius.
        var local=EnbSegLineDetailBudgetSemantics.Bounds(Points.Select(p=>p*scale).ToArray(),Line.Width*scale,Line.Noise*scale,Line.Subdivisions);
        return new(Vector3.Transform(local.Center,rigidWorld),local.Radius);
    }
}
