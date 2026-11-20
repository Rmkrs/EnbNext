using System.Numerics;
using System.Runtime.CompilerServices;

namespace EnbNext.Rendering;

/// <summary>Retained room members queried by98E7C0, independently of draw visibility.</summary>
public sealed class EnbRetailStationCameraGeometry
{
    private readonly List<EnbCameraCollisionMesh> _frameMeshes=[];
    private readonly List<EnbCameraCollisionBox> _frameBoxes=[];
    public void Clear(){_frameMeshes.Clear();_frameBoxes.Clear();}
    public EnbCameraCollider Frame=>new(0,default,Matrix4x4.Identity,_frameMeshes){Boxes=_frameBoxes};

    public void Append(EnbW3dRenderResource resource,Matrix4x4 world,EnbW3dAnimationSample? sample=null,bool scaleShipProxy=false)
    {
        var hlod=resource.HierarchicalLods.SingleOrDefault();
        if(hlod is null && resource.HierarchyModels.SingleOrDefault() is {} model)
            hlod=EnbW3dHmodelConversion.Create(model,_=>true);
        if(hlod is null)
        {foreach(var mesh in resource.Meshes)Leaf(mesh.ContainerName+"."+mesh.Name,world);return;}
        var hierarchy=resource.Hierarchies.Single(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
        var animation=sample is {} s?resource.Animations.Single(a=>a.Name.Equals(s.Name,StringComparison.OrdinalIgnoreCase)):null;
        if(!EnbW3dHierarchyPose.TryEvaluate(hierarchy,world,animation,sample?.Frame??0,out var pose,out var error))
            throw new InvalidDataException(error);
        if(hlod.Levels.Count>0)foreach(var member in hlod.Levels[^1].Members)Member(member);
        if(hlod.Aggregate is {} aggregate)foreach(var member in aggregate.Members)Member(member);
        void Member(EnbW3dRenderHlodMember member)=>Leaf(member.Name,pose.WorldTransforms[(int)member.ReferenceIndex]);
        void Leaf(string name,Matrix4x4 rigid)
        {
            int order=_frameMeshes.Count+_frameBoxes.Count;
            var mesh=resource.Meshes.FirstOrDefault(m=>EnbW3dRenderObjectNameSemantics.MatchesMesh(m,name));
            if(mesh is not null)
            {
                var geometry=scaleShipProxy && mesh.Name.Equals("SHIP_EXTENTS00",StringComparison.OrdinalIgnoreCase)
                    ?OwnerCache.Get(ref mesh.DerivedStationShipProxyGeometry,mesh,static m=>new EnbCameraCollisionGeometry(m,.6f*m.CurrentRetailLoadedGeometryScale))
                    :OwnerCache.Get(ref mesh.DerivedStationCameraGeometry,mesh,static m=>new EnbCameraCollisionGeometry(m,m.CurrentRetailLoadedGeometryScale));
                if((geometry.Mask&2)!=0)_frameMeshes.Add(new(geometry,rigid){TraversalOrder=order});
                return;
            }
            var external=resource.ExternalRenderObjects.FirstOrDefault(e=>e.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
            if(external?.BoxDefinition is {} box)
            {
                //998640: authored bits4..10 become collision bits1..7.
                if((box.Attributes&16)==0)return;
                bool oriented=box.RetailClassId==0x1b;
                _frameBoxes.Add(new(oriented?Vector3.Transform(box.Center,rigid):box.Center+rigid.Translation,box.Extents,
                    oriented?new(rigid.M11,rigid.M12,rigid.M13):Vector3.UnitX,
                    oriented?new(rigid.M21,rigid.M22,rigid.M23):Vector3.UnitY,
                    oriented?new(rigid.M31,rigid.M32,rigid.M33):Vector3.UnitZ){TraversalOrder=order});return;
            }
            if(external?.SpecialMaterialDefinition is not {} special || (special.CollisionMask&2)==0)return;
            var group=new object();
            foreach(var member in special.Members)
                if(EnbRetailSpecialMaterialGeometry.TryCreate(member,out var geometry,out _))
                    _frameMeshes.Add(new(new(geometry,special.CollisionMask),rigid){FirstHitGroup=group,TraversalOrder=_frameMeshes.Count+_frameBoxes.Count});
        }
    }
}
