using System.Numerics;

namespace EnbNext.Rendering;

// 0x742 prototype: 00913730 creates paired ring vertices/UVs/indices;
// 00913610 scales the two elliptical edges independently in XY.
// This is geometry preparation, not admission of the unclosed resource class.
public sealed record EnbRetailRingGeometry(Vector3[] Positions,Vector3[] Normals,Vector2[] TexCoords,uint[] Indices)
{
    public static EnbRetailRingGeometry Build(int segments,Vector2 firstRadii,Vector2 secondRadii,int repeats)
    {
        if(segments is <1 or >4096 || !float.IsFinite(firstRadii.X) || !float.IsFinite(firstRadii.Y) ||
            !float.IsFinite(secondRadii.X) || !float.IsFinite(secondRadii.Y))
            throw new InvalidDataException("Invalid ring geometry parameters");
        int count=2*(segments+1);
        var positions=new Vector3[count];var normals=new Vector3[count];var uv=new Vector2[count];
        var indices=new uint[segments*6];
        float angle=0,u=0,step=BitConverter.Int32BitsToSingle(0x40c90fdb)/segments,uvStep=repeats/(float)segments;
        for(int i=0;i<count;i+=2)
        {
            float x=-(float)Math.Sin(angle),y=(float)Math.Cos(angle);
            positions[i]=new(x*firstRadii.X,y*firstRadii.Y,0);
            positions[i+1]=new(x*secondRadii.X,y*secondRadii.Y,0);
            normals[i]=normals[i+1]=Vector3.UnitZ;
            uv[i]=new(u,0);uv[i+1]=new(u,1);
            angle+=step;u+=uvStep;
        }
        // Preserve native consecutive triples, including alternating winding.
        // Material/culling semantics must be resolved before production use.
        for(int i=0;i<segments*2;i++)
        {indices[i*3]=(uint)i;indices[i*3+1]=(uint)i+1;indices[i*3+2]=(uint)i+2;}
        return new(positions,normals,uv,indices);
    }
}
