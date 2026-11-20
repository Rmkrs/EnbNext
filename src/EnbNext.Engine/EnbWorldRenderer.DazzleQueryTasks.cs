using System.Numerics;
using EnbNext.Assets;
using EnbNext.Rendering;
namespace EnbNext.Engine;
internal sealed partial class EnbWorldRenderer
{
    private sealed class DazzleOtherQuery
    {
        internal EnbRetailDazzleMeshRay[]? Rays;
        internal Matrix4x4 World;
        internal Vector3 Center,Extents;
        internal bool Oriented;
        private readonly EnbRetailDazzleBoxRay _box;
        internal readonly EnbDazzleRayCast Cast;
        internal DazzleOtherQuery(EnbRetailDazzleBoxRay box){_box=box;Cast=Execute;}
        private (float Fraction,bool StartInside) Execute(Vector3 start,Vector3 end,float fraction,bool inside)
        {
            if(Rays is {} rays)
            {
                foreach(var ray in rays)
                {
                    var hit=ray.CastOcclusionSegment(World,start,end,fraction,inside);
                    if(hit.Fraction<fraction)return hit;
                }
                return (fraction,inside);
            }
            return _box.CastSegment(Center,Extents,Oriented?World:null,start,end,fraction,inside);
        }
    }
    private readonly List<DazzleOtherQuery> _dazzleOtherQueries=[];
    private int _dazzleOtherQueryCount;
    private DazzleOtherQuery RentDazzleOtherQuery(Matrix4x4 world)
    {
        if(_dazzleOtherQueryCount==_dazzleOtherQueries.Count)_dazzleOtherQueries.Add(new(_dazzleBoxRay));
        var query=_dazzleOtherQueries[_dazzleOtherQueryCount++];query.World=world;return query;
    }
    private void AppendDazzleOtherLeaf(EnbW3dRenderResource resource,EnbCurrentRetailSpecialMaterialDefinition? specialOverride,
        string name,Matrix4x4 rigid,List<EnbDazzleRayCast> leaves)
    {
        EnbW3dExternalRenderObject? external=null;
        for(int i=0;i<resource.ExternalRenderObjects.Count;i++)
        {
            var candidate=resource.ExternalRenderObjects[i];
            if(string.Equals(candidate.Name,name,StringComparison.OrdinalIgnoreCase)){external=candidate;break;}
        }
        var special=specialOverride??external?.SpecialMaterialDefinition;
        if(special is not null)
        {
            if(!_dazzleSpecialRays.TryGetValue(special,out var rays))
            {
                // Geometry-only construction belongs to definition lifetime.
                // It does not submit material or retain FILE texture leases.
                using var owner=new EnbRetailSpecialMaterialInstance(special);
                var packets=EnbRetailSpecialMaterialDrawPacket.Build(owner,resource.Key,Matrix4x4.Identity,
                    EnbW3dLightEnvironment.BlackNoLights,0,0,0,0,out _);
                rays=new EnbRetailDazzleMeshRay[packets.Length];
                for(int i=0;i<rays.Length;i++)
                    rays[i]=new(packets[i].Mesh,1,special.CollisionMask,hidden:false,usePlaneBlocks:DazzlePlaneBlocks,
                        useTreePlaneBlocks:DazzleTreePlaneBlocks,retainTriangleOperands:true,
                        usePlaneOnlyRange:Environment.GetEnvironmentVariable("ENB_EXPERIMENT_PLANE_ONLY_RANGE")=="1");
                _dazzleSpecialRays.Add(special,rays);
            }
            var query=RentDazzleOtherQuery(rigid);query.Rays=rays;leaves.Add(query.Cast);return;
        }
        if(external?.BoxDefinition is {} box&&(box.Attributes&0x80)!=0)
        {
            var query=RentDazzleOtherQuery(rigid);query.Rays=null;query.Oriented=box.RetailClassId==0x1b;
            query.Center=query.Oriented?Vector3.Transform(box.Center,rigid):box.Center+rigid.Translation;
            query.Extents=box.Extents;leaves.Add(query.Cast);
        }
    }
}
