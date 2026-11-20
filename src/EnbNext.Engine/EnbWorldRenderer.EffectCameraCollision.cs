using System.Collections.Immutable;
using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    //6EF200/942140 join and6EF4A0/942160 leave the scene queried by75FB00.
    //Use current retained instances, not the visible packet list.98E7C0/98E860
    //visit the final level and aggregate without testing pivot visibility.
    private void AppendEffectCameraColliders(CameraQueryScene scene)
    {
        int effectIndex=0;
        for(int sourceIndex=0;sourceIndex<2;sourceIndex++)
        foreach(var effect in sourceIndex==0?_effectDazzleCollision:_warpTunnelCollision)
        {
            try
            {
                if(effectIndex==scene.Effects.Count)scene.Effects.Add(new());
                var owner=scene.Effects[effectIndex++];owner.Begin();
                var bounds=Append(effect.Asset,effect.Resource,effect.World,effect.Animations);
                owner.Finish();
                if(bounds is {} box && (owner.Meshes.Count!=0 || owner.Boxes.Count!=0))
                {
                    owner.Collider.LocalBounds=box;owner.Collider.World=effect.World;owner.Collider.ExcludeWithOwner=false;
                    scene.Colliders.Add(owner.Collider);
                }

                EnbW3dAxisAlignedBounds? Append(EnbCurrentRetailRObjEffectAsset asset,EnbW3dRenderResource? resource,
                    Matrix4x4 world,IReadOnlyList<EnbW3dResolvedWeightedAnimationSample> animations,bool namedChild=false)
                {
                    if(resource is null || asset.ObjectLine is not null)return null;
                    var hlod=resource.HierarchicalLods.SingleOrDefault();
                    if(hlod is null && resource.HierarchyModels.SingleOrDefault() is {} model)
                        hlod=EnbW3dHmodelConversion.Create(model,_=>true);
                    if(hlod is not null)
                    {
                        var hierarchy=resource.Hierarchies.Single(h=>h.Name.Equals(hlod.HierarchyName,StringComparison.OrdinalIgnoreCase));
                        var queryOwner=owner.Bounds();
                        var post=queryOwner.PreparePost(hierarchy.Name,world,effect.Scale,asset.ResolveHierarchyScale(effect.Scale,effect.HierarchySourceScale));
                        var queried=asset.RetainedBoundsResource??resource;
                        if(!queryOwner.Constructor.TryCompute(queried,asset.RetainedBoundsResource?.HierarchicalLods.Single()??hlod,
                            world,animations,post,out var bounds,out var why) ||
                           !EnbCurrentRetailShipLegoRuntime.TryEvaluateScaledRigidHierarchyPose(hierarchy,world,post.HierarchyTranslationScale,
                            animations,post.Transforms,out var pose,out why))throw new InvalidDataException(why);
                        if(hlod.Levels.Count!=0)foreach(var member in hlod.Levels[^1].Members)Member(member);
                        if(hlod.Aggregate is {} aggregate)foreach(var member in aggregate.Members)Member(member);
                        return bounds.LocalBox;

                        void Member(EnbW3dRenderHlodMember member)
                        {
                            if(member.ReferenceIndex>=pose.WorldTransforms.Length)return;
                            var rigid=pose.WorldTransforms[(int)member.ReferenceIndex];
                            for(int i=0;i<asset.ModelAttachments.Count;i++)
                            {
                                var child=asset.ModelAttachments[i];
                                int ordinal=child.BoundsOrdinal<0?i:child.BoundsOrdinal;
                                if(child.Ancestors.Count!=0 || member.Name!=(child.BoundsMemberName??$"<0600:{hlod.Name}:{ordinal}:{child.Name}>"))continue;
                                Append(child.Asset,child.Asset.Resource,rigid,[],namedChild:true);return;
                            }
                            Leaf(member.Name,rigid,false);
                        }
                    }
                    if(asset.SpecialMaterial is {} special)
                    {
                        Leaf("",world,false);
                        return new((special.Minimum+special.Maximum)*.5f,(special.Maximum-special.Minimum)*.5f);
                    }
                    if(resource.Meshes.Count==1)
                    {
                        var mesh=resource.Meshes[0];
                        //48B220's unchanged direct lookup preserves loader flags;
                        //48B0F0's named children and fallback objects postprocess.
                        Leaf(mesh.ContainerName.Length==0?mesh.Name:mesh.ContainerName+"."+mesh.Name,world,
                            !namedChild && !asset.IsMissingFactoryObject && !asset.IsPreconfiguredFactoryObject && effect.RemainingGeometryScale is null);
                        float pending=effect.RemainingGeometryScale??effect.Scale/mesh.CurrentRetailLoadedGeometryScale;
                        var min=(mesh.CurrentRetailLoadedGeometry?.Minimum??mesh.BoundingBoxMin)*pending;
                        var max=(mesh.CurrentRetailLoadedGeometry?.Maximum??mesh.BoundingBoxMax)*pending;
                        return new((min+max)*.5f,(max-min)*.5f);
                    }
                    if(asset.StandaloneBox is {} box)
                    {Leaf(resource.ExternalRenderObjects[0].Name,world,false);return new(box.Center,box.Extents);}
                    return null;

                    void Leaf(string name,Matrix4x4 rigid,bool loadedOnly)
                    {
                        var mesh=EnbW3dRenderObjectNameSemantics.FindFirstMesh(resource.Meshes,name);
                        if(mesh is not null)
                        {
                            uint mask=loadedOnly?EnbRetailDazzleCollision.LoadedMeshMask(mesh):EnbRetailDazzleCollision.ConfiguredMeshMask(mesh);
                            if((mask&16)==0)return;
                            float scale=effect.RemainingGeometryScale is {} pending?pending*mesh.CurrentRetailLoadedGeometryScale:effect.Scale;
                            owner.Mesh(mesh,scale,mask,rigid);return;
                        }
                        var external=resource.ExternalRenderObjects.FirstOrDefault(e=>e.Name.Equals(name,StringComparison.OrdinalIgnoreCase));
                        if(external?.BoxDefinition is {} box)
                        {
                            if((box.Attributes&128)==0)return;
                            bool oriented=box.RetailClassId==0x1b;
                            owner.Box(oriented?Vector3.Transform(box.Center,rigid):box.Center+rigid.Translation,box.Extents,
                                oriented?new(rigid.M11,rigid.M12,rigid.M13):Vector3.UnitX,
                                oriented?new(rigid.M21,rigid.M22,rigid.M23):Vector3.UnitY,
                                oriented?new(rigid.M31,rigid.M32,rigid.M33):Vector3.UnitZ);return;
                        }
                        var special=asset.SpecialMaterial??external?.SpecialMaterialDefinition;
                        if(special is null || (special.CollisionMask&16)==0)return;
                        var group=owner.Group();
                        foreach(var member in special.Members)
                            if(EnbRetailSpecialMaterialGeometry.TryCreate(member,out var geometry,out _))
                                owner.Mesh(new(geometry,special.CollisionMask),rigid,group);
                    }
                }
            }
            catch(InvalidDataException error)
            { _effectObjectVisualStatus+=$" · camera collision {effect.Resource?.LogicalFileName}: {error.Message}"; }
        }
        if(scene.Effects.Count>effectIndex)scene.Effects.RemoveRange(effectIndex,scene.Effects.Count-effectIndex);
    }
}
