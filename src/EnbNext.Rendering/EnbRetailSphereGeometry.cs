using System.Numerics;

namespace EnbNext.Rendering;

// Numeric geometry from 0090E8E0. No production sphere admission.
public sealed record EnbRetailSphereGeometry(Vector3[] Positions,Vector3[] Normals,Vector2[] TexCoords,uint[] Indices)
{
    public static EnbRetailSphereGeometry? ForDetail(int level)
    {
        if(level is <0 or >10)throw new ArgumentOutOfRangeException(nameof(level));
        return level==0?null:Build(level+6,level+6);
    }

    public static EnbRetailSphereGeometry Build(int longitudeSegments,int latitudeRows)
    {
        int n=longitudeSegments,m=latitudeRows;
        // Bounded to the native ushort index domain; supported retail n=m=7..16.
        if(n<3||m<1||((long)n+1)*m+2>ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(longitudeSegments));
        int count=(n+1)*m+2;
        var positions=new Vector3[count];var normals=new Vector3[count];var uv=new Vector2[count];
        var indices=new uint[6*n*m];
        positions[0]=Vector3.UnitZ;uv[0]=new(.5f,0);
        positions[^1]=new(-0f,-0f,-1);uv[^1]=new(.5f,1);
        float pi=BitConverter.Int32BitsToSingle(0x40490fdb),tau=BitConverter.Int32BitsToSingle(0x40c90fdb);
        for(int row=0;row<m;row++)
        {
            double v=(row+1d)/(m+1d),latitude=v*pi;
            float sinLatitude=(float)Math.Sin(latitude),cosLatitude=(float)Math.Cos(latitude);
            for(int column=0;column<=n;column++)
            {
                double u=column/(double)n,longitude=u*tau;
                int at=1+row*(n+1)+column;
                positions[at]=new((float)Math.Sin(longitude)*sinLatitude,
                    -(float)Math.Cos((float)longitude)*sinLatitude,cosLatitude);
                uv[at]=new((float)u,(float)v);
            }
        }
        for(int i=0;i<count;i++)normals[i]=Vector3.Normalize(positions[i]);
        int index=0;
        void Triangle(int a,int b,int c){indices[index++]=(uint)a;indices[index++]=(uint)b;indices[index++]=(uint)c;}
        // Native strips list lower/upper pairs, then expand alternating triples.
        for(int row=0;row<m-1;row++)
            for(int column=0;column<n;column++)
            {
                int upper=1+row*(n+1)+column,lower=upper+n+1;
                Triangle(lower,upper,lower+1);Triangle(upper,upper+1,lower+1);
            }
        // Native cap fans are appended after every body strip.
        for(int column=0;column<n;column++)Triangle(0,column+2,column+1);
        for(int column=0;column<n;column++)Triangle(count-1,count-3-column,count-2-column);
        return new(positions,normals,uv,indices);
    }
}
