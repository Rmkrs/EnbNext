using System.Numerics;
using EnbNext.Rendering;
using R=EnbNext.Rendering.EnbRetailExtendedX87Register;
namespace EnbNext.Net7.World;

//713520/713670: mutable last sampled position, signed elapsed time, and
//577940/69F3B0 matrix rotations. Preview flag1 does not publish the cache.
internal sealed class Net7PlanetSpatialModel(uint stateTime,Vector3 cachedPosition,uint orbitId,
    float orbitDistance,float orbitAngle,float orbitRate,float rotationAngle,float rotationRate,float tiltAngle,
    EnbRetailLinePrecision precision=EnbRetailLinePrecision.Single24):INet7SpatialModel
{
    private Vector3 _cachedPosition=cachedPosition;
    internal Vector3 CachedPosition=>_cachedPosition;
    public bool TrySample(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Net7SpatialSample sample)
        =>TrySampleAtTime(world,time,context,false,out sample);
    public bool TrySampleRotation(Net7WorldState world,uint time,Net7SpatialSamplingContext context,out Vector3 rotation)
    {rotation=Vector3.Zero;return true;}
    internal bool TrySampleAtTime(Net7WorldState world,uint time,Net7SpatialSamplingContext context,bool preview,out Net7SpatialSample sample)
    {
        using var scope=new EnbRetailLineArithmetic.Scope(precision,true);
        float elapsed=(float)(R)(double)unchecked((int)(time-stateTime));
        float[] m=[1,0,0,0,0,1,0,0,0,0,1,0];
        Vector3 anchor=Vector3.Zero,position=_cachedPosition;
        if(orbitId!=0&&!context.Contains(orbitId)&&world.Objects.FindFirst(orbitId)?.Spatial?.Model is not null
            &&world.TrySampleObjectCore(orbitId,time,context,out var target))
        {
            anchor=target.Position;
            float angle=(float)((R)elapsed*orbitRate+orbitAngle);
            //713783..7137DF: translation Z remains on the stack until distance is added.
            R tx=(R)m[2]*anchor.Z+(R)m[1]*anchor.Y+(R)m[0]*anchor.X+m[3];
            R ty=(R)m[6]*anchor.Z+(R)m[5]*anchor.Y+(R)m[4]*anchor.X+m[7];
            R tz=(R)anchor.Z*m[10]+(R)m[9]*anchor.Y+(R)m[8]*anchor.X+m[11];
            m[3]=(float)tx;m[7]=(float)ty;
            Rotate(m,angle,0,1);
            m[3]=(float)((R)orbitDistance*m[0]+m[3]);
            m[7]=(float)((R)m[4]*orbitDistance+m[7]);
            m[11]=(float)((R)m[8]*orbitDistance+tz);
            Rotate(m,-angle,0,1);
            position=new(m[3],m[7],m[11]);
        }
        if(!preview)_cachedPosition=position;
        Rotate(m,tiltAngle,1,2);
        Rotate(m,(float)((R)elapsed*rotationRate+rotationAngle),0,1);
        Matrix4x4 basis=new(m[0],m[4],m[8],0,m[1],m[5],m[9],0,m[2],m[6],m[10],0,m[3],m[7],m[11],1);
        float dx=(float)((R)anchor.X-position.X);
        R dy=(R)anchor.Y-position.Y; //713979 retains Y difference through multiplication.
        if(orbitRate<=0)dx=-dx;else dy=-dy;
        var velocity=new Vector3((float)((R)dy*orbitRate),(float)((R)orbitRate*dx),0);
        sample=new(time,position,EnbRetailQuaternionConversions.ExtractOrientation(basis,precision),velocity,
            (float)((R)orbitRate*orbitDistance),Vector3.Zero){OrientationColumn0=new(m[0],m[4],m[8])};
        return true;
    }
    private static void Rotate(float[] m,float angle,int first,int second)
    {
        R sine=R.Sine((R)angle),cosine=R.Cosine((R)angle);
        for(int row=0;row<12;row+=4)
        {
            float a=m[row+first],b=m[row+second];
            m[row+first]=(float)((R)b*sine+(R)a*cosine);
            m[row+second]=(float)((R)b*cosine-(R)a*sine);
        }
    }
}
