using System.Numerics;
using EnbNext.Rendering;

namespace EnbNext.Engine;

internal sealed partial class EnbWorldRenderer
{
    private EnbW3dRenderResource? _stationShipPickResource;
    private EnbW3dDrawTask[] _stationShipPickSource=[];
    private EnbRetailMeshPick[] _stationShipPickProxy=[];
    private string? _stationShipPickDiagnostic;
    private bool _publishStationShipGeometry;
    private EnbRetailMeshPick[] _stationShipGeometryPicks=[];

    private void PublishStationShipGeometry(RootTaskList roots,EnbResolvedRenderCamera camera)
    {
        if(!_publishStationShipGeometry)return;
        var picks=new List<EnbRetailMeshPick>();
        foreach(var root in roots.Where(r=>r.Parent is null && r.Object.IsLocalShip && !r.Root.RenderObjectHidden))
            Append(root,[]);
        Volatile.Write(ref _stationShipGeometryPicks,picks.ToArray());

        void Append(LiveRootInstance instance,EnbW3dWorldBounds[] inherited)
        {
            if(!TryResolvePrimitiveFactoryTransform(instance,out var world,out float scale,out _))return;
            using var queryChildren=RetainedCollisionChildren(instance,roots);
            var children=queryChildren.Children;
            var resource=RetainedDrawResource(instance);
            if(RetainedDrawRoot(instance).Hlod is {} hlod)
            {
                if(!TryResolveConfiguredAttachmentHierarchy(instance.Root,out var hierarchy,out _) || hierarchy is null)return;
                var animations=instance.AnimationBlend?.ForHierarchy(hierarchy.Name)??Array.Empty<EnbW3dResolvedWeightedAnimationSample>();
                if(!TryResolveRetainedHierarchyPose(instance,hierarchy,world,ResolvePendingHierarchyScale(instance,scale),animations,
                    instance.HierarchyPostTransforms?.Transforms,out var pose,out _))return;
                var sphere=instance.CurrentRetailWorldSphere;
                if(sphere is null)
                {
                    // An undrawn retained occurrence still owns its +F0 sphere.
                    // Reuse the same transform and bounds consumers as the draw
                    // occurrence, without submitting it or changing its visibility.
                    if(!TryRefreshOrdinaryHlodChildCore(instance,false,out _))return;
                    if(CompositeMembership(instance) is {} membership)
                        instance.RetainedQueryRoot=membership.QueryRoot;
                    else if(instance.Root.RslBoneNode is not null)
                    {
                        try
                        {
                            var additions=RslBoneCompositeMembers(instance.Root);
                            if(additions.Count!=0)
                            {
                                var query=new RetainedCompositeMembership(instance.Root);
                                foreach(var pair in additions)query.Attachments.Add(pair.Key,pair.Value);
                                query.Rebuild();instance.RetainedQueryRoot=query.QueryRoot;
                            }
                        }
                        catch(InvalidDataException error)
                        {
                            _retainedEmitterErrors.Add($"Station retained child bounds: {error.Message}");
                            return;
                        }
                    }
                    if(TryResolveHlodCompositeBounds(instance,hlod,instance.WorldTransformOverride??world,animations,out var bounds,out _))
                        sphere=bounds.WorldSphere;
                }
                var castGates=sphere is {} value?inherited.Append(value).ToArray():inherited;
                //9671B0 enumerates every child. Only the last LOD and aggregate
                //are also reached by the parent's98E680 D0 without leaf gates.
                foreach(var level in hlod.Levels)
                    foreach(var member in level.Members)Member(member,level.Ordinal,false,ReferenceEquals(level,hlod.Levels[^1])?castGates:[]);
                if(hlod.Aggregate is {} aggregate)
                    foreach(var member in aggregate.Members)Member(member,null,true,castGates);
                foreach(var child in children.Where(c=>!c.IsOrdinaryHlodChild))Append(child,castGates);
                return;

                void Member(EnbW3dRenderHlodMember member,int? level,bool aggregate,EnbW3dWorldBounds[] gates)
                {
                    var child=children.FirstOrDefault(c=>c.IsAggregateHlodChild==aggregate &&
                        (c.OrdinaryHlodLevelOrdinal==level || hlod.IsConvertedHierarchyModel) && c.OrdinaryHlodPivot==member.ReferenceIndex &&
                        c.Root.RootRenderObjectName.Equals(member.Name,StringComparison.OrdinalIgnoreCase));
                    if(child is not null){children.Remove(child);Append(child,gates);return;}
                    if(member.ReferenceIndex>=pose.WorldTransforms.Length || !pose.Visibility[(int)member.ReferenceIndex])return;
                    Leaf(member.Name,pose.WorldTransforms[(int)member.ReferenceIndex],gates);
                }
            }
            else
            {
                if(instance.PivotVisible)Leaf(instance.Root.RootRenderObjectName,world,inherited);
                foreach(var child in children)Append(child,inherited);
            }
            void Leaf(string name,Matrix4x4 rigid,EnbW3dWorldBounds[] gates)
            {
                var mesh=EnbW3dRenderObjectNameSemantics.FindFirstMesh(resource.Meshes,name);
                if(mesh is not null)picks.Add(new(StationPickMesh(mesh,scale),rigid,camera){AdditionalAncestorSpheres=gates});
            }
        }
    }

    private void PublishStationShipPickProxy(EnbCurrentRetailStationInterior room,int width,int height)
    {
        var resource=room.HangarAttachments is null?null:room.SupplementalResource;
        if(!ReferenceEquals(resource,_stationShipPickResource))
        {
            _stationShipPickResource=resource;_stationShipPickSource=[];_stationShipPickDiagnostic=null;
            if(resource is not null)
            {
                try
                {
                    //634140 selects the supplemental SHIP_EXTENTS00 child.
                    //Mesh+160 ->93EEB0/92D460 scales its local vertices by.6;
                    //its authored child transform remains unchanged.
                    var packets=EnbCurrentRetailStationInterior.BuildModelDraws(resource,Matrix4x4.Identity,0,retainRenderHidden:true).Packets
                        .Where(p=>p.Mesh.Name.Equals("SHIP_EXTENTS00",StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(packets.Length==0)throw new InvalidDataException("SHIP_EXTENTS00 is absent");
                    foreach(var task in packets)task.RetainAfterDrain();
                    _stationShipPickSource=packets;
                }
                catch(Exception ex) when(ex is InvalidDataException or IOException or InvalidOperationException or ArgumentException)
                {_stationShipPickDiagnostic=ex.Message;}
            }
        }
        var camera=StationCamera(room,width,height);
        Volatile.Write(ref _stationShipPickProxy,_stationShipPickSource.Select(p=>
            new EnbRetailMeshPick(StationPickMesh(p.Mesh,.6f),p.WorldTransform,camera)).ToArray());
    }
}
